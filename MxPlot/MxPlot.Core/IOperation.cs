using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// Represents an abstract operation that can be applied to an <see cref="IMatrixData"/> source.
    /// This interface serves as a marker for the Visitor-like pattern used to dispatch specific processing logic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concrete implementations (e.g., <see cref="MatrixData{T}"/>) inspect the specific runtime type of the <see cref="IOperation"/>
    /// (such as <see cref="IVolumeOperation"/> or <see cref="IFilterOperation"/>) within their <c>Apply</c> method.
    /// This design decouples the definition of an operation from the generic type parameters required to execute it,
    /// allowing the UI layer to define operations without knowledge of the underlying data type <typeparamref name="T"/>.
    /// </para>
    /// <para>
    /// <strong>Implementation Guideline:</strong><br/>
    /// Implementations of this interface represent immutable operation descriptors (commands).
    /// It is highly recommended to use <see langword="record"/> types for concrete implementations
    /// to ensure immutability, structural equality, and built-in string representation for debugging.
    /// </para>
    /// </remarks>
    public interface IOperation { }

    /// <summary>
    /// Defines a contract for operations that process matrix data and return the result as an IMatrixData instance.
    /// </summary>
    /// <remarks>Implementations of this interface should provide specific matrix data operations. The generic
    /// type parameter T must be unmanaged to ensure efficient memory usage and compatibility with low-level
    /// operations.</remarks>
    public interface IMatrixDataOperation : IOperation
    {
        IMatrixData Execute<T>(MatrixData<T> data) where T : unmanaged; 
    }
}
