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

        public ParallelRenderPolicy ParallelPolicy { get; set; } = ParallelRenderPolicy.Auto;

        /// <remarks>
        /// These loops have no serial branch of their own, so Never is expressed as a single
        /// worker rather than by skipping Parallel.For. The body then runs sequentially, which is
        /// what a caller asking to leave the cores alone actually wants; the residual scheduling
        /// overhead is a few microseconds per frame.
        /// Also replaces the previous <c>ParallelOptions ?? new ParallelOptions()</c>, which
        /// allocated a fresh options object on every render loop invocation.
        /// </remarks>
        private ParallelOptions EffectiveParallelOptions => ParallelPolicy switch
        {
            // Never wins over explicitly supplied options: a caller that set both is saying
            // "these options, if you ever go parallel", and Never says not to.
            ParallelRenderPolicy.Never => SerialParallelOptions,
            ParallelRenderPolicy.Always => ParallelOptions ?? SharedParallelOptions,
            // The policy answers "should this go parallel", the options answer "with how many", so
            // a supplied cap does not smuggle a sub-threshold frame onto the parallel path. Auto
            // has to honour the threshold, or it is just Always under a name that says otherwise;
            // before RenderCore has run there is no size to judge, hence the serial default.
            _ => _autoWantsParallel ? ParallelOptions ?? SharedParallelOptions : SerialParallelOptions,
        };

        /// <remarks>
        /// Far lower than LutBitmapWriter's 2^17, and measured rather than assumed: this loop
        /// reads three planes, blends and packs per pixel, so parallel pays for itself much
        /// sooner. On the same machine the crossover sits between 4k and 9k pixels - 64x64 is
        /// 0.0126 ms serial against 0.0133 ms parallel, while 96x96 is 0.0366 against 0.0130, and
        /// by 512x512 it is 1.22 against 0.20. Sharing LutBitmapWriter's constant would have left
        /// everything from 8k to 131k pixels serial for no reason.
        /// The serial side of that comparison is a plain loop; what this writer can actually
        /// express below the threshold is MaxDegreeOfParallelism = 1, which measured no better
        /// than a plain loop at any size. So the threshold buys nothing except avoiding a
        /// pointless parallel setup on frames too small to care about either way.
        /// </remarks>
        private const int ParallelPixelThreshold = 1 << 13;
        private static readonly ParallelOptions SharedParallelOptions = new();
        private static readonly ParallelOptions SerialParallelOptions = new() { MaxDegreeOfParallelism = 1 };
        private bool _autoWantsParallel;


        /// <summary>
        /// Gets or sets the converter used for struct-based values during Complex rendering.
        /// Must be set to <c>Func&lt;Complex, double&gt;</c> when <see cref="ValueType"/> is <see cref="Complex"/>.
        /// </summary>
        public object? StructValueConverter { get; set; }

        #endregion

        #region Cache Fields

        // Structural-equality cache key (see EnsureLutCache) — no rebuild when the recipes/frame
        // indices/blend mode are unchanged, even though RenderSurface.BuildCompositeContext
        // constructs a new CompositeRenderingContext record on every single call.
        private CompositeRenderingContext? _lastContext;
        private object? _lastStructValueConverter;

        // Direct-index LUTs for byte / ushort / short.
        // Null entry means the layer is inactive (not visible or zero gain).
        private int[][]? _cachedDirectMaps;

        // True only when ValueType is byte, there are exactly 3 active layers, and each one is a
        // pure R/G/B channel at Gain=1/Gamma=1/ValueMin=0/ValueMax=255 in Additive mode - i.e. the
        // rendered result is mathematically identical to a raw byte interleave (see
        // IsRgbFastPathEligible), so RenderCore can skip the LUT lookup + blend + clamp entirely.
        // Recomputed only when EnsureLutCache actually rebuilds, not per frame.
        private bool _isRgbFastPathEligible;

        // Diagnostic-only, computed alongside _isRgbFastPathEligible (see IsRgbFastPathEligible) -
        // null when eligible, otherwise names the first condition that failed.
        private string? _rgbFastPathRejectReason;

        /// <summary>
        /// Diagnostic-only: whether the last <see cref="Render"/> call actually took the pure-RGB
        /// Fast Path (see <see cref="IsRgbFastPathEligible"/>) instead of the general LUT/blend
        /// path. Not meant for rendering decisions - it only reports what already happened.
        /// </summary>
        public bool LastRenderUsedRgbFastPath => _isRgbFastPathEligible;

        /// <summary>
        /// Diagnostic-only: when <see cref="LastRenderUsedRgbFastPath"/> is <c>false</c>, names
        /// the first Fast Path condition (see <see cref="IsRgbFastPathEligible"/>) that the last
        /// render's <see cref="CompositeRenderingContext"/> failed to satisfy. Null when eligible
        /// or before the first render.
        /// </summary>
        public string? LastRgbFastPathRejectReason => _rgbFastPathRejectReason;

        // Frame-sized scratch buffer; see EnsureScratch.
        private int[]? _scratch;

        // Scale LUTs for int / float / double / Complex.
        private int[][]? _cachedScaleMaps;
        private double[]? _cachedScales;
        private double[]? _cachedOffsets;

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

            if (valueType == typeof(Complex))
                StructValueConverter = (Func<Complex, double>)(c => c.Magnitude);

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
                    _ => RenderFallbackComposite
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
            var structConverter = StructValueConverter;

            // Structural equality (record Equals), not ReferenceEquals: BuildCompositeContext
            // constructs a brand new CompositeRenderingContext on every single render call, so
            // reference equality never hits for a source that Refreshes every frame (e.g. a live
            // camera feed) - this rebuilt the cache every frame for nothing. Record equality still
            // resolves cheaply here: Recipes (IReadOnlyList<BlendRecipe>) and FrameIndices (int[])
            // have no structural equality of their own, so this is still just a couple of
            // reference comparisons plus a BlendMode value comparison - identical cost to the old
            // check when nothing changed, but it now actually skips the rebuild when it should.
            if (_lastContext is not null && _lastContext.Equals(ctx)
                && ReferenceEquals(_lastStructValueConverter, structConverter))
                return;

            if (ValueType == typeof(byte))
            {
                BuildDirectMaps(ctx.Recipes, size: 256, indexOffset: 0);
                _rgbFastPathRejectReason = RgbFastPathRejectReason(ctx);
                _isRgbFastPathEligible = _rgbFastPathRejectReason == null;
            }
            else if (ValueType == typeof(ushort))
            {
                BuildDirectMaps(ctx.Recipes, size: 65536, indexOffset: 0);
                _isRgbFastPathEligible = false;
                _rgbFastPathRejectReason = "ValueType is ushort, not byte";
            }
            else if (ValueType == typeof(short))
            {
                BuildDirectMaps(ctx.Recipes, size: 65536, indexOffset: 32768);
                _isRgbFastPathEligible = false;
                _rgbFastPathRejectReason = "ValueType is short, not byte";
            }
            else
            {
                BuildScaleMaps(ctx.Recipes, structConverter);
                _isRgbFastPathEligible = false;
                _rgbFastPathRejectReason = $"ValueType is {ValueType.Name}, not byte";
            }

            _lastContext = ctx;
            _lastStructValueConverter = structConverter;
        }

        /// <summary>
        /// True when <paramref name="ctx"/> describes exactly the pure-RGB-passthrough case: 3
        /// layers, each a pure R/G/B channel (no color mixing) at Gain=1/Gamma=1/ValueMin=0/
        /// ValueMax=255, blended Additively. Under those conditions <see cref="PackLayerColor"/>'s
        /// LUT reduces to the identity map (v in -> v in the same byte position out) and
        /// <see cref="BlendPixel"/>'s per-layer accumulation never has more than one nonzero
        /// contributor per channel, so the general path's output is mathematically identical to a
        /// raw byte interleave - see RenderByteCompositeFastRgb.
        /// </summary>
        private static string? RgbFastPathRejectReason(CompositeRenderingContext ctx)
        {
            if (ctx.BlendMode != BlendMode.Additive)
                return $"BlendMode is {ctx.BlendMode}, not Additive";
            if (ctx.Recipes.Count != 3)
                return $"{ctx.Recipes.Count} active layer(s), not 3";

            return PureChannelRejectReason(ctx.Recipes[0], unchecked((int)0xFFFF0000), "R")
                ?? PureChannelRejectReason(ctx.Recipes[1], unchecked((int)0xFF00FF00), "G")
                ?? PureChannelRejectReason(ctx.Recipes[2], unchecked((int)0xFF0000FF), "B");
        }

        private static string? PureChannelRejectReason(BlendRecipe r, int argb, string channelLabel)
        {
            if (!r.IsVisible) return $"{channelLabel} not visible";
            if (r.ColorArgb != argb) return $"{channelLabel} color is 0x{r.ColorArgb:X8}, not 0x{argb:X8}";
            if (r.Gain != 1.0) return $"{channelLabel} Gain is {r.Gain}, not 1.0";
            if (r.Gamma != 1.0) return $"{channelLabel} Gamma is {r.Gamma}, not 1.0";
            if (r.ValueMin != 0.0) return $"{channelLabel} ValueMin is {r.ValueMin}, not 0";
            if (r.ValueMax != 255.0) return $"{channelLabel} ValueMax is {r.ValueMax}, not 255";
            return null;
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
        /// Also caches per-layer scale/offset arrays.
        /// </summary>
        private void BuildScaleMaps(IReadOnlyList<BlendRecipe> recipes, object? structConverter)
        {
            int count = recipes.Count;
            _cachedScaleMaps = new int[count][];
            _cachedScales = new double[count];
            _cachedOffsets = new double[count];

            if (ValueType == typeof(Complex)
                && structConverter is not Func<Complex, double>)
            {
                throw new InvalidOperationException(
                    "StructValueConverter must be set to Func<Complex, double> for Complex rendering.");
            }

            for (int i = 0; i < count; i++)
            {
                var r = recipes[i];
                if (!r.IsVisible || r.Gain <= 0) { _cachedScaleMaps[i] = null!; continue; }

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

        private unsafe void RenderCore(IMatrixData source, WriteableBitmap target,
                                       CompositeRenderingContext ctx)
        {
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
                if (_isRgbFastPathEligible)
                    RenderByteCompositeFastRgb(source, ctx.FrameIndices, targetPtr, strideInts, width, height);
                else
                    _renderLoop(source, ctx.FrameIndices, targetPtr, strideInts,
                                width, height, ctx.BlendMode);

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

            var pOpts = EffectiveParallelOptions;
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

        /// <summary>
        /// Only ever called when <see cref="_isRgbFastPathEligible"/> is true (see
        /// <see cref="IsRgbFastPathEligible"/>) - no LUT lookup, no per-layer blend accumulation,
        /// no clamp: each output pixel is exactly the source R/G/B triplet packed into BGRA,
        /// mathematically identical to what the general path above produces for this specific
        /// configuration (pure R/G/B channels, Gain=1, Gamma=1, full 0-255 range, Additive).
        /// </summary>
        private unsafe void RenderByteCompositeFastRgb(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height)
        {
            var typedData = (MatrixData<byte>)source;
            var rMem = typedData.AsMemory(frameIndices[0]);
            var gMem = typedData.AsMemory(frameIndices[1]);
            var bMem = typedData.AsMemory(frameIndices[2]);

            var pOpts = EffectiveParallelOptions;
            Parallel.For(0, height, pOpts, iy =>
            {
                var rSpan = rMem.Span;
                var gSpan = gMem.Span;
                var bSpan = bMem.Span;
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int i = rowOffset + ix;
                    pRow[ix] = unchecked((int)(0xFF000000u
                        | ((uint)rSpan[i] << 16) | ((uint)gSpan[i] << 8) | bSpan[i]));
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

            var pOpts = EffectiveParallelOptions;
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

            var pOpts = EffectiveParallelOptions;
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

            var pOpts = EffectiveParallelOptions;
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

            var pOpts = EffectiveParallelOptions;
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

            var pOpts = EffectiveParallelOptions;
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

            if (StructValueConverter is not Func<Complex, double> converter)
                throw new InvalidOperationException(
                    "StructValueConverter must be set to Func<Complex, double> for Complex rendering.");

            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;

            var memories = new ReadOnlyMemory<Complex>[frameIndices.Length];
            foreach (int a in active) memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = EffectiveParallelOptions;
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                    {
                        double val = converter(memories[a].Span[rowOffset + ix]);
                        if (double.IsNaN(val)) continue;
                        int idx = Math.Clamp(
                            (int)(val * scales[a] + offsets[a]), 0, lutMax);
                        BlendPixel(blendMode, maps[a][idx], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderFallbackComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var maps = _cachedScaleMaps!;
            int[] active = GetActiveIndices(frameIndices.Length, maps);
            if (active.Length == 0) return;

            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;

            var pOpts = EffectiveParallelOptions;
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    foreach (int a in active)
                    {
                        double val = source.GetValueAt(ix, iy, frameIndices[a]);
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