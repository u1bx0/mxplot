//Evaluate the compuational time of each step and output to console if FFT_DEBUG is defined
//#define FFT_DEBUG

using MathNet.Numerics.IntegralTransforms;
using MxPlot.Core;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;


namespace MxPlot.Extensions.Fft
{

    /// <summary>
    /// Performs 2D FFT using Intel MKL via Math.NET Numerics when available.
    /// The consuming project must reference Math.NET Numerics via NuGet.
    /// Native MKL support requires a x64 process; otherwise a managed 2D FFT
    /// implementation (built on 1D FFTs) is used as a fallback.
    /// When using native MKL the following packages are typically required:
    /// - MathNet.Numerics
    /// - MathNet.Numerics.MKL.Win-x64
    /// - MathNet.Numerics.Providers.MKL
    /// For managed-only usage, MathNet.Numerics is sufficient.
    /// </summary>
    public static class Fft2DExtensionMkl
    {
        #region Setup provider and define delegates
        /// <summary>
        /// array, ynum, xnum, _fourerOption
        /// </summary>
        private static Action<Complex[], int, int> _forward2D;

        /// <summary>
        /// array, ynum, xnum, _fourerOption
        /// </summary>
        private static Action<Complex[], int, int> _inverse2D;


        private static FourierOptions _fourierOption = FourierOptions.Default; //Symmetrical scaling with common exponent

        static Fft2DExtensionMkl()
        {

            // 1. Assign MKL provider only when running on x64 and MKL loads successfully
            if (CheckNativeMklAvailability())
            {
                //if true, MKL provider is successfully loaded and set by MathNet.Numerics.Control.TryUseNativeMKL()
                _forward2D = (array, ynum, xnum) => Fourier.Forward2D(array, ynum, xnum, _fourierOption); 
                _inverse2D = (array, ynum, xnum) => Fourier.Inverse2D(array, ynum, xnum, _fourierOption);
                IsNativeMklAvailable = true;
                IsNativeMklUsing = true;
                Debug.WriteLine("[FFTExtensionMkl] MKL Native Provider Loaded.");
            }
            else
            {
                IsNativeMklAvailable = false;
                IsNativeMklUsing = false;
                // 2. If MKL is not usable (e.g. ARM64 or native DLL missing), fall back to the managed implementation
                Debug.WriteLine("[FFTExtensionMkl] Fallback to ManagedFft2D");
                UseManaged();
            }

            Debug.WriteLine("[FFTExtensionMkl] Provider: " + GetMathNetLinearAlgebraProvider());
        }

        /// <summary>
        /// Gets a value indicating whether the native Intel Math Kernel Library (MKL) is available and can be used for mathematical operations.
        /// </summary>
        public static bool IsNativeMklAvailable { get; private set; }

        /// <summary>
        /// Gets a value indicating whether the native Intel Math Kernel Library (MKL) is currently being used for
        /// mathematical operations.
        /// </summary>
        public static bool IsNativeMklUsing { get; private set;  }

        /// <summary>
        /// Check whether native MKL-based FFT2D is available (x64 + MKL provider).
        /// </summary>
        private static bool CheckNativeMklAvailability()
        {
            if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            {
                try
                {
                    if (MathNet.Numerics.Control.TryUseNativeMKL())
                        return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FFTExtensionMkl] MKL Load Failed: {ex.Message}");
                }
            }
            return false;
        }

        /// <summary>
        /// Force use of the managed 2D FFT implementation (built from 1D FFTs) regardless of MKL availability.
        /// Useful for testing or to avoid native dependencies.
        /// </summary>
        public static void UseManaged()
        {
            // MathNetをManagedモードに明示的に固定
            MathNet.Numerics.Control.UseManaged();
            _forward2D = (array, ynum, xnum) => ManagedFft2D.Forward2D(array, ynum, xnum, _fourierOption);
            _inverse2D = (array, ynum, xnum) => ManagedFft2D.Inverse2D(array, ynum, xnum, _fourierOption);
            IsNativeMklUsing = false;
            Trace.WriteLine("[FFTExtensionMkl] Switched to Managed 2D FFT provider.");
        }

