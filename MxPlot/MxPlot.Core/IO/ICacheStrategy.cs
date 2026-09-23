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
        /// Gets whether <paramref name="index"/> is one of the frames actually on screen right now
        /// and must never be evicted, regardless of priority -- e.g. the single active frame, or
        /// every currently-blended channel at the current position in Composite mode. Unlike
        /// <see cref="IsHighPriority"/>, which can legitimately mark an entire axis sweep as
        /// protected (Volume mode) and therefore cannot itself guarantee any single frame survives
        /// an oversubscribed cache, a pinned frame is exempt from eviction outright. Defaults to
        /// <see langword="false"/> for strategies that have no notion of "currently displayed".
        /// </summary>
        bool IsPinned(int index) => false;

        /// <summary>
        /// Occurs when the internal state or parameters of the strategy change, 
        /// indicating that the cache manager should re-evaluate priorities or preloads.
        /// </summary>
        event EventHandler? StrategyChanged;
    }
}
