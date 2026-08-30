using Avalonia.Threading;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Live derived views ────────────────────────────────────────────────
        //
        // One source window, N follower windows, each showing a transform of whatever the source
        // currently displays. Spatial Filter sync, Log Transform sync and the orthogonal live
        // extracts are all the same shape: subscribe to the source, recompute on every change with
        // the previous computation cancelled, push the result into the follower, and tear
        // everything down when either window closes. LinkedView owns that shape; callers supply
        // only the transform.
        //
        // Not to be confused with MatrixPlotterSyncGroup, which broadcasts display settings between
        // peers with no source/follower hierarchy, or with CropAction's leader/follower ROI sync.

        /// <summary>How a recomputed result is pushed into the follower window.</summary>
        internal enum LinkedViewCommit
        {
            /// <summary>
            /// Swaps in the new data via <see cref="UpdateProjectionData"/>.
            /// </summary>
            /// <remarks>
            /// Deliberately not a full <see cref="SetMatrixData"/> re-initialization. The window was
            /// already set up when it was created, and its shape does not change from one update to
            /// the next, so re-initializing buys nothing and costs plenty: it rebuilds the chrome,
            /// resets overlay state the user may be measuring with, and tears Composite mode down
            /// so it has to be entered again - rebuilding those panels and dropping any flyout the
            /// user has open, several times a second under a live feed.
            /// </remarks>
            UpdateView,

            /// <summary>
            /// The recompute wrote into the follower's existing frame buffers, so there is nothing
            /// to swap - just redraw. Keeps one <see cref="IMatrixData"/> instance on screen for the
            /// window's whole life, which allocates nothing per update and leaves subscriptions to
            /// that instance valid. The recompute owns invalidating the min/max cache it dirtied.
            /// </summary>
            RefreshInPlace,
        }

        /// <summary>The outcome of one recompute.</summary>
        /// <param name="Data">The data to display in the follower window.</param>
        /// <param name="CompositeChannelAxisName">
        /// Non-<c>null</c> when <paramref name="Data"/> is a Composite channel cube, naming the
        /// axis to re-enter Composite mode on after the commit. <c>null</c> for ordinary data.
        /// </param>
        internal readonly record struct LinkedViewUpdate(
            IMatrixData Data,
            string? CompositeChannelAxisName = null);

        /// <summary>
        /// Recomputes the follower's content from the current state of <paramref name="source"/>.
        /// Return <c>null</c> to skip this tick.
        /// </summary>
        /// <remarks>
        /// The source is passed as a whole plotter rather than as an <see cref="IMatrixData"/>
        /// because the transform usually needs more than the data: Composite-aware callers ask it
        /// for the current channel cube, and the orthogonal extracts read the slice position off it.
        /// </remarks>
        internal delegate Task<LinkedViewUpdate?> LinkedViewRecompute(
            MatrixPlotter source, CancellationToken cancellationToken);

        /// <summary>
        /// Keeps a follower window recomputed from a source window, and tears the link down when
        /// either closes.
        /// </summary>
        internal sealed class LinkedView : IDisposable
        {
            private readonly MatrixPlotter _follower;
            private readonly MatrixPlotter _source;
            private readonly LinkedViewRecompute _recompute;
            private readonly LinkedViewCommit _commit;

            private readonly EventHandler _refreshedHandler;
            private readonly EventHandler? _activeIndexHandler;
            private readonly EventHandler<IMatrixData?>? _dataReplacedHandler;

            // The instance subscribed to, not source.MatrixData as it stands at teardown time: if
            // the source swapped its data in the meantime, unsubscribing from the current instance
            // would leave this handler attached to the discarded one.
            private readonly IMatrixData? _subscribedData;

            private CancellationTokenSource? _cts;
            private bool _inFlight;
            private bool _disposed;

            /// <param name="follower">The window being kept up to date.</param>
            /// <param name="source">The window driving it.</param>
            /// <param name="recompute">Produces the follower's new content.</param>
            /// <param name="commit">How the result is applied; see <see cref="LinkedViewCommit"/>.</param>
            /// <param name="trackActiveIndex">
            /// Whether frame-slider navigation on the source should trigger a recompute. Pass
            /// <c>false</c> for transforms pinned to a captured position, which only care about
            /// content changes.
            /// </param>
            /// <param name="closeOnSourceDataReplaced">
            /// Whether the follower should close when the source swaps its <see cref="IMatrixData"/>
            /// instance. Pass <c>true</c> when the transform closed over a specific instance: it
            /// would otherwise keep recomputing from data the source has already let go of.
            /// Transforms that re-read <see cref="MatrixData"/> on every tick do not need this -
            /// they simply follow the new instance.
            /// </param>
            internal LinkedView(
                MatrixPlotter follower,
                MatrixPlotter source,
                LinkedViewRecompute recompute,
                LinkedViewCommit commit = LinkedViewCommit.UpdateView,
                bool trackActiveIndex = true,
                bool closeOnSourceDataReplaced = false)
            {
                _follower = follower ?? throw new ArgumentNullException(nameof(follower));
                _source = source ?? throw new ArgumentNullException(nameof(source));
                _recompute = recompute ?? throw new ArgumentNullException(nameof(recompute));
                _commit = commit;

                source._syncFollowers.Add(follower);
                follower._linkedView = this;

                _refreshedHandler = (_, _) => Fire();
                source.Refreshed += _refreshedHandler;

                if (trackActiveIndex && source.MatrixData is { } md)
                {
                    _subscribedData = md;
                    _activeIndexHandler = (_, _) => Fire();
                    md.ActiveIndexChanged += _activeIndexHandler;
                }

                if (closeOnSourceDataReplaced)
                {
                    _dataReplacedHandler = (_, _) => _follower.Close();
                    source.MatrixDataChanged += _dataReplacedHandler;
                }

                source.Closed += OnSourceClosed;
                follower.Closed += OnFollowerClosed;
            }

            /// <summary>
            /// Starts a recompute, cancelling any still in flight so only the latest result lands.
            /// </summary>
            /// <remarks>
            /// Fire-and-forget on purpose. <see cref="Refreshed"/> is raised inside the source's
            /// <c>_isRefreshing</c> guard, so a handler that recomputed synchronously and pushed
            /// back into the plotter would be swallowed by it.
            /// <para>
            /// <see cref="IMatrixData.ActiveIndexChanged"/> is raised straight from the property
            /// setter with no marshalling, so a background write would otherwise reach the UI here.
            /// </para>
            /// </remarks>
            private void Fire()
            {
                if (_disposed) return;

                if (!Dispatcher.UIThread.CheckAccess())
                {
                    Dispatcher.UIThread.Post(Fire);
                    return;
                }

                if (_commit == LinkedViewCommit.RefreshInPlace)
                {
                    // In-place writes share one destination, so cancelling and starting over would
                    // let two recomputes interleave rows into the buffer being displayed. Coalesce
                    // instead: drop this tick, and the next one after the current write finishes
                    // picks up whatever the source looks like by then.
                    if (_inFlight) return;
                    _inFlight = true;
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();
                    _ = FireAsync(_cts.Token);
                    return;
                }

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                _ = FireAsync(_cts.Token);
            }

            private async Task FireAsync(CancellationToken ct)
            {
                LinkedViewUpdate? update;
                try
                {
                    update = await _recompute(_source, ct);
                }
                catch (OperationCanceledException) { return; }
                catch { return; }
                finally { _inFlight = false; }

                if (_disposed || ct.IsCancellationRequested || update == null) return;

                _follower.CommitLinkedUpdate(update.Value, _commit);
            }

            private void OnSourceClosed(object? sender, EventArgs e) => _follower.Close();

            private void OnFollowerClosed(object? sender, EventArgs e) => Dispose();

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;

                _source.Refreshed -= _refreshedHandler;
                if (_activeIndexHandler != null && _subscribedData != null)
                    _subscribedData.ActiveIndexChanged -= _activeIndexHandler;
                if (_dataReplacedHandler != null)
                    _source.MatrixDataChanged -= _dataReplacedHandler;

                _source.Closed -= OnSourceClosed;
                _follower.Closed -= OnFollowerClosed;
                _source._syncFollowers.Remove(_follower);

                if (ReferenceEquals(_follower._linkedView, this))
                    _follower._linkedView = null;
            }
        }

        // ── Follower / source bookkeeping ─────────────────────────────────────

        /// <summary>The link keeping this window up to date, when it is a follower.</summary>
        private LinkedView? _linkedView;

        /// <summary>
        /// Windows currently recomputed from this one. Tracked so that replacing this window's own
        /// data in place can close them: they would otherwise keep tracking a
        /// <see cref="IMatrixData"/> instance this window has already discarded.
        /// </summary>
        private readonly List<MatrixPlotter> _syncFollowers = [];

        /// <summary>
        /// Whether this window's content is being driven by a <see cref="LinkedView"/>.
        /// Processing dialogs consult this to disable their own "Replace data" option: overwriting
        /// a window that is itself live-driven would just be undone on the next update.
        /// </summary>
        internal bool IsSyncFollower => _linkedView != null;

        /// <summary>
        /// Closes every window recomputed from this one, cascading down any chain. Call wherever
        /// this window's own <see cref="_currentData"/> is replaced in place.
        /// </summary>
        internal void CloseSyncFollowers()
        {
            // Snapshot: closing a follower disposes its LinkedView, which mutates this list.
            foreach (var follower in _syncFollowers.ToList())
                follower.Close();
        }

        /// <summary>
        /// Applies a <see cref="LinkedView"/> result, restoring Composite mode afterwards when the
        /// payload is a channel cube - both commit paths reset it in their own way.
        /// </summary>
        private void CommitLinkedUpdate(in LinkedViewUpdate update, LinkedViewCommit commit)
        {
            if (commit == LinkedViewCommit.RefreshInPlace)
            {
                // Nothing was swapped, so the ordinary redraw path is the whole commit: it renders
                // the rewritten buffers, updates the derived panels and raises Refreshed for
                // anything derived from this window in turn.
                Refresh();
                return;
            }

            UpdateProjectionData(update.Data);
            if (update.CompositeChannelAxisName != null)
            {
                // In Composite the per-frame index refresh is all that is needed - the mode itself
                // is still standing, precisely because nothing re-initialized.
                RefreshCompositeAfterDataSwap();
            }
            else if (_isCompositeMode)
            {
                // The source stopped supplying a Channel-axis cube (typically because its own
                // Composite mode was just exited - see the equivalent transition in
                // ApplyCompositeStateToProjectionWindow), but this follower is still rendering as
                // Composite against data that no longer has the shape Composite assumes:
                // _view.CompositeFrameIndices stays sized for the old channel count while the new
                // data's frame count shrank. Left alone, that mismatch surfaces later as an
                // unrelated-looking ArgumentOutOfRangeException the moment anything reads a
                // per-channel pixel against it - e.g. the mouse-hover value overlay. Falling back
                // to LUT here keeps the follower's state consistent with what UpdateProjectionData
                // just swapped in.
                ExitCompositeMode();
            }
            else
            {
                SyncCurrentDataFromView();
            }
        }
    }
}
