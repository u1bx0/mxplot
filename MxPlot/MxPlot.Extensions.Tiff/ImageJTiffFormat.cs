using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.Core.IO.Formats;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Text;

namespace MxPlot.Extensions.Tiff
{

    /// <summary>
    /// Provides functionality for reading and writing TIFF files compatible with ImageJ.
    /// Supports both standard grayscale TIFFs and ImageJ Hyperstacks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Compatibility:</b> On saving, this format includes ImageJ-specific strings in the <c>ImageDescription</c> tag. 
    /// This allows standard TIFF viewers to open the file normally, while ImageJ can recognize it as a scaled stack or hyperstack.
    /// </para>
    /// <para>
    /// <b>Axes:</b> Channel, Z and Time/Timelapse axes are written as the channels, slices and frames of an
    /// ImageJ hyperstack. Any other axis is written as slices (or as frames when slices is taken) and its name
    /// is recorded in the file so that it is restored on load. Data with more axes than these three dimensions
    /// can hold is written as a plain stack: the frames keep their order but the axes are not stored.
    /// </para>
    /// <para>
    /// <b>Type Safety:</b> Currently supports only <see cref="byte"/> (8-bit) and <see cref="ushort"/> (16-bit). 
    /// The bit-depth of the file must strictly match the requested type <c>T</c>; otherwise, an exception is thrown.
    /// </para>
    /// <para>
    /// Example:
    /// <code>
    /// // Save as a standard-looking TIFF but with ImageJ metadata
    /// var format = new ImageJTiffFormat();
    /// matrix.Save("imagej_ready.tif", format);
    /// </code>
    /// </para>
    /// </remarks>
    public class ImageJTiffFormat : IMatrixDataReader, IMatrixDataWriter, IProgressReportable, IVirtualLoadable, ICompressible
    {
        public string FormatName => "TIFF (ImageJ-compatible)";

        public IReadOnlyList<string> Extensions { get; } = [".tif", ".tiff"];

        /// <summary>
        /// Gets or sets loading mode of the file. Virtual loading maps an uncompressed strip-organized
        /// file (one IFD per frame, or the single-IFD "packed" layout) through memory-mapped access, and
        /// decodes any other layout (compressed, tiled) frame by frame on access.
        /// </summary>
        public LoadingMode LoadingMode { get; set; } = LoadingMode.Auto;

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

        private CancellationToken _ct;
        CancellationToken IMatrixDataReader.CancellationToken { get => _ct; set => _ct = value; }
        bool IMatrixDataReader.IsCancellable => true;

        /// <summary>
        /// Maximum degree of parallelism for InMemory loading of compressed data.
        /// <c>-1</c> = all logical cores (default).
        /// <c>0</c> or <c>1</c> = sequential.
        /// <c>N &gt; 1</c> = use N threads.
        /// Uncompressed data always reads sequentially: it is I/O-bound.
        /// </summary>
        public int MaxParallelDegree { get; set; } = -1;

        /// <summary>
        /// Gets or sets whether frames are LZW-compressed when writing (default <c>true</c>).
        /// Uncompressed files can be memory-mapped by a Virtual load.
        /// </summary>
        public bool CompressionInWrite { get; set; } = true;

        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            return ImageJTiffHandler.Load<T>(filePath, ProgressReporter, _ct, MaxParallelDegree, LoadingMode);
        }

        public IMatrixData Read(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");

            string typeName;
            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(path, "r"))
            {
                if (tiff == null)
                    throw new IOException($"Failed to open TIFF file: {path}");
                typeName = TiffPixelType.NameOf(tiff);
            }

            return typeName switch
            {
                "uint8" => Load<byte>(path),
                "int8" => Load<sbyte>(path),
                "uint16" => Load<ushort>(path),
                "int16" => Load<short>(path),
                "uint32" => Load<uint>(path),
                "int32" => Load<int>(path),
                "float" => Load<float>(path),
                "double" => Load<double>(path),
                _ => throw new NotSupportedException($"Unsupported pixel type detected: {typeName}.")
            };
        }

        private IMatrixData Load<T>(string path) where T : unmanaged
            => ImageJTiffHandler.Load<T>(path, ProgressReporter, _ct, MaxParallelDegree, LoadingMode);

        public VirtualLoadProbe ProbeVirtualLoad(string filePath) => ImageJTiffHandler.ProbeVirtualLoad(filePath);

        public void Write<T>(string filePath, MatrixData<T> data, IBackendAccessor accessor) where T : unmanaged
        {
            ImageJTiffHandler.Save(filePath, data, ProgressReporter, CompressionInWrite);
        }
    }
}
