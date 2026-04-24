using BitMiracle.LibTiff.Classic;
using MxPlot.Core;
using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Text;
using static MxPlot.Extensions.Tiff.OmeTiffHandler;


namespace MxPlot.Extensions.Tiff
{
    /// <summary>
    /// Provides functionality for reading, writing, and virtually allocating matrix data in OME-TIFF format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This format supports high-bit-depth images and comprehensive microscopy metadata.
    /// The OME-XML header is preserved to ensure compatibility with bio-imaging software like ImageJ/Fiji.
    /// </para>
    /// <para>
    /// <b>Advanced Architecture:</b><br/>
    /// This class acts as a comprehensive I/O provider. In addition to standard read/write operations, 
    /// it can generate an <see cref="IVirtualFrameBuilder"/> via <see cref="AsVirtualBuilder"/> to allocate 
    /// memory-mapped OME-TIFF files. Furthermore, its <see cref="Write{T}"/> implementation supports a 
    /// "Fast Path" (zero-copy) save: if the data is uncompressed and backed by a compatible virtual storage, 
    /// it bypasses pixel-by-pixel encoding and performs an OS-level file move.
    /// </para>
    /// <para>
    /// For large files, you can monitor progress by setting the <see cref="ProgressReporter"/> property.
    /// </para>
    /// <para>
    /// Usage Example:
    /// <code>
    /// var format = new OmeTiffFormat { 
    ///     ProgressReporter = new Progress&lt;int&gt;(v => Console.WriteLine($"Loading: {v}%")) 
    /// };
    /// var matrix = MatrixData&lt;ushort&gt;.Load("sample.ome.tif", format);
    /// </code>
    /// </para>
    /// </remarks>
    public class OmeTiffFormat : IMatrixDataReader, IMatrixDataWriter, IProgressReportable, IVirtualLoadable, ICompressible
    {
        #region IFileFormatDescriptor

        public string FormatName => "OME-TIFF";

        /// <summary>
        /// Supported extensions for matching. Includes <c>.ome</c> as a short alias first,
        /// so the OS save dialog auto-appends <c>.ome</c> (a single token) instead of
        /// the compound <c>.ome.tif</c>, avoiding the accumulation problem.
        /// The writer normalizes <c>.ome</c> → <c>.ome.tif</c> before actually writing.
        /// The canonical file extension is <c>.ome.tif</c>.
        /// </summary>
        public IReadOnlyList<string> Extensions { get; } = [".ome", ".ome.tif", ".ome.tiff"];

        #endregion

        #region Logics for IMatrixDataReader and IMatrixDataWriter
        /// <summary>
        /// Gets or sets the progress reporter for tracking the read/write operations.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Progress reporting behavior:
        /// <list type="bullet">
        /// <item>At start: Reports the total frame count as a <b>negative</b> value (e.g., -100).</item>
        /// <item>During processing: Reports the <b>index</b> of the currently completed frame (0-based).</item>
        /// <item>At end: Reports the total frame count as a <b>positive</b> value.</item>
        /// </list>
        /// </para>
        /// </remarks>
        public IProgress<int>? ProgressReporter { get; set; } = null;

        /// <summary>
        /// Gets or sets loading mode of OME-TIFF file.
        /// </summary>
        public LoadingMode LoadingMode { get; set; } = LoadingMode.Auto;

        /// <summary>
        /// Gets or sets a value indicating whether to apply compression when writing OME-TIFF files.
        /// When Virtual (virtual frames) mode is used, this flag is ignored and compression is always disabled to allow for memory-mapped access.
        /// </summary>
        public bool CompressionInWrite { get; set; } = true;

        /// <summary>
        /// Maximum degree of parallelism for InMemory loading.
        /// <c>0</c> or <c>1</c> = sequential (default).
        /// <c>N &gt; 1</c> = open N LibTiff handles and decompress frames in parallel.
        /// Primarily useful for LZW/Deflate-compressed files; uncompressed files are already fast.
        /// </summary>
        public int MaxParallelDegree { get; set; } = 0;

        private CancellationToken _ct;
        CancellationToken IMatrixDataReader.CancellationToken { get => _ct; set => _ct = value; }
        bool IMatrixDataReader.IsCancellable => true;

        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            IMatrixData data = this.Read(filePath);

            // check the type
            if (data is MatrixData<T> typedData)
            {
                return typedData; // finish
            }
            else
            {
                //if the type is different from the specified type T
                // throw an exception or convert the data
                throw new InvalidOperationException(
                    $"File contained '{data.GetType().GetGenericArguments()[0].Name}' data, " +
                    $"but '{typeof(T).Name}' was requested.");
            }
        }

        public IMatrixData Read(string path)
        {
            try
            {
                var md = OmeTiffHandler.Load(path, LoadingMode, ProgressReporter, MaxParallelDegree, _ct);
                if (md == null) throw new InvalidDataException("Loaded data was null.");
                return md;
            }
            catch (IOException ex) when (IsFileLockError(ex))
            {
                throw new IOException(
                    $"Cannot open '{Path.GetFileName(path)}' because it is locked by another process. " +
                    $"If this file was created or saved via MatrixData.CreateVirtual / SaveAs, " +
                    $"the backing instance must be Disposed before calling Load. " +
                    $"Pattern: md.SaveAs(path, format); md.Dispose(); var loaded = Load(path, format);",
                    ex);
            }
        }

