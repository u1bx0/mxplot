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
    /// Supports pan (left-drag), zoom (mouse wheel), fit-to-data (double-click / Home),
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
        public double TickFontSize { get; set; } = 14;
        /// <summary>Font size for axis labels (X / Y).</summary>
        public double LabelFontSize { get; set; } = 14;
        /// <summary>Font size for legend entries.</summary>
        public double LegendFontSize { get; set; } = 14;
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

        /// <summary>When <c>true</c>, locks the X view range and disables X pan/zoom.</summary>
        public bool XAxisFixed
        {
            get => _xAxisFixed;
            set { if (_xAxisFixed == value) return; _xAxisFixed = value; if (value) { _xFixedMin = _vxMin; _xFixedMax = _vxMax; } InvalidateVisual(); }
        }
        /// <summary>When <c>true</c>, locks the Y view range and disables Y pan/zoom.</summary>
        public bool YAxisFixed
        {
            get => _yAxisFixed;
            set { if (_yAxisFixed == value) return; _yAxisFixed = value; if (value) { _yFixedMin = _vyMin; _yFixedMax = _vyMax; } InvalidateVisual(); }
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
        private IPen _axisPen = new Pen(Brushes.Black, 1.0);
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
        private double _axisThickness = 1.0;

        // ── Data ───────────────────────────────────────────────────────────────
        private readonly List<(PlotSeries Series, Color Color)> _entries = [];
        private string _xLabel = "";
        private string _yLabel = "";
        private string _title = "";

        // ── View range (data coordinates of the visible window) ───────────────
        private double _vxMin, _vxMax, _vyMin, _vyMax;
        private double _dxMin, _dxMax, _dyMin, _dyMax;

        // ── Interaction ────────────────────────────────────────────────────────
        private bool _isPanning;
        private Point _panStartScreen;
        private double _panVxMin, _panVxMax, _panVyMin, _panVyMax;
        private bool _isPointerInside;
        private Point _lastPointerPos;
        private Rect _plotArea;

        // ── Constructors ───────────────────────────────────────────────────────

        public ProfilePlotControl()
        {
            Focusable = true;
            ClipToBounds = true;
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
            _xLabel = xLabel;
            _yLabel = yLabel;
            _title = title;
            for (int i = 0; i < series.Count; i++)
            {
                var s = series[i];
                _entries.Add((s, s.Color ?? Palette[i % Palette.Length]));
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
            FitToData();
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
            if (!_xAxisFixed) { _vxMin = _dxMin; _vxMax = _dxMax; }
            if (!_yAxisFixed) { _vyMin = _dyMin; _vyMax = _dyMax; }
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
                foreach (var (series, color) in _entries)
                    DrawSeries(ctx, series, color, pa);
                if (_fitOverlayPoints != null && _fitOverlayPoints.Count > 1)
                    DrawFitOverlay(ctx, pa);
            }

            // Axis border
            ctx.DrawRectangle(null, _axisPen, pa);

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

            if (_isPointerInside && _showCrosshair && pa.Contains(_lastPointerPos))
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
            var named  = _entries.Where(e => e.Series.Name.Length > 0).ToList();
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
            if (e.ClickCount == 2) { FitToData(); e.Handled = true; return; }
            if (_xAxisFixed && _yAxisFixed) return;

            _isPanning = true;
            _panStartScreen = e.GetPosition(this);
            _panVxMin = _vxMin; _panVxMax = _vxMax;
            _panVyMin = _vyMin; _panVyMax = _vyMax;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var pos = e.GetPosition(this);
            _lastPointerPos = pos;
            if (_isPanning && _plotArea.Width > 0)
            {
                double ddx = (pos.X - _panStartScreen.X) / _plotArea.Width * (_panVxMax - _panVxMin);
                double ddy = (pos.Y - _panStartScreen.Y) / _plotArea.Height * (_panVyMax - _panVyMin);
                if (!_xAxisFixed) { _vxMin = _panVxMin - ddx; _vxMax = _panVxMax - ddx; }
                if (!_yAxisFixed) { _vyMin = _panVyMin + ddy; _vyMax = _panVyMax + ddy; }
            }
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_isPanning) { _isPanning = false; e.Pointer.Capture(null); }
            base.OnPointerReleased(e);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _isPointerInside = true;
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _isPointerInside = false;
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (_plotArea.Width <= 0 || _plotArea.Height <= 0) return;
            var pos = e.GetPosition(this);
            if (!_plotArea.Contains(pos)) return;
            if (_xAxisFixed && _yAxisFixed) { e.Handled = true; return; }

            double factor = e.Delta.Y > 0 ? 0.8 : 1.25;
            var (ax, ay) = ScreenToData(pos, _plotArea);
            if (!_xAxisFixed) { _vxMin = ax - (ax - _vxMin) * factor; _vxMax = ax + (_vxMax - ax) * factor; }
            if (!_yAxisFixed) { _vyMin = ay - (ay - _vyMin) * factor; _vyMax = ay + (_vyMax - ay) * factor; }

            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
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
