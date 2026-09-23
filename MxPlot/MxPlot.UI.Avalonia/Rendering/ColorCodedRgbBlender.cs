using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// How <see cref="ColorCodedRgbBlender"/> combines the depth-colored slices of one pixel.
    /// </summary>
    public enum ColorCodedBlend
    {
        /// <summary>Color (RGB-Max): each R/G/B component keeps its largest value over the slices.</summary>
        Maximum,
        /// <summary>Color (RGB-Add): the components are summed over the slices, clamped to 255.</summary>
        Additive,
        /// <summary>
        /// Color (RGB-Avg): the depth colors averaged with the slices' intensities as weights,
        /// brightness from the strongest slice. Not offered in the ProjectionSelector (no ComboBox
        /// item), so users cannot reach it; kept for programmatic use.
        /// </summary>
        Average,
    }

    /// <summary>
    /// Color (RGB-Max) / (RGB-Add) / (RGB-Avg): every slice of <c>[Start, End]</c> is given its depth
    /// color (scaled by that voxel's intensity <c>t</c>) and the slices are combined per pixel.
    /// Unlike the winner-take-all <see cref="ColorCodedColorizer"/>, overlapping structures at
    /// different depths mix, so the result can contain colors that are not in the depth palette.
    /// <list type="bullet">
    /// <item><see cref="ColorCodedBlend.Maximum"/>: each component keeps its largest value. Read it as three soft depth bands (the
    /// palette's R/G/B components as depth windows), each maximum-projected.</item>
    /// <item><see cref="ColorCodedBlend.Additive"/>: components are summed and clamped to 255 -- every
    /// slice is an independent light source and the image is their total, the same model Composite
    /// uses for channels. Whites out on a dense stack; ValueMax acts as its gain.</item>
    /// <item><see cref="ColorCodedBlend.Average"/>: color = <c>Σ(depthColor * t) / Σt</c>, the depth
    /// colors averaged with the intensities as weights; brightness = the strongest slice's <c>t</c>
    /// (the Maximum projection). Never saturates, but two structures at different depths blend into
    /// an in-between color, as if there were one structure between them.</item>
    /// </list>
    /// The Maximum/Additive per-pixel blend is Composite's
    /// (<see cref="CompositeBitmapWriter.BlendPixel"/> / <see cref="CompositeBitmapWriter.ClampAndPackPixel"/>),
    /// so those definitions stay one.
    /// <para>
    /// Lives in the UI/Rendering layer, next to <see cref="ColorCodedColorizer"/>, for the same
    /// reason: it needs a depth palette, which Core deliberately does not know about. It reads the
    /// slices through the public <see cref="MatrixData{T}.AsMemory"/> accessor rather than a Core
    /// <c>VolumeAccessor</c>, so no Core change is needed.
    /// </para>
    /// <para>
    /// The per-voxel intensity is clamped to <c>[ValueMin, ValueMax]</c> before the blend, so the
    /// result depends on the value range -- every range/palette/Invert change needs a fresh sweep
    /// (unlike Color(Max), where the range is applied at render time to an already-picked winner).
    /// </para>
    /// </summary>
    public static class ColorCodedRgbBlender
    {
        /// <summary>
        /// Blends the slices <c>[start, end]</c> of <paramref name="axisName"/> into one packed-ARGB image.
        /// </summary>
        /// <param name="source">The volume; must be numeric (not Complex).</param>
        /// <param name="axisName">The axis being swept.</param>
        /// <param name="start">First axis index (inclusive).</param>
        /// <param name="end">Last axis index (inclusive).</param>
        /// <param name="valueMin">Intensity normalization lower bound (maps to fully dark).</param>
        /// <param name="valueMax">Intensity normalization upper bound (maps to full brightness).</param>
        /// <param name="depthColors">
        /// ARGB color per swept slice, length <c>End - Start + 1</c>, in the same (Invert-applied)
        /// order <see cref="ColorCodedRenderInfo.DepthColors"/> uses.
        /// </param>
        /// <param name="mode">How the slices are combined.</param>
        /// <param name="baseIndices">
        /// Positions of all axes to hold the non-swept axes at, as returned by
        /// <c>DimensionStructure.GetAxisIndices</c>. <c>null</c> uses the current active indices,
        /// the same default <c>ExtremumIndexOperation</c> has.
        /// </param>
        /// <returns>A packed-ARGB <c>MatrixData&lt;int&gt;</c> with the source's XY size and scale.</returns>
        public static MatrixData<int> Blend(
            IMatrixData source, string axisName, int start, int end,
            double valueMin, double valueMax, IReadOnlyList<int> depthColors,
            ColorCodedBlend mode, int[]? baseIndices = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            int sliceCount = end - start + 1;
            if (start < 0 || sliceCount < 1 || end >= source.Dimensions[axisName].Count)
                throw new ArgumentOutOfRangeException(nameof(start), $"[{start}, {end}] is out of range for axis '{axisName}'.");
            if (depthColors.Count < sliceCount)
                throw new ArgumentException("depthColors must have one color per swept slice.", nameof(depthColors));

            var dims = source.Dimensions;
            int axisOrder = dims.GetAxisOrder(axisName);
            int[] indices = baseIndices != null ? (int[])baseIndices.Clone() : dims.GetAxisIndices();
            var frameIndices = new int[sliceCount];
            for (int i = 0; i < sliceCount; i++)
            {
                indices[axisOrder] = start + i;
                frameIndices[i] = dims.GetFrameIndexAt(indices);
            }

            return source.ValueType switch
            {
                Type t when t == typeof(byte) => BlendTyped((MatrixData<byte>)source, frameIndices, valueMin, valueMax, depthColors, mode),
                Type t when t == typeof(ushort) => BlendTyped((MatrixData<ushort>)source, frameIndices, valueMin, valueMax, depthColors, mode),
                Type t when t == typeof(short) => BlendTyped((MatrixData<short>)source, frameIndices, valueMin, valueMax, depthColors, mode),
                Type t when t == typeof(int) => BlendTyped((MatrixData<int>)source, frameIndices, valueMin, valueMax, depthColors, mode),
                Type t when t == typeof(float) => BlendTyped((MatrixData<float>)source, frameIndices, valueMin, valueMax, depthColors, mode),
                Type t when t == typeof(double) => BlendTyped((MatrixData<double>)source, frameIndices, valueMin, valueMax, depthColors, mode),
                _ => throw new NotSupportedException(
                    $"ColorCodedRgbBlender does not support value type '{source.ValueType}'."),
            };
        }

        /// <summary>The voxel's intensity in [0, 1]; full intensity for a degenerate (empty) range.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Intensity<T>(T value, double valueMin, double range) where T : INumber<T>
            => range > 0 ? Math.Clamp((double.CreateChecked(value) - valueMin) / range, 0.0, 1.0) : 1.0;

        private static MatrixData<int> BlendTyped<T>(
            MatrixData<T> src, int[] frameIndices, double valueMin, double valueMax,
            IReadOnlyList<int> depthColors, ColorCodedBlend mode)
            where T : unmanaged, INumber<T>
        {
            int width = src.XCount;
            int height = src.YCount;
            int n = frameIndices.Length;

            // Read-only views (AsMemory does not invalidate the source's cached min/max). All slices
            // are held at once, exactly like ExtremumIndexOperation's scan does.
            var frames = new T[n][];
            var cr = new int[n];
            var cg = new int[n];
            var cb = new int[n];
            for (int i = 0; i < n; i++)
            {
                var mem = src.AsMemory(frameIndices[i]);
                frames[i] = MemoryMarshal.TryGetArray(mem, out ArraySegment<T> seg) && seg.Offset == 0
                    ? seg.Array!
                    : mem.ToArray();
                int c = depthColors[i];
                cr[i] = (c >> 16) & 0xFF;
                cg[i] = (c >> 8) & 0xFF;
                cb[i] = c & 0xFF;
            }

            var result = new int[width * height];
            double range = valueMax - valueMin;
            bool average = mode == ColorCodedBlend.Average;
            var componentMode = mode == ColorCodedBlend.Additive ? BlendMode.Additive : BlendMode.Maximum;

            Parallel.For(0, height, y =>
            {
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    int pixel = rowStart + x;
                    result[pixel] = average
                        ? AveragePixel(frames, pixel, n, cr, cg, cb, valueMin, range)
                        : ComponentPixel(frames, pixel, n, cr, cg, cb, valueMin, range, componentMode);
                }
            });

            var md = new MatrixData<int>(width, height, result);
            md.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            md.XUnit = src.XUnit;
            md.YUnit = src.YUnit;
            return md;
        }

        // Truncation ((byte)(c * t)) in both, like ColorCodedColorizer / ColorCodedBitmapWriter, so a
        // slice that alone decides a pixel gives exactly the Color(Max) color.

        /// <summary>Maximum / Additive: each component is combined independently, by Composite's blend.</summary>
        private static int ComponentPixel<T>(
            T[][] frames, int pixel, int n, int[] cr, int[] cg, int[] cb,
            double valueMin, double range, BlendMode mode)
            where T : INumber<T>
        {
            int r = 0, g = 0, b = 0;
            for (int i = 0; i < n; i++)
            {
                double t = Intensity(frames[i][pixel], valueMin, range);
                if (t <= 0.0) continue;
                int scaled = ((byte)(cr[i] * t) << 16) | ((byte)(cg[i] * t) << 8) | (byte)(cb[i] * t);
                CompositeBitmapWriter.BlendPixel(mode, scaled, ref r, ref g, ref b);
            }
            return CompositeBitmapWriter.ClampAndPackPixel(mode, r, g, b);
        }

        private static int AveragePixel<T>(
            T[][] frames, int pixel, int n, int[] cr, int[] cg, int[] cb, double valueMin, double range)
            where T : INumber<T>
        {
            double sumT = 0, maxT = 0, sr = 0, sg = 0, sb = 0;
            for (int i = 0; i < n; i++)
            {
                double t = Intensity(frames[i][pixel], valueMin, range);
                if (t <= 0.0) continue;
                sumT += t;
                if (t > maxT) maxT = t;
                sr += cr[i] * t;
                sg += cg[i] * t;
                sb += cb[i] * t;
            }
            // Nothing above ValueMin anywhere along the ray: dark.
            if (sumT <= 0.0) return unchecked((int)0xFF000000);

            // (Σ c*t / Σ t) * maxT, folded into one factor. For a single slice maxT == sumT, so the
            // factor is exactly 1 and the result is that slice's own c * t.
            double k = maxT / sumT;
            return unchecked((int)0xFF000000) | ((byte)(sr * k) << 16) | ((byte)(sg * k) << 8) | (byte)(sb * k);
        }
    }
}
