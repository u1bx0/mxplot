using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MxPlot.Core.Processing
{

    /// <summary>
    /// The projection mode for volume rendering.
    /// </summary>
    public enum ProjectionMode
    {
        Maximum, // MIP
        Minimum, // MinIP
        Average  // AIP
    }


    /// <summary>
    /// Provides the extensions for VolumeAccessor, enabling the efficient projection along the x or y ais in the stack.
    /// This will be used to produce an orthogonal view of MatrixData
    /// </summary>
    public static class VolumeAccessorExtensions
    {
        /// <summary>
        /// Inner flag to enable optimized tiling for Z projection operations.
        /// </summary>
        /// <remarks>Set this field to <see langword="true"/> to enable performance optimizations during Z
        /// projection tiling. When disabled, standard tiling behavior is used. Changing this value may affect rendering
        /// performance and output quality depending on the projection algorithm.</remarks>
        public static  bool OptimizedTilingEnabledForZProjection = true;

        
        public static MatrixData<T> CreateProjection<T>(this VolumeAccessor<T> volume, ViewFrom axis, ProjectionMode mode)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            return axis switch
            {
                ViewFrom.X => ProjectAlongX(volume, mode),
                ViewFrom.Y => ProjectAlongY(volume, mode),
                ViewFrom.Z => OptimizedTilingEnabledForZProjection ? 
                                                ProjectAlongZ_Tiled_Safe(volume, mode) : 
                                                ProjectAlongZ(volume, mode),
                                _ => throw new ArgumentOutOfRangeException(nameof(axis), "Invalid axis for projection."),
            };
        }

        public static (MatrixData<T> XZ, MatrixData<T> YZ) CreateOrthogonalProjections<T>(this VolumeAccessor<T> volume, ProjectionMode mode,
            int numThreads = -1, IMatrixData? dstXZ = null, int dstXZIndex = 0, IMatrixData? dstYZ = null, int dstYZIndex = 0)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            return ProjectAlongXandY(volume, mode, numThreads: numThreads, dstXZ: dstXZ, dstXZIndex: dstXZIndex, dstYZ: dstYZ, dstYZIndex: dstYZIndex);
        }

        // ---------------------------------------------------------
        // Implementation: X Axis (YZ Plane)
        // ---------------------------------------------------------
        private static unsafe MatrixData<T> ProjectAlongX<T>(VolumeAccessor<T> vol, ProjectionMode mode)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            int outW = vol._height;
            int outH = vol._depth;
            var result = new T[outW * outH];

            // Average用の逆数係数 (double計算用)
            double invCount = 1.0 / vol._width;

            fixed (T* resBase = result)
            {
                nint resPtrAddr = (nint)resBase;
                Parallel.For(0, vol._depth, z =>
                {
                    fixed (T* srcBase = vol._frames[z])
                    {
                        T* resPtr = (T*)resPtrAddr + z * outW;
                        for (int y = 0; y < vol._height; y++)
                        {
                            T* rowPtr = srcBase + y * vol._width;

                            // モードによって最速のループを選択
                            if (mode == ProjectionMode.Maximum)
                            {
                                T maxVal = T.MinValue;
                                for (int x = 0; x < vol._width; x++)
                                {
                                    T val = rowPtr[x];
                                    if (val > maxVal) maxVal = val;
                                }
                                resPtr[y] = maxVal;
                            }
                            else if (mode == ProjectionMode.Minimum)
                            {
                                T minVal = T.MaxValue;
                                for (int x = 0; x < vol._width; x++)
                                {
                                    T val = rowPtr[x];
                                    if (val < minVal) minVal = val;
                                }
                                resPtr[y] = minVal;
                            }
                            else // Average
                            {
                                double sum = 0;
                                for (int x = 0; x < vol._width; x++)
                                {
                                    sum += double.CreateChecked(rowPtr[x]);
                                }
                                // T型に戻す (例: intなら切り捨て)
                                resPtr[y] = T.CreateChecked(sum * invCount);
                            }
                        }
                    }
                });
            }
            var scale = vol._scale;
            var axis = vol._axis;   
            var md = new MatrixData<T>(outW, outH, result);
            md.SetXYScale(scale.YMin, scale.YMax, axis.Min, axis.Max);
            md.XUnit = scale.YUnit;
            md.YUnit = axis.Unit;
            return md;
        }

        // ---------------------------------------------------------
        // Implementation: Y Axis (XZ Plane)
        // ---------------------------------------------------------
        private static unsafe MatrixData<T> ProjectAlongY<T>(VolumeAccessor<T> vol, ProjectionMode mode)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            int outW = vol._width;
            int outH = vol._depth;
            var result = new T[outW * outH];
            double invCount = 1.0 / vol._height;

            fixed (T* resBase = result)
            {
                nint resPtrAddr = (nint)resBase;
                Parallel.For(0, vol._depth, z =>
                {
                    fixed (T* srcBase = vol._frames[z])
                    {
                        T* resPtr = (T*)resPtrAddr + z * outW;
                        for (int x = 0; x < vol._width; x++)
                        {
                            T* colPtr = srcBase + x;
                            int stride = vol._width;

                            if (mode == ProjectionMode.Maximum)
                            {
                                T maxVal = T.MinValue;
                                for (int y = 0; y < vol._height; y++)
                                {
                                    T val = *colPtr;
                                    if (val > maxVal) maxVal = val;
                                    colPtr += stride;
                                }
                                resPtr[x] = maxVal;
                            }
                            else if (mode == ProjectionMode.Minimum)
                            {
                                T minVal = T.MaxValue;
                                for (int y = 0; y < vol._height; y++)
                                {
                                    T val = *colPtr;
                                    if (val < minVal) minVal = val;
                                    colPtr += stride;
                                }
                                resPtr[x] = minVal;
                            }
                            else // Average
                            {
                                double sum = 0;
                                for (int y = 0; y < vol._height; y++)
                                {
                                    sum += double.CreateChecked(*colPtr);
                                    colPtr += stride;
                                }
                                resPtr[x] = T.CreateChecked(sum * invCount);
                            }
                        }
                    }
                });
            }
            var scale = vol._scale;
            var axis = vol._axis;
            var md = new MatrixData<T>(outW, outH, result);
            md.SetXYScale(scale.XMin, scale.XMax, axis.Min, axis.Max);
            md.XUnit = scale.XUnit;
            md.YUnit = axis.Unit;
            return md;
        }


        // ---------------------------------------------------------
        // Implementation: XZ + YZ Combined (Single Frame Pass)
        // ---------------------------------------------------------
        /// <summary>
        /// Computes XZ and YZ projections simultaneously in a single pass over Z-frames.
        /// </summary>
        /// <remarks>
        /// <b>Loop structure:</b> <c>Parallel.For(z) { for y { for x { val = frame[y*W+x] } } }</c><br/>
        /// - Reading <c>frame[y*W+x]</c>: row-major sequential access → cache-friendly.<br/>
        /// - Writing <c>xzRow[x]</c>: sequential (same stride as x-loop) → cache-friendly.<br/>
        /// - Writing <c>yzRow[y]</c>: single scalar update per y-iteration → no cache pressure.<br/>
        /// - Per-z output rows are non-overlapping across threads → lock-free.<br/>
        /// <br/>
        /// For <see cref="ProjectionMode.Average"/>, per-thread <see cref="ArrayPool{T}"/> buffers
        /// are used to accumulate double-precision sums without GC allocation.
        /// </remarks>
        private static (MatrixData<T> XZ, MatrixData<T> YZ) ProjectAlongXandY<T>
            (VolumeAccessor<T> vol, ProjectionMode mode, 
            int numThreads = -1, IMatrixData? dstXZ = null, int dstXZIndex = 0, IMatrixData? dstYZ = null, int dstYZIndex = 0)
         where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            int width = vol._width;
            int height = vol._height;
            int depth = vol._depth;

            var xzResult = (dstXZ is MatrixData<T> xz) ? xz.GetArray(dstXZIndex) : null;
            if (xzResult == null || xzResult.Length < width * depth)
                xzResult = new T[width * depth]; 

            var yzResult = (dstYZ is MatrixData<T> yz) ? yz.GetArray(dstYZIndex) : null;
            if (yzResult == null || yzResult.Length < height * depth)
                yzResult = new T[height* depth];


            void Proc(int z)
            {
                // 1. 結果配列の先頭参照を取得 (fixedによるピン留めを完全に排除)
                ref T xzResultRef = ref MemoryMarshal.GetArrayDataReference(xzResult);
                ref T yzResultRef = ref MemoryMarshal.GetArrayDataReference(yzResult);

                // IList<T[]> を想定。フレームの先頭参照を直接取得
                T[] frame = vol._frames[z];
                ref T frameRef = ref MemoryMarshal.GetArrayDataReference(frame);

                // この z に対応する XZ/YZ 行の先頭参照
                ref T xzRowRef = ref Unsafe.Add(ref xzResultRef, z * width);
                ref T yzRowRef = ref Unsafe.Add(ref yzResultRef, z * height);

                int vectorCount = Vector<T>.Count;

                if (mode == ProjectionMode.Maximum)
                {
                    // XZ行を初期化
                    MemoryMarshal.CreateSpan(ref xzRowRef, width).Fill(T.MinValue);

                    for (int y = 0; y < height; y++)
                    {
                        ref T frameRowRef = ref Unsafe.Add(ref frameRef, y * width);
                        Vector<T> vYzMax = new Vector<T>(T.MinValue);
                        T rowMax = T.MinValue;

                        int x = 0;
                        // SIMDループ: XZの比較とYZ行内の最大値を同時計算
                        if (Vector.IsHardwareAccelerated && width >= vectorCount)
                        {
                            for (; x <= width - vectorCount; x += vectorCount)
                            {
                                Vector<T> vFrame = Vector.LoadUnsafe(ref frameRowRef, (nuint)x);
                                Vector<T> vXz = Vector.LoadUnsafe(ref xzRowRef, (nuint)x);

                                // XZ更新: Max(現在のXZ, 新しいフレームの行)
                                Vector.Max(vXz, vFrame).StoreUnsafe(ref xzRowRef, (nuint)x);

                                // YZ集計: 行内の最大値をベクトル単位で保持
                                vYzMax = Vector.Max(vYzMax, vFrame);
                            }
                        }

                        // vYzMax の中からスカラーの最大値を抽出
                        for (int i = 0; i < vectorCount; i++)
                        {
                            if (vYzMax[i] > rowMax) rowMax = vYzMax[i];
                        }

                        // 端数処理
                        for (; x < width; x++)
                        {
                            T val = Unsafe.Add(ref frameRowRef, x);
                            ref T xzVal = ref Unsafe.Add(ref xzRowRef, x);
                            if (val > xzVal) xzVal = val;
                            if (val > rowMax) rowMax = val;
                        }

                        // YZ行への書き込みは1回だけで完了
                        Unsafe.Add(ref yzRowRef, y) = rowMax;
                    }
                }
                else if (mode == ProjectionMode.Minimum)
                {
                    // XZ行を初期化
                    MemoryMarshal.CreateSpan(ref xzRowRef, width).Fill(T.MaxValue);

                    for (int y = 0; y < height; y++)
                    {
                        ref T frameRowRef = ref Unsafe.Add(ref frameRef, y * width);
                        Vector<T> vYzMin = new Vector<T>(T.MaxValue);
                        T rowMin = T.MaxValue;

                        int x = 0;
                        // SIMDループ: XZの比較とYZ行内の最小値を同時計算
                        if (Vector.IsHardwareAccelerated && width >= vectorCount)
                        {
                            for (; x <= width - vectorCount; x += vectorCount)
                            {
                                Vector<T> vFrame = Vector.LoadUnsafe(ref frameRowRef, (nuint)x);
                                Vector<T> vXz = Vector.LoadUnsafe(ref xzRowRef, (nuint)x);

                                Vector.Min(vXz, vFrame).StoreUnsafe(ref xzRowRef, (nuint)x);
                                vYzMin = Vector.Min(vYzMin, vFrame);
                            }
                        }

                        for (int i = 0; i < vectorCount; i++)
                        {
                            if (vYzMin[i] < rowMin) rowMin = vYzMin[i];
                        }

                        for (; x < width; x++)
                        {
                            T val = Unsafe.Add(ref frameRowRef, x);
                            ref T xzVal = ref Unsafe.Add(ref xzRowRef, x);
                            if (val < xzVal) xzVal = val;
                            if (val < rowMin) rowMin = val;
                        }

                        Unsafe.Add(ref yzRowRef, y) = rowMin;
                    }
                }
                else // Average
                {
                    // accYZは不要。y方向に進むループ内で合計が確定するため
                    double[] accXZ = ArrayPool<double>.Shared.Rent(width);
                    ref double accXzRef = ref MemoryMarshal.GetArrayDataReference(accXZ);
                    MemoryMarshal.CreateSpan(ref accXzRef, width).Clear();

                    double invW = 1.0 / width;
                    double invH = 1.0 / height;

                    try
                    {
                        for (int y = 0; y < height; y++)
                        {
                            ref T frameRowRef = ref Unsafe.Add(ref frameRef, y * width);
                            double yzRowSum = 0;

                            // INumber<T>制約下での安全なキャストと合計
                            for (int x = 0; x < width; x++)
                            {
                                double v = double.CreateChecked(Unsafe.Add(ref frameRowRef, x));
                                Unsafe.Add(ref accXzRef, x) += v;
                                yzRowSum += v;
                            }

                            // YZ行への書き込みは1回だけで完了
                            Unsafe.Add(ref yzRowRef, y) = T.CreateChecked(yzRowSum * invW);
                        }

                        // XZ平均の確定
                        for (int x = 0; x < width; x++)
                        {
                            Unsafe.Add(ref xzRowRef, x) = T.CreateChecked(Unsafe.Add(ref accXzRef, x) * invH);
                        }
                    }
                    finally
                    {
                        ArrayPool<double>.Shared.Return(accXZ);
                    }
                }
            }

            if (numThreads > 1 || numThreads < 0)
            {
                Parallel.For(0, depth, new ParallelOptions() { MaxDegreeOfParallelism = numThreads }, iz =>
                {
                    Proc(iz);
                });
            }
            else
            {
                for (int iz = 0; iz < depth; iz++)
                {
                    Proc(iz);
                }
            }

            // 2. メタデータの生成・割り当て
            var scale = vol._scale;
            var axis = vol._axis;

            var mdXZ = new MatrixData<T>(width, depth, xzResult);
            mdXZ.SetXYScale(scale.XMin, scale.XMax, axis.Min, axis.Max);
            mdXZ.XUnit = scale.XUnit;
            mdXZ.YUnit = axis.Unit;

            var mdYZ = new MatrixData<T>(height, depth, yzResult);
            mdYZ.SetXYScale(scale.YMin, scale.YMax, axis.Min, axis.Max);
            mdYZ.XUnit = scale.YUnit;
            mdYZ.YUnit = axis.Unit;

            return (mdXZ, mdYZ);
        }


        private static unsafe MatrixData<T> ProjectAlongZ<T>(VolumeAccessor<T> vol, ProjectionMode mode)
    where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            int width = vol._width;
            int height = vol._height;
            int depth = vol._depth;
            var result = new T[width * height];

            // ★ここが最大のポイント
            // ループの中で毎回 vol._frames[z] を引くと遅いので、
            // 最初に全フレームをメモリ上に「釘付け(Pin)」にして、その生アドレス(ポインタ)を配列化します。
            var handles = new System.Runtime.InteropServices.GCHandle[depth];
            var framePtrs = new T*[depth];

            try
            {
                // 1. 全フレームをPin留めする (コストは一瞬です)
                for (int z = 0; z < depth; z++)
                {
                    handles[z] = System.Runtime.InteropServices.GCHandle.Alloc(vol._frames[z], System.Runtime.InteropServices.GCHandleType.Pinned);
                    framePtrs[z] = (T*)handles[z].AddrOfPinnedObject();
                }

                fixed (T* resBase = result)
                {
                    nint resPtrAddr = (nint)resBase;
                    // 2. Y方向(行)で並列化
                    Parallel.For(0, height, y =>
                    {
                        T* resRowPtr = (T*)resPtrAddr + y * width;
                        int rowOffset = y * width;

                        // X方向(列)
                        for (int x = 0; x < width; x++)
                        {
                            int pixelOffset = rowOffset + x;

                            // 3. Z方向(深さ) - ここが最深部ループ
                            // framePtrs[z] を使うことで、Listアクセスも境界チェックもゼロになります。
                            if (mode == ProjectionMode.Maximum)
                            {
                                T maxVal = T.MinValue;
                                for (int z = 0; z < depth; z++)
                                {
                                    T val = *(framePtrs[z] + pixelOffset);
                                    if (val > maxVal) maxVal = val;
                                }
                                resRowPtr[x] = maxVal;
                            }
                            else if (mode == ProjectionMode.Minimum)
                            {
                                T minVal = T.MaxValue;
                                for (int z = 0; z < depth; z++)
                                {
                                    T val = *(framePtrs[z] + pixelOffset);
                                    if (val < minVal) minVal = val;
                                }
                                resRowPtr[x] = minVal;
                            }
                            else // Average
                            {
                                double sum = 0;
                                for (int z = 0; z < depth; z++)
                                {
                                    sum += double.CreateChecked(*(framePtrs[z] + pixelOffset));
                                }
                                resRowPtr[x] = T.CreateChecked(sum / depth);
                            }
                        }
                    });
                }
            }
            finally
            {
                // 4. 必ず解放する
                for (int z = 0; z < depth; z++)
                {
                    if (handles[z].IsAllocated) handles[z].Free();
                }
            }

            var md = new MatrixData<T>(width, height, result);
            md.SetXYScale(vol._scale.XMin, vol._scale.XMax, vol._scale.YMin, vol._scale.YMax);
            md.XUnit = vol._scale.XUnit;
            md.YUnit = vol._scale.YUnit;
            return md;
        }


        /// <summary>
        /// Projects the volume data along the Z-axis to create a 2D XY plane image.
        /// </summary>
        /// <remarks>
        /// <b>Optimization Strategy: Tiled (Blocked) Memory Access</b>
        /// <para>
        /// This method uses a "Tiled" approach to optimize CPU cache usage (L1/L2).
        /// Instead of processing the entire image at once (which causes cache thrashing for large resolutions like 1Kx1K+),
        /// the image is divided into small blocks (e.g., 4096 pixels) that fit perfectly into the L1 cache.
        /// </para>
        /// <para>
        /// Key Features:
        /// <list type="bullet">
        /// <item><b>Cache Locality:</b> Keeps the accumulator buffer in the CPU cache during AIP/MIP calculations.</item>
        /// <item><b>Parallelism:</b> Uses <see cref="Partitioner"/> to efficiently distribute blocks across CPU cores.</item>
        /// <item><b>Pointer Safety:</b> Uses <see cref="nint"/> for pointer passing to avoid unsafe lambda captures.</item>
        /// </list>
        /// </para>
        /// <para>
        /// <b>Generated by:</b> Gemini (Google DeepMind) - Optimized for high-performance volume rendering.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The numeric type of the volume data (e.g., ushort, float).</typeparam>
        /// <param name="vol">The source volume accessor.</param>
        /// <param name="mode">The projection mode (Maximum, Minimum, Average).</param>
        /// <returns>A <see cref="MatrixData{T}"/> containing the projected 2D image.</returns>
        private static unsafe MatrixData<T> ProjectAlongZ_Tiled_Safe<T>(VolumeAccessor<T> vol, ProjectionMode mode)
    where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            int width = vol._width;
            int height = vol._height;
            int depth = vol._depth;
            int length = width * height;

            var result = new T[length];

            // GCハンドルとポインタ配列の確保
            var handles = new GCHandle[depth];
            // ポインタ配列自体も固定する必要があるため、これもGCHandleでロックするか、
            // stackalloc (深さが浅い場合) を使う手もありますが、ここは安全にnative memoryか配列で。
            // 今回は「ポインタの配列」の中身（各フレームのアドレス）を渡すので、
            // フレームポインタの配列自体をPinして、そのアドレスを渡します。
            var framePtrs = new nint[depth];

            try
            {
                // 全フレームをPin留めしてアドレスを nint 配列に格納
                for (int z = 0; z < depth; z++)
                {
                    handles[z] = GCHandle.Alloc(vol._frames[z], GCHandleType.Pinned);
                    framePtrs[z] = handles[z].AddrOfPinnedObject();
                }

                fixed (T* pResBase = result)
                fixed (nint* pFramePtrsBase = framePtrs) // ポインタ配列の先頭アドレス
                {
                    nint resBaseAddr = (nint)pResBase;
                    nint framePtrsAddr = (nint)pFramePtrsBase;

                    // ★チューニングパラメータ: BlockSize = 4096
                    int blockSize = 4096;

                    // Partitionerで範囲を区切る
                    var rangePartitioner = Partitioner.Create(0, length, blockSize);

                    Parallel.ForEach(rangePartitioner, range =>
                    {
                        // ここは別スレッド。nintからポインタを復元
                        T* pRes = (T*)resBaseAddr;
                        nint* pFrames = (nint*)framePtrsAddr; // T*[] ではなく nint[] へのポインタ

                        int start = range.Item1;
                        int end = range.Item2;
                        int count = end - start;

                        // 該当ブロックの先頭へ移動
                        T* pResChunk = pRes + start;

                        // --- モード別処理 ---
                        if (mode == ProjectionMode.Maximum)
                        {
                            // 初期化
                            T minVal = T.MinValue;
                            // SpanFill的に初期化 (ループ展開されるので高速)
                            new Span<T>(pResChunk, count).Fill(minVal);

                            // Zループ
                            for (int z = 0; z < depth; z++)
                            {
                                T* pFrameChunk = (T*)pFrames[z] + start;

                                // ブロック内ループ (L1キャッシュヒット)
                                for (int i = 0; i < count; i++)
                                {
                                    T val = pFrameChunk[i];
                                    if (val > pResChunk[i]) pResChunk[i] = val;
                                }
                            }
                        }
                        else if (mode == ProjectionMode.Minimum)
                        {
                            T maxVal = T.MaxValue;
                            new Span<T>(pResChunk, count).Fill(maxVal);

                            for (int z = 0; z < depth; z++)
                            {
                                T* pFrameChunk = (T*)pFrames[z] + start;
                                for (int i = 0; i < count; i++)
                                {
                                    T val = pFrameChunk[i];
                                    if (val < pResChunk[i]) pResChunk[i] = val;
                                }
                            }
                        }
                        else if (mode == ProjectionMode.Average)
                        {
                            // AIP用の一時バッファ (double)
                            // stackalloc は危険(サイズ不明)なので、配列プールかローカル配列
                            // count=4096なら new double[4096] (32KB) はコスト軽微
                            double[] accBuffer = new double[count];

                            fixed (double* pAcc = accBuffer)
                            {
                                for (int z = 0; z < depth; z++)
                                {
                                    T* pFrameChunk = (T*)pFrames[z] + start;
                                    for (int i = 0; i < count; i++)
                                    {
                                        pAcc[i] += double.CreateChecked(pFrameChunk[i]);
                                    }
                                }

                                double invDepth = 1.0 / depth;
                                for (int i = 0; i < count; i++)
                                {
                                    pResChunk[i] = T.CreateChecked(pAcc[i] * invDepth);
                                }
                            }
                        }
                    });
                }
            }
            finally
            {
                for (int z = 0; z < depth; z++) if (handles[z].IsAllocated) handles[z].Free();
            }

            var md = new MatrixData<T>(width, height, result);
            md.SetXYScale(vol._scale.XMin, vol._scale.XMax, vol._scale.YMin, vol._scale.YMax);
            md.XUnit = vol._scale.XUnit;
            md.YUnit = vol._scale.YUnit;
            return md;
        }
    }


}
