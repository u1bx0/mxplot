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
    /// <para>
    /// <strong>Purpose:</strong><br/>
    /// A caller that holds only an <see cref="IMatrixData"/> (a UI, for instance) can run algorithms written
    /// for a known element type without knowing that type. It passes the request to <c>Apply</c> as a value,
    /// and <see cref="MatrixData{T}"/>, which knows <c>T</c>, executes it. Each algorithm is written once
    /// and is available both ways: as a typed extension method (<c>typed.Crop(...)</c>) and through <c>Apply</c>.
    /// </para>
    /// <para>
    /// <strong>Operation and operator:</strong><br/>
    /// An operation says <i>what</i> is to be done; an operator does it. The operator is a static
    /// <c>XxxOperator</c> class of typed extension methods over <see cref="MatrixData{T}"/>, in a file named
    /// after it. The operation is the <c>XxxOperation</c> record that carries the parameters; a category
    /// covering several operations keeps its operations in a separate <c>XxxOperations</c> file, while a
    /// self-contained single feature keeps its one operation in the operator's own file. The algorithm
    /// belongs in the operator: <c>Execute&lt;T&gt;</c> only forwards (through <c>dynamic</c>
    /// where the operator's constraints are stricter than <c>unmanaged</c>).
    /// </para>
    /// <para>
    /// <strong>When an operation is needed:</strong><br/>
    /// Only for an algorithm that is to be invoked on an <see cref="IMatrixData"/> without knowing its
    /// element type, yet has to see that type to run (copying or slicing frame buffers, for instance).
    /// An algorithm that can be expressed through the double-valued accessors of <see cref="IMatrixData"/>,
    /// such as sampling a line profile, is written as an extension method on <see cref="IMatrixData"/>
    /// and needs no operation. One meant only for callers that already hold a <see cref="MatrixData{T}"/>,
    /// such as element-wise arithmetic, is just a typed extension method.
    /// </para>
    /// <para>
    /// <strong>Frames:</strong><br/>
    /// An operator processes every frame of the data it is given. To process one frame, the caller slices it
    /// out first and passes the result (<c>data.SliceAt(index).Xxx(...)</c>): the slice shares the frame
    /// buffer instead of copying it, and it carries no metadata, so a caller that needs the source's
    /// properties copies them onto the result. An operator therefore needs no frame index and no
    /// single-frame branch. The exceptions are data accessors (<c>GetArray</c>, <c>GetValueAt</c>, ...),
    /// where a frame index of -1 means <see cref="IMatrixData.ActiveIndex"/>, and methods for which the
    /// index itself matters, such as a transform that writes into a given frame of a destination.
    /// </para>
    /// </remarks>
    /// <example>
    /// The operation is a description of the request; the operator does the work:
    /// <code>
    /// // The operation (DimensionalOperations.cs): parameters, and a forward to the operator.
    /// public record CropOperation(int X, int Y, int Width, int Height, ...) : IMatrixDataOperation
    /// {
    ///     public IMatrixData Execute&lt;T&gt;(MatrixData&lt;T&gt; src) where T : unmanaged
    ///         => src.Crop(X, Y, Width, Height, Progress, CancellationToken);  // DimensionalOperator.Crop
    /// }
    ///
    /// // A caller that holds only an IMatrixData, and so does not know T:
    /// IMatrixData cropped = data.Apply(new CropOperation(10, 10, 64, 64));
    ///
    /// // A caller that knows T calls the operator directly:
    /// MatrixData&lt;float&gt; cropped = typed.Crop(10, 10, 64, 64);
    /// </code>
    /// </example>
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