using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Defines a strategy for determining which data indices should be preloaded and which should be cached with high
    /// priority based on the current display state.
    /// </summary>
    /// <remarks>Implementations of this interface can be used to optimize data loading and caching behavior
    /// in scenarios where only a subset of data needs to be accessed or prioritized. Typical use cases include
    /// applications that display large datasets and require efficient memory management by preloading relevant data and
    /// prioritizing certain indices for caching.</remarks>
    public interface ICacheStrategy
    {
        /// <summary>
        /// Gets the indices of the data that should be preloaded based on the current index and total count.
        /// </summary>
        IEnumerable<int> GetPreloadIndices(int currentIndex, int totalCount);

        /// <summary>
        /// Gets wheter the specified index should be cached with high priority based on the current display state.
        /// e.g. all channels of the current Z-slice might return true.
        /// </summary>
        bool IsHighPriority(int index);

        /// <summary>
        /// Occurs when the internal state or parameters of the strategy change, 
        /// indicating that the cache manager should re-evaluate priorities or preloads.
        /// </summary>
        event EventHandler? StrategyChanged;
    }
}
