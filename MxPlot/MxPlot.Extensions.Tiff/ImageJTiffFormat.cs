using MxPlot.Core;
using MxPlot.Core.IO;
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
    public class ImageJTiffFormat : IMatrixDataReader, IMatrixDataWriter, IProgressReportable
    {
        public string FormatName => "TIFF (ImageJ-compatible)";

        public IReadOnlyList<string> Extensions { get; } = [".tif", ".tiff"];

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
        /// Maximum degree of parallelism for loading compressed files.
        /// <c>1</c> = sequential.
        /// <c>-1</c> = auto (use all available cores, default).
        /// <c>N &gt; 1</c> = use N threads.
        /// Effective only for LZW/Deflate-compressed files; uncompressed files are I/O-bound and gain nothing.
        /// </summary>
        public int MaxParallelDegree { get; set; } = 1;

        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            if (typeof(T) == typeof(byte) || typeof(T) == typeof(ushort))
            {
                var md = ImageJTiffHandler.Load<T>(filePath, ProgressReporter, _ct, MaxParallelDegree);
                return md;
            }
            else
            {
                throw new NotSupportedException($"Unsupported pixel type: {typeof(T).Name}. Only byte and ushort are supported.");
            }
        }

        public IMatrixData Read(string path)
        {
            string typeName = OmeTiffReader.DetectPixelType(path);
            if (typeName == "uint16"){
                return ImageJTiffHandler.Load<ushort>(path, ProgressReporter, _ct, MaxParallelDegree);
            } 
            else if (typeName == "uint8")
            {
                return ImageJTiffHandler.Load<byte>(path, ProgressReporter, _ct, MaxParallelDegree);
            }
            else
            {
                throw new NotSupportedException($"Unsupported pixel type detected: {typeName}. Only uint8 and uint16 are supported.");
            }
        }

        public void Write<T>(string filePath, MatrixData<T> data, IBackendAccessor accessor) where T : unmanaged
        {
            ImageJTiffHandler.Save(filePath, data, ProgressReporter);
        }
    }
}
