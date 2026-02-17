using MxPlot.Core;
using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Extensions.Hdf5
{
    /// <summary>
    /// Provides functionality for exporting matrix data to the HDF5 (Hierarchical Data Format) container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This format is suitable for large-scale datasets and integration with Python/MATLAB workflows.
    /// Note: This implementation currently supports <b>write-only</b> operations due to the highly 
    /// flexible and varied internal structure of generic HDF5 files.
    /// </para>
    /// <para>
    /// Usage Example:
    /// <code>
    /// var hdf5 = new Hdf5Format { 
    ///     GroupPath = "experiment/session1/results",  //deafult = "matrix_data"
    ///     FlipY = true 
    /// };
    /// matrix.Save("data.h5", hdf5);
    /// </code>
    /// </para>
    /// </remarks>
    public class Hdf5Format: IMatrixDataWriter
    {
        //public IProgress<int>? ProgressReporter { get; set; }
        /// <summary>
        /// Gets or sets the internal HDF5 group path where the dataset will be stored.
        /// Defaults to "matrix_data".
        /// </summary>
        public string GroupPath { get; set; } = "matrix_data";

        /// <summary>
        /// Gets or sets a value indicating whether to flip the data along the Y-axis to 
        /// match standard coordinate systems (bottom-left origin).
        /// </summary>
        public bool FlipY { get; set; } = true;

        public void Write<T>(string filePath, MatrixData<T> data) where T : unmanaged
        {
            Hdf5Handler.Save(filePath, data, GroupPath, FlipY);
        }
    }
}
