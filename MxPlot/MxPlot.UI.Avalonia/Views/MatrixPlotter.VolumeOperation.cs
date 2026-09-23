// Diagnostic: stage timings for the post-Restack UI path (result hand-off, history, new-window
// creation) -- see VolumeAccessor.cs's matching RESTACK_DIAG for the Core-side timings and what it
// traced. Off by default; uncomment alongside VolumeAccessor.cs's RESTACK_DIAG to re-enable.
//#define RESTACK_DIAG
using Avalonia.Controls;
using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.Core.IO.CacheStrategies;
using MxPlot.Core.IO.Formats;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Orthogonal side panel ──────────────────────────────────────────────

        //private bool _suppressOrthoResize;

        /// <summary>
        /// Wires the <see cref="AxisTracker.FreezeButton"/> toggle to activate/deactivate
        /// orthogonal side views and adjust the window size accordingly.
        /// </summary>
        private void WireFreezeButton(AxisTracker tracker, Axis axis)
        {
            tracker.FreezeButton.IsCheckedChanged += (_, _) =>
            {
                if (tracker.FreezeButton.IsChecked == true)
                {
                    // When switching axes, suppress the resize fired by deactivating the old button
                    bool wasShowing = _orthoPanel.ShowRight;
                    using var _ = _reentrancy.Begin(GuardContext.Operating);
                    foreach (var child in _trackerPanel.Children)
                        if (child is AxisTracker t && t != tracker)
                            t.FreezeButton.IsChecked = false;

                    if (_currentData != null)
                    {
                        Debug.WriteLine($"[OrthoScale] Activate: axis={axis.Name}");
                        ApplyCacheStrategy(axis, ResolveCompositeAxisForCacheStrategy());
                        _orthoController.Activate(_currentData, axis.Name);
                        // Guard: clamp any active action's ROIs to the new axis dimensions.
                        _activeTool?.NotifyContextChanged(CreateToolContext());
                        if (!wasShowing && WindowState != WindowState.Maximized)
                        {
                            Width += _orthoPanel.SavedSideDeltaWidth;
                            Height += _orthoPanel.SavedSideDeltaHeight;
                        }
                    }
                }
                else
                {
                    // Capture sizes BEFORE deactivating (panels are hidden after Deactivate)
                    var (deltaW, deltaH) = _orthoPanel.GetCurrentSideSizes();
                    Debug.WriteLine($"[OrthoScale] Deactivate: axis={_orthoController.ActiveAxisName ?? "(none)"}");
                    _orthoController.Deactivate();
                    // Notify any active action that orthogonal views are no longer available.
                    _activeTool?.NotifyContextChanged(CreateToolContext());

                    // Reset to neighbour strategy only when no axis is frozen
                    bool anyFrozen = false;
                    foreach (var child in _trackerPanel.Children)
                    {
                        if (child is AxisTracker t && t.FreezeButton.IsChecked == true)
                        {
                            anyFrozen = true; break;
                        }
                    }

                    if (!anyFrozen && _currentData != null)
                        ApplyCacheStrategy(null, null);

                    if (!_reentrancy.IsActive(GuardContext.Operating) && deltaW > 0 && WindowState != WindowState.Maximized)
                    {
                        Width -= deltaW;
                        Height -= deltaH;
                    }
                }
            };
        }

        /// <summary>
        /// Sets <see cref="MxView.OverlayInfoText"/> to the current axis position (and global frame
        /// when there are multiple axes) while the pointer is over an <see cref="AxisTracker"/> slider,
        /// or a drag on one is in progress. Called on <see cref="AxisTracker.SliderDragStarted"/>,
        /// <see cref="AxisTracker.SliderPointerEntered"/>, and on every <see cref="AxisTracker.IndexChanged"/>
        /// while <c>_isDraggingAxisTracker</c> is <c>true</c>.
        /// </summary>
        private void UpdateAxisDragOverlay(AxisTracker tracker)
        {
            _isDraggingAxisTracker = true;
            string text;
            int axisCount = _currentData?.Axes.Count ?? 0;
            if (axisCount > 1)
            {
                int globalFrame = _currentData?.ActiveIndex ?? 0;
                int totalFrames = _currentData?.FrameCount ?? 1;
                // "[count]" bracket convention matches AxisTracker.BuildPositionText (0-based, no
                // "index/count" fraction-looking format -- see its doc comment). This global
                // linearised frame position keeps its own "i=" label, though, separated by "|" from
                // the per-axis text before it -- BuildPositionText deliberately carries no "i="
                // itself precisely so the two don't both say "i=" and read as the same number
                // repeated instead of two different ones.
                text = $"{tracker.BuildPositionText()}  | i={globalFrame} [{totalFrames}]";
            }
            else
            {
                text = tracker.BuildPositionText();
            }
            _view.OverlayInfoTextAnchor = OverlayInfoTextAnchor.TopRight;
            _view.OverlayInfoText = text;
        }

        /// <summary>
        /// Clears the axis-drag overlay set by <see cref="UpdateAxisDragOverlay"/>. Called once neither
        /// a hover nor a drag remains on the slider that showed it (see the callers in
        /// <see cref="CreateAndWireAxisTracker"/>).
        /// </summary>
        private void HideAxisDragOverlay()
        {
            _isDraggingAxisTracker = false;
            _view.OverlayInfoText = null;
            _view.OverlayInfoTextAnchor = OverlayInfoTextAnchor.BottomLeft;
        }

        /// <summary>
        /// When <paramref name="targetAxis"/> is set and the current data is virtual, installs a
        /// <see cref="DimensionStrategy"/> in Volume mode so that the on-demand cache pre-fetches
        /// the full Z-stack for the selected axis. When <paramref name="targetAxis"/> is
        /// <c>null</c> (or the data is in-memory), reverts to <see cref="NeighborStrategy"/> for
        /// normal frame-by-frame pre-fetching.
        /// </summary>
        /// <param name="targetAxis">The orthogonal axis being activated, or <c>null</c> to deactivate.</param>
        /// <param name="compositeAxis">
        /// The axis to preload/protect alongside <paramref name="targetAxis"/> (all its values, not
        /// just the current one) -- pass the already-resolved axis from
        /// <see cref="ResolveCompositeAxisForCacheStrategy"/>. Ignored when <paramref name="targetAxis"/>
        /// is <c>null</c>.
        /// </param>
        private void ApplyCacheStrategy(Axis? targetAxis, Axis? compositeAxis)
        {
            if (_currentData is not { IsVirtual: true } data) return;

            if (targetAxis != null)
            {
                // Grow (never shrink here) the cache to the Volume-mode working set -- every frame
                // along targetAxis, times every value of compositeAxis (if any) -- so that once
                // orthogonal slicing has paid for one full read pass, further crosshair moves are
                // served from RAM instead of re-thrashing the same frames back out of a too-small
                // cache. Uses the higher, temporary VolumeModeMemoryBudgetFraction rather than the
                // baseline fraction, since this budget is expected to be released again via
                // TrimCacheTo below once orthogonal viewing ends. Remember the pre-Volume-mode
                // capacity exactly once (not on every axis switch while already in Volume mode) so
                // deactivation can restore it.
                // Not MMF-specific: any ICacheableFrameList-backed data (MMF, TIFF Lazy decode, a
                // future remote backend) benefits equally from a grown Volume-mode working set.
                if (data.GetDiagnosticCacheableList() is { } cacheable)
                {
                    if (_preVolumeModeCacheCapacity < 0)
                    {
                        _preVolumeModeCacheCapacity = cacheable.CacheCapacity;
                        // Captured together, in the same "first time entering Volume mode this
                        // session" guard, so the two always travel as a pair -- restored below
                        // alongside the capacity, instead of the fixed plain NeighborStrategy()
                        // that used to unconditionally replace whatever was active (e.g. discarding
                        // TiffDecodedFrames<T>'s own widened, whole-file NeighborStrategy).
                        _preVolumeModeCacheStrategy = data.CacheStrategy;
                    }

                    long frameSizeBytes = (long)data.XCount * data.YCount * data.ElementSize;
                    int idealVolumeFrames = targetAxis.Count * Math.Max(1, compositeAxis?.Count ?? 1);
                    int grown = VirtualCachePolicy.ComputeCapacity(frameSizeBytes, idealVolumeFrames, VirtualCachePolicy.VolumeModeMemoryBudgetFraction);
                    if (grown > cacheable.CacheCapacity)
                        cacheable.CacheCapacity = grown;
                }

                if (data.CacheStrategy is DimensionStrategy ds)
                {
                    ds.TargetAxis = targetAxis;
                    ds.CompositeAxis = compositeAxis;
                    ds.Mode = DimensionStrategy.CacheMode.Volume;
                    ds.SetTargetChannels([]);
                }
                else
                {
                    var st = new DimensionStrategy(data.Dimensions, targetAxis, compositeAxis)
                    {
                        Mode = DimensionStrategy.CacheMode.Volume
                    };
                    data.CacheStrategy = st;
                }
            }
            else
            {
                // Switch away from DimensionStrategy's Volume mode FIRST: while it's still active,
                // every frame along the (former) target axis is marked high-priority uniformly, which
                // would block most of TrimCacheTo's eviction. Only after a non-Volume strategy is in
                // place does trimming actually free the memory instead of mostly no-op'ing. Restore
                // whatever was active before Volume mode (e.g. TiffDecodedFrames<T>'s own widened
                // NeighborStrategy) rather than always resetting to a fresh plain one -- captured
                // strategies are never a DimensionStrategy themselves (only this method ever installs
                // one, and only after the capture point on entry), so this can't reintroduce the
                // high-priority-blocks-eviction problem above.
                data.CacheStrategy = _preVolumeModeCacheStrategy ?? new NeighborStrategy();
                _preVolumeModeCacheStrategy = null;

                if (_preVolumeModeCacheCapacity >= 0)
                {
                    if (data.GetDiagnosticCacheableList() is { } cacheable)
                        cacheable.TrimCacheTo(_preVolumeModeCacheCapacity);
                    _preVolumeModeCacheCapacity = -1;
                }
            }
        }

        /// <summary>
        /// The virtual dataset's <c>CacheCapacity</c> as it was just before the first time
        /// <see cref="ApplyCacheStrategy"/> grew it for Volume mode, so it can be restored (and the
        /// extra memory released via <see cref="ICacheableFrameList.TrimCacheTo"/>) once orthogonal
        /// viewing ends. -1 means "not currently elevated / nothing to restore".
        /// </summary>
        private int _preVolumeModeCacheCapacity = -1;

        /// <summary>
        /// The virtual dataset's <see cref="ICacheableFrameList.CacheStrategy"/> as it was just
        /// before the first time <see cref="ApplyCacheStrategy"/> switched it to Volume mode's
        /// <see cref="DimensionStrategy"/>, so it can be restored verbatim once orthogonal viewing
        /// ends -- rather than always resetting to a fresh plain <see cref="NeighborStrategy"/>,
        /// which would discard a backend-specific customization (e.g.
        /// <c>TiffDecodedFrames{T}</c>'s own widened, whole-file-background-fill strategy). Captured
        /// and restored together with <see cref="_preVolumeModeCacheCapacity"/>. Null means "not
        /// currently elevated / nothing to restore".
        /// </summary>
        private ICacheStrategy? _preVolumeModeCacheStrategy;

        /// <summary>
        /// Resolves which axis should be preloaded/protected alongside the orthogonal target axis:
        /// the live Composite-mode axis (<see cref="_compositeAxisDimIndex"/>) when Composite mode is
        /// actually active, otherwise falls back to an axis literally named "Channel" if one exists --
        /// resolved here (not left to <see cref="DimensionStrategy"/>'s own constructor-only default)
        /// so that both the "construct a new strategy" and "reuse the existing one" branches of
        /// <see cref="ApplyCacheStrategy"/> agree, since only the constructor path would otherwise see
        /// that fallback.
        /// </summary>
        private Axis? ResolveCompositeAxisForCacheStrategy()
        {
            if (_currentData == null) return null;

            if (_isCompositeMode && _compositeAxisDimIndex >= 0 && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount)
                return _currentData.Dimensions[_compositeAxisDimIndex];

            return _currentData.Dimensions.Contains("Channel") ? _currentData.Dimensions["Channel"] : null;
        }

        // ── Public orthogonal-view control ────────────────────────────────────────

        /// <summary>
        /// Returns the <see cref="AxisTracker"/> that controls the specified axis,
        /// or <c>null</c> if no tracker with that name exists.
        /// </summary>
        /// <param name="axisName">The <see cref="Axis.Name"/> of the target axis.</param>
        public AxisTracker? GetAxisTracker(string axisName)
            => _axisTrackers.TryGetValue(axisName, out var t) ? t : null;

        /// <summary>
        /// Activates the orthogonal side views for the specified axis, or deactivates them
        /// when <paramref name="axisName"/> is <c>null</c>.
        /// Equivalent to toggling the 🧊 freeze button on the corresponding <see cref="AxisTracker"/>.
        /// Has no effect on a window that receives a new data instance on every update (see <see cref="UpdateFreezeButtonVisibility"/>).
        /// </summary>
        /// <param name="axisName">
        /// The <see cref="Axis.Name"/> of the axis to freeze, or <c>null</c> to deactivate.
        /// </param>
        public void SetOrthogonalView(string? axisName)
        {
            if (axisName == null)
            {
                foreach (var t in _axisTrackers.Values)
                    t.FreezeButton.IsChecked = false;
                return;
            }
            if (ReceivesNewDataInstances) return;
            if (_axisTrackers.TryGetValue(axisName, out var tracker))
                tracker.FreezeButton.IsChecked = true;
        }

        /// <summary>
        /// Gets the name of the axis currently shown in orthogonal side views,
        /// or <c>null</c> when no orthogonal view is active.
        /// </summary>
        public string? OrthogonalViewAxisName => _orthoController.ActiveAxisName;

        // ── XY Projection (Z-direction) ─ opens a separate MatrixPlotter window ──

        // XY (Z-direction) projection window opened via ProjectionSelector
        private MatrixPlotter? _xyProjectionWindow;

        private void OnXYProjectionChanged(object? sender, IMatrixData? projectionData)
        {
            if (projectionData == null)
            {
                CloseXYProjectionWindow();
                return;
            }

            bool colorCoded = _orthoPanel.ProjectionSelector.IsColorCoded(ProjectionPlane.XY);
            var xyMode = _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY);
            string modeName = ProjectionModeName(
                colorCoded, xyMode, _orthoPanel.ProjectionSelector.GetColorCodedBlend(ProjectionPlane.XY));
            if (_xyProjectionWindow == null || !_xyProjectionWindow.IsVisible)
            {
                _xyProjectionWindow = MatrixPlotter.Create(projectionData, _view.Lut,
                    $"{modeName} Z-Projection — {Title}");
                _xyProjectionWindow.IsSecondaryWindow = true;
                // The projected data carries no axes, so overlay text tokens read this plotter's instead.
                _xyProjectionWindow.OverlayAxisSource = this;
                _xyProjectionWindow.Closed += OnXYProjectionWindowClosed;
                _xyProjectionWindow.ColorCodedParamsChanged += OnXYProjectionColorCodedParamsChanged;
                PlotWindowNotifier.SetParentLink(_xyProjectionWindow, this);
                PositionBesideParent(_xyProjectionWindow, this);
                ApplyCompositeStateToProjectionWindow(_xyProjectionWindow, establishMode: true);
                if (colorCoded)
                {
                    _xyProjectionWindow.EnterColorCodedProjectionMode(
                        _orthoController.ActiveAxisCount, ToChildColorCodedParams(_orthoController.ColorCodedParams));
                    var (autoMin, autoMax) = _orthoController.LastColorCodedAutoRange;
                    _xyProjectionWindow.UpdateColorCodedAutoRange(autoMin, autoMax);
                    _xyProjectionWindow.UpdateColorCodedInfo(_orthoController.LastColorCodedRenderInfo);
                }
                _xyProjectionWindow.Show();
            }
            else
            {
                _xyProjectionWindow.Title = $"{modeName} Z-Projection — {Title}";
                bool wasColorCoded = _xyProjectionWindow.IsColorCodedProjectionChild;
                // UpdateProjectionData renders synchronously (SetMatrixDataInternal) against
                // whatever RenderingMode is already set. ExitColorCodedProjectionMode already
                // cleared ColorCodedInfo to null, so the ColorCoded->Lut transition itself cannot
                // render stale winner-index/depth-colour data against the new plain projection --
                // but RenderingMode itself still needs to move off ColorCoded, or nothing else
                // ever would. Strictly on the transition, though: forcing Lut on every refresh
                // also stomped Composite (which used to need re-asserting further down) and, worse,
                // any mode the user had picked in this window.
                bool leftColorCoded = !colorCoded && wasColorCoded;
                if (leftColorCoded)
                {
                    _xyProjectionWindow.ExitColorCodedProjectionMode();
                    _xyProjectionWindow._view.RenderingMode = RenderingMode.Lut;
                }
                // A Composite payload and a plain projection are different stack shapes, so the
                // parent going in or out of Composite changes the layout under this window. Its
                // chrome is rebuilt for the new shape (UpdateProjectionData handles that), and the
                // mode it was in no longer describes anything - it has to be established again.
                bool layoutChanged = !HasSameAxisLayout(_xyProjectionWindow.MatrixData, projectionData);
                // Use UpdateProjectionData instead of ViewModel.MatrixData= to avoid a full
                // SetMatrixData re-initialization that would clear overlay state (ROI mode,
                // line profiles, region statistics) on every parent frame change.
                _xyProjectionWindow.UpdateProjectionData(projectionData);
                // Leaving ColorCoded is the other moment this window has no mode of its own to
                // keep: it landed on Lut by default, not by choice.
                ApplyCompositeStateToProjectionWindow(_xyProjectionWindow,
                                                     establishMode: leftColorCoded || layoutChanged);
                // EnterColorCodedProjectionMode only runs on the LUT/Max/Min -> ColorCoded
                // transition (swaps the details panel, seeds the header controls); once already a
                // ColorCoded child, only the RenderingMode needs restoring on every frame -- rebuilding
                // the details panel or reseeding Start/End from the parent every frame would fight
                // whatever the user is mid-typing in those boxes.
                if (colorCoded && !wasColorCoded)
                    _xyProjectionWindow.EnterColorCodedProjectionMode(
                        _orthoController.ActiveAxisCount, ToChildColorCodedParams(_orthoController.ColorCodedParams));
                else if (colorCoded)
                    _xyProjectionWindow._view.RenderingMode = RenderingMode.ColorCoded;
                // Keep the child's Auto-mode display, Search buttons, and rendered colours current
                // with every fresh recompute, not just the first entry into ColorCoded mode.
                if (colorCoded)
                {
                    var (autoMin, autoMax) = _orthoController.LastColorCodedAutoRange;
                    _xyProjectionWindow.UpdateColorCodedAutoRange(autoMin, autoMax);
                    _xyProjectionWindow.UpdateColorCodedInfo(_orthoController.LastColorCodedRenderInfo);
                    // Recompute triggered by the child's own Start/End/palette/range/Invert edit
                    // (see OnXYProjectionColorCodedParamsChanged, which set this true) has now been
                    // applied. Harmless no-op if this update came from something else (e.g. the
                    // parent's own frame navigation) -- the indicator was never turned on for those.
                    _xyProjectionWindow.SetColorCodedBusy(false);
                }
            }
        }

        /// <summary>
        /// The short mode name used in the projection window's title and the baked dataset's label:
        /// the plain projection's, or, for ColorCoded, the depth-color variant's (RGB-Max/RGB-Add/RGB-Avg
        /// when <paramref name="colorBlend"/> is set, otherwise the winner-take-all Color(Max)/(Min)).
        /// </summary>
        private static string ProjectionModeName(bool colorCoded, ProjectionMode mode, ColorCodedBlend? colorBlend)
            => colorCoded
                ? colorBlend switch
                {
                    ColorCodedBlend.Maximum => "Color(RGB-Max)",
                    ColorCodedBlend.Additive => "Color(RGB-Add)",
                    ColorCodedBlend.Average => "Color(RGB-Avg)",
                    _ => mode == ProjectionMode.Minimum ? "Color(Min)" : "Color(Max)",
                }
                : mode switch
                {
                    ProjectionMode.Minimum => "Min",
                    ProjectionMode.Average => "Avg",
                    _ => "Max",
                };

        private static ColorCodedProjectionParams ToChildColorCodedParams(
            (int Start, int End, LookupTable? DepthLut, bool RangeFixed, double FixedMin, double FixedMax, bool Invert) p)
            => new(p.Start, p.End, p.DepthLut, p.RangeFixed, p.FixedMin, p.FixedMax, p.Invert);

        /// <summary>
        /// Forwards a Start/End/depth-LUT/ValueRange/Invert edit made in the ColorCoded projection
        /// child window's own details panel to the OrthogonalViewController that actually owns the
        /// source data and does the scan+colorize -- see MatrixPlotter.ColorCoded.cs's header
        /// comment for why the child cannot recolor itself. Marks the child busy first: the scan
        /// this kicks off runs in the background (see ComputeXYProjectionAsync), but for a large
        /// XY / long-axis dataset still takes long enough that the histogram it's about to redraw
        /// should show something is happening. Cleared by OnXYProjectionChanged once the result
        /// comes back and gets applied.
        /// </summary>
        private void OnXYProjectionColorCodedParamsChanged(object? sender, ColorCodedProjectionParams e)
        {
            _xyProjectionWindow?.SetColorCodedBusy(true);
            _orthoController.SetColorCodedParams(
                e.Start, e.End, e.DepthLut, e.RangeFixed, e.FixedMin, e.FixedMax, e.Invert);
        }

        /// <summary>
        /// When Composite mode is active on this (parent) plotter, the projection window's data
        /// (built by <see cref="OrthogonalViewController.ComputeXYProjectionAsync"/> via the same
        /// per-channel-extract-and-merge as the XZ/YZ side views, <c>BuildChannelComposite</c>) is
        /// an N-frame Composite payload carrying its own cloned "Channel" axis — not a stripped
        /// N-frame stack — so the projection window can drive the real
        /// <see cref="EnterCompositeMode"/> through it, the same as any other Composite window.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="SyncCurrentDataFromView"/> re-syncs <c>_currentData</c> - see its own doc
        /// comment for why <see cref="UpdateProjectionData"/> leaves that necessary - before
        /// anything below reads it, on every call, not just the first.
        /// </para>
        /// <para>
        /// <see cref="EnterCompositeMode"/> itself runs only while the window's mode is being
        /// established (rebuilding the header/details UI on every parent frame step would be
        /// wasteful and would drop any in-progress interaction, e.g. an open flyout). The channel
        /// axis's position is stable across updates — <c>BuildChannelComposite</c> always merges in
        /// the same shape — so later calls only need the cheap per-frame refresh
        /// <see cref="ApplyCompositeFrameIndices"/> already does for an ordinary window's Z/T
        /// navigation. The state itself comes from <see cref="SeedChildCompositeState"/> (shared
        /// with Extract's live-extract windows), which hands the window a starting point and then
        /// leaves it independently tunable; see that method's doc comment for the full reasoning.
        /// </para>
        /// </remarks>
        /// <param name="establishMode">
        /// <c>true</c> while the window has no rendering mode of its own to keep: it was just
        /// created, or it just left ColorCoded because the projection type changed. Then Composite
        /// is seeded from this parent, so a Composite parent opens a Composite projection.
        /// <para>
        /// <c>false</c> for an ordinary data refresh. Whatever mode the window is in is its own
        /// from then on — including LUT, if the user chose "Switch to LUT mode" from the Composite
        /// header. That is a legitimate destination, not a state to correct: the projection payload
        /// keeps its channel axis either way, so LUT mode simply browses the channels one at a
        /// time through the axis tracker. Re-seeding here would undo the choice on the parent's
        /// very next frame step.
        /// </para>
        /// </param>
        private void ApplyCompositeStateToProjectionWindow(MatrixPlotter window, bool establishMode)
        {
            window.SyncCurrentDataFromView();

            // Look the axis up by the parent's composited axis name, not a hardcoded "Channel" --
            // Composite is no longer restricted to that one name (see
            // ColorCoded_View_InitialDesign.md section 3.3.7).
            Axis? channelAxis = null;
            if (_isCompositeMode && _currentData != null && _compositeAxisDimIndex >= 0
                && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount)
            {
                string parentAxisName = _currentData.Dimensions[_compositeAxisDimIndex].Name;
                channelAxis = window._currentData?.Axes.FindAxis(parentAxisName);
            }

            if (channelAxis == null)
            {
                // No Composite payload is coming any more - typically the parent just left
                // Composite mode. A window still rendering as Composite has to follow: its
                // CompositeFrameIndices stays sized for the old channel count, and the mismatch
                // surfaces later as an out-of-range read (e.g. from the hover value overlay).
                if (window._isCompositeMode) window.ExitCompositeMode();
                return;
            }

            if (window._isCompositeMode)
            {
                window.ApplyCompositeFrameIndices();
                return;
            }

            if (establishMode) SeedChildCompositeState(window, channelAxis);
        }

        private void OnXYProjectionWindowClosed(object? sender, EventArgs e)
        {
            if (_xyProjectionWindow != null)
                _xyProjectionWindow.OverlayAxisSource = null;
            _xyProjectionWindow = null;
            _orthoPanel.ProjectionSelector.SetEnabled(ProjectionPlane.XY, false);
            _orthoController.ClearXYProjection();
        }

        private void CloseXYProjectionWindow()
        {
            if (_xyProjectionWindow != null)
            {
                _xyProjectionWindow.Closed -= OnXYProjectionWindowClosed;
                _xyProjectionWindow.ColorCodedParamsChanged -= OnXYProjectionColorCodedParamsChanged;
                _xyProjectionWindow.OverlayAxisSource = null;
                _xyProjectionWindow.Close();
                _xyProjectionWindow = null;
                _orthoPanel.ProjectionSelector.SetState(ProjectionPlane.XY, false,
                    _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY));
                _orthoController.ClearXYProjection();
            }
        }

        // ── Axis Scale context menu for side views ────────────────────────────

        // ── Create projected data ──────────────────────────────────────────────
        //
        // Bakes the projection into a standalone dataset instead of the ephemeral single frame the
        // XY-projection window shows. The result owns the surviving axes, so it can be navigated,
        // saved as .mxd, and exported as a movie through the ordinary export path - no special
        // parent-driven export host required.

        private CancellationTokenSource? _projectionCts;

        private async Task InvokeCreateProjectedDataAsync(ProjectionPlane plane, ProjectionMode initialMode)
        {
            if (_currentData == null) return;

            string axisName = _orthoController.ActiveAxisName ?? "Z";
            var axes = _currentData.Axes;
            if (axes.FindAxis(axisName) == null) return;

            // The ortho axis always supplies the volume; the plane decides which direction is
            // collapsed within it, mirroring how the live side views are built.
            // planeLabel follows ProjectionSelector.UpdateAxisName so the wording matches the UI.
            var (viewFrom, planeLabel, alongLabel) = plane switch
            {
                ProjectionPlane.XZ => (ViewFrom.Y, $"X-{axisName}", "Y"),
                ProjectionPlane.YZ => (ViewFrom.X, $"{axisName}-Y", "X"),
                _ => (ViewFrom.Z, "X-Y", axisName),
            };

            // ColorCoded is scoped to the XY plane only (ProjectionSelector.IsColorCoded is always
            // false for XZ/YZ) and is mutually exclusive with Composite (SetCompositeActive already
            // keeps the UI from offering Color(Max)/(Min) while Composite is on), so channelAxisName
            // below and isColorCoded here never both apply. Also requires the row's own preview
            // checkbox to be on: the combo alone can show Color(Max)/(Min) while unchecked (the
            // "Create Data" button is deliberately independent of it), but OrthogonalViewController's
            // ColorCodedParams (Start/End/LUT/Invert) are only ever initialized by the checkbox's own
            // enable handler -- baking without that would use stale or default-zero values instead of
            // what's actually on screen. Falls back to plain Maximum/Minimum in that case, matching
            // initialMode (ProjectionSelector.GetMode already maps Color(Max)/(Min) to Maximum/Minimum).
            bool isColorCoded = plane == ProjectionPlane.XY
                && _orthoPanel.ProjectionSelector.IsColorCoded(ProjectionPlane.XY)
                && _orthoPanel.ProjectionSelector.IsProjectionEnabled(ProjectionPlane.XY);
            // Color(RGB-Max)/(RGB-Add)/(RGB-Avg) bake the same way as Color(Max)/(Min) -- read-only scan
            // parameters, RGB-decomposed output -- but blend with ColorCodedRgbBlender instead of
            // picking a winner. null = winner-take-all.
            ColorCodedBlend? colorCodedBlend = isColorCoded
                ? _orthoPanel.ProjectionSelector.GetColorCodedBlend(ProjectionPlane.XY)
                : null;
            // Captured once here (not re-read inside the Task.Run below) so the value baked and the
            // value recorded in the "reproduce this" history detail below are guaranteed to be the
            // same snapshot -- the dialog only ever displays this read-only, so it cannot itself
            // change while the dialog is open, but hoisting it here removes any doubt.
            (int Start, int End, LookupTable? DepthLut, bool RangeFixed, double FixedMin, double FixedMax, bool Invert)? ccp = null;
            CreateProjectionDialog.ColorCodedBakeInfo? colorCodedInfo = null;
            if (isColorCoded)
            {
                ccp = _orthoController.ColorCodedParams;
                string lutName = (ccp.Value.DepthLut ?? ColorThemes.Spectrum).Name;
                colorCodedInfo = new CreateProjectionDialog.ColorCodedBakeInfo(ccp.Value.Start, ccp.Value.End, lutName, ccp.Value.Invert);
            }

            var p = await CreateProjectionDialog.ShowAsync(
                this, initialMode, planeLabel, alongLabel, axisName, axes, IsReplaceDataBlocked, colorCodedInfo);
            if (p == null) return;

            // Composite blends channels at render time, so a projection taken at a single position
            // has to be built per channel and merged - exactly what the live XY-projection does.
            // The all-positions path needs none of that: the Channel axis simply survives.
            string? channelAxisName = _isCompositeMode && _compositeAxisDimIndex >= 0
                ? _currentData.Dimensions[_compositeAxisDimIndex].Name
                : null;
            if (string.Equals(channelAxisName, axisName, StringComparison.OrdinalIgnoreCase))
                channelAxisName = null;   // the channel axis is the one being collapsed

            string coloredAxisName = $"{axisName} (Colored)";
            string modeLabel = ProjectionModeName(isColorCoded, initialMode, colorCodedBlend);

            var source = _currentData;
            _projectionCts?.Dispose();
            _projectionCts = new CancellationTokenSource();
            var ct = _projectionCts.Token;
            var progress = BeginProgress($"Creating {modeLabel} {alongLabel}-projection…", blockInput: true, _projectionCts);

            IMatrixData result;
            // Only meaningful when isColorCoded; populated from the Task.Run below so the history
            // detail recorded further down reflects the ValueMin/Max actually applied (Auto range
            // mode resolves these from the data at bake time, not from ccp alone -- see
            // ColorCodedBakeResult's doc comment).
            double colorCodedValueMin = 0, colorCodedValueMax = 0;
            try
            {
                result = await Task.Run(() =>
                {
                    if (isColorCoded)
                    {
                        var baked = p.ThisPositionOnly
                            ? BakeColorCodedThisPosition(source, axisName, initialMode, ccp!.Value, colorCodedBlend)
                            : BakeColorCodedAllPositions(source, axisName, initialMode, ccp!.Value, colorCodedBlend, progress, ct);
                        colorCodedValueMin = baked.ValueMin;
                        colorCodedValueMax = baked.ValueMax;
                        return baked.Data;
                    }
                    return p.ThisPositionOnly
                        ? ProjectSinglePosition(source, axisName, viewFrom, initialMode, channelAxisName)
                        : source.Apply(new CreateProjectedStackOperation(
                            viewFrom, initialMode, axisName, LoadingMode.Auto, progress, ct));
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (OutOfMemoryException)
            {
                await ShowMessageDialogAsync("Out of Memory",
                    "Not enough memory to build the projection. Operation cancelled.");
                return;
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Create Projected Data Failed", ex.Message);
                return;
            }
            finally
            {
                _projectionCts?.Dispose();
                _projectionCts = null;
                EndProgress();
            }

            // "this position" alone doesn't say which position -- with survivors (Time, Channel, ...)
            // present, a bare "this position" can't be reconstructed later; record their indices too,
            // the same way CreateProjectionDialog's own description label already does for the user
            // while the dialog is still open. Read now (not before the bake): input was blocked for
            // its whole duration (BeginProgress's blockInput: true) so these can't have moved, but
            // this is the moment that actually corresponds to "the position result was baked at".
            //
            // With no surviving axes (a single-axis dataset), "This position only" isn't a real
            // choice at all -- CreateProjectionDialog forces ThisPositionOnly=true and hides the
            // checkbox entirely (hasSurvivors is false there too) -- so there is no "position" to
            // name and no alternative ("all positions") that was passed over. Say nothing rather
            // than a bare "this position", which would wrongly imply a choice was made.
            var survivingAxes = axes.Where(a => !string.Equals(a.Name, axisName, StringComparison.OrdinalIgnoreCase)).ToList();
            string? scope = p.ThisPositionOnly
                ? (survivingAxes.Count > 0
                    ? "this position (" + string.Join(", ", survivingAxes.Select(a => $"{a.Name}={a.Index}")) + ")"
                    : null)
                : "all positions";
            string detail = scope != null
                ? $"{planeLabel} plane, {scope}, {result.FrameCount} frame(s)"
                : $"{planeLabel} plane, {result.FrameCount} frame(s)";
            if (isColorCoded)
            {
                // Everything needed to redo this exact bake by hand: which axis, the Start/End
                // sweep, the depth LUT, the Invert state, and the ValueMin/Max normalization that
                // was actually applied (Auto range mode only pins this down at bake time -- see
                // ColorCodedBakeResult). Without this the plain "X-Y plane, ..." line above gives no
                // way to reconstruct what was on screen when the bake was taken.
                string lutName = (ccp!.Value.DepthLut ?? ColorThemes.Spectrum).Name;
                detail += $"; ColorCoded {axisName} {ccp.Value.Start}-{ccp.Value.End}, LUT {lutName}, " +
                    $"Range {colorCodedValueMin:G6}-{colorCodedValueMax:G6}, Invert {(ccp.Value.Invert ? "Yes" : "No")}";
            }
            AppendHistory(result, $"{modeLabel} {alongLabel}-Projection", Title, detail);

            if (p.ReplaceData)
            {
                var newTitle = $"{modeLabel} {alongLabel}-Projection of {Title}";
                SetMatrixData(result, closeDerivedWindows: true);
                Title = newTitle;
                SetDirty(DirtyFlags.Data, true);
            }
            else
            {
                // Create, not CreateLinked: the projection is baked into its own data, so the
                // window owns it outright - it must not be marked secondary (which would suppress
                // the unsaved-changes prompt) nor be closed when this plotter closes.
                var child = MatrixPlotter.Create(result, _view.Lut,
                    $"{modeLabel} {alongLabel}-Projection of {Title}");
                if (channelAxisName != null) SeedChildCompositeMode(child, result, channelAxisName);
                // The RGB-decomposed bake is only actually a colour image once Composite blends its
                // three channels back together - open the child already showing that, rather than
                // an R/G/B-navigable grayscale stack the user would have to switch modes on.
                else if (isColorCoded) SeedColorCodedCompositeMode(child, result, coloredAxisName);
                child.Show();
            }
        }

        /// <summary>
        /// Projects the volume at the current indices only. In Composite mode every channel is
        /// projected separately and merged, mirroring <c>OrthogonalViewController.ComputeXYProjectionAsync</c>,
        /// so the result still blends instead of collapsing to whichever channel ActiveIndex points at.
        /// </summary>
        private IMatrixData ProjectSinglePosition(
            IMatrixData source, string axisName, ViewFrom viewFrom, ProjectionMode mode, string? channelAxisName)
        {
            if (channelAxisName == null)
                return source.Apply(new ProjectionOperation(viewFrom, mode, axisName));

            int dimIndex = _compositeAxisDimIndex;
            int channelCount = source.Dimensions[dimIndex].Count;
            return OrthogonalViewController.BuildChannelComposite(source, dimIndex, channelCount,
                bi => source.Apply(new ProjectionOperation(viewFrom, mode, axisName, bi)));
        }

        /// <summary>
        /// Seeds a freshly baked ColorCoded child window with a fixed, pure R/G/B Additive Composite
        /// recipe that reproduces the live ColorCoded appearance exactly — deliberately not
        /// <see cref="SeedChildCompositeMode"/>, which copies whatever recipes the parent currently
        /// holds. The parent is never itself in Composite mode while ColorCoded is active (the two
        /// are mutually exclusive), so any recipes it happens to be holding are leftovers from an
        /// unrelated earlier Composite session on that same window — and <c>EnterCompositeMode</c>
        /// reuses them verbatim whenever their count happens to match the new axis's (3 channels),
        /// which is exactly what produced wrong colours and a stale Gain. Additive blending (not a
        /// value read from the parent) is what makes R*(1,0,0)+G*(0,1,0)+B*(0,0,1) reconstruct the
        /// original pixel exactly — see <see cref="ColorAxis.CreateRgb"/>'s remarks. Range mode is
        /// Fixed 0..255, not Current/All: the live ColorCoded view's own range mode (Auto or Fixed)
        /// is already what produced each baked byte value (<c>ColorCodedColorizer.Colorize</c>'s
        /// <c>valueMin</c>/<c>valueMax</c> normalization, evaluated at bake time) — the byte itself
        /// *is* the final 0..255 display intensity already. Composite must therefore treat the byte
        /// range as an identity 0..255 mapping; re-deriving a *different* min/max from the baked
        /// data's own observed extent (Current/All) would re-normalize on top of that and no longer
        /// match what the live view showed.
        /// </summary>
        private void SeedColorCodedCompositeMode(MatrixPlotter child, IMatrixData childData, string coloredAxisName)
        {
            var axis = childData.Axes.FindAxis(coloredAxisName);
            if (axis == null) return;
            child._compositeRecipes = new List<BlendRecipe>
            {
                new BlendRecipe(true, unchecked((int)0xFFFF0000), 0, 255),
                new BlendRecipe(true, unchecked((int)0xFF00FF00), 0, 255),
                new BlendRecipe(true, unchecked((int)0xFF0000FF), 0, 255),
            };
            child._compositeBlendMode = BlendMode.Additive;
            child._compositeScope = CompositeRangeScope.Global;
            child._compositeGlobalMode = ValueRangeMode.Fixed;
            // In Fixed mode ApplyCompositeRanges mirrors the recipes' shared 0..255 onto the header bar.
            child.EnterCompositeMode(axis);
        }

        /// <summary>
        /// The ColorCoded bake helpers' result: the baked dataset plus the ValueMin/ValueMax that
        /// were actually applied. Start/End/LUT/Invert are already known to the caller (the same
        /// <c>ColorCodedParams</c> snapshot used to seed the dialog's read-only info block), but
        /// ValueMin/Max are only pinned down here when the live view is in Auto range mode -- the
        /// caller needs them back to record a complete, reproducible history entry (see
        /// <see cref="InvokeCreateProjectedDataAsync"/>'s <c>AppendHistory</c> call).
        /// </summary>
        private readonly record struct ColorCodedBakeResult(IMatrixData Data, double ValueMin, double ValueMax);

        /// <summary>
        /// Bakes the live ColorCoded projection at the current position only into an RGB-decomposed
        /// 3-frame dataset (no surviving axes kept navigable) — the ColorCoded counterpart to
        /// <see cref="ProjectSinglePosition"/>. Every scan/colorize parameter (Start/End, depth LUT,
        /// Invert, ValueMin/Max) is inherited read-only from <c>_orthoController</c>'s live state,
        /// never re-entered by the dialog — see ColorCoded_View_InitialDesign.md section 3.3.6.
        /// </summary>
        private ColorCodedBakeResult BakeColorCodedThisPosition(
            IMatrixData source, string axisName, ProjectionMode mode,
            (int Start, int End, LookupTable? DepthLut, bool RangeFixed, double FixedMin, double FixedMax, bool Invert) p,
            ColorCodedBlend? blendMode)
        {
            // The winner scan is needed for the Auto range even when RGB-blending (the range is the
            // Maximum projection's), exactly as in the live view.
            var (winnerIndex, winnerValue) = source.Apply(new ExtremumIndexOperation(mode, p.Start, p.End, axisName));
            var (naturalMin, naturalMax) = winnerValue.GetValueRange();
            double valueMin = p.RangeFixed ? p.FixedMin : naturalMin;
            double valueMax = p.RangeFixed ? p.FixedMax : naturalMax;
            var depthColors = BuildColorCodedDepthColors(p.DepthLut, p.Start, p.End, p.Invert);
            var packed = blendMode is { } blend
                ? ColorCodedRgbBlender.Blend(source, axisName, p.Start, p.End, valueMin, valueMax, depthColors, blend)
                : ColorCodedColorizer.Colorize(winnerIndex, winnerValue, p.Start, valueMin, valueMax, depthColors);
            return new ColorCodedBakeResult(DecomposeToColoredAxis(packed, axisName), valueMin, valueMax);
        }

        /// <summary>
        /// Bakes the ColorCoded projection for every combination of the surviving axes (those other
        /// than <paramref name="axisName"/>) into one navigable RGB-decomposed dataset — the
        /// ColorCoded counterpart to <c>CreateProjectedStackOperation</c>. Unlike that Core-layer
        /// operation this stays UI-side (in-memory only, no Virtual/memory-mapped output) since it
        /// needs <see cref="ColorCodedColorizer"/>; the RGB byte output is small enough for that to
        /// be an acceptable first cut.
        /// </summary>
        private ColorCodedBakeResult BakeColorCodedAllPositions(
            IMatrixData source, string axisName, ProjectionMode mode,
            (int Start, int End, LookupTable? DepthLut, bool RangeFixed, double FixedMin, double FixedMax, bool Invert) p,
            ColorCodedBlend? blendMode, IProgress<int>? progress, CancellationToken ct)
        {
            double valueMin, valueMax;
            if (p.RangeFixed)
            {
                valueMin = p.FixedMin;
                valueMax = p.FixedMax;
            }
            else
            {
                // Auto mode: one common natural range (the live view's most recently displayed one)
                // across every baked frame, rather than re-normalizing each combination on its own —
                // otherwise the same absolute intensity would map to a different brightness on
                // different frames, making the baked stack's colours inconsistent as it's navigated.
                var (autoMin, autoMax) = _orthoController.LastColorCodedAutoRange;
                valueMin = autoMin ?? 0;
                valueMax = autoMax ?? 0;
            }
            var depthColors = BuildColorCodedDepthColors(p.DepthLut, p.Start, p.End, p.Invert);

            var survivorFrameIndices = source.Dimensions.GetIndicesForSlice(axisName, 0);
            var survivorAxes = source.Dimensions.CreateAxesWithout(axisName);
            var coloredAxis = ColorAxis.CreateRgb();
            coloredAxis.Name = $"{axisName} (Colored)";

            var frames = new List<byte[]>(survivorFrameIndices.Count * 3);
            int width = 0, height = 0;
            double xMin = 0, xMax = 0, yMin = 0, yMax = 0;
            string xUnit = "", yUnit = "";
            int done = 0;
            foreach (int fi in survivorFrameIndices)
            {
                ct.ThrowIfCancellationRequested();
                var bi = source.Dimensions.GetAxisIndices(fi);
                MatrixData<int> packed;
                if (blendMode is { } blend)
                {
                    // No winner scan needed: the range is already fixed above.
                    packed = ColorCodedRgbBlender.Blend(source, axisName, p.Start, p.End, valueMin, valueMax, depthColors, blend, bi);
                }
                else
                {
                    var (winnerIndex, winnerValue) = source.Apply(new ExtremumIndexOperation(mode, p.Start, p.End, axisName, bi));
                    packed = ColorCodedColorizer.Colorize(winnerIndex, winnerValue, p.Start, valueMin, valueMax, depthColors);
                }
                if (width == 0)
                {
                    width = packed.XCount;
                    height = packed.YCount;
                    (xMin, xMax, yMin, yMax) = (packed.XMin, packed.XMax, packed.YMin, packed.YMax);
                    (xUnit, yUnit) = (packed.XUnit, packed.YUnit);
                }
                var (r, g, b) = DecomposeArgb(packed.GetArray(0));
                frames.Add(r);
                frames.Add(g);
                frames.Add(b);
                progress?.Report((int)(100.0 * ++done / survivorFrameIndices.Count));
            }

            var merged = new MatrixData<byte>(width, height, frames);
            merged.SetXYScale(xMin, xMax, yMin, yMax);
            merged.XUnit = xUnit;
            merged.YUnit = yUnit;
            var allAxes = new Axis[survivorAxes.Length + 1];
            allAxes[0] = coloredAxis;
            Array.Copy(survivorAxes, 0, allAxes, 1, survivorAxes.Length);
            merged.DefineDimensions(allAxes);
            return new ColorCodedBakeResult(merged, valueMin, valueMax);
        }

        private static int[] BuildColorCodedDepthColors(LookupTable? depthLut, int start, int end, bool invert)
        {
            int sliceCount = end - start + 1;
            var depthColors = (depthLut ?? ColorThemes.Spectrum).Resample(sliceCount).AsSpan().ToArray();
            if (invert) Array.Reverse(depthColors);
            return depthColors;
        }

        private static IMatrixData DecomposeToColoredAxis(MatrixData<int> packed, string axisName)
        {
            var (r, g, b) = DecomposeArgb(packed.GetArray(0));
            var md = new MatrixData<byte>(packed.XCount, packed.YCount, new List<byte[]> { r, g, b });
            md.SetXYScale(packed.XMin, packed.XMax, packed.YMin, packed.YMax);
            md.XUnit = packed.XUnit;
            md.YUnit = packed.YUnit;
            var coloredAxis = ColorAxis.CreateRgb();
            coloredAxis.Name = $"{axisName} (Colored)";
            md.DefineDimensions(coloredAxis);
            return md;
        }

        private static (byte[] R, byte[] G, byte[] B) DecomposeArgb(int[] packed)
        {
            int n = packed.Length;
            var r = new byte[n];
            var g = new byte[n];
            var b = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int px = packed[i];
                r[i] = (byte)(px >> 16);
                g[i] = (byte)(px >> 8);
                b[i] = (byte)px;
            }
            return (r, g, b);
        }

        private System.Collections.Generic.IEnumerable<MenuItem> BuildSideViewContextMenuItems(ViewFrom direction)
        {
            var scaleItem = new MenuItem { Header = "Axis View Scale\u2026", FontSize = 11 };
            scaleItem.Icon = new global::Avalonia.Controls.PathIcon
            {
                Data = MxPlot.UI.Avalonia.Helpers.MenuIcons.Ruler,
                Width = 12,
                Height = 12,
            };
            scaleItem.Click += async (_, _) => await ShowAxisScaleDialogAsync();
            yield return scaleItem;

            // No ellipsis: unlike the dialog-driven items above, this runs immediately (progress +
            // Cancel in the status bar), matching a single fixed position -- see InvokeRestackAsync.
            string stackLetter = direction == ViewFrom.X ? "X" : "Y";
            var restackItem = new MenuItem { Header = $"Restack along {stackLetter}", FontSize = 11 };
            // CubeLeftSolid for BottomView (direction=Y), CubeRightSolid for RightView (direction=X).
            var restackIcon = direction == ViewFrom.X
                ? MxPlot.UI.Avalonia.Helpers.MenuIcons.CubeRightSolid
                : MxPlot.UI.Avalonia.Helpers.MenuIcons.CubeLeftSolid;
            restackItem.Icon = new global::Avalonia.Controls.PathIcon
            {
                Data = restackIcon,
                Width = 12,
                Height = 12,
                // PathIcon has no colour of its own -- ControlFactory.MakeContent applies this same
                // lookup for every other menu icon; this item builds its PathIcon directly instead of
                // going through that helper, so it has to look the brush up itself.
                Foreground = MxPlot.UI.Avalonia.Helpers.MenuIcons.DefaultBrush(restackIcon),
            };
            // Deliberately not "Reslice" -- that's ImageJ's term for a related but different
            // operation, and using it here would misleadingly imply parity with it.
            global::Avalonia.Controls.ToolTip.SetTip(restackItem,
                $"Reconstruct the volume as a new stack viewed from the {stackLetter} axis.");
            restackItem.Click += async (_, _) => await InvokeRestackAsync(direction);
            yield return restackItem;
        }

        private async System.Threading.Tasks.Task ShowAxisScaleDialogAsync()
        {
            var data = _currentData;
            string axisName = _orthoController.ActiveAxisName ?? "";
            double xStep = data?.XStep ?? 0;
            string xUnit = data?.XUnit ?? "";
            double yStep = data?.YStep ?? 0;
            string yUnit = data?.YUnit ?? "";

            // Convert current CustomAxisStep (d, physical units) → display ratio.
            // ratio = ZCount × d / (XCount × XStep)
            // When ScaleMode is Physical, Custom has never been configured for this axis,
            // so default the ratio to 1.0 instead of back-calculating from the placeholder value.
            int xCount = data?.XCount ?? 1;
            int zCount = _orthoPanel.BottomView.MatrixData?.YCount ?? 1;
            double axisStep = _orthoPanel.BottomView.MatrixData?.YStep ?? 0;
            var (xyDisplayW, xyDisplayH) = _orthoPanel.MainView.GetNaturalDims();
            int physicalAxisPx = (xStep > 0 && axisStep > 0 && xyDisplayW > 0)
                ? (int)System.Math.Round(xyDisplayW * axisStep / xStep)
                : zCount;
            double currentRatio;
            if (_orthoController.ScaleMode == OrthoScaleMode.Custom)
            {
                double currentD = _orthoController.CustomAxisStep;
                currentRatio = (xCount > 0 && xStep > 0 && zCount > 0)
                    ? zCount * currentD / (xCount * xStep)
                    : currentD;
            }
            else
            {
                currentRatio = 1.0;
            }

            var dlg = new OrthogonalScaleDialog(
                _orthoController.ScaleMode,
                currentRatio,
                axisName,
                xStep, xUnit,
                yStep, yUnit,
                xyDisplayW, xyDisplayH, physicalAxisPx);
            await dlg.ShowDialog(this);
            if (dlg.ResultMode.HasValue)
            {
                _orthoController.ApplyOrthoViewScale(dlg.ResultMode.Value, dlg.ResultCustomRatio);
                SetScaleDirty(true);
            }
        }

        // ── Restack (side-view context menu) ───────────────────────────────────
        //
        // Reslices the whole volume from a different viewpoint (VolumeAccessor<T>.Restack), not a
        // reduction like the projections above -- there is no "this position vs all positions" and
        // no ReplaceData choice: every other axis is pinned at its current position (the Composite
        // channel axis, which has no meaningful ActiveIndex of its own, is pinned at 0 instead), and
        // the result always opens as a new child window since its X/Y extent and axis count differ
        // from the parent's.

        private CancellationTokenSource? _restackCts;

        private async Task InvokeRestackAsync(ViewFrom direction)
        {
            if (_currentData == null) return;
#if RESTACK_DIAG
            var diagSw = Stopwatch.StartNew();
#endif

            string axisName = _orthoController.ActiveAxisName ?? "";
            if (string.IsNullOrEmpty(axisName) || _currentData.Axes.FindAxis(axisName) == null) return;

            // Core's CreateStackFromViewX/Y names the new stack axis after the physical axis it now
            // tracks ("X" or "Y") -- accurate but context-free once it's navigable in a new window.
            // Rename it to show what it replaced: e.g. an ortho axis "Time" restacked along Y becomes
            // "Time(Y)", so the axis still reads as "the Time-like navigation axis" even though it now
            // steps through physical Y. planeLabel mirrors the new frame's own (X,Y) layout -- for
            // direction=Y each frame is (physical X) x (old axisName), so "X-Time along Y"; for
            // direction=X each frame is (physical Y) x (old axisName), so "Y-Time along X".
            string stackLetter = direction == ViewFrom.X ? "X" : "Y";
            string otherLetter = direction == ViewFrom.X ? "Y" : "X";
            string newAxisName = $"{axisName}({stackLetter})";
            string planeLabel = $"{otherLetter}-{axisName}";

            var source = _currentData;

            // AsVolume/RestackOperation pin every other axis at baseIndices; default to each axis's
            // current position, except the Composite channel axis (if active), which never had a
            // single meaningful ActiveIndex while blending -- pin it at 0 instead of whatever stale
            // index it's carrying.
            int[] baseIndices = source.Dimensions.GetAxisIndices();
            bool compositeFixedAtZero = _isCompositeMode && _compositeAxisDimIndex >= 0;
            if (compositeFixedAtZero)
                baseIndices[_compositeAxisDimIndex] = 0;

            _restackCts?.Dispose();
            _restackCts = new CancellationTokenSource();
            var ct = _restackCts.Token;
            var progress = BeginProgress($"Restacking along {stackLetter}…", blockInput: true, _restackCts);

            IMatrixData result;
            try
            {
                result = await Task.Run(() =>
                    source.Apply(new RestackOperation(direction, axisName, baseIndices, LoadingMode.Auto, progress, ct)), ct);
#if RESTACK_DIAG
                Console.WriteLine($"[Restack:UI] Task.Run(Apply) returned to UI thread @ {diagSw.ElapsedMilliseconds} ms");
#endif
            }
            catch (OperationCanceledException) { return; }
            catch (OutOfMemoryException)
            {
                await ShowMessageDialogAsync("Out of Memory",
                    "Not enough memory to restack the volume. Operation cancelled.");
                return;
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Restack Failed", ex.Message);
                return;
            }
            finally
            {
                _restackCts?.Dispose();
                _restackCts = null;
                EndProgress();
            }

            // result has exactly one axis (the new stack axis) -- rename it in place.
            if (result.Dimensions.AxisCount > 0)
                result.Dimensions[0].Name = newAxisName;

            // Reproducibility: which axis the volume was extracted along, from which side view
            // (direction), and where every other axis was pinned.
            var otherAxisParts = new List<string>();
            for (int i = 0; i < source.Dimensions.AxisCount; i++)
            {
                var a = source.Dimensions[i];
                if (string.Equals(a.Name, axisName, StringComparison.OrdinalIgnoreCase)) continue;
                otherAxisParts.Add(compositeFixedAtZero && i == _compositeAxisDimIndex
                    ? $"{a.Name}=0 (Composite)"
                    : $"{a.Name}={a.Index}");
            }
            string detail = $"{planeLabel} along {stackLetter}";
            if (otherAxisParts.Count > 0)
                detail += " @ " + string.Join(", ", otherAxisParts);
            detail += $"; {result.FrameCount} frame(s)";
            AppendHistory(result, $"Restack along {stackLetter}", Title, detail);
#if RESTACK_DIAG
            Console.WriteLine($"[Restack:UI] Rename + AppendHistory done, calling MatrixPlotter.Create @ {diagSw.ElapsedMilliseconds} ms");
#endif

            var child = MatrixPlotter.Create(result, _view.Lut, $"Restack along {stackLetter} of {Title}");
#if RESTACK_DIAG
            Console.WriteLine($"[Restack:UI] MatrixPlotter.Create returned @ {diagSw.ElapsedMilliseconds} ms");
#endif
            child.Show();
#if RESTACK_DIAG
            Console.WriteLine($"[Restack:UI] child.Show() returned @ {diagSw.ElapsedMilliseconds} ms");
#endif
        }
    }
}
