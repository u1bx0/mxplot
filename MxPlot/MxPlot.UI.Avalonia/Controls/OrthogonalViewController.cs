using Avalonia;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.Core.Utils;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Controls how the depth axis (Z/Time/Channel/…) is scaled relative to the XY plane
    /// in the orthogonal side views.
    /// </summary>
    public enum OrthoScaleMode
    {
        /// <summary>
        /// Physical aspect: 1 physical unit on any axis occupies the same screen distance.
        /// Side-view zoom is compensated by the step-size ratio (default).
        /// </summary>
        Physical,
        /// <summary>
        /// Custom: the depth-axis pixel size is specified explicitly via
        /// <see cref="OrthogonalViewController.CustomDepthScale"/>.
        /// </summary>
        Custom,
    }

    /// <summary>
    /// Manages the orthogonal slice views inside an <see cref="OrthogonalPanel"/>.
    /// Call <see cref="Activate"/> to enable the crosshair and slice views for a given axis,
    /// and <see cref="Deactivate"/> to hide them.
    /// </summary>
    /// <remarks>
    /// Slice updates run on a background thread; a pending-update pattern ensures the
    /// final crosshair position is always rendered even under rapid mouse movement.
    /// </remarks>
    public sealed partial class OrthogonalViewController
    {
        private readonly OrthogonalPanel _panel;

        /// <summary>
        /// Controls how the depth axis is scaled in the side views relative to the XY plane.
        /// Changing this triggers a re-sync from the main view.
        /// </summary>
        public OrthoScaleMode ScaleMode
        {
            get => _orthoScaleMode;
            set
            {
                if (_orthoScaleMode == value) return;
                _orthoScaleMode = value;
                if (_data != null) SyncZoomFromMainView();
            }
        }
        private OrthoScaleMode _orthoScaleMode = OrthoScaleMode.Physical;

        /// <summary>
        /// When <see cref="ScaleMode"/> is <see cref="OrthoScaleMode.Custom"/>, specifies the
        /// physical size of one orthogonal-axis pixel (same unit as XStep/YStep).
        /// Setting this triggers a re-sync when Custom mode is active.
        /// </summary>
        public double CustomAxisStep
        {
            get => _orthoCustomAxisStep;
            set
            {
                if (_orthoCustomAxisStep == value) return;
                _orthoCustomAxisStep = value;
                if (_orthoScaleMode == OrthoScaleMode.Custom && _data != null) SyncZoomFromMainView();
            }
        }
        private double _orthoCustomAxisStep = 1.0;

        /// <summary>
        /// Applies an orthogonal-view scale setting for the currently active axis, records it in the
        /// per-axis dictionary, and triggers a view re-sync.
        /// <paramref name="ratio"/> is the user-facing display ratio
        /// (ZCount × d) / (XCount × XStep); ignored when <paramref name="mode"/> is not Custom.
        /// </summary>
        public void ApplyOrthoViewScale(OrthoScaleMode mode, double ratio)
        {
            if (!string.IsNullOrEmpty(_axisName))
                _orthoViewScalePerAxis[_axisName] = (mode, ratio);
            var data = _data;
            if (mode == OrthoScaleMode.Custom && data != null)
            {
                int xCount = data.XCount;
                double xStep = data.XStep;
                int zCount = _panel.BottomView.MatrixData?.YCount ?? 1;
                _orthoCustomAxisStep = (xCount > 0 && xStep > 0 && zCount > 0)
                    ? ratio * xCount * xStep / zCount
                    : ratio;
                System.Diagnostics.Debug.WriteLine(
                    $"[OrthoScale] Apply: axis={_axisName} mode=Custom ratio={ratio} customAxisStep={_orthoCustomAxisStep}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OrthoScale] Apply: axis={_axisName} mode={mode}");
            }
            ScaleMode = mode; // triggers SyncZoomFromMainView via setter
            // If mode was already Custom and only the ratio changed, the setter's equality
            // check returns early without syncing. Force a sync here to cover that case.
            if (mode == OrthoScaleMode.Custom && _data != null)
                SyncZoomFromMainView();
        }

        /// <summary>
        /// Pre-loads a saved orthogonal-view scale setting for <paramref name="axisName"/> from persisted metadata.
        /// Call this before <see cref="Activate"/> so the value is available when the axis is activated.
        /// </summary>
        public void SetSavedOrthoViewScale(string axisName, OrthoScaleMode mode, double ratio)
            => _orthoViewScalePerAxis[axisName] = (mode, ratio);

        /// <summary>
        /// Read-only view of the per-axis orthogonal-view scale settings (axisName → (mode, ratio)).
        /// Used by <c>MatrixPlotter.Settings</c> for persistence.
        /// </summary>
        public System.Collections.Generic.IReadOnlyDictionary<string, (OrthoScaleMode Mode, double Ratio)> OrthoViewScalePerAxis
            => _orthoViewScalePerAxis;

        // Stores per-axis orthogonal-view scale settings: axisName → (mode, user-facing ratio)
        private readonly System.Collections.Generic.Dictionary<string, (OrthoScaleMode Mode, double Ratio)> _orthoViewScalePerAxis = new(StringComparer.OrdinalIgnoreCase);

        private IMatrixData? _data;
        private string       _axisName = string.Empty;

        // Scoped to this controller's lifetime (== its owning MatrixPlotter window's lifetime --
        // one controller is created once in InitializeOrthogonalPanel and never recreated). A
        // background Task.Run (UpdateSlicesAsync, ComputeXYProjectionAsync) started before the
        // window closes keeps running regardless; cancelling this right when the window closes
        // (see CancelPendingWork, called from MatrixPlotter.OnClosed) lets each Task's completion
        // check "does anyone still want this?" once, right before it hands off to
        // Dispatcher.UIThread.Post, instead of every downstream consumer needing its own
        // "is my window still alive" check scattered wherever a continuation might eventually touch
        // window-level state (an actual crash: PositionBesideParent's Screens access threw
        // ObjectDisposedException after a ComputeXYProjectionAsync scan outlived its parent window).
        private readonly CancellationTokenSource _lifetimeCts = new();

        /// <summary>
        /// Cancels this controller's lifetime token, so any background compute already in flight
        /// (see the token checks in <see cref="UpdateSlicesAsync"/> / <see cref="ComputeXYProjectionAsync"/>)
        /// discards its result instead of posting it back to a UI thread continuation that would
        /// touch this controller's (about to be gone) owning window. Call once, from the owning
        /// <c>MatrixPlotter</c>'s <c>OnClosed</c>.
        /// </summary>
        internal void CancelPendingWork() => _lifetimeCts.Cancel();

        private bool _isUpdating;
        private bool _hasPending;
        // Accumulated (OR'd) across every request coalesced into one pending re-run while a Task
        // is in flight -- see UpdateSlicesAsync for why OR-accumulation, not "latest call wins",
        // is required for correctness here.
        private bool _pendingXChanged;
        private bool _pendingYChanged;
        private bool _isSyncing;
        private bool _isExporting;
        // Current/target crosshair position (data-array indices). Despite the name matching
        // CurrentIX/CurrentIY below, this is written *before* the async slice rebuild starts, so
        // during an in-flight update it already holds the next target, not what's currently
        // rendered -- exactly the semantics CurrentIX/CurrentIY's callers (InvokeExtractFrame) want.
        private int  _currentIX = -1;
        private int  _currentIY = -1;

        // Same coalescing shape as _isUpdating/_hasPending above, for ComputeXYProjectionAsync:
        // without it, dragging the ColorCoded histogram's Start/End bars fires one full
        // ExtremumIndexOperation-scanning Task.Run per drag tick, all running concurrently and
        // fighting over CPU cores instead of queueing -- confirmed via trace logging (each scan's
        // own elapsed time balloons from ~800ms to 7s+ as more pile up). While one is in flight,
        // later requests just note that a fresh recompute is needed once it finishes; the shared
        // _xyColorCoded* fields SetColorCodedParams already writes into before calling this method
        // mean the eventual re-run naturally picks up the latest params with no extra state to carry.
        private bool _xyComputeInFlight;
        private bool _xyComputeHasPending;

        private EventHandler<int>? _axisIndicatorDraggedHandlerBottom;
        private EventHandler<int>? _axisIndicatorDraggedHandlerRight;
        private EventHandler?      _axisIndicatorDragEndedHandlerBottom;
        private EventHandler?      _axisIndicatorDragEndedHandlerRight;
        private EventHandler<(double Min, double Max)>? _autoRangeComputedHandler;

        // ── Projection state ──────────────────────────────────────────────────
        private ProjectionMode? _xzProjectionMode;   // null = slice mode
        private ProjectionMode? _yzProjectionMode;   // null = slice mode
        private ProjectionMode? _xyProjectionMode;   // null = no XY projection
        // Whether the current XY projection is ColorCoded (Color(Max)/Color(Min)) rather than a
        // plain intensity projection. _xyProjectionMode still carries which extremum (Maximum/
        // Minimum) even when this is true -- ColorCoded is an orthogonal "also colour it" flag,
        // not a separate mode value (see ColorCoded_View_InitialDesign.md section 3.3.3).
        private bool _xyColorCoded;
        // Start/End sweep range for the ColorCoded scan. Reset to the full axis whenever XY
        // ColorCoded is freshly (re-)enabled; from then on driven by the projection child
        // window's own details panel via SetColorCodedParams (design doc section 3.3.2).
        private int _xyColorCodedStart;
        private int _xyColorCodedEnd;
        // Depth palette (null = default ColorThemes.Spectrum), intensity-range mode (false = Auto,
        // derived from winnerValue.GetValueRange() every recompute; true = Fixed, pinned to
        // _xyColorCodedFixedMin/Max), and Invert -- all driven by the child window's header/details
        // UI the same way Start/End are. See MatrixPlotter.ColorCoded.cs / SetColorCodedParams.
        private LookupTable? _xyColorCodedDepthLut;
        private bool _xyColorCodedRangeFixed;
        private double _xyColorCodedFixedMin;
        private double _xyColorCodedFixedMax;
        private bool _xyColorCodedInvert;
        // The natural (winnerValue.GetValueRange()) range from the most recent recompute -- always
        // captured regardless of Fixed/Auto, so the child window's Auto-mode display can show it
        // and its Search buttons have something to search for even while Fixed is selected. Null
        // until the first ColorCoded recompute completes.
        private double? _xyColorCodedLastAutoMin;
        private double? _xyColorCodedLastAutoMax;
        // The winner-index/depth-palette/resolved-range/invert bundle from the most recent recompute
        // -- pushed to the projection child window as ColorCodedRenderInfo, which
        // ColorCodedBitmapWriter combines at render time with the window's own MatrixData (now the
        // winner *value* matrix directly, an ordinary projection-shaped result) and which the
        // pointer read-out also uses for the depth position. See ColorCodedRenderInfo's doc comment
        // for the reasoning behind this split (and why the earlier packed-ARGB baked-pixel design
        // was abandoned).
        private ColorCodedRenderInfo? _xyColorCodedLastRenderInfo;
        private IMatrixData?    _xzProjectionCache;
        private IMatrixData?    _yzProjectionCache;
        private IMatrixData?    _xyProjectionCache;

        // Snapshot of every axis's position (Dimensions.GetAxisIndices()) as of the last
        // RefreshSlices() call that actually went through, used to detect "did anything besides the
        // frozen/orthogonal axis itself move" -- see RefreshSlices. Null forces the next call to
        // always run: an all-zero array would be indistinguishable from a real position where every
        // axis happens to sit at index 0, so "no snapshot yet" needs its own sentinel. Reset in
        // Activate/Deactivate, since which axis is "the frozen one" (and therefore excluded from the
        // comparison) changes there.
        private int[]? _lastNonOrthoAxisPositions;

        /// <summary>
        /// Incremented whenever a projection cache is invalidated. An async slice/projection run
        /// captures it at entry and only stores its result if the value is unchanged on completion.
        /// Without this an in-flight run that had captured a cache entry *before* an invalidation
        /// writes that stale entry straight back afterwards, resurrecting it - which is exactly what
        /// happened on a Time change in Composite mode, where SetCompositeState kicks off a run just
        /// before RefreshSlices clears the caches.
        /// </summary>
        private int _cacheEpoch;

        /// <summary>
        /// Fired when an XY (Z-direction) projection result is available or cleared.
        /// Carries the projected <see cref="IMatrixData"/> or <c>null</c> when disabled.
        /// </summary>
        public event EventHandler<IMatrixData?>? XYProjectionChanged;

        /// <summary>
        /// Fired when a ColorCoded recompute's background <see cref="Task.Run"/> throws, so a
        /// listener that turned on a "recomputing" indicator around the request (e.g. the projection
        /// child window's busy overlay over its histogram, see <c>MatrixPlotter.SetColorCodedBusy</c>)
        /// gets a chance to turn it back off -- <see cref="XYProjectionChanged"/> is not raised on
        /// this path, so nothing else clears it.
        /// </summary>
        internal event Action? ColorCodedComputeFailed;

        // ── Composite state (Channel-axis blending; see MatrixPlotter.Composite.cs) ──
        private bool _compositeActive;
        private int _compositeAxisDimIndex = -1;
        private int _compositeChannelCount;
        private System.Collections.Generic.IReadOnlyList<Rendering.BlendRecipe>? _compositeRecipes;
        private Rendering.BlendMode _compositeBlendMode = Rendering.BlendMode.Additive;

        /// <summary>
        /// Informs the controller whether Composite mode is active for the given Channel-axis
        /// Dimensions index, and the current recipes/blend mode to use when Composite is on.
        /// Called by <c>MatrixPlotter.Composite.cs</c> on every Enter/Exit and recipe edit.
        /// The actual per-channel slice/projection extraction happens in
        /// <see cref="UpdateSlicesAsync"/> / <see cref="ComputeXYProjectionAsync"/>.
        /// </summary>
        internal void SetCompositeState(
            bool active, int channelAxisDimIndex, int channelCount,
            System.Collections.Generic.IReadOnlyList<Rendering.BlendRecipe>? recipes,
            Rendering.BlendMode blendMode)
        {
            // Composite and ColorCoded are mutually exclusive (design doc: combining them -- e.g.
            // depth-colouring each Composite channel separately -- has no clear meaning and isn't a
            // wanted feature). Restrict the XY combo before anything else below so a nested
            // SelectionChanged (fired only if the XY row was actually showing Color(Max)/(Min))
            // reaches OnProjectionSelectionChanged while every field here still reflects the *old*
            // state -- harmless: the ComputeXYProjectionAsync this method itself triggers further
            // down, once the new state is fully applied, supersedes it via the epoch guard.
            _panel.ProjectionSelector.SetCompositeActive(active);

            // A cached projection was computed for one particular shape: a LUT-mode projection is a
            // single frame, a composite one a merged N-frame set. Toggling composite - or changing
            // the channel count - therefore makes every cache stale. Without dropping them,
            // UpdateSlicesAsync's "both planes projected and cached" fast path returns immediately
            // and the side views keep displaying the projection built in the previous mode.
            bool shapeChanged = _compositeActive != active
                             || _compositeChannelCount != (active ? channelCount : 0);

            _compositeActive = active;
            _compositeAxisDimIndex = active ? channelAxisDimIndex : -1;
            _compositeChannelCount = active ? channelCount : 0;
            _compositeRecipes = active ? recipes : null;
            _compositeBlendMode = blendMode;

            if (shapeChanged)
            {
                _xzProjectionCache = null;
                _yzProjectionCache = null;
                _xyProjectionCache = null;
                _cacheEpoch++;
            }

            // Recipe / blend edits change only how the existing slices are rendered, so they need no
            // recomputation - but the side views still have to receive them. UpdateSlicesAsync
            // applies the composite state only after doing work, and its fast path can skip that
            // entirely, so push it here too.
            ApplyCompositeViewState(_panel.BottomView, _compositeActive, _compositeChannelCount,
                                    _compositeRecipes, _compositeBlendMode);
            ApplyCompositeViewState(_panel.RightView, _compositeActive, _compositeChannelCount,
                                    _compositeRecipes, _compositeBlendMode);

            // Only a shape change (composite toggled on/off, or channel count changed) invalidates
            // the cached slices/projections and requires recomputation. A pure recipe/blend edit
            // (color, min/max, gain) is already fully handled by the synchronous ApplyCompositeViewState
            // calls above - triggering UpdateSlicesAsync here as well would let a stale recipe captured
            // by that in-flight run overwrite the fresh one just pushed (see UpdateSlicesAsync).
            if (shapeChanged && _data != null && _currentIX >= 0 && _currentIY >= 0)
            {
                UpdateSlicesAsync(xChanged: true, yChanged: true);
                if (_xyProjectionMode != null)
                    ComputeXYProjectionAsync();
            }
        }

        /// <summary>
        /// The depth axis name when orthogonal views are active, or <c>null</c> when deactivated.
        /// </summary>
        public string? ActiveAxisName => _data != null ? _axisName : null;

        /// <summary>Current crosshair X index (main data column) used for YZ slice.</summary>
        public int CurrentIX => _currentIX;

        /// <summary>Current crosshair Y index (main data row) used for XZ slice.</summary>
        public int CurrentIY => _currentIY;

        /// <summary>Current XZ projection mode, or <c>null</c> when in slice mode.</summary>
        internal ProjectionMode? XzProjectionMode => _xzProjectionMode;

        /// <summary>Current YZ projection mode, or <c>null</c> when in slice mode.</summary>
        internal ProjectionMode? YzProjectionMode => _yzProjectionMode;

        /// <summary>
        /// Current ColorCoded scan/colorize parameters, for the projection child window to seed its
        /// header/details UI from when it first enters ColorCoded mode (<see cref="MatrixPlotter.EnterColorCodedProjectionMode"/>).
        /// </summary>
        internal (int Start, int End, LookupTable? DepthLut, bool RangeFixed, double FixedMin, double FixedMax, bool Invert) ColorCodedParams
            => (_xyColorCodedStart, _xyColorCodedEnd, _xyColorCodedDepthLut,
                _xyColorCodedRangeFixed, _xyColorCodedFixedMin, _xyColorCodedFixedMax, _xyColorCodedInvert);

        /// <summary>The frozen axis's frame count, i.e. the valid Start/End upper bound. 1 when inactive.</summary>
        internal int ActiveAxisCount => _data?.Dimensions[_axisName]?.Count ?? 1;

        /// <summary>
        /// The natural (Auto) intensity range from the most recently completed ColorCoded
        /// recompute -- <c>winnerValue.GetValueRange()</c>, captured regardless of whether Fixed or
        /// Auto is actually selected. <c>null</c> until the first recompute completes. The child
        /// window uses this to keep its Auto-mode display current and to seed its Search buttons
        /// while in Fixed mode (<see cref="MatrixPlotter.UpdateColorCodedAutoRange"/>).
        /// </summary>
        internal (double? Min, double? Max) LastColorCodedAutoRange => (_xyColorCodedLastAutoMin, _xyColorCodedLastAutoMax);

        /// <summary>
        /// The winner-index/depth-palette/resolved-range/invert bundle from the most recently
        /// completed ColorCoded recompute, for the child window to push into its
        /// <c>MxView.ColorCodedInfo</c> (<see cref="MatrixPlotter.UpdateColorCodedInfo"/>) -- drives
        /// both <see cref="ColorCodedBitmapWriter"/>'s rendering and the pointer read-out's depth
        /// position. <c>null</c> until the first recompute completes.
        /// </summary>
        internal ColorCodedRenderInfo? LastColorCodedRenderInfo => _xyColorCodedLastRenderInfo;

        /// <summary>
        /// Updates the ColorCoded scan/colorize parameters from the projection child window's own
        /// header/details UI (<see cref="MatrixPlotter.ColorCodedParamsChanged"/>) and recomputes if
        /// ColorCoded is currently active. The scan (<see cref="ExtremumIndexOperation"/>) runs here,
        /// against the source data this controller owns -- the winner value result and the
        /// winner-index/depth-palette/range/invert bundle needed to colour it are both pushed back
        /// down through <see cref="XYProjectionChanged"/> -> <c>OnXYProjectionChanged</c> ->
        /// <c>UpdateProjectionData</c>/<c>UpdateColorCodedInfo</c>.
        /// </summary>
        internal void SetColorCodedParams(int start, int end, LookupTable? depthLut,
            bool rangeFixed, double fixedMin, double fixedMax, bool invert)
        {
            _xyColorCodedStart = start;
            _xyColorCodedEnd = end;
            _xyColorCodedDepthLut = depthLut;
            _xyColorCodedRangeFixed = rangeFixed;
            _xyColorCodedFixedMin = fixedMin;
            _xyColorCodedFixedMax = fixedMax;
            _xyColorCodedInvert = invert;
            _xyProjectionCache = null;
            _cacheEpoch++;
            if (_xyColorCoded) ComputeXYProjectionAsync();
        }

        /// <summary>
        /// Rebuilds the side-view slices/projections for the given crosshair position and
        /// returns a <see cref="Task"/> that completes on the UI thread once both views have
        /// been updated. Safe to await from an export loop running on the UI thread.
        /// </summary>
        internal void BeginExport() => _isExporting = true;

        internal void EndExport()
        {
            _isExporting = false;
            // Invalidate caches that may have been left in an inconsistent state by the
            // export (the restored ActiveIndex differs from the last exported frame).
            // Then trigger a normal refresh so side views and caches are correct.
            _xzProjectionCache = null;
            _yzProjectionCache = null;
            _xyProjectionCache = null;
            _cacheEpoch++;
            if (_data != null && _currentIX >= 0 && _currentIY >= 0)
            {
                UpdateSlicesAsync(xChanged: true, yChanged: true);
                if (_xyProjectionMode != null)
                    ComputeXYProjectionAsync();
            }
        }

        internal Task RebuildSlicesForExportAsync(int ix, int iy)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var data = _data;
            var axisName = _axisName;
            if (data == null) { tcs.SetResult(true); return tcs.Task; }

            var xzProjMode = _xzProjectionMode;
            var yzProjMode = _yzProjectionMode;
            // Never use cached projections during export: each frame may come from a different
            // ActiveIndex (time/channel), so the projection must be recomputed every frame.
            // We also avoid writing back to _xzProjectionCache/_yzProjectionCache so that
            // the normal UI cache is not poisoned with export-time data.

            Task.Run(() =>
            {
                try
                {
                    IMatrixData? xz = null;
                    IMatrixData? yz = null;

                    if (xzProjMode.HasValue)
                        xz = data.Apply(new ProjectionOperation(ViewFrom.Y, xzProjMode.Value, axisName));
                    if (yzProjMode.HasValue)
                        yz = data.Apply(new ProjectionOperation(ViewFrom.X, yzProjMode.Value, axisName));

                    if (xz == null && yz == null)
                    {
                        var slices = data.Apply(new SliceOrthogonalOperation(ix, iy, axisName));
                        xz = slices.XZ;
                        yz = slices.YZ;
                    }
                    else if (xz == null)
                        xz = data.Apply(new SliceOperation(ViewFrom.Y, iy, axisName));
                    else if (yz == null)
                        yz = data.Apply(new SliceOperation(ViewFrom.X, ix, axisName));

                    Dispatcher.UIThread.Post(() =>
                    {
                        _panel.BottomView.SetMatrixDataInternal(xz);
                        _panel.RightView.SetMatrixDataInternal(yz);
                        tcs.TrySetResult(true);
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() => tcs.TrySetException(ex));
                }
            });

            return tcs.Task;
        }

        public OrthogonalViewController(OrthogonalPanel panel)
        {
            _panel = panel;
        }

        /// <summary>Activates orthogonal views for the specified data and axis name.</summary>
        public void Activate(IMatrixData data, string axisName)
        {
            _data     = data;
            _axisName = axisName;
            // Which axis is "the frozen one" just changed (or this is a fresh activation), so any
            // previous non-frozen-axis-position snapshot no longer applies -- see RefreshSlices.
            _lastNonOrthoAxisPositions = null;

            // Restore per-axis orthogonal-view scale setting if one was previously saved.
            if (_orthoViewScalePerAxis.TryGetValue(axisName, out var saved))
            {
                _orthoScaleMode = saved.Mode;
                if (saved.Mode == OrthoScaleMode.Custom)
                {
                    int xCount = data.XCount;
                    double xStep = data.XStep;
                    // ZCount = depth pixel count; look up the exact axis being frozen, not Axes[0]
                    var frozenAxis = data.Axes.FindAxis(axisName);
                    int zCount = frozenAxis?.Count ?? 1;
                    _orthoCustomAxisStep = (xCount > 0 && xStep > 0 && zCount > 0)
                        ? saved.Ratio * xCount * xStep / zCount
                        : saved.Ratio;
                    System.Diagnostics.Debug.WriteLine(
                        $"[OrthoScale] Activate: axis={axisName} mode=Custom savedRatio={saved.Ratio} xCount={xCount} xStep={xStep} zCount={zCount} customAxisStep={_orthoCustomAxisStep}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[OrthoScale] Activate: axis={axisName} mode={saved.Mode} (no custom step)");
                }
            }
            else
            {
                _orthoScaleMode = OrthoScaleMode.Physical;
                _orthoCustomAxisStep = 1.0;
                System.Diagnostics.Debug.WriteLine(
                    $"[OrthoScale] Activate: axis={axisName} no saved scale → default Physical");
            }

            // Ensure single subscription
            _panel.MainView.CrosshairMoved -= OnCrosshairMoved;
            _panel.MainView.CrosshairMoved += OnCrosshairMoved;
            _panel.MainView.ScrollStateChanged -= OnMainScrollStateChanged;
            _panel.MainView.ScrollStateChanged += OnMainScrollStateChanged;
            _panel.BottomView.ScrollStateChanged -= OnBottomScrollStateChanged;
            _panel.BottomView.ScrollStateChanged += OnBottomScrollStateChanged;
            _panel.RightView.ScrollStateChanged -= OnRightScrollStateChanged;
            _panel.RightView.ScrollStateChanged += OnRightScrollStateChanged;
            _panel.MainView.AutoRangeComputed -= _autoRangeComputedHandler;
            _autoRangeComputedHandler = (_, range) => OnMainAutoRangeComputed(range);
            _panel.MainView.AutoRangeComputed += _autoRangeComputedHandler;

            // Ensure single subscription for AxisIndicator drag
            _panel.BottomView.AxisIndicatorDragged -= _axisIndicatorDraggedHandlerBottom;
            _panel.RightView.AxisIndicatorDragged  -= _axisIndicatorDraggedHandlerRight;
            _axisIndicatorDraggedHandlerBottom = (_, newIdx) => OnAxisIndicatorDragged(newIdx);
            _axisIndicatorDraggedHandlerRight  = (_, newIdx) => OnAxisIndicatorDragged(newIdx);
            _panel.BottomView.AxisIndicatorDragged += _axisIndicatorDraggedHandlerBottom;
            _panel.RightView.AxisIndicatorDragged  += _axisIndicatorDraggedHandlerRight;

            _panel.BottomView.AxisIndicatorDragEnded -= _axisIndicatorDragEndedHandlerBottom;
            _panel.RightView.AxisIndicatorDragEnded  -= _axisIndicatorDragEndedHandlerRight;
            _axisIndicatorDragEndedHandlerBottom = (_, _) => OnAxisIndicatorDragEnded();
            _axisIndicatorDragEndedHandlerRight  = (_, _) => OnAxisIndicatorDragEnded();
            _panel.BottomView.AxisIndicatorDragEnded += _axisIndicatorDragEndedHandlerBottom;
            _panel.RightView.AxisIndicatorDragEnded  += _axisIndicatorDragEndedHandlerRight;

            SyncRenderSettings();
            _panel.ProjectionSelector.UpdateAxisName(axisName);
            // Projecting a single-axis volume down to one frame is still useful, so this is not
            // gated on hyperstacks - only on an orthogonal axis being active at all.
            _panel.ProjectionSelector.UpdateProjectedDataAvailability(true);

            // Disable projection for Complex data (projection requires IMinMaxValue<T> constraint)
            bool supportsProjection = data.ValueType != typeof(System.Numerics.Complex);
            _panel.ProjectionSelector.IsEnabled = supportsProjection;
            if (!supportsProjection)
            {
                global::Avalonia.Controls.ToolTip.SetTip(_panel.ProjectionSelector,
                    "Projection is not available for Complex data.\n" +
                    "Use slice mode (uncheck all projection options) to view orthogonal views.");
            }
            else
            {
                global::Avalonia.Controls.ToolTip.SetTip(_panel.ProjectionSelector, null);
            }

            _panel.ProjectionSelector.SelectionChanged -= OnProjectionSelectionChanged;
            _panel.ProjectionSelector.SelectionChanged += OnProjectionSelectionChanged;

            _panel.MainView.ShowCrosshair = true;
            ApplyCrosshairVisibility();
            _panel.ShowRight  = true;
            _panel.ShowBottom = true;

            // Initial crosshair at the data center
            double cx = data.XMin + data.XStep * (data.XCount / 2.0);
            double cy = data.YMin + data.YStep * (data.YCount / 2.0);
            var    cp = new Point(cx, cy);
            _panel.MainView.SetCrosshairDataPosition(cp);
            UpdateFrameIndicator();

            // Force OnCrosshairMoved below to rebuild both side views even if the computed ix/iy
            // happen to equal _currentIX/_currentIY from a previous Activate() call (e.g.
            // re-freezing at the same data-center position after switching the frozen axis, or
            // unfreeze-then-refreeze without moving the crosshair in between) -- xChanged/yChanged
            // there is a pure position diff meant only for genuine crosshair drags (see
            // UpdateSlicesAsync's doc comment: "callers that changed something other than
            // position... must pass true for both"), and can't detect "the axis being sliced
            // changed" on its own since Activate() always resets to the same centered position.
            // -1 is this class's existing "no valid position yet" sentinel (see _currentIX's
            // field initializer and its CurrentIX >= 0 guard elsewhere), so it's guaranteed to
            // differ from any real (always >= 0) computed index.
            _currentIX = -1;
            _currentIY = -1;
            OnCrosshairMoved(this, cp);
        }

        /// <summary>Deactivates orthogonal views and hides the crosshair.</summary>
        public void Deactivate()
        {
            System.Diagnostics.Debug.WriteLine(
                $"[OrthoScale] Deactivate: axis={_axisName} mode={_orthoScaleMode}" +
                (_orthoScaleMode == OrthoScaleMode.Custom ? $" customAxisStep={_orthoCustomAxisStep}" : ""));
            _panel.MainView.CrosshairMoved     -= OnCrosshairMoved;
            _panel.MainView.ScrollStateChanged -= OnMainScrollStateChanged;
            _panel.BottomView.ScrollStateChanged -= OnBottomScrollStateChanged;
            _panel.RightView.ScrollStateChanged  -= OnRightScrollStateChanged;
            _panel.MainView.AutoRangeComputed  -= _autoRangeComputedHandler;
            _autoRangeComputedHandler = null;
            _panel.BottomView.AxisIndicatorDragged -= _axisIndicatorDraggedHandlerBottom;
            _panel.RightView.AxisIndicatorDragged  -= _axisIndicatorDraggedHandlerRight;
            _axisIndicatorDraggedHandlerBottom = null;
            _axisIndicatorDraggedHandlerRight  = null;
            _panel.BottomView.AxisIndicatorDragEnded -= _axisIndicatorDragEndedHandlerBottom;
            _panel.RightView.AxisIndicatorDragEnded  -= _axisIndicatorDragEndedHandlerRight;
            _axisIndicatorDragEndedHandlerBottom = null;
            _axisIndicatorDragEndedHandlerRight  = null;
            _panel.ProjectionSelector.SelectionChanged -= OnProjectionSelectionChanged;
            _panel.ProjectionSelector.UpdateProjectedDataAvailability(false);
            _panel.MainView.ShowCrosshair = false;
            _panel.MainView.AxisIndicatorPx = null;
            _panel.MainView.AxisIndicatorLabel = null;
            _panel.ShowRight  = false;
            _panel.ShowBottom = false;
            _panel.RightView.SetMatrixDataInternal(null);
            _panel.BottomView.SetMatrixDataInternal(null);
            _xzProjectionCache = null;
            _yzProjectionCache = null;
            _cacheEpoch++;
            bool hadXYProjection = _xyProjectionMode != null;
            _xyProjectionMode  = null;
            _xyColorCoded      = false;
            _xyProjectionCache = null;
            _data     = null;
            _axisName = string.Empty;
            _lastNonOrthoAxisPositions = null;

            if (hadXYProjection)
                XYProjectionChanged?.Invoke(this, null);
        }

        /// <summary>
        /// Syncs all render settings (LUT, depth, inversion, value range) from the
        /// main view to the side views. Safe to call even when the side views are inactive.
        /// </summary>
        public void SyncRenderSettings()
        {
            var main  = _panel.MainView;
            var right = _panel.RightView;
            var bot   = _panel.BottomView;
            right.Lut  = main.Lut;
            bot.Lut    = main.Lut;
            right.LutDepth  = main.LutDepth;
            bot.LutDepth    = main.LutDepth;
            right.IsInvertedColor  = main.IsInvertedColor;
            bot.IsInvertedColor    = main.IsInvertedColor;
            right.ComplexValueMode  = main.ComplexValueMode;
            bot.ComplexValueMode    = main.ComplexValueMode;

            // Side views always use fixed range so they display the same value scale as MainView.
            double min, max;
            if (main.IsFixedRange)
            {
                min = main.FixedMin;
                max = main.FixedMax;
            }
            else
            {
                // For Complex data: scan using the current display mode
                int valueMode = main.MatrixData?.ValueType == typeof(System.Numerics.Complex)
                    ? (int)main.ComplexValueMode : 0;
                (min, max) = main.ScanCurrentFrameRange(valueMode);
            }
            right.IsFixedRange = true;
            bot.IsFixedRange   = true;
            right.FixedMin  = min;
            bot.FixedMin    = min;
            right.FixedMax  = max;
            bot.FixedMax    = max;

            // When MainView is Complex and ComplexValueMode changed, side views need explicit refresh.
            // (Side views' MatrixData is usually float/ushort slice, so ComplexValueMode setter doesn't trigger rebuild.)
            if (main.MatrixData?.ValueType == typeof(System.Numerics.Complex))
            {
                if (_panel.ShowBottom) bot.Refresh();
                if (_panel.ShowRight) right.Refresh();
            }
        }

        /// <inheritdoc cref="SyncRenderSettings"/>
        [Obsolete("Use SyncRenderSettings() instead.")]
        public void SyncLut() => SyncRenderSettings();

        /// <summary>
        /// Updates the axis indicator line in side views to reflect the current depth frame.
        /// Looks up the frozen axis by name and uses its <see cref="Axis.Index"/> directly,
        /// so the indicator correctly tracks the Z-axis frame (not the global ActiveIndex
        /// which is a linearised index across all axes).
        /// Call whenever the active frame changes.
        /// </summary>
        public void UpdateFrameIndicator()
        {
            int idx = 0;
            string label = "";
            if (_data != null)
            {
                var axis = _data.Axes.FindAxis(_axisName);
                if (axis != null)
                {
                    idx = axis.Index;
                    string unit = axis.Unit.Length > 0 ? $" {axis.Unit}" : "";
                    label = $"{axis.Name}={axis.ValueAt(idx):G4}{unit}";
                }
            }
            _panel.BottomView.AxisIndicatorPx    = idx;
            _panel.RightView.AxisIndicatorPx     = idx;
            _panel.BottomView.AxisIndicatorLabel = label;
            _panel.RightView.AxisIndicatorLabel  = label;
            bool isDragging = _panel.BottomView.IsAxisIndicatorDragging
                           || _panel.RightView.IsAxisIndicatorDragging;
            if (!isDragging)
            {
                _panel.BottomView.ScrollToAxisIndicator();
                _panel.RightView.ScrollToAxisIndicator();
            }
        }

        /// <summary>
        /// Re-runs the slice update at the last known crosshair position, always recomputing.
        /// Call after the underlying pixel data itself may have changed in place -- e.g.
        /// <c>MatrixPlotter.Refresh(rebuildOrthogonalData: true)</c> after a filter/edit -- where
        /// axis positions alone can't tell us whether the visible slice actually needs rebuilding,
        /// so this always does the work. For a pure axis-navigation trigger (every
        /// <see cref="Core.IMatrixData.ActiveIndexChanged"/>), use
        /// <see cref="RefreshSlicesIfAxisChanged"/> instead, which can skip redundant work.
        /// No-op if the orthogonal view is not active or no crosshair position has been set.
        /// </summary>
        public void RefreshSlices()
        {
            if (_isExporting) return;
            if (_data == null || _currentIX < 0 || _currentIY < 0) return;
            // Keep the baseline current so a later RefreshSlicesIfAxisChanged call isn't judged
            // against a stale snapshot that predates this (possibly content-only) refresh.
            _lastNonOrthoAxisPositions = _data.Dimensions.GetAxisIndices();
            DoRefreshSlices();
        }

        /// <summary>
        /// Same as <see cref="RefreshSlices"/>, but skips the recompute when the only axis whose
        /// index changed since the last call is the frozen/orthogonal axis itself
        /// (<see cref="_axisName"/>) -- moving along it doesn't change what XZ/YZ (slice or
        /// projection mode) or the XY-projection window show, since those already span its entire
        /// range by construction. Call this, not <see cref="RefreshSlices"/>, from
        /// <see cref="Core.IMatrixData.ActiveIndexChanged"/>, which fires uniformly for every axis --
        /// the frozen axis's own <see cref="AxisTracker"/> slider and the side views' draggable
        /// depth indicator included, both of which just set the same <see cref="Axis.Index"/>.
        /// </summary>
        public void RefreshSlicesIfAxisChanged()
        {
            if (_isExporting) return;
            if (_data == null || _currentIX < 0 || _currentIY < 0) return;

            // _lastNonOrthoAxisPositions == null means "no baseline yet" (first call after
            // Activate, or after a fresh RefreshSlices reset it) -- must always fall through and run.
            int[] currentPositions = _data.Dimensions.GetAxisIndices();
            if (_lastNonOrthoAxisPositions != null
                && OnlyFrozenAxisDiffers(currentPositions, _lastNonOrthoAxisPositions))
            {
                _lastNonOrthoAxisPositions = currentPositions;
                return;
            }
            _lastNonOrthoAxisPositions = currentPositions;
            DoRefreshSlices();
        }

        private void DoRefreshSlices()
        {
            // Projection caches depend on frame data; invalidate on any refresh
            _xzProjectionCache = null;
            _yzProjectionCache = null;
            _xyProjectionCache = null;
            _cacheEpoch++;
            UpdateSlicesAsync(xChanged: true, yChanged: true);
            if (_xyProjectionMode != null)
                ComputeXYProjectionAsync();
        }

        /// <summary>
        /// True when <paramref name="a"/> and <paramref name="b"/> agree on every axis except the
        /// currently frozen/orthogonal one (<see cref="_axisName"/>) -- the equality that makes
        /// <see cref="RefreshSlicesIfAxisChanged"/>'s skip valid. Mirrors
        /// <see cref="Core.IO.CacheStrategies.DimensionStrategy.ContextMatchesExceptTargetAxis"/>'s
        /// same shape for the same reason (Volume mode's preload target set is likewise independent
        /// of the target axis's own position), just applied here to "does this recompute need to run"
        /// instead of "is this memoized target set still valid".
        /// </summary>
        private bool OnlyFrozenAxisDiffers(int[] a, int[] b)
        {
            int frozenIdx = _data!.Dimensions.GetAxisOrder(_axisName);
            for (int i = 0; i < a.Length; i++)
            {
                if (i == frozenIdx) continue;
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Re-anchors the crosshair to the current data scale after XY or axis scale changes.
        /// Clamps the stored pixel indices to the current data dimensions, recomputes the
        /// data-space crosshair position, refreshes the axis indicator labels on the side views,
        /// and refreshes the orthogonal slices.
        /// Has no effect if the orthogonal view is not active or no crosshair position has been set.
        /// </summary>
        public void RefreshCrosshairAndSlices()
        {
            var md = _data;
            if (md == null || _currentIX < 0 || _currentIY < 0) return;

            int ix = Math.Clamp(_currentIX, 0, md.XCount - 1);
            int iy = Math.Clamp(_currentIY, 0, md.YCount - 1);

            if (md.XStep != 0 && md.YStep != 0)
            {
                double cx = md.XMin + (ix + 0.5) * md.XStep;
                // iy is a data array index (0=YMin). Convert back to snap-convention coordinate.
                double cy = md.YMax - (md.YCount - 1 - iy + 0.5) * md.YStep;
                _panel.MainView.SetCrosshairDataPosition(new Point(cx, cy));
            }

            _currentIX = ix;
            _currentIY = iy;
            UpdateFrameIndicator();
            UpdateSlicesAsync(xChanged: true, yChanged: true);
        }

        private void OnMainScrollStateChanged(object? sender, EventArgs e) => SyncZoomFromMainView();
        private void OnBottomScrollStateChanged(object? sender, EventArgs e) => SyncZoomFromSideView(isBottom: true);
        private void OnRightScrollStateChanged(object? sender, EventArgs e)  => SyncZoomFromSideView(isBottom: false);

        private void OnMainAutoRangeComputed((double Min, double Max) range)
        {
            _panel.RightView.FixedMin  = range.Min;
            _panel.RightView.FixedMax  = range.Max;
            _panel.BottomView.FixedMin = range.Min;
            _panel.BottomView.FixedMax = range.Max;
        }

        /// <summary>
        /// Propagates MainView zoom and axis-aligned translation to the side views.
        /// BottomView shares the X axis → sync TransX.
        /// RightView (Transposed) shares the Y axis → sync TransY.
        /// When aspect correction is active, the zoom is compensated so that shared
        /// physical axes have the same pixels-per-unit in all three views.
        /// After syncing zoom, forces the Z-axis AxisIndicator to the centre of each
        /// side view so the user always sees the current depth frame after zooming.
        /// </summary>
        private void SyncZoomFromMainView()
        {
            if (_isSyncing) return;
            _isSyncing = true;
            try
            {
                SyncSidesFromMain();
                // Issues 2&3: after XY zoom, always centre the Z-axis indicator in the side views.
                // ApplyZoomAndTrans (called inside SyncSidesFromMain) does not fire ScrollStateChanged,
                // so ScrollToAxisIndicator(forceCenter: true) runs without triggering a sync loop.
                _panel.BottomView.ScrollToAxisIndicator(forceCenter: true);
                _panel.RightView.ScrollToAxisIndicator(forceCenter: true);
            }
            finally { _isSyncing = false; }
        }

        /// <summary>
        /// Inner sync body: propagates MainView zoom and translation to the side views.
        /// Must be called with <see cref="_isSyncing"/> already set to prevent re-entrancy.
        /// </summary>
        private void SyncSidesFromMain()
        {
            double zoom = _panel.MainView.Zoom;
            double tx   = _panel.MainView.RawTransX;
            double ty   = _panel.MainView.RawTransY;
            var (axMain, ayMain) = _panel.MainView.GetAspectScales();

            if (_orthoScaleMode == OrthoScaleMode.Custom)
            {
                // Custom mode: override the orthogonal-axis step on the side views so that
                // GetAspectScales() returns the desired ratio, then apply the same Physical
                // zoom formula. This keeps the shared X/Y axis pixel size identical to
                // MainView while only adjusting the orthogonal axis direction.
                double d = _orthoCustomAxisStep > 0 ? _orthoCustomAxisStep : 1;
                _panel.BottomView.OrthoAxisStepOverride = d;
                _panel.RightView.OrthoAxisStepOverride  = d;

                var (axBottom, _) = _panel.BottomView.GetAspectScales();
                double zoomBottom = axBottom > 0 ? zoom * axMain / axBottom : zoom;
                _panel.BottomView.ApplyZoomAndTrans(zoomBottom, tx, _panel.BottomView.RawTransY);

                var (axRight, _) = _panel.RightView.GetAspectScales();
                double zoomRight = axRight > 0 ? zoom * ayMain / axRight : zoom;
                _panel.RightView.ApplyZoomAndTrans(zoomRight, _panel.RightView.RawTransX, ty);
                return;
            }

            // Physical mode (default): compensate for step-size ratio so 1 physical unit
            // occupies the same screen distance in all three views.
            _panel.BottomView.OrthoAxisStepOverride = null;
            _panel.RightView.OrthoAxisStepOverride  = null;

            // BottomView (XZ): X-axis shared with MainView.
            var (axBottomP, _) = _panel.BottomView.GetAspectScales();
            double zoomBottomP = axBottomP > 0 ? zoom * axMain / axBottomP : zoom;
            _panel.BottomView.ApplyZoomAndTrans(zoomBottomP, tx, _panel.BottomView.RawTransY);

            // RightView (YZ, Rotate90CCW): screen-height direction = Y axis, shared with MainView.
            var (axRightP, _) = _panel.RightView.GetAspectScales();
            double zoomRightP = axRightP > 0 ? zoom * ayMain / axRightP : zoom;
            _panel.RightView.ApplyZoomAndTrans(zoomRightP, _panel.RightView.RawTransX, ty);
        }

        /// <summary>
        /// Propagates a side view's zoom/translation back to MainView, then forwards to the other side view.
        /// <list type="bullet">
        ///   <item>Bottom → Main → Right: BottomView shares the X axis with MainView.</item>
        ///   <item>Right → Main → Bottom: RightView shares the Y axis with MainView.</item>
        /// </list>
        /// <para>
        /// Issue 1 fix: distinguishes a user mouse-wheel zoom from a mere viewport-resize event by
        /// comparing the side view's actual zoom against the zoom that <see cref="SyncSidesFromMain"/>
        /// would have produced from the current MainView state.  Only when the two differ is it treated
        /// as a genuine user zoom; otherwise the current main-view state is simply forwarded to the side
        /// views (preserving <c>IsFitToView</c> on MainView).
        /// </para>
        /// <para>
        /// Issue 4 fix: when a genuine user zoom is detected, the non-shared axis translation of
        /// MainView is adjusted so that the CrossHair position is kept near the centre of the viewport.
        /// </para>
        /// </summary>
        private void SyncZoomFromSideView(bool isBottom)
        {
            if (_isSyncing) return;
            _isSyncing = true;
            try
            {
                var main = _panel.MainView;
                var (axMain, ayMain) = main.GetAspectScales();

                if (isBottom)
                {
                    var bot = _panel.BottomView;
                    var (axBottom, _) = bot.GetAspectScales();

                    // Issue 1: detect whether the scroll event is caused by a user zoom or a resize.
                    double expectedBotZoom = axBottom > 0 ? main.Zoom * axMain / axBottom : main.Zoom;
                    bool isUserZoom = Math.Abs(bot.Zoom - expectedBotZoom) > 1e-9;
                    if (main.IsFitToView && !isUserZoom)
                    {
                        SyncSidesFromMain();
                        return;
                    }

                    // BottomView → MainView: recover main zoom (inverse of SyncSidesFromMain formula).
                    double zoomMain = axMain > 0 && axBottom > 0 ? bot.Zoom * axBottom / axMain : bot.Zoom;

                    // Issue 4: adjust MainView's Y translation so the CrossHair row stays centred.
                    double targetTransY = main.RawTransY;
                    if (_data != null && _currentIY >= 0 && main.Bounds.Height > 0)
                    {
                        // _currentIY is a data-array row index (0 = YMin).
                        // MainView uses FlipY=true: bmpRow 0 = top of image = YMax.
                        int bmpRow = _data.YCount - 1 - _currentIY;
                        targetTransY = main.Bounds.Height / 2.0 - (bmpRow + 0.5) * zoomMain * ayMain;
                    }
                    main.ApplyZoomAndTrans(zoomMain, bot.RawTransX, targetTransY);

                    // MainView → RightView: propagate (Y axis shared).
                    {
                        var (axRight, _) = _panel.RightView.GetAspectScales();
                        double zoomRight = axRight > 0 ? zoomMain * ayMain / axRight : zoomMain;
                        _panel.RightView.ApplyZoomAndTrans(zoomRight, _panel.RightView.RawTransX, main.RawTransY);
                    }
                }
                else
                {
                    var right = _panel.RightView;
                    var (axRight, _) = right.GetAspectScales();

                    // Issue 1: same resize-vs-user-zoom detection for RightView.
                    double expectedRightZoom = axRight > 0 ? main.Zoom * ayMain / axRight : main.Zoom;
                    bool isUserZoom = Math.Abs(right.Zoom - expectedRightZoom) > 1e-9;
                    if (main.IsFitToView && !isUserZoom)
                    {
                        SyncSidesFromMain();
                        return;
                    }

                    // RightView → MainView: recover main zoom (inverse of SyncSidesFromMain formula).
                    double zoomMain = ayMain > 0 && axRight > 0 ? right.Zoom * axRight / ayMain : right.Zoom;

                    // Issue 4: adjust MainView's X translation so the CrossHair column stays centred.
                    double targetTransX = main.RawTransX;
                    if (_data != null && _currentIX >= 0 && main.Bounds.Width > 0)
                    {
                        targetTransX = main.Bounds.Width / 2.0 - (_currentIX + 0.5) * zoomMain * axMain;
                    }
                    main.ApplyZoomAndTrans(zoomMain, targetTransX, right.RawTransY);

                    // MainView → BottomView: propagate (X axis shared).
                    {
                        var (axBottom, _) = _panel.BottomView.GetAspectScales();
                        double zoomBottom = axBottom > 0 ? zoomMain * axMain / axBottom : zoomMain;
                        _panel.BottomView.ApplyZoomAndTrans(zoomBottom, main.RawTransX, _panel.BottomView.RawTransY);
                    }
                }
            }
            finally { _isSyncing = false; }
        }

        private void OnAxisIndicatorDragged(int newIdx)
        {
            var data = _data;
            if (data == null) return;
            var axis = data.Axes.FindAxis(_axisName);
            if (axis == null) return;
            int clamped = Math.Clamp(newIdx, 0, axis.Count - 1);
            if (axis.Index != clamped)
                axis.Index = clamped;
        }

        private void OnAxisIndicatorDragEnded()
        {
            _panel.BottomView.ScrollToAxisIndicator();
            _panel.RightView.ScrollToAxisIndicator();
        }

        private void OnCrosshairMoved(object? sender, Point dataPos)
        {
            var md = _data;
            if (md == null) return;

            int ix, iy;
            if (md.XStep != 0 && md.YStep != 0)
            {
                ix = Math.Clamp((int)Math.Floor((dataPos.X - md.XMin) / md.XStep), 0, md.XCount - 1);
                // Convert data-space Y to array row index (row 0 = YMin, row YCount-1 = YMax)
                iy = Math.Clamp(md.YCount - 1 - (int)Math.Floor((md.YMax - dataPos.Y) / md.YStep), 0, md.YCount - 1);
            }
            else
            {
                ix = Math.Clamp((int)Math.Floor(dataPos.X), 0, md.XCount - 1);
                iy = Math.Clamp((int)Math.Floor(dataPos.Y), 0, md.YCount - 1);
            }

            bool xChanged = ix != _currentIX;
            bool yChanged = iy != _currentIY;
            _currentIX = ix;
            _currentIY = iy;
            UpdateSlicesAsync(xChanged, yChanged);
        }

        /// <summary>
        /// Rebuilds whichever of the XZ/YZ side views actually needs it, using
        /// <see cref="_currentIX"/>/<see cref="_currentIY"/> as the target position.
        /// </summary>
        /// <param name="xChanged">
        /// Whether X changed since the view currently on screen was built -- if not, and YZ isn't
        /// waiting on a fresh projection, YZ (which slices at X) is left untouched: XZ is a
        /// Y-constant plane, so moving only along X can never change it, and rebuilding it anyway
        /// was the redundant recompute this parameter exists to avoid. Callers that changed
        /// something other than position (composite shape, projection mode, data itself) must pass
        /// <see langword="true"/> for both -- the position-based skip only applies to pure
        /// crosshair moves.
        /// </param>
        /// <param name="yChanged">Same as <paramref name="xChanged"/>, but for Y / the XZ view.</param>
        private void UpdateSlicesAsync(bool xChanged, bool yChanged)
        {
            if (_isUpdating)
            {
                // Multiple requests can collide here while one Task is in flight (e.g. two rapid
                // crosshair moves). OR-accumulate rather than overwrite: if hop A→B left X unchanged
                // but B→C changed it, the merged request (A→C) must still rebuild YZ. Transitivity
                // guarantees this is exact, not just a safe over-approximation: A.x != C.x if and
                // only if at least one of the coalesced hops changed X. The same accumulation also
                // makes it safe for a "force everything" call (composite/projection/data change) to
                // collide with a pending pure position move -- the merged flags end up true either way.
                _hasPending = true;
                _pendingXChanged |= xChanged;
                _pendingYChanged |= yChanged;
                return;
            }

            var data     = _data;
            var axisName = _axisName;
            if (data == null) return;
            int ix = _currentIX;
            int iy = _currentIY;

            // Capture current projection state for the background thread
            var xzProjMode = _xzProjectionMode;
            var yzProjMode = _yzProjectionMode;
            var xzCache    = _xzProjectionCache;
            var yzCache    = _yzProjectionCache;
            int epoch      = _cacheEpoch;

            // Capture Composite shape state for the background thread (set via SetCompositeState;
            // see MatrixPlotter.Composite.cs). When inactive this whole block is a no-op and
            // the branches below fall through to the original single-channel code path.
            // Recipes/blend mode are intentionally NOT captured here - they are read live
            // (_compositeRecipes/_compositeBlendMode) when applied back on the UI thread below,
            // so a recipe edit that lands while this run is in flight is never regressed.
            bool compositeActive = _compositeActive;
            int compositeAxisDimIndex = _compositeAxisDimIndex;
            int compositeChannelCount = _compositeChannelCount;

            // Fast path: nothing needs rebuilding, either because both views are in projection
            // mode with valid caches, or because the axis a slice-mode view depends on didn't move.
            // XZ slices at Y (ViewFrom.Y) so it only needs yChanged; YZ slices at X so it only needs
            // xChanged.
            bool xzNeedsWork = xzProjMode.HasValue ? xzCache == null : yChanged;
            bool yzNeedsWork = yzProjMode.HasValue ? yzCache == null : xChanged;
            if (!xzNeedsWork && !yzNeedsWork)
            {
                // May be reached from the pending re-entry after the previous run
                // cached both projections — ensure the spinners are cleared.
                _panel.BottomView.IsBusy = false;
                _panel.RightView.IsBusy  = false;
                return;
            }

            _isUpdating = true;
            if (xzNeedsWork) _panel.BottomView.IsBusy = true;
            if (yzNeedsWork) _panel.RightView.IsBusy  = true;
            var lifetimeToken = _lifetimeCts.Token;
            Task.Run(() =>
            {
                try
                {
                    if (lifetimeToken.IsCancellationRequested) return;
                    IMatrixData? xz = null;
                    IMatrixData? yz = null;

                    if (compositeActive)
                    {
                        // Composite path: extract per-channel and merge, instead of a single
                        // data.Apply(...) call. Never touches the non-composite branch below.
                        if (xzProjMode.HasValue)
                            xz = xzCache ?? BuildChannelComposite(data, compositeAxisDimIndex, compositeChannelCount, bi =>
                                data.Apply(new ProjectionOperation(ViewFrom.Y, xzProjMode.Value, axisName, bi)));
                        if (yzProjMode.HasValue)
                            yz = yzCache ?? BuildChannelComposite(data, compositeAxisDimIndex, compositeChannelCount, bi =>
                                data.Apply(new ProjectionOperation(ViewFrom.X, yzProjMode.Value, axisName, bi)));

                        // Only rebuild the sides that actually need it -- see xzNeedsWork/yzNeedsWork
                        // above. Leaving xz/yz null here means "unchanged, don't touch this view"
                        // (see the SetMatrixDataInternal calls below).
                        if (xz == null && xzNeedsWork)
                            xz = BuildChannelComposite(data, compositeAxisDimIndex, compositeChannelCount, bi =>
                                data.Apply(new SliceOperation(ViewFrom.Y, iy, axisName, bi)));
                        if (yz == null && yzNeedsWork)
                            yz = BuildChannelComposite(data, compositeAxisDimIndex, compositeChannelCount, bi =>
                                data.Apply(new SliceOperation(ViewFrom.X, ix, axisName, bi)));
                    }
                    else
                    {
                        // XZ (BottomView): projection or slice?
                        if (xzProjMode.HasValue)
                            xz = xzCache ?? data.Apply(new ProjectionOperation(ViewFrom.Y, xzProjMode.Value, axisName));
                        // YZ (RightView): projection or slice?
                        if (yzProjMode.HasValue)
                            yz = yzCache ?? data.Apply(new ProjectionOperation(ViewFrom.X, yzProjMode.Value, axisName));

                        // Slice for whichever view is not in projection mode AND actually needs
                        // rebuilding. Use the fused dual-plane op only when both do; otherwise a
                        // single-plane SliceOperation avoids touching the unchanged side entirely.
                        bool needXzSlice = xz == null && xzNeedsWork;
                        bool needYzSlice = yz == null && yzNeedsWork;
                        if (needXzSlice && needYzSlice)
                        {
                            var slices = data.Apply(new SliceOrthogonalOperation(ix, iy, axisName));
                            xz = slices.XZ;
                            yz = slices.YZ;
                        }
                        else if (needXzSlice)
                        {
                            xz = data.Apply(new SliceOperation(ViewFrom.Y, iy, axisName));
                        }
                        else if (needYzSlice)
                        {
                            yz = data.Apply(new SliceOperation(ViewFrom.X, ix, axisName));
                        }
                    }

                    // Capture computed projections for caching on UI thread
                    var newXzCache = xzProjMode.HasValue ? xz : null;
                    var newYzCache = yzProjMode.HasValue ? yz : null;

                    if (lifetimeToken.IsCancellationRequested) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        // Update projection caches, unless something invalidated them while this
                        // run was in flight - storing then would resurrect a stale projection.
                        if (epoch == _cacheEpoch)
                        {
                            if (xzProjMode.HasValue) _xzProjectionCache = newXzCache;
                            if (yzProjMode.HasValue) _yzProjectionCache = newYzCache;
                        }

                        // compositeActive/compositeChannelCount were captured when this run started --
                        // if SetCompositeState changed either of them while this run was in flight
                        // (e.g. EnterCompositeMode pins the composited axis to index 0 first, which
                        // fires an axis-change slice refresh -- capturing compositeActive=false --
                        // before SetCompositeState itself runs and flips it to true), the xz/yz frames
                        // just built are shaped for the stale state, not the live one. Applying them
                        // (and the composite view state below, built from that same stale shape) would
                        // regress the Composite mode/frame-indices SetCompositeState already applied
                        // synchronously -- visibly, Bottom/Right staying in LUT mode until some
                        // unrelated refresh (e.g. moving Time) happens to call UpdateSlicesAsync again
                        // with fresh state. SetCompositeState always re-requests a shape-correct run
                        // (via _hasPending below, since this run is in flight whenever the race above
                        // can happen) whenever it changes active/channelCount, so it is safe to just
                        // drop this stale result instead of applying it.
                        bool compositeShapeCurrent = compositeActive == _compositeActive
                                                   && compositeChannelCount == _compositeChannelCount;

                        if (compositeShapeCurrent)
                        {
                            // Save non-shared axis translations before FitToView resets them.
                            // OnMatrixDataChanged → FitToView resets all translations; SyncSidesFromMain
                            // only restores the shared axis (BottomView.TransX, RightView.TransY).
                            var oldBottom = _panel.BottomView.MatrixData;
                            var oldRight  = _panel.RightView.MatrixData;
                            double savedBotTransY   = _panel.BottomView.RawTransY;
                            double savedRightTransX = _panel.RightView.RawTransX;

                            // Apply the *live* recipes/blend mode here, not the ones captured at the
                            // start of this Task.Run: a recipe/blend edit (e.g. dragging a channel's
                            // histogram Max) made while this run was in flight already pushed its own
                            // fresh values synchronously via SetCompositeState -> ApplyCompositeViewState.
                            // Reapplying the stale captured copy here would silently regress that edit
                            // right after it took effect. compositeActive/compositeChannelCount stay
                            // captured (guarded by compositeShapeCurrent above) because they must match
                            // the shape of the xz/yz frames this run just built.
                            _isSyncing = true;
                            ApplyCompositeViewState(_panel.BottomView, compositeActive, compositeChannelCount, _compositeRecipes, _compositeBlendMode);
                            ApplyCompositeViewState(_panel.RightView, compositeActive, compositeChannelCount, _compositeRecipes, _compositeBlendMode);
                            // null here means "this side wasn't rebuilt because its axis didn't move" --
                            // leaving the view's existing MatrixData in place IS the reuse, no separate
                            // cache needed.
                            if (xz != null) _panel.BottomView.SetMatrixDataInternal(xz);
                            if (yz != null) _panel.RightView.SetMatrixDataInternal(yz);
                            SyncSidesFromMain();

                            // Restore non-shared axis scroll position so the user's Z-axis
                            // scroll is preserved across slice updates.  Skip on the first
                            // assignment (old data null) to let FitToView centre correctly.
                            if (oldBottom != null)
                                _panel.BottomView.ApplyZoomAndTrans(
                                    _panel.BottomView.Zoom, _panel.BottomView.RawTransX, savedBotTransY);
                            if (oldRight != null)
                                _panel.RightView.ApplyZoomAndTrans(
                                    _panel.RightView.Zoom, savedRightTransX, _panel.RightView.RawTransY);

                            bool isDragging = _panel.BottomView.IsAxisIndicatorDragging
                                           || _panel.RightView.IsAxisIndicatorDragging;
                            if (!isDragging)
                            {
                                _panel.BottomView.ScrollToAxisIndicator();
                                _panel.RightView.ScrollToAxisIndicator();
                            }
                            _isSyncing = false;
                        }
                        _isUpdating = false;

                        if (_hasPending)
                        {
                            _hasPending = false;
                            bool pendingXChanged = _pendingXChanged;
                            bool pendingYChanged = _pendingYChanged;
                            _pendingXChanged = false;
                            _pendingYChanged = false;
                            UpdateSlicesAsync(pendingXChanged, pendingYChanged);
                        }
                        else
                        {
                            _panel.BottomView.IsBusy = false;
                            _panel.RightView.IsBusy  = false;
                        }
                    });
                }
                catch
                {
                    if (lifetimeToken.IsCancellationRequested) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _panel.BottomView.IsBusy = false;
                        _panel.RightView.IsBusy  = false;
                        _isUpdating = false;
                    });
                }
            });
        }

        // ── Projection ────────────────────────────────────────────────────────

        private void OnProjectionSelectionChanged(object? sender,
            (ProjectionPlane Plane, bool IsEnabled, ProjectionMode Mode) e)
        {
            switch (e.Plane)
            {
                case ProjectionPlane.XZ:
                    _xzProjectionMode  = e.IsEnabled ? e.Mode : null;
                    _xzProjectionCache = null;   // invalidate cache on any change
                    _cacheEpoch++;
                    break;
                case ProjectionPlane.YZ:
                    _yzProjectionMode  = e.IsEnabled ? e.Mode : null;
                    _yzProjectionCache = null;
                    _cacheEpoch++;
                    break;
                case ProjectionPlane.XY:
                    _xyProjectionMode  = e.IsEnabled ? e.Mode : null;
                    _xyColorCoded      = e.IsEnabled && _panel.ProjectionSelector.IsColorCoded(ProjectionPlane.XY);
                    _xyProjectionCache = null;
                    _cacheEpoch++;
                    if (e.IsEnabled)
                    {
                        if (_xyColorCoded)
                        {
                            // Reset to defaults on every fresh enable: full axis range, default
                            // (Spectrum) depth palette, Auto intensity range, no invert. The child
                            // window's details panel (MatrixPlotter.ColorCoded.cs) is seeded from
                            // these via ColorCodedParams right after this.
                            var axis = _data?.Dimensions[_axisName];
                            _xyColorCodedStart = 0;
                            _xyColorCodedEnd = axis != null ? axis.Count - 1 : 0;
                            _xyColorCodedDepthLut = null;
                            _xyColorCodedRangeFixed = false;
                            _xyColorCodedInvert = false;
                        }
                        ComputeXYProjectionAsync();
                    }
                    else
                        XYProjectionChanged?.Invoke(this, null);
                    return;   // XY doesn't affect crosshair or side-view slices
            }

            ApplyCrosshairVisibility();

            // Re-run with last known position to update the affected view
            if (_data != null && _currentIX >= 0 && _currentIY >= 0)
                UpdateSlicesAsync(xChanged: true, yChanged: true);
        }

        /// <summary>
        /// Hides the crosshair axis that is no longer meaningful when projection is active.
        /// XZ projection collapses Y → hide horizontal line.
        /// YZ projection collapses X → hide vertical line.
        /// </summary>
        private void ApplyCrosshairVisibility()
        {
            _panel.MainView.ShowCrosshairH = _xzProjectionMode == null;
            _panel.MainView.ShowCrosshairV = _yzProjectionMode == null;
        }

        /// <summary>
        /// Computes the XY (Z-direction) projection asynchronously and fires
        /// <see cref="XYProjectionChanged"/> with the result on the UI thread.
        /// </summary>
        private void ComputeXYProjectionAsync()
        {
            var data     = _data;
            var axisName = _axisName;
            var mode     = _xyProjectionMode;
            if (data == null || mode == null) return;

            if (_xyProjectionCache != null)
            {
                XYProjectionChanged?.Invoke(this, _xyProjectionCache);
                return;
            }

            if (_xyComputeInFlight)
            {
                _xyComputeHasPending = true;
                return;
            }
            _xyComputeInFlight = true;

            bool compositeActive = _compositeActive;
            int compositeAxisDimIndex = _compositeAxisDimIndex;
            int compositeChannelCount = _compositeChannelCount;
            // Composite and ColorCoded are mutually exclusive (see SetCompositeState's comment) --
            // ProjectionSelector.SetCompositeActive already keeps the UI from offering Color(Max)/
            // (Min) while Composite is on, but this is the actual point where the two would collide
            // (ExtremumIndexOperation's axis-index scan does not account for a composited axis), so
            // it gets its own unconditional check rather than trusting the UI never to reach here
            // with both set. Falls back to a plain projection instead of colouring.
            bool colorCoded = _xyColorCoded && !compositeActive;
            int colorCodedStart = _xyColorCodedStart;
            int colorCodedEnd = _xyColorCodedEnd;
            var colorCodedDepthLut = _xyColorCodedDepthLut;
            bool colorCodedRangeFixed = _xyColorCodedRangeFixed;
            double colorCodedFixedMin = _xyColorCodedFixedMin;
            double colorCodedFixedMax = _xyColorCodedFixedMax;
            bool colorCodedInvert = _xyColorCodedInvert;
            int epoch = _cacheEpoch;
            var lifetimeToken = _lifetimeCts.Token;

            _panel.MainView.IsBusy = true;
            Task.Run(() =>
            {
                try
                {
                    // The window (and this controller) may already be gone by the time this Task
                    // gets scheduled -- skip the scan entirely rather than do wasted work.
                    if (lifetimeToken.IsCancellationRequested) return;
                    IMatrixData result;
                    double? autoMin = null, autoMax = null;
                    ColorCodedRenderInfo? resultRenderInfo = null;
                    if (colorCoded)
                    {
                        // compositeActive is guaranteed false here (colorCoded is defined above as
                        // _xyColorCoded && !compositeActive) -- Composite and ColorCoded are
                        // mutually exclusive by design, not an unhandled combination to revisit
                        // later. See this method's colorCoded assignment and
                        // ProjectionSelector.SetCompositeActive.
                        var (winnerIndex, winnerValue) = data.Apply(
                            new ExtremumIndexOperation(mode.Value, colorCodedStart, colorCodedEnd, axisName));
                        // winnerValue -- an ordinary projection-shaped result, same type as the
                        // source data -- becomes the window's own MatrixData directly. Colorizing
                        // happens only at render time (ColorCodedBitmapWriter, driven by
                        // ColorCodedRenderInfo below); the window's data itself is a real projected
                        // value everywhere Duplicate/Convert/Filter/Overlay analysis/Save touch it,
                        // not an opaque packed-ARGB int the way the earlier TrueColorBitmapWriter
                        // design left it. See ColorCodedRenderInfo's doc comment.
                        result = winnerValue;
                        // The natural range comes from winnerValue itself (the winning values
                        // actually picked by the scan), not data.GetValueRange() -- the latter is
                        // the *source* data's current-frame range, which depends on _axisName's own
                        // ActiveIndex. Since the projected axis is fully collapsed by the Start..End
                        // scan, moving its slider must not change the displayed colours; winnerValue
                        // is a fresh single-frame result that only depends on the other axes'
                        // positions (correctly re-picked when e.g. Time changes). Always computed
                        // (not just when Auto is selected) so the child's Search buttons have
                        // something to search for even while Fixed is selected.
                        var (naturalMin, naturalMax) = winnerValue.GetValueRange();
                        autoMin = naturalMin;
                        autoMax = naturalMax;
                        double valueMin = colorCodedRangeFixed ? colorCodedFixedMin : naturalMin;
                        double valueMax = colorCodedRangeFixed ? colorCodedFixedMax : naturalMax;
                        int sliceCount = colorCodedEnd - colorCodedStart + 1;
                        var depthColors = (colorCodedDepthLut ?? ColorThemes.Spectrum)
                            .Resample(sliceCount).AsSpan().ToArray();
                        // Invert reverses which end of the depth palette maps to which end of the
                        // axis -- the same "reverse the LUT array" semantics ordinary LUT mode's
                        // Invert already has (MatrixPlotter.cs's BuildHistogramLutColors), not a
                        // per-pixel intensity flip. Baking it into the array order here means
                        // ColorCodedBitmapWriter and the depth histogram (both of which just consume
                        // DepthColors as given) automatically respect it with no separate flag.
                        if (colorCodedInvert) Array.Reverse(depthColors);
                        var scannedAxis = data.Axes.FindAxis(axisName);
                        if (scannedAxis != null)
                        {
                            // Computed here (background thread), not in UpdateColorCodedHistogram on
                            // the UI thread -- this single-threaded full-frame scan over WinnerIndex
                            // was the dominant remaining UI-thread stall on every Start/End edit for
                            // a large XY / long-axis dataset (e.g. otomo1.tif's 997-frame stack).
                            // Riding along with the ExtremumIndexOperation scan that already produced
                            // WinnerIndex means no extra pass is needed later, just a hand-off.
                            int[] hist = winnerIndex.CreateHistogram(0, scannedAxis.Count, 0, scannedAxis.Count);
                            resultRenderInfo = new ColorCodedRenderInfo(
                                winnerIndex, colorCodedStart, depthColors, valueMin, valueMax, scannedAxis, hist);
                        }
                    }
                    else
                    {
                        result = compositeActive
                            ? BuildChannelComposite(data, compositeAxisDimIndex, compositeChannelCount, bi =>
                                data.Apply(new ProjectionOperation(ViewFrom.Z, mode.Value, axisName, bi)))
                            : data.Apply(new ProjectionOperation(ViewFrom.Z, mode.Value, axisName));
                    }
                    // Nothing left to do once the owning window is gone -- skip scheduling the
                    // continuation entirely rather than post work whose only purpose is updating
                    // that window's (and its controller's) now-meaningless state.
                    if (lifetimeToken.IsCancellationRequested) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (epoch == _cacheEpoch)
                        {
                            _xyProjectionCache = result;
                            if (colorCoded)
                            {
                                _xyColorCodedLastAutoMin = autoMin;
                                _xyColorCodedLastAutoMax = autoMax;
                                _xyColorCodedLastRenderInfo = resultRenderInfo;
                            }
                        }
                        _panel.MainView.IsBusy = false;
                        XYProjectionChanged?.Invoke(this, result);
                        _xyComputeInFlight = false;
                        if (_xyComputeHasPending)
                        {
                            _xyComputeHasPending = false;
                            ComputeXYProjectionAsync();
                        }
                    });
                }
                catch
                {
                    if (lifetimeToken.IsCancellationRequested) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        _panel.MainView.IsBusy = false;
                        if (colorCoded) ColorCodedComputeFailed?.Invoke();
                        _xyComputeInFlight = false;
                        if (_xyComputeHasPending)
                        {
                            _xyComputeHasPending = false;
                            ComputeXYProjectionAsync();
                        }
                    });
                }
            });
        }

        /// <summary>
        /// Programmatically clears the XY projection state without firing <see cref="XYProjectionChanged"/>.
        /// Used by the host when the projection window is closed externally.
        /// </summary>
        public void ClearXYProjection()
        {
            _xyProjectionMode  = null;
            _xyColorCoded      = false;
            _xyProjectionCache = null;
            _cacheEpoch++;
        }
    }
}