        /// <summary>
        /// Attempts to enable the use of the native Intel Math Kernel Library (MKL) for optimized mathematical
        /// operations if it is available.
        /// </summary>
        /// <remarks>If the native MKL is not available or an error occurs during initialization, the
        /// method logs a message and continues using the default provider. This method should be called before
        /// performing operations that benefit from MKL acceleration to ensure optimal performance.</remarks>
        public static void UseMkl()
        {
            if (IsNativeMklAvailable)
            {
                try
                {
                    if (MathNet.Numerics.Control.TryUseNativeMKL())
                    {
                        _forward2D = (array, ynum, xnum) => Fourier.Forward2D(array, ynum, xnum, _fourierOption);
                        _inverse2D = (array, ynum, xnum) => Fourier.Inverse2D(array, ynum, xnum, _fourierOption);
                        IsNativeMklUsing = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[FFTExtensionMkl] MKL Failed: {ex.Message}");
                }
            }
            else
            {
                Trace.WriteLine("[FFTExtensionMkl] MKL is not available. Cannot switch to MKL provider.");
            }
        }
        
        public static string GetMathNetLinearAlgebraProvider()
        {
            return MathNet.Numerics.Control.NativeProviderPath;
        }

        #endregion

        #region Single FFT2D / IFFT2D with Shift 


        private static void ImportData(object src, int idx, Complex[] dst, int h, int w, bool applySwap)
        {
            if (src is MatrixData<Complex> srcComplex)
            {
                var srcArr = srcComplex.GetArray(idx);
                if (applySwap)
                    // When applySwap is true perform center->corner swapping during import
                    SwapKernel(srcArr, dst, h, w, SwapDirection.CenterToCorner);
                else
                    srcArr.AsSpan().CopyTo(dst);
            }
            else
            {
                // In other cases we cast to dynamic and call a generic import-and-swap helper.
                // In practice the caller should know the concrete T and call ImportAndSwap directly.
                dynamic dSrc = src;
                Fft2DHelpers.ImportAndSwap(dSrc, idx, dst, applySwap);
            }
        }

        /// <summary>
        /// Computes the two-dimensional Fast Fourier Transform (FFT) of the specified source matrix and returns the
        /// result as a matrix of complex numbers.
        /// </summary>
        /// <remarks>The method supports in-place and out-of-place transformations depending on whether a
        /// destination matrix is provided. The shift option can be used to control the arrangement of frequency
        /// components in the output, which is useful for certain signal processing applications.</remarks>
        /// <typeparam name="T">Specifies the type of the elements in the source matrix. The type must be unmanaged.</typeparam>
        /// <param name="src">The source matrix containing the data to be transformed.</param>
        /// <param name="option">Specifies the shift option to apply to the FFT output, determining how the frequency components are arranged
        /// in the result.</param>
        /// <param name="srcIndex">The starting index in the source matrix from which to begin the FFT operation. The default value is -1,
        /// which indicates that the entire matrix is used.</param>
        /// <param name="dst">An optional destination matrix in which to store the FFT result. If null, a new matrix is created to hold
        /// the output.</param>
        /// <param name="dstIndex">The starting index in the destination matrix at which to store the result. The default value is -1, which
        /// indicates that the result is stored starting from the beginning of the matrix.</param>
        /// <param name="skipRefreshValueRange">true to skip updating the value range metadata of the destination matrix after the operation; otherwise,
        /// false.</param>
        /// <returns>A matrix of complex numbers representing the two-dimensional FFT of the source matrix.</returns>
        public static MatrixData<Complex> Fft2D<T>(this MatrixData<T> src,
            ShiftOption option,
            int srcIndex = -1, MatrixData<Complex>? dst = null, int dstIndex = -1,
            bool skipRefreshValueRange = false)
                where T : unmanaged
        {
            return Fft2DProcWithSwap(true, src, option, srcIndex, dst, dstIndex, skipRefreshValueRange);
        }

        /// <summary>
        /// Compute inerver FFT2D. See <see cref="Fft2D"/> for details and parameter descriptions.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="src"></param>
        /// <param name="option"></param>
        /// <param name="srcIndex"></param>
        /// <param name="dst"></param>
        /// <param name="dstIndex"></param>
        /// <param name="skipRefreshValueRange"></param>
        /// <returns></returns>
        public static MatrixData<Complex> InverseFft2D<T>(this MatrixData<T> src,
            ShiftOption option,
            int srcIndex = -1, MatrixData<Complex>? dst = null, int dstIndex = -1,
            bool skipRefreshValueRange = false)
                where T : unmanaged
        {
            return Fft2DProcWithSwap(false, src, option, srcIndex, dst, dstIndex, skipRefreshValueRange);
        }


        private static MatrixData<Complex> Fft2DProcWithSwap<T>(
            bool isForward,
            MatrixData<T> src,
            ShiftOption option,
            int srcIndex = -1, MatrixData<Complex>? dst = null, int dstIndex = -1,
            bool skipRefreshValueRange = false
            )
            where T : unmanaged
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            int len = xnum * ynum;

            if (src.XRange <= 0 || src.YRange <= 0)
                throw new InvalidOperationException("XRange/YRange must be positive to define frequency step (df).");

            // 1. 周波数分解能 (1画素あたりの周波数ステップ)
            // 物理幅 w, h が正しく設定されている前提
            double dfx = 1.0 / src.XRange;
            double dfy = 1.0 / src.YRange;

            // 2. Scale (範囲) の決定
            Scale2D ftScale;

            if (option == ShiftOption.Centered || option == ShiftOption.BothCentered)
            {
                // --- Centered (Shifted) ---
                // Goal: keep frequency step df while placing DC near the center
                // Calculation:
                // Min (left): -Floor(N / 2) * df
                // Max (right): +Floor((N - 1) / 2) * df
                // Example checks:
                // N=4 (even): indices -2,-1,0,1 -> Min=-2*df, Max=+1*df -> Range=3df -> Step=1df
                // N=5 (odd):  indices -2,-1,0,1,2 -> Min=-2*df, Max=+2*df -> Range=4df -> Step=1df

                double fxmin = -Math.Floor(xnum / 2.0) * dfx;
                double fxmax = Math.Floor((xnum - 1) / 2.0) * dfx; // CeilingではなくFloorを使う
                double fymin = -Math.Floor(ynum / 2.0) * dfy;
                double fymax = Math.Floor((ynum - 1) / 2.0) * dfy;

                ftScale = new Scale2D(xnum, fxmin, fxmax, ynum, fymin, fymax);
            }
            else
            {
                // --- NoShift ---
                // Goal: frequency indices run from 0 to (N-1)*df
                // Example N=4: indices 0..3 -> Min=0, Max=3*df -> Range=3df -> Step=1df
                // Define a scale that increases from the origin using dfx/dfy for Shift=None
                ftScale = new Scale2D(xnum, 0, (xnum - 1) * dfx, ynum, 0, (ynum - 1) * dfy);
            }

            if (dst == null)
            {
                dst = new MatrixData<Complex>(src.XCount, src.YCount);
            }
            else if ((dst.XCount != src.XCount) || (dst.YCount != src.YCount))
            {
                throw new Exception($"Unmatched array length, src[{src.XCount}x{src.YCount}],dst[{dst.XCount}x{dst.YCount}]");
            }

            if (!dst.GetScale().Equals(ftScale))
            {
                dst.SetXYScale(ftScale.XMin, ftScale.XMax, ftScale.YMin, ftScale.YMax);
            }
                        
            bool isInverse = !isForward;
            Complex[] primary = dst.GetArray(dstIndex); //output
            
            // 2. Secondary temporary buffer
            //    If swapping is required (any option other than None) rent a working buffer
            Complex[]? secondary = null;

            // Is pre-swap processing required?
            // - BothCentered: always required (spatial center -> corner)
            // - IFFT & Centered: required (spectral center -> corner)
            bool needsPreSwap = (option == ShiftOption.BothCentered) ||
                                (isInverse && option == ShiftOption.Centered);

            // 後処理が必要か？
            // Is post-swap processing required?
            // - BothCentered: always required (corner -> spatial center)
            // - FFT & Centered: required (corner -> spectral center)
            bool needsPostSwap = (option == ShiftOption.BothCentered) ||
                                 (!isInverse && option == ShiftOption.Centered);

            // 作業バッファが必要なのは、どちらか片方でもSwapが発生する場合
            // A working buffer is required if either pre- or post-swap will occur
            bool needsBuffer = needsPreSwap || needsPostSwap;

            if (needsBuffer)
            {
                secondary = ArrayPool<Complex>.Shared.Rent(len);
            }

            try
            {
                // ---------------------------------------------------------
                // Path 1: No shift (None) -> fastest, direct path
                // ---------------------------------------------------------
                if (!needsBuffer)
                {
                    // Input(Corner) -> Primary -> [FFT] -> Output(Corner)
                    ImportData(src, srcIndex, primary, ynum, xnum, applySwap: false);

                    if (isInverse) _inverse2D(primary, ynum, xnum);
                    else _forward2D(primary, ynum, xnum);

                }
                else
                {

                    // ---------------------------------------------------------
                    // Path 2: Shifted processing (Centered / BothCentered)
                    // ---------------------------------------------------------

                    // A. Input -> Secondary (decide whether to pre-swap)
                    // If needsPreSwap is true perform CenterToCorner swap, otherwise copy
                    ImportData(src, srcIndex, secondary!, ynum, xnum, applySwap: needsPreSwap);

                    // B. 計算 (常に Corner ベース)
                    if (isInverse) _inverse2D(secondary!, ynum, xnum);
                    else _forward2D(secondary!, ynum, xnum);

                    // C. Secondary -> Primary (decide whether to post-swap)
                    if (needsPostSwap)
                    {
                        // Corner -> Center
                        SwapKernel(secondary!, primary, ynum, xnum, SwapDirection.CornerToCenter);
                    }
                    else
                    {
                        // No swap required: copy back (for example, end of IFFT Centered path)
                        // Note: IFFT Centered uses In(Center) -> [Swap] -> In(Corner) -> [IFFT] -> Out(Corner)
                        // therefore the final result remains in corner order and can be copied directly
                        secondary.AsSpan(0, len).CopyTo(primary);
                    }
                }
            }
            finally
            {
                if (secondary != null)
                {
                    ArrayPool<Complex>.Shared.Return(secondary);
                }
            }


            if (!skipRefreshValueRange)
                dst.RefreshValueRange(dstIndex);

            return dst;
        }


