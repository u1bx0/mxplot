using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core.IO
{
    
    /// <summary>
    /// Defines a method for reading matrix data from a file into a strongly typed structure.
    /// </summary>
    /// <remarks>Implementations of this interface are responsible for parsing files containing matrix data
    /// and returning the data in a type-safe manner. The generic type parameter allows reading matrices of various
    /// unmanaged numeric types, such as integers or floating-point values.</remarks>
    public interface IMatrixDataReader
    {
        /// <summary>
        /// Reads matrix data from the specified file and returns it as a MatrixData<T> instance.
        /// </summary>
        /// <typeparam name="T">The unmanaged value type of the matrix elements to read from the file.</typeparam>
        /// <param name="filePath">The path to the file containing the matrix data to read. Cannot be null or empty.</param>
        /// <returns>A MatrixData<T> object containing the matrix data read from the specified file.</returns>
        MatrixData<T> Read<T>(string filePath) where T : unmanaged;

        /// <summary>
        /// Reads matrix data from the specified file path.
        /// </summary>
        /// <param name="path">The path to the file containing the matrix data to read. Cannot be null or empty.</param>
        /// <returns>An <see cref="IMatrixData"/> instance containing the data read from the file.</returns>
        IMatrixData Read(string path);
    }

    /// <summary>
    /// Defines a method for writing matrix data to a file in a specific format.
    /// </summary>
    /// <remarks>Implementations of this interface are responsible for persisting matrix data to disk. The
    /// format and structure of the output file depend on the concrete implementation. This interface is typically used
    /// to abstract file output for different matrix storage formats.</remarks>
    public interface IMatrixDataWriter
    {
        void Write<T>(string filePath, MatrixData<T> data) where T : unmanaged;
    }
}