        /// <summary>
        /// Returns true when the IOException indicates a sharing violation (file in use),
        /// as opposed to file-not-found or other I/O errors.
        /// ERROR_SHARING_VIOLATION = Win32 error 32 → HRESULT 0x80070020
        /// </summary>
        private static bool IsFileLockError(IOException ex)
            => (ex.HResult & 0xFFFF) == 0x0020; // Win32 ERROR_SHARING_VIOLATION



        /// <summary>
        /// Writes the specified matrix data to a file.
        /// </summary>
        /// <typeparam name="T">The unmanaged value type of the matrix elements.</typeparam>
        /// <param name="filePath">The destination path where the matrix data will be saved.</param>
        /// <param name="data">The <see cref="MatrixData{T}"/> instance containing the data to write.</param>
        /// <param name="accessor">
        /// An accessor that safely provides the underlying backing store. The writer uses this to query 
        /// for compatible virtual frames (e.g., <see cref="WritableVirtualStrippedFrames{T}"/>) 
        /// to execute highly optimized, zero-copy file moves.
        /// </param>
        private static bool IsOmeTiffPath(string path)
            => path.EndsWith(".ome.tif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ome.tiff", StringComparison.OrdinalIgnoreCase);

        public void Write<T>(string filePath, MatrixData<T> data, IBackendAccessor accessor) where T : unmanaged
        {
            // Normalize short alias: .ome → .ome.tif (canonical OME-TIFF extension)
            if (filePath.EndsWith(".ome", StringComparison.OrdinalIgnoreCase) && !IsOmeTiffPath(filePath))
                filePath += ".tif";

            if (accessor.TryGet<WritableVirtualStrippedFrames<T>>(out var wvsf)
                && IsOmeTiffPath(wvsf!.FilePath))
            {
                // Fast path: backing file is already a valid OME-TIFF layout.
                // OS-level move without re-encoding.
                wvsf.SaveAs(filePath, progress: ProgressReporter);
            }
            else
            {
                // General path: save using the handler, which will read the data and write with the specified compression.
                OmeTiffOptions options = new OmeTiffOptions();
                options.Compression = CompressionInWrite ? Compression.LZW : Compression.NONE;
                OmeTiffHandler.Save(filePath, data, options, ProgressReporter);
            }
        }
        #endregion

        #region Static methods for CreateVirtual

        /// <summary>
        /// Creates a virtual frame builder specific to the OME-TIFF format using the provided metadata specification.
        /// </summary>
        /// <param name="spec">The structural specification (dimensions, channels, resolution, etc.) for the virtual frames.</param>
        /// <returns>An <see cref="IVirtualFrameBuilder"/> capable of constructing a writable <see cref="MatrixData{T}"/>.</returns>
        public static IVirtualFrameBuilder AsVirtualBuilder(HyperstackMetadata spec)
        {
            return new OmeTiffVirtualBuilder(spec);
        }

        private class OmeTiffVirtualBuilder : IVirtualFrameBuilder
        {
            private readonly HyperstackMetadata _spec;

            public OmeTiffVirtualBuilder(HyperstackMetadata spec) => _spec = spec;

            /// <summary>
            /// 
            /// </summary>
            /// <typeparam name="T"></typeparam>
            /// <param name="path"></param>
            /// <returns></returns>
            public MatrixData<T> CreateWritable<T>(string? filePath) where T : unmanaged
            {
                bool isTemporary = false;

                // 1. If no path is specified, generate a temporary path and take ownership of the file lifecycle.
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    filePath = Path.Combine(Path.GetTempPath(), $"MxTemp_{Guid.NewGuid():N}.ome.tif");
                    isTemporary = true;
                }
                else
                {
                    // Extension check applies only when the user explicitly provides a path.
                    if (!filePath.EndsWith(".ome.tiff", StringComparison.OrdinalIgnoreCase) &&
                        !filePath.EndsWith(".ome.tif", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("File extension must be .ome.tiff or .ome.tif", nameof(filePath));
                }

                // Compression.NONE is mandatory for MMF direct access
                var options = new OmeTiffOptions { Compression = BitMiracle.LibTiff.Classic.Compression.NONE, UseBigTiff = true };

                var handler = new OmeTiffHandlerInstance<T>();

                // Fast vessel creation:
                // - Computes OME-XML and all IFD/strip offsets arithmetically (no disk I/O for pixel data)
                // - Writes only header + IFDs (few KB total)
                // - FileStream.SetLength() pre-allocates pixel data space in O(1) on NTFS
                // No skeleton write, no re-read; offsets are returned directly from the builder.
                var (offsets, byteCounts) = handler.BuildVesselFast(filePath, _spec);

                // Open as WritableVirtualStrippedFrames (MMF, read-write)
                // isYFlipped=true: TIFF stores top-to-bottom, MatrixData uses bottom-left origin
                // isTemporary: when the filePath is explicitly provided by the user, we assume they will manage the file lifecycle; 
                // when we generate a temp path, we take responsibility for cleanup.
                var wvsf = new WritableVirtualStrippedFrames<T>(
                    filePath, _spec.Width, _spec.Height, offsets, byteCounts, isYFlipped: true, isTemporary: isTemporary);

                // Wrap in MatrixData and apply scale / axis from spec
                var md = MatrixData<T>.CreateAsVirtualFrames(_spec.Width, _spec.Height, wvsf);
                ApplyMetadataToMatrixData(md, _spec);

                return md;
            }
        }

        #endregion
    }
}