        #endregion

        /// <summary>
        /// ((Shift))-FFT2D-(Shift)-Action-(Shift)-IFFT2D-((Shift))<br/>
        /// Processes the input complex matrix through a two-dimensional Fast Fourier Transform (FFT) pipeline, applies
        /// a specified action to the transformed data, and returns the resulting matrix. The transformation and action
        /// are performed according to the selected shift option, which determines the frequency domain alignment.
        /// </summary>
        /// <remarks>The method supports different shift options to control the alignment of the frequency
        /// domain data: None (no shift), Centered (centered only during the action), and BothCentered (centered for
        /// input, action, and output). The action is applied in the frequency domain, and the result is transformed
        /// back to the spatial domain. The method manages buffer allocation and may use array pooling for temporary
        /// storage. The value range update can be skipped for performance by setting skipRefreshValueRange to
        /// true.</remarks>
        /// <param name="src">The source matrix containing complex values to be transformed.</param>
        /// <param name="action">An action to perform on the transformed data, which receives the frequency-domain array and its associated
        /// scale information.</param>
        /// <param name="option">A value that specifies how the frequency domain data is shifted during the FFT process. Determines the
        /// alignment of the DC component and how the action is applied.</param>
        /// <param name="srcIndex">The starting index in the source matrix to process. Use -1 to process the entire matrix.</param>
        /// <param name="dst">An optional destination matrix to store the results. If null, a new matrix is created to hold the output.</param>
        /// <param name="dstIndex">The starting index in the destination matrix where results are written. Use -1 to write to the entire
        /// matrix.</param>
        /// <param name="skipRefreshValueRange">true to skip updating the value range after processing; otherwise, false.</param>
        /// <returns>A new or updated matrix containing the result of the FFT pipeline and the applied action.</returns>
        /// <exception cref="ArgumentException">Thrown when the provided destination matrix's array length does not match the expected size for the
        /// transformation.</exception>
        public static MatrixData<Complex> FftPipelineWithAction(this MatrixData<Complex> src, 
            Action<Complex[], Scale2D> action, 
            ShiftOption option, 
            int srcIndex = -1, 
            MatrixData<Complex>? dst = null, int dstIndex = -1, 
            bool skipRefreshValueRange = false)
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            int len = ynum * xnum;

