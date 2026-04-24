using System;
using System.Collections.Generic;

namespace MxPlot.Core
{
    /// <summary>
    /// Describes the network / background-fetch state of a remote-backed
    /// <see cref="IRemoteMatrixData"/> instance.
    /// </summary>
    public enum RemoteLoadingState
    {
        /// <summary>No fetch is in progress; all requested frames are cached.</summary>
        Idle,

        /// <summary>One or more frames are currently being fetched from the remote store.</summary>
        Fetching,

        /// <summary>The last requested frame was fetched and decoded successfully.</summary>
        Ready,

        /// <summary>The last fetch attempt failed; see the error details on the implementing class.</summary>
        Error,
    }

    /// <summary>
    /// Extends <see cref="IMatrixData"/> with capabilities required by
    /// remote / cloud-backed data sources such as OME-Zarr over HTTP or S3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Design rationale — why not on <see cref="IMatrixData"/>:</strong><br/>
    /// <see cref="IMatrixData"/> is intentionally kept minimal. Remote-specific concerns
    /// (progressive loading, multi-scale pyramids, network state) are orthogonal to the
    /// core data-access contract and would force every <c>MatrixData&lt;T&gt;</c> implementation
    /// to carry empty stub members. Instead, consumers cast with <c>is IRemoteMatrixData</c>:
    /// <code>
    /// if (data is IRemoteMatrixData remote)
    /// {
    ///     remote.FrameContentChanged += OnFrameUpdated;
    ///     if (remote.Levels is { Count: > 1 } levels) ShowLevelSelector(levels);
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// <strong>Planned concrete implementation:</strong><br/>
    /// <c>RemoteMatrixData&lt;T&gt; : MatrixData&lt;T&gt;, IRemoteMatrixData</c>
    /// (MxPlot.Extensions.Zarr or MxPlot.Core.Remote).
    /// It inherits all existing <c>MatrixData&lt;T&gt;</c> logic unchanged and uses
    /// <c>RemoteFrames&lt;T&gt;</c> as its <c>_arrayList</c>.
    /// </para>
    /// <para>
    /// <strong>This interface is not yet implemented.</strong>
    /// It is a forward-looking reservation. See the design memo at
    /// <c>Tests.Documents/Working/MxPlot.Remote/RemoteFrames_Design.md</c>
    /// §11 (progressive loading) and §12 (interface design policy) for details.
    /// </para>
    /// </remarks>
    public interface IRemoteMatrixData : IMatrixData
    {
        /// <summary>
        /// Raised when the <c>T[]</c> at the specified global frame index has been
        /// updated by a background fetch (progressive / async loading).
        /// The event argument is the zero-based global frame index.
        /// Consumers should re-render the frame if it is currently displayed.
        /// </summary>
        event EventHandler<int>? FrameContentChanged;

        /// <summary>
        /// Multi-scale resolution pyramid, if available (e.g., OME-Zarr <c>multiscales</c>).
        /// <c>Levels[0]</c> is full resolution. Returns <see langword="null"/> for
        /// single-scale sources.
        /// </summary>
        IReadOnlyList<IMatrixData>? Levels { get; }

        /// <summary>
        /// Returns <see langword="true"/> if the <c>T[]</c> for the specified frame
        /// has been fully fetched and decoded from the remote store.
        /// </summary>
        /// <param name="frameIndex">Zero-based global frame index.</param>
        bool IsFrameLoaded(int frameIndex);

        /// <summary>
        /// Gets the current network / background-fetch state of this instance.
        /// </summary>
        RemoteLoadingState LoadingState { get; }
    }
}
