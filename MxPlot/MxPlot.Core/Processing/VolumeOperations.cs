using Microsoft.CSharp.RuntimeBinder;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace MxPlot.Core.Processing
{
    // ========================================================================================================
    // File Overview: Volume Operations Bridge
    // ========================================================================================================
    // This file defines IVolumeOperation and the concrete implementations (Visitor Pattern).
    // These classes act as a bridge between the non-generic application layer and the highly generic Core layer.
    //
    // Key Responsibilities:
    // 1. Contextual Encapsulation: Each class captures not only the operation parameters (axis, mode) 
    //    but also the spatial context (AxisName, BaseIndices). This allows the 'Apply' method to 
    //    reconstruct the appropriate VolumeAccessor view before execution.
    // 2. Type Bridging: The Execute<T> method resolves the specific type T at runtime, enabling 
    //    calls to generic extension methods.
    // 3. Dynamic Dispatch: Uses dynamic binding for operations with strict constraints (like Projection)
    //    to maintain a clean interface while supporting generic numeric processing.
    // ========================================================================================================

    /// <summary>
    /// Defines an operation that can be performed on a <see cref="VolumeAccessor{T}"/>.
    /// <para>
    /// This interface plays the role of the "Visitor" in the Double Dispatch pattern.
    /// It allows the definition of algorithms (like slicing, cropping, or processing) 
    /// to be separated from the data structure itself.
    /// </para>
    /// </summary>
    public interface IVolumeOperation: IOperation
    {
        string AxisName { get; }
        int[]? BaseIndices { get; }

        /// <summary>
        /// Executes the operation on the provided typed accessor.
        /// </summary>
        /// <typeparam name="T">The underlying data type of the volume (e.g., int, float, double).</typeparam>
        /// <param name="accessor">The typed accessor instance that invoked this operation.</param>
        /// <returns>
        /// The result of the operation, wrapped in a non-generic <see cref="IMatrixData"/> 
        /// to maintain type erasure for the caller.
        /// </returns>
        IMatrixData Execute<T>(VolumeAccessor<T> accessor) where T : unmanaged;
    }

    /// <summary>
    /// Represents an operation to extract a specific 2D slice from the volume.<br/>
    /// <c>e.g. IMatrixData processed = src.Apply(new SliceOperation(ViewFrom.Y, 0));</c>
    /// </summary>
    /// <param name="Axis">The axis along which to slice.</param>
    /// <param name="Index">The zero-based index of the slice.</param>
    /// <param name="AxisName">Optional:  Name for the axis mapping.</param>
    /// <param name="BaseIndices">Optional indices to define the sub-region or base coordinates.</param>
    public record SliceOperation(ViewFrom Axis, int Index, string AxisName = "", int[]? BaseIndices = null)
        : IVolumeOperation
    {
        /// <summary>
        /// Executes the slice operation on the accessor provided by the 'Apply' method.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor, pre-configured with the operation's spatial context.</param>
        /// <returns>A 2D <see cref="IMatrixData"/> representing the slice.</returns>
        public IMatrixData Execute<T>(VolumeAccessor<T> accessor)
            where T : unmanaged
            => accessor.SliceAt(Axis, Index);
    }

    /// <summary>
    /// Represents an operation to reorganize the internal memory layout of the volume (Restack).<br/>
    /// <c>e.g. IMatrixData processed = src.Apply(new RestackOperation(ViewFrom.Y));</c>
    /// </summary>
    /// <param name="NewAxis">The target axis to become the primary dimension.</param>
    /// <param name="AxisName">Optional: Name for the axis mapping.</param>
    /// <param name="BaseIndices">Optional indices to define the sub-region.</param>
    public record RestackOperation(ViewFrom NewAxis, string AxisName = "", int[]? BaseIndices = null)
        : IVolumeOperation
    {
        /// <summary>
        /// Executes the restack operation, creating a new volume view with reordered axes.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor to be restacked.</param>
        /// <returns>A 3D <see cref="IMatrixData"/> with the modified layout.</returns>
        public IMatrixData Execute<T>(VolumeAccessor<T> accessor)
            where T : unmanaged
            => accessor.Restack(NewAxis);
    }

    /// <summary>
    /// Represents an operation to create a 2D projection (MIP, MinIP, Average) along a specific axis.<br/>
    /// <c>e.g. IMatrixData processed = src.Apply(new ProjectionOperation(ViewFrom.Y, ProjectionMode.Maximum));</c>
    /// </summary>
    /// <remarks>
    /// Requires <typeparamref name="T"/> to support <see cref="System.Numerics.INumber{T}"/>.
    /// Uses dynamic dispatch to invoke constrained generic methods at runtime.
    /// </remarks>
    /// <param name="Axis">The axis along which to project.</param>
    /// <param name="Mode">The projection mode.</param>
    /// <param name="AxisName">Optional: Name for the axis mapping.</param>
    /// <param name="BaseIndices">Optional indices to define the sub-region.</param>
    public record ProjectionOperation(ViewFrom Axis, ProjectionMode Mode, string AxisName = "", int[]? BaseIndices = null)
        : IVolumeOperation
    {
        /// <summary>
        /// Executes the projection operation using dynamic dispatch for numeric type support.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor to project.</param>
        /// <returns>A 2D <see cref="IMatrixData"/> representing the projected image.</returns>
        /// <exception cref="NotSupportedException">Thrown if T is not a numeric type.</exception>
        public IMatrixData Execute<T>(VolumeAccessor<T> accessor) where T : unmanaged
        {
            dynamic vol = accessor;

            try
            {
                return VolumeAccessorExtensions.CreateProjection(vol, Axis, Mode);
            }
            catch (RuntimeBinderException)
            {
                throw new NotSupportedException(
                    $"Type '{typeof(T).Name}' does not support Projection operations. " +
                    "Projection requires numeric types (INumber<T>)."
                );
            }
        }
    }
}
