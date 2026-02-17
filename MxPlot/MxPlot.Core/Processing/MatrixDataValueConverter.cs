using System;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MxPlot.Core.Processing
{

    /// <summary>
    /// Provides static methods for converting matrix data of various types to double-precision values, supporting both
    /// single-frame and multi-frame operations.
    /// </summary>
    /// <remarks>This class includes conversion methods optimized for performance, utilizing parallel
    /// processing and unsafe code where appropriate. It supports conversion from unmanaged types and complex numbers,
    /// with options for applying specific conversion modes and logarithmic scaling. Callers must ensure that
    /// destination matrices are correctly sized to avoid exceptions. The conversion methods are designed for use with
    /// large matrices and may leverage parallelism based on matrix size and processor count.</remarks>
    public static class MatrixDataValueConverter
    {
        public static readonly int ParallelThreshold = 256 * 256;//512*512;//4096;

        public static MatrixData<double> ToDoubleAllFrames<T> (this MatrixData<T> src,
            Func<T, double> converter,
            MatrixData<double>? dst = null)
            where T : unmanaged
        {
            var (ret, xnum, ynum) = PrepareDstForToDouble(src, dst, src.FrameCount);
            dst = ret;

            Parallel.For(0, src.FrameCount, frameIndex =>
            {
                T[] srcArray = src.GetArray(frameIndex);
                double[] dstArray = dst.GetArray(frameIndex);
                ToDoubleProc(srcArray, dstArray, converter, xnum, ynum, useParallel: false);
                dst.RefreshValueRange(frameIndex);
            });
            return dst;
        }

        public static MatrixData<double> ToDouble<T>(this MatrixData<T> src,
         Func<T, double> converter,
         int frameIndex = -1, //-1 = ActiveIndex
         MatrixData<double>? dst = null)
         where T : unmanaged
        {
           var (dstRet, xnum, ynum) = PrepareDstForToDouble(src, dst, 1);
            dst = dstRet;

            T[] srcArray = src.GetArray(frameIndex);
            double[] dstArray = dst.GetArray(); //dst.FrameCount == 1

            ToDoubleProc(srcArray, dstArray, converter, xnum, ynum, useParallel: true);

            dst.RefreshValueRange();
            return dst;
        }

        private static (MatrixData<Double> dst, int xnum, int ynum) 
            PrepareDstForToDouble<T>(MatrixData<T> src, MatrixData<double>? dst, int frameCount)
            where T : unmanaged
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            var scale = src.GetScale();
            if (dst == null)
            {
                dst = new MatrixData<double>(xnum, ynum, frameCount);
            }
            else if (dst.XCount != xnum || dst.YCount != ynum || dst.FrameCount != frameCount)
            {
                throw new ArgumentException("Size mismatch.");
            }

            if (!dst.GetScale().Equals(scale)) //スケールが異なる場合は合わせる
            {
                dst.SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            }
            return (dst, xnum, ynum);
        } 

        private static void ToDoubleProc<T>(T[] srcArray, double[] dstArray, 
            Func<T, double> converter, int xnum, int ynum, bool useParallel) 
            where T: unmanaged
        {
            int totalLength = xnum * ynum;

            // 行ごとの処理ロジック
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;
                // 配列の境界チェックを回避するため、参照(ref)を取得して演算する
                // 1. 配列の先頭への参照を取得 (GetArrayDataReferenceは最速)
                ref T srcRefBase = ref MemoryMarshal.GetArrayDataReference(srcArray);
                ref double dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);

                // 2. 行の開始位置まで参照を進める (Unsafe.Add)
                // ポインタ演算: ptr + offset と同じ
                ref T currentSrc = ref Unsafe.Add(ref srcRefBase, offset);
                ref double currentDst = ref Unsafe.Add(ref dstRefBase, offset);

                // 3. ループ (境界チェックなし)
                for (int ix = 0; ix < xnum; ix++)
                {
                    // converter には 値 を渡す (Tが構造体ならコピーコストは微小)
                    // currentDst = ... はポインタへの書き込みと同等
                    currentDst = converter(currentSrc);

                    // 参照を1つ進める (ptr++)
                    currentSrc = ref Unsafe.Add(ref currentSrc, 1);
                    currentDst = ref Unsafe.Add(ref currentDst, 1);
                }
            }

            // 並列実行判定
            if (useParallel && Environment.ProcessorCount > 1 && totalLength >= ParallelThreshold)
                Parallel.For(0, ynum, ProcessRow);
            else
                for (int y = 0; y < ynum; y++)
                    ProcessRow(y);
        }

        public static MatrixData<double> ToDouble(
            this MatrixData<Complex> src,
            ComplexValueMode mode,
            bool applyLog10 = false,
            int frameIndex = -1,
            MatrixData<double>? dst = null)
        {
            var (ret, xnum, ynum) = PrepareDstForToDouble<Complex>(src, dst, 1);
            dst = ret;

            // 配列そのものを取得
            Complex[] srcArray = src.GetArray(frameIndex);
            double[] dstArray = dst.GetArray();
             
            //実行
            ToDoubleFromComplexProc(srcArray, dstArray, mode, applyLog10, xnum, ynum, useParallel: true);

            // 4. 値の範囲更新
            dst.RefreshValueRange();
            return dst;
        }

        public static MatrixData<double> ToDoubleAllFrames(
            this MatrixData<Complex> src,
            ComplexValueMode mode,
            bool applyLog10 = false,
            MatrixData<double>? dst = null)
        {
            var (ret, xnum, ynum) = PrepareDstForToDouble<Complex>(src, dst, src.FrameCount);
            dst = ret;
            Parallel.For(0, src.FrameCount, frameIndex =>
            {
                Complex[] srcArray = src.GetArray(frameIndex);
                double[] dstArray = dst.GetArray(frameIndex);
                ToDoubleFromComplexProc(srcArray, dstArray, mode, applyLog10, xnum, ynum, useParallel: false);
                dst.RefreshValueRange(frameIndex);
            });
            return dst;
        }


        private static void ToDoubleFromComplexProc(Complex[] srcArray, double[] dstArray, ComplexValueMode mode, bool applyLog10, int xnum, int ynum, bool useParallel)
        {
            // 2. 行ごとの処理（高速化版）
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;

                // 1. 行の「先頭」への参照を取得
                ref Complex srcRefBase = ref MemoryMarshal.GetArrayDataReference(srcArray);
                ref double dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);

                // 行の開始位置（ベースアドレス）を固定します
                ref Complex rowSrcBase = ref Unsafe.Add(ref srcRefBase, offset);
                ref double rowDstBase = ref Unsafe.Add(ref dstRefBase, offset);

                // 2. ループ：ixを使って「ベース + ix」でアクセス
                for (int ix = 0; ix < xnum; ix++)
                {
                    // ★変更点：現在のポインタを更新するのではなく、ixを使ってその都度算出
                    // JITコンパイラはこれが「配列アクセス」と同等であることを理解し、最適化しやすくなります
                    ref Complex currentSrc = ref Unsafe.Add(ref rowSrcBase, ix);

                    double val;

                    // switch (mode) はここに残ります
                    switch (mode)
                    {
                        case ComplexValueMode.Magnitude:
                            val = currentSrc.Magnitude;
                            break;
                        case ComplexValueMode.Real:
                            val = currentSrc.Real;
                            break;
                        case ComplexValueMode.Imaginary:
                            val = currentSrc.Imaginary;
                            break;
                        case ComplexValueMode.Phase:
                            val = currentSrc.Phase;
                            break;
                        case ComplexValueMode.Power:
                            double r = currentSrc.Real;
                            double i = currentSrc.Imaginary;
                            val = r * r + i * i;
                            break;
                        default:
                            val = 0.0;
                            break;
                    }

                    if (applyLog10)
                    {
                        double eps = 1e-12;
                        val = Math.Log10(Math.Max(val, eps));
                    }

                    // 結果を書き込む
                    // ★変更点：書き込み先も「ベース + ix」で指定
                    Unsafe.Add(ref rowDstBase, ix) = val;

                    // ★削除：ポインタを進める処理は不要になります
                    // pSrc = ref Unsafe.Add(ref pSrc, 1);  <-- これが不要
                }
            }

            // 3. 並列化実行
            if (useParallel && Environment.ProcessorCount > 1 && (long)xnum * ynum >= ParallelThreshold)
            {
                Parallel.For(0, ynum, ProcessRow);
            }
            else
            {
                for (int y = 0; y < ynum; y++) ProcessRow(y);
            }
        }

    }
}
