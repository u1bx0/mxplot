using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// Manages the multi-dimensional coordinate system for the linear matrix data
    /// and synchronizes the linear ActiveIndex with individual axis indices.
    /// </summary>
    /// <remarks>
    /// <b>Important Note on Axis Order:</b><br/>
    /// The order of axes passed to the constructor defines the dimension hierarchy (memory layout).
    /// The <b>first axis</b> provided is treated as the <b>innermost dimension</b> (fastest-varying index / stride = 1).
    /// Subsequent axes represent outer dimensions with progressively larger strides.
    /// <para>
    /// Example: <c>new DimensionStructure(md, axisZ, axisTime)</c> implies that the Z-index increments
    /// first as the linear index increases (e.g., [Z0,T0] -> [Z1,T0] -> ...).
    /// </para>
    /// </remarks>
    public class DimensionStructure: IDisposable
    {
        private readonly IMatrixData _md;

        /// <summary>
        /// Used for preventing recursive updates between Axis index changes and MatrixData ActiveIndex changes.
        /// 0 means no update in progress.
        /// </summary>
        private int _isUpdating = 0; 

        private readonly List<Axis> _axisList = [];

        /// <summary>
        ///  The number of axis elements to skip to move to the next index in each axis.
        /// </summary>
        private int[] _strides;

        
        /// <summary>
        /// Determines whether the collection contains an axis with the specified name.
        /// </summary>
        /// <param name="axisName">The name of the axis to locate. The comparison is case-insensitive.</param>
        /// <returns>true if an axis with the specified name exists in the collection; otherwise, false.</returns>
        public bool Contains(string axisName) => _axisList.Exists(a => string.Equals(a.Name, axisName, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Determines whether the collection contains the specified axis.
        /// </summary>
        /// <param name="axis">The axis to locate in the collection.</param>
        /// <returns>true if the specified axis is found in the collection; otherwise, false.</returns>
        public bool Contains(Axis axis) => _axisList.Contains(axis);

        /// <summary>
        /// Evaluates the length of the specified axis by name. IMPORTANT: If the axis does not exist, returns 1.
        /// </summary>
        /// <param name="axisName">The axisName is case-insensitive.</param>
        /// <returns></returns>
        public int GetLength(string axisName) => Contains(axisName) ? this[axisName]!.Count : 1;

        /// <summary>
        /// Evaluates the length of the specified axis. IMPORTANT: If the axis does not exist, returns 1.
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        public int GetLength(Axis axis) => Contains(axis) ? axis.Count : 1;

        /// <summary>
        /// Gets the axis at the specified index. The index is zero-based and corresponds to the order of the axes. If the index is out of range, an IndexOutOfRangeException is thrown.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public Axis this[int index] => _axisList[index];

        /// <summary>
        /// Gets the axis with the specified name. The search is case-insensitive. If no matching axis is found, returns null.
        /// </summary>
        /// <param name="name"></param>
        /// <returns></returns>
        public Axis? this[string name] => _axisList.Find(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Returns the index corresponding to the specified axis positions within the multidimensional structure.
        /// </summary>
        /// <param name="coords">An array of tuples, each containing an axis name and its associated index. Specifies the position along each
        /// axis to retrieve the corresponding index. If empty, the method returns the current active index.</param>
        /// <returns>The index representing the position in the multidimensional structure for the specified axis indices. If no
        /// axis indices are provided, returns the active index.</returns>
        /// <exception cref="ArgumentException">Thrown if an axis name specified in <paramref name="coords"/> does not exist in the structure.</exception>
        public int At(params (string AxisName, int Index)[] coords)
        {
            
            if (coords.Length == 0)
            {
                Debug.WriteLine("[DimensionStructure.At] indexer is empty.");
                return _md.ActiveIndex;
            }

            Span<int> pos = stackalloc int[AxisCount];
            //Index array pointing to ActiveIndex in the entire frames.
            CopyAxisIndicesTo(pos, _md.ActiveIndex);

            foreach (var item in coords)
            {
                if(!Contains(item.AxisName))
                    throw new ArgumentException($"Axis '{item.AxisName}' does not exist.");
                pos[GetAxisOrder(item.AxisName)] = item.Index;
            }
            return GetFrameIndexFrom(pos);
        }

        /// <summary>
        /// Getsthe array of axes (as shallow copies) defined in this dimension structure. 
        /// </summary>
        public IReadOnlyList<Axis> Axes => _axisList;

        public int AxisCount => _axisList.Count;

        /// <summary>
        /// Initializes with no specific axis, but if FrameCount > 1, a single "Frame" axis is created.
        /// </summary>
        public DimensionStructure(IMatrixData md)
        {
            _md = md;
            if (_md.FrameCount > 1)
                RegisterAxes(new Axis(_md.FrameCount, 0, _md.FrameCount - 1, "Frame"));
            else
                _strides = Array.Empty<int>();
        }

        public DimensionStructure(IMatrixData md, params Axis[] axes)
        {
            if (md is null || axes is null) //not null
                throw new ArgumentNullException();

            _md = md;
            _md.ActiveIndexChanged += ActiveIndex_Changed;
            //重複チェック：その場合は例外を投げる
            //WhereでもどってくるのはGroupingオブジェクト
            var duplicated = axes.GroupBy(axis => axis.Name)
                .Where(name => name.Count() > 1).Select(grp => grp.Key)
                .ToList();
            if (duplicated.Count > 0)
            {
                var sb = new StringBuilder("Duplication of axis names: ");
                foreach (var d in duplicated)
                {
                    sb.Append(d).Append(" ");
                }
                //Nameでグループ化したときに要素数が1より大きいと、重複があるということ
                throw new ArgumentException(sb.ToString());
            }

            RegisterAxes(axes);
        }

        /// <summary>
        /// The reason for implementing IDisposable is to unregister event handlers to prevent memory leaks.
        /// </summary>
        public void Dispose()
        {
            _md.ActiveIndexChanged -= ActiveIndex_Changed;
            foreach (var axis in _axisList)
            {
                axis.IndexChanged -= AxisIndex_Changed;
            }
            _axisList.Clear();
        }

        [MemberNotNull(nameof(_strides))]
        private void RegisterAxes(params Axis[] axes)
        {
            
            _axisList.Clear();

            // ストライド配列を確保
            _strides = new int[axes.Length];
            
            
            int currentStride = 1;

            // ※重要: 提示コードの論理だと axis[0] が最も頻繁に変わる(Innermost)軸です。
            // その順序に合わせてストライドを計算します。
            for (int i = 0; i < axes.Length; i++)
            {
                var axis = axes[i];

                _axisList.Add(axis);
                _strides[i] = currentStride; // ストライドをキャッシュ

                axis.IndexChanged += AxisIndex_Changed;

                currentStride *= axis.Count;
            }

            // 総数チェック
            if (currentStride != _md.FrameCount)
                throw new ArgumentException($"Total count mismatch. Expected {_md.FrameCount}, but axes product is {currentStride}");

        }

        /// <summary>
        /// MatrixDataのActiveIndexが変化した->各軸のIndexを更新する必要がある
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ActiveIndex_Changed(object? sender, EventArgs e)
        {
            if (_isUpdating == 1 || _axisList.Count == 0) //自分自身がMatrixDataのAcitveIndexを更新している場合はスキップ
                return;

            UpdateAxisIndicesFromFrameIndex(_md.ActiveIndex);
        }
        /// <summary>
        /// どれかのAxisのIndexが変化した -> MatrixDataのActiveIndexを更新する必要がある
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void AxisIndex_Changed(object? sender, EventArgs e)
        {
            if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0 
                || _axisList.Count == 0)
                return;//自分自身がAxisのIndexを更新している場合はスキップ
            
            //Here: _isUpdating = 1;

            try
            {
                int index = ToFrameIndex();
                _md.ActiveIndex = index;
            }
            finally
            {
                Interlocked.Exchange(ref _isUpdating, 0);
            }
        }

        /// <summary>
        /// MatrixDataのActiveIndexが変化に同期して、各軸のactiveなIndexを更新する
        /// </summary>
        internal void UpdateAxisIndicesFromFrameIndex(int frameIndex)
        {
            if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0
               || _axisList.Count == 0)
                return;//自分自身がAxisのIndexを更新している場合はスキップ

            //Here: _isUpdating = 1;
            try
            {
                var indeces = GetAxisIndices(frameIndex);
                for (int i = 0; i < indeces.Length; i++)
                {
                    //各軸のActiveなIndexを更新->イベント通知されるがスキップ
                    _axisList[i].Index = indeces[i];
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isUpdating, 0);
            }
        }

        /// <summary>
        /// indexの配列を指定してActiveなindexを直接変更する
        /// </summary>
        /// <param name="indeces"></param>
        public void SetIndeces(params int[] indeces)
        {
            if (indeces.Length != _axisList.Count)
                throw new ArgumentException("Invalid lenght of indeces!");
            var index = GetFrameIndexFrom(indeces);
            //内部で更新する
            UpdateAxisIndicesFromFrameIndex(index);
        }
           

        #region Utilities for accessing each axis index in the entire frames



        /// <summary>
        /// 現在のSeries axis位置に対して，指定したAxisをindexにした場合のseries indexを返す
        /// </summary>
        /// <param name="axis">このSeriesに登録されているAxisオブジェクト</param>
        /// <param name="index"></param>
        /// <returns></returns>
        public int GetFrameIndexFor(Axis axis, int index)
        {
            if (axis == null)
            {
                Debug.WriteLine("[Series.GetIndexInEntireSeriesFor] axis is null");
                return 0;
            }

            if (index < 0 || index >= axis.Count)
                throw new IndexOutOfRangeException("Invalid index for " + axis + "index = " + index);

            if (!_axisList.Contains(axis))
                throw new ArgumentException(axis + " does not exist in this series.");

            int[] indeces = GetAxisIndices();
            int pos = _axisList.IndexOf(axis);

            indeces[pos] = index;
            return GetFrameIndexFrom(indeces);
        }

        /// <summary>
        /// キー文字列を持つ軸がindexの場合のseries indexを返す
        /// </summary>
        /// <param name="axisKey"></param>
        /// <param name="index"></param>
        /// <returns></returns>
        public int GetFrameIndexFor(string axisKey, int index)
        {
            var axis = this[axisKey];
            if (axis == null)
                return 0;

            return GetFrameIndexFor(axis, index);
        }

        /// <summary>
        /// 指定した軸がSeries軸の何番目かを返す = axisList.IndexOf(axis)
        /// Axis[0] が最初の軸
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        public int GetAxisOrder(Axis axis)
        {
            return _axisList.IndexOf(axis);
        }

        public int GetAxisOrder(string axisName)
        {
            if(!Contains(axisName))
                throw new ArgumentException($"Axis '{axisName}' does not exist.");
            return GetAxisOrder(this[axisName]!);
        }

        /// <summary>
        /// Represents a transient collection of axis values for a specific frame, supporting deconstruction.
        /// </summary>
        /// <remarks>
        /// This struct is designed to be short-lived and avoids additional memory allocation when wrapping existing data.
        /// It enables tuple-like syntax for retrieving coordinates:
        /// <code>
        /// // Example usage:
        /// var (x, y) = matrix.GetAxisValues(frameIndex);
        /// </code>
        /// </remarks>
        public readonly ref struct AxisValues
        {
            private readonly ReadOnlySpan<double> _values;

            public AxisValues(ReadOnlySpan<double> values) { _values = values; }

            public void Deconstruct(out double v0, out double v1)
            {
                v0 = _values.Length > 0 ? _values[0] : 0;
                v1 = _values.Length > 1 ? _values[1] : 0;
            }
            public void Deconstruct(out double v0, out double v1, out double v2)
            {
                v0 = _values.Length > 0 ? _values[0] : 0;
                v1 = _values.Length > 1 ? _values[1] : 0;
                v2 = _values.Length > 2 ? _values[2] : 0;
            }
            public void Deconstruct(out double v0, out double v1, out double v2, out double v3)
            {
                v0 = _values.Length > 0 ? _values[0] : 0;
                v1 = _values.Length > 1 ? _values[1] : 0;
                v2 = _values.Length > 2 ? _values[2] : 0;
                v3 = _values.Length > 3 ? _values[3] : 0;
            }
        }

        /// <summary>
        /// Represents a transient collection of axis indices for a specific frame.
        /// Supports tuple deconstruction.
        /// </summary>
        public readonly ref struct AxisIndices
        {
            private readonly ReadOnlySpan<int> _indices;

            public AxisIndices(ReadOnlySpan<int> indices) { _indices = indices; }

            public void Deconstruct(out int i0, out int i1)
            {
                i0 = _indices.Length > 0 ? _indices[0] : 0;
                i1 = _indices.Length > 1 ? _indices[1] : 0;
            }

            public void Deconstruct(out int i0, out int i1, out int i2)
            {
                i0 = _indices.Length > 0 ? _indices[0] : 0;
                i1 = _indices.Length > 1 ? _indices[1] : 0;
                i2 = _indices.Length > 2 ? _indices[2] : 0;
            }

            public void Deconstruct(out int i0, out int i1, out int i2, out int i3)
            {
                i0 = _indices.Length > 0 ? _indices[0] : 0;
                i1 = _indices.Length > 1 ? _indices[1] : 0;
                i2 = _indices.Length > 2 ? _indices[2] : 0;
                i3 = _indices.Length > 3 ? _indices[3] : 0;
            }

            public int this[int index] =>
                (uint)index < (uint)_indices.Length ? _indices[index] : 0;

            public int Length => _indices.Length;
        }

        /// <summary>
        /// Retrieves the values of all axes for the specified frame index.
        /// </summary>
        /// <param name="frameIndex">
        /// The target frame index. Specify -1 to use the current active frame index.
        /// </param>
        /// <returns>
        /// An <see cref="AxisValues"/> structure containing the values of all axes at the specified frame.
        /// </returns>
        /// <remarks>
        /// The returned structure supports tuple-like deconstruction for convenience.
        /// <code>
        /// // Example usage:
        /// var (c, z) = matrix.GetAxisValuesStruct(i);
        /// var (c, z, t, f) = matrix.GetAxisValuesStruct(i);
        /// </code>
        /// <para>
        /// <b>Limitation:</b> Deconstruction is supported for a maximum of <b>4 variables</b>. 
        /// To access more axes, use <see cref="GetAxisValues(int)"/>.
        /// </para>
        /// </remarks>
        public AxisValues GetAxisValuesStruct(int frameIndex = -1)
        {
            return new AxisValues(GetAxisValues(frameIndex));
        }

        /// <summary>
        /// Returns an array containing the values of all axes for the specified frame index.
        /// </summary>
        /// <param name="frameIndex">The index of the frame from which to retrieve axis values. Specify -1 to use the current frame.</param>
        /// <returns>An array of doubles representing the values of each axis at the specified frame. The array will contain one
        /// value per axis.</returns>
        public double[] GetAxisValues(int frameIndex = -1)
        {
            int count = _axisList.Count;
            Span<int> buffer = count < 256 ? stackalloc int[count] : new int[count];
            CopyAxisIndicesTo(buffer, frameIndex);
            var values = new double[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = _axisList[i].ValueAt(buffer[i]);
            }
            return values;
        }

        /// <summary>
        /// Populates the specified buffer with axis values for the given frame index.
        /// </summary>
        /// <param name="destination">A span of doubles that receives the axis values. The span must have a length greater than or equal to the
        /// number of axes.</param>
        /// <param name="frameIndex">The index of the frame for which to retrieve axis values. Specify -1 to use the current frame.</param>
        /// <exception cref="ArgumentException">Thrown if <paramref name="destination"/> is smaller than the number of axes.</exception>
        public void CopyAxisValuesTo(Span<double> destination, int frameIndex = -1)
        {
            if (destination.Length < _axisList.Count)
                throw new ArgumentException("Buffer is too small.");

            // ここで stackalloc を使えば、内部計算もヒープ確保ゼロ！
            Span<int> indicesBuffer = stackalloc int[_axisList.Count];
            CopyAxisIndicesTo(indicesBuffer, frameIndex);
            for (int i = 0; i < _axisList.Count; i++)
            {
                destination[i] = _axisList[i].ValueAt(indicesBuffer[i]);
            }
        }

        /// <summary>
        /// Gets the axis indices for the specified frame as an AxisIndices structure.
        /// </summary>
        /// <remarks>
        /// The returned structure supports tuple-like deconstruction for convenience.
        /// <code>
        /// // Example usage:
        /// var (iz, it) = matrix.GetAxisIndiciesStruct(i); //iz and it are the indices for Z and T axes 
        /// var (it, ic, iz) = matrix.GetAxisIndicesStruct(i);
        /// </code>
        /// <para>
        /// <b>Limitation:</b> Deconstruction is supported for a maximum of <b>4 variables</b>. 
        /// To access more axes, use <see cref="GetAxisIndices(int)"/>.
        /// </para>
        /// </remarks>
        /// <param name="frameIndex">The zero-based index of the frame for which to retrieve axis indices. Specify -1 to use the current frame.</param>
        /// <returns>An AxisIndices structure containing the axis indices for the specified frame.</returns>
        public AxisIndices GetAxisIndicesStruct(int frameIndex = -1)
        {
            // 配列版を呼んでラップする
            return new AxisIndices(GetAxisIndices(frameIndex));
        }

        /// <summary>
        /// Returns an array containing the indices of all axes for the specified frame.
        /// </summary>
        /// <param name="frameIndex">The index of the frame for which to retrieve axis indices. Specify -1 to use the current frame.</param>
        /// <returns>An array of integers representing the indices of all axes for the specified frame. The array will be empty
        /// if there are no axes.</returns>
        public int[] GetAxisIndices(int frameIndex = -1)
        {
            var buffer = new int[_axisList.Count];
            // Span版の Copy を呼ぶことで配列への暗黙変換を利用
            CopyAxisIndicesTo(buffer, frameIndex);
            return buffer;
        }

        /// <summary>
        /// Copies the axis indices for the specified frame to the provided buffer.
        /// </summary>
        /// <param name="buffer">The array that receives the axis indices. Must be large enough to hold all axis indices.</param>
        /// <param name="frameIndex">The index of the frame from which to copy axis indices. Specify -1 to use the current frame.</param>
        public void CopyAxisIndicesTo(int[] buffer, int frameIndex = -1)
        {
            // 配列版は Span版へ丸投げするだけ (ロジック重複排除)
            CopyAxisIndicesTo(buffer.AsSpan(), frameIndex);
        }

        /// <summary>
        /// Copies the axis indices for the specified frame into the provided buffer.
        /// </summary>
        /// <param name="buffer">The destination span that receives the axis indices. Must have a length greater than or equal to the number
        /// of axes.</param>
        /// <param name="frameIndex">The index of the frame for which to retrieve axis indices. If set to -1, the active frame index is used.</param>
        /// <exception cref="ArgumentException">Thrown if the length of <paramref name="buffer"/> is less than the number of axes.</exception>
        public void CopyAxisIndicesTo(Span<int> buffer, int frameIndex = -1)
        {
            if (frameIndex == -1) frameIndex = _md.ActiveIndex; // ここは適宜修正
            if (buffer.Length < _axisList.Count)
                throw new ArgumentException("Destination span is too short.");

            // ロジック本体 
            for (int i = 0; i < _axisList.Count; i++)
            {
                buffer[i] = (frameIndex / _strides[i]) % _axisList[i].Count;
            }
        }

        /// <summary>
        /// index配列で指定したindexからシリーズ中のIndexに変換する
        /// </summary>
        /// <param name="indeces"></param>
        /// <returns></returns>
        public int GetFrameIndexFrom(int[] indeces)
        {
            if(_axisList.Count == 0)
                return 0;

            if(indeces.Length != _axisList.Count)
                throw new ArgumentException("Invalid lenght of indeces!");

            int newIndex = 0;
            // ループ内で掛け算を累積するのではなく、事前計算済みのStrideと内積をとる
            for (int i = 0; i < _axisList.Count; ++i)
            {
                newIndex += indeces[i] * _strides[i];
            }
            return newIndex;
        }

        public int GetFrameIndexFrom(Span<int> indeces)
        {
            if (_axisList.Count == 0)
                return 0;

            if (indeces.Length != _axisList.Count)
                throw new ArgumentException("Invalid lenght of indeces!");

            int newIndex = 0;
            // ループ内で掛け算を累積するのではなく、事前計算済みのStrideと内積をとる
            for (int i = 0; i < _axisList.Count; ++i)
            {
                newIndex += indeces[i] * _strides[i];
            }
            return newIndex;
        }

        /// <summary>
        ///  Gets the list of frame indices corresponding to a slice where the specified axis has a fixed index value.
        /// </summary>
        /// <param name="axisKey"></param>
        /// <param name="fixedIndex"></param>
        /// <returns></returns>
        public List<int> GetIndicesForSlice(string axisKey, int fixedIndex)
        {
            int totalFrames = _md.FrameCount;

            var targetAxis = this[axisKey]; 
            if (targetAxis == null)
                throw new ArgumentException($"Axis with key '{axisKey}' not found.");
            int axisOrder = _axisList.IndexOf(targetAxis);

            var indices = new List<int>();

            // バッファ再利用（GC抑制）
            int[] buffer = new int[AxisCount];

            for (int i = 0; i < totalFrames; i++)
            {
                // 高速なインデックス分解
                CopyAxisIndicesTo(buffer, i);

                // 指定軸の値が合致するものだけをピックアップ
                if (buffer[axisOrder] == fixedIndex)
                {
                    indices.Add(i);
                }
            }
            return indices;
        }

        /* 
         * This method has been removed to maintain the logic's simplicity.
        /// <summary>
        /// 
        /// Generates a two-dimensional table mapping each frame to its corresponding axis indices based on the
        /// configured strides and axis counts. 
        /// If pixel-wise access across all frames is required, consider using this precomputed table for efficiency.
        /// </summary>
        /// <remarks>The returned table can be used to efficiently look up which axis index corresponds to
        /// a given frame and axis combination. 
        /// <code>
        ///  var table = dimensionStructure.CreateAxisIndicesTable();
        ///  int c = table[frameIndex, cAxisOrder]; // Get the index for axis 'C' at the specified frame
        ///  int t = table[frameIndex, tAxisOrder]; // Get the index for axis 'T' at the specified frame
        ///  int z = table[frameIndex, zAxisOrder]; // Get the index for axis 'Z' at the specified frame
        /// </code>
        /// </remarks>
        /// <returns> The value at [frame, axis] indicates the index in the axis for that frame.</returns>
        public int[,] CreateAxisIndicesTable()
        {
            var table = new int[_md.FrameCount, _axisList.Count];
            for (int frame = 0; frame < _md.FrameCount; frame++)
            {
                for (int axis = 0; axis < _axisList.Count; axis++)
                {
                    table[frame, axis] = (frame / _strides[axis]) % _axisList[axis].Count;
                }
            }
            return table;
        }
        */

        /// <summary>
        /// Creates a new array of Axis objects excluding the specified axis by name.
        /// </summary>
        /// <remarks
        /// <code>
        /// return _axisList
        ///   .Where(a => !string.Equals(a.Name, axisNameToRemove, StringComparison.OrdinalIgnoreCase))
        ///   .Select(a => a.Clone()) // Indexはリセット
        ///   .ToArray();
        /// </code>
        /// </remarks>
        /// <param name="axisToRemove"></param>
        /// <returns></returns>
        public Axis[] CreateAxesWithout(string axisNameToRemove)
        {
            // 指定軸以外を抽出
            var newAxes = _axisList
                .Where(a => !string.Equals(a.Name, axisNameToRemove, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Clone()) // Indexはリセット
                .ToArray();

            // ※もし「軸が1つもなくなる（0次元スカラーになる）」場合は
            // 空配列を返すか、エラーにするか仕様を決める必要があります。
            // ここでは空配列（長さ0）を許容します。

            return newAxes;
        }

        /// <summary>
        /// 現在のAxis配列からシリーズ中のindexに変換する
        /// </summary>
        /// <returns></returns>
        private int ToFrameIndex()
        {
            int index = 0;
            for (int i = 0; i < _axisList.Count; i++)
            {
                index += _axisList[i].Index * _strides[i];
            }
            return index;
        }

        #endregion

    }

   

}
