using Microsoft.CSharp.RuntimeBinder;
using System;
using System.Threading;
using MxPlot.Core.IO;

namespace MxPlot.Core.Processing
{
    // ========================================================================================================
    // File Overview: Projection Stack Bridge
    // ========================================================================================================
    // Type-erasure bridge only - the actual work lives in ProjectionStackOperator.
    // IMatrixDataOperation.Execute<T> can only promise T : unmanaged, while the projection kernels
    // require INumber<T> and IMinMaxValue<T>. Dynamic binding closes that gap at runtime, the same
    // way ProjectionOperation does in VolumeOperations.cs.
    // ========================================================================================================

    /// <summary>
    /// Collapses one axis by projection for every combination of the remaining axes, yielding a
    /// navigable stack.<br/>
    /// <c>e.g. IMatrixData stack = src.Apply(new CreateProjectedStackOperation(ViewFrom.Z, ProjectionMode.Maximum, "Z"));</c>
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectionOperation"/> is the single-frame sibling: it projects only at the current
    /// position of the other axes. This one bakes every combination, so the result can be navigated,
    /// saved, and exported like ordinary data.
    /// </remarks>
    /// <param name="Axis">The direction to project along within each volume.</param>
    /// <param name="Mode">The projection mode.</param>
    /// <param name="AxisName">The axis to collapse. Empty uses the only axis when the data has exactly one.</param>
    /// <param name="OutputMode">In-memory or memory-mapped output; Auto defers to <see cref="VirtualPolicy"/>.</param>
    /// <param name="Progress">Optional progress reporter.</param>
    /// <param name="CancellationToken">Cancels the operation.</param>
    public record CreateProjectedStackOperation(
        ViewFrom Axis,
        ProjectionMode Mode,
        string AxisName = "",
        LoadingMode OutputMode = LoadingMode.Auto,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        /// <inheritdoc/>
        public IMatrixData Execute<T>(MatrixData<T> data) where T : unmanaged
        {
            dynamic typed = data;

            try
            {
                return ProjectionStackOperator.CreateProjectedStack(
                    typed, Axis, Mode, AxisName, OutputMode, Progress, CancellationToken);
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
