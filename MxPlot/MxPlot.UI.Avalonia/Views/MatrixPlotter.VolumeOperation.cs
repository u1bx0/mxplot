// Diagnostic: stage timings for the post-Restack UI path (result hand-off, history, new-window
// creation) -- see VolumeAccessor.cs's matching RESTACK_DIAG for the Core-side timings and what it
// traced. Off by default; uncomment alongside VolumeAccessor.cs's RESTACK_DIAG to re-enable.
//#define RESTACK_DIAG
using Avalonia.Controls;
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.Core.IO;
using MxPlot.Core.IO.CacheStrategies;
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
                        _activeAction?.NotifyContextChanged(CreateActionContext());
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
                    _activeAction?.NotifyContextChanged(CreateActionContext());

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
        /// when there are multiple axes) while the user is dragging an <see cref="AxisTracker"/> slider.
        /// Called on <see cref="AxisTracker.SliderDragStarted"/> and on every <see cref="AxisTracker.IndexChanged"/>
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
                if (data.GetDiagnosticVirtualList() is { } vfl)
                {
                    if (_preVolumeModeCacheCapacity < 0)
                        _preVolumeModeCacheCapacity = vfl.CacheCapacity;

                    long frameSizeBytes = (long)data.XCount * data.YCount * data.ElementSize;
                    int idealVolumeFrames = targetAxis.Count * Math.Max(1, compositeAxis?.Count ?? 1);
                    int grown = VirtualCachePolicy.ComputeCapacity(frameSizeBytes, idealVolumeFrames, VirtualCachePolicy.VolumeModeMemoryBudgetFraction);
                    if (grown > vfl.CacheCapacity)
                        vfl.CacheCapacity = grown;
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
                // would block most of TrimCacheTo's eviction. Only after NeighborStrategy is in place
                // does trimming actually free the memory instead of mostly no-op'ing.
                data.CacheStrategy = new NeighborStrategy();

                if (_preVolumeModeCacheCapacity >= 0)
                {
                    if (data.GetDiagnosticVirtualList() is { } vfl)
                        vfl.TrimCacheTo(_preVolumeModeCacheCapacity);
                    _preVolumeModeCacheCapacity = -1;
                }
            }
        }

        /// <summary>
        /// The virtual dataset's <c>CacheCapacity</c> as it was just before the first time
        /// <see cref="ApplyCacheStrategy"/> grew it for Volume mode, so it can be restored (and the
        /// extra memory released via <see cref="IVirtualFrameList.TrimCacheTo"/>) once orthogonal
        /// viewing ends. -1 means "not currently elevated / nothing to restore".
        /// </summary>
        private int _preVolumeModeCacheCapacity = -1;

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
            string modeName = (colorCoded, xyMode) switch
            {
                (true, ProjectionMode.Minimum) => "Color(Min)",
                (true, _) => "Color(Max)",
                (false, ProjectionMode.Minimum) => "Min",
                (false, ProjectionMode.Average) => "Avg",
                _ => "Max",
            };
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
                ApplyCompositeStateToProjectionWindow(_xyProjectionWindow);
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
                // but RenderingMode itself still needs to move off ColorCoded whenever we're not
                // staying there, or nothing else ever would. ApplyCompositeStateToProjectionWindow
                // below may then re-target it to Composite if that's the appropriate mode instead.
                if (!colorCoded && wasColorCoded) _xyProjectionWindow.ExitColorCodedProjectionMode();
                if (!colorCoded) _xyProjectionWindow._view.RenderingMode = RenderingMode.Lut;
                // Use UpdateProjectionData instead of ViewModel.MatrixData= to avoid a full
                // SetMatrixData re-initialization that would clear overlay state (ROI mode,
                // line profiles, region statistics) on every parent frame change.
                _xyProjectionWindow.UpdateProjectionData(projectionData);
                ApplyCompositeStateToProjectionWindow(_xyProjectionWindow);
                // ApplyCompositeStateToProjectionWindow's "parent not in Composite" branch resets
                // to RenderingMode.Lut -- wrong while staying ColorCoded, so restore it after.
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
        /// When Composite mode is active on this (parent) plotter, <paramref name="projectionData"/>
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
        /// <see cref="EnterCompositeMode"/> itself is called only once, though (rebuilding the
        /// header/details UI on every parent frame step would be wasteful and would drop any
        /// in-progress interaction, e.g. an open flyout). The channel axis's position is stable
        /// across updates — <c>BuildChannelComposite</c> always merges in the same shape — so
        /// later calls only need the cheap per-frame refresh <see cref="ApplyCompositeFrameIndices"/>
        /// already does for an ordinary window's Z/T navigation. The window's Composite state is
        /// seeded from the parent only on that first call, via <see cref="SeedChildCompositeState"/>
        /// (shared with Extract's live-extract windows) - and deliberately not re-seeded
        /// afterward, since the window is meant to become independently tunable; see that method's
        /// doc comment for the full reasoning.
        /// </para>
        /// </remarks>
        private void ApplyCompositeStateToProjectionWindow(MatrixPlotter window)
        {
            if (_isCompositeMode)
            {
                window.SyncCurrentDataFromView();
                // Look the axis up by the parent's composited axis name, not a hardcoded "Channel" --
                // Composite is no longer restricted to that one name (see
                // ColorCoded_View_InitialDesign.md section 3.3.7).
                if (_currentData == null || _compositeAxisDimIndex < 0
                    || _compositeAxisDimIndex >= _currentData.Dimensions.AxisCount) return;
                string parentAxisName = _currentData.Dimensions[_compositeAxisDimIndex].Name;
                var channelAxis = window._currentData?.Axes.FindAxis(parentAxisName);
                if (channelAxis == null) return;

                if (!window._isCompositeMode)
                    SeedChildCompositeState(window, channelAxis);
                else
                {
                    window.ApplyCompositeFrameIndices();
                    // ApplyCompositeFrameIndices only refreshes composite frame data/ranges, not
                    // RenderingMode -- EnterCompositeMode (called once, inside SeedChildCompositeState
                    // above) is the only other place that sets it. OnXYProjectionChanged's caller
                    // unconditionally forces RenderingMode.Lut just before this whenever the new
                    // projection isn't ColorCoded (see its own comment on why), which silently broke
                    // this steady-state Composite path: the window stayed on Composite frame data but
                    // rendered it through Lut (grayscale) until something else happened to reset the
                    // mode again. Re-assert it explicitly every call instead of assuming it survived.
                    window._view.RenderingMode = RenderingMode.Composite;
                }
            }
            else if (window._isCompositeMode)
            {
                // The parent left Composite mode while this window was open — follow it back to LUT.
                window.ExitCompositeMode();
            }
            else
            {
                window._view.RenderingMode = RenderingMode.Lut;
            }
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
                this, initialMode, planeLabel, alongLabel, axisName, axes, IsSyncFollower, colorCodedInfo);
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
            string modeLabel = isColorCoded
                ? (initialMode == ProjectionMode.Minimum ? "Color(Min)" : "Color(Max)")
                : initialMode switch
                {
                    ProjectionMode.Minimum => "Min",
                    ProjectionMode.Average => "Avg",
                    _ => "Max",
                };

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
                            ? BakeColorCodedThisPosition(source, axisName, initialMode, ccp!.Value)
                            : BakeColorCodedAllPositions(source, axisName, initialMode, ccp!.Value, progress, ct);
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
                SetMatrixData(result, closeSyncFollowers: true);
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
            child.EnterCompositeMode(axis);
            // Fixed mode deliberately skips auto-populating displayed ranges in ApplyCompositeRanges
            // (EvaluateCompositeRange returns NaN for Fixed -- "keep the value the user already set"),
            // trusting each per-channel BlendRecipeBar to already show the right numbers from its own
            // constructor (new BlendRecipeBar(name, recipe) reads recipe.ValueMin/Max directly). The
            // header _compositeRangeBar has no such constructor path -- it starts with no range set
            // at all (BuildCompositeModeHeader only calls SetMode on it) -- so it has to be told here.
            child._compositeRangeBar?.SetRange(0, 255);
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
            (int Start, int End, LookupTable? DepthLut, bool RangeFixed, double FixedMin, double FixedMax, bool Invert) p)
        {
            var (winnerIndex, winnerValue) = source.Apply(new ExtremumIndexOperation(mode, p.Start, p.End, axisName));
            var (naturalMin, naturalMax) = winnerValue.GetValueRange();
            double valueMin = p.RangeFixed ? p.FixedMin : naturalMin;
            double valueMax = p.RangeFixed ? p.FixedMax : naturalMax;
            var depthColors = BuildColorCodedDepthColors(p.DepthLut, p.Start, p.End, p.Invert);
            var packed = ColorCodedColorizer.Colorize(winnerIndex, winnerValue, p.Start, valueMin, valueMax, depthColors);
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
            IProgress<int>? progress, CancellationToken ct)
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
                var (winnerIndex, winnerValue) = source.Apply(new ExtremumIndexOperation(mode, p.Start, p.End, axisName, bi));
                var packed = ColorCodedColorizer.Colorize(winnerIndex, winnerValue, p.Start, valueMin, valueMax, depthColors);
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