            Complex[] input = src.GetArray(srcIndex);
            Complex[]? buf = dst?.GetArray(dstIndex) ?? null;

            if (buf != null && buf.Length != len)
            {
                throw new ArgumentException("Array length mismatch");
            }

            // Result buffer (Primary)
            Complex[] primary = buf ?? new Complex[len];

            // Temporary buffer (Secondary) - rent from ArrayPool if needed
            // Not required for ShiftOption.None but declared for common logic
            Complex[]? secondary = null;
            bool rentSecondary = (option != ShiftOption.None); // None以外はスワップ用に必要

            if (rentSecondary)
            {
                secondary = ArrayPool<Complex>.Shared.Rent(len);
            }

            double dfx = 1.0 / src.XRange;
            double dfy = 1.0 / src.YRange;
            Scale2D ftScale;
            if (option == ShiftOption.Centered || option == ShiftOption.BothCentered)
            {
                double fxmin = -Math.Floor(xnum / 2.0) * dfx;
                double fxmax = Math.Floor((xnum - 1) / 2.0) * dfx; // CeilingではなくFloorを使う
                double fymin = -Math.Floor(ynum / 2.0) * dfy;
                double fymax = Math.Floor((ynum - 1) / 2.0) * dfy;

                ftScale = new Scale2D(xnum, fxmin, fxmax, ynum, fymin, fymax);
            }
            else
            {
                ftScale = new Scale2D(xnum, 0, (xnum - 1) * dfx, ynum, 0, (ynum - 1) * dfy);
            }

