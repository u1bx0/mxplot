using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>Plot rendering style for a data series.</summary>
    public enum PlotStyle
    {
        /// <summary>Connected line segments only.</summary>
        Line,
        /// <summary>Individual markers at each data point.</summary>
        Marker,
        /// <summary>Connected line segments with markers at each data point.</summary>
        MarkedLine,
    }

    /// <summary>Legend placement mode for <see cref="ProfilePlotControl"/>.</summary>
    public enum LegendPosition
    {
        /// <summary>Legend box overlaid inside the plot area, top-right corner.</summary>
        InsetTopRight,
        /// <summary>Legend row rendered below the X-axis label.</summary>
        BelowPlot,
        /// <summary>Legend is not drawn.</summary>
        None,
    }

    /// <summary>
    /// A part of <see cref="ProfilePlotControl"/> outside the plot area that shows the title,
    /// the labels and the tick numbers of an axis.
    /// </summary>
    public enum PlotAxisRegion
    {
        /// <summary>The strip above the plot area.</summary>
        Title,
        /// <summary>The strip below the plot area: X tick numbers and the X axis label.</summary>
        XAxis,
        /// <summary>The strip left of the plot area: Y tick numbers and the Y axis label.</summary>
        YAxis,
    }

    /// <summary>Immutable data series for <see cref="ProfilePlotControl"/>.</summary>
    public sealed class PlotSeries
    {
        public IReadOnlyList<(double X, double Y)> Points { get; }
        public string Name { get; }
        public PlotStyle Style { get; }
        /// <summary>Explicit color, or <see langword="null"/> to auto-assign from the palette.</summary>
        public Color? Color { get; }
        /// <summary>Line width in device-independent pixels. Default is 1.5.</summary>
        public double LineWidth { get; }

        public PlotSeries(
            IReadOnlyList<(double X, double Y)> points,
            string name = "",
            PlotStyle style = PlotStyle.Line,
            Color? color = null,
            double lineWidth = 1.5)
        {
            Points = points ?? throw new ArgumentNullException(nameof(points));
            Name = name;
            Style = style;
            Color = color;
            LineWidth = lineWidth > 0 ? lineWidth : 1.5;
        }
    }

    /// <summary>
    /// Lightweight scatter/line plot control for displaying one or more data series.
    /// Supports zoom into a selection box (left-drag right), fit (left-drag left, double-click, Home),
    /// crosshair with coordinate readout, and auto-generated legend.
    /// </summary>
    public class ProfilePlotControl : Control
    {
        // ── Default color palette (Tableau 10 subset) ─────────────────────────
        private static readonly Color[] Palette =
        [
            Color.FromRgb(31, 119, 180),
            Color.FromRgb(255, 127, 14),
            Color.FromRgb(44, 160, 44),
            Color.FromRgb(214, 39, 40),
            Color.FromRgb(148, 103, 189),
            Color.FromRgb(140, 86, 75),
            Color.FromRgb(227, 119, 194),
            Color.FromRgb(127, 127, 127),
        ];

        // ── Layout ─────────────────────────────────────────────────────────────
        private const double PadTop = 12;
        private const double PadRight = 16;
        private const double PadBottom = 8;
        private const double TickLen = 5;
        private const double TickGap = 3;
        private const double AxisLabelGap = 4;
        private const double DotRadius = 2.5;
        private const double SeriesLineWidth = 1.5;
        private const double LegendPad = 6;
        private const int DesiredTicks = 6;

        // ── Fonts ──────────────────────────────────────────────────────────────
        private static readonly Typeface TickTypeface = new("Consolas");
        private static readonly Typeface LabelTypeface = Typeface.Default;

        /// <summary>Font size for tick labels and crosshair readout.</summary>
        public double TickFontSize { get; set; } = 18;
        /// <summary>Font size for axis labels (X / Y).</summary>
        public double LabelFontSize { get; set; } = 18;
        /// <summary>Font size for legend entries.</summary>
        public double LegendFontSize { get; set; } = 18;
        /// <summary>Font size for the plot title.</summary>
        public double TitleFontSize { get; set; } = 18;

        /// <summary>Raised on the calling thread whenever the series collection is replaced via <see cref="SetData"/>.</summary>
        public event EventHandler? SeriesChanged;

        /// <summary>
        /// Raised whenever any series point data changes — via <see cref="SetData"/>,
        /// <see cref="UpdatePoints"/>, or <see cref="UpdatePointsAndFit"/>.
        /// Use this event to react to data updates without rebuilding style controls.
        /// </summary>
        public event EventHandler? DataChanged;

        /// <summary>Legend placement. Changing this property triggers a redraw.</summary>
        public LegendPosition LegendPosition
        {
            get => _legendPosition;
            set { _legendPosition = value; InvalidateVisual(); }
        }
        private LegendPosition _legendPosition = LegendPosition.InsetTopRight;

        /// <summary>Gets or sets whether the crosshair and coordinate readout are shown when the pointer is inside the plot area.</summary>
        public bool ShowCrosshair
        {
            get => _showCrosshair;
            set { _showCrosshair = value; InvalidateVisual(); }
        }
        private bool _showCrosshair = true;

        // ── Axis range lock ────────────────────────────────────────────────────
        private bool   _xAxisFixed, _yAxisFixed;
        private double _xFixedMin, _xFixedMax, _yFixedMin, _yFixedMax;
        // An Auto axis the user zoomed into with a selection box. Data updates leave its view range
        // alone; Fit (or switching the axis to Fixed / Auto) releases it.
        private bool   _xViewPinned, _yViewPinned;

        /// <summary>When <c>true</c>, locks the X view range: selection-box zoom and fit leave it alone.</summary>
        public bool XAxisFixed
        {
            get => _xAxisFixed;
            set
            {
                if (_xAxisFixed == value) 
                    return; 
                _xAxisFixed = value;
                _xViewPinned = false;
                if (value)
                {
                    _xFixedMin = _vxMin;
                    _xFixedMax = _vxMax;
                }
                else
                {
                    _vxMin = _dxMin;
                    _vxMax = _dxMax;
                }
                InvalidateVisual();
            }
        }
        /// <summary>When <c>true</c>, locks the Y view range: selection-box zoom and fit leave it alone.</summary>
        public bool YAxisFixed
        {
            get => _yAxisFixed;
            set
            {
                if (_yAxisFixed == value)
                    return;
                _yAxisFixed = value;
                _yViewPinned = false;
                if (value)
                {
                    _yFixedMin = _vyMin;
                    _yFixedMax = _vyMax;
                }
                else
                {
                    _vyMin = _dyMin;
                    _vyMax = _dyMax;
                }
                InvalidateVisual();
            }
        }
        /// <summary>Fixed minimum for the X axis (effective when <see cref="XAxisFixed"/> is <c>true</c>).</summary>
        public double XFixedMin { get => _xFixedMin; set { _xFixedMin = value; if (_xAxisFixed) { _vxMin = value; InvalidateVisual(); } } }
        /// <summary>Fixed maximum for the X axis (effective when <see cref="XAxisFixed"/> is <c>true</c>).</summary>
        public double XFixedMax { get => _xFixedMax; set { _xFixedMax = value; if (_xAxisFixed) { _vxMax = value; InvalidateVisual(); } } }
        /// <summary>Fixed minimum for the Y axis (effective when <see cref="YAxisFixed"/> is <c>true</c>).</summary>
        public double YFixedMin { get => _yFixedMin; set { _yFixedMin = value; if (_yAxisFixed) { _vyMin = value; InvalidateVisual(); } } }
        /// <summary>Fixed maximum for the Y axis (effective when <see cref="YAxisFixed"/> is <c>true</c>).</summary>
        public double YFixedMax { get => _yFixedMax; set { _yFixedMax = value; if (_yAxisFixed) { _vyMax = value; InvalidateVisual(); } } }

        // ── Static brushes / pens ──
        private static readonly IBrush BgBrush     = Brushes.White;
        private static readonly IBrush PlotBgBrush = new SolidColorBrush(Color.FromRgb(252, 252, 252));
        private static readonly IBrush TextBrush   = Brushes.Black;
        private IPen? _axisPen = null;
        private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 225)), 0.5);
        private static readonly IPen CrosshairPen = new Pen(
            new SolidColorBrush(Color.FromArgb(120, 100, 100, 100)), 1.0,
            new DashStyle([4, 3], 0));

        // ── Fit overlay ─────────────────────────────────────────────────────────
        private IReadOnlyList<(double X, double Y)>? _fitOverlayPoints;
        private string _fitOverlayName = "";
        private Color  _fitOverlayColor     = Color.FromArgb(200, 220, 50, 50);
        private double _fitOverlayLineWidth = 1.5;
        private IPen   _fitOverlayPen = new Pen(
            new SolidColorBrush(Color.FromArgb(200, 220, 50, 50)), 1.5,
            new DashStyle([5, 3], 0));

        private void RebuildFitPen() =>
            _fitOverlayPen = new Pen(
                new SolidColorBrush(_fitOverlayColor), _fitOverlayLineWidth,
                new DashStyle([5, 3], 0));

        /// <summary>Gets or sets the axis border and tick line thickness in device-independent pixels.</summary>
        public double AxisThickness
        {
            get => _axisThickness;
            set { _axisThickness = Math.Max(0.1, value); _axisPen = new Pen(Brushes.Black, _axisThickness); InvalidateVisual(); }
        }
        private double _axisThickness = 1.5;

        // ── Data ───────────────────────────────────────────────────────────────
        private readonly List<(PlotSeries Series, Color Color)> _entries = [];
        private readonly List<bool> _seriesVisible = [];
        private string _xLabel = "";
        private string _yLabel = "";
        private string _title = "";

        // ── View range (data coordinates of the visible window) ───────────────
        private double _vxMin, _vxMax, _vyMin, _vyMax;
        private double _dxMin, _dxMax, _dyMin, _dyMax;

        // ── Interaction ────────────────────────────────────────────────────────
        // Selection-box drag: dragging right zooms into the box on the Auto axes, dragging left fits them.
        private bool _isSelecting;
        private bool _selectShift;   // Shift held: only the axis the drag extends further along is zoomed
        private Point _selectStart;
        private Point _selectCurrent;
        // A drag has to cover this many pixels along an axis to count: rightwards it is a zoom box,
        // leftwards it is a fit. The same value for both leaves no stretch of a leftward drag that is
        // already a zoom box but not yet a fit.
        private const double SelectMinPx = 6;
        private bool _isPointerInside;
        private Point _lastPointerPos;
        private Rect _plotArea;
        // Bottom edge of the X tick numbers and the X axis label (the legend row lies below it).
        private double _xAxisRegionBottom;
        private readonly Cursor _axisRegionCursor = new(StandardCursorType.Hand);

        /// <summary>
        /// Raised when the title, an axis label or the tick numbers of an axis are double-clicked
        /// (see <see cref="PlotAxisRegion"/>). A double-click inside the plot area fits the view
        /// to the data instead.
        /// </summary>
        public event EventHandler<PlotAxisRegion>? AxisRegionDoubleClicked;

        // ── Fit range ──────────────────────────────────────────────────────────
        private double? _fitRangeStart, _fitRangeEnd;
        private bool _showFitRangeMarkers;
        private enum FitMarker { None, Start, End }
        private FitMarker _dragMarker, _hoverMarker;
        private readonly Cursor _markerCursor = new(StandardCursorType.SizeWestEast);
        private const double MarkerHalf = 6;       // half-diagonal of the diamond handle
        private const double MarkerHitRadius = 10;
        private int _fitSeriesIndex;
        private PlotSeries? _fitDomainSeries;      // the series the cached X domain below belongs to
        private double _fitDomainLo, _fitDomainHi;

        /// <summary>
        /// Start of the X range a curve fit uses, or <c>null</c> for the first data point. Only the
        /// markers and the shading outside the range use it; the fit itself is up to the owner.
        /// </summary>
        public double? FitRangeStart
        {
            get => _fitRangeStart;
            set { if (_fitRangeStart == value) return; _fitRangeStart = value; InvalidateVisual(); }
        }

        /// <summary>End of the X range a curve fit uses, or <c>null</c> for the last data point.</summary>
        public double? FitRangeEnd
        {
            get => _fitRangeEnd;
            set { if (_fitRangeEnd == value) return; _fitRangeEnd = value; InvalidateVisual(); }
        }

        /// <summary>
        /// When <c>true</c>, the ends of the fit range are drawn as draggable markers, and the part of
        /// the plot outside a range that was set is shaded.
        /// </summary>
        public bool ShowFitRangeMarkers
        {
            get => _showFitRangeMarkers;
            set { if (_showFitRangeMarkers == value) return; _showFitRangeMarkers = value; InvalidateVisual(); }
        }

        /// <summary>
        /// Raised when the user moves or resets a fit range marker. <see cref="FitRangeStart"/> and
        /// <see cref="FitRangeEnd"/> are already updated.
        /// </summary>
        public event EventHandler? FitRangeChanged;

        /// <summary>
        /// The fewest points the fit needs. A marker can only move to where this many points remain in the
        /// range, and with no more points than this in the series there is nothing to adjust, so the
        /// markers are not shown.
        /// </summary>
        public int FitMinimumPoints { get; set; } = 2;

        private PlotSeries? _fitSortedXSeries;
        private double[] _fitSortedX = [];

        /// <summary>The finite X values of the fit series in ascending order; cached until the series is replaced.</summary>
        private double[] GetFitSortedX()
        {
            var series = FitSeries;
            if (series == null) return [];
            if (!ReferenceEquals(series, _fitSortedXSeries))
            {
                _fitSortedX = series.Points
                    .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y))
                    .Select(p => p.X)
                    .Order()
                    .ToArray();
                _fitSortedXSeries = series;
            }
            return _fitSortedX;
        }

        private int MinFitPoints => Math.Max(2, FitMinimumPoints);

        /// <summary>Whether the fit range can be narrowed at all: the series holds more points than the fit needs.</summary>
        private bool FitRangeAdjustable => GetFitSortedX().Length > MinFitPoints;

        /// <summary>
        /// Index of the series the fit range belongs to: the markers sit on it and follow its data.
        /// Clamped to the available series.
        /// </summary>
        public int FitSeriesIndex
        {
            get => _fitSeriesIndex;
            set { if (_fitSeriesIndex == value) return; _fitSeriesIndex = value; InvalidateVisual(); }
        }

        private PlotSeries? FitSeries
        {
            get
            {
                int index = Math.Min(_fitSeriesIndex, _entries.Count - 1);
                return index < 0 ? null : _entries[index].Series;
            }
        }

        /// <summary>The X extent of the fit series' finite points; cached until the series is replaced.</summary>
        private bool TryGetFitDomain(out double lo, out double hi)
        {
            lo = hi = 0;
            var series = FitSeries;
            if (series == null) return false;
            if (!ReferenceEquals(series, _fitDomainSeries))
            {
                double min = double.PositiveInfinity, max = double.NegativeInfinity;
                foreach (var (x, y) in series.Points)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
                _fitDomainSeries = series;
                _fitDomainLo = min;
                _fitDomainHi = max;
            }
            lo = _fitDomainLo;
            hi = _fitDomainHi;
            return lo <= hi;
        }

        /// <summary>
        /// The data points that bound the fit range now: the first point at or after the start and the
        /// last point at or before the end, so a marker always sits on a point the fit uses. A range end
        /// left unset is the end of the data, and one beyond the data (it was set for data that has since
        /// changed) is held at that end, so it takes effect again if the data grows back.
        /// </summary>
        private bool TryGetFitEnds(out (double X, double Y) start, out (double X, double Y) end)
        {
            start = end = default;
            var pts = FitSeries?.Points;
            if (pts == null || !TryGetFitDomain(out double lo, out double hi)) return false;

            double s = _fitRangeStart is { } a ? Math.Clamp(a, lo, hi) : lo;
            double e = _fitRangeEnd is { } b ? Math.Clamp(b, lo, hi) : hi;
            bool hasStart = false, hasEnd = false;
            foreach (var p in pts)
            {
                if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
                if (p.X >= s && (!hasStart || p.X < start.X)) { start = p; hasStart = true; }
                if (p.X <= e && (!hasEnd || p.X > end.X)) { end = p; hasEnd = true; }
            }
            return hasStart && hasEnd;
        }

        // ── Constructors ───────────────────────────────────────────────────────

        public ProfilePlotControl()
        {
            Focusable = true;
            ClipToBounds = true;
            _axisPen = new Pen(Brushes.Black, _axisThickness);
        }

        public ProfilePlotControl(
            IReadOnlyList<PlotSeries> series,
            string xAxisLabel = "",
            string yAxisLabel = "") : this()
        {
            SetData(series, xAxisLabel, yAxisLabel);
        }

        // ── Public API ─────────────────────────────────────────────────────────

        public void SetData(IReadOnlyList<PlotSeries> series, string xLabel = "", string yLabel = "", string title = "")
        {
            _entries.Clear();
            _seriesVisible.Clear();
            _xLabel = xLabel;
            _yLabel = yLabel;
            _title = title;
            for (int i = 0; i < series.Count; i++)
            {
                var s = series[i];
                _entries.Add((s, s.Color ?? Palette[i % Palette.Length]));
                _seriesVisible.Add(true);
            }
            ComputeDataBounds();
            FitToData();
            SeriesChanged?.Invoke(this, EventArgs.Empty);
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Replaces point data for an existing series by index without changing labels,
        /// colors, or view range. Ideal for real-time / streaming updates.</summary>
        /// colors, or view range. Ideal for real-time / streaming updates.
        /// Call <see cref="FitToData"/> afterwards if auto-ranging is desired.
        /// </summary>
        public void UpdatePoints(int seriesIndex, IReadOnlyList<(double X, double Y)> points)
        {
            if ((uint)seriesIndex >= (uint)_entries.Count)
                throw new ArgumentOutOfRangeException(nameof(seriesIndex));
            var (old, color) = _entries[seriesIndex];
            _entries[seriesIndex] = (new PlotSeries(points, old.Name, old.Style, old.Color, old.LineWidth), color);
            InvalidateVisual();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Replaces point data and recomputes data bounds + fit.
        /// Equivalent to <see cref="UpdatePoints"/> followed by bounds recomputation and <see cref="FitToData"/>.
        /// </summary>
        public void UpdatePointsAndFit(int seriesIndex, IReadOnlyList<(double X, double Y)> points)
        {
            UpdatePoints(seriesIndex, points);
            ComputeDataBounds();
            FitUnpinned();
        }

        /// <summary>
        /// Sets the visible view range and data bounds explicitly, avoiding O(N) recomputation.
        /// Fastest path when the caller already knows the data extents (e.g. from data generation).
        /// Pass <see langword="null"/> for <paramref name="xRange"/> or <paramref name="yRange"/>
        /// to keep the current range on that axis.
        /// </summary>
        public void SetViewRange((double Min, double Max)? xRange, (double Min, double Max)? yRange)
        {
            if (xRange is var (xMin, xMax))
            {
                EnsureNonZeroRange(ref xMin, ref xMax);
                _dxMin = _vxMin = xMin;
                _dxMax = _vxMax = xMax;
            }
            if (yRange is var (yMin, yMax))
            {
                EnsureNonZeroRange(ref yMin, ref yMax);
                _dyMin = _vyMin = yMin;
                _dyMax = _vyMax = yMax;
            }
            InvalidateVisual();
        }

        public void FitToData()
        {
            _xViewPinned = _yViewPinned = false;
            FitUnpinned();
        }

        /// <summary>Fits every Auto axis the user has not zoomed into.</summary>
        private void FitUnpinned()
        {
            if (!_xAxisFixed && !_xViewPinned) { _vxMin = _dxMin; _vxMax = _dxMax; }
            if (!_yAxisFixed && !_yViewPinned) { _vyMin = _dyMin; _vyMax = _dyMax; }
            InvalidateVisual();
        }

        /// <summary>
        /// Computes the full layout for a canvas of size (<paramref name="w"/>, <paramref name="h"/>)
        /// and packages all state needed to reproduce one frame of rendering.
        /// Must be called on the UI thread (uses <see cref="FormattedText"/> for measurement).
        /// </summary>
        internal PlotRenderData GetRenderData(double w, double h)
        {
            static PlotRenderData Empty(double w, double h, ProfilePlotControl p) => new()
            {
                Width = w, Height = h, PlotArea = new Rect(0, 0, w, h),
                YLabelCx = 0, LegendAbsY = 0,
                XTicks = [], YTicks = [], XSpacing = 1, YSpacing = 1,
                VxMin = p._vxMin, VxMax = p._vxMax, VyMin = p._vyMin, VyMax = p._vyMax,
                Title = p._title, XLabel = p._xLabel, YLabel = p._yLabel,
                TickFontSize = p.TickFontSize, LabelFontSize = p.LabelFontSize,
                TitleFontSize = p.TitleFontSize, LegendFontSize = p.LegendFontSize,
                AxisThickness = p._axisThickness, LegendPosition = p._legendPosition,
                Entries = [],
            };
            if (_entries.Count == 0 || _vxMax <= _vxMin || _vyMax <= _vyMin)
                return Empty(w, h, this);

            var (xTicks, xSp) = GenerateTicks(_vxMin, _vxMax, DesiredTicks);
            var (yTicks, ySp) = GenerateTicks(_vyMin, _vyMax, DesiredTicks);

            double titleH    = _title.Length  > 0 ? MakeTitle(_title).Height   + AxisLabelGap : 0;
            double maxYTW    = yTicks.Count   > 0 ? yTicks.Max(t => MakeTick(FormatTick(t, ySp)).Width) : 0;
            double yLabelW   = _yLabel.Length > 0 ? MakeLabel(_yLabel).Height  + AxisLabelGap : 0;
            double xLabelH   = _xLabel.Length > 0 ? MakeLabel(_xLabel).Height  + AxisLabelGap : 0;
            bool   hasNamed  = _entries.Any(e => e.Series.Name.Length > 0);
            double legendBelowH = (_legendPosition == LegendPosition.BelowPlot && hasNamed)
                ? LegendFontSize + 6 + LegendPad * 2 + 4 : 0;

            double mL = yLabelW + maxYTW + TickGap + TickLen + 4;
            double mT = PadTop + titleH;
            double mB = TickLen + TickGap + TickFontSize + 2 + xLabelH + legendBelowH + PadBottom;
            var pa = new Rect(mL, mT, Math.Max(1, w - mL - PadRight), Math.Max(1, h - mT - mB));

            return new PlotRenderData
            {
                Width = w, Height = h, PlotArea = pa,
                YLabelCx   = yLabelW / 2,
                LegendAbsY = pa.Bottom + TickLen + TickGap + TickFontSize + 2 + xLabelH + 2,
                XTicks = xTicks, YTicks = yTicks, XSpacing = xSp, YSpacing = ySp,
                VxMin = _vxMin, VxMax = _vxMax, VyMin = _vyMin, VyMax = _vyMax,
                Title = _title, XLabel = _xLabel, YLabel = _yLabel,
                TickFontSize = TickFontSize, LabelFontSize = LabelFontSize,
                TitleFontSize = TitleFontSize, LegendFontSize = LegendFontSize,
                AxisThickness = _axisThickness, LegendPosition = _legendPosition,
                Entries = _entries.ToList(),
                FitOverlay = _fitOverlayPoints,
                FitOverlayName = _fitOverlayName,
                FitOverlayColor = _fitOverlayColor,
                FitOverlayLineWidth = _fitOverlayLineWidth,
            };
        }

        /// <summary>
        /// Writes the current plot view as an SVG document to <paramref name="stream"/>.
        /// The crosshair is never included. Must be called on the UI thread.
        /// </summary>
        public void RenderToSvg(Stream stream, double width, double height)
            => PlotSvgWriter.Write(stream, GetRenderData(width, height));

        // Data accessors

        /// <summary>
        /// Changes the <see cref="PlotStyle"/> of a single series by index and redraws.
        /// </summary>
        public void SetSeriesStyle(int index, PlotStyle style)
        {
            if ((uint)index >= (uint)_entries.Count) return;
            var (old, color) = _entries[index];
            _entries[index] = (new PlotSeries(old.Points, old.Name, style, old.Color, old.LineWidth), color);
            InvalidateVisual();
        }

        /// <summary>
        /// Changes the name of a single series by index and redraws. Raises
        /// <see cref="SeriesChanged"/> so that UI listing the series names can refresh.
        /// </summary>
        public void SetSeriesName(int index, string name)
        {
            if ((uint)index >= (uint)_entries.Count) return;
            var (old, color) = _entries[index];
            _entries[index] = (new PlotSeries(old.Points, name ?? "", old.Style, old.Color, old.LineWidth), color);
            InvalidateVisual();
            SeriesChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Returns the resolved display color for the series at <paramref name="index"/>.</summary>
        public Color GetSeriesColor(int index) => _entries[index].Color;

        /// <summary>Changes the color of a single series by index and redraws.</summary>
        public void SetSeriesColor(int index, Color color)
        {
            if ((uint)index >= (uint)_entries.Count) return;
            var (old, _) = _entries[index];
            _entries[index] = (new PlotSeries(old.Points, old.Name, old.Style, color, old.LineWidth), color);
            InvalidateVisual();
        }

        /// <summary>Changes the line width of a single series by index and redraws.</summary>
        public void SetSeriesLineWidth(int index, double width)
        {
            if ((uint)index >= (uint)_entries.Count) return;
            var (old, color) = _entries[index];
            _entries[index] = (new PlotSeries(old.Points, old.Name, old.Style, old.Color, width), color);
            InvalidateVisual();
        }

        /// <summary>Gets the visibility state of a series by index.</summary>
        public bool GetSeriesVisible(int index) =>
            (uint)index < (uint)_seriesVisible.Count ? _seriesVisible[index] : true;

        /// <summary>Sets the visibility of a single series by index and redraws.</summary>
        public void SetSeriesVisible(int index, bool visible)
        {
            if ((uint)index >= (uint)_seriesVisible.Count) return;
            _seriesVisible[index] = visible;
            InvalidateVisual();
        }

        /// <summary>
        /// Sets or clears the fit overlay curve rendered on top of the data series.
        /// Does <b>not</b> raise <see cref="SeriesChanged"/>.
        /// </summary>
        public void SetFitOverlay(IReadOnlyList<(double X, double Y)>? points, string name = "")
        {
            _fitOverlayPoints = points;
            _fitOverlayName   = points == null ? "" : name;
            InvalidateVisual();
        }

        /// <summary>Returns the current fit overlay points, or <see langword="null"/> if none.</summary>
        public IReadOnlyList<(double X, double Y)>? FitOverlayPoints => _fitOverlayPoints;
        /// <summary>Returns the legend label for the fit overlay (empty when none is set).</summary>
        public string FitOverlayName => _fitOverlayName;
        /// <summary>Returns the current fit overlay color.</summary>
        public Color  FitOverlayColor     => _fitOverlayColor;
        /// <summary>Returns the current fit overlay line width.</summary>
        public double FitOverlayLineWidth => _fitOverlayLineWidth;

        /// <summary>Updates the fit overlay color and line width without clearing the overlay.</summary>
        public void SetFitOverlayStyle(Color color, double lineWidth)
        {
            _fitOverlayColor     = color;
            _fitOverlayLineWidth = lineWidth;
            RebuildFitPen();
            InvalidateVisual();
        }

        /// <summary>Gets a snapshot of the current data series.</summary>
        public IReadOnlyList<PlotSeries> Series => _entries.Select(e => e.Series).ToList();

        /// <summary>Gets or sets the X-axis label.</summary>
        public string XLabel    { get => _xLabel;  set { _xLabel  = value ?? ""; InvalidateVisual(); } }
        /// <summary>Gets or sets the Y-axis label.</summary>
        public string YLabel    { get => _yLabel;  set { _yLabel  = value ?? ""; InvalidateVisual(); } }
        /// <summary>Gets or sets the plot title.</summary>
        public string PlotTitle { get => _title;   set { _title   = value ?? ""; InvalidateVisual(); } }

        // ── Data bounds ────────────────────────────────────────────────────────

        private void ComputeDataBounds()
        {
            _dxMin = _dxMax = _dyMin = _dyMax = 0;
            bool first = true;
            foreach (var (s, _) in _entries)
            {
                foreach (var (x, y) in s.Points)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    if (first) { _dxMin = _dxMax = x; _dyMin = _dyMax = y; first = false; }
                    else
                    {
                        if (x < _dxMin) _dxMin = x; if (x > _dxMax) _dxMax = x;
                        if (y < _dyMin) _dyMin = y; if (y > _dyMax) _dyMax = y;
                    }
                }
            }
            EnsureNonZeroRange(ref _dxMin, ref _dxMax);
            EnsureNonZeroRange(ref _dyMin, ref _dyMax);
            double px = (_dxMax - _dxMin) * 0.05, py = (_dyMax - _dyMin) * 0.05;
            _dxMin -= px; _dxMax += px; _dyMin -= py; _dyMax += py;
        }

        private static void EnsureNonZeroRange(ref double min, ref double max)
        {
            if (max - min >= 1e-15) return;
            double c = (max + min) / 2;
            double half = Math.Max(Math.Abs(c) * 0.5, 0.5);
            min = c - half; max = c + half;
        }

        // ── Coordinate conversion ──────────────────────────────────────────────

        private Point DataToScreen(double dx, double dy, Rect pa)
        {
            double xr = _vxMax - _vxMin, yr = _vyMax - _vyMin;
            double sx = xr > 0 ? pa.X + (dx - _vxMin) / xr * pa.Width : pa.X + pa.Width / 2;
            double sy = yr > 0 ? pa.Bottom - (dy - _vyMin) / yr * pa.Height : pa.Y + pa.Height / 2;
            return new Point(sx, sy);
        }

        private (double x, double y) ScreenToData(Point sp, Rect pa)
        {
            double xr = _vxMax - _vxMin, yr = _vyMax - _vyMin;
            double dx = pa.Width > 0 ? _vxMin + (sp.X - pa.X) / pa.Width * xr : _vxMin;
            double dy = pa.Height > 0 ? _vyMin + (pa.Bottom - sp.Y) / pa.Height * yr : _vyMin;
            return (dx, dy);
        }

        // ── Nice numbers (Heckbert) ───────────────────────────────────────────

        private static double NiceNum(double range, bool round)
        {
            if (range <= 0 || !double.IsFinite(range)) return 1;
            double exp = Math.Floor(Math.Log10(range));
            double frac = range / Math.Pow(10, exp);
            double nice = round
                ? (frac < 1.5 ? 1 : frac < 3 ? 2 : frac < 7 ? 5 : 10)
                : (frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10);
            return nice * Math.Pow(10, exp);
        }

        private static (List<double> ticks, double spacing) GenerateTicks(double min, double max, int desired)
        {
            double range = NiceNum(max - min, false);
            double spacing = NiceNum(range / Math.Max(desired - 1, 1), true);
            double tickStart = Math.Ceiling(min / spacing) * spacing;
            var ticks = new List<double>();
            for (double t = tickStart; t <= max + spacing * 1e-6; t += spacing)
            {
                double rounded = Math.Round(t / spacing) * spacing;
                if (rounded >= min - spacing * 1e-6 && rounded <= max + spacing * 1e-6)
                    ticks.Add(rounded);
                if (ticks.Count > 200) break;
            }
            return (ticks, spacing);
        }

        private static string FormatTick(double value, double spacing)
        {
            if (Math.Abs(value) < spacing * 1e-10) return "0";
            double absRef = Math.Max(Math.Abs(value), spacing);
            if (absRef >= 1e6 || absRef < 1e-3)
                return value.ToString("G4", CultureInfo.InvariantCulture);
            int dec = Math.Max(0, -(int)Math.Floor(Math.Log10(spacing) - 0.001));
            return value.ToString($"F{Math.Min(dec, 10)}", CultureInfo.InvariantCulture);
        }

        // ── Text helpers ───────────────────────────────────────────────────────

        private FormattedText MakeTick(string text) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TickTypeface, TickFontSize, TextBrush);

        private FormattedText MakeLabel(string text) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, TextBrush);

        private FormattedText MakeTitle(string text) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, TitleFontSize, TextBrush);

        // ── Render ─────────────────────────────────────────────────────────────

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            ctx.FillRectangle(BgBrush, new Rect(Bounds.Size));
            if (_entries.Count == 0 || _vxMax <= _vxMin || _vyMax <= _vyMin) return;

            var (xTicks, xSp) = GenerateTicks(_vxMin, _vxMax, DesiredTicks);
            var (yTicks, ySp) = GenerateTicks(_vyMin, _vyMax, DesiredTicks);

            // Title height
            double titleH = _title.Length > 0 ? MakeTitle(_title).Height + AxisLabelGap : 0;

            // Measure Y-tick labels for left margin
            double maxYTW = 0;
            foreach (var t in yTicks)
            {
                var ft = MakeTick(FormatTick(t, ySp));
                if (ft.Width > maxYTW) maxYTW = ft.Width;
            }
            double yLabelW = _yLabel.Length > 0 ? MakeLabel(_yLabel).Height + AxisLabelGap : 0;
            double xLabelH = _xLabel.Length > 0 ? MakeLabel(_xLabel).Height + AxisLabelGap : 0;

            bool hasNamedEntries = _entries.Any(e => e.Series.Name.Length > 0);
            double legendBelowH = (_legendPosition == LegendPosition.BelowPlot && hasNamedEntries)
                ? LegendFontSize + 6 + LegendPad * 2 + 4 : 0;

            double mL = yLabelW + maxYTW + TickGap + TickLen + 4;
            double mT = PadTop + titleH;
            double mB = TickLen + TickGap + TickFontSize + 2 + xLabelH + legendBelowH + PadBottom;
            var pa = new Rect(mL, mT, Math.Max(1, w - mL - PadRight), Math.Max(1, h - mT - mB));
            _plotArea = pa;
            _xAxisRegionBottom = pa.Bottom + TickLen + TickGap + TickFontSize + 2 + xLabelH;
            if (pa.Width < 10 || pa.Height < 10) return;

            // Plot area background
            ctx.FillRectangle(PlotBgBrush, pa);

            // Grid lines
            foreach (var t in xTicks)
            {
                double sx = pa.X + (t - _vxMin) / (_vxMax - _vxMin) * pa.Width;
                if (sx >= pa.X && sx <= pa.Right)
                    ctx.DrawLine(GridPen, new Point(sx, pa.Y), new Point(sx, pa.Bottom));
            }
            foreach (var t in yTicks)
            {
                double sy = pa.Bottom - (t - _vyMin) / (_vyMax - _vyMin) * pa.Height;
                if (sy >= pa.Y && sy <= pa.Bottom)
                    ctx.DrawLine(GridPen, new Point(pa.X, sy), new Point(pa.Right, sy));
            }

            // Data series (clipped)
            using (ctx.PushClip(pa))
            {
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (_seriesVisible[i])
                    {
                        var (series, color) = _entries[i];
                        DrawSeries(ctx, series, color, pa);
                    }
                }
                if (_fitOverlayPoints != null && _fitOverlayPoints.Count > 1)
                    DrawFitOverlay(ctx, pa);
            }

            // Axis border
            ctx.DrawRectangle(null, _axisPen, pa);
            if (_showFitRangeMarkers)
                DrawFitRangeMarkers(ctx, pa);

            // X ticks and labels
            foreach (var t in xTicks)
            {
                double sx = pa.X + (t - _vxMin) / (_vxMax - _vxMin) * pa.Width;
                if (sx < pa.X - 1 || sx > pa.Right + 1) continue;
                ctx.DrawLine(_axisPen, new Point(sx, pa.Bottom), new Point(sx, pa.Bottom + TickLen));
                var ft = MakeTick(FormatTick(t, xSp));
                ctx.DrawText(ft, new Point(sx - ft.Width / 2, pa.Bottom + TickLen + TickGap));
            }

            // Y ticks and labels
            foreach (var t in yTicks)
            {
                double sy = pa.Bottom - (t - _vyMin) / (_vyMax - _vyMin) * pa.Height;
                if (sy < pa.Y - 1 || sy > pa.Bottom + 1) continue;
                ctx.DrawLine(_axisPen, new Point(pa.X - TickLen, sy), new Point(pa.X, sy));
                var ft = MakeTick(FormatTick(t, ySp));
                ctx.DrawText(ft, new Point(pa.X - TickLen - TickGap - ft.Width, sy - ft.Height / 2));
            }

            // X axis label
            if (_xLabel.Length > 0)
            {
                var ft = MakeLabel(_xLabel);
                ctx.DrawText(ft, new Point(
                    pa.X + (pa.Width - ft.Width) / 2,
                    pa.Bottom + TickLen + TickGap + TickFontSize + 2 + AxisLabelGap));
            }

            // Y axis label (rotated 90° CCW)
            if (_yLabel.Length > 0)
            {
                var ft = MakeLabel(_yLabel);
                double cx = yLabelW / 2;
                double cy = pa.Y + pa.Height / 2;
                // CCW 90°: (1,0)→(0,-1)
                var mat = Matrix.CreateTranslation(-cx, -cy)
                        * new Matrix(0, -1, 1, 0, 0, 0)
                        * Matrix.CreateTranslation(cx, cy);
                using (ctx.PushTransform(mat))
                    ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
            }

            double legendAbsY = pa.Bottom + TickLen + TickGap + TickFontSize + 2 + xLabelH + 2;
            if (_legendPosition != LegendPosition.None) DrawLegend(ctx, pa, legendAbsY);

            // Title
            if (_title.Length > 0)
            {
                var ft = MakeTitle(_title);
                ctx.DrawText(ft, new Point(pa.X + (pa.Width - ft.Width) / 2, PadTop));
            }

            if (_isSelecting)
                DrawSelectionBox(ctx, pa);
            else if (_isPointerInside && _showCrosshair && pa.Contains(_lastPointerPos))
                DrawCrosshair(ctx, pa, xSp, ySp);
        }

        // ── Series rendering ───────────────────────────────────────────────────

        private void DrawSeries(DrawingContext ctx, PlotSeries s, Color color, Rect pa)
        {
            if (s.Points.Count == 0) return;
            var brush = new SolidColorBrush(color);
            var pen = new Pen(brush, s.LineWidth);

            if (s.Style is PlotStyle.Line or PlotStyle.MarkedLine && s.Points.Count > 1)
            {
                var geo = new StreamGeometry();
                using (var sgc = geo.Open())
                {
                    bool started = false;
                    foreach (var (x, y) in s.Points)
                    {
                        if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                        var sp = DataToScreen(x, y, pa);
                        if (!started) { sgc.BeginFigure(sp, false); started = true; }
                        else sgc.LineTo(sp);
                    }
                }
                ctx.DrawGeometry(null, pen, geo);
            }

            if (s.Style is PlotStyle.Marker or PlotStyle.MarkedLine)
            {
                double dotR = s.LineWidth + 1.0;
                foreach (var (x, y) in s.Points)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    var sp = DataToScreen(x, y, pa);
                    ctx.DrawEllipse(brush, null, sp, dotR, dotR);
                }
            }
        }

        private void DrawFitOverlay(DrawingContext ctx, Rect pa)
        {
            if (_fitOverlayPoints == null || _fitOverlayPoints.Count < 2) return;
            var geo = new StreamGeometry();
            using (var sgc = geo.Open())
            {
                bool started = false;
                foreach (var (x, y) in _fitOverlayPoints)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    var sp = DataToScreen(x, y, pa);
                    if (!started) { sgc.BeginFigure(sp, false); started = true; }
                    else sgc.LineTo(sp);
                }
            }
            ctx.DrawGeometry(null, _fitOverlayPen, geo);
        }

        // ── Legend ──────────────────────────────────────────────────────────────

        private void DrawLegend(DrawingContext ctx, Rect pa, double legendAbsY)
        {
            var named = new List<(PlotSeries Series, Color Color)>();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_seriesVisible[i] && _entries[i].Series.Name.Length > 0)
                    named.Add(_entries[i]);
            }
            bool hasFit = _fitOverlayPoints is { Count: > 0 } && _fitOverlayName.Length > 0;
            if (named.Count == 0 && !hasFit) return;

            const double iconW = 16, gap = 5;
            double itemH  = LegendFontSize + 6;
            int totalItems = named.Count + (hasFit ? 1 : 0);
            var texts = named.Select(e => new FormattedText(e.Series.Name, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, TickTypeface, LegendFontSize, TextBrush)).ToList();
            FormattedText? fitText = hasFit
                ? new FormattedText(_fitOverlayName, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, TickTypeface, LegendFontSize, TextBrush)
                : null;
            IPen? fitDashPen = hasFit ? _fitOverlayPen : null;

            var boxBrush  = new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));
            var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 0.5);

            void DrawIcon(double ix, double midY, PlotStyle style, Color color, double lineWidth)
            {
                if (style is PlotStyle.Line or PlotStyle.MarkedLine)
                    ctx.DrawLine(new Pen(new SolidColorBrush(color), Math.Min(lineWidth, 3)),
                        new Point(ix, midY), new Point(ix + iconW, midY));
                if (style is PlotStyle.Marker or PlotStyle.MarkedLine)
                {
                    double iconDotR = Math.Min(lineWidth + 1.0, 4.5);
                    ctx.DrawEllipse(new SolidColorBrush(color), null, new Point(ix + iconW / 2, midY), iconDotR, iconDotR);
                }
            }

            if (_legendPosition == LegendPosition.BelowPlot)
            {
                const double itemGap = 12;
                double totalItemW = named.Count * (iconW + gap) + texts.Sum(t => t.Width)
                                  + Math.Max(0, named.Count - 1) * itemGap;
                if (hasFit) totalItemW += (named.Count > 0 ? itemGap : 0) + iconW + gap + fitText!.Width;
                double boxW = LegendPad * 2 + totalItemW;
                double boxH = LegendPad * 2 + itemH;
                double bx = pa.X + (pa.Width - boxW) / 2;
                double by = legendAbsY;
                ctx.FillRectangle(boxBrush, new Rect(bx, by, boxW, boxH), 3);
                ctx.DrawRectangle(null, borderPen, new Rect(bx, by, boxW, boxH), 3, 3);
                double ix = bx + LegendPad;
                double midY = by + LegendPad + itemH / 2;
                for (int i = 0; i < named.Count; i++)
                {
                    var (s, color) = named[i];
                    var lft = texts[i];
                    DrawIcon(ix, midY, s.Style, color, s.LineWidth);
                    ctx.DrawText(lft, new Point(ix + iconW + gap, by + LegendPad + (itemH - lft.Height) / 2));
                    ix += iconW + gap + lft.Width + (i < named.Count - 1 || hasFit ? itemGap : 0);
                }
                if (hasFit)
                {
                    ctx.DrawLine(fitDashPen!, new Point(ix, midY), new Point(ix + iconW, midY));
                    ctx.DrawText(fitText!, new Point(ix + iconW + gap, by + LegendPad + (itemH - fitText!.Height) / 2));
                }
            }
            else
            {
                const double itemGap = 3;
                double maxTW = texts.Count > 0 ? texts.Max(t => t.Width) : 0;
                if (fitText != null) maxTW = Math.Max(maxTW, fitText.Width);
                double boxW = LegendPad * 2 + iconW + gap + maxTW;
                double boxH = LegendPad * 2 + totalItems * itemH + Math.Max(0, totalItems - 1) * itemGap;
                double bx = pa.Right - boxW - 8, by = pa.Y + 8;
                ctx.FillRectangle(boxBrush, new Rect(bx, by, boxW, boxH), 3);
                ctx.DrawRectangle(null, borderPen, new Rect(bx, by, boxW, boxH), 3, 3);
                double y = by + LegendPad;
                for (int i = 0; i < named.Count; i++)
                {
                    var (s, color) = named[i];
                    double ix = bx + LegendPad;
                    DrawIcon(ix, y + itemH / 2, s.Style, color, s.LineWidth);
                    ctx.DrawText(texts[i], new Point(ix + iconW + gap, y + (itemH - texts[i].Height) / 2));
                    y += itemH + itemGap;
                }
                if (hasFit)
                {
                    double ix = bx + LegendPad;
                    ctx.DrawLine(fitDashPen!, new Point(ix, y + itemH / 2), new Point(ix + iconW, y + itemH / 2));
                    ctx.DrawText(fitText!, new Point(ix + iconW + gap, y + (itemH - fitText!.Height) / 2));
                }
            }
        }

        // ── Crosshair ─────────────────────────────────────────────────────────

        private void DrawCrosshair(DrawingContext ctx, Rect pa, double xSp, double ySp)
        {
            var (dx, dy) = ScreenToData(_lastPointerPos, pa);

            using (ctx.PushClip(pa))
            {
                ctx.DrawLine(CrosshairPen,
                    new Point(_lastPointerPos.X, pa.Y), new Point(_lastPointerPos.X, pa.Bottom));
                ctx.DrawLine(CrosshairPen,
                    new Point(pa.X, _lastPointerPos.Y), new Point(pa.Right, _lastPointerPos.Y));
            }

            string label = $"({FormatTick(dx, xSp / 10)}, {FormatTick(dy, ySp / 10)})";
            var ft = MakeTick(label);
            double lx = _lastPointerPos.X + 10;
            double ly = _lastPointerPos.Y - ft.Height - 6;
            if (lx + ft.Width + 4 > pa.Right) lx = _lastPointerPos.X - ft.Width - 10;
            if (ly < pa.Y) ly = _lastPointerPos.Y + 6;

            ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)),
                new Rect(lx - 3, ly - 2, ft.Width + 6, ft.Height + 4), 2);
            ctx.DrawText(ft, new Point(lx, ly));
        }

        // ── Pointer events ─────────────────────────────────────────────────────

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Focus();
            var pos = e.GetPosition(this);
            if (e.ClickCount == 2)
            {
                var marker = HitMarker(pos);
                if (marker != FitMarker.None)
                {
                    // Double-clicking a marker takes that end of the fit range back to the end of the data.
                    if (marker == FitMarker.Start) _fitRangeStart = null; else _fitRangeEnd = null;
                    InvalidateVisual();
                    FitRangeChanged?.Invoke(this, EventArgs.Empty);
                }
                else if (HitAxisRegion(pos) is { } region) AxisRegionDoubleClicked?.Invoke(this, region);
                else if (_plotArea.Contains(pos)) FitToData();
                e.Handled = true;
                return;
            }
            var grabbed = HitMarker(pos);
            if (grabbed != FitMarker.None)
            {
                _dragMarker = grabbed;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            if (!_plotArea.Contains(pos) || (_xAxisFixed && _yAxisFixed)) return;

            _isSelecting = true;
            _selectShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _selectStart = _selectCurrent = pos;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var pos = e.GetPosition(this);
            _lastPointerPos = pos;
            if (_dragMarker != FitMarker.None)
            {
                DragMarkerTo(pos.X);
                Cursor = _markerCursor;
            }
            else if (_isSelecting)
            {
                _selectCurrent = ClampToPlotArea(pos);
                _selectShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                Cursor = null;
            }
            else
            {
                _hoverMarker = HitMarker(pos);
                Cursor = _hoverMarker != FitMarker.None ? _markerCursor
                    : HitAxisRegion(pos) != null ? _axisRegionCursor : null;
            }
            InvalidateVisual();
        }

        /// <summary>
        /// The title or axis region under <paramref name="p"/>, or <c>null</c> over the plot area,
        /// the legend and the margins that hold no axis text. The title region is the whole strip
        /// above the plot area, so a title can be added while there is none.
        /// </summary>
        private PlotAxisRegion? HitAxisRegion(Point p)
        {
            var pa = _plotArea;
            if (pa.Width <= 0 || pa.Height <= 0 || pa.Contains(p)) return null;
            if (p.Y < pa.Top) return PlotAxisRegion.Title;
            if (p.Y <= pa.Bottom) return p.X < pa.Left ? PlotAxisRegion.YAxis : null;
            if (p.Y <= _xAxisRegionBottom && p.X >= pa.Left && p.X <= pa.Right) return PlotAxisRegion.XAxis;
            return null;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_dragMarker != FitMarker.None)
            {
                _dragMarker = FitMarker.None;
                e.Pointer.Capture(null);
                InvalidateVisual();
            }
            else if (_isSelecting)
            {
                _selectCurrent = ClampToPlotArea(e.GetPosition(this));
                _selectShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                _isSelecting = false;
                e.Pointer.Capture(null);
                ApplySelection();
            }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            // Capture lost without a release (e.g. focus taken away): drop the selection unapplied.
            if (_dragMarker != FitMarker.None)
            {
                _dragMarker = FitMarker.None;
                InvalidateVisual();
            }
            if (_isSelecting)
            {
                _isSelecting = false;
                InvalidateVisual();
            }
            base.OnPointerCaptureLost(e);
        }

        // ── Selection box ──────────────────────────────────────────────────────

        private Point ClampToPlotArea(Point p) => new(
            Math.Clamp(p.X, _plotArea.Left, _plotArea.Right),
            Math.Clamp(p.Y, _plotArea.Top, _plotArea.Bottom));

        /// <summary>
        /// A drag that ends left of where it started asks to fit the Auto axes instead of zooming.
        /// With Shift held a drag always zooms.
        /// </summary>
        private bool IsFitDrag(Point start, Point current) =>
            !_selectShift && start.X - current.X >= SelectMinPx;

        /// <summary>
        /// The part of the plot area a zoom drag selects. Every Auto axis takes part, or with Shift held
        /// only the one the drag extends further along; a Fixed axis is selected over its whole length.
        /// <paramref name="useX"/> / <paramref name="useY"/> tell which axes would be zoomed.
        /// Returns <c>false</c> when nothing would be zoomed: no axis takes part, or the drag covers an
        /// axis that does by less than <see cref="SelectMinPx"/>. <paramref name="box"/> is then just the
        /// rectangle dragged.
        /// </summary>
        private bool TryGetZoomBox(Point start, Point current, out Rect box, out bool useX, out bool useY)
        {
            var pa = _plotArea;
            double dx = Math.Abs(current.X - start.X), dy = Math.Abs(current.Y - start.Y);
            useX = !_xAxisFixed;
            useY = !_yAxisFixed;
            if (_selectShift)
            {
                bool xDominant = dx >= dy;
                useX &= xDominant;
                useY &= !xDominant;
            }

            var dragged = new Rect(
                new Point(Math.Min(start.X, current.X), Math.Min(start.Y, current.Y)),
                new Point(Math.Max(start.X, current.X), Math.Max(start.Y, current.Y)));
            bool large = (useX || useY)
                && (!useX || dx >= SelectMinPx)
                && (!useY || dy >= SelectMinPx);
            if (!large)
            {
                box = dragged;
                return false;
            }
            box = new Rect(
                new Point(useX ? dragged.Left : pa.Left, useY ? dragged.Top : pa.Top),
                new Point(useX ? dragged.Right : pa.Right, useY ? dragged.Bottom : pa.Bottom));
            return true;
        }

        /// <summary>Ends a selection drag: zooms the Auto axes into the box, or fits them.</summary>
        private void ApplySelection()
        {
            if (IsFitDrag(_selectStart, _selectCurrent))
            {
                FitToData();
            }
            else if (TryGetZoomBox(_selectStart, _selectCurrent, out var box, out bool useX, out bool useY))
            {
                var (x1, y1) = ScreenToData(new Point(box.Left, box.Bottom), _plotArea);
                var (x2, y2) = ScreenToData(new Point(box.Right, box.Top), _plotArea);
                if (useX)
                {
                    _vxMin = Math.Min(x1, x2); _vxMax = Math.Max(x1, x2);
                    _xViewPinned = true;
                }
                if (useY)
                {
                    _vyMin = Math.Min(y1, y2); _vyMax = Math.Max(y1, y2);
                    _yViewPinned = true;
                }
            }
            InvalidateVisual();
        }

        private static readonly IBrush ZoomShadeBrush = new SolidColorBrush(Color.FromArgb(70, 60, 110, 200));
        // Every box edge is a white line under a dashed dark one, so it reads against both the
        // shaded and the clear side.
        private static readonly IPen SelectionUnderPen = new Pen(Brushes.White, 3);
        private static readonly IPen ZoomBoxPen = new Pen(
            new SolidColorBrush(Color.FromRgb(25, 55, 140)), 1, new DashStyle([4, 3], 0));
        private static readonly IPen FitBoxPen = new Pen(
            new SolidColorBrush(Color.FromRgb(90, 90, 90)), 1, new DashStyle([4, 3], 0));

        /// <summary>
        /// Draws the selection box. A drag that would zoom shades the plot area outside the box; a fit
        /// drag, and a drag still too small to zoom, only outline the area they cover, since releasing
        /// there zooms nowhere.
        /// </summary>
        private void DrawSelectionBox(DrawingContext ctx, Rect pa)
        {
            bool fit = IsFitDrag(_selectStart, _selectCurrent);
            Rect box;
            bool zoom = false;
            if (fit)
            {
                box = new Rect(
                    new Point(Math.Min(_selectStart.X, _selectCurrent.X), Math.Min(_selectStart.Y, _selectCurrent.Y)),
                    new Point(Math.Max(_selectStart.X, _selectCurrent.X), Math.Max(_selectStart.Y, _selectCurrent.Y)));
            }
            else
            {
                zoom = TryGetZoomBox(_selectStart, _selectCurrent, out box, out _, out _);
            }

            using (ctx.PushClip(pa))
            {
                if (zoom)
                {
                    ctx.FillRectangle(ZoomShadeBrush, new Rect(pa.Left, pa.Top, pa.Width, box.Top - pa.Top));
                    ctx.FillRectangle(ZoomShadeBrush, new Rect(pa.Left, box.Bottom, pa.Width, pa.Bottom - box.Bottom));
                    ctx.FillRectangle(ZoomShadeBrush, new Rect(pa.Left, box.Top, box.Left - pa.Left, box.Height));
                    ctx.FillRectangle(ZoomShadeBrush, new Rect(box.Right, box.Top, pa.Right - box.Right, box.Height));
                }
                ctx.DrawRectangle(null, SelectionUnderPen, box);
                ctx.DrawRectangle(null, fit ? FitBoxPen : ZoomBoxPen, box);
            }
            DrawSelectionHint(ctx, pa);
        }

        // ── Fit range markers ──────────────────────────────────────────────────

        private static readonly IPen FitRangeHandlePen = new Pen(new SolidColorBrush(Color.FromRgb(40, 40, 40)), 1);

        /// <summary>
        /// The screen position of a marker: on the data point that ends the fit range, so it follows the
        /// data. It is held inside the plot area, so a marker whose point is out of the Y range stays reachable.
        /// Returns <c>false</c> when the marker is outside the X range shown.
        /// </summary>
        private bool TryGetMarkerPosition(FitMarker marker, out Point center)
        {
            center = default;
            var pa = _plotArea;
            if (pa.Width <= 0 || !TryGetFitEnds(out var start, out var end)) return false;

            var point = marker == FitMarker.Start ? start : end;
            var screen = DataToScreen(point.X, point.Y, pa);
            if (screen.X < pa.Left || screen.X > pa.Right) return false;

            double y = double.IsFinite(screen.Y) ? screen.Y : pa.Top;
            y = pa.Height >= 2 * MarkerHalf
                ? Math.Clamp(y, pa.Top + MarkerHalf, pa.Bottom - MarkerHalf)
                : pa.Top + pa.Height / 2;
            center = new Point(screen.X, y);
            return true;
        }

        /// <summary>
        /// Draws each end of the fit range as a diamond handle on the fit series, with a dashed vertical
        /// line through it, in the colour of the fit curve. The X value shows while a marker is hovered or dragged.
        /// </summary>
        private void DrawFitRangeMarkers(DrawingContext ctx, Rect pa)
        {
            if (_entries.Count == 0 || pa.Width <= 0 || !FitRangeAdjustable
                || !TryGetFitEnds(out var start, out var end)) return;
            var color = _fitOverlayColor;
            var linePen = new Pen(new SolidColorBrush(Color.FromArgb(200, color.R, color.G, color.B)), 1, new DashStyle([4, 3], 0));
            var fill = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));

            foreach (var marker in new[] { FitMarker.Start, FitMarker.End })
            {
                if (!TryGetMarkerPosition(marker, out var c)) continue;

                bool active = marker == _hoverMarker || marker == _dragMarker;
                double h = active ? MarkerHalf + 1.5 : MarkerHalf;

                ctx.DrawLine(linePen, new Point(c.X, pa.Top), new Point(c.X, pa.Bottom));
                var diamond = new StreamGeometry();
                using (var g = diamond.Open())
                {
                    g.BeginFigure(new Point(c.X, c.Y - h), true);
                    g.LineTo(new Point(c.X + h, c.Y));
                    g.LineTo(new Point(c.X, c.Y + h));
                    g.LineTo(new Point(c.X - h, c.Y));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(fill, FitRangeHandlePen, diamond);

                if (!active) continue;
                double value = (marker == FitMarker.Start ? start : end).X;
                var ft = new FormattedText(value.ToString("G5", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, LabelTypeface, 11, fill);
                double tx = c.X + h + 3;
                if (tx + ft.Width > pa.Right) tx = c.X - h - 3 - ft.Width;
                ctx.DrawText(ft, new Point(tx, c.Y - ft.Height / 2));
            }
        }

        /// <summary>The marker whose diamond handle is under <paramref name="p"/>, if any.</summary>
        private FitMarker HitMarker(Point p)
        {
            if (!_showFitRangeMarkers || _entries.Count == 0 || !FitRangeAdjustable) return FitMarker.None;

            static double Dist(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
            double ds = TryGetMarkerPosition(FitMarker.Start, out var cs) ? Dist(p, cs) : double.PositiveInfinity;
            double de = TryGetMarkerPosition(FitMarker.End, out var ce) ? Dist(p, ce) : double.PositiveInfinity;
            if (ds > MarkerHitRadius && de > MarkerHitRadius) return FitMarker.None;
            // Markers on the same spot: the pointer's side tells which one is meant.
            if (Math.Abs(ds - de) < 1) return p.X < cs.X ? FitMarker.Start : FitMarker.End;
            return ds < de ? FitMarker.Start : FitMarker.End;
        }

        /// <summary>
        /// Moves the dragged marker to the data point nearest the pointer's X, among the points that leave
        /// at least <see cref="FitMinimumPoints"/> points between the two markers. A marker brought back to
        /// the end of the data goes back to "no limit" (<c>null</c>), so the range follows the data again.
        /// </summary>
        private void DragMarkerTo(double screenX)
        {
            var pa = _plotArea;
            var xs = GetFitSortedX();
            if (pa.Width <= 0 || xs.Length <= MinFitPoints
                || !TryGetFitDomain(out double lo, out double hi)
                || !TryGetFitEnds(out var start, out var end)) return;
            double x = ScreenToData(new Point(screenX, pa.Top), pa).x;

            // Positions of the other marker in the sorted X values: the first / last point at its X.
            int firstStart = Array.BinarySearch(xs, start.X);
            while (firstStart > 0 && xs[firstStart - 1] == start.X) firstStart--;
            int lastEnd = Array.BinarySearch(xs, end.X);
            while (lastEnd >= 0 && lastEnd < xs.Length - 1 && xs[lastEnd + 1] == end.X) lastEnd++;
            if (firstStart < 0 || lastEnd < 0) return;

            bool dragStart = _dragMarker == FitMarker.Start;
            double? best = null;
            double bestDist = double.PositiveInfinity;
            for (int i = 0; i < xs.Length; i++)
            {
                // One candidate per distinct X: the first point for a start, the last point for an end.
                bool allowed = dragStart
                    ? (i == 0 || xs[i - 1] != xs[i]) && lastEnd - i + 1 >= MinFitPoints
                    : (i == xs.Length - 1 || xs[i + 1] != xs[i]) && i - firstStart + 1 >= MinFitPoints;
                if (!allowed) continue;
                double d = Math.Abs(xs[i] - x);
                if (d < bestDist) { bestDist = d; best = xs[i]; }
            }
            if (best is not { } snapped) return;

            if (dragStart) _fitRangeStart = snapped <= lo ? null : snapped;
            else _fitRangeEnd = snapped >= hi ? null : snapped;
            InvalidateVisual();
            FitRangeChanged?.Invoke(this, EventArgs.Empty);
        }

        private static readonly IBrush SelectionHintTextBrush = new SolidColorBrush(Color.FromRgb(70, 100, 160));
        private const double SelectionHintFontSize = 12;

        /// <summary>
        /// Draws the gesture hint at the bottom-left of the plot area while a selection is dragged.
        /// Its size is fixed, independent of the tick font size.
        /// </summary>
        private void DrawSelectionHint(DrawingContext ctx, Rect pa)
        {
            const double inset = 6;
            var ft = new FormattedText("Shift: zoom one axis only  |  Drag left: fit", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelTypeface, SelectionHintFontSize, SelectionHintTextBrush);
            using (ctx.PushClip(pa))
                ctx.DrawText(ft, new Point(pa.Left + inset, pa.Bottom - inset - ft.Height));
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _isPointerInside = true;
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _isPointerInside = false;
            _hoverMarker = FitMarker.None;
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key is Key.LeftShift or Key.RightShift && _isSelecting)
            {
                _selectShift = false;
                InvalidateVisual();
            }
            base.OnKeyUp(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isSelecting)
            {
                _isSelecting = false;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.LeftShift or Key.RightShift && _isSelecting)
            {
                _selectShift = true;
                InvalidateVisual();
            }
            if (e.Key is Key.Home or Key.F)
            {
                FitToData();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
