using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// Represents an abstract operation that can be applied to an <see cref="IMatrixData"/> source, 
    /// returning a result of type <typeparamref name="TResult"/>.
    /// This interface serves as a marker for the Visitor-like pattern used to dispatch specific processing logic.
    /// </summary>
    /// <typeparam name="TResult">The covariant return type of the operation. This allows operations to return single matrices, multiple matrices (tuples), or scalar values.</typeparam>
    /// <remarks>
    /// <para>
    /// Concrete implementations (e.g., <see cref="MatrixData{T}"/>) inspect the specific runtime type of the <see cref="IOperation{TResult}"/>
    /// (such as <c>IVolumeOperation{TResult}</c> or <see cref="IMatrixDataOperation"/>) within their <c>Apply</c> method.
    /// This design decouples the definition of an operation from the generic type parameters required to execute it,
    /// allowing the UI layer to define operations and receive strongly-typed results without knowledge of the underlying data type <c>T</c>.
    /// </para>
    /// <para>
    /// <strong>Implementation Guideline:</strong><br/>
    /// Implementations of this interface represent immutable operation descriptors (commands).
    /// It is highly recommended to use <see langword="record"/> types for concrete implementations
    /// to ensure immutability, structural equality, and built-in string representation for debugging.
    /// </para>
    /// </remarks>
    public interface IOperation<out TResult>
    {
    }

    /// <summary>
    /// Defines a contract for operations that process matrix data and specifically return the result as an <see cref="IMatrixData"/> instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations of this interface should provide specific matrix data operations. The generic
    /// type parameter <c>T</c> must be unmanaged to ensure efficient memory usage and compatibility with low-level
    /// operations.
    /// </para>
    /// <para>
    /// By inheriting from <see cref="IOperation{IMatrixData}"/>, concrete implementations (like Transpose or Crop) 
    /// automatically integrate with the <c>Apply</c> pipeline and seamlessly return an <see cref="IMatrixData"/>.
    /// </para>
    /// </remarks>
    public interface IMatrixDataOperation : IOperation<IMatrixData>
    {
        /// <summary>
        /// Executes the matrix data operation.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type of the matrix.</typeparam>
        /// <param name="data">The typed matrix data instance to process.</param>
        /// <returns>A new <see cref="IMatrixData"/> representing the processed result.</returns>
        IMatrixData Execute<T>(MatrixData<T> data) where T : unmanaged;
    }
}