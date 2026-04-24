using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Provides element-wise operators that transform pixel values without changing
    /// the matrix dimensions or frame structure.
    /// </summary>
    public static class ElementWiseOperator
    {
        // =========================================================================================
        // Normalize
        // =========================================================================================

        /// <summary>
        /// Normalizes pixel values so that the maximum maps to <paramref name="target"/>.
        /// The minimum is preserved proportionally — origin stays at 0 (i.e., value 0 maps to 0).
        /// <para>
        /// When <paramref name="singleFrameIndex"/> is ≥ 0, only that frame is processed and
        /// a single-frame result is returned. Otherwise the operation runs on all frames according
        /// to <paramref name="scope"/>.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Conversion is performed via <see cref="Convert.ToDouble"/> and
        /// <see cref="Convert.ChangeType"/>, so integer types are rounded (not truncated to 0).
        /// For example, normalizing a <c>ushort</c> dataset to 100 yields values in 0–100.
        /// </remarks>
        public static MatrixData<T> Normalize<T>(
            this MatrixData<T> src,
            double target,
            NormalizeScope scope,
            int singleFrameIndex = -1,
            double precomputedGlobalMax = double.NaN,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
            where T : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (target <= 0) throw new ArgumentOutOfRangeException(nameof(target), "Target must be positive.");

            bool singleFrame = singleFrameIndex >= 0;
            int frameCount = singleFrame ? 1 : src.FrameCount;
            progress?.Report(-frameCount);

            double globalMax = double.NaN;
            if (!singleFrame && scope == NormalizeScope.Global)
                globalMax = double.IsNaN(precomputedGlobalMax) ? ScanMax(src, ct) : precomputedGlobalMax;

            int[] frameIndices = singleFrame
                ? [singleFrameIndex]
                : Enumerable.Range(0, src.FrameCount).ToArray();

            int width = src.XCount;
            int height = src.YCount;
            var resultArrays = new T[frameIndices.Length][];
            var minList = new double[frameIndices.Length];
            var maxList = new double[frameIndices.Length];
            int completed = 0;

            Parallel.For(0, frameIndices.Length, new ParallelOptions { CancellationToken = ct }, i =>
            {
                int fi = frameIndices[i];
                var srcArr = src.GetArray(fi);
                var dstArr = new T[srcArr.Length];

                double frameMax = (!singleFrame && scope == NormalizeScope.Global)
                    ? globalMax
                    : src.GetValueRange(fi).Max;

                double scale = (double.IsNaN(frameMax) || frameMax == 0.0)
                    ? 0.0
                    : target / frameMax;

                double dstMin = double.MaxValue, dstMax = double.MinValue;
                for (int k = 0; k < srcArr.Length; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    double v = Convert.ToDouble(srcArr[k]) * scale;
                    var tv = (T)Convert.ChangeType(v, typeof(T));
                    dstArr[k] = tv;
                    double dv = Convert.ToDouble(tv);
                    if (dv < dstMin) dstMin = dv;
                    if (dv > dstMax) dstMax = dv;
                }
                resultArrays[i] = dstArr;
                minList[i] = dstMin;
                maxList[i] = dstMax;
                progress?.Report(Interlocked.Increment(ref completed) - 1);
            });

            var vminList = minList.Select(v => new List<double> { v }).ToList();
            var vmaxList = maxList.Select(v => new List<double> { v }).ToList();
            var result = new MatrixData<T>(width, height, resultArrays.ToList(), vminList, vmaxList);
            result.CopyPropertiesFrom(src, copyScale: true, copyDimensions: !singleFrame);
            return result;
        }

        // =========================================================================================
        // Log Transform
        // =========================================================================================

        private const double LogEpsilon = 1e-10;

        /// <summary>
        /// Applies a per-element logarithm transform to all (or a single) frame(s),
        /// always returning a <see cref="MatrixData{T}"/> of type <c>double</c>.
        /// <para>
        /// Non-positive values are handled according to <paramref name="handling"/>:
        /// <list type="bullet">
        ///   <item><see cref="NegativeHandling.Shift"/>: adds <c>|frameMin| + ε</c> per frame.</item>
        ///   <item><see cref="NegativeHandling.Clamp"/>: clamps to ε before the log.</item>
        /// </list>
        /// </para>
        /// </summary>
        public static MatrixData<double> LogTransform<T>(
            this MatrixData<T> src,
            LogBase logBase = LogBase.Natural,
            NegativeHandling handling = NegativeHandling.Shift,
            int singleFrameIndex = -1,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
            where T : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));

            bool singleFrame = singleFrameIndex >= 0;
            int[] frameIndices = singleFrame
                ? [singleFrameIndex]
                : Enumerable.Range(0, src.FrameCount).ToArray();

            progress?.Report(-frameIndices.Length);

            Func<double, double> logFunc = logBase switch
            {
                LogBase.Log10 => Math.Log10,
                LogBase.Log2 => v => Math.Log(v, 2),
                _ => Math.Log,
            };

            int width = src.XCount;
            int height = src.YCount;
            var resultArrays = new double[frameIndices.Length][];
            var minList = new double[frameIndices.Length];
            var maxList = new double[frameIndices.Length];
            int completed = 0;

            Parallel.For(0, frameIndices.Length, new ParallelOptions { CancellationToken = ct }, i =>
            {
                int fi = frameIndices[i];
                var srcArr = src.GetArray(fi);
                var dstArr = new double[srcArr.Length];

                double shift = 0.0;
                if (handling == NegativeHandling.Shift)
                {
                    var (frameMin, _) = src.GetValueRange(fi);
                    shift = frameMin <= 0 ? Math.Abs(frameMin) + LogEpsilon : 0.0;
                }

                double dstMin = double.MaxValue, dstMax = double.MinValue;
                for (int k = 0; k < srcArr.Length; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    double v = Convert.ToDouble(srcArr[k]);
                    double lv = handling == NegativeHandling.Shift
                        ? logFunc(v + shift)
                        : logFunc(Math.Max(v, LogEpsilon));
                    dstArr[k] = lv;
                    if (lv < dstMin) dstMin = lv;
                    if (lv > dstMax) dstMax = lv;
                }

                resultArrays[i] = dstArr;
                minList[i] = dstMin;
                maxList[i] = dstMax;
                progress?.Report(Interlocked.Increment(ref completed) - 1);
            });

            var vminList = minList.Select(v => new List<double> { v }).ToList();
            var vmaxList = maxList.Select(v => new List<double> { v }).ToList();
            var result = new MatrixData<double>(width, height, resultArrays.ToList(), vminList, vmaxList);
            result.CopyPropertiesFrom(src, copyScale: true, copyDimensions: !singleFrame);
            return result;
        }

        // =========================================================================================
        // Shared helpers
        // =========================================================================================

        /// <summary>Scans all frames and returns the global maximum value.</summary>
        internal static double ScanMax<T>(MatrixData<T> src, CancellationToken ct) where T : unmanaged
        {
            double max = double.NegativeInfinity;
            for (int i = 0; i < src.FrameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (_, frameMax) = src.GetValueRange(i);
                if (frameMax > max) max = frameMax;
            }
            return max;
        }
    }
}
