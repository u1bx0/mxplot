using System;
using System.Numerics;
using System.Threading.Tasks;
using MathNet.Numerics.IntegralTransforms;

namespace MxPlot.Extensions.Fft
{
    public static class ManagedFft2D
    {
        /// <summary>
        /// 2D FFT (Forward). Matlab 規約が既定（Forward: 無スケール / Inverse: 1/N スケール）。
        /// </summary>
        public static void Forward2D(
            Complex[] data, int ynum, int xnum,
            FourierOptions opts = FourierOptions.Matlab,
            int blockY = 32, int blockX = 32,
            int? maxDegreeOfParallelism = null)
        {
            Validate(data, ynum, xnum);

            // 1) 行方向 1D FFT（並列）
            RowFftInPlace(data, ynum, xnum, opts, maxDegreeOfParallelism);

            // 2) 転置（ブロッキング） data -> work
            var work = new Complex[data.Length];
            BlockTranspose(data, work, ynum, xnum, blockY, blockX);

            // 3) （転置後の）行方向 1D FFT ＝ 元の列方向（並列）
            RowFftInPlace(work, xnum, ynum, opts, maxDegreeOfParallelism);

            // 4) 逆転置（ブロッキング） work -> data
            BlockTranspose(work, data, xnum, ynum, blockY, blockX);
        }

        /// <summary>
        /// 2D IFFT (Inverse). Matlab 規約が既定（合成で 1/(Nx·Ny) スケール）。
        /// </summary>
        public static void Inverse2D(
            Complex[] data, int ynum, int xnum,
            FourierOptions opts = FourierOptions.Matlab,
            int blockY = 32, int blockX = 32,
            int? maxDegreeOfParallelism = null)
        {
            Validate(data, ynum, xnum);

            // 1) 行方向 1D IFFT（並列）
            RowIfftInPlace(data, ynum, xnum, opts, maxDegreeOfParallelism);

            // 2) 転置（ブロッキング） data -> work
            var work = new Complex[data.Length];
            BlockTranspose(data, work, ynum, xnum, blockY, blockX);

            // 3) （転置後の）行方向 1D IFFT（並列）
            RowIfftInPlace(work, xnum, ynum, opts, maxDegreeOfParallelism);

            // 4) 逆転置（ブロッキング） work -> data
            BlockTranspose(work, data, xnum, ynum, blockY, blockX);
        }

        // ----------------- helpers -----------------

        private static void Validate(Complex[] data, int ynum, int xnum)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            if (ynum <= 0 || xnum <= 0) throw new ArgumentOutOfRangeException();
            if (data.Length != checked(ynum * xnum))
                throw new ArgumentException("Length mismatch: data.Length != ynum * xnum");
        }

        /// <summary>
        /// 行ごとに 1D FFT（並列・スレッドローカルバッファで GC/同期を最小化）
        /// </summary>
        private static void RowFftInPlace(
            Complex[] data, int rows, int cols, FourierOptions opts, int? dop)
        {
            var po = new ParallelOptions();
            if (dop.HasValue) po.MaxDegreeOfParallelism = dop.Value;

            Parallel.For(0, rows, po,
                () => new Complex[cols], // thread-local 行バッファ
                (r, state, row) =>
                {
                    int baseIdx = r * cols;
                    Array.Copy(data, baseIdx, row, 0, cols);
                    Fourier.Forward(row, opts);            // 1D FFT
                    Array.Copy(row, 0, data, baseIdx, cols);
                    return row;
                },
                row => { /* return バッファ破棄 */ });
        }

        private static void RowIfftInPlace(
            Complex[] data, int rows, int cols, FourierOptions opts, int? dop)
        {
            var po = new ParallelOptions();
            if (dop.HasValue) po.MaxDegreeOfParallelism = dop.Value;

            Parallel.For(0, rows, po,
                () => new Complex[cols], // thread-local 行バッファ
                (r, state, row) =>
                {
                    int baseIdx = r * cols;
                    Array.Copy(data, baseIdx, row, 0, cols);
                    Fourier.Inverse(row, opts);            // 1D IFFT
                    Array.Copy(row, 0, data, baseIdx, cols);
                    return row;
                },
                row => { /* return バッファ破棄 */ });
        }

        /// <summary>
        /// out-of-place 2D 転置（row-major）をブロッキングで実行。
        /// src[ y * xnum + x ] → dst[ x * ynum + y ]
        /// </summary>
        private static void BlockTranspose(
            Complex[] src, Complex[] dst, int ynum, int xnum, int blockY, int blockX)
        {
            if (dst.Length != src.Length) throw new ArgumentException("dst length mismatch");

            for (int yBase = 0; yBase < ynum; yBase += blockY)
            {
                int yMax = Math.Min(yBase + blockY, ynum);
                for (int xBase = 0; xBase < xnum; xBase += blockX)
                {
                    int xMax = Math.Min(xBase + blockX, xnum);

                    // タイル (yBase..yMax-1, xBase..xMax-1)
                    for (int y = yBase; y < yMax; y++)
                    {
                        int srcRow = y * xnum;
                        for (int x = xBase; x < xMax; x++)
                        {
                            dst[x * ynum + y] = src[srcRow + x];
                        }
                    }
                }
            }
        }
    }

}
