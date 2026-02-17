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
            if (data.Length < checked(ynum * xnum))
                throw new ArgumentException($"Array length(={data.Length}) is smaller than ynum * xnum (={ynum*xnum})");
        }

        /// <summary>
        /// 行ごとに 1D FFT（並列・スレッドローカルバッファで GC/同期を最小化）
        /// </summary>
        private static void RowFftInPlace(
            Complex[] data, int rows, int cols, FourierOptions opts, int? dop)
        {
            {
                var po = new ParallelOptions();
                if (dop.HasValue) po.MaxDegreeOfParallelism = dop.Value;

                Parallel.For(0, rows, po,
                    // 1. Thread-Localなバッファ確保 (ここは配列のまま)
                    // 1スレッドにつき1回だけ確保されるので new でもコストは低い
                    () => new Complex[cols],
                    // 2. ループ本体
                    (r, state, buffer) =>
                    {
                        // data から現在の行に該当する領域を Span で切り出す
                        // (メモリコピーではなく、ポインタ計算のみなので超高速)
                        var rowSpan = data.AsSpan(r * cols, cols);
                        // バッファも Span として扱う
                        var bufferSpan = buffer.AsSpan(); // もし buffer が cols より大きい場合は (0, cols) で切る
                        // --- Copy In (data -> buffer) ---
                        // Array.Copy より低レベルで効率的
                        rowSpan.CopyTo(bufferSpan);
                        // --- FFT ---
                        // MathNetのForwardは配列全体を処理するため、buffer(配列)を渡す
                        Fourier.Forward(buffer, opts);
                        // --- Copy Out (buffer -> data) ---
                        bufferSpan.CopyTo(rowSpan);
                        return buffer; // 次のイテレーションのためにバッファを返す
                    },

                    // 3. 終了処理
                    buffer => { /* GCに任せるので何もしない */ }
                );
            }
        }

        private static void RowIfftInPlace(
            Complex[] data, int rows, int cols, FourierOptions opts, int? dop)
        {
            var po = new ParallelOptions();
            if (dop.HasValue) po.MaxDegreeOfParallelism = dop.Value;

            Parallel.For(0, rows, po,
                () => new Complex[cols],
                (r, state, buffer) =>
                {
                    var rowSpan = data.AsSpan(r * cols, cols);
                    var bufferSpan = buffer.AsSpan();
                    rowSpan.CopyTo(bufferSpan);
                    Fourier.Inverse(buffer, opts);
                    bufferSpan.CopyTo(rowSpan);
                    return buffer;
                },
                _ => { /* GCにおまかせ */ }
            );
        }

        /// <summary>
        /// out-of-place 2D 転置（row-major）をブロッキングで実行。
        /// src[ y * xnum + x ] → dst[ x * ynum + y ]
        /// </summary>
        private static unsafe void BlockTranspose(
        Complex[] src, Complex[] dst, int ynum, int xnum, int blockH = 32, int blockW = 32)
        {
            // 1. ArrayPool対応: 長さは「必要なサイズ以上」であればOKとする
            int totalLen = ynum * xnum;
            if (src.Length < totalLen || dst.Length < totalLen)
                throw new ArgumentException("Array length is too small");

            fixed (Complex* pSrcBase = src)
            fixed (Complex* pDstBase = dst)
            {
                IntPtr pSrc = (IntPtr)pSrcBase;
                IntPtr pDst = (IntPtr)pDstBase;
                // 2. 外側ループを並列化 (y方向のブロックごと)
                // Parallel.Forを使うことで、複数のCPUコアでメモリ転送を行う
                Parallel.For(0, (ynum + blockH - 1) / blockH, by =>
                {
                    int yBase = by * blockH;
                    int yMax = Math.Min(yBase + blockH, ynum);

                    
                    for (int xBase = 0; xBase < xnum; xBase += blockW)
                    {
                        int xMax = Math.Min(xBase + blockW, xnum);

                        // --- ブロック内部の転置処理 (Bounds Checkなし) ---
                        for (int y = yBase; y < yMax; y++)
                        {
                            // ソース: 行(y)は固定、列(x)が進む -> 連続アクセス
                            Complex* pS = (Complex*)pSrc + y * xnum + xBase;

                            // 転送先: 行(x)が進む、列(y)は固定 -> ストライドアクセス (跳び飛び)
                            // dst[x, y] -> dst[x * ynum + y]
                            Complex* pD = (Complex*)pDst + xBase * ynum + y;

                            for (int x = xBase; x < xMax; x++)
                            {
                                *pD = *pS;

                                pS++;        // srcは隣へ (+1)
                                pD += ynum;  // dstは下の行へ (+Width相当)
                            }
                        }
                    }
                });
            }
        }

    }

}
