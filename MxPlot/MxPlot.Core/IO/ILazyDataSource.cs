using System;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Common contract for a frame list backed by an external, on-demand data source -- access may
    /// trigger a decode or fetch rather than a plain array read. Implemented by
    /// <see cref="VirtualFrames{T}"/> and every backend built on it (<see cref="MmfFrames{T}"/>,
    /// a future compressed-format decoder, a future remote/chunk-based backend).
    /// </summary>
    /// <remarks>
    /// Bundles the cross-cutting concerns every such source needs regardless of backend: origin
    /// (<see cref="SourcePath"/>), load progress (<see cref="StoredFrameCount"/>/
    /// <see cref="FrameStored"/>/<see cref="FrameEvicted"/>/<see cref="IsVirtual"/>/<see cref="IsVirtualChanged"/>), and external-resource lifecycle
    /// (<see cref="IsOwned"/>/<see cref="IsDisposed"/>). Deliberately excludes whether an
    /// implementor evicts already-loaded frames or converges to holding everything -- that is
    /// <see cref="ICacheableFrameList"/>, a separate, optional capability.
    /// </remarks>
    public interface ILazyDataSource
    {
        /// <summary>
        /// Where this data actually comes from: a local file path, the compressed file being
        /// streamed, a URL/S3 key, etc. Independent of how the backend reads it.
        /// </summary>
        string SourcePath { get; }

        /// <summary>
        /// How many of the total (<c>Count</c>) frames are currently resident in memory, right
        /// now. A live occupancy count, not a monotonic "progress toward done" value -- it can
        /// decrease (eviction) as freely as it can increase, depending on the implementor.
        /// </summary>
        int StoredFrameCount { get; }

        /// <summary>
        /// Raised when a frame has been decoded/fetched into memory. The argument is the frame
        /// index in this source (not a view's remapped index).
        /// </summary>
        /// <remarks>May be raised on a background (prefetch) thread.</remarks>
        event EventHandler<int>? FrameStored;

        /// <summary>
        /// Raised when a frame has left memory (evicted from the cache). The argument is the frame
        /// index in this source.
        /// </summary>
        /// <remarks>May be raised on a background (prefetch) thread.</remarks>
        event EventHandler<int>? FrameEvicted;

        /// <summary>
        /// Gets/sets whether this instance is owned by an external component (typically a
        /// <c>MatrixData&lt;T&gt;</c>) responsible for calling <see cref="IDisposable.Dispose"/>.
        /// First-claim-wins: once set to <see langword="true"/>, later attempts to set it again
        /// should throw, so that only one of several instances that end up reference-sharing this
        /// backend takes disposal responsibility for it.
        /// </summary>
        bool IsOwned { get; set; }

        /// <summary>
        /// Whether <see cref="IDisposable.Dispose"/> has already been called on this instance.
        /// Not MMF-specific -- any backend holding external resources (a file handle, an HTTP
        /// connection) needs this, e.g. so a diagnostic view can stop polling a disposed source
        /// without needing to know which concrete backend it is.
        /// </summary>
        bool IsDisposed { get; }

        /// <summary>
        /// Whether this instance currently does not hold every frame resident in memory. Backs
        /// <see cref="IMatrixData.IsVirtual"/>. See <see cref="VirtualFrames{T}.IsVirtual"/>'s own
        /// remarks for the default (dynamic, converges to <see langword="false"/> once fully loaded)
        /// versus <see cref="MmfFrames{T}"/>'s override (unconditionally <see langword="true"/>).
        /// </summary>
        bool IsVirtual { get; }

        /// <summary>
        /// Raised when <see cref="IsVirtual"/> changes value -- e.g. a backend whose background
        /// fill just brought every frame into memory (true to false), or one that evicted below
        /// full occupancy again after a capacity trim (false to true). Never raised by a backend
        /// whose <see cref="IsVirtual"/> is constant, such as <see cref="MmfFrames{T}"/>.
        /// </summary>
        /// <remarks>
        /// May be raised on a background (prefetch) thread; UI subscribers must marshal to their
        /// own thread. Carries no new value on purpose: raises from concurrent inserts and
        /// evictions can reach a subscriber out of order, so re-read <see cref="IsVirtual"/> in the
        /// handler instead of assuming which way it flipped.
        /// </remarks>
        event EventHandler? IsVirtualChanged;
    }
}
