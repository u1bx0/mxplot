using Avalonia.Media.Imaging;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Per-<see cref="IBitmapWriter.Render"/> parameters for <see cref="ColorCodedBitmapWriter"/>:
    /// which frame of the source (winner-value) data to render, plus the winner-index/depth-colour/
    /// intensity-range/invert data <see cref="ColorCodedRenderInfo"/> already bundles.
    /// </summary>
    public sealed record ColorCodedRenderingContext(int FrameIndex, ColorCodedRenderInfo Info) : IRenderingContext;

    /// <summary>
    /// Renders the ColorCoded live-projection window's data -- the winner *value* matrix from
    /// <c>ExtremumIndexOperation</c>, an ordinary projection-shaped <see cref="IMatrixData"/> just
    /// like any other projection result -- by combining it at render time with the winner-index
    /// matrix and depth palette carried in <see cref="ColorCodedRenderInfo"/>.
    /// <para>
    /// This replaces the earlier design where the parent baked the final packed-ARGB pixels ahead of
    /// time (<c>TrueColorBitmapWriter</c>, a packed-ARGB-only writer with no value-to-colour mapping)
    /// and the window's own <c>MatrixData</c> was that opaque packed-int result. That shortcut made
    /// every generic tool that operates on a window's data -- Duplicate, Convert, Filter, Normalize,
    /// overlay/ROI analysis, even Save/Export -- silently meaningless for a ColorCoded window, since
    /// none of them know packed-ARGB from an ordinary numeric value. Keeping the window's own data as
    /// the real winner values (exactly what an ordinary Max/Min projection's data already is) and
    /// doing the colour combine only here, at render time, fixes all of that for free: those tools
    /// just operate on real projected values like they always do, and only the *display* differs.
    /// </para>
    /// <para>
    /// This is also what section 3.3.3 of the design doc originally intended -- winner-index/value
    /// extraction (<c>ExtremumIndexOperation</c>) as the heavy, low-frequency step, and colorizing as
    /// the light, high-frequency one done on every ValueRange/Invert/palette tweak. The
    /// per-pixel formula here matches <c>ColorCodedColorizer.ColorizeTyped</c> (kept for
    /// Phase 3 baking / its existing unit tests) exactly; it is intentionally not shared code, since
    /// the two write into different destinations (a managed <c>int[]</c> there, an unsafe frame
    /// buffer with FlipY handling here) the way no two <see cref="IBitmapWriter"/>s in this namespace
    /// share their inner pixel loop with a non-writer utility either.
    /// </para>
    /// </summary>
    public sealed class ColorCodedBitmapWriter : IBitmapWriter
    {
        private unsafe delegate void RenderLoopDelegate(
            IMatrixData source, int frameIndex, ColorCodedRenderInfo info,
            int* targetPtr, int strideInts, int width, int height);

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
        /// Shares CompositeBitmapWriter's threshold rather than LutBitmapWriter's: this loop also
        /// combines more than one source per pixel (value plus winner index plus depth palette),
        /// so its crossover should sit near composite's measured 4k-9k pixels rather than near
        /// the LUT writer's 131k. Unlike composite's, this one has not been measured directly -
        /// it is a structural argument, and the constant is worth revisiting if ColorCoded ever
        /// becomes a hot path.
        /// </remarks>
        private const int ParallelPixelThreshold = 1 << 13;
        private static readonly ParallelOptions SharedParallelOptions = new();
        private static readonly ParallelOptions SerialParallelOptions = new() { MaxDegreeOfParallelism = 1 };
        private bool _autoWantsParallel;


        /// <inheritdoc />
        public object? StructValueConverter { get; set; }

        private readonly RenderLoopDelegate _renderLoop;
        private int[]? _scratch;

        /// <summary>
        /// Initializes a new <see cref="ColorCodedBitmapWriter"/> for the specified winner-value
        /// element type. Complex is not supported -- <c>ExtremumIndexOperation</c> excludes it too,
        /// since "extreme value" has no ordering for a complex number.
        /// </summary>
        public ColorCodedBitmapWriter(Type valueType)
        {
            ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));

            unsafe
            {
                _renderLoop = valueType switch
                {
                    Type t when t == typeof(byte) => RenderTyped<byte>,
                    Type t when t == typeof(ushort) => RenderTyped<ushort>,
                    Type t when t == typeof(short) => RenderTyped<short>,
                    Type t when t == typeof(int) => RenderTyped<int>,
                    Type t when t == typeof(float) => RenderTyped<float>,
                    Type t when t == typeof(double) => RenderTyped<double>,
                    _ => throw new NotSupportedException(
                        $"{nameof(ColorCodedBitmapWriter)} does not support value type '{valueType}'."),
                };
            }
        }

        /// <inheritdoc />
        public unsafe void Render(IMatrixData source, WriteableBitmap target, IRenderingContext context)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (context is not ColorCodedRenderingContext ctx)
                throw new ArgumentException(
                    $"Expected {nameof(ColorCodedRenderingContext)}, got {context.GetType().Name}.",
                    nameof(context));
            if (source.ValueType != ValueType)
                throw new ArgumentException(
                    $"Source type {source.ValueType} does not match writer type {ValueType}.");
            if (target.PixelSize.Width != source.XCount || target.PixelSize.Height != source.YCount)
                throw new ArgumentException(
                    $"Target bitmap size ({target.PixelSize.Width}x{target.PixelSize.Height}) " +
                    $"does not match source data size ({source.XCount}x{source.YCount}).");
            if (ctx.Info.WinnerIndex.XCount != source.XCount || ctx.Info.WinnerIndex.YCount != source.YCount)
                throw new ArgumentException(
                    "ColorCodedRenderInfo.WinnerIndex dimensions do not match the source data.");

            int width = source.XCount;
            int height = source.YCount;
            int needed = width * height;
            if (_scratch == null || _scratch.Length < needed)
                _scratch = new int[needed];
            var scratch = _scratch;
            _autoWantsParallel = width * height >= ParallelPixelThreshold;

            fixed (int* pScratch = scratch)
            {
                // FlipY is absorbed here, so the blit below is always a plain forward copy --
                // mirrors every other writer in this namespace.
                int* targetPtr = FlipY ? pScratch + (height - 1) * width : pScratch;
                int strideInts = FlipY ? -width : width;

                _renderLoop(source, ctx.FrameIndex, ctx.Info, targetPtr, strideInts, width, height);

                using var fb = target.Lock();
                BitmapBlit.Rows(pScratch, width, height, fb);
            }
        }

        private unsafe void RenderTyped<T>(
            IMatrixData source, int frameIndex, ColorCodedRenderInfo info,
            int* targetPtr, int strideInts, int width, int height)
            where T : unmanaged, INumber<T>
        {
            var typedData = (MatrixData<T>)source;
            var valArray = typedData.GetArray(frameIndex);
            var idxArray = ((MatrixData<int>)info.WinnerIndex).GetArray(0);
            var depthColors = info.DepthColors;
            int colorCount = depthColors.Count;
            int start = info.Start;
            double valueMin = info.ValueMin;
            double range = info.ValueMax - info.ValueMin;

            var pOpts = EffectiveParallelOptions;
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowStart = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int pixel = rowStart + ix;
                    int localIndex = Math.Clamp(idxArray[pixel] - start, 0, colorCount - 1);
                    int baseColor = depthColors[localIndex];

                    // No separate invert here: DepthColors' own order already reflects it (see
                    // ColorCodedRenderInfo's doc comment on DepthColors).
                    double t = range > 0
                        ? Math.Clamp((double.CreateChecked(valArray[pixel]) - valueMin) / range, 0.0, 1.0)
                        : 1.0;

                    byte r = (byte)((byte)(baseColor >> 16) * t);
                    byte g = (byte)((byte)(baseColor >> 8) * t);
                    byte b = (byte)((byte)baseColor * t);
                    pRow[ix] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                }
            });
        }
    }
}
