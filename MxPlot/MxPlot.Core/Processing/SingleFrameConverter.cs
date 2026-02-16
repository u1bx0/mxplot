using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MxPlot.Core.Processing
{
    public static class SingleFrameConverter
    {
        public static readonly int ParallelThreshold = 128 * 128;//512*512;//4096;

        public static MatrixData<double> ToMatrixDataDouble<T>(this MatrixData<T> src,
         Func<T, double> converter,
         int frameIndex = -1,
         MatrixData<double>? dst = null)
         where T : unmanaged
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            long totalLength = (long)xnum * ynum;

            if (dst == null)
            {
                dst = new MatrixData<double>(xnum, ynum);
                var s = src.GetScale();
                dst.SetXYScale(s.XMin, s.XMax, s.YMin, s.YMax);
            }
            else if (dst.XCount != xnum || dst.YCount != ynum)
            {
                throw new ArgumentException("Size mismatch.");
            }

            // 配列そのものを取得
            T[] srcArray = src.GetArray(frameIndex);
            double[] dstArray = dst.GetArray();

            // 行ごとの処理ロジック
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;

                // ★ここが Plan B の活用ポイント！★
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
            if (Environment.ProcessorCount > 1 && totalLength >= ParallelThreshold)
                Parallel.For(0, ynum, ProcessRow);
            else
                for (int y = 0; y < ynum; y++) 
                    ProcessRow(y);

            dst.RefreshValueRange();
            return dst;
        }

        public static MatrixData<double> ToMatrixDataDouble(
            this MatrixData<Complex> src,
            ComplexValueMode mode,
            bool applyLog10 = false,
            int frameIndex = -1,
            MatrixData<double>? dst = null)
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            long totalLength = (long)xnum * ynum;
            var scale = src.GetScale();

            // 1. 出力先の準備
            if (dst == null)
            {
                dst = new MatrixData<double>(xnum, ynum);
                dst.SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            }
            else
            {
                if (dst.XCount != xnum || dst.YCount != ynum)
                    throw new ArgumentException($"Size mismatch. src:{xnum}x{ynum} dst:{dst.XCount}x{dst.YCount}");
            }

            if (!dst.GetScale().Equals(scale))
            {
                dst.SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            }

            // 配列そのものを取得
            Complex[] srcArray = src.GetArray(frameIndex);
            double[] dstArray = dst.GetArray();

            // 2. 行ごとの処理（高速化版）
            void ProcessRow(int iy)
            {
                // 行の先頭オフセットを計算
                int offset = iy * xnum;

                // ★高速化ポイント1: 配列の「参照(ref)」を直接取得
                // これにより通常の srcArray[i] で発生する境界チェックをバイパスします
                ref Complex srcRefBase = ref MemoryMarshal.GetArrayDataReference(srcArray);
                ref double dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);

                // ★高速化ポイント2: 行の開始位置まで参照を進める
                // Unsafe.Add はポインタ演算と同様、境界チェックなしでアドレスをずらします
                ref Complex pSrc = ref Unsafe.Add(ref srcRefBase, offset);
                ref double pDst = ref Unsafe.Add(ref dstRefBase, offset);

                // ループ: インデックスを使わず、参照をインクリメントしていく
                for (int ix = 0; ix < xnum; ix++)
                {
                    // pSrc は現在の Complex 値への参照
                    double val;

                    // 必要な成分のみ計算
                    switch (mode)
                    {
                        case ComplexValueMode.Magnitude:
                            val = pSrc.Magnitude;
                            break;
                        case ComplexValueMode.Real:
                            val = pSrc.Real;
                            break;
                        case ComplexValueMode.Imaginary:
                            val = pSrc.Imaginary;
                            break;
                        case ComplexValueMode.Phase:
                            val = pSrc.Phase;
                            break;
                        case ComplexValueMode.Power:
                            // MagnitudeプロパティはSqrt呼ぶので、自前で計算したほうが速い
                            double r = pSrc.Real;
                            double i = pSrc.Imaginary;
                            val = r * r + i * i;
                            break;
                        default:
                            val = 0.0;
                            break;
                    }

                    if (applyLog10)
                    {
                        double eps = 1e-12; //ゼロ回避
                        val = Math.Log10(Math.Max(val, eps));
                    }

                    // 結果を書き込む (dstArray[offset + ix] = val と同義だが高速)
                    pDst = val;

                    // ★高速化ポイント3: 参照を1つ進める (ポインタのインクリメント)
                    // 次のループのためにアドレスを隣へ移動。掛け算が発生しない。
                    pSrc = ref Unsafe.Add(ref pSrc, 1);
                    pDst = ref Unsafe.Add(ref pDst, 1);
                }
            }

            // 3. 並列化実行
            if (Environment.ProcessorCount > 1 && totalLength >= ParallelThreshold)
            {
                Parallel.For(0, ynum, ProcessRow);
            }
            else
            {
                for (int y = 0; y < ynum; y++) ProcessRow(y);
            }

            // 4. 値の範囲更新
            dst.RefreshValueRange();
            return dst;
        }

        /// <summary>
        /// 【Complex特化版】unsafeポインタで高速化 (switchは維持)
        /// </summary>
        public static unsafe MatrixData<double> ToMatrixDataDoubleUnsafe(
            this MatrixData<Complex> src,
            ComplexValueMode mode,
            bool applyLog10 = false,
            int frameIndex = -1,
            MatrixData<double>? dst = null)
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            long totalLength = (long)xnum * ynum;

            if (dst == null)
            {
                dst = new MatrixData<double>(xnum, ynum);
                var s = src.GetScale();
                dst.SetXYScale(s.XMin, s.XMax, s.YMin, s.YMax);
            }
            else if (dst.XCount != xnum || dst.YCount != ynum)
            {
                throw new ArgumentException($"Size mismatch.");
            }

            // ポインタ固定
            fixed (Complex* pSrcRoot = src.GetArray(frameIndex))
            fixed (double* pDstRoot = dst.GetArray())
            {
                IntPtr pSrc = (IntPtr)pSrcRoot;
                IntPtr pDst = (IntPtr)pDstRoot;
                // ポインタ渡し用のAction
                void ProcessRow(int iy)
                {
                    // 行の先頭ポインタを計算
                    Complex* ptrSrc = (Complex*)pSrc + (iy * xnum);
                    double* ptrDst = (double*)pDst + (iy * xnum);

                    // ループ回数
                    int count = xnum;

                    // ★ここが高速化ポイント★
                    // ポインタインクリメントで回すため、境界チェックが発生しない
                    for (int i = 0; i < count; i++)
                    {
                        double val;
                        // ポインタから値を読み出し (*ptrSrc)
                        double r = ptrSrc->Real;
                        double im = ptrSrc->Imaginary;

                        switch (mode)
                        {
                            case ComplexValueMode.Magnitude:
                                val = Math.Sqrt(r * r + im * im);
                                break;
                            case ComplexValueMode.Real:
                                val = r;
                                break;
                            case ComplexValueMode.Imaginary:
                                val = im;
                                break;
                            case ComplexValueMode.Phase:
                                val = Math.Atan2(im, r);
                                break;
                            case ComplexValueMode.Power:
                                val = r * r + im * im;
                                break;
                            default:
                                val = 0.0;
                                break;
                        }

                        if (applyLog10)
                        {
                            // 0対策 (+1やMin値設定は用途によるが、単純にLog10)
                            val = Math.Log10(val);
                        }

                        // ポインタへ書き込み (*ptrDst)
                        *ptrDst = val;

                        // アドレスを進める
                        ptrSrc++;
                        ptrDst++;
                    }
                }

                if (Environment.ProcessorCount > 1 && totalLength >= ParallelThreshold)
                    Parallel.For(0, ynum, ProcessRow);
                else
                    for (int y = 0; y < ynum; y++) ProcessRow(y);
            }

            dst.RefreshValueRange();
            return dst;
        }
    }
}
