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
        /// Performs FFT2D/IFFT2D without any coordinate shifting.
        /// The DC (zero-frequency) component remains at array index [0] (corner layout) for both input and output.
        /// <para>
        /// <b>Performance:</b> Fastest — no array swap operations are performed.<br/>
        /// <b>Use case:</b> Best when transfer functions are already defined in corner layout,
        /// or when maximum throughput is the priority.
        /// </para>
        /// </summary>
        None,

        /// <summary>
        /// Applies a circular array swap (fftshift/ifftshift) to move the DC component between
        /// corner and center layout on one side of the transform.
        /// The swap is implemented via index remapping — no checkerboard multiplication is used.
        /// <para>
        /// <b>FFT:</b> Input in corner layout (DC at index [0]) →
        ///   a CornerToCenter swap is applied <i>after</i> the FFT →
        ///   Output has DC at the logical center (<c>Height/2 * Width + Width/2</c>).
        /// </para>
        /// <para>
        /// <b>IFFT:</b> Input in center layout (DC at logical center) →
        ///   a CenterToCorner swap is applied <i>before</i> the IFFT →
        ///   Output in corner layout (DC at index [0]).
        /// </para>
        /// <para>
        /// <b>FftPipelineWithAction:</b> Spatial input/output remain in corner layout;
        ///   the action callback receives the frequency-domain array in center layout.
        /// </para>
        /// <para>
        /// <b>Use case:</b> Frequency-domain visualization (centered power spectrum),
        ///   and round-trip pipelines such as FFT(Centered) → filter action → IFFT(Centered)
        ///   where corner-layout spatial data is expected.
        /// </para>
        /// </summary>
        Centered,

        /// <summary>
        /// Applies circular array swaps on <b>both</b> sides of the transform, so that
        /// the coordinate origin is treated as the array center in both spatial and frequency domains.
        /// The swap is implemented via index remapping — no checkerboard multiplication is used.
        /// <para>
        /// <b>FFT:</b> Input in center layout →
        ///   CenterToCorner swap before FFT, CornerToCenter swap after FFT →
        ///   Output in center layout.
        /// </para>
        /// <para>
        /// <b>IFFT:</b> Input in center layout →
        ///   CenterToCorner swap before IFFT, CornerToCenter swap after IFFT →
        ///   Output in center layout.
        /// </para>
        /// <para>
        /// <b>FftPipelineWithAction:</b> Spatial input/output and the action callback
        ///   all operate in center layout.
        /// </para>
        /// <para>
        /// <b>Use case:</b> Wave-optics simulations (e.g., Angular Spectrum Method) where
        ///   the physical origin must remain at the array center throughout the entire pipeline,
        ///   such as when the input field and the output propagated field are both center-origin.
        /// </para>
        /// </summary>
        BothCentered
    }

    public static class Fft2DHelpers
    {
        /// <summary>
        /// Reads a <see cref="MatrixData{T}"/> frame and writes it into a <see cref="Complex"/> array,
        /// optionally applying a CenterToCorner circular swap (ifftshift equivalent) during the copy.
        /// This performs the T-to-Complex conversion and the index remapping in a single pass.
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
