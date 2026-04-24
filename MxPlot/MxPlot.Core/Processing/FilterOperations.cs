using System;
using System.Threading;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Applies a spatial filter (median, gaussian, etc.) to all frames.
    /// The filter behavior is determined by the injected <see cref="IFilterKernel"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// // Median 3×3
    /// var result = data.Apply(new SpatialFilterOperation(new MedianKernel()));
    ///
    /// // Gaussian 5×5 with sigma=1.5
    /// var result2 = data.Apply(new SpatialFilterOperation(new GaussianKernel(radius: 2, sigma: 1.5)));
    /// </code>
    /// </example>
    public record SpatialFilterOperation(
        IFilterKernel Kernel,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.ApplyFilter(Kernel, progress: Progress, cancellationToken: CancellationToken);
    }
}
