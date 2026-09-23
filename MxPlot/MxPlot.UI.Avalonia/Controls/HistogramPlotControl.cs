using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Controls
{
    public class HistogramPlotControl : Control
    {
        // ── Data ──────────────────────────────────────────────────────────────
        private int[]? _bins;
        private double _valueMin;   // Minimum value of the data range (left edge)
        private double _valueMax;   // Maximum value of the data range (right edge)

        private int[]? _aRgbs;

        // ── View / Plot ───────────────────────────────────────────────────────
        // _viewMin/Max : range for LUT application. Red line position. Does not exceed _valueMin/Max.
        // _plotMin/Max : plot window edges. Changes with zoom.
        //                Always includes both _viewMin/Max and _valueMin/Max.
        private double _viewMin;
        private double _viewMax;
        private double _plotMin;
        private double _plotMax;

        // For badge calculation: true = use viewRange (All mode), false = use valueRange (Fixed/Current)
        private bool _useViewRangeAsBase;

        private bool _logViewEnabled = false;

        // ── Rendering ─────────────────────────────────────────────────────────
        private IBrush[]? _cachedLutBrushes;
        private readonly IBrush _grayoutBrush = new SolidColorBrush(Color.FromArgb(100, 128, 128, 128));
        private Pen _boundaryPen = new Pen(Brushes.Red, 2.0);

        // ── Drag ──────────────────────────────────────────────────────────────
        private enum DragTarget { None, ViewMin, ViewMax }
        private DragTarget _hoverTarget = DragTarget.None;
        private DragTarget _dragTarget = DragTarget.None;
        private bool _isDragging = false;
        private const double HitTolerance = 8.0;

        // Prevents OnPropertyChanged from overwriting _viewMin/_viewMax during SetHistogram()
        private bool _suppressPropertySync = false;

        // ── HUD Badge ─────────────────────────────────────────────────────────
        private bool _showBadge = false;
        private string _badgeText = "";
        private Rect? _scaleBadgeRect = null;
        private DispatcherTimer? _badgeHideTimer;

        /// <summary>
        /// Horizontal alignment for badge positioning.
        /// </summary>
        private enum BadgeAnchor { TopLeft, TopRight }


        // ── Events / Properties ───────────────────────────────────────────────
        public event Action<double, double>? ViewRangeChanged;

        public static readonly StyledProperty<double> ViewMinProperty =
            AvaloniaProperty.Register<HistogramPlotControl, double>(nameof(ViewMin), 0.0);
        public static readonly StyledProperty<double> ViewMaxProperty =
            AvaloniaProperty.Register<HistogramPlotControl, double>(nameof(ViewMax), 1.0);

        public static readonly StyledProperty<bool> LogViewEnabledProperty =
            AvaloniaProperty.Register<HistogramPlotControl, bool>(nameof(LogViewEnabled), false);

        public double ViewMin
        {
            get => GetValue(ViewMinProperty);
            set => SetValue(ViewMinProperty, value);
        }
        public double ViewMax
        {
            get => GetValue(ViewMaxProperty);
            set => SetValue(ViewMaxProperty, value);
        }

        public bool LogViewEnabled
        {
            get => GetValue(LogViewEnabledProperty);
            set => SetValue(LogViewEnabledProperty, value);
        }

        /// <summary>
        /// Color of the two boundary lines marking [ViewMin, ViewMax]. Defaults to red (the ordinary
        /// value-range/LUT reading). Callers that reuse this control for something other than a value
        /// range -- e.g. MatrixPlotter.ColorCoded's depth histogram, where the boundary lines mark the
        /// frame range used for color coding -- should pick a different color here so the two readings
        /// aren't visually confused with each other.
        /// </summary>
        public IBrush BoundaryBrush
        {
            get => _boundaryPen.Brush!;
            set { _boundaryPen = new Pen(value, _boundaryPen.Thickness); InvalidateVisual(); }
        }

        /// <summary>
        /// Optional text prepended to the tooltip, for callers that reuse this control for something
        /// other than a value range (see <see cref="BoundaryBrush"/>).
        /// </summary>
        public string? Description
        {
            get => _description;
            set { _description = value; UpdateTooltip(); }
        }
        private string? _description;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ViewMinProperty || change.Property == ViewMaxProperty)
            {
                // Skip sync during SetHistogram() — the field values are already correct
                if (_suppressPropertySync) return;

                _viewMin = ViewMin;
                _viewMax = ViewMax;
                if (_bins == null) return;
                UpdateBrushCache();  // Refresh LUT color mapping when view range changes
                InvalidateVisual();
            }
            if(change.Property == LogViewEnabledProperty)
            {
                _logViewEnabled = LogViewEnabled;
                if (_bins == null) return;
                ShowBadge(_logViewEnabled ? "Y: Log" : "Y: Linear");
                UpdateTooltip();
            }
        }

        private void UpdateTooltip()
        {
            string mode = _logViewEnabled ? "Log" : "Linear";
            string next = _logViewEnabled ? "Linear" : "Log";
            string baseText =
                $"Y: {mode}  |  Right-click to switch to {next}\n" +
                "Double-click to fit view";
            ToolTip.SetTip(this, string.IsNullOrEmpty(_description) ? baseText : $"{_description}\n{baseText}");
        }

        public HistogramPlotControl()
        {
            IsHitTestVisible = true;
            // Static tooltip describing available interactions.
            UpdateTooltip();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Sets histogram data, value range, view range (red lines), and LUT.
        /// </summary>
        /// <param name="bins">Histogram bin counts.</param>
        /// <param name="valueMin">Data minimum value (histogram left edge).</param>
        /// <param name="valueMax">Data maximum value (histogram right edge).</param>
        /// <param name="viewMin">View range minimum (left red line position, LUT application start).</param>
        /// <param name="viewMax">View range maximum (right red line position, LUT application end).</param>
        /// <param name="aRgbs">LUT color array (length = LUT level, typically 256).</param>
        /// <param name="preservePlotWindow">
        /// When <c>true</c>, preserves the current plot window zoom state (Fixed/Roi mode).
        /// When <c>false</c>, resets plot window to 1:1 with view range (Auto/Current/All mode).
        /// Default is <c>false</c>.
        /// </param>
        /// <param name="useViewRangeAsBase">
        /// When <c>true</c> (All mode), uses viewRange as the base for "Range: 1x" badge calculation.
        /// When <c>false</c> (Fixed/Current/Roi mode), uses valueRange as the base.
        /// Default is <c>false</c>.
        /// </param>
        public void SetHistogram(int[] bins, double valueMin, double valueMax, 
            double viewMin, double viewMax, int[]? aRgbs = null, bool preservePlotWindow = false, bool useViewRangeAsBase = false)
        {
            void Apply()
            {
                bool isFirstCall = _bins == null;

                _bins = bins;
                _valueMin = valueMin;
                _valueMax = valueMax;
                _aRgbs = aRgbs;
                _useViewRangeAsBase = useViewRangeAsBase;

                // Set view range (red line positions) to provided values.
                _viewMin = viewMin;
                _viewMax = viewMax;

                // Plot window behavior:
                // - First call: initialize to view range (red line positions)
                // - Subsequent calls with preservePlotWindow=true (Fixed/Roi mode):
                //   Keep current zoom, but ensure plot window includes view range.
                // - Subsequent calls with preservePlotWindow=false (Auto/Current/All):
                //   Reset to 1:1 display (plot window = view range).
                if (isFirstCall)
                {
                    // Initialize plot window to view range (NOT data range).
                    // User can zoom out beyond data range later via wheel.
                    _plotMin = viewMin;
                    _plotMax = viewMax;
                }
                else if (preservePlotWindow)
                {
                    // Fixed/Roi mode: preserve zoom, but expand plot window if needed to include view range
                    _plotMin = Math.Min(_plotMin, viewMin);
                    _plotMax = Math.Max(_plotMax, viewMax);
                }
                else
                {
                    // Auto/Current/All: reset plot window to 1:1 with view range
                    _plotMin = viewMin;
                    _plotMax = viewMax;
                    Debug.WriteLine($"[SetHistogram] Reset plotRange to viewRange: plotMin={_plotMin:F6}, plotMax={_plotMax:F6}");
                }

                // Sync StyledProperties without triggering OnPropertyChanged feedback
                _suppressPropertySync = true;
                SetCurrentValue(ViewMinProperty, _viewMin);
                SetCurrentValue(ViewMaxProperty, _viewMax);
                SetCurrentValue(LogViewEnabledProperty, _logViewEnabled);
                _suppressPropertySync = false;

                UpdateBrushCache();
                IsVisible = bins.Length > 0;
                InvalidateVisual();

                Debug.WriteLine($"[SetHistogram] Apply: valueMin={_valueMin}, valueMax={_valueMax}, viewMin={_viewMin}, viewMax={_viewMax}, plotMin={_plotMin}, plotMax={_plotMax}, bins={_bins.Length}, aRgbs={_aRgbs?.Length ?? 0}, preservePlot={preservePlotWindow}");
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        public void SetLut(int[]? aRgbs)
        {
            void Apply()
            {
                if (_bins == null) return;
                // Allow any LUT size; UpdateBrushCache will map bins to LUT via _lutMin/_lutMax
                _aRgbs = aRgbs;
                UpdateBrushCache();
                InvalidateVisual();
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        public void SetViewValueRange(double min, double max)
        {
            void Apply()
            {
                if (_bins == null) return;

                // Ignore external updates during user drag to prevent feedback loop
                if (_isDragging) return;

                // Update view range (red lines).
                // No clamping needed here — the caller (RangeBar) already ensures validity.
                _viewMin = min;
                _viewMax = max;

                // Keep the plot window framing the (possibly newly-widened) view range, mirroring
                // SetHistogram's preservePlotWindow=true behavior. Without this, a programmatic
                // range push that lands outside the current zoom (e.g. the "Find" data-extremes
                // button) just clamps to the widget edges at render time and looks like nothing
                // happened, even though _viewMin/_viewMax genuinely changed.
                _plotMin = Math.Min(_plotMin, _viewMin);
                _plotMax = Math.Max(_plotMax, _viewMax);

                // Sync StyledProperties
                SetCurrentValue(ViewMinProperty, _viewMin);
                SetCurrentValue(ViewMaxProperty, _viewMax);

                // Refresh brush cache to reflect new view range in LUT color mapping.
                UpdateBrushCache();

                InvalidateVisual();
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }

        // ── Render ────────────────────────────────────────────────────────────

        public override void Render(DrawingContext context)
        {
            // Draw a fully transparent rectangle covering the entire control bounds.
            // This ensures pointer events (wheel, click, drag) are received across
            // the full control area, not just over rendered pixels.

            //context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
            // Theme-aware background: mid-gray base, slightly lighter for Light mode.
            // Mid-gray is the most neutral choice across all LUT types (white, black, color).
            var bgBrush = ActualThemeVariant == ThemeVariant.Dark
                ? new SolidColorBrush(Color.FromRgb(95, 95, 95))   // Dark: #2D2D2D
                : new SolidColorBrush(Color.FromRgb(200, 200,200));   // Light: #5A5A5A  
            context.DrawRectangle(bgBrush, null, new Rect(Bounds.Size));

            if (_bins == null || _bins.Length == 0) return;

            double width = Bounds.Width;
            double height = Bounds.Height;

            // Guard against layout-incomplete state: skip rendering if control is too small
            if (width < 10 || height < 10) return;

            double plotRange = Math.Max(1e-9, _plotMax - _plotMin);
            double binSizeInValue = (_valueMax - _valueMin) / _bins.Length;

            int maxCount = 0;
            foreach (var b in _bins) if (b > maxCount) maxCount = b;
            if (maxCount == 0) maxCount = 1;

            double pixelViewMin = Math.Clamp((_viewMin - _plotMin) / plotRange * width, 0, width);
            double pixelViewMax = Math.Clamp((_viewMax - _plotMin) / plotRange * width, 0, width);

            Debug.WriteLine($"[Render] width={width:F1}, _viewMin={_viewMin:F6}, _viewMax={_viewMax:F6}, _plotMin={_plotMin:F6}, _plotMax={_plotMax:F6}, plotRange={plotRange:F6}, pixelViewMin={pixelViewMin:F1}, pixelViewMax={pixelViewMax:F1}");

            // Clip all histogram drawing to the control bounds.
            // Required when _plotMin/_plotMax is wider than _valueMin/_valueMax
            // (i.e. histogram is zoomed out), to prevent bars from rendering outside the control.
            using (context.PushClip(new Rect(0, 0, width, height)))
            {
                // PASS 1: grayout
                DrawHistogramPass(context, width, height, plotRange, binSizeInValue, maxCount, null);

                // PASS 2: LUT color in active view range
                if (pixelViewMax > pixelViewMin)
                {
                    using (context.PushClip(new Rect(pixelViewMin, 0, pixelViewMax - pixelViewMin, height)))
                        DrawHistogramPass(context, width, height, plotRange, binSizeInValue, maxCount, _cachedLutBrushes);
                }
            }

            // PASS 3: red boundary lines drawn outside the clip so they are always fully visible at edges.
            // Draw lines even when ViewMin == ViewMax (single position) to maintain visibility.
            if (pixelViewMax >= pixelViewMin)
            {
                double half = _boundaryPen.Thickness / 2.0;
                double lineMin = Math.Clamp(pixelViewMin, half, width - half);
                double lineMax = Math.Clamp(pixelViewMax, half, width - half);
                context.DrawLine(_boundaryPen, new Point(lineMin, 0), new Point(lineMin, height));
                // Only draw the second line if positions differ (avoids overdraw when overlapping)
                if (Math.Abs(lineMax - lineMin) > 0.5)
                    context.DrawLine(_boundaryPen, new Point(lineMax, 0), new Point(lineMax, height));
            }

            // PASS 4: HUD badge (top-right, temporary, shared slot for zoom and scale mode)
            if (_showBadge && !string.IsNullOrEmpty(_badgeText))
                DrawBadge(context, width, _badgeText, BadgeAnchor.TopRight);
        }

        private void DrawHistogramPass(
            DrawingContext context, double controlWidth, double controlHeight,
            double plotRange, double binSizeInValue, int maxCount, IBrush[]? colorPalette)
        {
            if (_bins == null) return;

            // Precompute log denominator once outside the loop for performance.
            double logMaxCount = _logViewEnabled ? Math.Log(maxCount + 1) : 1.0;

            for (int i = 0; i < _bins.Length; i++)
            {
                double binValMin = _valueMin + (i * binSizeInValue);
                double binValMax = _valueMin + ((i + 1) * binSizeInValue);

                double xStart = (binValMin - _plotMin) / plotRange * controlWidth;
                double xEnd = (binValMax - _plotMin) / plotRange * controlWidth;
                double barWidth = Math.Max(1.0, (xEnd - xStart) + 0.4);

                // Linear or logarithmic height normalization.
                double normalizedHeight = _logViewEnabled
                    ? Math.Log(_bins[i] + 1) / logMaxCount   // log(count+1) / log(maxCount+1)
                    : (double)_bins[i] / maxCount;            // count / maxCount

                double barHeight = normalizedHeight * controlHeight;
                double yStart = controlHeight - barHeight;

                // Use pre-cached brush indexed by bin position.
                // colorPalette is either null (grayout) or _cachedLutBrushes (colored).
                IBrush brush = colorPalette?[i] ?? _grayoutBrush;

                context.DrawRectangle(brush, null, new Rect(xStart, yStart, barWidth, barHeight));
            }
        }

        // Unified badge text: zoom and scale mode share the same top-right slot.
        // Latest call wins; the hide timer is always restarted.
        private void ShowBadge(string text)
        {
            _badgeText = text;
            _showBadge = true;
            _scaleBadgeRect = null; // rect is recalculated on next Render

            _badgeHideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _badgeHideTimer.Tick -= OnBadgeTimerTick;
            _badgeHideTimer.Tick += OnBadgeTimerTick;
            _badgeHideTimer.Stop();
            _badgeHideTimer.Start();

            InvalidateVisual();
        }

       

        /// <summary>
        /// Draws a rounded rectangle badge with text at the specified anchor position.
        /// </summary>
        private void DrawBadge(DrawingContext context, double controlWidth, string text, BadgeAnchor anchor)
        {
            var typeface = new Typeface("Consolas, Courier New, monospace");
            var ft = new FormattedText(
                text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11.0, Brushes.White);

            const double px = 6.0, py = 2.0, margin = 4.0;
            double bw = ft.Width + px * 2;
            double bh = ft.Height + py * 2;
            double bx = anchor == BadgeAnchor.TopLeft ? margin : controlWidth - bw - margin;
            double by = margin;

            var rect = new Rect(bx, by, bw, bh);

            // Store top-left badge rect for pointer hit testing.
            if (anchor == BadgeAnchor.TopLeft)
                _scaleBadgeRect = rect;

            context.DrawRectangle(
                new SolidColorBrush(Color.FromArgb(180, 20, 20, 20)), null,
                rect, 4.0, 4.0);
            context.DrawText(ft, new Point(bx + px, by + py));
        
        }

        // ── Mouse ─────────────────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (_bins == null || _bins.Length == 0) return;

            // Double-click: reset plot window to 1:1 with view range.
            if (e.ClickCount == 2)
            {
                
                _plotMin = Math.Min(_viewMin, _valueMin);
                _plotMax = Math.Max(_viewMax, _valueMax);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if (_hoverTarget != DragTarget.None)
            {
                // Near a boundary line: start drag.
                _isDragging = true;
                _dragTarget = _hoverTarget;
                e.Pointer.Capture(this);
                e.Handled = true;
            }
            else if(e.Properties.IsRightButtonPressed) 
            {
                // Single click on empty area: toggle log/linear scale.
                LogViewEnabled = !LogViewEnabled;
                e.Handled = true;
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(this);
            double w = Bounds.Width;

            if (_isDragging && w > 0)
            {
                // Convert mouse position to physical value using CURRENT plot window, not drag-start snapshot
                double plotRange = Math.Max(1e-9, _plotMax - _plotMin);
                double physical = _plotMin + (pos.X / w) * plotRange;

                if (_dragTarget == DragTarget.ViewMin)
                {
                    // Clamp new ViewMin: must stay within plot bounds and not exceed current ViewMax
                    double newMin = Math.Clamp(physical, _plotMin, ViewMax);
                    ViewMin = newMin;
                    // Fire event during drag for real-time sync to ValueRangeBar
                    ViewRangeChanged?.Invoke(ViewMin, ViewMax);
                }
                else if (_dragTarget == DragTarget.ViewMax)
                {
                    // Clamp new ViewMax: must stay within plot bounds and not go below current ViewMin
                    double newMax = Math.Clamp(physical, ViewMin, _plotMax);

                    ViewMax = newMax;
                    // Fire event during drag for real-time sync to ValueRangeBar
                    ViewRangeChanged?.Invoke(ViewMin, ViewMax);
                }
                return;
            }

            _hoverTarget = GetHitTarget(pos.X);
            Cursor = _hoverTarget != DragTarget.None
                ? new Cursor(StandardCursorType.SizeWestEast)
                : Cursor.Default;
        }


        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            // No need to fire ViewRangeChanged here anymore (already fired during drag)

            _isDragging = false;
            _dragTarget = DragTarget.None;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_bins == null || _bins.Length == 0) return;

            double w = Bounds.Width;
            double plotRange = Math.Max(1e-9, _plotMax - _plotMin);

            // Zoom anchor: mouse X position mapped to physical value.
            double mouseValue = w > 0
                ? _plotMin + (e.GetPosition(this).X / w) * plotRange
                : (_plotMin + _plotMax) / 2.0;

            const double ZoomFactor = 1.2;
            double ratio = (mouseValue - _plotMin) / plotRange;

            if (e.Delta.Y < 0)
            {
                // Scroll up = shrink histogram (expand plot window outward).
                // This reveals margin space beyond valueMin/Max where ViewMin/Max can be dragged.
                double newRange = plotRange * ZoomFactor;
                double newMin = mouseValue - ratio * newRange;
                double newMax = mouseValue + (1.0 - ratio) * newRange;
                _plotMin = newMin;
                _plotMax = newMax;
            }
            else
            {
                // Scroll down = expand histogram back toward 1:1 (shrink plot window).
                // Stop when plot window matches the view range exactly (1:1, no margin).
                if (_plotMin >= _viewMin && _plotMax <= _viewMax) return;

                double newRange = plotRange / ZoomFactor;
                double newMin = mouseValue - ratio * newRange;
                double newMax = mouseValue + (1.0 - ratio) * newRange;

                // Clamp: plot window must not become smaller than the view range.
                // Allow zooming in until plotMin/Max matches viewMin/Max (1:1 display).
                newMin = Math.Min(newMin, _viewMin);
                newMax = Math.Max(newMax, _viewMax);

                _plotMin = newMin;
                _plotMax = newMax;
            }

            // Badge: zoom ratio calculation depends on mode
            // All mode: plotRange / viewRange (viewRange = global range from rangeBar)
            // Fixed/Current: plotRange / valueRange (valueRange = current frame data range)
            double baseRange = _useViewRangeAsBase
                ? Math.Max(1e-9, _viewMax - _viewMin)   // All mode: viewRange
                : Math.Max(1e-9, _valueMax - _valueMin); // Fixed/Current: valueRange
            double plotRangeRatio = Math.Max(1e-9, _plotMax - _plotMin) / baseRange;
            ShowBadge(plotRangeRatio > 1.005 ? $"Range: {plotRangeRatio:F1}x" : "Range: 1x");
            

            InvalidateVisual();
            e.Handled = true;
        }

        private void OnBadgeTimerTick(object? sender, EventArgs e)
        {
            _showBadge = false;
            _scaleBadgeRect = null;
            _badgeHideTimer?.Stop();
            InvalidateVisual();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private DragTarget GetHitTarget(double pixelX)
        {
            double w = Bounds.Width;
            if (w <= 0 || _bins == null) return DragTarget.None;

            double plotRange = Math.Max(1e-9, _plotMax - _plotMin);
            double pxMin = (_viewMin - _plotMin) / plotRange * w;
            double pxMax = (_viewMax - _plotMin) / plotRange * w;

            // Apply same clamp as used in Render for red line drawing
            double half = _boundaryPen.Thickness / 2.0;
            pxMin = Math.Clamp(pxMin, half, w - half);
            pxMax = Math.Clamp(pxMax, half, w - half);

            if (Math.Abs(pixelX - pxMin) <= HitTolerance) return DragTarget.ViewMin;
            if (Math.Abs(pixelX - pxMax) <= HitTolerance) return DragTarget.ViewMax;
            return DragTarget.None;
        }

        private void UpdateBrushCache()
        {
            if (_bins == null) { _cachedLutBrushes = null; return; }

            if (_aRgbs == null || _aRgbs.Length == 0)
            {
                _cachedLutBrushes = new IBrush[_bins.Length];
                Array.Fill(_cachedLutBrushes, Brushes.DodgerBlue);
                return;
            }

            _cachedLutBrushes = new IBrush[_bins.Length];
            double binSizeInValue = (_valueMax - _valueMin) / _bins.Length;

            for (int i = 0; i < _bins.Length; i++)
            {
                // Calculate the value at the center of this bin
                double binValueMid = _valueMin + (i + 0.5) * binSizeInValue;

                // Map bin value to LUT index
                // Values outside [_viewMin, _viewMax] are clamped to LUT edges
                double viewRange = Math.Max(1e-9, _viewMax - _viewMin);
                double t = Math.Clamp((binValueMid - _viewMin) / viewRange, 0.0, 1.0);
                int idx = (int)(t * (_aRgbs.Length - 1));

                uint argb = (uint)_aRgbs[idx];
                byte a = (byte)((argb >> 24) & 0xFF);
                byte r = (byte)((argb >> 16) & 0xFF);
                byte g = (byte)((argb >> 8) & 0xFF);
                byte b = (byte)(argb & 0xFF);
                _cachedLutBrushes[i] = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            }
        }
    }
}