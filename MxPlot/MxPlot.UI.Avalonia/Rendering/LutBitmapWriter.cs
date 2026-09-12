using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MxPlot.Core;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// High-performance LUT-based pseudo-color renderer for <see cref="IMatrixData"/>.
    /// Maps scalar values to colors via a <see cref="LookupTable"/> and writes the result
    /// into an Avalonia <see cref="WriteableBitmap"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For small-range integer types (byte, ushort, short), a pre-computed direct-index
    /// color map eliminates all floating-point arithmetic in the inner pixel loop.
    /// For wider types (int, float, double, Complex), each value is scaled to a LUT index
    /// at render time.
    /// </para>
    /// <para>
    /// The color map cache is rebuilt only when the rendering context parameters
    /// (LUT, value range, inversion) change between calls, as determined by record equality.
    /// </para>
    /// <para>
    /// Color format: <see cref="LookupTable"/> stores colors as ARGB integers
    /// (<c>0xAARRGGBB</c>), which in little-endian memory layout is [B][G][R][A] —
    /// identical to Avalonia's <see cref="PixelFormat.Bgra8888"/>. No conversion is required.
    /// </para>
    /// </remarks>
    public sealed class LutBitmapWriter : IBitmapWriter
    {
        #region Fields

        private unsafe delegate void RenderLoopDelegate(
            IMatrixData source,
            int frameIndex,
            int* targetPtr,
            int strideInts,
            int width,
            int height,
            ReadOnlyMemory<int> lutMemory,
            double valueScale,
            double valueOffset,
            int lutMaxIndex);

        private readonly RenderLoopDelegate _renderLoop;
        private readonly bool _supportsCachedColorMap;

        // Cache invalidation state
        private LookupTable? _cachedLut;
        private double _cachedMin = double.NaN;
        private double _cachedMax = double.NaN;
        private bool _cachedInverted;
        private int[]? _cachedColorMap;

        // Frame-sized scratch buffer; see EnsureScratch.
        private int[]? _scratch;

        #endregion

        #region IBitmapWriter Properties

        /// <inheritdoc />
        public Type ValueType { get; }

        /// <inheritdoc />
        public bool FlipY { get; set; } = true;

        /// <inheritdoc />
        public ParallelOptions? ParallelOptions { get; set; }

        /// <summary>
        /// Parallel options actually used by the render loops: whatever the caller configured, or
        /// a default one once the frame is large enough to be worth splitting.
        /// </summary>
        /// <remarks>
        /// Nothing in the library ever assigned <see cref="ParallelOptions"/>, so every loop below
        /// took its serial fallback: a 4096x4096 byte frame measured 6.68 ms against roughly 2 ms
        /// for the same loop across all cores. <c>CompositeBitmapWriter</c> already defaulted its
        /// own options (<c>ParallelOptions ?? new ParallelOptions()</c>), so composite rendering
        /// was parallel while LUT rendering was not - the asymmetry was unintended.
        /// Small frames stay serial. The crossover was measured on this loop (i9-14900KF, 32T):
        /// 320x320 (102k px) serial 0.035 ms vs parallel 0.045 ms; 384x384 (147k px) serial
        /// 0.054 ms vs parallel 0.021 ms. So it sits between 100k and 150k pixels, and 2^17 lands
        /// on it. Getting this wrong in either direction is cheap - below the crossover the whole
        /// loop is a few hundredths of a millisecond either way - but placing it too high gives up
        /// a real win: 512x512 is already 4.5x faster in parallel.
        /// </remarks>
        public ParallelRenderPolicy ParallelPolicy { get; set; } = ParallelRenderPolicy.Auto;

        private ParallelOptions? EffectiveParallelOptions => ParallelPolicy switch
        {
            // The policy answers "should this go parallel", the options answer "with how many".
            // Keeping them separate is what lets a caller cap the degree of parallelism without
            // also forcing a frame too small to benefit through a parallel loop - and it is why
            // Never wins over supplied options: setting both says "these options, if you ever go
            // parallel", and Never says not to.
            ParallelRenderPolicy.Never => null,
            ParallelRenderPolicy.Always => ParallelOptions ?? SharedParallelOptions,
            _ => _autoWantsParallel ? ParallelOptions ?? SharedParallelOptions : null,
        };

        /// <remarks>
        /// Evaluated per render rather than cached when the data is assigned: it is one multiply
        /// and a compare against dimensions <see cref="RenderCore"/> has already read, so there is
        /// nothing to gain by caching, and no cached value that can go stale when the frame size
        /// changes without the writer being recreated (the writer is only rebuilt on a value-type
        /// or rendering-mode change, not on every new MatrixData).
        /// </remarks>
        private const int ParallelPixelThreshold = 1 << 17;
        private static readonly ParallelOptions SharedParallelOptions = new();
        private bool _autoWantsParallel;

        /// <inheritdoc />
        public object? StructValueConverter { get; set; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new <see cref="LutBitmapWriter"/> for the specified element type.
        /// </summary>
        /// <param name="valueType">
        /// The element type of the matrix data to render
        /// (e.g. <c>typeof(byte)</c>, <c>typeof(ushort)</c>, <c>typeof(float)</c>).
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="valueType"/> is <c>null</c>.</exception>
        public LutBitmapWriter(Type valueType)
        {
            ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));

            _supportsCachedColorMap = valueType == typeof(byte)
                                   || valueType == typeof(ushort)
                                   || valueType == typeof(short);

            unsafe
            {
                _renderLoop = valueType switch
                {
                    Type t when t == typeof(byte) => RenderByte,
                    Type t when t == typeof(ushort) => RenderUShort,
                    Type t when t == typeof(short) => RenderShort,
                    Type t when t == typeof(int) => RenderInt,
                    Type t when t == typeof(float) => RenderFloat,
                    Type t when t == typeof(double) => RenderDouble,
                    Type t when t == typeof(Complex) => RenderComplex,
                    _ => RenderFallback
                };
            }

            if (valueType == typeof(Complex))
                StructValueConverter = (Func<Complex, double>)(c => c.Magnitude);
        }

        #endregion

        #region IBitmapWriter.Render

        /// <inheritdoc />
        public void Render(IMatrixData source, WriteableBitmap target, IRenderingContext context)
        {
            if (context is not LutRenderingContext ctx)
                throw new ArgumentException(
                    $"Expected {nameof(LutRenderingContext)}, got {context.GetType().Name}.",
                    nameof(context));

            Validate(source, target, ctx);
            EnsureColorMapCache(ctx);
            RenderCore(source, target, ctx);
        }

        #endregion

        #region Validation

        private void Validate(IMatrixData source, WriteableBitmap target, LutRenderingContext ctx)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));

            if (target.PixelSize.Width != source.XCount || target.PixelSize.Height != source.YCount)
                throw new ArgumentException(
                    $"Target bitmap size ({target.PixelSize.Width}×{target.PixelSize.Height}) " +
                    $"does not match source data size ({source.XCount}×{source.YCount}).");

            if (ctx.FrameIndex < 0 || ctx.FrameIndex >= source.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(ctx.FrameIndex),
                    $"Frame index must be in [0, {source.FrameCount - 1}]. Got: {ctx.FrameIndex}");

            if (double.IsInfinity(ctx.ValueMin) || double.IsInfinity(ctx.ValueMax))
                throw new InvalidOperationException(
                    $"Invalid value range: Min={ctx.ValueMin}, Max={ctx.ValueMax}");
        }

        #endregion

        #region Color Map Cache

        private void EnsureColorMapCache(LutRenderingContext ctx)
        {
            if (!_supportsCachedColorMap) return;

            if (_cachedColorMap != null
                && ReferenceEquals(_cachedLut, ctx.LookupTable)
                && _cachedMin == ctx.ValueMin
                && _cachedMax == ctx.ValueMax
                && _cachedInverted == ctx.IsInvertedColor)
            {
                return;
            }

            RebuildColorMapCache(ctx);
        }

        /// <summary>
        /// Builds the value -> LUT level transform. Levels are treated as equal-width bins:
        /// level i covers [i/L, (i+1)/L) of the value range, so every color gets the same share.
        /// Anchoring the scale on (Levels - 1) instead would hand the topmost level only the
        /// single exact-max value - invisible at 256 levels, but glaring for a hand-written
        /// .mlut with a handful of entries (a 3-color LUT rendered as two wide bands plus a
        /// one-pixel sliver).
        /// </summary>
        internal static (double Scale, double Offset) BuildIndexMapping(int levels, double viewMin, double viewMax)
        {
            double range = viewMax - viewMin;
            if (range == 0) range = 1.0;

            double scale = levels / range;
            return (scale, -viewMin * scale);
        }

        /// <summary>
        /// Applies the transform from <see cref="BuildIndexMapping"/> to a single value.
        /// The unsafe render loops inline this same expression for speed.
        /// </summary>
        internal static int MapValueToLevel(double value, double scale, double offset, int maxLevelIndex)
            => Math.Clamp((int)(value * scale + offset), 0, maxLevelIndex);

        private void RebuildColorMapCache(LutRenderingContext ctx)
        {
            int size, offset;
            if (ValueType == typeof(byte)) { size = 256; offset = 0; }
            else if (ValueType == typeof(ushort)) { size = 65536; offset = 0; }
            else if (ValueType == typeof(short)) { size = 65536; offset = 32768; }
            else return;

            var (viewMin, viewMax) = ctx.GetEffectiveRange();
            var (scale, valueOffset) = BuildIndexMapping(ctx.LookupTable.Levels, viewMin, viewMax);
            int lutMaxIndex = ctx.LookupTable.Levels - 1;
            var lut = ctx.LookupTable.AsSpan();

            var map = new int[size];
            for (int v = 0; v < size; v++)
                map[v] = lut[MapValueToLevel(v - offset, scale, valueOffset, lutMaxIndex)];

            _cachedColorMap = map;
            _cachedLut = ctx.LookupTable;
            _cachedMin = ctx.ValueMin;
            _cachedMax = ctx.ValueMax;
            _cachedInverted = ctx.IsInvertedColor;
        }

        #endregion

        #region Core Rendering

        private unsafe void RenderCore(IMatrixData source, WriteableBitmap target, LutRenderingContext ctx)
        {
            var (viewMin, viewMax) = ctx.GetEffectiveRange();
            var (valueScale, valueOffset) = BuildIndexMapping(ctx.LookupTable.Levels, viewMin, viewMax);
            int lutMaxIndex = ctx.LookupTable.Levels - 1;

            int width = source.XCount;
            int height = source.YCount;
            var scratch = EnsureScratch(width, height);
            _autoWantsParallel = width * height >= ParallelPixelThreshold;

            fixed (int* pScratch = scratch)
            {
                // FlipY is absorbed here, so the blit below is always a plain forward copy.
                int* targetPtr = FlipY ? pScratch + (height - 1) * width : pScratch;
                int strideInts = FlipY ? -width : width;

                // Rendered outside the bitmap lock on purpose — see BitmapBlit.
                var lutMemory = ctx.LookupTable.AsReadOnlyMemory();
                _renderLoop(source, ctx.FrameIndex, targetPtr, strideInts, width, height,
                            lutMemory, valueScale, valueOffset, lutMaxIndex);

                using var fb = target.Lock();
                BitmapBlit.Rows(pScratch, width, height, fb);
            }
        }

        /// <summary>
        /// Returns the buffer the render loops write into, growing it when the frame size changes.
        /// </summary>
        /// <remarks>
        /// Deliberately not an <see cref="System.Buffers.ArrayPool{T}"/> rental: the shared pool
        /// does not retain arrays larger than 2^20 elements, so a 4000x3000 frame would allocate
        /// 48 MB on the large object heap on every single render. One buffer per writer — and
        /// there is one writer per <c>RenderSurface</c> — is both cheaper and simpler.
        /// </remarks>
        private int[] EnsureScratch(int width, int height)
        {
            int needed = width * height;
            if (_scratch == null || _scratch.Length < needed)
                _scratch = new int[needed];
            return _scratch;
        }

        #endregion

        #region Type-Specific Render Loops

        private unsafe void RenderByte(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = (MatrixData<byte>)source;
            var colorMap = _cachedColorMap!;
            var mem = typedData.AsMemory(frameIndex);

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    ReadOnlySpan<byte> row = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                        pRow[ix] = colorMap[row[ix]];
                });
            }
            else
            {
                for (int iy = 0; iy < height; iy++)
                {
                    ReadOnlySpan<byte> row = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ix++)
                        pRow[ix] = colorMap[row[ix]];
                }
            }
        }

        private unsafe void RenderUShort(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = (MatrixData<ushort>)source;
            var colorMap = _cachedColorMap!;
            var mem = typedData.AsMemory(frameIndex);

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    ReadOnlySpan<ushort> row = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                        pRow[ix] = colorMap[row[ix]];
                });
            }
            else
            {
                for (int iy = 0; iy < height; iy++)
                {
                    ReadOnlySpan<ushort> row = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ix++)
                        pRow[ix] = colorMap[row[ix]];
                }
            }
        }

        private unsafe void RenderShort(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = (MatrixData<short>)source;
            var colorMap = _cachedColorMap!;
            var mem = typedData.AsMemory(frameIndex);

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                        pRow[ix] = colorMap[values[ix] + 32768];
                });
            }
            else
            {
                for (int iy = 0; iy < height; iy++)
                {
                    var values = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                        pRow[ix] = colorMap[values[ix] + 32768];
                }
            }
        }

        private unsafe void RenderInt(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = (MatrixData<int>)source;
            var mem = typedData.AsMemory(frameIndex);

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                        pRow[ix] = lut[Math.Clamp((int)(values[ix] * valueScale + valueOffset), 0, lutMaxIndex)];
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ix++)
                        pRow[ix] = lut[Math.Clamp((int)(values[rowOffset + ix] * valueScale + valueOffset), 0, lutMaxIndex)];
                }
            }
        }

        private unsafe void RenderFloat(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = (MatrixData<float>)source;
            int missingColor = _cachedLut?.MissingColor ?? 0;
            var mem = typedData.AsMemory(frameIndex);

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[rowOffset + ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        private unsafe void RenderDouble(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = (MatrixData<double>)source;
            int missingColor = _cachedLut?.MissingColor ?? 0;
            var mem = typedData.AsMemory(frameIndex);

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[rowOffset + ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        private unsafe void RenderComplex(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = (MatrixData<Complex>)source;
            if (StructValueConverter is not Func<Complex, double> converter)
                throw new InvalidOperationException(
                    "StructValueConverter must be set to Func<Complex, double> for Complex rendering.");
            int missingColor = _cachedLut?.MissingColor ?? 0;
            var mem = typedData.AsMemory(frameIndex);

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = converter(values[ix]);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = converter(values[rowOffset + ix]);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        private unsafe void RenderFallback(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts,
            int width, int height, ReadOnlyMemory<int> lutMemory,
            double valueScale, double valueOffset, int lutMaxIndex)
        {
            int missingColor = _cachedLut?.MissingColor ?? 0;

            if (EffectiveParallelOptions != null)
            {
                Parallel.For(0, height, EffectiveParallelOptions!, iy =>
                {
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = source.GetValueAt(ix, iy, frameIndex);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = source.GetValueAt(ix, iy, frameIndex);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// Creates a new <see cref="WriteableBitmap"/> from the specified matrix data using
        /// LUT-based pseudo-color rendering.
        /// </summary>
        /// <param name="source">The source matrix data.</param>
        /// <param name="frameIndex">Frame index to render.</param>
        /// <param name="lut">Lookup table for color mapping.</param>
        /// <param name="valueMin">
        /// Minimum value for color mapping.
        /// Pass <see cref="double.NaN"/> to auto-compute from frame data.
        /// </param>
        /// <param name="valueMax">
        /// Maximum value for color mapping.
        /// Pass <see cref="double.NaN"/> to auto-compute from frame data.
        /// </param>
        /// <param name="dpi">Target DPI. Defaults to 96×96.</param>
        /// <returns>A new bitmap containing the rendered frame.</returns>
        public static WriteableBitmap CreateBitmap(
            IMatrixData source,
            int frameIndex,
            LookupTable lut,
            double valueMin = double.NaN,
            double valueMax = double.NaN,
            global::Avalonia.Vector dpi = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            if (double.IsNaN(valueMin) || double.IsNaN(valueMax))
            {
                var (min, max) = source.GetValueRange(frameIndex);
                valueMin = min;
                valueMax = max;
            }

            var effectiveDpi = dpi == default ? new global::Avalonia.Vector(96, 96) : dpi;
            var bmp = new WriteableBitmap(
                new PixelSize(source.XCount, source.YCount),
                effectiveDpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            var writer = new LutBitmapWriter(source.ValueType);
            var context = new LutRenderingContext(frameIndex, lut, valueMin, valueMax);
            writer.Render(source, bmp, context);
            return bmp;
        }

        #endregion
    }
}