            try
            {
                switch (option)
                {
                    case ShiftOption.None:
                        // =========================================================
                        // Mode: None (最速)
                        // Swapなし。Actionは「Corner DC」を扱う必要がある。
                        // Route: Input -> Primary -> [FFT] -> [Action] -> [IFFT] -> Return
                        // =========================================================

                        // Input -> Primary (Copy)
                        input.AsSpan().CopyTo(primary);

                        _forward2D(primary, ynum, xnum);

                        // Action (DC is at Corner [0])
                        action(primary,ftScale);

                        _inverse2D(primary, ynum, xnum);
                        break;

                    case ShiftOption.Centered:
                        // =========================================================
                        // Mode: Centered
                        // 入出力はCornerだが、Action時のみCenterに見せる。
                        // Route: Input -> Primary(FFT) -> Sec(Swap) -> [Action] -> Primary(Swap) -> IFFT
                        // =========================================================

                // 1. Input -> Primary (copy)
                        input.AsSpan().CopyTo(primary);

                        // 2. FFT (Primary: corner DC)
                        _forward2D(primary, ynum, xnum);

                        // 3. SwapR: Corner -> Center (Primary -> Secondary)
                        SwapKernel(primary, secondary!, ynum, xnum, SwapDirection.CornerToCenter);

                        // 4. Action (Secondary: Center DC)
                        action(secondary!, ftScale);

                        // 5. SwapL: Center -> Corner (Secondary -> Primary)
                        SwapKernel(secondary!, primary, ynum, xnum, SwapDirection.CenterToCorner);

                        // 6. IFFT (Primary: Corner DC)
                        _inverse2D(primary, ynum, xnum);
                        break;

                    case ShiftOption.BothCentered:
                        // =========================================================
                        // Mode: BothCentered (Strict)
                        // 入力・出力・ActionすべてがCenter基準。4回Swap。
                        // Route: Input -> Sec(Swap) -> [FFT] -> Primary(Swap) -> [Action] 
                        //        -> Sec(Swap) -> [IFFT] -> Primary(Swap)
                        // =========================================================

                        // 1. SwapL: Center -> Corner (Input -> Secondary)
                        //    Convert input (centered) to corner order for computation
                        SwapKernel(input, secondary!, ynum, xnum, SwapDirection.CenterToCorner);

                        // 2. FFT (Secondary)
                        _forward2D(secondary!, ynum, xnum);

                        // 3. SwapR: Corner -> Center (Secondary -> Primary)
                        //    Move spectrum to center order for display; result goes into Primary (buffer)
                        SwapKernel(secondary!, primary, ynum, xnum, SwapDirection.CornerToCenter);

                        // 4. Action (Primary: center DC)
                        //    The user action is applied on the primary buffer in center order (easier to inspect)
                        action(primary, ftScale);

                        // 5. SwapL: Center -> Corner (Primary -> Secondary)
                        //    Convert back to corner order for IFFT
                        SwapKernel(primary, secondary!, ynum, xnum, SwapDirection.CenterToCorner);

                        // 6. IFFT (Secondary)
                        _inverse2D(secondary!, ynum, xnum);

                        // 7. SwapR: Corner -> Center (Secondary -> Primary)
                        //    Return final result to center order for display
                        SwapKernel(secondary!, primary, ynum, xnum, SwapDirection.CornerToCenter);
                        break;
                }
            }
            finally
            {
                if (secondary != null)
                {
                    ArrayPool<Complex>.Shared.Return(secondary);
                }
            }

