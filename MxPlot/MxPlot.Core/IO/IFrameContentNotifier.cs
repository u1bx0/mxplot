using System;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Implemented by frame-list classes that can asynchronously update a frame's
    /// <c>T[]</c> content after the initial (possibly empty) array has been returned —
    /// i.e., progressive / background loading.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Motivation (progressive loading contract):</strong><br/>
    /// When a consumer calls <c>IList&lt;T[]&gt;[i]</c> on a remote-backed frame list,
    /// the list may return a pre-allocated zero-filled <c>T[]</c> immediately and then fill
    /// it in the background (HTTP fetch → decompress → copy).
    /// Once filling is complete, the implementation raises <see cref="FrameUpdated"/> so
    /// that subscribers (e.g., <c>RenderSurface</c> via <c>IRemoteMatrixData.FrameContentChanged</c>)
    /// can trigger a re-render without polling.
    /// </para>
    /// <para>
    /// <strong>Wiring pattern in <c>MatrixData&lt;T&gt;</c>:</strong><br/>
    /// When <c>_arrayList</c> is set to an implementation of this interface, the
    /// <c>MatrixData&lt;T&gt;</c> constructor (or a future <c>RemoteMatrixData&lt;T&gt;</c>
    /// subclass) subscribes:
    /// <code>
    /// if (_arrayList is IFrameContentNotifier notifier)
    ///     notifier.FrameUpdated += (_, i) => RaiseFrameContentChanged(i);
    /// </code>
    /// </para>
    /// <para>
    /// <strong>This interface is not yet wired.</strong>
    /// It is reserved as an extension point for <c>RemoteFrames&lt;T&gt;</c>
    /// (MxPlot.Extensions.Zarr / MxPlot.Core.Remote).
    /// See <c>Tests.Documents/Working/MxPlot.Remote/RemoteFrames_Design.md</c> §11 and §12.
    /// </para>
    /// </remarks>
    public interface IFrameContentNotifier
    {
        /// <summary>
        /// Raised when the <c>T[]</c> at the specified global frame index has been
        /// partially or fully updated by a background operation.
        /// The event argument is the zero-based global frame index.
        /// </summary>
        event EventHandler<int>? FrameUpdated;
    }
}
