using MxPlot.Core;
using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Text;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MxPlot.Extensions.Tiff
{

    /// <summary>
    /// Provides functionality for reading and writing matrix data in OME-TIFF format.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This format supports high-bit-depth images and comprehensive microscopy metadata.
    /// The OME-XML header is preserved to ensure compatibility with bio-imaging software like ImageJ/Fiji.
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
    /// var matrix = MatrixData.Load("sample.ome.tif", format);
    /// </code>
    /// </para>
    /// </remarks>
    public class OmeTiffFormat : IMatrixDataReader, IMatrixDataWriter
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
            var md = OmeTiffHandler.Load(path, ProgressReporter);
            if (md == null) throw new InvalidDataException("Loaded data was null.");
            return md;
        }

        public void Write<T>(string filePath, MatrixData<T> data) where T : unmanaged
        {
            OmeTiffHandler.Save(filePath, data, ProgressReporter);
        }
    }
}
