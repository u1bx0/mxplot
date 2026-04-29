using MxPlot.Core;
using PureHDF;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MxPlot.Extensions.Hdf5
{
    /// <summary>
    /// Utility for exporting MatrixData in HDF5 format using the PureHDF v2 simple API.
    ///
    /// HDF5 structure:
    /// /matrix_data/
    ///     data (Dataset)      - raw data [Y, X] or [Frame, Y, X]
    ///     Attributes:
    ///       - XMin, XMax, YMin, YMax
    ///       - XCount, YCount, FrameCount
    ///       - XUnit, YUnit
    ///       - ValueType
    ///
    /// Example: reading with Python (h5py):
    ///   import h5py
    ///   with h5py.File('data.h5', 'r') as f:
    ///       data = f['/matrix_data/data'][:]
    ///       xmin = f['/matrix_data'].attrs['XMin']
    ///       print(f"Data shape: {data.shape}, X range: [{xmin}, {f['/matrix_data'].attrs['XMax']}]")
    ///       # Print root attributes
    ///       for key, value in f.attrs.items():
    ///           if 'IMAGE' in key or 'DISPLAY' in key or 'DIMENSION' in key:
    ///               print(f"{key}: {value}")
    /// </summary>
    public static class Hdf5Handler
    {
        /// <summary>
        /// Exports MatrixData to an HDF5 file.
        /// </summary>
        public static void Save<T>(string filePath, MatrixData<T> data, string groupPath = "matrix_data", bool flipY = true)
            where T : unmanaged
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("File path cannot be empty", nameof(filePath));

            // Delete existing file
            if (File.Exists(filePath)) File.Delete(filePath);

            var file = new H5File();

            try
            {
                // Before writing, compute the reordered indices and sorted axis list
                // so that the data array (Array) and attributes (Attributes) share the same ordering.
                var (reorderedIndices, sortedAxes) = GetReorderedIndices(data);

                var group = new H5Group();
                // 1. Write data array
                WriteDataArray(group, groupPath, data, sortedAxes, reorderedIndices, flipY);
                file[groupPath] = group;

                // 2. Write attributes
                // Save as root-level attributes
                file.Attributes[$"{groupPath}_Creator"] = "MxPlot.External.HDF5.Hdf5Handler";
                file.Attributes[$"{groupPath}_Version"] = "1.0";
                file.Attributes[$"{groupPath}_CreatedAt"] = DateTime.Now.ToString("o");

                //Write MatrixData info to groupPath
                WriteAttributes(group, groupPath, data, sortedAxes, flipY);
                
                // 3. Write to file
                file.Write(filePath);
            }
            catch (Exception ex)
            {
                throw new Hdf5ExportException($"Failed to export to HDF5: {ex.Message}", ex);
            }
        }

        #region Load, GetFileInfo (not yet implemented)
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
        /// Recalculates axis ordering and returns reordered frame indices and sorted axis list.
        /// </summary>
        private static (int[] reorderedIndices, List<Axis> sortedAxes) GetReorderedIndices<T>(MatrixData<T> data) where T : unmanaged
        {
            // IReadOnlyList does not expose IndexOf, so convert to List first.
            var originalAxes = data.Dimensions.Axes.ToList();

            // 1. Sort axes into canonical HDF5 order
            var sortedAxes = originalAxes
                .OrderBy(a => GetAxisPriority(a))
                .ToList();

            int frameCount = data.FrameCount;
            int[] reorderedIndices = new int[frameCount];

            // 2. Prepare counters and sorted-to-original index map
            int[] currentCounters = new int[sortedAxes.Count];
            int[] mapSortedToOriginal = new int[sortedAxes.Count];
            for (int i = 0; i < sortedAxes.Count; i++)
            {
                mapSortedToOriginal[i] = originalAxes.IndexOf(sortedAxes[i]);
            }

            int[] originalIndexer = new int[originalAxes.Count];

            // 3. Build reordered index array
            for (int i = 0; i < frameCount; i++)
            {
                // A. Counter -> original coordinates
                for (int k = 0; k < sortedAxes.Count; k++)
                {
                    int originalPos = mapSortedToOriginal[k];
                    originalIndexer[originalPos] = currentCounters[k];
                }

                // B. Resolve original frame index
                int sourceIndex = data.Dimensions.GetFrameIndexAt(originalIndexer);
                reorderedIndices[i] = sourceIndex;

                // C. Increment counters (innermost loop first)
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
        /// Writes the data array into the HDF5 group using unsafe pointer operations.
        /// </summary>
        private static unsafe void WriteDataArray<T>(H5Group group, string groupPath, MatrixData<T> data,
                                                List<Axis> sortedAxes, int[] reorderedIndices, bool flipY)
            where T : unmanaged
        {
            

            // 1. Determine dimension sizes for the N-dimensional array
            long[] dimensions = new long[sortedAxes.Count + 2];
            for (int i = 0; i < sortedAxes.Count; i++)
            {
                dimensions[i] = sortedAxes[i].Count;
            }
            dimensions[dimensions.Length - 2] = data.YCount;
            dimensions[dimensions.Length - 1] = data.XCount;

            // 2. Allocate the multi-dimensional array
            var multiDimArray = Array.CreateInstance(typeof(T), dimensions);

            // 3. Pin the array and obtain a pointer
            GCHandle handle = GCHandle.Alloc(multiDimArray, GCHandleType.Pinned);

            try
            {
                byte* dstBasePtr = (byte*)handle.AddrOfPinnedObject();

                // Size calculations
                int pixelSize = Unsafe.SizeOf<T>();
                long rowSizeBytes = (long)data.XCount * pixelSize;
                long frameSizeBytes = rowSizeBytes * data.YCount;

                // 4. Write loop
                for (int i = 0; i < data.FrameCount; i++)
                {
                    byte* dstFramePtr = dstBasePtr + ((long)i * frameSizeBytes);

                    // Retrieve source frame data
                    var srcFrame = data.GetArray(reorderedIndices[i]);

                    // Because T has the unmanaged constraint, fixed (T* p = srcFrame) would work,
                    // but using Unsafe.As<T, byte> allows bypassing the generic type constraint error
                    // while still achieving fast copies.
                    if (srcFrame.Length > 0)
                    {
                        // Get a reference to the first element reinterpreted as byte
                        ref byte srcRef = ref Unsafe.As<T, byte>(ref srcFrame[0]);

                        // Pin that reference and obtain a pointer
                        fixed (byte* srcBasePtr = &srcRef)
                        {
                            for (int y = 0; y < data.YCount; y++)
                            {
                                int srcY = flipY ? (data.YCount - 1 - y) : y;

                                byte* srcRowPtr = srcBasePtr + ((long)srcY * rowSizeBytes);
                                byte* dstRowPtr = dstFramePtr + ((long)y * rowSizeBytes);

                                // Fast memory copy
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
            group.Attributes["DISPLAY_ORIGIN"] = flipY ? "UL" : "LL"; // flipY=true → upper-left origin; MatrixData uses lower-left origin
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
            // Note: for simple 3D display only. Detailed multi-dimensional scale is handled in WriteDimensionAttributes.
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
            var (globalMin, globalMax) = data.GetGlobalValueRange();
            groupOrDataset.Attributes["IMAGE_MINMAXRANGE"] = new double[] { globalMin, globalMax };
            groupOrDataset.Attributes["VALUE_MIN"] = globalMin;
            groupOrDataset.Attributes["VALUE_MAX"] = globalMax;
        }

        /// <summary>
        /// Writes MatrixData attributes to the specified HDF5 object, using sortedAxes for dimension ordering.
        /// </summary>
        //private static void WriteAttributes<T>(H5File file, string groupPath, MatrixData<T> data, List<Axis> sortedAxes, bool flipY) where T : struct
        private static void WriteAttributes<T>(H5Object groupOrDataset, string groupPath, MatrixData<T> data, List<Axis> sortedAxes, bool flipY) where T : unmanaged
        {
            string prefix = groupPath.TrimStart('/').Replace('/', '_');

            // ... (basic attributes below are unchanged) ...
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
            
            // Write Dimension info (passing sortedAxes)
            WriteDimensionAttributes(groupOrDataset, prefix, sortedAxes);
        }

        /// <summary>
        /// Writes dimension metadata based on sortedAxes ordering.
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

            // Iterate in sortedAxes order
            for (int i = 0; i < sortedAxes.Count; i++)
            {
                var axis = sortedAxes[i];
                string axisPrefix = $"{prefix}_Dim{i}";

                // Basic Axis info
                groupOrDataset.Attributes[$"{axisPrefix}_Name"] = axis.Name;
                groupOrDataset.Attributes[$"{axisPrefix}_Count"] = (double)axis.Count;
                groupOrDataset.Attributes[$"{axisPrefix}_Min"] = axis.Min;
                groupOrDataset.Attributes[$"{axisPrefix}_Max"] = axis.Max;
                groupOrDataset.Attributes[$"{axisPrefix}_Unit"] = axis.Unit ?? "";
                groupOrDataset.Attributes[$"{axisPrefix}_IsIndexBased"] = axis.IsIndexBased;

                // Check whether the axis is a FovAxis
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
            // ... (unchanged) ...
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
        /// Identifies and tags each dimension with a common type label (FOV, Channel, Time, Z, or Unknown).
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
    /// Exception thrown when an HDF5 export operation fails.
    /// </summary>
    public class Hdf5ExportException : Exception
    {
        public Hdf5ExportException(string message) : base(message) { }
        public Hdf5ExportException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Holds metadata about an HDF5 file.
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

        // Dimension info
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
