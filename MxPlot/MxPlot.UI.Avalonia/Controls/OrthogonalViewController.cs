using Avalonia;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.Processing;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Manages the orthogonal slice views inside an <see cref="OrthogonalPanel"/>.
    /// Call <see cref="Activate"/> to enable the crosshair and slice views for a given axis,
    /// and <see cref="Deactivate"/> to hide them.
    /// </summary>
    /// <remarks>
    /// Slice updates run on a background thread; a pending-update pattern ensures the
    /// final crosshair position is always rendered even under rapid mouse movement.
    /// </remarks>
    public sealed class OrthogonalViewController
    {
        private readonly OrthogonalPanel _panel;

        private IMatrixData? _data;
        private string       _axisName = string.Empty;

        private bool _isUpdating;
        private bool _hasPending;
        private int  _pendingIX;
        private int  _pendingIY;
        private bool _isSyncing;
        private int  _lastIX = -1;
        private int  _lastIY = -1;

        private EventHandler<int>? _axisIndicatorDraggedHandlerBottom;
        private EventHandler<int>? _axisIndicatorDraggedHandlerRight;
        private EventHandler?      _axisIndicatorDragEndedHandlerBottom;
        private EventHandler?      _axisIndicatorDragEndedHandlerRight;
        private EventHandler<(double Min, double Max)>? _autoRangeComputedHandler;

        // ── Projection state ──────────────────────────────────────────────────
        private ProjectionMode? _xzProjectionMode;   // null = slice mode
        private ProjectionMode? _yzProjectionMode;   // null = slice mode
        private ProjectionMode? _xyProjectionMode;   // null = no XY projection
        private IMatrixData?    _xzProjectionCache;
        private IMatrixData?    _yzProjectionCache;
        private IMatrixData?    _xyProjectionCache;

        /// <summary>
        /// Fired when an XY (Z-direction) projection result is available or cleared.
        /// Carries the projected <see cref="IMatrixData"/> or <c>null</c> when disabled.
        /// </summary>
        public event EventHandler<IMatrixData?>? XYProjectionChanged;

        /// <summary>
        /// The depth axis name when orthogonal views are active, or <c>null</c> when deactivated.
        /// </summary>
        public string? ActiveAxisName => _data != null ? _axisName : null;

        public OrthogonalViewController(OrthogonalPanel panel)
        {
            _panel = panel;
        }

        /// <summary>Activates orthogonal views for the specified data and axis name.</summary>
        public void Activate(IMatrixData data, string axisName)
        {
            _data     = data;
            _axisName = axisName;

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
            OnCrosshairMoved(this, cp);
        }

        /// <summary>Deactivates orthogonal views and hides the crosshair.</summary>
        public void Deactivate()
        {
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
            _panel.MainView.ShowCrosshair   = false;
            _panel.MainView.ShowCrosshairH  = true;
            _panel.MainView.ShowCrosshairV  = true;
            _panel.ShowRight  = false;
            _panel.ShowBottom = false;
            _panel.RightView.MatrixData  = null;
            _panel.BottomView.MatrixData = null;
            _xzProjectionCache = null;
            _yzProjectionCache = null;
            bool hadXYProjection = _xyProjectionMode != null;
            _xyProjectionMode  = null;
            _xyProjectionCache = null;
            _data     = null;
            _axisName = string.Empty;

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

            // Side views always use fixed range so they display the same value scale as MainView.
            var (min, max) = main.IsFixedRange
                ? (main.FixedMin, main.FixedMax)
                : main.ScanCurrentFrameRange();
            right.IsFixedRange = true;
            bot.IsFixedRange   = true;
            right.FixedMin  = min;
            bot.FixedMin    = min;
            right.FixedMax  = max;
            bot.FixedMax    = max;
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
                var axis = _data.Axes.FirstOrDefault(a => a.Name == _axisName);
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
        /// Re-runs the slice update at the last known crosshair position.
        /// Call when the active frame changes because a non-volume axis index changed.
        /// Has no effect if the orthogonal view is not active or no crosshair position has been set.
        /// </summary>
        public void RefreshSlices()
        {
            if (_data == null || _lastIX < 0 || _lastIY < 0) return;
            // Projection caches depend on frame data; invalidate on any refresh
            _xzProjectionCache = null;
            _yzProjectionCache = null;
            _xyProjectionCache = null;
            UpdateSlicesAsync(_lastIX, _lastIY);
            if (_xyProjectionMode != null)
                ComputeXYProjectionAsync();
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
            if (md == null || _lastIX < 0 || _lastIY < 0) return;

            int ix = Math.Clamp(_lastIX, 0, md.XCount - 1);
            int iy = Math.Clamp(_lastIY, 0, md.YCount - 1);

            if (md.XStep != 0 && md.YStep != 0)
            {
                double cx = md.XMin + (ix + 0.5) * md.XStep;
                // iy is a data array index (0=YMin). Convert back to snap-convention coordinate.
                double cy = md.YMax - (md.YCount - 1 - iy + 0.5) * md.YStep;
                _panel.MainView.SetCrosshairDataPosition(new Point(cx, cy));
            }

            _lastIX = ix;
            _lastIY = iy;
            UpdateFrameIndicator();
            UpdateSlicesAsync(ix, iy);
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

            // BottomView (XZ): X-axis shared with MainView.
            // Compensate zoom so 1 physical unit in X occupies the same screen pixels.
            var (axBottom, _) = _panel.BottomView.GetAspectScales();
            double zoomBottom = axBottom > 0 ? zoom * axMain / axBottom : zoom;
            _panel.BottomView.ApplyZoomAndTrans(zoomBottom, tx, _panel.BottomView.RawTransY);

            // RightView (YZ, Rotate90CCW): screen-height direction = Y axis, shared with MainView.
            // ax_right = YStep_main / min(YStep_main, ZStep) → compensate with ayMain.
            var (axRight, _) = _panel.RightView.GetAspectScales();
            double zoomRight = axRight > 0 ? zoom * ayMain / axRight : zoom;
            _panel.RightView.ApplyZoomAndTrans(zoomRight, _panel.RightView.RawTransX, ty);
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
                    // SyncSidesFromMain computes: zoomBottom = zoomMain * axMain / axBottom.
                    // If bot.Zoom matches that formula → resize triggered this event; keep fit mode.
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
                    if (_data != null && _lastIY >= 0 && main.Bounds.Height > 0)
                    {
                        // _lastIY is a data-array row index (0 = YMin).
                        // MainView uses FlipY=true: bmpRow 0 = top of image = YMax.
                        int bmpRow = _data.YCount - 1 - _lastIY;
                        targetTransY = main.Bounds.Height / 2.0 - (bmpRow + 0.5) * zoomMain * ayMain;
                    }
                    main.ApplyZoomAndTrans(zoomMain, bot.RawTransX, targetTransY);

                    // MainView → RightView: propagate (Y axis shared).
                    var (axRight, _) = _panel.RightView.GetAspectScales();
                    double zoomRight = axRight > 0 ? zoomMain * ayMain / axRight : zoomMain;
                    _panel.RightView.ApplyZoomAndTrans(zoomRight, _panel.RightView.RawTransX, main.RawTransY);
                }
                else
                {
                    var right = _panel.RightView;
                    var (axRight, _) = right.GetAspectScales();

                    // Issue 1: same resize-vs-user-zoom detection for RightView.
                    // SyncSidesFromMain computes: zoomRight = zoomMain * ayMain / axRight.
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
                    if (_data != null && _lastIX >= 0 && main.Bounds.Width > 0)
                    {
                        targetTransX = main.Bounds.Width / 2.0 - (_lastIX + 0.5) * zoomMain * axMain;
                    }
                    main.ApplyZoomAndTrans(zoomMain, targetTransX, right.RawTransY);

                    // MainView → BottomView: propagate (X axis shared).
                    var (axBottom, _) = _panel.BottomView.GetAspectScales();
                    double zoomBottom = axBottom > 0 ? zoomMain * axMain / axBottom : zoomMain;
                    _panel.BottomView.ApplyZoomAndTrans(zoomBottom, main.RawTransX, _panel.BottomView.RawTransY);
                }
            }
            finally { _isSyncing = false; }
        }

        private void OnAxisIndicatorDragged(int newIdx)
        {
            var data = _data;
            if (data == null) return;
            var axis = data.Axes.FirstOrDefault(a => a.Name == _axisName);
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

            _lastIX = ix;
            _lastIY = iy;
            UpdateSlicesAsync(ix, iy);
        }

        private void UpdateSlicesAsync(int ix, int iy)
        {
            if (_isUpdating)
            {
                _hasPending = true;
                _pendingIX  = ix;
                _pendingIY  = iy;
                return;
            }

            var data     = _data;
            var axisName = _axisName;
            if (data == null) return;

            // Capture current projection state for the background thread
            var xzProjMode = _xzProjectionMode;
            var yzProjMode = _yzProjectionMode;
            var xzCache    = _xzProjectionCache;
            var yzCache    = _yzProjectionCache;

            // Fast path: both views are in projection mode and both caches are valid
            // → no computation needed, only the slice-mode view requires a new result.
            bool xzNeedsWork = xzProjMode.HasValue ? xzCache == null : true;
            bool yzNeedsWork = yzProjMode.HasValue ? yzCache == null : true;
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
            Task.Run(() =>
            {
                try
                {
                    IMatrixData? xz = null;
                    IMatrixData? yz = null;

                    // XZ (BottomView): projection or slice?
                    if (xzProjMode.HasValue)
                        xz = xzCache ?? data.Apply(new ProjectionOperation(ViewFrom.Y, xzProjMode.Value, axisName));
                    // YZ (RightView): projection or slice?
                    if (yzProjMode.HasValue)
                        yz = yzCache ?? data.Apply(new ProjectionOperation(ViewFrom.X, yzProjMode.Value, axisName));

                    // Slice for whichever view is not in projection mode
                    if (xz == null || yz == null)
                    {
                        if (xz == null && yz == null)
                        {
                            // Both slice
                            var slices = data.Apply(new SliceOrthogonalOperation(ix, iy, axisName));
                            xz = slices.XZ;
                            yz = slices.YZ;
                        }
                        else if (xz == null)
                        {
                            xz = data.Apply(new SliceOperation(ViewFrom.Y, iy, axisName));
                        }
                        else
                        {
                            yz = data.Apply(new SliceOperation(ViewFrom.X, ix, axisName));
                        }
                    }

                    // Capture computed projections for caching on UI thread
                    var newXzCache = xzProjMode.HasValue ? xz : null;
                    var newYzCache = yzProjMode.HasValue ? yz : null;

                    Dispatcher.UIThread.Post(() =>
                    {
                        // Update projection caches
                        if (xzProjMode.HasValue) _xzProjectionCache = newXzCache;
                        if (yzProjMode.HasValue) _yzProjectionCache = newYzCache;

                        // Save non-shared axis translations before FitToView resets them.
                        // OnMatrixDataChanged → FitToView resets all translations; SyncSidesFromMain
                        // only restores the shared axis (BottomView.TransX, RightView.TransY).
                        var oldBottom = _panel.BottomView.MatrixData;
                        var oldRight  = _panel.RightView.MatrixData;
                        double savedBotTransY   = _panel.BottomView.RawTransY;
                        double savedRightTransX = _panel.RightView.RawTransX;

                        _isSyncing = true;
                        _panel.BottomView.MatrixData = xz;
                        _panel.RightView.MatrixData  = yz;
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
                        _isUpdating = false;

                        if (_hasPending)
                        {
                            _hasPending = false;
                            UpdateSlicesAsync(_pendingIX, _pendingIY);
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
                    break;
                case ProjectionPlane.YZ:
                    _yzProjectionMode  = e.IsEnabled ? e.Mode : null;
                    _yzProjectionCache = null;
                    break;
                case ProjectionPlane.XY:
                    _xyProjectionMode  = e.IsEnabled ? e.Mode : null;
                    _xyProjectionCache = null;
                    if (e.IsEnabled)
                        ComputeXYProjectionAsync();
                    else
                        XYProjectionChanged?.Invoke(this, null);
                    return;   // XY doesn't affect crosshair or side-view slices
            }

            ApplyCrosshairVisibility();

            // Re-run with last known position to update the affected view
            if (_data != null && _lastIX >= 0 && _lastIY >= 0)
                UpdateSlicesAsync(_lastIX, _lastIY);
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

            _panel.MainView.IsBusy = true;
            Task.Run(() =>
            {
                try
                {
                    var result = data.Apply(new ProjectionOperation(ViewFrom.Z, mode.Value, axisName));
                    Dispatcher.UIThread.Post(() =>
                    {
                        _xyProjectionCache = result;
                        _panel.MainView.IsBusy = false;
                        XYProjectionChanged?.Invoke(this, result);
                    });
                }
                catch
                {
                    Dispatcher.UIThread.Post(() => _panel.MainView.IsBusy = false);
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
            _xyProjectionCache = null;
        }
    }
}
