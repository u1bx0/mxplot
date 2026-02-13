using MxPlot.Core;
using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
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
    public class ImageJTiffFormat : IMatrixDataReader, IMatrixDataWriter
    {
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

        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            var md = ImageJTiffHandler.Load<T>(filePath, ProgressReporter);
            return md;
        }

        public IMatrixData Read(string path)
        {
            //needs consideration for data type. Currently, ushot and byte are supported to write.
            var md = ImageJTiffHandler.Load<ushort>(path, ProgressReporter);
            return md;
        }

        public void Write<T>(string filePath, MatrixData<T> data) where T : unmanaged
        {
            ImageJTiffHandler.Save(filePath, data, ProgressReporter);
        }
    }
}
