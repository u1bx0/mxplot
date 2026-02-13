using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Provides binary serialization and deserialization for MatrixData&lt;T&gt; objects.
    /// File format (.mxd) includes header metadata and compressed data arrays.
    /// Statistical values (min/max) are NOT saved and will be recalculated on load.
    /// </summary>
    public static class MatrixDataSerializer
    {
        private const string MagicNumber = "MXDATA";
        private const int CurrentVersion = 1;

        /// <summary>
        /// Saves a MatrixData object to a binary file with optional GZip compression.
        /// </summary>
        /// <typeparam name="T">The data type of matrix elements (must be a struct).</typeparam>
        /// <param name="path">The file path where data will be saved.</param>
        /// <param name="data">The MatrixData object to save.</param>
        /// <param name="compress">If true, applies GZip compression to reduce file size (default: true).</param>
        /// <exception cref="ArgumentNullException">Thrown if data or path is null.</exception>
        /// <exception cref="IOException">Thrown if file write operation fails.</exception>
        public static void Save<T>(string path, MatrixData<T> data, bool compress = true) where T : unmanaged
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (data == null) throw new ArgumentNullException(nameof(data));

            using var fs = File.Create(path);
            using var compressionStream = compress ? new GZipStream(fs, CompressionLevel.Optimal) : null;
            var targetStream = compress ? (Stream)compressionStream! : fs;
            using var writer = new BinaryWriter(targetStream, Encoding.UTF8, leaveOpen: compress);

            try
            {
                // Write Header
                WriteHeader(writer, data, compress);

                // Write Dimensions
                WriteDimensions(writer, data);

                // Write Data Arrays (no statistics)
                WriteDataArrays(writer, data);

                // Write Metadata
                WriteMetadata(writer, data);
            }
            catch (Exception ex)
            {
                throw new IOException($"Failed to save MatrixData to '{path}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads a MatrixData object from a binary file.
        /// Statistical values (min/max) are automatically recalculated after loading.
        /// </summary>
        /// <typeparam name="T">The expected data type of matrix elements.</typeparam>
        /// <param name="path">The file path to load data from.</param>
        /// <returns>A new MatrixData&lt;T&gt; instance loaded from the file.</returns>
        /// <exception cref="ArgumentNullException">Thrown if path is null.</exception>
        /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
        /// <exception cref="InvalidDataException">Thrown if file format is invalid or type mismatch occurs.</exception>
        public static MatrixData<T> Load<T>(string path) where T : unmanaged
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");

            using var fs = File.OpenRead(path);

            // Detect compression by checking GZip magic bytes (0x1F 0x8B)
            bool compressed = false;
            var firstTwoBytes = new byte[2];
            if (fs.Read(firstTwoBytes, 0, 2) == 2)
            {
                compressed = firstTwoBytes[0] == 0x1F && firstTwoBytes[1] == 0x8B;
            }
            fs.Seek(0, SeekOrigin.Begin);

            // Decompress if needed
            using var decompressionStream = compressed ? new GZipStream(fs, CompressionMode.Decompress) : null;
            var sourceStream = compressed ? (Stream)decompressionStream! : fs;
            using var reader = new BinaryReader(sourceStream, Encoding.UTF8, leaveOpen: compressed);

            try
            {
                // Read and validate header
                var magic = reader.ReadString();
                if (magic != MagicNumber)
                    throw new InvalidDataException($"Invalid file format. Expected '{MagicNumber}', got '{magic}'");

                var version = reader.ReadInt32();
                if (version != CurrentVersion)
                    throw new InvalidDataException($"Unsupported version: {version}. Expected: {CurrentVersion}");

                var typeName = reader.ReadString();
                var compressedFlag = reader.ReadBoolean(); // Read but not used (already detected)

                // Validate type
                if (typeName != typeof(T).FullName)
                    throw new InvalidDataException(
                        $"Type mismatch: file contains '{typeName}', expected '{typeof(T).FullName}'");

                // Read rest of header
                var header = new HeaderInfo
                {
                    XCount = reader.ReadInt32(),
                    YCount = reader.ReadInt32(),
                    FrameCount = reader.ReadInt32(),
                    XMin = reader.ReadDouble(),
                    XMax = reader.ReadDouble(),
                    YMin = reader.ReadDouble(),
                    YMax = reader.ReadDouble(),
                    XUnit = reader.ReadString(),
                    YUnit = reader.ReadString()
                };

                // Read Dimensions
                var axes = ReadDimensions(reader);

                // Read Data Arrays
                var arrays = ReadDataArrays<T>(reader, header.XCount, header.YCount, header.FrameCount);

                // Create MatrixData (statistics will be auto-calculated)
                var matrix = new MatrixData<T>(header.XCount, header.YCount, arrays, 
                    minValueList: [], maxValueList: []); 
                matrix.SetXYScale(header.XMin, header.XMax, header.YMin, header.YMax);
                matrix.XUnit = header.XUnit;
                matrix.YUnit = header.YUnit;

                if (axes.Length > 0)
                    matrix.DefineDimensions(axes);

                // Read Metadata
                ReadMetadata(reader, matrix);

                return matrix;
            }
            catch (Exception ex) when (!(ex is InvalidDataException))
            {
                throw new InvalidDataException($"Failed to load MatrixData from '{path}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Reads file header information without loading the entire dataset.
        /// Useful for file browsing and preview functionality.
        /// </summary>
        /// <param name="path">The file path to inspect.</param>
        /// <returns>A FileInfo object containing header metadata.</returns>
        /// <exception cref="ArgumentNullException">Thrown if path is null.</exception>
        /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
        /// <exception cref="InvalidDataException">Thrown if file format is invalid.</exception>
        public static MatrixDataFileInfo GetFileInfo(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            if (!File.Exists(path)) throw new FileNotFoundException($"File not found: {path}");

            using var fs = File.OpenRead(path);

            // Detect compression
            bool compressed = false;
            var firstTwoBytes = new byte[2];
            if (fs.Read(firstTwoBytes, 0, 2) == 2)
            {
                compressed = firstTwoBytes[0] == 0x1F && firstTwoBytes[1] == 0x8B;
            }
            fs.Seek(0, SeekOrigin.Begin);

            using var decompressionStream = compressed ? new GZipStream(fs, CompressionMode.Decompress) : null;
            var sourceStream = compressed ? (Stream)decompressionStream! : fs;
            using var reader = new BinaryReader(sourceStream, Encoding.UTF8, leaveOpen: compressed);

            try
            {
                var magic = reader.ReadString();
                if (magic != MagicNumber)
                    throw new InvalidDataException($"Invalid file format");

                var version = reader.ReadInt32();
                var typeName = reader.ReadString();
                var compressedFlag = reader.ReadBoolean();

                var fileInfo = new MatrixDataFileInfo
                {
                    FilePath = path,
                    Version = version,
                    DataTypeName = typeName,
                    IsCompressed = compressed,
                    XCount = reader.ReadInt32(),
                    YCount = reader.ReadInt32(),
                    FrameCount = reader.ReadInt32(),
                    XMin = reader.ReadDouble(),
                    XMax = reader.ReadDouble(),
                    YMin = reader.ReadDouble(),
                    YMax = reader.ReadDouble(),
                    XUnit = reader.ReadString(),
                    YUnit = reader.ReadString(),
                    FileSize = new FileInfo(path).Length
                };

                // Read dimension info (but don't fully parse)
                int axisCount = reader.ReadInt32();
                var axisNames = new List<string>();
                for (int i = 0; i < axisCount; i++)
                {
                    string name = reader.ReadString();
                    reader.ReadInt32(); // count
                    reader.ReadDouble(); // min
                    reader.ReadDouble(); // max
                    axisNames.Add(name);
                }
                fileInfo.DimensionNames = axisNames.ToArray();

                return fileInfo;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to read file info from '{path}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Dynamically loads a MatrixData object without prior knowledge of its type.
        /// Returns an IMatrixData interface that can be cast to the appropriate MatrixData&lt;T&gt;.
        /// </summary>
        /// <param name="path">The file path to load data from.</param>
        /// <returns>An IMatrixData instance (cast to specific MatrixData&lt;T&gt; as needed).</returns>
        /// <exception cref="ArgumentNullException">Thrown if path is null.</exception>
        /// <exception cref="FileNotFoundException">Thrown if the file does not exist.</exception>
        /// <exception cref="InvalidDataException">Thrown if file format is invalid.</exception>
        /// <exception cref="NotSupportedException">Thrown if the stored type is not supported.</exception>
        public static IMatrixData LoadDynamic(string path)
        {
            var fileInfo = GetFileInfo(path);
            
            // Map type name to appropriate Load<T> call
            return fileInfo.DataTypeName switch
            {
                "System.Double" => Load<double>(path),
                "System.Single" => Load<float>(path),
                "System.Int32" => Load<int>(path),
                "System.Int64" => Load<long>(path),
                "System.Int16" => Load<short>(path),
                "System.Byte" => Load<byte>(path),
                "System.UInt32" => Load<uint>(path),
                "System.UInt64" => Load<ulong>(path),
                "System.UInt16" => Load<ushort>(path),
                "System.SByte" => Load<sbyte>(path),
                "System.Numerics.Complex" => Load<System.Numerics.Complex>(path),
                _ => throw new NotSupportedException(
                    $"Unsupported data type: {fileInfo.DataTypeName}. " +
                    $"Supported types: double, float, int, uint, long, ulong, short, ushort, byte, sbyte, Complex")
            };
        }

        #region Write Methods

        private static void WriteHeader<T>(BinaryWriter writer, MatrixData<T> data, bool compressed) where T : unmanaged
        {
            writer.Write(MagicNumber);
            writer.Write(CurrentVersion);
            writer.Write(typeof(T).FullName ?? string.Empty);
            writer.Write(compressed);
            writer.Write(data.XCount);
            writer.Write(data.YCount);
            writer.Write(data.FrameCount);
            writer.Write(data.XMin);
            writer.Write(data.XMax);
            writer.Write(data.YMin);
            writer.Write(data.YMax);
            writer.Write(data.XUnit ?? string.Empty);
            writer.Write(data.YUnit ?? string.Empty);
        }

        private static void WriteDimensions<T>(BinaryWriter writer, MatrixData<T> data) where T : unmanaged
        {
            var axes = data.Dimensions?.Axes?.ToArray() ?? Array.Empty<Axis>();
            writer.Write(axes.Length);

            foreach (var axis in axes)
            {
                writer.Write(axis.Name ?? string.Empty);
                writer.Write(axis.Count);
                writer.Write(axis.Min);
                writer.Write(axis.Max);
                writer.Write(axis.Unit ?? string.Empty);
            }
        }

        private static void WriteDataArrays<T>(BinaryWriter writer, MatrixData<T> data) where T : unmanaged
        {
            for (int i = 0; i < data.FrameCount; i++)
            {
                var bytes = data.GetRawBytes(i);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }

        private static void WriteMetadata<T>(BinaryWriter writer, MatrixData<T> data) where T : unmanaged
        {
            var metadata = data.Metadata;
            writer.Write(metadata?.Count ?? 0);

            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    writer.Write(kvp.Key);
                    writer.Write(kvp.Value ?? string.Empty);
                }
            }
        }

        #endregion

        #region Read Methods

        private static Axis[] ReadDimensions(BinaryReader reader)
        {
            int axisCount = reader.ReadInt32();
            var axes = new Axis[axisCount];

            for (int i = 0; i < axisCount; i++)
            {
                string name = reader.ReadString();
                int count = reader.ReadInt32();
                double min = reader.ReadDouble();
                double max = reader.ReadDouble();
                string unit = reader.ReadString();
                axes[i] = new Axis(count, min, max, name, unit);
            }

            return axes;
        }

        private static List<T[]> ReadDataArrays<T>(BinaryReader reader, int xCount, int yCount, int frameCount) 
            where T : unmanaged
        {
            var arrays = new List<T[]>(frameCount);
            int expectedLength = xCount * yCount;

            for (int i = 0; i < frameCount; i++)
            {
                int byteLength = reader.ReadInt32();
                var bytes = reader.ReadBytes(byteLength);
                
                var array = MemoryMarshal.Cast<byte, T>(bytes).ToArray();
                
                if (array.Length != expectedLength)
                    throw new InvalidDataException(
                        $"Frame {i} has incorrect size: {array.Length}, expected: {expectedLength}");

                arrays.Add(array);
            }

            return arrays;
        }

        private static void ReadMetadata<T>(BinaryReader reader, MatrixData<T> matrix) where T : unmanaged
        {
            int metadataCount = reader.ReadInt32();

            for (int i = 0; i < metadataCount; i++)
            {
                string key = reader.ReadString();
                string value = reader.ReadString();
                matrix.Metadata[key] = value;
            }
        }

        #endregion

        #region Helper Classes

        /// <summary>
        /// Contains metadata information about a MatrixData file without loading the full dataset.
        /// Useful for file browsing, preview, and validation.
        /// </summary>
        public class MatrixDataFileInfo
        {
            /// <summary>Full path to the file.</summary>
            public string FilePath { get; set; } = string.Empty;

            /// <summary>File format version.</summary>
            public int Version { get; set; }

            /// <summary>Full type name of the stored data (e.g., "System.Double").</summary>
            public string DataTypeName { get; set; } = string.Empty;

            /// <summary>Short type name (e.g., "double").</summary>
            public string DataType => DataTypeName switch
            {
                "System.Double" => "double",
                "System.Single" => "float",
                "System.Int32" => "int",
                "System.Int64" => "long",
                "System.Int16" => "short",
                "System.Byte" => "byte",
                "System.UInt32" => "uint",
                "System.UInt64" => "ulong",
                "System.UInt16" => "ushort",
                "System.SByte" => "sbyte",
                "System.Numerics.Complex" => "Complex",
                _ => DataTypeName
            };

            /// <summary>Whether the file uses GZip compression.</summary>
            public bool IsCompressed { get; set; }

            /// <summary>Number of pixels in X direction.</summary>
            public int XCount { get; set; }

            /// <summary>Number of pixels in Y direction.</summary>
            public int YCount { get; set; }

            /// <summary>Number of frames/layers.</summary>
            public int FrameCount { get; set; }

            /// <summary>Minimum X coordinate value.</summary>
            public double XMin { get; set; }

            /// <summary>Maximum X coordinate value.</summary>
            public double XMax { get; set; }

            /// <summary>Minimum Y coordinate value.</summary>
            public double YMin { get; set; }

            /// <summary>Maximum Y coordinate value.</summary>
            public double YMax { get; set; }

            /// <summary>Physical unit of X axis (e.g., "mm").</summary>
            public string XUnit { get; set; } = string.Empty;

            /// <summary>Physical unit of Y axis (e.g., "mm").</summary>
            public string YUnit { get; set; } = string.Empty;

            /// <summary>Names of dimension axes (e.g., ["Time", "Z"]).</summary>
            public string[] DimensionNames { get; set; } = Array.Empty<string>();

            /// <summary>File size in bytes.</summary>
            public long FileSize { get; set; }

            /// <summary>Human-readable file size string.</summary>
            public string FileSizeString
            {
                get
                {
                    string[] sizes = { "B", "KB", "MB", "GB" };
                    double len = FileSize;
                    int order = 0;
                    while (len >= 1024 && order < sizes.Length - 1)
                    {
                        order++;
                        len /= 1024;
                    }
                    return $"{len:F2} {sizes[order]}";
                }
            }

            /// <summary>Total number of data elements.</summary>
            public long TotalElements => (long)XCount * YCount * FrameCount;

            /// <summary>String representation of matrix dimensions.</summary>
            public string DimensionsString => $"{XCount}×{YCount}×{FrameCount}";

            public override string ToString()
            {
                return $"{DataType} [{DimensionsString}] - {FileSizeString}";
            }
        }

        private class HeaderInfo
        {
            public string Magic { get; set; } = string.Empty;
            public int Version { get; set; }
            public string TypeName { get; set; } = string.Empty;
            public bool Compressed { get; set; }
            public int XCount { get; set; }
            public int YCount { get; set; }
            public int FrameCount { get; set; }
            public double XMin { get; set; }
            public double XMax { get; set; }
            public double YMin { get; set; }
            public double YMax { get; set; }
            public string XUnit { get; set; } = string.Empty;
            public string YUnit { get; set; } = string.Empty;
        }

        #endregion
    }

    /// <summary>
    /// Provides functionality for reading and writing matrix data in the library's native binary format (.mxd).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This format is designed for high-performance I/O and full metadata preservation, 
    /// supporting all numeric types, axis definitions, and custom metadata tags.
    /// </para>
    /// <para>
    /// Usage Example:
    /// <code>
    /// // Saving with GZip compression
    /// var format = new MxBinaryFormat { Compress = true };
    /// MatrixData.Save("data.mxd", matrix, format);
    /// 
    /// // Loading with automatic type detection
    /// IMatrixData loaded = MatrixData.Load("data.mxd", format);
    /// </code>
    /// </para>
    /// </remarks>
    public class MxBinaryFormat: IMatrixDataWriter, IMatrixDataReader
    {
        public bool Compress { get; set; } = true;
        public void Write<T>(string filePath, MatrixData<T> data) where T : unmanaged
        {
            MatrixDataSerializer.Save(filePath, data, Compress);
        }
        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            return MatrixDataSerializer.Load<T>(filePath);
        }
        public IMatrixData Read(string path)
        {
            return MatrixDataSerializer.LoadDynamic(path);
        }
    }
}