            var result = new MatrixData<Complex>(xnum, ynum, primary);
            result.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            return result;
        }

        // --------------------------------------------------------------------------
        // Span-based Swap Logic
        // --------------------------------------------------------------------------

        private enum SwapDirection
        {
            CenterToCorner, // ifftshift (Shift = Floor)
            CornerToCenter  // fftshift  (Shift = Ceiling)
        }

        /// <summary>
        /// Spanを使用したSafeかつ高速なSwap実装
        /// </summary>
        private static void SwapKernel(Complex[] srcArray, Complex[] dstArray, int height, int width, SwapDirection dir)
        {
            // 1. シフト量の決定
            // CenterToCorner (IFFT前/Input時): shift = N / 2  (Floor)
            // CornerToCenter (FFT後/Output時): shift = N - (N / 2) (Ceiling)

            int shiftY, shiftX;

            if (dir == SwapDirection.CenterToCorner)
            {
                shiftY = height / 2;
                shiftX = width / 2;
            }
            else
            {
                shiftY = height - (height / 2);
                shiftX = width - (width / 2);
            }

            ReadOnlySpan<Complex> srcSpan = srcArray.AsSpan();
            Span<Complex> dstSpan = dstArray.AsSpan();

            // 2. 行ごとの並列処理
            Parallel.For(0, height, y =>
            {
                // Y軸の移動先 (Circular Shift)
                int dstY = y + shiftY;
                if (dstY >= height) dstY -= height;

                // srcArray, dstArray はクラス(配列)なのでキャプチャ可能
                ReadOnlySpan<Complex> sRow = srcArray.AsSpan(y * width, width);
                Span<Complex> dRow = dstArray.AsSpan(dstY * width, width);

                // X軸の回転コピー (2分割)
                // Block A: 先頭から width-shiftX 分 -> 後ろへ
                // Block B: 後ろの shiftX 分 -> 先頭へ

                // 例: CornerToCenter (shiftX = large)
                // src: [0...M][M...W] -> dst: [M...W][0...M]
                // ここではシンプルに「shiftX = 右への回転量」として実装

                // 1. Srcの右側 (shiftX個) を Dstの左側へ
                //    Source: [width - shiftX ... width] (長さ shiftX)
                //    Dest:   [0 ... shiftX]
                //    ※注意: ここでのshiftXの定義は「右回転量」。
                //    CornerToCenter(Ceil)の場合、DC(0)を中央(Ceil)に持っていくので
                //    「右にCeil分ずらす」動作になる。これで合っている。

                int splitPoint = width - shiftX; // ここが境目

                // コピー1: 後半 -> 前半
                sRow.Slice(splitPoint, shiftX).CopyTo(dRow.Slice(0, shiftX));

                // コピー2: 前半 -> 後半
                sRow.Slice(0, splitPoint).CopyTo(dRow.Slice(shiftX, splitPoint));
            });
        }


    }

}