using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// High-performance multi-channel composite renderer for <see cref="IMatrixData"/>.
    /// Blends multiple frames into a single <see cref="WriteableBitmap"/> using
    /// per-layer color, gain, and gamma settings defined by <see cref="BlendRecipe"/> entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For small-range integer types (byte, ushort, short), per-layer direct-index color maps
    /// are pre-computed, eliminating all floating-point arithmetic in the inner pixel loop.
    /// For wider types (int, float, double, Complex), a fixed-size scale LUT
    /// (<see cref="ScaleLutSize"/> entries per layer) is used.
    /// </para>
    /// <para>
    /// LUT reconstruction is skipped when the <see cref="CompositeRenderingContext"/>
    /// is reference-equal to the one used in the previous call. Callers that reuse the same
    /// context instance across frames therefore pay no rebuild cost.
    /// </para>
    /// </remarks>
    public sealed class CompositeBitmapWriter : IBitmapWriter
    {
        #region Constants

        /// <summary>
        /// Number of entries in each scale-based LUT (used for int, float, double, Complex).
        /// Matches the value used in the original implementation.
        /// </summary>
        private const int ScaleLutSize = 4096;

        #endregion

        #region Delegate

        private unsafe delegate void RenderLoopDelegate(
            IMatrixData source,
            int[] frameIndices,
            int* targetPtr,
            int strideInts,
            int width,
            int height,
            BlendMode blendMode);

        #endregion

        #region IBitmapWriter Properties

        /// <inheritdoc />
        public Type ValueType { get; }

        /// <inheritdoc />
        public bool FlipY { get; set; } = true;

        /// <inheritdoc />
        public ParallelOptions? ParallelOptions { get; set; }

        /// <summary>
        /// Not used by <see cref="CompositeBitmapWriter"/>.
        /// Per-layer converters are specified via <see cref="BlendRecipe.ValueConverter"/>.
        /// </summary>
        public object? StructValueConverter { get; set; }

        #endregion

        #region Cache Fields

        // Reference-equality cache key — no rebuild when same context instance is reused.
        private CompositeRenderingContext? _lastContext;

        // Direct-index LUTs for byte / ushort / short.
        // Null entry means the layer is inactive (not visible or zero gain).
        private int[][]? _cachedDirectMaps;

        // Scale LUTs for int / float / double / Complex.
        private int[][]? _cachedScaleMaps;
        private double[]? _cachedScales;
        private double[]? _cachedOffsets;
        private Func<Complex, double>[]? _cachedConverters;

        #endregion

        #region Render Loop Field

        private readonly RenderLoopDelegate _renderLoop;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new <see cref="CompositeBitmapWriter"/> for the specified element type.
        /// </summary>
        /// <param name="valueType">
        /// The element type of the matrix data to render
        /// (e.g. <c>typeof(ushort)</c>, <c>typeof(float)</c>).
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="valueType"/> is <c>null</c>.</exception>
        /// <exception cref="NotSupportedException"><paramref name="valueType"/> is not supported.</exception>
        public CompositeBitmapWriter(Type valueType)
        {
            ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));

            unsafe
            {
                _renderLoop = valueType switch
                {
                    Type t when t == typeof(byte) => RenderByteComposite,
                    Type t when t == typeof(ushort) => RenderUShortComposite,
                    Type t when t == typeof(short) => RenderShortComposite,
                    Type t when t == typeof(int) => RenderIntComposite,
                    Type t when t == typeof(float) => RenderFloatComposite,
                    Type t when t == typeof(double) => RenderDoubleComposite,
                    Type t when t == typeof(Complex) => RenderComplexComposite,
                    _ => throw new NotSupportedException($"Type {valueType} is not supported.")
                };
            }
        }

        #endregion

        #region IBitmapWriter.Render

        /// <inheritdoc />
        public void Render(IMatrixData source, WriteableBitmap target, IRenderingContext context)
        {
            if (context is not CompositeRenderingContext ctx)
                throw new ArgumentException(
                    $"Expected {nameof(CompositeRenderingContext)}, got {context.GetType().Name}.",
                    nameof(context));

            Validate(source, target, ctx);
            EnsureLutCache(ctx);
            RenderCore(source, target, ctx);
        }

        #endregion

        #region Validation

        private void Validate(IMatrixData source, WriteableBitmap target, CompositeRenderingContext ctx)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));

            if (target.PixelSize.Width != source.XCount || target.PixelSize.Height != source.YCount)
                throw new ArgumentException(
                    $"Target bitmap size ({target.PixelSize.Width}×{target.PixelSize.Height}) " +
                    $"does not match source data size ({source.XCount}×{source.YCount}).");

            if (ctx.FrameIndices.Length != ctx.Recipes.Count)
                throw new ArgumentException(
                    "FrameIndices and Recipes must have the same length.");

            if (source.ValueType != ValueType)
                throw new ArgumentException(
                    $"Source type {source.ValueType} does not match writer type {ValueType}.");
        }

        #endregion

        #region LUT Cache

        private void EnsureLutCache(CompositeRenderingContext ctx)
        {
            // Reference equality: same context instance → skip rebuild.
            if (ReferenceEquals(_lastContext, ctx)) return;

            if (ValueType == typeof(byte))
                BuildDirectMaps(ctx.Recipes, size: 256, indexOffset: 0);
            else if (ValueType == typeof(ushort))
                BuildDirectMaps(ctx.Recipes, size: 65536, indexOffset: 0);
            else if (ValueType == typeof(short))
                BuildDirectMaps(ctx.Recipes, size: 65536, indexOffset: 32768);
            else
                BuildScaleMaps(ctx.Recipes);

            _lastContext = ctx;
        }

        /// <summary>
        /// Builds a direct-index color map per layer for byte / ushort / short data.
        /// All of ValueMin, ValueMax, Gain, Gamma, and Color are baked into the map.
        /// </summary>
        private void BuildDirectMaps(IReadOnlyList<BlendRecipe> recipes, int size, int indexOffset)
        {
            int count = recipes.Count;
            _cachedDirectMaps = new int[count][];

            for (int i = 0; i < count; i++)
            {
                var r = recipes[i];
                if (!r.IsVisible || r.Gain <= 0) { _cachedDirectMaps[i] = null!; continue; }

                double range = r.ValueMax - r.ValueMin;
                if (range == 0) range = 1.0;

                int baseR = (r.ColorArgb >> 16) & 0xFF;
                int baseG = (r.ColorArgb >> 8) & 0xFF;
                int baseB = r.ColorArgb & 0xFF;

                var map = new int[size];
                for (int v = 0; v < size; v++)
                {
                    double t = ((v - indexOffset) - r.ValueMin) / range;
                    map[v] = PackLayerColor(Math.Clamp(t, 0.0, 1.0), r.Gain, r.Gamma,
                                           baseR, baseG, baseB);
                }
                _cachedDirectMaps[i] = map;
            }
        }

        /// <summary>
        /// Builds a <see cref="ScaleLutSize"/>-entry color map per layer for
        /// int / float / double / Complex data.
        /// Also caches per-layer scale/offset arrays and Complex converters.
        /// </summary>
        private void BuildScaleMaps(IReadOnlyList<BlendRecipe> recipes)
        {
            int count = recipes.Count;
            _cachedScaleMaps = new int[count][];
            _cachedScales = new double[count];
            _cachedOffsets = new double[count];
            bool isComplex = ValueType == typeof(Complex);
            _cachedConverters = isComplex ? new Func<Complex, double>[count] : null;

            for (int i = 0; i < count; i++)
            {
                var r = recipes[i];
                if (!r.IsVisible || r.Gain <= 0) { _cachedScaleMaps[i] = null!; continue; }

                if (isComplex)
                {
                    _cachedConverters![i] = r.ValueConverter as Func<Complex, double>
                        ?? throw new InvalidOperationException(
                            $"Layer {i}: BlendRecipe.ValueConverter must be " +
                            $"Func<Complex, double> for Complex rendering.");
                }

                double range = r.ValueMax - r.ValueMin;
                if (range == 0) range = 1.0;
                _cachedScales[i] = (ScaleLutSize - 1) / range;
                _cachedOffsets[i] = -r.ValueMin * _cachedScales[i];

                int baseR = (r.ColorArgb >> 16) & 0xFF;
                int baseG = (r.ColorArgb >> 8) & 0xFF;
                int baseB = r.ColorArgb & 0xFF;

                var map = new int[ScaleLutSize];
                for (int v = 0; v < ScaleLutSize; v++)
                {
                    double t = v / (double)(ScaleLutSize - 1);
                    map[v] = PackLayerColor(t, r.Gain, r.Gamma, baseR, baseG, baseB);
                }
                _cachedScaleMaps[i] = map;
            }
        }

        #endregion

        #region Core Rendering

        private void RenderCore(IMatrixData source, WriteableBitmap target,
                                CompositeRenderingContext ctx)
        {
            using var fb = target.Lock();
            unsafe
            {
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

                _renderLoop(source, ctx.FrameIndices, targetPtr, strideInts,
                            source.XCount, height, ctx.BlendMode);
            }
        }

        #endregion

        #region Pixel Helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PackLayerColor(double t, double gain, double gamma,
                                          int baseR, int baseG, int baseB)
        {
            double tg = gamma == 1.0 ? t : Math.Pow(t, gamma);
            int intensity = Math.Clamp((int)(tg * gain * 255.0), 0, 255);
            return (((baseR * intensity) / 255) << 16)
                 | (((baseG * intensity) / 255) << 8)
                 | ((baseB * intensity) / 255);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BlendPixel(BlendMode mode, int color,
                                       ref int r, ref int g, ref int b)
        {
            int cr = (color >> 16) & 0xFF;
            int cg = (color >> 8) & 0xFF;
            int cb = color & 0xFF;
            if (mode == BlendMode.Additive) { r += cr; g += cg; b += cb; }
            else { if (cr > r) r = cr; if (cg > g) g = cg; if (cb > b) b = cb; }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ClampAndPackPixel(BlendMode mode, int r, int g, int b)
        {
            if (mode == BlendMode.Additive)
            {
                r = Math.Min(r, 255);
                g = Math.Min(g, 255);
                b = Math.Min(b, 255);
            }
            return (255 << 24) | (r << 16) | (g << 8) | b;
        }

        private static int[] GetActiveIndices(int count, int[][] maps)
        {
            var list = new System.Collections.Generic.List<int>(count);
            for (int i = 0; i < count; i++)
                if (maps[i] != null) list.Add(i);
            return list.ToArray();
        }

        #endregion

        #region Type-Specific Render Loops

        private unsafe void RenderByteComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = (MatrixData<byte>)source;
            var maps = _cachedDirectMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var memories = new ReadOnlyMemory<byte>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                        BlendPixel(blendMode, maps[a][memories[a].Span[rowOffset + ix]],
                                   ref r, ref g, ref b);
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderUShortComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = (MatrixData<ushort>)source;
            var maps = _cachedDirectMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var memories = new ReadOnlyMemory<ushort>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                        BlendPixel(blendMode, maps[a][memories[a].Span[rowOffset + ix]],
                                   ref r, ref g, ref b);
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderShortComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = (MatrixData<short>)source;
            var maps = _cachedDirectMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var memories = new ReadOnlyMemory<short>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                        BlendPixel(blendMode, maps[a][memories[a].Span[rowOffset + ix] + 32768],
                                   ref r, ref g, ref b);
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderIntComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = (MatrixData<int>)source;
            var maps = _cachedScaleMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;

            var memories = new ReadOnlyMemory<int>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                    {
                        int idx = Math.Clamp(
                            (int)(memories[a].Span[rowOffset + ix] * scales[a] + offsets[a]),
                            0, lutMax);
                        BlendPixel(blendMode, maps[a][idx], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderFloatComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = (MatrixData<float>)source;
            var maps = _cachedScaleMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;

            var memories = new ReadOnlyMemory<float>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                    {
                        float val = memories[a].Span[rowOffset + ix];
                        if (float.IsNaN(val)) continue;
                        int idx = Math.Clamp(
                            (int)(val * scales[a] + offsets[a]), 0, lutMax);
                        BlendPixel(blendMode, maps[a][idx], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderDoubleComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = (MatrixData<double>)source;
            var maps = _cachedScaleMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;

            var memories = new ReadOnlyMemory<double>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                    {
                        double val = memories[a].Span[rowOffset + ix];
                        if (double.IsNaN(val)) continue;
                        int idx = Math.Clamp(
                            (int)(val * scales[a] + offsets[a]), 0, lutMax);
                        BlendPixel(blendMode, maps[a][idx], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderComplexComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = (MatrixData<Complex>)source;
            var maps = _cachedScaleMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            var converters = _cachedConverters!;
            int lutMax = ScaleLutSize - 1;

            var memories = new ReadOnlyMemory<Complex>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                    {
                        double val = converters[a](memories[a].Span[rowOffset + ix]);
                        if (double.IsNaN(val)) continue;
                        int idx = Math.Clamp(
                            (int)(val * scales[a] + offsets[a]), 0, lutMax);
                        BlendPixel(blendMode, maps[a][idx], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        #endregion

        #region Static Factory

        /// <summary>
        /// Creates a new <see cref="WriteableBitmap"/> by compositing the specified frames.
        /// </summary>
        /// <param name="source">The source matrix data.</param>
        /// <param name="frameIndices">Frame indices to composite.</param>
        /// <param name="recipes">Per-layer rendering recipes.</param>
        /// <param name="blendMode">How layers are blended. Defaults to <see cref="BlendMode.Additive"/>.</param>
        /// <param name="dpi">Target DPI. Defaults to 96×96.</param>
        /// <returns>A new bitmap containing the composited result.</returns>
        public static WriteableBitmap CreateBitmap(
            IMatrixData source,
            int[] frameIndices,
            IReadOnlyList<BlendRecipe> recipes,
            BlendMode blendMode = BlendMode.Additive,
            global::Avalonia.Vector dpi = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var effectiveDpi = dpi == default ? new global::Avalonia.Vector(96, 96) : dpi;
            var bmp = new WriteableBitmap(
                new PixelSize(source.XCount, source.YCount),
                effectiveDpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            var writer = new CompositeBitmapWriter(source.ValueType);
            var context = new CompositeRenderingContext(frameIndices, recipes, blendMode);
            writer.Render(source, bmp, context);
            return bmp;
        }

        #endregion
    }
}