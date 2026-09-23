using Microsoft.CSharp.RuntimeBinder;
using MxPlot.Core.IO.Formats;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;

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
    /// <typeparam name="TResult">The covariant return type of the operation.</typeparam>
    public interface IVolumeOperation<out TResult> : IOperation<TResult>
    {
        string AxisName { get; }
        int[]? BaseIndices { get; }

        /// <summary>
        /// Executes the operation on the provided typed accessor.
        /// </summary>
        /// <typeparam name="T">The underlying data type of the volume (e.g., int, float, double).</typeparam>
        /// <param name="accessor">The typed accessor instance that invoked this operation.</param>
        /// <returns>
        /// The result of the operation, strongly typed as <typeparamref name="TResult"/>.
        /// </returns>
        TResult Execute<T>(VolumeAccessor<T> accessor) where T : unmanaged;
    }

    /// <summary>
    /// Represents an operation to extract a specific 2D slice from the volume.<br/>
    /// <c>e.g. IMatrixData processed = src.Apply(new SliceOperation(ViewFrom.Y, 0));</c>
    /// </summary>
    /// <param name="Axis">The axis along which to slice.</param>
    /// <param name="Index">The zero-based index of the slice.</param>
    /// <param name="AxisName">Optional:  Name for the axis mapping.</param>
    /// <param name="BaseIndices">Optional indices to define the sub-region or base coordinates.</param>
    /// <param name="Dst">
    /// Optional destination to receive the slice in place; see <see cref="VolumeAccessor{T}.SliceAt"/>.
    /// </param>
    /// <param name="DstIndex">Frame index within <paramref name="Dst"/> to write into.</param>
    public record SliceOperation(ViewFrom Axis, int Index, string AxisName = "", int[]? BaseIndices = null,
        IMatrixData? Dst = null, int DstIndex = 0)
       : IVolumeOperation<IMatrixData>
    {
        /// <summary>
        /// Executes the slice operation on the accessor provided by the 'Apply' method.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor, pre-configured with the operation's spatial context.</param>
        /// <returns>A 2D <see cref="IMatrixData"/> representing the slice.</returns>
        public IMatrixData Execute<T>(VolumeAccessor<T> accessor)
            where T : unmanaged
            => accessor.SliceAt(Axis, Index, Dst, DstIndex);
    }

    /// <summary>
    /// Represents a high-performance operation that extracts both YZ (SliceX) and XZ (SliceY) planes simultaneously 
    /// at the intersection of specified (X, Y) coordinates. 
    /// This is optimized for 14th Gen Intel Core architectures to minimize memory bandwidth saturation 
    /// and virtual frame list access overhead.
    /// </summary>
    /// <remarks>
    /// This operation supports zero-allocation workflows via buffer reuse and provides granular thread control 
    /// (numThreads) to prevent thread explosion when nested within channel-level parallelism.
    /// </remarks>
    /// <param name="X">The X coordinate used to generate the YZ plane.</param>
    /// <param name="Y">The Y coordinate used to generate the XZ plane.</param>
    /// <param name="numThreads">Degree of parallelism. Set to 1 for sequential execution (recommended when
    /// the caller is already parallelized), or -1 to use the system default.</param>
    /// <param name="dstXZ">Optional pre-allocated MatrixData for the XZ plane to avoid GC pressure.</param>
    /// <param name="dstXZIndex">The starting offset index within <paramref name="dstXZ"/>.</param>
    /// <param name="dstYZ">Optional pre-allocated MatrixData for the YZ plane to avoid GC pressure.</param>
    /// <param name="dstYZIndex">The starting offset index within <paramref name="dstYZ"/>.</param>
    /// <param name="AxisName">Optional: Name for the axis mapping metadata.</param>
    /// <param name="BaseIndices">Optional: Indices to define sub-regions or coordinate offsets.</param>
    public record SliceOrthogonalOperation(int X, int Y, string AxisName = "", int[]? BaseIndices = null, 
        int numThreads = -1, IMatrixData? dstXZ = null, int dstXZIndex = 0, IMatrixData? dstYZ = null, int dstYZIndex = 0)
        : IVolumeOperation<(IMatrixData XZ, IMatrixData YZ)>
    {
        /// <summary>
        /// Executes the orthogonal slice operation, returning both planes in a single pass.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor, pre-configured with the operation's spatial context.</param>
        /// <returns>A tuple containing the XZ plane and YZ plane as <see cref="IMatrixData"/>.</returns>
        public (IMatrixData XZ, IMatrixData YZ) Execute<T>(VolumeAccessor<T> accessor) where T : unmanaged
            => accessor.SliceOrthogonal(X, Y, numThreads, dstXZ, dstXZIndex, dstYZ, dstYZIndex);
    }

    /// <summary>
    /// Represents an operation to reorganize the internal memory layout of the volume (Restack).<br/>
    /// <c>e.g. IMatrixData processed = src.Apply(new RestackOperation(ViewFrom.Y));</c>
    /// </summary>
    /// <param name="NewAxis">The target axis to become the primary dimension.</param>
    /// <param name="AxisName">Optional: Name for the axis mapping.</param>
    /// <param name="BaseIndices">Optional indices to define the sub-region.</param>
    /// <param name="OutputMode">In-memory or memory-mapped output; Auto defers to <see cref="VirtualPolicy"/>.</param>
    /// <param name="Progress">Optional progress reporter.</param>
    /// <param name="CancellationToken">Cancels the operation.</param>
    public record RestackOperation(ViewFrom NewAxis, string AxisName = "", int[]? BaseIndices = null,
        LoadingMode OutputMode = LoadingMode.Auto, IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default)
       : IVolumeOperation<IMatrixData>
    {
        /// <summary>
        /// Executes the restack operation, creating a new volume view with reordered axes.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor to be restacked.</param>
        /// <returns>A 3D <see cref="IMatrixData"/> with the modified layout.</returns>
        public IMatrixData Execute<T>(VolumeAccessor<T> accessor)
            where T : unmanaged
            => accessor.Restack(NewAxis, OutputMode, Progress, CancellationToken);
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
       : IVolumeOperation<IMatrixData>
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
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                throw new NotSupportedException(
                    $"Type '{typeof(T).Name}' does not support Projection operations. " +
                    "Projection requires numeric types (INumber<T>)."
                );
            }
        }
    }

    /// <summary>
    /// Represents a "winner-take-all" operation used by ColorCoded depth/time visualisation: for
    /// each (x, y) pixel, finds the axis index in <c>[Start, End]</c> holding the extreme
    /// (<see cref="ProjectionMode.Maximum"/> or <see cref="ProjectionMode.Minimum"/>) value, and
    /// returns both that index and the value found there.<br/>
    /// <c>e.g. var (idx, val) = src.Apply(new ExtremumIndexOperation(ProjectionMode.Maximum, 0, 40));</c>
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="ProjectionOperation"/> rather than an extension of it:
    /// unlike a plain projection, which reduces to a single value, this needs to report *which*
    /// slice won so a caller can look up a depth colour for it. Restricted to Maximum/Minimum --
    /// <see cref="ProjectionMode.Average"/> has no single winning index to report. Deliberately
    /// Core/UI split: this operation only extracts index and value (no colour, no LUT) -- turning
    /// them into a coloured image is the UI/Rendering layer's job.
    /// </remarks>
    /// <param name="Mode">Must be <see cref="ProjectionMode.Maximum"/> or <see cref="ProjectionMode.Minimum"/>.</param>
    /// <param name="Start">First axis index (inclusive) to consider.</param>
    /// <param name="End">Last axis index (inclusive) to consider.</param>
    /// <param name="AxisName">Optional: Name for the axis mapping.</param>
    /// <param name="BaseIndices">Optional indices to define the sub-region.</param>
    public record ExtremumIndexOperation(ProjectionMode Mode, int Start, int End, string AxisName = "", int[]? BaseIndices = null)
       : IVolumeOperation<(IMatrixData WinnerIndex, IMatrixData WinnerValue)>
    {
        /// <summary>
        /// Executes the extremum-index operation using dynamic dispatch for numeric type support.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor to scan.</param>
        /// <returns>
        /// <c>WinnerIndex</c>: a <c>MatrixData&lt;int&gt;</c> of the winning axis index per pixel.
        /// <c>WinnerValue</c>: a <c>MatrixData&lt;T&gt;</c> of the value found at that index, same type as the source.
        /// </returns>
        /// <exception cref="NotSupportedException">Thrown if T is not a numeric type.</exception>
        public (IMatrixData WinnerIndex, IMatrixData WinnerValue) Execute<T>(VolumeAccessor<T> accessor) where T : unmanaged
        {
            if (Mode != ProjectionMode.Maximum && Mode != ProjectionMode.Minimum)
                throw new ArgumentException(
                    $"ExtremumIndexOperation requires Maximum or Minimum, not {Mode} (Average has no single winning index).",
                    nameof(Mode));

            dynamic vol = accessor;

            try
            {
                // The DLR's dynamic binder can convert each tuple element individually (a plain
                // reference-type widening, MatrixData<T> -> IMatrixData) but not the value tuple as
                // a whole -- (MatrixData<int>, MatrixData<T>) has no implicit dynamic conversion to
                // (IMatrixData, IMatrixData) even though every element does. Reading .Item1/.Item2
                // off the dynamic result (not the named WinnerIndex/WinnerValue -- tuple element
                // names are compile-time metadata the dynamic binder does not see) sidesteps that.
                dynamic result = VolumeAccessorExtensions.CreateExtremumIndex(vol, Mode, Start, End);
                IMatrixData winnerIndex = result.Item1;
                IMatrixData winnerValue = result.Item2;
                return (winnerIndex, winnerValue);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                throw new NotSupportedException(
                    $"Type '{typeof(T).Name}' does not support ExtremumIndex operations. " +
                    "ExtremumIndex requires numeric types (INumber<T>)."
                );
            }
        }
    }

    public record OrthogonalProjectionsOperation(ProjectionMode Mode, string AxisName = "", int[]? BaseIndices = null,
        int numThreads = -1, IMatrixData? dstXZ = null, int dstXZIndex = 0, IMatrixData? dstYZ = null, int dstYZIndex = 0)
       : IVolumeOperation<(IMatrixData XZ, IMatrixData YZ)>
    {
        /// <summary>
        /// Executes the orthogonal projection operation using dynamic dispatch for numeric type support.
        /// </summary>
        /// <typeparam name="T">The unmanaged data type.</typeparam>
        /// <param name="accessor">The typed accessor to project.</param>
        /// <returns>A 2D <see cref="IMatrixData"/> representing the projected image.</returns>
        /// <exception cref="NotSupportedException">Thrown if T is not a numeric type.</exception>
        public (IMatrixData XZ, IMatrixData YZ) Execute<T>(VolumeAccessor<T> accessor) where T : unmanaged
        {
            dynamic vol = accessor;

            try
            {
                return VolumeAccessorExtensions.CreateOrthogonalProjections(vol, Mode, numThreads: numThreads, dstXZ: dstXZ, dstXZIndex: dstXZIndex, dstYZ: dstYZ, dstYZIndex: dstYZIndex);
            }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                throw new NotSupportedException(
                    $"Type '{typeof(T).Name}' does not support OrthogonalProjectionsOperations. " +
                    "Projection requires numeric types (INumber<T>)."
                );
            }
        }
    }
}