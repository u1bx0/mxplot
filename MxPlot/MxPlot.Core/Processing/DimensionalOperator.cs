using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MxPlot.Core.Processing
{

    /// <summary>
    /// Provides multi-dimensional operators for processing MatrixData instances, including transposition, slicing, reordering, and mapping.
    /// </summary>
    public static class DimensionalOperator
    {

        /// <summary>
        /// Simple 2D transpose through all frames.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="src"></param>
        /// <returns></returns>
        public static MatrixData<T> Transpose<T>(this MatrixData<T> src) where T : unmanaged
        {
            int srcW = src.XCount;
            int srcH = src.YCount;
            int frameCount = src.FrameCount;

            int newW = srcH;
            int newH = srcW;
            
            var transposed = new T[frameCount][];
            var vminArray = new double[frameCount][];
            var vmaxArray = new double[frameCount][];

            // 1. Parallel Transpose with Cache Blocking
            Parallel.For(0, frameCount, frameIndex =>
            {
                var srcArray = src.GetArray(frameIndex); // srcの生データ取得
                var dstArray = new T[newW * newH];

                // キャッシュブロッキング定数
                const int BlockSize = 32;

                // srcW, srcH は元の画像の幅・高さ
                for (int xBase = 0; xBase < srcW; xBase += BlockSize)
                {
                    for (int yBase = 0; yBase < srcH; yBase += BlockSize)
                    {
                        int xMax = Math.Min(xBase + BlockSize, srcW);
                        int yMax = Math.Min(yBase + BlockSize, srcH);

                        for (int x = xBase; x < xMax; x++)
                        {
                            // 転置ロジック
                            // src: (x, y) -> index = y * srcW + x
                            // dst: (y, x) -> index = x * newW + y  (newW = srcH)

                            // dstの書き込み開始位置 (dstの x 行目 = 元の x 列目)
                            int dstBaseIndex = x * newW + yBase;

                            // srcの読み込み開始位置
                            int srcBaseIndex = yBase * srcW + x;

                            for (int y = yBase; y < yMax; y++)
                            {
                                dstArray[dstBaseIndex] = srcArray[srcBaseIndex];

                                dstBaseIndex++;     // dstは横(Y)に連続して進む
                                srcBaseIndex += srcW; // srcは縦(Y)に進むのでWidth分加算
                            }
                        }
                    }
                }
                transposed[frameIndex] = dstArray;
                var (minArray, maxArray) = src.GetMinMaxArrays(frameIndex);
                vminArray[frameIndex] = minArray;
                vmaxArray[frameIndex] = maxArray;
            });
            

            // 2. 新しいインスタンスの生成
            var result = new MatrixData<T>(newW, newH, transposed.ToList(), vminArray.ToList(), vmaxArray.ToList());
            // 物理スケールと単位の入れ替え
            // src.XMin -> result.YMin, src.YUnit -> result.XUnit
            result.SetXYScale(src.YMin, src.YMax, src.XMin, src.XMax);
            result.XUnit = src.YUnit;
            result.YUnit = src.XUnit;

            return result;
        }

        /// <summary>
        /// Creates a lower-dimensional subset (N-1) of the matrix by "snapping" the specified axis to a single index.
        /// The targeted axis is removed from the resulting dimensions.
        /// </summary>
        /// <remarks>The returned matrix will have <b>one fewer dimension than the original</b>, with the
        /// specified axis removed. If deepCopy is false, changes to the returned matrix may affect the original data.
        /// Use deepCopy to ensure the slice is independent.</remarks>
        /// <param name="axisName">The name of the axis along which to slice. Must correspond to an existing axis in the matrix.</param>
        /// <param name="indexInAxis">The zero-based index along the specified axis at which to extract the slice. Must be within the valid range
        /// for the axis.</param>
        /// <param name="deepCopy">true to create a deep copy of the underlying data for the slice; otherwise, false to create a shallow view.</param>
        /// <returns>A new MatrixData<typeparamref name="T"/> instance representing the slice at the specified axis and index, with the selected axis
        /// removed from its dimensions.</returns>
        /// <exception cref="ArgumentException">Thrown if axisKey does not correspond to an existing axis in the matrix.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if index is less than zero or greater than or equal to the number of elements in the specified axis.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the resulting slice contains no frames.</exception>
        public static MatrixData<T> SnapTo<T>(this MatrixData<T> src, string axisName, int indexInAxis, bool deepCopy = false)
            where T : unmanaged
        {
            // 1. Specify the axis to slice and validate inputs
            if (!src.Dimensions.Contains(axisName))
                throw new ArgumentException($"Axis '{axisName}' does not exist in the current dimensions.");

            var targetAxis = src.Dimensions[axisName]!;
            if (indexInAxis < 0 || indexInAxis >= targetAxis.Count)
                throw new ArgumentOutOfRangeException(nameof(indexInAxis), $"Index {indexInAxis} is out of range for axis '{axisName}' (Count: {targetAxis.Count}).");

            // 2. Get the list of frame indices that correspond to the slice
            var targetFrameIndices = src.Dimensions.GetIndicesForSlice(axisName, indexInAxis);

            if (targetFrameIndices.Count == 0)
                throw new InvalidOperationException("Resulting slice contains no frames. Logic error.");

            // 3. Extract the frames using Reorder
            var snapped = src.Reorder(targetFrameIndices, deepCopy);

            // 4. Define new dimensions excluding the sliced axis
            var newAxes = src.Dimensions.CreateAxesWithout(axisName);

            // 5. Apply the new dimensions to the sliced matrix
            snapped.DefineDimensions(newAxes);

            return snapped;
        }

        /// <summary>
        /// Extract a single frame as a MatrixData<typeparamref name="T"/> specified by frameIndex.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="src"></param>
        /// <param name="frameIndex"></param>
        /// <param name="deepCopy"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static MatrixData<T> SliceAt<T>(this MatrixData<T> src, int frameIndex, bool deepCopy = false)
            where T : unmanaged
        {
            if (frameIndex < 0 || frameIndex >= src.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frameIndex), $"Frame index {frameIndex} is out of range (0 to {src.FrameCount - 1}).");
            return src.Reorder(new List<int> { frameIndex }, deepCopy);
        }


        /// <summary>
        /// This provides a convenient way to extract a slice (single frame) by specifying multiple axes and their corresponding indices.
        /// e.g. var slice = data.SliceAt(("Time", 5), ("Z", 2)); 
        /// </summary>
        /// <remarks>
        /// Note 1: The returned MatrixData is a <b>shallow copy</b> of the original. No option to deepCopy provided. Use Duplicate if necessary<br/>
        /// Note 2: If all axis indices are not provided, the remaining axes point to the current index.
        /// </remarks>
        /// <typeparam name="T"></typeparam>
        /// <param name="src"></param>
        /// <param name="coords">A variable number of tuples specifying the axis name and the target index (e.g., ("X", 10)).</param>
        /// <returns></returns>
        public static MatrixData<T> SliceAt<T>(this MatrixData<T> src, params (string AxisName, int AxisIndex)[] coords)
            where T : unmanaged
        {
            return SliceAt(src, src.Dimensions.At(coords), false);
        }


        /// <summary>
        /// Extracts a one-dimensional slice along the specified axis of the matrix, using the provided base indices for
        /// all other axes.
        /// </summary>
        /// <remarks>The extracted slice will have the same scale and units as the source matrix. Use
        /// deepCopy to control whether the data is copied or referenced. This method is useful for extracting a vector
        /// or profile from a multi-dimensional matrix along a specific axis.
        /// </remarks>
        /// <typeparam name="T">The type of elements stored in the matrix. Must be an unmanaged type.</typeparam>
        /// <param name="src">The source matrix from which to extract the slice.</param>
        /// <param name="axisName">The name of the axis along which to extract the slice. Must correspond to an existing axis in the matrix
        /// dimensions.</param>
        /// <param name="baseIndices">An array of indices specifying the fixed positions for all axes except the extraction axis. The length must
        /// match the number of axes in the matrix.</param>
        /// <param name="deepCopy">true to create a deep copy of the extracted data; otherwise, false to create a shallow view. The default is
        /// false.</param>
        /// <returns>A new MatrixData<typeparamref name="T"/> containing the extracted slice along the specified axis. The result is one-dimensional,
        /// with the axis order and metadata preserved.</returns>
        /// <exception cref="ArgumentException">Thrown if axisName does not exist in the matrix dimensions, or if baseIndices does not match the number of
        /// axes.</exception>
        public static MatrixData<T> ExtractAlong<T>(this MatrixData<T> src, string axisName, int[] baseIndices, bool deepCopy = false)
            where T: unmanaged
        {
            var dims = src.Dimensions;

            // 1. Specify the axis to exctract and validate inputs
            if (!dims.Contains(axisName))
                throw new ArgumentException($"Axis '{axisName}' does not exist in the current dimensions.");

            var targetAxis = dims[axisName]!;
            
            if(baseIndices.Length != dims.AxisCount)
                throw new ArgumentException($"baseIndices length {baseIndices.Length} does not match axis count {dims.AxisCount}.");

            var axisIndex = dims.GetAxisOrder(targetAxis);
            var order = new List<int>();
            var workIndices = (int[])baseIndices.Clone();
            for (int i = 0; i < targetAxis.Count; i++)
            {
                workIndices[axisIndex] = i;
                var frameIndices = dims.GetFrameIndexFrom(workIndices);
                order.Add(frameIndices);
            }

            // 3. Extract the frames using Reorder with the same XY scale and Units.
            var extracted = src.Reorder(order, deepCopy);
            extracted.DefineDimensions(targetAxis.Clone());
            
            return extracted;
        }

        /// <summary>
        /// Create a new instance with the frames reordered by the specified order. Important: the order list does not require the complete set of the original frames.
        /// This method can be used to extract or duplicate specific frames as well. 
        /// Consequently, the dimension information is totally removed.
        /// <b>Metadata is not copied to the new instance.</b>
        /// </summary>
        /// <param name="order"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public static MatrixData<T> Reorder<T>(this MatrixData<T> src, List<int> order, bool deepCopy = false)
            where T : unmanaged
        {
            int num = order.Count;
            int max = order.Max();
            int min = order.Min();
            if (max >= src.FrameCount || min < 0)
                throw new ArgumentException($"invalid order: min = {min}, max = {max}, count = {num}");

            var arrays = new List<T[]>();
            var vminList = new List<double[]>();
            var vmaxList = new List<double[]>();

            foreach (var idx in order)
            {
                var array = src.GetArray(idx);
                if (deepCopy)
                {
                    var dst = new T[array.Length];
                    array.AsSpan().CopyTo(dst);
                    array = dst;
                }
                arrays.Add(array);
                var (minArray, maxArray) = src.GetMinMaxArrays(idx);
                vminList.Add(minArray);
                vmaxList.Add(maxArray);
            }

            var md = new MatrixData<T>(src.XCount, src.YCount, arrays, vminList, vmaxList);
            md.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            md.XUnit = src.XUnit;
            md.YUnit = src.YUnit;

            return md;
        }

        /// <summary>
        /// Reorders frames by rearranging axes in the specified order.
        /// </summary>
        /// <remarks>
        /// This method changes the memory layout by reordering frames according to the new axis order.
        /// All axes must be specified, and the axis order determines which dimension varies fastest (first axis = innermost).
        /// <para>
        /// <b>Example:</b> Original axes [Z, Channel, Time] can be reordered to [Channel, Z, Time] to make Channel the fastest-varying dimension.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The type of elements in the matrix. Must be an unmanaged type.</typeparam>
        /// <param name="src">The source matrix to reorder.</param>
        /// <param name="newAxisOrder">
        /// New axis order as an array of axis names. Must include all existing axes exactly once.
        /// Example: new[] { "Channel", "Z", "Time" }
        /// </param>
        /// <param name="deepCopy">
        /// true to create an independent copy of the data; false to create a shallow view (default).
        /// </param>
        /// <returns>A new MatrixData instance with frames reordered according to the specified axis order.</returns>
        /// <exception cref="ArgumentNullException">Thrown if src or newAxisOrder is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the source matrix has no defined dimensions.</exception>
        /// <exception cref="ArgumentException">
        /// Thrown if:
        /// - The number of axis names doesn't match the number of existing axes
        /// - Duplicate axis names are detected
        /// - Any specified axis name doesn't exist in the source matrix
        /// </exception>
        /// <example>
        /// <code>
        /// // Original: Z=10, Channel=3, Time=5 (Z varies fastest)
        /// var data = new MatrixData&lt;double&gt;(512, 512, 150);
        /// data.DefineDimensions(
        ///     Axis.Z(10, 0, 50, "µm"),
        ///     Axis.Channel(3),
        ///     Axis.Time(5, 0, 10, "s")
        /// );
        /// 
        /// // Reorder to make Channel vary fastest
        /// var reordered = data.Reorder(new[] { "Channel", "Z", "Time" });
        /// 
        /// // Result: Channel=3, Z=10, Time=5 (Channel now varies fastest)
        /// // Frame 0: (C=0, Z=0, T=0), Frame 1: (C=1, Z=0, T=0), Frame 2: (C=2, Z=0, T=0), ...
        /// </code>
        /// </example>
        public static MatrixData<T> Reorder<T>(
            this MatrixData<T> src, 
            string[] newAxisOrder, 
            bool deepCopy = false)
            where T : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (newAxisOrder == null) throw new ArgumentNullException(nameof(newAxisOrder));
            
            var dims = src.Dimensions;
            if (dims == null || dims.AxisCount == 0)
                throw new InvalidOperationException("Source matrix has no defined dimensions.");
            
            // === Validation ===
            if (newAxisOrder.Length != dims.AxisCount)
                throw new ArgumentException(
                    $"Must specify exactly {dims.AxisCount} axis names, but got {newAxisOrder.Length}. " +
                    $"Available axes: {string.Join(", ", dims.Axes.Select(a => a.Name))}");
            
            if (newAxisOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != newAxisOrder.Length)
                throw new ArgumentException("Duplicate axis names detected.");
            
            foreach (var name in newAxisOrder)
            {
                if (!dims.Contains(name))
                    throw new ArgumentException(
                        $"Axis '{name}' does not exist. Available axes: {string.Join(", ", dims.Axes.Select(a => a.Name))}");
            }
            
            // === Calculate new strides ===
            var newAxes = newAxisOrder.Select(name => dims[name]!).ToArray();
            var newStrides = new int[newAxes.Length];
            int stride = 1;
            for (int i = 0; i < newAxes.Length; i++)
            {
                newStrides[i] = stride;
                stride *= newAxes[i].Count;
            }
            
            // === Build frame mapping ===
            // For each old frame position, calculate its new position in the reordered structure
            var frameMapping = new List<int>(src.FrameCount);
            
            for (int oldFrame = 0; oldFrame < src.FrameCount; oldFrame++)
            {
                // Get axis coordinates of the old frame
                var oldCoords = dims.GetAxisIndices(oldFrame);
                
                // Calculate new frame index with new axis order
                int newFrame = 0;
                for (int i = 0; i < newAxes.Length; i++)
                {
                    int oldAxisIndex = dims.GetAxisOrder(newAxisOrder[i]);
                    int coord = oldCoords[oldAxisIndex];
                    newFrame += coord * newStrides[i];
                }
                
                frameMapping.Add(newFrame);
            }
            
            // Sort to get the order for extracting frames
            var sortedMapping = frameMapping
                .Select((newIdx, oldIdx) => (newIdx, oldIdx))
                .OrderBy(x => x.newIdx)
                .Select(x => x.oldIdx)
                .ToList();
            
            // Call existing Reorder method
            var reordered = src.Reorder(sortedMapping, deepCopy);
            
            // Apply new axis structure
            reordered.DefineDimensions(Axis.CreateFrom(newAxes));
            
            return reordered;
        }

        /// <summary>
        /// Represents a method that converts a matrix element from the source type to the destination type, using its
        /// value and position within a frame.
        /// </summary>
        /// <remarks>Use this delegate to define custom conversion logic for matrix elements, which may
        /// depend on their value, position, or frame context.</remarks>
        /// <typeparam name="TSrc">The type of the source matrix element to be converted.</typeparam>
        /// <typeparam name="TDst">The type of the destination matrix element after conversion.</typeparam>
        /// <param name="value">The value of the source matrix element to convert.</param>
        /// <param name="xIndex">The zero-based column index of the element within the matrix.</param>
        /// <param name="yIndex">The zero-based row index of the element within the matrix.</param>
        /// <param name="frameIndex">The zero-based index of the frame containing the matrix element.</param>
        /// <returns>The converted matrix element of type <typeparamref name="TDst"/>.</returns>
        public delegate TDst MatrixElementConverter<TSrc, TDst>(
            TSrc value,
            int xIndex,
            int yIndex,
            int frameIndex
        );

        /// <summary>
        /// Specifies the parallelization strategy for the Map operation.
        /// </summary>
        public enum MapStrategy
        {
            NoValueRangeCheck,

            /// <summary>
            /// Parallelizes along the frame axis (Parallel.For over frames).
            /// Best for: Many frames with moderate image sizes.
            /// </summary>
            ParallelAlongFrames,
        }

        /// <summary>
        /// Creates a new matrix by applying a specified conversion function to each element of the source matrix.
        /// </summary>
        /// <remarks>If the destination type implements IComparable<TDst>, the resulting matrix will
        /// include per-frame minimum and maximum values, converted to double if possible. The X and Y scale, units, and
        /// metadata from the source matrix are copied to the result.</remarks>
        /// <typeparam name="TSrc">The type of the elements in the source matrix. Must be an unmanaged type.</typeparam>
        /// <typeparam name="TDst">The type of the elements in the destination matrix. Must be an unmanaged type.</typeparam>
        /// <param name="src">The source matrix whose elements will be converted.</param>
        /// <param name="converter">A delegate that defines how to convert each element from the source type to the destination type. The
        /// delegate receives the source value, its X and Y coordinates, and the frame index.</param>
        /// <param name="strategy">The parallelization strategy to use. Default is Adaptive, which automatically selects the best strategy.</param>
        /// <returns>A new MatrixData<TDst> containing the converted elements. Metadata, axis information, and units from the
        /// source matrix are preserved in the result.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="src"/> or <paramref name="converter"/> is null.</exception>
        public static MatrixData<TDst> Map<TSrc, TDst>(
            this MatrixData<TSrc> src, 
            MatrixElementConverter<TSrc, TDst> converter,
            MapStrategy strategy = MapStrategy.ParallelAlongFrames)
            where TSrc : unmanaged
            where TDst : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            // Execute with selected strategy
            if(strategy == MapStrategy.NoValueRangeCheck)
            {
                return MapNoValueRangeCheck(src, converter);
            }
            else // MapStrategy.ParallelAlongFrames
            {
                return MapParallelAlongFrames(src, converter);
            }
        }

        /// <summary>
        /// Parallelizes along the frame axis (Parallel.For over frames).
        /// </summary>
        private static MatrixData<TDst> MapNoValueRangeCheck<TSrc, TDst>(MatrixData<TSrc> src, MatrixElementConverter<TSrc, TDst> converter)
            where TSrc : unmanaged
            where TDst : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            int width = src.XCount;
            int height = src.YCount;
            int frameCount = src.FrameCount;

            // 結果格納用配列
            var convertedArrays = new TDst[frameCount][];

            // システムの論理コア数
            int processorCount = Environment.ProcessorCount;

            // FrameCountがコア数以上なら「フレーム並列」（オーバーヘッド最小・キャッシュ効率最大）
            // FrameCountが少ないなら「行並列」（リソース総動員）
            if (frameCount >= processorCount)
            {
                // === Plan A: Frame Parallel ===
                Parallel.For(0, frameCount, frame =>
                {
                    // 配列確保
                    var srcArray = src.GetArray(frame);
                    var dstArray = new TDst[srcArray.Length];

                    // Span & Ref 取得 (境界チェック回避)
                    // srcArray.Lengthは width * height と保証されている前提
                    ref TSrc srcRef = ref MemoryMarshal.GetReference(srcArray.AsSpan());
                    ref TDst dstRef = ref MemoryMarshal.GetReference(dstArray.AsSpan());

                    int index = 0;
                    // 2重ループ (y, x の順序を守る)
                    for (int iy = 0; iy < height; iy++)
                    {
                        for (int ix = 0; ix < width; ix++)
                        {
                            // ポインタ演算でアクセス (Unsafe.Add)
                            var s = Unsafe.Add(ref srcRef, index);                       
                            var d = converter(s, ix, iy, frame);
                            Unsafe.Add(ref dstRef, index) = d;
                            index++;
                        }
                    }
                    convertedArrays[frame] = dstArray;
                });
            }
            else
            {
                // === Plan B: Row Parallel (Hybrid) ===
                // フレームループは直列、内部で行(Y)を並列化
                for (int frame = 0; frame < frameCount; frame++)
                {
                    var srcArray = src.GetArray(frame);
                    var dstArray = new TDst[srcArray.Length];

                    // 行単位で並列化
                    Parallel.For(0, height, iy =>
                    {
                        // ここでSpan再取得（Parallel内でのref struct利用制約のため）
                        // コストはほぼゼロ
                        ref TSrc srcRefBase = ref MemoryMarshal.GetReference(srcArray.AsSpan());
                        ref TDst dstRefBase = ref MemoryMarshal.GetReference(dstArray.AsSpan());

                        int rowStart = iy * width;

                        // 行の先頭アドレスを計算
                        ref TSrc rowSrcRef = ref Unsafe.Add(ref srcRefBase, rowStart);
                        ref TDst rowDstRef = ref Unsafe.Add(ref dstRefBase, rowStart);

                        for (int ix = 0; ix < width; ix++)
                        {
                            // 行内はシーケンシャルアクセス
                            // ix を加算しながらポインタを進める
                            var s = Unsafe.Add(ref rowSrcRef, ix);
                            var d = converter(s, ix, iy, frame);
                            Unsafe.Add(ref rowDstRef, ix) = d;
                        }
                    });
                    convertedArrays[frame] = dstArray;
                }
            }

            var result = new MatrixData<TDst>(width, height, convertedArrays.ToList());
            result.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            result.XUnit = src.XUnit;
            result.YUnit = src.YUnit;
            foreach (var kvp in src.Metadata) result.Metadata[kvp.Key] = kvp.Value;
            if (src.Dimensions?.Axes?.Any() == true)
                result.DefineDimensions(Axis.CreateFrom(src.Dimensions.Axes.ToArray()));

            return result;
        }


        /// <summary>
        /// Parallelizes along the frame axis (Parallel.For over frames).
        /// </summary>
        private static MatrixData<TDst> MapParallelAlongFrames<TSrc, TDst>(MatrixData<TSrc> src, MatrixElementConverter<TSrc, TDst> converter)
            where TSrc : unmanaged
            where TDst : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (converter == null) throw new ArgumentNullException(nameof(converter));

            int width = src.XCount;
            int height = src.YCount;
            int frameCount = src.FrameCount;

            var convertedArrays = new TDst[frameCount][];

            // Min/Max 計算用（IComparableチェック付き）
            bool isComparable = typeof(IComparable<TDst>).IsAssignableFrom(typeof(TDst));
            var minValues = isComparable ? new TDst[frameCount] : null;
            var maxValues = isComparable ? new TDst[frameCount] : null;

            Parallel.For(0, frameCount, frame =>
            {
                var srcArray = src.GetArray(frame);
                var dstArray = new TDst[srcArray.Length];

                // Span化（配列境界チェックの抑制を期待）
                var srcSpan = srcArray.AsSpan();
                var dstSpan = dstArray.AsSpan();

                // Min/Max 一時変数
                var comparer = isComparable ? Comparer<TDst>.Default : null;
                bool first = true;
                TDst localMin = default;
                TDst localMax = default;

                // 2次元ループとして展開（物理座標計算の最適化のため）
                // index変数を別途管理することで、除算/剰余(i % width)を回避して高速化
                int index = 0;
                for (int iy = 0; iy < height; iy++)
                {
                    for (int ix = 0; ix < width; ix++)
                    {
                        // ユーザーデリゲート呼び出し
                        var val = converter(srcSpan[index], ix, iy, frame);
                        dstSpan[index] = val;

                        // Min/Max更新ロジック
                        if (isComparable)
                        {
                            if (first)
                            {
                                localMin = val;
                                localMax = val;
                                first = false;
                            }
                            else
                            {
                                // comparerはnullでないことが保証されている
                                if (comparer!.Compare(val, localMin) < 0) localMin = val;
                                if (comparer.Compare(val, localMax) > 0) localMax = val;
                            }
                        }
                        index++;
                    }
                }

                if (isComparable && !first)
                {
                    minValues![frame] = localMin;
                    maxValues![frame] = localMax;
                }

                convertedArrays[frame] = dstArray;
            });

            List<double>? minDoubleValues = null;
            List<double>? maxDoubleValues = null;
            if (isComparable && minValues is not null && maxValues is not null)
            {
                try
                {
                    minDoubleValues = minValues!.Select(v => System.Convert.ToDouble(v)).ToList();
                    maxDoubleValues = maxValues!.Select(v => System.Convert.ToDouble(v)).ToList();
                }
                catch
                {
                    minDoubleValues = null;
                    maxDoubleValues = null;
                }
            }

            MatrixData<TDst> result;
            if (minDoubleValues == null || maxDoubleValues == null)
            {
                result = new MatrixData<TDst>(
                    width, height, convertedArrays.ToList());
            }
            else
            {
                result = new MatrixData<TDst>(
                    width, height, convertedArrays.ToList(),
                    minDoubleValues, maxDoubleValues);
            }

            result.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            result.XUnit = src.XUnit;
            result.YUnit = src.YUnit;
            foreach (var kvp in src.Metadata) result.Metadata[kvp.Key] = kvp.Value;
            if (src.Dimensions?.Axes?.Any() == true)
                result.DefineDimensions(Axis.CreateFrom(src.Dimensions.Axes.ToArray()));

            return result;
        }

       
        /// <summary>
        /// Create a new MatrixData instance containing a single transformed frame from the source.
        /// The transformation is applied using a converter function that has access to both array indices and physical coordinates.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method extracts a specific frame (specified by <paramref name="frame"/>) and creates a new single-frame MatrixData.
        /// Unlike standard projection, the <paramref name="converter"/> function receives 5 arguments:
        /// <list type="bullet">
        /// <item><description><c>value</c>: The element value.</description></item>
        /// <item><description><c>ix</c>, <c>iy</c>: The zero-based array indices.</description></item>
        /// <item><description><c>x</c>, <c>y</c>: The physical coordinates calculated using <see cref="MatrixData{T}.XMin"/>, <see cref="MatrixData{T}.XStep"/>, etc.</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// This implementation is highly optimized: it creates a lightweight view (shallow copy) of the source frame before mapping, minimizing memory allocation overhead.
        /// </para>
        /// </remarks>
        /// <example>
        /// <b>Example 1: Circular Masking based on physical distance</b>
        /// <code>
        /// // Extract the current active frame and apply a mask.
        /// // Keep values within 10mm radius from the center (0,0), set others to NaN.
        /// var maskedData = rawMatrix.MapAt<double, double>((val, ix, iy, x, y) => 
        /// {
        ///     double dist = Math.Sqrt(x * x + y * y);
        ///     return (dist &lt;= 10.0) ? val : double.NaN;
        /// });
        /// </code>
        /// 
        /// <b>Example 2: Converting to Byte for visualization (Heatmap generation)</b>
        /// <code>
        /// // Convert the 5th frame to grayscale (0-255) based on signal intensity.
        /// var visualData = rawMatrix.MapAt<double, byte>((val, ix, iy, x, y) => 
        /// {
        ///     return (byte)Math.Clamp(val * 255.0, 0, 255);
        /// }, frame: 5);
        /// </code>
        /// </example>
        /// <typeparam name="Tsrc">The type of the elements in the source matrix. Must be an unmanaged type.</typeparam>
        /// <typeparam name="TDst">The type of the elements in the resulting matrix. Must be an unmanaged type.</typeparam>
        /// <param name="src">The source matrix whose elements are to be transformed.</param>
        /// <param name="converter">
        /// A function that converts each element.
        /// Signature: <c>(Tsrc value, int xIndex, int yIndex, double xPos, double yPos) -> TDst</c>
        /// </param>
        /// <param name="frame">The index of the frame to use from the source matrix. If set to -1 (default), the <see cref="MatrixData{T}.ActiveIndex"/> is used.</param>
        /// <returns>A new MatrixData&lt;TDst&gt; containing the single transformed frame.</returns>
        public static MatrixData<TDst> MapAt<Tsrc, TDst>(this MatrixData<Tsrc> src,
            Func<Tsrc, int, int, double, double, TDst> converter, int frame = -1)
            where Tsrc : unmanaged
            where TDst : unmanaged
        {
            double xmin = src.XMin;
            double ymin = src.YMin;
            double xstep = src.XStep;
            double ystep = src.YStep; 
            if (frame == -1) frame = src.ActiveIndex;
            return src.SliceAt(frame, false).Map<Tsrc, TDst>((v, ix, iy, f) 
                => converter(v, ix, iy, ix * xstep + xmin, iy * ystep + ymin));
        }

        // 【重要】Span<T> を引数に取れる専用の型を定義する
        // ジェネリックの Func<> は使えないが、直接定義したデリゲートなら .NET Standard 2.1 / .NET Core 2.1 頃から Span を使える
        /// <summary>
        /// Represents a method that performs a reduction operation using the specified coordinates and values.
        /// </summary>
        /// <remarks>This delegate enables high-performance reduction operations over spans, allowing for
        /// efficient processing of large or non-contiguous data sets. The use of Span<T> and ReadOnlySpan<T> avoids
        /// unnecessary allocations and enables stack-only or memory-safe operations.</remarks>
        /// <typeparam name="T">The type of the value to be produced by the reduction operation.</typeparam>
        /// <param name="ix">The x index in 2D matrix, where the reduction operation is performed.</param>
        /// <param name="iy">The y index in 2D matrix, where the reduction operation is performed..</param>
        /// <param name="coords">A read-only span containing the coordinates relevant to the reduction. The contents and length are
        /// determined by the calling context.</param>
        /// <param name="values">A span of values to be reduced. The method may read from and write to this span as part of the reduction
        /// process.</param>
        /// <returns>The result of the reduction operation of type T.</returns>
        public delegate T ReducerFunc<T>(int ix, int iy, ReadOnlySpan<int> coords, Span<T> values);

        /// <summary>
        /// Represents a method that performs a reduction operation over a span of values, using the specified integer
        /// index to influence the reduction.
        /// </summary>
        /// <typeparam name="T">The type of the elements in the span and the result of the reduction operation.</typeparam>
        /// <param name="ix">The x index in 2D matrix, where the reduction operation is performed.</param>
        /// <param name="iy">The y index in 2D matrix, where the reduction operation is performed.</param>
        /// <param name="values">A span of values to be reduced. The reduction is performed over the elements of this span.</param>
        /// <returns>The result of the reduction operation, of type T.</returns>
        public delegate T EntireReducerFunc<T>(int ix, int iy, Span<T> values);

        /// <summary>
        /// フレーム軸全体に対する縮約
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="src"></param>
        /// <param name="reducer"></param>
        /// <param name="useParallel"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static MatrixData<T> Reduce<T>(this MatrixData<T> src, EntireReducerFunc<T> reducer, bool useParallel = true)
            where T: unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (reducer == null) throw new ArgumentNullException(nameof(reducer));

            var xnum = src.XCount;
            var ynum = src.YCount;
            var fnum = src.FrameCount;
            var totalPixels = xnum * ynum;

            // 結果格納用配列（1枚の画像になる）
            var arrayReduced = new T[totalPixels];

            // 高速化: 事前に全フレームの生配列への参照を取得しておく
            // (GetArrayのオーバーヘッドをループ内で繰り返さないため)
            var allFrames = new T[fnum][];
            for (int f = 0; f < fnum; f++)
            {
                allFrames[f] = src.GetArray(f);
            }

            if (useParallel)
            {
                // Parallel.Forの「スレッドローカル変数」機能を使用
                Parallel.For(
                    0,
                    ynum, // Y行ごとに分割して並列化
                          // [LocalInit] スレッドごとに1回だけ呼ばれる。作業用バッファを確保。
                    () => new T[fnum],

                    // [Body] ループ本体
                    (iy, loopState, buffer) =>
                    {
                        int st = iy * xnum;
                        for (int ix = 0; ix < xnum; ix++)
                        {
                            int pos = st + ix;

                            // 全フレームから値を集める
                            for (int f = 0; f < fnum; f++)
                            {
                                // キャッシュしておいたフレーム配列から取得
                                buffer[f] = allFrames[f][pos];
                            }

                            // Reducerに渡す（Spanでラップして渡すのでゼロコピー）
                            arrayReduced[pos] = reducer(ix, iy, new Span<T>(buffer));
                        }
                        return buffer; // 次のイテレーションにバッファを引き継ぐ
                    },

                    // [LocalFinally]
                    (buffer) => { /* 何もしない */ }
                );
            }
            else
            {
                // シングルスレッド版
                var buffer = new T[fnum];
                for (int iy = 0; iy < ynum; iy++)
                {
                    int st = iy * xnum;
                    for (int ix = 0; ix < xnum; ix++)
                    {
                        int pos = st + ix;
                        for (int f = 0; f < fnum; f++)
                        {
                            buffer[f] = allFrames[f][pos];
                        }
                        arrayReduced[pos] = reducer(ix, iy, new Span<T>(buffer));
                    }
                }
            }

            // 結果の生成
            var md = new MatrixData<T>(xnum, ynum, new List<T[]> { arrayReduced });

            // 物理情報の継承
            md.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            md.XUnit = src.XUnit;
            md.YUnit = src.YUnit;

            return md;
        }

        /// <summary>
        /// Reduces the dimensionality of the matrix data by aggregating values along a specified target axis.
        /// </summary>
        /// <typeparam name="T">The type of the data elements (must be unmanaged).</typeparam>
        /// <param name="targetAxisName">The name of the axis to reduce (e.g., "Time", "Z").</param>
        /// <param name="reducer">
        /// A function that calculates the aggregated value for a single pixel.
        /// <br/>Arguments:
        /// <list type="bullet">
        /// <item><c>ix</c>, <c>iy</c>: Pixel coordinates.</item>
        /// <item><c>coords</c>: Coordinates of the current frame context (e.g., [C=0, T=5]).</item>
        /// <item><c>values</c>: A span of values along the target axis to be reduced.</item>
        /// </list>
        /// </param>
        /// <param name="useParallel">
        /// If set to <c>true</c> (default), executes processing in parallel for high performance. 
        /// Set to <c>false</c> if the reducer function is not thread-safe.
        /// </param>
        /// <returns>A new <see cref="MatrixData{T}"/> instance with the target axis removed.</returns>
        /// <exception cref="ArgumentException">Thrown when the specified target axis does not exist.</exception>
         public static MatrixData<T> Reduce<T>(
            this MatrixData<T> src,
            string targetAxisName,
            ReducerFunc<T> reducer,
            bool useParallel = true) 
            where T : unmanaged
        {
            var dims = src.Dimensions;
            if (!dims.Contains(targetAxisName))
                throw new ArgumentException($"Axis '{targetAxisName}' not found.");

            int targetOrder = dims.GetAxisOrder(targetAxisName);
            var targetAxis = dims[targetOrder];

            // --- 1. 地図作り ---
            var orders = Enumerable.Range(0, dims.AxisCount).ToList();
            orders.Remove(targetOrder);
            orders.Add(targetOrder);

            var groups = new List<(int[] Frames, int[] Coords)>();
            var pos = new int[dims.AxisCount];

            void RecursiveLoop(int depth)
            {
                if (depth == orders.Count - 1)
                {
                    int count = targetAxis.Count;
                    var frames = new int[count];
                    for (int i = 0; i < count; i++)
                    {
                        pos[targetOrder] = i;
                        frames[i] = dims.GetFrameIndexFrom(pos);
                    }
                    groups.Add((frames, (int[])pos.Clone()));
                    return;
                }

                int axisIndex = orders[depth];
                int axisCount = dims[axisIndex].Count;
                for (int i = 0; i < axisCount; i++)
                {
                    pos[axisIndex] = i;
                    RecursiveLoop(depth + 1);
                }
            }
            RecursiveLoop(0);

            // --- 2. Reduce実行 ---
            

            int width = src.XCount;
            int height = src.YCount;

            var resultFrames = new List<T[]>(groups.Count);
            var srcArrays = new T[src.FrameCount][];
            for (int i = 0; i < src.FrameCount; i++) srcArrays[i] = src.GetArray(i);

            foreach (var (sourceIndices, coords) in groups)
            {
                var resultPixels = new T[width * height];
                var contextCoords = coords;

                // ★ ここで分岐
                if (useParallel)
                {
                    // 【並列モード】
                    Parallel.For(0, height, y =>
                    {
                        // スレッドごとにスタック領域を確保
                        Span<T> valueSpan = stackalloc T[sourceIndices.Length];
                        int rowOffset = y * width;

                        for (int x = 0; x < width; x++)
                        {
                            int i = rowOffset + x;

                            // Gather
                            for (int k = 0; k < sourceIndices.Length; k++)
                            {
                                valueSpan[k] = srcArrays[sourceIndices[k]][i];
                            }

                            resultPixels[i] = reducer(x, y, contextCoords, valueSpan);
                        }
                    });
                }
                else
                {
                    // 【シーケンシャルモード】
                    // シングルスレッドなので、バッファは1つ作って使い回せばOK
                    // (stackallocではなく普通の配列でも良いが、Spanインターフェース統一のためstackallocか配列を使用)
                    var buffer = new T[sourceIndices.Length];
                    Span<T> valueSpan = buffer;

                    for (int y = 0; y < height; y++)
                    {
                        int rowOffset = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int i = rowOffset + x;

                            // Gather
                            for (int k = 0; k < sourceIndices.Length; k++)
                            {
                                valueSpan[k] = srcArrays[sourceIndices[k]][i];
                            }

                            resultPixels[i] = reducer(x, y, contextCoords, valueSpan);
                        }
                    }
                }

                resultFrames.Add(resultPixels);
            }

            // --- 3. 結果構築 ---
            var resultMatrix = new MatrixData<T>(width, height, resultFrames);
            var newAxes = dims.CreateAxesWithout(targetAxisName);
            resultMatrix.DefineDimensions(newAxes);
            resultMatrix.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            resultMatrix.XUnit = src.XUnit;
            resultMatrix.YUnit = src.YUnit;

            return resultMatrix;
        }


        /// <summary>
        /// Crops a rectangular region of interest (ROI) from the matrix data.
        /// Physical coordinates (XMin/XMax/YMin/YMax) are automatically updated
        /// to match the cropped region.
        /// </summary>
        /// <typeparam name="T">The data type of matrix elements.</typeparam>
        /// <param name="source">The source MatrixData to crop from.</param>
        /// <param name="x">The starting X index (inclusive) of the crop region.</param>
        /// <param name="y">The starting Y index (inclusive) of the crop region.</param>
        /// <param name="width">The width (in pixels) of the crop region.</param>
        /// <param name="height">The height (in pixels) of the crop region.</param>
        /// <returns>A new MatrixData instance containing only the cropped region.</returns>
        /// <exception cref="ArgumentNullException">Thrown if source is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown if the crop region exceeds the source matrix bounds.
        /// </exception>
        /// <example>
        /// <code>
        /// var matrix = new MatrixData&lt;double&gt;(100, 100);
        /// matrix.SetXYScale(-10, 10, -10, 10);
        /// 
        /// // Crop a 50x50 region starting at (25, 25)
        /// var cropped = matrix.Crop(25, 25, 50, 50);
        /// 
        /// // Physical coordinates are updated:
        /// // Original: XMin=-10, XMax=10
        /// // Cropped:  XMin=-5,  XMax=5 (approximately)
        /// </code>
        /// </example>
        public static MatrixData<T> Crop<T>(this MatrixData<T> source, int x, int y, int width, int height)
            where T : unmanaged
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            // Validate crop region
            if (x < 0 || y < 0)
                throw new ArgumentOutOfRangeException(
                    $"Crop coordinates must be non-negative: x={x}, y={y}");

            if (x + width > source.XCount)
                throw new ArgumentOutOfRangeException(
                    $"Crop region exceeds X bounds: x={x}, width={width}, XCount={source.XCount}");

            if (y + height > source.YCount)
                throw new ArgumentOutOfRangeException(
                    $"Crop region exceeds Y bounds: y={y}, height={height}, YCount={source.YCount}");

            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(
                    $"Crop dimensions must be positive: width={width}, height={height}");

            if (source.XCount <= 1 || source.YCount <= 1)
                throw new ArgumentException("Source matrix must have at least 2 elements in each dimension.");

            double xStep = source.XStep; // 既に計算済みのプロパティを使用
            double yStep = source.YStep;

            // Calculate new physical bounds
            double newXMin = source.XMin + x * xStep;
            double newXMax = newXMin + (width - 1) * xStep;
            double newYMin = source.YMin + y * yStep;
            double newYMax = newYMin + (height - 1) * yStep;

            // Crop all frames
            var croppedArrays = new List<T[]>(source.FrameCount);

            for (int frame = 0; frame < source.FrameCount; frame++)
            {
                var srcArray = source.GetArray(frame);
                var dstArray = new T[width * height];

                // Copy ROI row-by-row using Array.Copy for better performance
                for (int iy = 0; iy < height; iy++)
                {
                    int srcIndex = (y + iy) * source.XCount + x;
                    int dstIndex = iy * width;
                    Array.Copy(srcArray, srcIndex, dstArray, dstIndex, width);
                }

                croppedArrays.Add(dstArray);
            }

            // Create new MatrixData with cropped data
            var result = new MatrixData<T>(width, height, croppedArrays);

            // Update physical coordinates
            result.SetXYScale(newXMin, newXMax, newYMin, newYMax);

            // Copy metadata
            result.XUnit = source.XUnit;
            result.YUnit = source.YUnit;

            foreach (var kvp in source.Metadata)
            {
                result.Metadata[kvp.Key] = kvp.Value;
            }

            // Copy dimension structure if exists
            if (source.Dimensions?.Axes?.Any() == true)
            {
                var axes = Axis.CreateFrom(source.Dimensions.Axes.ToArray());
                result.DefineDimensions(axes);
            }

            return result;
        }

        /// <summary>
        /// Crops a region of interest using physical coordinates instead of pixel indices.
        /// </summary>
        /// <typeparam name="T">The data type of matrix elements.</typeparam>
        /// <param name="source">The source MatrixData to crop from.</param>
        /// <param name="xMin">The minimum X coordinate (in physical units).</param>
        /// <param name="xMax">The maximum X coordinate (in physical units).</param>
        /// <param name="yMin">The minimum Y coordinate (in physical units).</param>
        /// <param name="yMax">The maximum Y coordinate (in physical units).</param>
        /// <returns>A new MatrixData instance containing the cropped region.</returns>
        /// <example>
        /// <code>
        /// var matrix = new MatrixData&lt;double&gt;(100, 100);
        /// matrix.SetXYScale(-10, 10, -10, 10);
        /// 
        /// // Crop using physical coordinates
        /// var cropped = matrix.CropByCoordinates(-5, 5, -5, 5);
        /// // Result: 50x50 pixels centered on origin
        /// </code>
        /// </example>
        public static MatrixData<T> CropByCoordinates<T>(this MatrixData<T> source,
            double xMin, double xMax, double yMin, double yMax)
            where T : unmanaged
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            // Validate coordinate ranges
            if (xMin >= xMax)
                throw new ArgumentException($"xMin ({xMin}) must be less than xMax ({xMax})");
            if (yMin >= yMax)
                throw new ArgumentException($"yMin ({yMin}) must be less than yMax ({yMax})");

            // Convert physical coordinates to pixel indices
            double xRange = source.XMax - source.XMin;
            double yRange = source.YMax - source.YMin;

            int x = (int)Math.Round((xMin - source.XMin) / xRange * (source.XCount - 1));
            int y = (int)Math.Round((yMin - source.YMin) / yRange * (source.YCount - 1));

            int xEnd = (int)Math.Round((xMax - source.XMin) / xRange * (source.XCount - 1));
            int yEnd = (int)Math.Round((yMax - source.YMin) / yRange * (source.YCount - 1));

            int width = xEnd - x + 1;
            int height = yEnd - y + 1;

            // Clamp to valid bounds
            x = Math.Max(0, Math.Min(x, source.XCount - 1));
            y = Math.Max(0, Math.Min(y, source.YCount - 1));
            width = Math.Max(1, Math.Min(width, source.XCount - x));
            height = Math.Max(1, Math.Min(height, source.YCount - y));

            return source.Crop(x, y, width, height);
        }

        /// <summary>
        /// Creates a centered crop of specified dimensions.
        /// </summary>
        /// <typeparam name="T">The data type of matrix elements.</typeparam>
        /// <param name="source">The source MatrixData to crop from.</param>
        /// <param name="width">The width of the centered crop.</param>
        /// <param name="height">The height of the centered crop.</param>
        /// <returns>A new MatrixData instance containing the centered crop.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown if requested dimensions exceed source dimensions.
        /// </exception>
        public static MatrixData<T> CropCenter<T>(this MatrixData<T> source, int width, int height)
            where T : unmanaged
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            if (width > source.XCount || height > source.YCount)
                throw new ArgumentException(
                    $"Crop dimensions ({width}x{height}) exceed source dimensions ({source.XCount}x{source.YCount})");

            int x = (source.XCount - width) / 2;
            int y = (source.YCount - height) / 2;

            return source.Crop(x, y, width, height);
        }

        
    }

}
