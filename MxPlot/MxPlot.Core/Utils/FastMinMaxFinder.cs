using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace MxPlot.Utilities
{
    /// <summary>
    /// Provides high-performance min/max search over arrays of numeric types,
    /// using SIMD vectorization and multi-threaded parallelism.
    /// </summary>
    public static class FastMinMaxFinder
    {
        // -----------------------------------------------------------------
        // 1. Integer types (int, long, short, etc.)
        // -----------------------------------------------------------------

        /// <summary>
        /// Finds the minimum and maximum values in <paramref name="data"/> for integer-like types.
        /// Uses <see cref="IMinMaxValue{T}.MinValue"/> and <see cref="IMinMaxValue{T}.MaxValue"/> as initial sentinels.
        /// </summary>
        /// <typeparam name="T">An unmanaged numeric type that implements <see cref="IMinMaxValue{T}"/>.</typeparam>
        /// <param name="data">The input span to search.</param>
        /// <returns>A tuple of <c>(Min, Max)</c>. Returns <c>(default, default)</c> for an empty span.</returns>
        public static (T Min, T Max) FindInteger<T>(ReadOnlySpan<T> data)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            // Use T.MaxValue / T.MinValue as initial sentinels
            return FindMinMaxInternal(data, T.MaxValue, T.MinValue);
        }

        // -----------------------------------------------------------------
        // 2. Floating-point types (double, float, etc.)
        // -----------------------------------------------------------------

        /// <summary>
        /// Finds the minimum and maximum values in <paramref name="data"/> for IEEE 754 floating-point types.
        /// Uses <c>PositiveInfinity</c> and <c>NegativeInfinity</c> as initial sentinels so that
        /// NaN values in the input do not corrupt the result.
        /// </summary>
        /// <typeparam name="T">An unmanaged IEEE 754 floating-point type (e.g. <c>float</c>, <c>double</c>).</typeparam>
        /// <param name="data">The input span to search.</param>
        /// <returns>A tuple of <c>(Min, Max)</c>. Returns <c>(default, default)</c> for an empty span.</returns>
        public static (T Min, T Max) FindFloatingPoint<T>(ReadOnlySpan<T> data)
            where T : unmanaged, INumber<T>, IFloatingPointIeee754<T>
        {
            // Use PositiveInfinity / NegativeInfinity as initial sentinels
            return FindMinMaxInternal(data, T.PositiveInfinity, T.NegativeInfinity);
        }

        // -----------------------------------------------------------------
        // 3. Shared internal implementation
        // -----------------------------------------------------------------
        private static unsafe (T Min, T Max) FindMinMaxInternal<T>(
            ReadOnlySpan<T> data, T initMin, T initMax)
            where T : unmanaged, INumber<T>
        {
            int length = data.Length;
            if (length == 0)
            {
                return (default(T), default(T));
            }

            (T GlobalMin, T GlobalMax) result = (initMin, initMax);
            object lockObj = new object();

            fixed (T* pData = data)
            {
                nint pData_addr = (nint)pData;
                (T localInitMin, T localInitMax) localInit = (initMin, initMax);

                Parallel.ForEach(
                    Partitioner.Create(0, length),
                    () => localInit,
                    (range, loopState, localResult) =>
                    {
                        if ( Vector.IsHardwareAccelerated && range.Item2 - range.Item1 > Vector<T>.Count)
                        {
                            return FindMinMaxRangeSimd(
                                (T*)pData_addr, range.Item1, range.Item2,
                                localResult.Item1, localResult.Item2);
                        }
                        else
                        {
                            return FindMinMaxRangeScalar(
                                (T*)pData_addr, range.Item1, range.Item2,
                                localResult.Item1, localResult.Item2);
                        }
                    },
                    (finalLocalResult) =>
                    {
                        lock (lockObj)
                        {
                            result.GlobalMin = T.Min(result.GlobalMin, finalLocalResult.Item1);
                            result.GlobalMax = T.Max(result.GlobalMax, finalLocalResult.Item2);
                        }
                    }
                );
            }
            return result;
        }

        // -----------------------------------------------------------------
        // 4. Shared worker — SIMD path
        // -----------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe (T Min, T Max) FindMinMaxRangeSimd<T>(
            T* pData, int from, int to, T currentMin, T currentMax)
            where T : unmanaged, INumber<T>
        {
            int i = from;
            int vectorSize = Vector<T>.Count;
            int alignedEnd = from + (to - from) / vectorSize * vectorSize;

            var localMinVec = new Vector<T>(currentMin);
            var localMaxVec = new Vector<T>(currentMax);

            for (; i < alignedEnd; i += vectorSize)
            {
                var vec = Unsafe.ReadUnaligned<Vector<T>>(&pData[i]);
                localMinVec = Vector.Min(localMinVec, vec);
                localMaxVec = Vector.Max(localMaxVec, vec);
            }

            T minHorizontal = localMinVec[0];
            T maxHorizontal = localMaxVec[0];
            for (int j = 1; j < Vector<T>.Count; j++)
            {
                minHorizontal = T.Min(minHorizontal, localMinVec[j]);
                maxHorizontal = T.Max(maxHorizontal, localMaxVec[j]);
            }

            T localMin = T.Min(currentMin, minHorizontal);
            T localMax = T.Max(currentMax, maxHorizontal);

            for (; i < to; i++)
            {
                T val = pData[i];
                localMin = T.Min(localMin, val);
                localMax = T.Max(localMax, val);
            }
            return (localMin, localMax);
        }

        // -----------------------------------------------------------------
        // 5. Shared worker — scalar path
        // -----------------------------------------------------------------
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe (T Min, T Max) FindMinMaxRangeScalar<T>(
            T* pData, int from, int to, T currentMin, T currentMax)
            where T : unmanaged, INumber<T>
        {
            T localMin = currentMin;
            T localMax = currentMax;

            for (int i = from; i < to; i++)
            {
                T val = pData[i];
                localMin = T.Min(localMin, val);
                localMax = T.Max(localMax, val);
            }
            return (localMin, localMax);
        }

        // -----------------------------------------------------------------
        // 6. Complex type — parallel, deferred Sqrt, pointer capture workaround
        // -----------------------------------------------------------------

        /// <summary>
        /// Finds the per-component min/max values of a <see cref="Complex"/> array in a single parallel pass.
        /// Magnitude computation defers the <see cref="Math.Sqrt"/> call to after the scan to improve throughput.
        /// </summary>
        /// <param name="data">The input array of complex values.</param>
        /// <returns>
        /// A tuple of two <c>double[5]</c> arrays indexed as follows:
        /// <list type="bullet">
        /// <item>[0] Magnitude (|z|)</item>
        /// <item>[1] Real part</item>
        /// <item>[2] Imaginary part</item>
        /// <item>[3] Phase (radians, via <see cref="Math.Atan2"/>)</item>
        /// <item>[4] Power (|z|²)</item>
        /// </list>
        /// Returns two zero-filled arrays when <paramref name="data"/> is <c>null</c> or empty.
        /// </returns>
        public static (double[] Mins, double[] Maxs) FindComplex(Complex[] data)
        {
            if (data == null || data.Length == 0)
            {
                return (new double[5], new double[5]);
            }

            // Per-component accumulators
            double minR = double.MaxValue, maxR = double.MinValue;
            double minI = double.MaxValue, maxI = double.MinValue;
            double minP = double.MaxValue, maxP = double.MinValue;
            double minMagSq = double.MaxValue, maxMagSq = double.MinValue;
            

            object lockObj = new object();

            unsafe
            {
                fixed (Complex* pData = data)
                {
                    // Convert the pointer to nint so it can be captured by the lambda
                    nint pDataAddr = (nint)pData;

                    Parallel.ForEach(
                        Partitioner.Create(0, data.Length),
                        () =>
                        {
                            // Thread-local initial values
                            return (
                                MinR: double.MaxValue, MaxR: double.MinValue,
                                MinI: double.MaxValue, MaxI: double.MinValue,
                                MinP: double.MaxValue, MaxP: double.MinValue,
                                MinMagSq: double.MaxValue, MaxMagSq: double.MinValue
                            );
                        },
                        (range, state, local) =>
                        {
                            // Restore pointer from the captured nint
                            Complex* ptr = (Complex*)pDataAddr;

                            for (int i = range.Item1; i < range.Item2; i++)
                            {
                                // Direct access to Real and Imaginary fields
                                double r = ptr[i].Real;
                                double im = ptr[i].Imaginary;

                                // 1. Real
                                if (r < local.MinR) local.MinR = r;
                                if (r > local.MaxR) local.MaxR = r;

                                // 2. Imaginary
                                if (im < local.MinI) local.MinI = im;
                                if (im > local.MaxI) local.MaxI = im;

                                // 3. Magnitude Squared (deferred Sqrt for performance)
                                double magSq = r * r + im * im;
                                if (magSq < local.MinMagSq) local.MinMagSq = magSq;
                                if (magSq > local.MaxMagSq) local.MaxMagSq = magSq;

                                // 4. Phase
                                double p = Math.Atan2(im, r);
                                if (p < local.MinP) local.MinP = p;
                                if (p > local.MaxP) local.MaxP = p;
                                    
                            }
                            return local;
                        },
                        (finalLocal) =>
                        {
                            lock (lockObj)
                            {
                                if (finalLocal.MinR < minR) minR = finalLocal.MinR;
                                if (finalLocal.MaxR > maxR) maxR = finalLocal.MaxR;

                                if (finalLocal.MinI < minI) minI = finalLocal.MinI;
                                if (finalLocal.MaxI > maxI) maxI = finalLocal.MaxI;

                                if (finalLocal.MinMagSq < minMagSq) minMagSq = finalLocal.MinMagSq;
                                if (finalLocal.MaxMagSq > maxMagSq) maxMagSq = finalLocal.MaxMagSq;

                                if (finalLocal.MinP < minP) minP = finalLocal.MinP;
                                if (finalLocal.MaxP > maxP) maxP = finalLocal.MaxP;
                            }
                        }
                    );
                }
            }

            // Apply Sqrt once after the full scan.
            // When data is non-empty, MinMagSq/MaxMagSq will have been updated from their
            // double.MaxValue sentinels, so Math.Sqrt is safe here.
            return (
                new[] { Math.Sqrt(minMagSq), minR, minI,  minP, minMagSq },
                new[] { Math.Sqrt(maxMagSq), maxR, maxI,  maxP, maxMagSq }
            );
        }

    }
}
