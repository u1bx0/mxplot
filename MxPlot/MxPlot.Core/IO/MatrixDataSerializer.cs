using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Provides binary serialization and deserialization for MatrixData&lt;T&gt; objects.
    /// <para>
    /// File format (.mxd):
    /// <code>
    /// [MXDF 4B][DataLength int64][ConfigOffset int64]  ← 20B fixed header
    /// [Frame data ...]                                   ← raw or GZip-compressed
    /// [JSON Config (MatrixDataConfig)]                   ← metadata trailer
    /// </code>
    /// When uncompressed, frames are contiguous and fixed-size, enabling direct
    /// memory-mapped file (MMF) access from byte offset <see cref="HeaderSize"/>.
    /// A <c>ConfigOffset</c> of 0 indicates a temporary working file whose metadata
    /// has not yet been written.
    /// </para>
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
        /// Fixed header size in bytes: [Magic 4B] + [DataLength 8B] + [ConfigOffset 8B] = 20.
        /// Frame data begins at this byte offset.
        /// </summary>
        public const int HeaderSize = 20;

        /// <summary>
        /// Creates a temporary .mxd file pre-allocated for the given dimensions,
        /// and returns a <see cref="WritableVirtualStrippedFrames{T}"/> mounted on it.
        /// The file uses <c>ConfigOffset = 0</c> (temp marker) and contiguous uncompressed layout.
        /// </summary>
        /// <typeparam name="T">The unmanaged element type.</typeparam>
        /// <param name="xcount">Width (pixels per row).</param>
        /// <param name="ycount">Height (rows per frame).</param>
        /// <param name="frameCount">Number of frames to allocate.</param>
        /// <returns>A writable, temporary WVSF backed by the new file.</returns>
        public static WritableVirtualStrippedFrames<T> CreateTempVessel<T>(int xcount, int ycount, int frameCount) where T : unmanaged
            => CreateVessel<T>(null, xcount, ycount, frameCount);

        /// <summary>
        /// Creates a .mxd file pre-allocated for the given dimensions,
        /// and returns a <see cref="WritableVirtualStrippedFrames{T}"/> mounted on it.
        /// The file uses <c>ConfigOffset = 0</c> (temp marker) and contiguous uncompressed layout.
        /// </summary>
        /// <typeparam name="T">The unmanaged element type.</typeparam>
        /// <param name="filePath">
        /// Explicit path for the .mxd file. If <see langword="null"/>, a temporary path is generated
        /// and the WVSF is marked as temporary (auto-deleted on Dispose).
        /// </param>
        /// <param name="xcount">Width (pixels per row).</param>
        /// <param name="ycount">Height (rows per frame).</param>
        /// <param name="frameCount">Number of frames to allocate.</param>
        /// <returns>A writable WVSF backed by the new file.</returns>
        internal static WritableVirtualStrippedFrames<T> CreateVessel<T>(string? filePath, int xcount, int ycount, int frameCount) where T : unmanaged
        {
            bool isTemporary = string.IsNullOrWhiteSpace(filePath);
            string path = isTemporary
                ? Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".mxd")
                : filePath!;

            long frameByteSize = (long)xcount * ycount * Unsafe.SizeOf<T>();

            // Step 1: Write the 20-byte header and pre-allocate the file, then close it
            // so that the WVSF constructor can open it exclusively via Mount().
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var writer = new BinaryWriter(fs))
            {
                writer.Write(Encoding.ASCII.GetBytes(MagicNumber)); // 4B
                writer.Write(0L);  // DataLength placeholder
                writer.Write(0L);  // ConfigOffset = 0 (temp marker)
                fs.SetLength(HeaderSize + frameByteSize * frameCount);
            }

            // Step 2: Build contiguous offset table (1 strip per frame)
            var offsets    = new long[frameCount][];
            var byteCounts = new long[frameCount][];
            for (int i = 0; i < frameCount; i++)
            {
                offsets[i]    = [HeaderSize + i * frameByteSize];
                byteCounts[i] = [frameByteSize];
            }

            // Step 3: Mount the WVSF
            return new WritableVirtualStrippedFrames<T>(
                path, xcount, ycount, offsets, byteCounts,
                isYFlipped: false, isTemporary: isTemporary);
        }

        /// <summary>
        /// Saves the specified matrix data to a file at the given path, with optional compression and optional progress reporting.
        /// </summary>
        /// <typeparam name="T">The type of elements in the matrix data. Must be an unmanaged type.</typeparam>
        /// <param name="path">The file path where the matrix data will be saved.</param>
        /// <param name="data">The matrix data to save.</param>
        /// <param name="compress">A value indicating whether to compress the data before saving. The default is <see langword="true"/>.</param>
        /// <param name="progress">An optional progress reporter that receives updates on the number of frames processed during the save
        /// operation.</param>
        public static void Save<T>(string path, MatrixData<T> data, bool compress = true, IProgress<int>? progress = null) where T : unmanaged
        {
            using var fs = File.Create(path);
            using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

            progress?.Report(-data.FrameCount);

            // 1. Fixed header (placeholders — back-patched at step 4)
            writer.Write(Encoding.ASCII.GetBytes(MagicNumber)); // 4B
            writer.Write(0L);  // DataLength placeholder  (8B)
            writer.Write(0L);  // ConfigOffset placeholder (8B)
            // fs.Position == HeaderSize (20)

            // 2. Data section
            if (compress)
            {
                // Scope the GZipStream so it is fully flushed before we read fs.Position
                using (var gzs = new GZipStream(fs, CompressionLevel.Optimal, leaveOpen: true))
                {
                    WriteDataToStream(gzs, data, progress);
                }
            }
            else
            {
                WriteDataToStream(fs, data, progress);
            }

            long dataLength = fs.Position - HeaderSize;

            // 3. Append JSON config (trailer)
            long configOffset = fs.Position;
            var config = new MatrixDataConfig(data) with { IsCompressed = compress };
            byte[] configBytes = Encoding.UTF8.GetBytes(config.ToHeaderString());
            writer.Write(configBytes);

            // 4. Back-patch header
            fs.Seek(4, SeekOrigin.Begin);
            writer.Write(dataLength);
            writer.Write(configOffset);

            progress?.Report(data.FrameCount);
        }

        private static void WriteDataToStream<T>(Stream stream, MatrixData<T> data, IProgress<int>? progress) where T : unmanaged
        {
            for (int i = 0; i < data.FrameCount; i++)
            {
                ReadOnlySpan<byte> bytes = data.GetRawBytes(i);
                stream.Write(bytes);
                progress?.Report(i);
            }
        }

        /// <summary>
        /// Read only the config metadata from a .mxd file without loading frame data.
        /// </summary>
        public static MatrixDataConfig ReadHeaderConfig(string path)
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8);
            var (_, _, config) = ReadFixedHeaderAndConfig(fs, reader);
            return config;
        }

        /// <summary>
        /// Reads the 20-byte fixed header, then seeks to <c>ConfigOffset</c> and
        /// deserialises the JSON trailer into a <see cref="MatrixDataConfig"/>.
        /// </summary>
        private static (long dataLength, long configOffset, MatrixDataConfig config)
            ReadFixedHeaderAndConfig(Stream fs, BinaryReader reader)
        {
            // 1. Magic
            var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (magic != MagicNumber)
                throw new InvalidDataException("Not a valid MXDF data file.");

            // 2. Fixed fields
            long dataLength   = reader.ReadInt64();
            long configOffset = reader.ReadInt64();
            // reader position == HeaderSize (20)

            if (configOffset <= 0)
                throw new InvalidDataException(
                    "File does not contain metadata (incomplete or temporary file).");

            // 3. Seek to config trailer
            fs.Seek(configOffset, SeekOrigin.Begin);
            int configSize = (int)(fs.Length - configOffset);
            string json = Encoding.UTF8.GetString(reader.ReadBytes(configSize));

            var config = MatrixDataConfig.FromHeaderString(json)
                         ?? throw new InvalidOperationException("Invalid config content.");

            return (dataLength, configOffset, config);
        }

        /// <summary>
        /// Loads matrix data from a .mxd file and returns a new <see cref="MatrixData{T}"/> instance.
        /// </summary>
        /// <typeparam name="T">The type of the matrix elements to load. Must match the data stored in the file.</typeparam>
        /// <param name="path">The path to the .mxd file.</param>
        /// <param name="progress">An optional progress reporter.</param>
        /// <returns>A MatrixData&lt;T&gt; instance containing the loaded data.</returns>
        public static MatrixData<T> Load<T>(string path, IProgress<int>? progress = null, CancellationToken ct = default) where T : unmanaged
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            var (_, _, config) = ReadFixedHeaderAndConfig(fs, reader);

            // 型チェック（ガードレール）
            if (config.ValueTypeName != typeof(T).FullName)
                throw new InvalidDataException($"Type mismatch: File={config.ValueTypeAlias}, Requested={typeof(T).Name}");

            progress?.Report(-config.FrameCount);

            // Seek back to data start
            fs.Seek(HeaderSize, SeekOrigin.Begin);

            List<T[]> frames;
            if (config.IsCompressed)
            {
                using var gzs = new GZipStream(fs, CompressionMode.Decompress);
                using var buffered = new BufferedStream(gzs, 65536);
                frames = ReadDataFromStream<T>(buffered, config, progress, ct);
            }
            else
            {
                frames = ReadDataFromStream<T>(fs, config, progress, ct);
            }

            var md = config.CreateNewInstance(frames);
            progress?.Report(md.FrameCount);
            return md;
        }

        /// <summary>
        /// Opens a .mxd file as a read-only virtual (MMF-backed) <see cref="MatrixData{T}"/>.
        /// Only uncompressed files are supported; compressed files will throw <see cref="NotSupportedException"/>.
        /// </summary>
        /// <typeparam name="T">The type of the matrix elements. Must match the data stored in the file.</typeparam>
        /// <param name="path">The path to the .mxd file.</param>
        /// <returns>A read-only virtual <see cref="MatrixData{T}"/> instance backed by the file.</returns>
        public static MatrixData<T> LoadVirtual<T>(string path) where T : unmanaged
        {
            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

            var (_, _, config) = ReadFixedHeaderAndConfig(fs, reader);

            if (config.ValueTypeName != typeof(T).FullName)
                throw new InvalidDataException($"Type mismatch: File={config.ValueTypeAlias}, Requested={typeof(T).Name}");

            if (config.IsCompressed)
                throw new NotSupportedException(
                    "Virtual loading is not supported for compressed .mxd files. Use LoadingMode.InMemory instead.");

            long frameByteSize = (long)config.XCount * config.YCount * Unsafe.SizeOf<T>();

            var offsets    = new long[config.FrameCount][];
            var byteCounts = new long[config.FrameCount][];
            for (int i = 0; i < config.FrameCount; i++)
            {
                offsets[i]    = [HeaderSize + i * frameByteSize];
                byteCounts[i] = [frameByteSize];
            }

            var vsf = new VirtualStrippedFrames<T>(
                path, config.XCount, config.YCount, offsets, byteCounts, isYFlipped: false);

            return config.CreateFromVirtualFrames(vsf);
        }

        private static List<T[]> ReadDataFromStream<T>(Stream sourceStream, MatrixDataConfig config, IProgress<int>? progress, CancellationToken ct = default)
           where T : unmanaged
        {
            int frameByteSize = config.XCount * config.YCount * Unsafe.SizeOf<T>();
            var arrays = new List<T[]>(config.FrameCount);

            for (int i = 0; i < config.FrameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var bytes = new byte[frameByteSize];
                sourceStream.ReadExactly(bytes);

                var array = MemoryMarshal.Cast<byte, T>(bytes).ToArray();

                if (array.Length != config.XCount * config.YCount)
                    throw new InvalidDataException($"Frame {i} size mismatch.");

                arrays.Add(array);
                progress?.Report(i + 1);
            }

            return arrays;
        }

        private static IMatrixData TryLoadCustomStruct(string typeName, string path, IProgress<int>? progress, CancellationToken ct = default)
        {
            var type = Type.GetType(typeName);
            if (type == null) throw new NotSupportedException($"Type {typeName} not found.");

            // MatrixDataSerializer.Load<T> をリフレクションで叩く
            var method = typeof(MatrixDataSerializer).GetMethod(nameof(Load))!.MakeGenericMethod(type);
            return (IMatrixData)method.Invoke(null, [path, progress, ct])!;
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
        public static IMatrixData LoadDynamic(string path, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            var conf = ReadHeaderConfig(path);

            // Map type name to appropriate Load<T> call
            return conf.ValueTypeName switch
            {
                "System.Double" => Load<double>(path, progress, ct),
                "System.Single" => Load<float>(path, progress, ct),
                "System.Int32" => Load<int>(path, progress, ct),
                "System.Int64" => Load<long>(path, progress, ct),
                "System.Int16" => Load<short>(path, progress, ct),
                "System.Byte" => Load<byte>(path, progress, ct),
                "System.UInt32" => Load<uint>(path, progress, ct),
                "System.UInt64" => Load<ulong>(path, progress, ct),
                "System.UInt16" => Load<ushort>(path, progress, ct),
                "System.SByte" => Load<sbyte>(path, progress, ct),
                "System.Numerics.Complex" => Load<System.Numerics.Complex>(path, progress, ct),
                _ => TryLoadCustomStruct(conf.ValueTypeName, path, progress, ct)
            };
        }

        /// <summary>
        /// Dynamically loads a MatrixData object as read-only virtual (MMF-backed) without prior knowledge of its type.
        /// Only uncompressed .mxd files are supported.
        /// </summary>
        public static IMatrixData LoadDynamicVirtual(string path)
        {
            var conf = ReadHeaderConfig(path);

            return conf.ValueTypeName switch
            {
                "System.Double"  => LoadVirtual<double>(path),
                "System.Single"  => LoadVirtual<float>(path),
                "System.Int32"   => LoadVirtual<int>(path),
                "System.Int64"   => LoadVirtual<long>(path),
                "System.Int16"   => LoadVirtual<short>(path),
                "System.Byte"    => LoadVirtual<byte>(path),
                "System.UInt32"  => LoadVirtual<uint>(path),
                "System.UInt64"  => LoadVirtual<ulong>(path),
                "System.UInt16"  => LoadVirtual<ushort>(path),
                "System.SByte"   => LoadVirtual<sbyte>(path),
                _ => throw new NotSupportedException(
                    $"Virtual loading is not supported for type '{conf.ValueTypeAlias}'.")
            };
        }

        /// <summary>
        /// Appends (or replaces) the JSON metadata trailer on an existing .mxd file and back-patches the fixed header.
        /// This is designed to be called from a <c>beforeRemount</c> hook while the file is not MMF-locked.
        /// </summary>
        /// <remarks>
        /// Handles both cases:
        /// <list type="bullet">
        ///   <item><c>ConfigOffset == 0</c> (temp vessel): pixel data fills from <see cref="HeaderSize"/> to EOF. Trailer is appended.</item>
        ///   <item><c>ConfigOffset &gt; 0</c> (existing .mxd): old trailer is truncated via <see cref="FileStream.SetLength"/>, then new trailer is appended.</item>
        /// </list>
        /// </remarks>
        internal static void FinalizeTrailer<T>(string path, MatrixData<T> data) where T : unmanaged
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            using var reader = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            using var writer = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

            // 1. Read existing header
            fs.Seek(0, SeekOrigin.Begin);
            var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (magic != MagicNumber)
                throw new InvalidDataException($"Not a valid MXDF file: {path}");

            long existingDataLength = reader.ReadInt64();
            // reader.ReadInt64(); // skip ConfigOffset — we'll overwrite it

            // 2. Determine pixel data length
            long dataLength = existingDataLength > 0
                ? existingDataLength                  // existing .mxd with known data length
                : fs.Length - HeaderSize;              // temp vessel (ConfigOffset==0): data fills to EOF

            // 3. Truncate any old trailer (no-op if file is already at the right size)
            fs.SetLength(HeaderSize + dataLength);

            // 4. Append new trailer at the end
            fs.Seek(0, SeekOrigin.End);
            long configOffset = fs.Position;
            var config = new MatrixDataConfig(data) with { IsCompressed = false };
            byte[] configBytes = Encoding.UTF8.GetBytes(config.ToHeaderString());
            writer.Write(configBytes);

            // 5. Back-patch header
            fs.Seek(4, SeekOrigin.Begin);
            writer.Write(dataLength);
            writer.Write(configOffset);
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
    public class MxBinaryFormat: IMatrixDataWriter, IMatrixDataReader, IProgressReportable, IVirtualLoadable, ICompressible
    {
        public string FormatName => "MxPlot Binary";

        public IReadOnlyList<string> Extensions { get; } = [".mxd"];

        public bool CompressionInWrite { get; set; } = false;
        public LoadingMode LoadingMode { get; set; } = LoadingMode.InMemory;
        public IProgress<int>? ProgressReporter { get; set; } = null;
        private CancellationToken _ct;
        CancellationToken IMatrixDataReader.CancellationToken { get => _ct; set => _ct = value; }
        bool IMatrixDataReader.IsCancellable => true;
        public void Write<T>(string filePath, MatrixData<T> data, IBackendAccessor accessor) where T : unmanaged
        {
            // Fast-path: if the backing store is a WVSF whose file is already in .mxd layout
            // and compression is not requested, use OS-level file move + trailer finalization.
            if (!CompressionInWrite
                && accessor.TryGet<WritableVirtualStrippedFrames<T>>(out var wvsf)
                && wvsf!.FilePath.EndsWith(".mxd", StringComparison.OrdinalIgnoreCase))
            {
                wvsf.SaveAs(filePath,
                    beforeRemount: path => MatrixDataSerializer.FinalizeTrailer(path, data),
                    ProgressReporter);
            }
            else
            {
                MatrixDataSerializer.Save(filePath, data, CompressionInWrite, ProgressReporter);
            }
        }
        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            var mode = ResolveLoadingMode(filePath);
            if (mode == LoadingMode.Virtual)
                return MatrixDataSerializer.LoadVirtual<T>(filePath);
            return MatrixDataSerializer.Load<T>(filePath, ProgressReporter, _ct);
        }
        public IMatrixData Read(string path)
        {
            var mode = ResolveLoadingMode(path);
            if (mode == LoadingMode.Virtual)
                return MatrixDataSerializer.LoadDynamicVirtual(path);
            return MatrixDataSerializer.LoadDynamic(path, ProgressReporter, _ct);
        }

        private LoadingMode ResolveLoadingMode(string filePath)
        {
            if (LoadingMode != LoadingMode.Auto) return LoadingMode;
            long fileSize = new FileInfo(filePath).Length;
            var config = MatrixDataSerializer.ReadHeaderConfig(filePath);
            return VirtualPolicy.Resolve(
                LoadingMode, fileSize, frameCount: config.FrameCount, canVirtual: !config.IsCompressed);
        }

        /// <summary>
        /// Creates a virtual frame builder that allocates .mxd-backed writable virtual storage.
        /// </summary>
        /// <param name="width">Width (pixels per row).</param>
        /// <param name="height">Height (rows per frame).</param>
        /// <param name="frameCount">Number of frames to allocate.</param>
        /// <returns>An <see cref="IVirtualFrameBuilder"/> that produces a writable <see cref="MatrixData{T}"/>.</returns>
        /// <example>
        /// <code>
        /// var builder = MxBinaryFormat.AsVirtualBuilder(512, 512, 100);
        /// var data = MatrixData&lt;float&gt;.CreateVirtual(null, builder);      // temp .mxd
        /// var data2 = MatrixData&lt;float&gt;.CreateVirtual("out.mxd", builder); // explicit path
        /// </code>
        /// </example>
        public static IVirtualFrameBuilder AsVirtualBuilder(int width, int height, int frameCount)
        {
            return new MxdVirtualBuilder(width, height, frameCount);
        }

        private class MxdVirtualBuilder : IVirtualFrameBuilder
        {
            private readonly int _width, _height, _frameCount;

            public MxdVirtualBuilder(int width, int height, int frameCount)
            {
                _width = width;
                _height = height;
                _frameCount = frameCount;
            }

            public MatrixData<T> CreateWritable<T>(string? filePath) where T : unmanaged
            {
                if (filePath != null
                    && !filePath.EndsWith(".mxd", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("File extension must be .mxd", nameof(filePath));

                var wvsf = MatrixDataSerializer.CreateVessel<T>(filePath, _width, _height, _frameCount);
                return MatrixData<T>.CreateAsVirtualFrames(_width, _height, wvsf);
            }
        }
    }
}
