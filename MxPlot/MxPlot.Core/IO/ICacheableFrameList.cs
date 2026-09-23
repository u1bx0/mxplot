namespace MxPlot.Core.IO
{
    /// <summary>
    /// Bounded, LRU-style eviction control for a frame list, usable by any on-demand backend
    /// (MMF, compressed-decode, remote), not just MMF.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="ILazyDataSource"/>: eviction is not implied by being lazy.
    /// <see cref="VirtualFrames{T}"/> implements both, so in practice every current implementor of
    /// <see cref="ILazyDataSource"/> also implements this, but a hypothetical non-evicting backend
    /// would only need <see cref="ILazyDataSource"/>. Whether a given instance ever fully converges
    /// to holding everything (vs. evicting forever) is not part of this contract either -- that
    /// depends only on how <see cref="CacheCapacity"/>/<see cref="CacheStrategy"/> happen to be
    /// configured, not on which class implements it.
    /// </remarks>
    public interface ICacheableFrameList
    {
        /// <summary>The pluggable prefetch/eviction-priority strategy currently in effect.</summary>
        ICacheStrategy CacheStrategy { get; set; }

        /// <summary>Maximum number of decoded frames held in the cache at once.</summary>
        int CacheCapacity { get; set; }

        /// <summary>Captures a diagnostic snapshot of the current cache state.</summary>
        CacheSnapshot GetCacheStatus();

        /// <summary>
        /// Sets <see cref="CacheCapacity"/> to <paramref name="newCapacity"/> and, unlike assigning
        /// the property directly, proactively evicts down to it immediately instead of waiting for
        /// future cache-miss traffic to do it lazily. Use this to actually release memory after a
        /// temporary elevated-budget mode (e.g. orthogonal/Volume-mode viewing) ends.
        /// </summary>
        void TrimCacheTo(int newCapacity);
    }
}
