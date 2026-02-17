using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MxPlot.Extensions.Fft
{
    public enum ShiftOption
    {
        /// <summary>
        /// Performs a standard FFT2D/IFFT2D without any coordinate shifting.
        /// The DC (zero-frequency) component is located at index [0] (corners of the array).
        /// <para>
        /// <b>Performance:</b> Fastest (no extra multiplication).<br/>
        /// <b>Use Case:</b> Best when using pre-shifted transfer functions (where DC is at corners) 
        /// or when raw performance is prioritized.
        /// </para>
        /// </summary>
        None,

        /// <summary>
        /// Shifts the component layout by applying a checkerboard phase modulation <b>before</b> the transform.
        /// <para>
        /// <b>FFT:</b> Input origin at index 0 -> Output DC at <b>Logical Center</b> (index: <c>Height/2 * Width + Width/2</c>).<br/>
        /// (Output contains a phase tilt).<br/>
        /// <b>IFFT:</b> Input DC at Logical Center -> Output origin at index 0. (Assumes centered input).
        /// </para>
        /// <para>
        /// <b>Use Case:</b> Ideal for visualization (magnitude spectrum), or for symmetric round-trips 
        /// (FFT(Centered) -> Process -> IFFT(Centered)) where the phase tilt cancels out.
        /// </para>
        /// </summary>
        Centered,

        /// <summary>
        /// Performs a physically rigorous centered transform by applying phase modulation <b>both before and after</b> the transform.
        /// This ensures the coordinate origin (0,0) is treated as the array center in both spatial and frequency domains.
        /// <para>
        /// <b>FFT/IFFT:</b> Input Center -> Output Center (index: <c>Height/2 * Width + Width/2</c>).<br/>
        /// (Removes the residual phase tilt).
        /// </para>
        /// <para>
        /// <b>Use Case:</b> Essential for wave-optics simulations (e.g., Angular Spectrum Method) 
        /// where the exact phase distribution relative to the physical center is critical.
        /// </para>
        /// </summary>
        BothCentered
    }

    public static class Fft2DHelpers
    {
        /// <summary>
        /// 任意の型のMatrixDataを読み込み、Swap(CenterToCorner)を適用しながらComplex配列に格納します。
        /// これにより "T -> Complex変換" と "fftshift" を1パスで行います。
        /// </summary>
        public static unsafe void ImportAndSwap<T>(
            MatrixData<T> src,
            int frameIndex,
            Complex[] dstArray,
            bool applySwap)
            where T : unmanaged
        {
            int width = src.XCount;
            int height = src.YCount;
            var srcData = src.GetArray(frameIndex); // T[]

            fixed (T* pSrc = srcData)
            fixed (Complex* pDst = dstArray)
            {
                if (applySwap)
                {
                    // BothCentered / Centered の入力時: Center -> Corner (ifftshift相当)
                    // Shift量 = Floor(N/2)
                    int shiftY = height / 2;
                    int shiftX = width / 2;

                    ExecuteImportSwap<T>(pSrc, pDst, width, height, shiftX, shiftY);
                }
                else
                {
                    // None の場合: 単純な並列変換コピー
                    ExecuteImportStraight<T>(pSrc, pDst, width, height);
                }
            }
        }

        // ------------------------------------------------------------------------
        // Core Kernels
        // ------------------------------------------------------------------------

        private static unsafe void ExecuteImportSwap<T>(
            T* pSrc, Complex* pDst, int width, int height, int shiftX, int shiftY)
            where T : unmanaged
        {
            // T -> Complex なので MemoryCopy は使えない。
            // しかし、インデックス計算をループ外に出し、2分割ループにすることで分岐を排除する。

            Parallel.For(0, height, y =>
            {
                // Y軸のシフト先
                int dstY = y + shiftY;
                if (dstY >= height) dstY -= height;

                T* srcRow = pSrc + (y * width);
                Complex* dstRow = pDst + (dstY * width);

                // X軸の分割点 (srcのどこで折り返すか)
                // CenterToCorner (Input) の場合、左側半分(0..W/2)を右へ、右側半分を左へ。
                // shiftX = width/2

                // Loop 1: Srcの右側 (shiftX .. width) -> Dstの左側 (0 .. width-shiftX)
                // ※ src[k] を dst[k - shiftX] に書くイメージだが、
                //    SwapCenterToCorner (LeftShift) の定義に従い実装する。

                // 定義: SwapCenterToCorner (ifftshift)
                // [0...M][M...W] -> [M...W][0...M]  (M = width/2)
                // 入力の後半(M..W) が 出力の前半(0..) に来る

                int M = shiftX;      // Split Point
                int len1 = width - M; // Length of second half

                // Block 1: Src[M ... W] -> Dst[0 ... len1]
                CopyLoop<T>(srcRow + M, dstRow, len1);

                // Block 2: Src[0 ... M] -> Dst[len1 ... W]
                CopyLoop<T>(srcRow, dstRow + len1, M);
            });
        }

        private static unsafe void ExecuteImportStraight<T>(
            T* pSrc, Complex* pDst, int width, int height)
            where T : unmanaged
        {
            Parallel.For(0, height, y =>
            {
                T* srcRow = pSrc + (y * width);
                Complex* dstRow = pDst + (y * width);
                CopyLoop<T>(srcRow, dstRow, width);
            });
        }

        // JITによる定数畳み込みを期待した変換ループ
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void CopyLoop<T>(T* s, Complex* d, int count)
            where T : unmanaged
        {
            // ポインタをインクリメントしながら回す
            T* end = s + count;
            while (s < end)
            {
                double val;

                // typeof(T) チェックはJITですべて消え、該当する行だけが残る
                if (typeof(T) == typeof(double)) val = *(double*)s;
                else if (typeof(T) == typeof(float)) val = *(float*)s;
                else if (typeof(T) == typeof(byte)) val = *(byte*)s;
                else if (typeof(T) == typeof(sbyte)) val = *(sbyte*)s;
                else if (typeof(T) == typeof(ushort)) val = *(ushort*)s;
                else if (typeof(T) == typeof(short)) val = *(short*)s;
                else if (typeof(T) == typeof(uint)) val = *(uint*)s;
                else if (typeof(T) == typeof(int)) val = *(int*)s;
                else if (typeof(T) == typeof(ulong)) val = *(ulong*)s;
                else if (typeof(T) == typeof(long)) val = *(long*)s;
                else val = 0; // fallback

                // Realに代入、Imagは0
                // Complex構造体レイアウト: [double Real][double Imag]
                double* dDbl = (double*)d;
                dDbl[0] = val;
                dDbl[1] = 0.0;

                s++;
                d++;
            }
        }
    }

}
