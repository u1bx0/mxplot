using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Provides spatial filter operations for <see cref="MatrixData{T}"/>.
    /// Applies an <see cref="IFilterKernel"/> to every pixel of every frame,
    /// using edge-clamped neighborhoods and frame-level parallelism.
    /// </summary>
    public static class FilterOperator
    {
        /// <summary>
        /// Applies a spatial kernel filter to all frames.
        /// </summary>
        /// <typeparam name="T">The unmanaged element type.</typeparam>
        /// <param name="source">Source matrix data.</param>
        /// <param name="kernel">The filter kernel to apply.</param>
        /// <param name="useParallel">
        /// When <c>true</c> (default) and <c>FrameCount ≥ 2</c>, frames are processed
        /// in parallel via <see cref="Parallel.For"/>.
        /// </param>
        /// <param name="progress">
        /// Optional progress reporter. Receives a negative value (<c>-FrameCount</c>) as
        /// the initial "total" hint, then <c>0..FrameCount-1</c> as frames complete.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A new <see cref="MatrixData{T}"/> with the filtered data.</returns>
        public static MatrixData<T> ApplyFilter<T>(
            this MatrixData<T> source,
            IFilterKernel kernel,
            bool useParallel = true,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) where T : unmanaged
        {
            int w = source.XCount;
            int h = source.YCount;
            int frameCount = source.FrameCount;
            int r = kernel.Radius;
            int kernelFullSize = (2 * r + 1) * (2 * r + 1);

            var toDouble = MatrixData<T>.ToDoubleConverter;
            var fromDouble = MatrixData<T>.FromDoubleConverter;

            var dstArrays = new T[frameCount][];
            progress?.Report(-frameCount);

            void ProcessFrame(int frame)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var src = source.AsSpan(frame);
                var dst = new T[w * h];

                // Per-thread scratch buffer for neighborhood values
                var buf = new double[kernelFullSize];

                for (int py = 0; py < h; py++)
                {
                    for (int px = 0; px < w; px++)
                    {
                        int count = 0;
                        int yMin = Math.Max(0, py - r);
                        int yMax = Math.Min(h - 1, py + r);
                        int xMin = Math.Max(0, px - r);
                        int xMax = Math.Min(w - 1, px + r);

                        for (int ky = yMin; ky <= yMax; ky++)
                        {
                            int rowOff = ky * w;
                            for (int kx = xMin; kx <= xMax; kx++)
                            {
                                buf[count++] = toDouble(src[rowOff + kx]);
                            }
                        }

                        dst[py * w + px] = fromDouble(kernel.Apply(buf.AsSpan(), count));
                    }
                }

                dstArrays[frame] = dst;
            }

            if (useParallel && frameCount >= 2)
            {
                int completed = 0;
                var options = new ParallelOptions { CancellationToken = cancellationToken };
                Parallel.For(0, frameCount, options, frame =>
                {
                    ProcessFrame(frame);
                    progress?.Report(Interlocked.Increment(ref completed) - 1);
                });
            }
            else
            {
                for (int f = 0; f < frameCount; f++)
                {
                    ProcessFrame(f);
                    progress?.Report(f);
                }
            }

            // Build result with same geometry
            var result = new MatrixData<T>(w, h, new List<T[]>(dstArrays));
            result.CopyPropertiesFrom(source);

            return result;
        }
    }
}
