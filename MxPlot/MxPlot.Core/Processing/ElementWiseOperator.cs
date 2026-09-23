using MxPlot.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Provides element-wise operators that transform pixel values without changing
    /// the matrix dimensions or frame structure.
    /// </summary>
    public static class ElementWiseOperator
    {


        /// <summary>
        /// Normalizes pixel values so that the maximum maps to <paramref name="target"/>.
        /// The minimum is preserved proportionally — origin stays at 0 (i.e., value 0 maps to 0).
        /// <para>
        /// All frames are processed according to <paramref name="scope"/>. To normalize one frame, slice it
        /// out first (<c>SliceAt</c>).
        /// </para>
        /// </summary>
        /// <remarks>
        /// Conversion is performed via internal methods. Integer types are rounded (not truncated to 0).
        /// For example, normalizing a <c>ushort</c> dataset to 100 yields values in 0–100.
        /// </remarks>
        public static MatrixData<T> Normalize<T>(
            this MatrixData<T> src,
            double target,
            NormalizeScope scope,
            double precomputedGlobalMax = double.NaN,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
            where T : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (target <= 0) throw new ArgumentOutOfRangeException(nameof(target), "Target must be positive.");

            int frameCount = src.FrameCount;
            progress?.Report(-frameCount);

            double globalMax = double.NaN;
            if (scope == NormalizeScope.Global)
                globalMax = double.IsNaN(precomputedGlobalMax) ? ScanMax(src, ct) : precomputedGlobalMax;

            int width = src.XCount;
            int height = src.YCount;
            var resultArrays = new T[frameCount][];
            var minList = new double[frameCount];
            var maxList = new double[frameCount];
            int completed = 0;

            Parallel.For(0, frameCount, new ParallelOptions { CancellationToken = ct }, i =>
            {
                ct.ThrowIfCancellationRequested();

                var srcSpan = src.AsSpan(i);
                var dstArr = new T[srcSpan.Length];

                double frameMax = scope == NormalizeScope.Global
                    ? globalMax
                    : src.GetValueRange(i).Max;

                double scale = (double.IsNaN(frameMax) || frameMax == 0.0)
                    ? 0.0
                    : target / frameMax;

                double dstMin = double.MaxValue, dstMax = double.MinValue;
                for (int k = 0; k < srcSpan.Length; k++)
                {
                    
                    double v = NumericConverter.ToDouble(srcSpan[k]) * scale;
                    var tv = NumericConverter.FromDouble<T>(v);
                    dstArr[k] = tv;
                    // Use NumericConverter.ToDouble(tv) rather than v so that dstMin/dstMax reflect
                    // the actual stored value after rounding and clamping.
                    double dv = NumericConverter.ToDouble(tv);
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
            result.CopyPropertiesFrom(src, copyScale: true, copyDimensions: true);
            return result;
        }

        // =========================================================================================
        // Log Transform
        // =========================================================================================

        private const double LogEpsilon = 1e-10;

        /// <summary>
        /// Applies a per-element logarithm transform to all frames,
        /// always returning a <see cref="MatrixData{T}"/> of type <c>double</c>.
        /// To transform one frame, slice it out first (<c>SliceAt</c>).
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
            IProgress<int>? progress = null,
            CancellationToken ct = default)
            where T : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));

            int frameCount = src.FrameCount;
            progress?.Report(-frameCount);

            Func<double, double> logFunc = logBase switch
            {
                LogBase.Log10 => Math.Log10,
                LogBase.Log2 => v => Math.Log(v, 2),
                _ => Math.Log,
            };

            int width = src.XCount;
            int height = src.YCount;
            var resultArrays = new double[frameCount][];
            var minList = new double[frameCount];
            var maxList = new double[frameCount];
            int completed = 0;

            Parallel.For(0, frameCount, new ParallelOptions { CancellationToken = ct }, i =>
            {
                //Check for cancellation at the start of each frame processing
                ct.ThrowIfCancellationRequested();

                var srcSpan = src.AsSpan(i);
                var dstArr = new double[srcSpan.Length];

                double shift = 0.0;
                if (handling == NegativeHandling.Shift)
                {
                    var (frameMin, _) = src.GetValueRange(i);
                    shift = frameMin <= 0 ? Math.Abs(frameMin) + LogEpsilon : 0.0;
                }

                double dstMin = double.MaxValue, dstMax = double.MinValue;
                for (int k = 0; k < srcSpan.Length; k++)
                {
                    
                    double v = NumericConverter.ToDouble(srcSpan[k]);
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
            result.CopyPropertiesFrom(src, copyScale: true, copyDimensions: true);
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
