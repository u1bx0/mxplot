using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        /// <summary>
        /// Magic Number: "MXDF" (Matrix Data File).
        /// Using a 4-byte FourCC (Four Character Code) allows for efficient 32-bit integer comparison 
        /// and ensures proper memory alignment during initial file validation.
        /// </summary>
        public readonly static string MagicNumber = "MXDF";

        /// <summary>
        /// Saves the specified matrix data to a file at the given path, with optional compression and optional progress reporting.
        /// </summary>
        /// <remarks>Progress is reported before and after saving the data. If compression is enabled, the
        /// data is written using GZip compression.</remarks>
        /// <typeparam name="T">The type of elements in the matrix data. Must be an unmanaged type.</typeparam>
        /// <param name="path">The file path where the matrix data will be saved.</param>
        /// <param name="data">The matrix data to save.</param>
        /// <param name="compress">A value indicating whether to compress the data before saving. The default is <see langword="true"/>.</param>
        /// <param name="progress">An optional progress reporter that receives updates on the number of frames processed during the save
        /// operation.</param>
        public static void Save<T>(string path, MatrixData<T> data, bool compress = true, IProgress<int>? progress = null) where T : unmanaged
        {
            using var fs = File.Create(path);
            using var writer = new BinaryWriter(fs, Encoding.UTF8);

            //Report progress (negative value indicates total frames to process)
            progress?.Report(-data.FrameCount);

            // 1. Magic Number
            writer.Write(Encoding.ASCII.GetBytes(MagicNumber));

            // 2. Prepare Header
            var config = new MatrixDataConfig(data) with { IsCompressed = compress };
            byte[] headerBytes = Encoding.UTF8.GetBytes(config.ToHeaderString());

            // 3. Write Header Size & Content
            writer.Write(headerBytes.Length);
            writer.Write(headerBytes);

            // 4. Binary Data Section
            if (compress)
            {
                using var gzs = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true);
                WriteDataToStream(gzs, data, progress);
            }
            else
            {
                WriteDataToStream(fs, data, progress);
            }
            progress?.Report(data.FrameCount);
        }

        private static void WriteDataToStream<T>(Stream stream, MatrixData<T> data, IProgress<int>? progress) where T : unmanaged
        {
            // BinaryWriter を一時的に作成（ストリームを閉じない設定で）
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            for (int i = 0; i < data.FrameCount; i++)
            {
                ReadOnlySpan<byte> bytes = data.GetRawBytes(i);
                writer.Write(bytes.Length);
                writer.Write(bytes);
                progress?.Report(i);
            }
        }

        /// <summary>
        /// Read only the header metadata from a MxPlot data file, returning a MatrixDataConfig instance without loading the full matrix data.
        /// </summary>
        public static MatrixDataConfig ReadHeaderConfig(string path)
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8);
            return ReadHeaderInternal(reader); // 共通ロジックへ
        }

        /// <summary>
        /// Internal logic for reading the header metadata from a MxPlot data file. 
        /// This method assumes that the reader is positioned at the beginning of the file 
        /// and will read the magic number and header content, returning a MatrixDataConfig instance. 
        /// The reader's position will be advanced to the end of the header, ready for subsequent reading of the matrix data if needed.
        /// </summary>
        private static MatrixDataConfig ReadHeaderInternal(BinaryReader reader)
        {
            // 1. Check Magic Number
            var magicBytes = reader.ReadBytes(4);
            var magic = Encoding.ASCII.GetString(magicBytes);
            if (magic != MagicNumber) throw new InvalidDataException("Not a valid MXDF data file.");

            // 2. Read Header
            int headerSize = reader.ReadInt32();
            string json = Encoding.UTF8.GetString(reader.ReadBytes(headerSize));

            return MatrixDataConfig.FromHeaderString(json)
                   ?? throw new InvalidOperationException("Invalid header content.");
        }

        /// <summary>
        /// Loads matrix data from a file in the MxPlot format and returns a new instance of the corresponding
        /// MatrixData<T> type.
        /// </summary>
        /// <remarks>This method supports loading both compressed and uncompressed MxPlot files. If the
        /// file is compressed, decompression is handled automatically. The caller must ensure that the type parameter T
        /// matches the type of the data in the file; otherwise, an exception is thrown.</remarks>
        /// <typeparam name="T">The type of the matrix elements to load. Must be an unmanaged type that matches the data stored in the file.</typeparam>
        /// <param name="path">The path to the MxPlot data file to load. The file must exist and be in a valid MxPlot format.</param>
        /// <param name="progress">An optional progress reporter that receives updates on the number of frames processed during loading. If
        /// provided, negative values indicate progress before loading, and a positive value is reported upon
        /// completion.</param>
        /// <returns>A MatrixData<T> instance containing the matrix data loaded from the specified file.</returns>
        /// <exception cref="InvalidDataException">Thrown if the file does not contain valid MxPlot data, as indicated by an incorrect magic number.</exception>
        /// <exception cref="InvalidOperationException">Thrown if the file header is invalid or if the type parameter T does not match the type of data stored in
        /// the file.</exception>
        public static MatrixData<T> Load<T>(string path, IProgress<int>? progress = null) where T : unmanaged
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            // ★ここで共通化！ reader の位置は自動的にヘッダー終了直後（＝バイナリ開始位置）になる
            var config = ReadHeaderInternal(reader);

            // 型チェック（ガードレール）
            if (config.ValueTypeName != typeof(T).FullName)
                throw new InvalidDataException($"Type mismatch: File={config.ValueTypeAlias}, Requested={typeof(T).Name}");

            progress?.Report(-config.FrameCount);

            // 3. Read Body (Conditional Decompression)
            List<T[]> frames;
            if (config.IsCompressed) // Metadataから引くのではなくプロパティを直接参照
            {
                // GZipStreamを明示的に作成し、読み終わったら確実に閉じる
                using var gzs = new GZipStream(fs, CompressionMode.Decompress);
                using var buffered = new BufferedStream(gzs, 65536); // 64KBバッファ
                frames = ReadDataFromStream<T>(buffered, config, progress);
            }
            else
            {
                frames = ReadDataFromStream<T>(fs, config, progress);
            }

            var md = config.CreateNewInstance(frames);
            progress?.Report(md.FrameCount);
            return md;
        }

        private static List<T[]> ReadDataFromStream<T>(Stream sourceStream, MatrixDataConfig config, IProgress<int>? progress)
           where T : unmanaged
        {
            // ここでは BinaryReader を使わず、直接 Stream から読むか、
            // leaveOpen: true で reader を作る（sourceStreamを閉じないため）
            using var reader = new BinaryReader(sourceStream, Encoding.UTF8, leaveOpen: true);

            var arrays = new List<T[]>(config.FrameCount);
            int expectedLength = config.XCount * config.YCount;

            for (int i = 0; i < config.FrameCount; i++)
            {
                int byteLength = reader.ReadInt32();
                var bytes = reader.ReadBytes(byteLength);

                // キャストして配列化
                var array = MemoryMarshal.Cast<byte, T>(bytes).ToArray();

                if (array.Length != expectedLength)
                    throw new InvalidDataException($"Frame {i} size mismatch.");

                arrays.Add(array);
                progress?.Report(i + 1); // 0-based index なので +1 して報告
            }

            return arrays;
        }

        private static IMatrixData TryLoadCustomStruct(string typeName, string path, IProgress<int>? progress)
        {
            var type = Type.GetType(typeName);
            if (type == null) throw new NotSupportedException($"Type {typeName} not found.");

            // MatrixDataSerializer.Load<T> をリフレクションで叩く
            var method = typeof(MatrixDataSerializer).GetMethod(nameof(Load))!.MakeGenericMethod(type);
            return (IMatrixData)method.Invoke(null, [path, progress])!;
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
        public static IMatrixData LoadDynamic(string path, IProgress<int>? progress = null)
        {
            var conf = ReadHeaderConfig(path);

            // Map type name to appropriate Load<T> call
            return conf.ValueTypeName switch
            {
                "System.Double" => Load<double>(path, progress),
                "System.Single" => Load<float>(path, progress),
                "System.Int32" => Load<int>(path, progress),
                "System.Int64" => Load<long>(path, progress),
                "System.Int16" => Load<short>(path, progress),
                "System.Byte" => Load<byte>(path, progress),
                "System.UInt32" => Load<uint>(path, progress),
                "System.UInt64" => Load<ulong>(path, progress),
                "System.UInt16" => Load<ushort>(path, progress),
                "System.SByte" => Load<sbyte>(path, progress),
                "System.Numerics.Complex" => Load<System.Numerics.Complex>(path, progress),
                _ => TryLoadCustomStruct(conf.ValueTypeName, path, progress)
            };
        }
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
        public IProgress<int>? ProgressReporter { get; set; } = null;
        public void Write<T>(string filePath, MatrixData<T> data) where T : unmanaged
        {
            MatrixDataSerializer.Save(filePath, data, Compress, ProgressReporter);
        }
        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            return MatrixDataSerializer.Load<T>(filePath, ProgressReporter);
        }
        public IMatrixData Read(string path)
        {
            return MatrixDataSerializer.LoadDynamic(path, ProgressReporter);
        }
    }
}
