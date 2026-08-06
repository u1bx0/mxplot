using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MxPlot.Core;
using MxPlot.Core.Imaging;
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

        #endregion

        #region IBitmapWriter Properties

        /// <inheritdoc />
        public Type ValueType { get; }

        /// <inheritdoc />
        public bool FlipY { get; set; } = true;

        /// <inheritdoc />
        public ParallelOptions? ParallelOptions { get; set; }

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

        private void RebuildColorMapCache(LutRenderingContext ctx)
        {
            int size, offset;
            if (ValueType == typeof(byte)) { size = 256; offset = 0; }
            else if (ValueType == typeof(ushort)) { size = 65536; offset = 0; }
            else if (ValueType == typeof(short)) { size = 65536; offset = 32768; }
            else return;

            var (viewMin, viewMax) = ctx.GetEffectiveRange();
            double range = viewMax - viewMin;
            if (range == 0) range = 1.0;
            double scale = (ctx.LookupTable.Levels - 1) / range;
            double valueOffset = -viewMin * scale;
            int lutMaxIndex = ctx.LookupTable.Levels - 1;
            var lut = ctx.LookupTable.AsSpan();

            var map = new int[size];
            for (int v = 0; v < size; v++)
            {
                int index = (int)((v - offset) * scale + valueOffset);
                map[v] = lut[Math.Clamp(index, 0, lutMaxIndex)];
            }

            _cachedColorMap = map;
            _cachedLut = ctx.LookupTable;
            _cachedMin = ctx.ValueMin;
            _cachedMax = ctx.ValueMax;
            _cachedInverted = ctx.IsInvertedColor;
        }

        #endregion

        #region Core Rendering

        private void RenderCore(IMatrixData source, WriteableBitmap target, LutRenderingContext ctx)
        {
            using var fb = target.Lock();

            unsafe
            {
                var (viewMin, viewMax) = ctx.GetEffectiveRange();
                double range = viewMax - viewMin;
                if (range == 0) range = 1.0;

                double valueScale = (ctx.LookupTable.Levels - 1) / range;
                double valueOffset = -viewMin * valueScale;
                int lutMaxIndex = ctx.LookupTable.Levels - 1;

                int width = source.XCount;
                int height = source.YCount;
                int posStride = fb.RowBytes / 4;

                int* targetPtr;
                int strideInts;
                if (FlipY)
                {
                    targetPtr = (int*)fb.Address + (height - 1) * posStride;
                    strideInts = -posStride;
                }
                else
                {
                    targetPtr = (int*)fb.Address;
                    strideInts = posStride;
                }

                var lutMemory = ctx.LookupTable.AsReadOnlyMemory();
                _renderLoop(source, ctx.FrameIndex, targetPtr, strideInts, width, height,
                            lutMemory, valueScale, valueOffset, lutMaxIndex);
            }
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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

            if (ParallelOptions != null)
            {
                Parallel.For(0, height, ParallelOptions, iy =>
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
