using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Provides controlled access to the underlying backing data of MatrixData.
    /// </summary>
    public interface IBackendAccessor
    {
        /// <summary>
        /// Attempts to retrieve the underlying backing data if it matches the requested specific type.
        /// </summary>
        /// <typeparam name="TBackend">The specific type of the backing store requested by the writer.</typeparam>
        /// <param name="backend">The backing store instance if the type matches; otherwise, null.</param>
        /// <returns><see langword="true"/> if the backing store matches the requested type; otherwise, <see langword="false"/>.</returns>
        bool TryGet<TBackend>(out TBackend? backend) where TBackend : class;
    }
}
