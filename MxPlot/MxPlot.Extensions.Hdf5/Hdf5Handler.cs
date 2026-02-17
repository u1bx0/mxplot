using MxPlot.Core;
using PureHDF;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MxPlot.Extensions.Hdf5
{
    /// <summary>
    /// MatrixData を HDF5 形式でエクスポートするユーティリティ
    /// PureHDF v2 の超シンプルAPIを使用
    /// 
    /// HDF5 構造:
    /// /matrix_data/
    ///     data (Dataset)      - 実データ [Y, X] または [Frame, Y, X]
    ///     Attributes:
    ///       - XMin, XMax, YMin, YMax
    ///       - XCount, YCount, FrameCount
    ///       - XUnit, YUnit
    ///       - ValueType
    /// 
    /// Python (h5py) での読み込み例:
    ///   import h5py
    ///   with h5py.File('data.h5', 'r') as f:
    ///       data = f['/matrix_data/data'][:]
    ///       xmin = f['/matrix_data'].attrs['XMin']
    ///       print(f"Data shape: {data.shape}, X range: [{xmin}, {f['/matrix_data'].attrs['XMax']}]")
    ///       # ルート属性を表示
    ///       for key, value in f.attrs.items():
    ///           if 'IMAGE' in key or 'DISPLAY' in key or 'DIMENSION' in key:
    ///               print(f"{key}: {value}")
    /// </summary>
    public static class Hdf5Handler
    {
        /// <summary>
        /// MatrixData を HDF5 ファイルにエクスポート
        /// </summary>
        public static void Save<T>(string filePath, MatrixData<T> data, string groupPath = "matrix_data", bool flipY = true)
            where T : unmanaged
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));

            // 既存ファイルを削除
            if (File.Exists(filePath)) File.Delete(filePath);

            var file = new H5File();

            try
            {
                // 書き込み前に「並び替えインデックス」と「ソート済み軸リスト」を生成
                // これにより、データの実体(Array)と属性(Attributes)の順序を一致させます。
                var (reorderedIndices, sortedAxes) = GetReorderedIndices(data);

                var group = new H5Group();
                // 1. データ配列を書き込み
                WriteDataArray(group, groupPath, data, sortedAxes, reorderedIndices, flipY);
                file[groupPath] = group;

                // 2. 属性を書き込み
                // ルート属性として保存
                file.Attributes[$"{groupPath}_Creator"] = "MxPlot.External.HDF5.Hdf5Handler";
                file.Attributes[$"{groupPath}_Version"] = "1.0";
                file.Attributes[$"{groupPath}_CreatedAt"] = DateTime.Now.ToString("o");

                //groupPathにMatrixData情報を書き込む
                WriteAttributes(group, groupPath, data, sortedAxes, flipY);
                
                // 3. ファイルに保存
                file.Write(filePath);
            }
            catch (Exception ex)
            {
                throw new Hdf5ExportException($"Failed to export to HDF5: {ex.Message}", ex);
            }
        }

        #region Load, GetFileInfo は未実装
        public static MatrixData<T> Load<T>(string filePath, string groupPath = "matrix_data") where T : unmanaged
        {
            throw new NotImplementedException("Import functionality is under development.");
        }

        public static Hdf5FileInfo GetFileInfo(string filePath, string groupPath = "matrix_data")
        {
            throw new NotImplementedException("GetFileInfo is under development.");
        }
        #endregion

        #region Private Helper Methods

        /// <summary>
        /// 【変更点】軸の順序を再計算するメソッド
        /// </summary>
        private static (int[] reorderedIndices, List<Axis> sortedAxes) GetReorderedIndices<T>(MatrixData<T> data) where T : unmanaged
        {
            // 【重要修正】IReadOnlyListにはIndexOfがないため、ToList()でリスト化します
            var originalAxes = data.Dimensions.Axes.ToList();

            // 1. 軸を標準的なHDF5順序にソート
            var sortedAxes = originalAxes
                .OrderBy(a => GetAxisPriority(a))
                .ToList();

            int frameCount = data.FrameCount;
            int[] reorderedIndices = new int[frameCount];

            // 2. カウンター準備
            int[] currentCounters = new int[sortedAxes.Count];
            int[] mapSortedToOriginal = new int[sortedAxes.Count];
            for (int i = 0; i < sortedAxes.Count; i++)
            {
                mapSortedToOriginal[i] = originalAxes.IndexOf(sortedAxes[i]);
            }

            int[] originalIndexer = new int[originalAxes.Count];

            // 3. ループ処理
            for (int i = 0; i < frameCount; i++)
            {
                // A. カウンター -> 元の座標
                for (int k = 0; k < sortedAxes.Count; k++)
                {
                    int originalPos = mapSortedToOriginal[k];
                    originalIndexer[originalPos] = currentCounters[k];
                }

                // B. 元のインデックス取得
                int sourceIndex = data.Dimensions.GetFrameIndexAt(originalIndexer);
                reorderedIndices[i] = sourceIndex;

                // C. カウンターインクリメント (Innermost loop first)
                for (int k = sortedAxes.Count - 1; k >= 0; k--)
                {
                    currentCounters[k]++;
                    if (currentCounters[k] < sortedAxes[k].Count)
                        break;
                    else
                        currentCounters[k] = 0;
                }
            }

            return (reorderedIndices, sortedAxes);
        }

        /// <summary>
        /// 
        /// 
        /// </summary>
        /// <param name="axis"></param>
        /// <returns></returns>
        private static int GetAxisPriority(Axis axis)
        {
            if (axis is FovAxis) return 0;
            if (ContainsIgnoreCase(axis.Name, "FOV")) return 0;
            if (ContainsIgnoreCase(axis.Name, "Time") || ContainsIgnoreCase(axis.Name, "Frame") || ContainsIgnoreCase(axis.Name, "T")) return 80;
            if (ContainsIgnoreCase(axis.Name, "Z") || ContainsIgnoreCase(axis.Name, "Depth") || ContainsIgnoreCase(axis.Name, "Slice")) return 90;
            if (ContainsIgnoreCase(axis.Name, "Channel") || ContainsIgnoreCase(axis.Name, "Ch") || ContainsIgnoreCase(axis.Name, "Wavelength")) return 100;
            return 50;
        }

        private static bool ContainsIgnoreCase(string source, string toCheck)
        {
            if (string.IsNullOrEmpty(source)) return false;
            return source.IndexOf(toCheck, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 【修正版】制約をstructに戻し、Unsafeクラスを使ってポインタ操作を実現します。
        /// </summary>
        private static unsafe void WriteDataArray<T>(H5Group group, string groupPath, MatrixData<T> data,
                                                List<Axis> sortedAxes, int[] reorderedIndices, bool flipY)
            where T : unmanaged
        {
            

            // 1. N次元配列の次元サイズを決定
            long[] dimensions = new long[sortedAxes.Count + 2];
            for (int i = 0; i < sortedAxes.Count; i++)
            {
                dimensions[i] = sortedAxes[i].Count;
            }
            dimensions[dimensions.Length - 2] = data.YCount;
            dimensions[dimensions.Length - 1] = data.XCount;

            // 2. 多次元配列を確保
            var multiDimArray = Array.CreateInstance(typeof(T), dimensions);

            // 3. 配列をPinしてポインタを取得
            GCHandle handle = GCHandle.Alloc(multiDimArray, GCHandleType.Pinned);

            try
            {
                byte* dstBasePtr = (byte*)handle.AddrOfPinnedObject();

                // サイズ計算
                int pixelSize = Unsafe.SizeOf<T>();
                long rowSizeBytes = (long)data.XCount * pixelSize;
                long frameSizeBytes = rowSizeBytes * data.YCount;

                // 4. 書き込みループ
                for (int i = 0; i < data.FrameCount; i++)
                {
                    byte* dstFramePtr = dstBasePtr + ((long)i * frameSizeBytes);

                    // 元データの取得
                    var srcFrame = data.GetArray(reorderedIndices[i]);

                    // 【重要】Tがunmanaged制約でないため、fixed (T* p = srcFrame) は使えません。
                    // 代わりに Unsafe.As で強引に byte* として扱います。
                    // これによりジェネリック型制約のエラーを回避しつつ高速コピーが可能です。
                    if (srcFrame.Length > 0)
                    {
                        // 配列の先頭要素への参照をbyte型として取得
                        ref byte srcRef = ref Unsafe.As<T, byte>(ref srcFrame[0]);

                        // その参照を固定してポインタ化
                        fixed (byte* srcBasePtr = &srcRef)
                        {
                            for (int y = 0; y < data.YCount; y++)
                            {
                                int srcY = flipY ? (data.YCount - 1 - y) : y;

                                byte* srcRowPtr = srcBasePtr + ((long)srcY * rowSizeBytes);
                                byte* dstRowPtr = dstFramePtr + ((long)y * rowSizeBytes);

                                // 高速メモリコピー
                                Buffer.MemoryCopy(srcRowPtr, dstRowPtr, rowSizeBytes, rowSizeBytes);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (handle.IsAllocated)
                    handle.Free();
            }

            group["data"] = multiDimArray;
            AddImageSpecAttributesToGroup(group, data, flipY);
            
        }
        

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="group"></param>
        /// <param name="data"></param>
        /// <param name="flipY"></param>
        private static void AddImageSpecAttributesToGroup<T>(H5Group group, MatrixData<T> data, bool flipY) where T : unmanaged
        {
            group.Attributes["CLASS"] = "IMAGE";
            group.Attributes["IMAGE_VERSION"] = "1.2";
            group.Attributes["IMAGE_SUBCLASS"] = "IMAGE_GRAYSCALE";
            group.Attributes["DISPLAY_ORIGIN"] = flipY ? "UL" : "LL"; //flipY=trueなら左上原点-> MatrixDataは左下原点
            group.Attributes["IMAGE_WIDTH"] = data.XCount;
            group.Attributes["IMAGE_HEIGHT"] = data.YCount;
            if (data.FrameCount > 1) group.Attributes["IMAGE_FRAMES"] = data.FrameCount;
            AddPhysicalScaleAttributes(group, data, flipY);
            AddValueRangeAttributes(group, data);
            group.Attributes["IMAGE_WHITE_IS_ZERO"] = 0;
            group.Attributes["INTERLACE_MODE"] = "INTERLACE_PIXEL";
        }

        private static void AddPhysicalScaleAttributes<T>(H5Object groupOrDataset, MatrixData<T> data, bool flipY) where T : unmanaged
        {
            double pixelSizeX = data.XRange / data.XCount;
            double pixelSizeY = data.YRange / data.YCount;
            // 注意: 単純な3D表示用。多次元の詳細スケールはWriteDimensionAttributesで処理。
            if (data.FrameCount > 1)
                groupOrDataset.Attributes["element_size_um"] = new double[] { 1.0, pixelSizeY, pixelSizeX };
            else
                groupOrDataset.Attributes["element_size_um"] = new double[] { pixelSizeY, pixelSizeX };

            if (!string.IsNullOrEmpty(data.XUnit)) groupOrDataset.Attributes["UNIT_X"] = data.XUnit;
            if (!string.IsNullOrEmpty(data.YUnit)) groupOrDataset.Attributes["UNIT_Y"] = data.YUnit;
            groupOrDataset.Attributes["SCALE_X_MIN"] = data.XMin;
            groupOrDataset.Attributes["SCALE_X_MAX"] = data.XMax;
            groupOrDataset.Attributes["SCALE_Y_MIN"] = data.YMin;
            groupOrDataset.Attributes["SCALE_Y_MAX"] = data.YMax;
        }

        private static void AddValueRangeAttributes<T>(H5Object groupOrDataset, MatrixData<T> data) where T : unmanaged
        {
            var (globalMin, globalMax) = data.GetGlobalMinMaxValues();
            groupOrDataset.Attributes["IMAGE_MINMAXRANGE"] = new double[] { globalMin, globalMax };
            groupOrDataset.Attributes["VALUE_MIN"] = globalMin;
            groupOrDataset.Attributes["VALUE_MAX"] = globalMax;
        }

        /// <summary>
        /// 【変更点】sortedAxes を受け取るように修正
        /// </summary>
        //private static void WriteAttributes<T>(H5File file, string groupPath, MatrixData<T> data, List<Axis> sortedAxes, bool flipY) where T : struct
        private static void WriteAttributes<T>(H5Object groupOrDataset, string groupPath, MatrixData<T> data, List<Axis> sortedAxes, bool flipY) where T : unmanaged
        {
            string prefix = groupPath.TrimStart('/').Replace('/', '_');

            // ... (ここから下の基本属性は変更なし) ...
            groupOrDataset.Attributes[$"{prefix}_XMin"] = data.XMin;
            groupOrDataset.Attributes[$"{prefix}_XMax"] = data.XMax;
            groupOrDataset.Attributes[$"{prefix}_YMin"] = data.YMin;
            groupOrDataset.Attributes[$"{prefix}_YMax"] = data.YMax;
            groupOrDataset.Attributes[$"{prefix}_XCount"] = (double)data.XCount;
            groupOrDataset.Attributes[$"{prefix}_YCount"] = (double)data.YCount;
            groupOrDataset.Attributes[$"{prefix}_FrameCount"] = (double)data.FrameCount;
            groupOrDataset.Attributes[$"{prefix}_XUnit"] = data.XUnit ?? "";
            groupOrDataset.Attributes[$"{prefix}_YUnit"] = data.YUnit ?? "";
            groupOrDataset.Attributes[$"{prefix}_ValueType"] = typeof(T).Name;
            groupOrDataset.Attributes[$"{prefix}_YFlipped"] = flipY;
            groupOrDataset.Attributes[$"{prefix}_CoordinateSystem"] = flipY ? "Image (top-left origin)" : "Mathematical (bottom-left origin)";
            
            // 【変更点】Dimension情報を保存（sortedAxesを渡す）
            WriteDimensionAttributes(groupOrDataset, prefix, sortedAxes);
        }

        /// <summary>
        /// 【変更点】sortedAxes を受け取り、それに基づいてメタデータを記述
        /// </summary>
        //private static void WriteDimensionAttributes(H5File file, string prefix, List<Axis> sortedAxes)
        private static void WriteDimensionAttributes(H5Object groupOrDataset, string prefix, List<Axis> sortedAxes)
        {
            if (sortedAxes == null || sortedAxes.Count == 0)
            {
                groupOrDataset.Attributes[$"{prefix}_HasDimensions"] = false;
                return;
            }

            groupOrDataset.Attributes[$"{prefix}_HasDimensions"] = true;
            groupOrDataset.Attributes[$"{prefix}_DimensionCount"] = (double)sortedAxes.Count;

            // sortedAxes の順序でループ
            for (int i = 0; i < sortedAxes.Count; i++)
            {
                var axis = sortedAxes[i];
                string axisPrefix = $"{prefix}_Dim{i}";

                // 基本的なAxis情報
                groupOrDataset.Attributes[$"{axisPrefix}_Name"] = axis.Name;
                groupOrDataset.Attributes[$"{axisPrefix}_Count"] = (double)axis.Count;
                groupOrDataset.Attributes[$"{axisPrefix}_Min"] = axis.Min;
                groupOrDataset.Attributes[$"{axisPrefix}_Max"] = axis.Max;
                groupOrDataset.Attributes[$"{axisPrefix}_Unit"] = axis.Unit ?? "";
                groupOrDataset.Attributes[$"{axisPrefix}_IsIndexBased"] = axis.IsIndexBased;

                // FovAxisかどうかをチェック
                if (axis is FovAxis fovAxis)
                {
                    groupOrDataset.Attributes[$"{axisPrefix}_IsFovAxis"] = true;
                    WriteFovAxisSpecificInfo(groupOrDataset, axisPrefix, fovAxis);
                }
                else
                {
                    groupOrDataset.Attributes[$"{axisPrefix}_IsFovAxis"] = false;
                }
            }

            IdentifyCommonDimensions(groupOrDataset, prefix, sortedAxes);
        }

        private static void WriteFovAxisSpecificInfo(H5Object groupOrDataset, string axisPrefix, FovAxis fovAxis)
        {
            // ... (変更なし) ...
            groupOrDataset.Attributes[$"{axisPrefix}_TileLayoutX"] = (double)fovAxis.TileLayout.X;
            groupOrDataset.Attributes[$"{axisPrefix}_TileLayoutY"] = (double)fovAxis.TileLayout.Y;
            groupOrDataset.Attributes[$"{axisPrefix}_TileLayoutZ"] = (double)fovAxis.TileLayout.Z;
            //groupOrDataset.Attributes[$"{axisPrefix}_ZIndex"] = (double)fovAxis.ZIndex;

            var origins = fovAxis.Origins;
            var originX = new double[origins.Length];
            var originY = new double[origins.Length];
            var originZ = new double[origins.Length];

            for (int j = 0; j < origins.Length; j++)
            {
                originX[j] = origins[j].X;
                originY[j] = origins[j].Y;
                originZ[j] = origins[j].Z;
            }

            groupOrDataset.Attributes[$"{axisPrefix}_OriginX"] = originX;
            groupOrDataset.Attributes[$"{axisPrefix}_OriginY"] = originY;
            groupOrDataset.Attributes[$"{axisPrefix}_OriginZ"] = originZ;

            groupOrDataset.Attributes[$"{axisPrefix}_OriginCount"] = (double)origins.Length;
        }

        /// <summary>
        /// 【変更点】ソート済みリストを受け取るように変更
        /// </summary>
        private static void IdentifyCommonDimensions(H5Object groupOrDataset, string prefix, List<Axis> axes)
        {
            for (int i = 0; i < axes.Count; i++)
            {
                var axis = axes[i];
                string dimType = "Unknown";

                if (axis is FovAxis)
                {
                    dimType = "FOV";
                }
                else if (ContainsIgnoreCase(axis.Name, "Channel") || ContainsIgnoreCase(axis.Name, "Ch") || ContainsIgnoreCase(axis.Name, "Wavelength"))
                {
                    dimType = "Channel";
                }
                else if (ContainsIgnoreCase(axis.Name, "Time") || ContainsIgnoreCase(axis.Name, "Frame") || ContainsIgnoreCase(axis.Name, "T"))
                {
                    dimType = "Time";
                }
                else if (ContainsIgnoreCase(axis.Name, "Z") || ContainsIgnoreCase(axis.Name, "Depth") || ContainsIgnoreCase(axis.Name, "Stack"))
                {
                    dimType = "Z";
                }

                groupOrDataset.Attributes[$"{prefix}_Dim{i}_Type"] = dimType;
            }
        }

       


        #endregion
    
    }

    /// <summary>
    /// HDF5エクスポート例外
    /// </summary>
    public class Hdf5ExportException : Exception
    {
        public Hdf5ExportException(string message) : base(message) { }
        public Hdf5ExportException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// HDF5ファイル情報
    /// </summary>
    public class Hdf5FileInfo
    {
        public string FilePath { get; set; } = "";
        public string DataType { get; set; } = "";
        public int XCount { get; set; }
        public int YCount { get; set; }
        public int FrameCount { get; set; }
        public double XMin { get; set; }
        public double XMax { get; set; }
        public double YMin { get; set; }
        public double YMax { get; set; }
        public long FileSize { get; set; }

        // Dimension情報
        public bool HasDimensions { get; set; }
        public int DimensionCount { get; set; }
        public string[]? DimensionNames { get; set; }

        public string DimensionsString => FrameCount > 1 
            ? $"{XCount}×{YCount}×{FrameCount}" 
            : $"{XCount}×{YCount}";

        public string FullDimensionsString
        {
            get
            {
                var parts = new List<string> { $"{XCount}×{YCount}" };
                
                if (HasDimensions && DimensionNames != null)
                {
                    parts.Add($"[{string.Join(", ", DimensionNames)}]");
                }
                else if (FrameCount > 1)
                {
                    parts.Add($"×{FrameCount} frames");
                }

                return string.Join(" ", parts);
            }
        }

        public string FileSizeString => FileSize switch
        {
            < 1024 => $"{FileSize} B",
            < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{FileSize / (1024.0 * 1024):F1} MB",
            _ => $"{FileSize / (1024.0 * 1024 * 1024):F2} GB"
        };
    }
}
