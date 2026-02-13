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
    /// 整数型および浮動小数点数型の配列から、
    /// SIMDと並列化を駆使して最小値・最大値を検索する
    /// </summary>
    public static class FastMinMaxFinder
    {
        // -----------------------------------------------------------------
        // 1. 整数型用のメソッド
        // (int, long, short など)
        // -----------------------------------------------------------------
        public static (T Min, T Max) FindInteger<T>(ReadOnlySpan<T> data)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            // 初期値に T.MaxValue / T.MinValue を使用
            return FindMinMaxInternal(data, T.MaxValue, T.MinValue);
        }

        // -----------------------------------------------------------------
        // 2. 浮動小数点数型用のメソッド
        // (double, float など)
        // -----------------------------------------------------------------
        public static (T Min, T Max) FindFloatingPoint<T>(ReadOnlySpan<T> data)
            where T : unmanaged, INumber<T>, IFloatingPointIeee754<T>
        {
            // 初期値に T.PositiveInfinity / T.NegativeInfinity を使用
            return FindMinMaxInternal(data, T.PositiveInfinity, T.NegativeInfinity);
        }

        // -----------------------------------------------------------------
        // 3. 共通の内部実装
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
        // 4. 共通ワーカー (SIMD)
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
        // 5. 共通ワーカー (スカラ) 
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
        // 6. Complex型用のメソッド (並列化 + Sqrt削減 + ポインタキャプチャ対策)
        // -----------------------------------------------------------------
        public static (double[] Mins, double[] Maxs) FindComplex(Complex[] data)
        {
            if (data == null || data.Length == 0)
            {
                return (new double[5], new double[5]);
            }

            // 結果格納用
            double minR = double.MaxValue, maxR = double.MinValue;
            double minI = double.MaxValue, maxI = double.MinValue;
            double minP = double.MaxValue, maxP = double.MinValue;
            double minMagSq = double.MaxValue, maxMagSq = double.MinValue;
            

            object lockObj = new object();

            unsafe
            {
                fixed (Complex* pData = data)
                {
                    // 【修正点】ポインタを一度 nint (整数) に変換する
                    // これでラムダ式の中にキャプチャ可能になります
                    nint pDataAddr = (nint)pData;

                    Parallel.ForEach(
                        Partitioner.Create(0, data.Length),
                        () =>
                        {
                            // スレッドローカル変数の初期化
                            return (
                                MinR: double.MaxValue, MaxR: double.MinValue,
                                MinI: double.MaxValue, MaxI: double.MinValue,
                                MinP: double.MaxValue, MaxP: double.MinValue,
                                MinMagSq: double.MaxValue, MaxMagSq: double.MinValue
                            );
                        },
                        (range, state, local) =>
                        {
                            // 【修正点】整数からポインタに戻す
                            Complex* ptr = (Complex*)pDataAddr;

                            for (int i = range.Item1; i < range.Item2; i++)
                            {
                                // 構造体の実部・虚部へ直接アクセス
                                double r = ptr[i].Real;
                                double im = ptr[i].Imaginary;

                                // 1. Real
                                if (r < local.MinR) local.MinR = r;
                                if (r > local.MaxR) local.MaxR = r;

                                // 2. Imaginary
                                if (im < local.MinI) local.MinI = im;
                                if (im > local.MaxI) local.MaxI = im;

                                // 3. Magnitude Squared (Sqrt回避)
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

            // 最後にSqrt計算
            // 配列要素が存在する場合、MinMagSqの初期値(double.MaxValue)のままになることはないため
            // 単純にSqrtして安全です。
            return (
                new[] { Math.Sqrt(minMagSq), minR, minI,  minP, minMagSq },
                new[] { Math.Sqrt(maxMagSq), maxR, maxI,  maxP, maxMagSq }
            );
        }

    }
}
