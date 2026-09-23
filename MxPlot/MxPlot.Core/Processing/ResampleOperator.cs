using MxPlot.Core.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.Core.Processing
{
    /// <summary>How <see cref="ResampleOperator.Resample{T}"/> obtains the value of a new pixel.</summary>
    public enum ResampleMethod
    {
        /// <summary>
        /// The value of the nearest source pixel. Exact for every element type, since no arithmetic is
        /// done. A position exactly halfway between two source pixels takes the higher one.
        /// </summary>
        Nearest,

        /// <summary>
        /// The distance-weighted average of the four surrounding source pixels. Integer types are
        /// rounded to the nearest value; Complex values are interpolated in the real and imaginary
        /// parts separately. An element type that is neither a supported numeric type nor has an
        /// interpolator registered falls back to <see cref="Nearest"/>, as <c>GetValue</c> does.
        /// </summary>
        Bilinear,
    }

    /// <summary>
    /// Changes the number of pixels of every frame to <paramref name="Width"/> x <paramref name="Height"/>,
    /// keeping the physical extent (see <see cref="ResampleOperator.Resample{T}"/>). The result has the
    /// same element type as the source.
    /// </summary>
    /// <param name="Width">The new pixel count along X (at least 1).</param>
    /// <param name="Height">The new pixel count along Y (at least 1).</param>
    /// <param name="Method">How a new pixel gets its value.</param>
    /// <param name="Progress">
    /// Optional progress reporter; reports a negative total once, then <c>0 .. total-1</c> as frames complete.
    /// </param>
    /// <param name="CancellationToken">Checked before each frame and each block of rows.</param>
    public record ResampleOperation(
        int Width,
        int Height,
        ResampleMethod Method = ResampleMethod.Bilinear,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        /// <inheritdoc/>
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Resample(Width, Height, Method, Progress, CancellationToken);
    }

    /// <summary>Changes the pixel count of a <see cref="MatrixData{T}"/> along X and Y.</summary>
    public static class ResampleOperator
    {
        /// <summary>Output pixels per frame from which one frame is split into blocks of rows for parallel work.</summary>
        private const int RowSplitThreshold = 1 << 16;

        private enum Kernel { Copy, Nearest, BilinearNumeric, BilinearInterpolator }

        /// <summary>The source positions that the pixels of one output axis take their values from.</summary>
        private sealed class AxisMap
        {
            /// <summary>The nearest source index (Nearest), or the lower of the two neighbors (Bilinear).</summary>
            public readonly int[] Low;

            /// <summary>The upper neighbor; equal to <see cref="Low"/> where the position is exactly on a source pixel.</summary>
            public readonly int[] High;

            /// <summary>The weight of the upper neighbor, in [0, 1).</summary>
            public readonly double[] Weight;

            public AxisMap(int sourceCount, int newCount, bool bilinear)
            {
                Low = new int[newCount];
                High = new int[newCount];
                Weight = new double[newCount];

                // New pixel o sits at source position o * (sourceCount - 1) / (newCount - 1): the first
                // and last pixel keep the physical positions of the source's, which is what a fixed
                // XMin/XMax means. The position is kept as the exact fraction num / den, so positions on
                // a source pixel and exactly halfway between two are decided without floating-point noise.
                // A single new pixel sits at the first source pixel (num = 0).
                long den = Math.Max(1, newCount - 1);
                for (int o = 0; o < newCount; o++)
                {
                    long num = (long)o * (sourceCount - 1);
                    if (bilinear)
                    {
                        long low = num / den;
                        long rem = num % den;
                        Low[o] = (int)low;
                        High[o] = rem == 0 ? (int)low : (int)low + 1;
                        Weight[o] = (double)rem / den;
                    }
                    else
                    {
                        // Round half up.
                        Low[o] = (int)((2 * num + den) / (2 * den));
                    }
                }
            }
        }

        private static class ElementInfo<T> where T : unmanaged
        {
            public static readonly bool IsNumeric = MatrixData.SupportedPrimitiveTypes.Contains(typeof(T));
        }

        /// <summary>
        /// Changes the number of pixels of every frame to <paramref name="newWidth"/> x
        /// <paramref name="newHeight"/>, keeping the physical extent <c>XMin..XMax</c>, <c>YMin..YMax</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Pixel centers lie on the scale (the first and last pixel at the minimum and maximum), so the
        /// pixel pitch becomes <c>(max - min) / (count - 1)</c> for the new count. The value of a new
        /// pixel is the source sampled at the new pixel's physical position, the same value
        /// <c>GetValue(x, y, frame, interpolate)</c> returns there. A result of one pixel along an axis
        /// takes the source's first pixel.
        /// </para>
        /// <para>
        /// Every frame is processed. To resample one frame, slice it out first (<c>SliceAt</c>). Units,
        /// metadata and the axes of the source are carried over. The result is held in memory, also
        /// when the source is a virtual (on-demand) data set.
        /// </para>
        /// <para>
        /// The values are point samples: reducing the pixel count by a large factor aliases fine
        /// structure instead of averaging it. An identical pixel count returns a copy of the source.
        /// </para>
        /// <para>
        /// The source positions of the new columns and rows are worked out once and shared by all
        /// frames, so a pixel costs a few array reads and, for <see cref="ResampleMethod.Bilinear"/>,
        /// a few multiplications. Frames are processed in parallel; one large frame is split into
        /// blocks of rows.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The element type; the result has the same type.</typeparam>
        /// <param name="src">The source data.</param>
        /// <param name="newWidth">The new pixel count along X (at least 1).</param>
        /// <param name="newHeight">The new pixel count along Y (at least 1).</param>
        /// <param name="method">How a new pixel gets its value.</param>
        /// <param name="progress">
        /// Optional progress reporter; reports a negative total once, then <c>0 .. total-1</c> as frames complete.
        /// </param>
        /// <param name="cancellationToken">Checked before each frame and each block of rows.</param>
        /// <returns>A new <see cref="MatrixData{T}"/> with the new pixel counts.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="src"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// A new pixel count is below 1, or one frame of the result would exceed the largest array.
        /// </exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signalled.</exception>
        public static MatrixData<T> Resample<T>(
            this MatrixData<T> src, int newWidth, int newHeight,
            ResampleMethod method = ResampleMethod.Bilinear,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            where T : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (newWidth < 1)
                throw new ArgumentOutOfRangeException(nameof(newWidth), newWidth, "The pixel count must be at least 1.");
            if (newHeight < 1)
                throw new ArgumentOutOfRangeException(nameof(newHeight), newHeight, "The pixel count must be at least 1.");
            long framePixels = (long)newWidth * newHeight;
            if (framePixels > Array.MaxLength)
                throw new ArgumentOutOfRangeException(nameof(newWidth), $"{newWidth} x {newHeight} pixels do not fit in one frame array.");

            int srcWidth = src.XCount;
            int srcHeight = src.YCount;
            int frames = src.FrameCount;

            var interpolator = src.Interpolator;
            Kernel kernel;
            if (newWidth == srcWidth && newHeight == srcHeight) kernel = Kernel.Copy;
            else if (method == ResampleMethod.Nearest) kernel = Kernel.Nearest;
            else if (ElementInfo<T>.IsNumeric) kernel = Kernel.BilinearNumeric;
            else if (interpolator != null) kernel = Kernel.BilinearInterpolator;
            else kernel = Kernel.Nearest;

            bool bilinear = kernel is Kernel.BilinearNumeric or Kernel.BilinearInterpolator;
            var columns = kernel == Kernel.Copy ? null : new AxisMap(srcWidth, newWidth, bilinear);
            var rows = kernel == Kernel.Copy ? null : new AxisMap(srcHeight, newHeight, bilinear);

            var arrays = new T[frames][];
            progress?.Report(-frames);
            int completed = 0;
            var options = new ParallelOptions { CancellationToken = cancellationToken };
            bool splitRows = kernel != Kernel.Copy && framePixels >= RowSplitThreshold;

            void ResampleFrame(int frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = src.AsMemory(frame);
                var result = new T[framePixels];

                if (kernel == Kernel.Copy)
                {
                    source.Span.CopyTo(result);
                }
                else if (splitRows)
                {
                    int block = Math.Max(1, newHeight / (Environment.ProcessorCount * 4));
                    int blocks = (newHeight + block - 1) / block;
                    Parallel.For(0, blocks, options, b =>
                    {
                        int first = b * block;
                        ResampleRows(kernel, source.Span, result, first, Math.Min(newHeight, first + block),
                            srcWidth, newWidth, columns!, rows!, interpolator);
                    });
                }
                else
                {
                    ResampleRows(kernel, source.Span, result, 0, newHeight, srcWidth, newWidth, columns!, rows!, interpolator);
                }

                arrays[frame] = result;
                progress?.Report(Interlocked.Increment(ref completed) - 1);
            }

            if (frames >= 2)
                Parallel.For(0, frames, options, ResampleFrame);
            else
                ResampleFrame(0);

            var resampled = new MatrixData<T>(newWidth, newHeight, new List<T[]>(arrays));
            resampled.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            resampled.CopyPropertiesFrom(src, copyScale: false);
            return resampled;
        }

        private static void ResampleRows<T>(
            Kernel kernel, ReadOnlySpan<T> source, T[] result, int firstRow, int endRow,
            int srcWidth, int newWidth, AxisMap columns, AxisMap rows, IBilinearInterpolator<T>? interpolator)
            where T : unmanaged
        {
            var lowX = columns.Low;
            var highX = columns.High;
            var weightX = columns.Weight;

            for (int oy = firstRow; oy < endRow; oy++)
            {
                var target = result.AsSpan(oy * newWidth, newWidth);

                if (kernel == Kernel.Nearest)
                {
                    // Upscaling repeats source rows: the repeat is a copy of the row just written.
                    if (oy > firstRow && rows.Low[oy] == rows.Low[oy - 1])
                    {
                        result.AsSpan((oy - 1) * newWidth, newWidth).CopyTo(target);
                        continue;
                    }
                    var row = source.Slice(rows.Low[oy] * srcWidth, srcWidth);
                    for (int ox = 0; ox < target.Length; ox++)
                        target[ox] = row[lowX[ox]];
                    continue;
                }

                var upper = source.Slice(rows.Low[oy] * srcWidth, srcWidth);
                var lower = source.Slice(rows.High[oy] * srcWidth, srcWidth);
                double weightY = rows.Weight[oy];

                if (kernel == Kernel.BilinearInterpolator)
                {
                    for (int ox = 0; ox < target.Length; ox++)
                        target[ox] = interpolator!.Interpolate(
                            upper[lowX[ox]], upper[highX[ox]], lower[lowX[ox]], lower[highX[ox]], weightX[ox], weightY);
                }
                else if (weightY == 0)
                {
                    // On a source row: nothing to blend vertically.
                    for (int ox = 0; ox < target.Length; ox++)
                    {
                        double a = NumericConverter.ToDouble(upper[lowX[ox]]);
                        double b = NumericConverter.ToDouble(upper[highX[ox]]);
                        target[ox] = NumericConverter.FromDouble<T>(a + (b - a) * weightX[ox]);
                    }
                }
                else
                {
                    for (int ox = 0; ox < target.Length; ox++)
                    {
                        int x0 = lowX[ox];
                        int x1 = highX[ox];
                        double wx = weightX[ox];
                        double a = NumericConverter.ToDouble(upper[x0]);
                        double b = NumericConverter.ToDouble(upper[x1]);
                        double c = NumericConverter.ToDouble(lower[x0]);
                        double d = NumericConverter.ToDouble(lower[x1]);
                        double top = a + (b - a) * wx;
                        double bottom = c + (d - c) * wx;
                        target[ox] = NumericConverter.FromDouble<T>(top + (bottom - top) * weightY);
                    }
                }
            }
        }
    }
}
