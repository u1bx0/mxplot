using Avalonia;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Captures the fully-computed layout and data state of a <see cref="ProfilePlotControl"/>
    /// needed to reproduce one frame of rendering — used by <see cref="PlotSvgWriter"/>.
    /// </summary>
    internal sealed record PlotRenderData
    {
        public required double Width       { get; init; }
        public required double Height      { get; init; }
        public required Rect   PlotArea    { get; init; }
        public required double YLabelCx    { get; init; }
        public required double LegendAbsY  { get; init; }
        public required IReadOnlyList<double> XTicks    { get; init; }
        public required IReadOnlyList<double> YTicks    { get; init; }
        public required double XSpacing    { get; init; }
        public required double YSpacing    { get; init; }
        public required double VxMin       { get; init; }
        public required double VxMax       { get; init; }
        public required double VyMin       { get; init; }
        public required double VyMax       { get; init; }
        public required string Title       { get; init; }
        public required string XLabel      { get; init; }
        public required string YLabel      { get; init; }
        public required double TickFontSize   { get; init; }
        public required double LabelFontSize  { get; init; }
        public required double TitleFontSize  { get; init; }
        public required double LegendFontSize { get; init; }
        public required double AxisThickness  { get; init; }
        public required LegendPosition LegendPosition { get; init; }
        public required IReadOnlyList<(PlotSeries Series, Color Color)> Entries { get; init; }
        /// <summary>Optional fit overlay curve; rendered as a dashed line if non-null.</summary>
        public IReadOnlyList<(double X, double Y)>? FitOverlay { get; init; }
        /// <summary>Legend label for the fit overlay (null or empty = no legend entry).</summary>
        public string? FitOverlayName { get; init; }
        /// <summary>Fit overlay stroke color.</summary>
        public Color FitOverlayColor { get; init; } = Color.FromArgb(200, 220, 50, 50);
        /// <summary>Fit overlay stroke line width.</summary>
        public double FitOverlayLineWidth { get; init; } = 1.5;
    }

    /// <summary>
    /// Writes a <see cref="ProfilePlotControl"/> frame as an SVG document.
    /// No additional NuGet dependencies — pure XML generation.
    /// </summary>
    internal static class PlotSvgWriter
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        private static string N(double v)   => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string Rgb(Color c)   => $"rgb({c.R},{c.G},{c.B})";
        private static string Rgba(Color c)  => $"rgba({c.R},{c.G},{c.B},{c.A / 255.0:F2})";
        private static string Esc(string s)  => s.Replace("&","&amp;").Replace("<","&lt;")
                                                  .Replace(">","&gt;").Replace("\"","&quot;");

        private static double Sx(double dx, PlotRenderData d) =>
            d.VxMax > d.VxMin
                ? d.PlotArea.X + (dx - d.VxMin) / (d.VxMax - d.VxMin) * d.PlotArea.Width
                : d.PlotArea.X + d.PlotArea.Width / 2;

        private static double Sy(double dy, PlotRenderData d) =>
            d.VyMax > d.VyMin
                ? d.PlotArea.Bottom - (dy - d.VyMin) / (d.VyMax - d.VyMin) * d.PlotArea.Height
                : d.PlotArea.Y + d.PlotArea.Height / 2;

        private static string FormatTick(double value, double spacing)
        {
            if (Math.Abs(value) < spacing * 1e-10) return "0";
            double absRef = Math.Max(Math.Abs(value), spacing);
            if (absRef >= 1e6 || absRef < 1e-3) return value.ToString("G4", CultureInfo.InvariantCulture);
            int dec = Math.Max(0, -(int)Math.Floor(Math.Log10(spacing) - 0.001));
            return value.ToString($"F{Math.Min(dec, 10)}", CultureInfo.InvariantCulture);
        }

        // ── Public entry point ────────────────────────────────────────────────

        public static void Write(Stream stream, PlotRenderData d)
        {
            using var w = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            var pa = d.PlotArea;

            w.WriteLine("""<?xml version="1.0" encoding="utf-8"?>""");
            w.WriteLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{N(d.Width)}" height="{N(d.Height)}">""");
            w.WriteLine($"""<defs><clipPath id="pc"><rect x="{N(pa.X)}" y="{N(pa.Y)}" width="{N(pa.Width)}" height="{N(pa.Height)}"/></clipPath></defs>""");

            // Background
            w.WriteLine($"""<rect width="{N(d.Width)}" height="{N(d.Height)}" fill="white"/>""");

            if (d.Entries.Count == 0 || d.VxMax <= d.VxMin || d.VyMax <= d.VyMin)
            { w.WriteLine("</svg>"); return; }

            // Plot area background
            w.WriteLine($"""<rect x="{N(pa.X)}" y="{N(pa.Y)}" width="{N(pa.Width)}" height="{N(pa.Height)}" fill="rgb(252,252,252)"/>""");

            // Grid lines
            w.WriteLine("""<g stroke="rgb(225,225,225)" stroke-width="0.5">""");
            foreach (var t in d.XTicks) { double sx = Sx(t, d); if (sx >= pa.X && sx <= pa.Right)  w.WriteLine($"""  <line x1="{N(sx)}" y1="{N(pa.Y)}"    x2="{N(sx)}"    y2="{N(pa.Bottom)}"/>"""); }
            foreach (var t in d.YTicks) { double sy = Sy(t, d); if (sy >= pa.Y && sy <= pa.Bottom) w.WriteLine($"""  <line x1="{N(pa.X)}" y1="{N(sy)}"    x2="{N(pa.Right)}" y2="{N(sy)}"/>"""); }
            w.WriteLine("</g>");

            // Data series (clipped to plot area)
            w.WriteLine("""<g clip-path="url(#pc)">""");
            foreach (var (s, c) in d.Entries) WriteSeries(w, s, c, d);
            w.WriteLine("</g>");

            // Fit overlay (dashed, clipped)
            if (d.FitOverlay != null && d.FitOverlay.Count > 1)
            {
                var sb2 = new StringBuilder();
                bool first2 = true;
                foreach (var (x, y) in d.FitOverlay)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    if (first2) { sb2.Append($"M{N(Sx(x,d))},{N(Sy(y,d))}"); first2 = false; }
                    else         sb2.Append($" L{N(Sx(x,d))},{N(Sy(y,d))}");
                }
                if (sb2.Length > 0)
                {
                    w.WriteLine($"""<g clip-path="url(#pc)">""");
                    w.WriteLine($"""  <path d="{sb2}" fill="none" stroke="{Rgba(d.FitOverlayColor)}" stroke-width="{N(d.FitOverlayLineWidth)}" stroke-dasharray="5,3" stroke-linejoin="round" stroke-linecap="round"/>""");
                    w.WriteLine("</g>");
                }
            }

            // Axis border
            w.WriteLine($"""<rect x="{N(pa.X)}" y="{N(pa.Y)}" width="{N(pa.Width)}" height="{N(pa.Height)}" fill="none" stroke="black" stroke-width="{N(d.AxisThickness)}"/>""");

            // Tick lines
            w.WriteLine($"""<g stroke="black" stroke-width="{N(d.AxisThickness)}">""");
            foreach (var t in d.XTicks) { double sx = Sx(t, d); if (sx >= pa.X-1 && sx <= pa.Right+1)  w.WriteLine($"""  <line x1="{N(sx)}"    y1="{N(pa.Bottom)}"   x2="{N(sx)}"    y2="{N(pa.Bottom+5)}"/>"""); }
            foreach (var t in d.YTicks) { double sy = Sy(t, d); if (sy >= pa.Y-1 && sy <= pa.Bottom+1) w.WriteLine($"""  <line x1="{N(pa.X-5)}" y1="{N(sy)}"         x2="{N(pa.X)}"  y2="{N(sy)}"/>"""); }
            w.WriteLine("</g>");

            // Tick labels — SVG y is the text baseline; y = avaloniaTop + fontSize ≈ baseline
            double tfs = d.TickFontSize;
            w.WriteLine($"""<g font-family="Consolas,monospace" font-size="{N(tfs)}" fill="black">""");
            foreach (var t in d.XTicks) { double sx = Sx(t, d); if (sx >= pa.X-1 && sx <= pa.Right+1)  w.WriteLine($"""  <text x="{N(sx)}"          y="{N(pa.Bottom+5+3+tfs)}" text-anchor="middle">{Esc(FormatTick(t, d.XSpacing))}</text>"""); }
            foreach (var t in d.YTicks) { double sy = Sy(t, d); if (sy >= pa.Y-1 && sy <= pa.Bottom+1) w.WriteLine($"""  <text x="{N(pa.X-5-3)}"    y="{N(sy + tfs*0.35)}"     text-anchor="end"   >{Esc(FormatTick(t, d.YSpacing))}</text>"""); }
            w.WriteLine("</g>");

            // Axis labels
            double lfs = d.LabelFontSize;
            if (d.XLabel.Length > 0)
                w.WriteLine($"""<text x="{N(pa.X + pa.Width/2)}" y="{N(pa.Bottom+5+3+tfs+4+lfs)}" text-anchor="middle" font-family="sans-serif" font-size="{N(lfs)}" fill="black">{Esc(d.XLabel)}</text>""");
            if (d.YLabel.Length > 0)
            {
                double cx = d.YLabelCx, cy = pa.Y + pa.Height / 2;
                w.WriteLine($"""<text x="{N(cx)}" y="{N(cy)}" text-anchor="middle" dominant-baseline="middle" font-family="sans-serif" font-size="{N(lfs)}" fill="black" transform="rotate(-90 {N(cx)} {N(cy)})">{Esc(d.YLabel)}</text>""");
            }

            // Legend
            if (d.LegendPosition != LegendPosition.None) WriteLegend(w, d, pa);

            // Title
            if (d.Title.Length > 0)
                w.WriteLine($"""<text x="{N(pa.X + pa.Width/2)}" y="{N(12 + d.TitleFontSize)}" text-anchor="middle" font-family="sans-serif" font-size="{N(d.TitleFontSize)}" fill="black">{Esc(d.Title)}</text>""");

            w.WriteLine("</svg>");
        }

        // ── Series ────────────────────────────────────────────────────────────

        private static void WriteSeries(TextWriter w, PlotSeries s, Color color, PlotRenderData d)
        {
            if (s.Points.Count == 0) return;
            var stroke = Rgb(color);

            if (s.Style is PlotStyle.Line or PlotStyle.MarkedLine && s.Points.Count > 1)
            {
                var sb = new StringBuilder();
                bool first = true;
                foreach (var (x, y) in s.Points)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    if (first) { sb.Append($"M{N(Sx(x,d))},{N(Sy(y,d))}"); first = false; }
                    else         sb.Append($" L{N(Sx(x,d))},{N(Sy(y,d))}");
                }
                if (sb.Length > 0)
                    w.WriteLine($"""<path d="{sb}" fill="none" stroke="{stroke}" stroke-width="{N(s.LineWidth)}" stroke-linejoin="round" stroke-linecap="round"/>""");
            }

            if (s.Style is PlotStyle.Marker or PlotStyle.MarkedLine)
            {
                double r = s.LineWidth + 1.0;
                w.WriteLine($"""<g fill="{stroke}">""");
                foreach (var (x, y) in s.Points)
                {
                    if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                    w.WriteLine($"""  <circle cx="{N(Sx(x,d))}" cy="{N(Sy(y,d))}" r="{N(r)}"/>""");
                }
                w.WriteLine("</g>");
            }
        }

        // ── Legend ────────────────────────────────────────────────────────────

        private static void WriteLegend(TextWriter w, PlotRenderData d, Rect pa)
        {
            var named = d.Entries.Where(e => e.Series.Name.Length > 0).ToList();
            bool hasFit = d.FitOverlay is { Count: > 0 } && !string.IsNullOrEmpty(d.FitOverlayName);
            if (named.Count == 0 && !hasFit) return;

            const double iconW = 16, gap = 5, lpad = 6;
            double itemH  = d.LegendFontSize + 6;
            int totalItems = named.Count + (hasFit ? 1 : 0);
            double EstW(string text) => text.Length * d.LegendFontSize * 0.55;

            void Icon(double ix, double my, PlotStyle ps, Color c, double lw)
            {
                var col = Rgb(c);
                if (ps is PlotStyle.Line or PlotStyle.MarkedLine)
                    w.WriteLine($"""  <line x1="{N(ix)}" y1="{N(my)}" x2="{N(ix+iconW)}" y2="{N(my)}" stroke="{col}" stroke-width="{N(Math.Min(lw,3))}"/>""");
                if (ps is PlotStyle.Marker or PlotStyle.MarkedLine)
                    w.WriteLine($"""  <circle cx="{N(ix+iconW/2)}" cy="{N(my)}" r="{N(Math.Min(lw+1,4.5))}" fill="{col}"/>""");
            }
            void FitIcon(double ix, double my) =>
                w.WriteLine($"""  <line x1="{N(ix)}" y1="{N(my)}" x2="{N(ix+iconW)}" y2="{N(my)}" stroke="{Rgba(d.FitOverlayColor)}" stroke-width="{N(d.FitOverlayLineWidth)}" stroke-dasharray="5,3"/>""");

            if (d.LegendPosition == LegendPosition.BelowPlot)
            {
                const double itemGap = 12;
                double totalW = named.Sum(e => iconW + gap + EstW(e.Series.Name))
                              + Math.Max(0, named.Count - 1) * itemGap;
                if (hasFit) totalW += (named.Count > 0 ? itemGap : 0) + iconW + gap + EstW(d.FitOverlayName!);
                double boxW = lpad * 2 + totalW, boxH = lpad * 2 + itemH;
                double bx = pa.X + (pa.Width - boxW) / 2, by = d.LegendAbsY;
                w.WriteLine($"""<rect x="{N(bx)}" y="{N(by)}" width="{N(boxW)}" height="{N(boxH)}" rx="3" fill="rgba(255,255,255,0.82)" stroke="rgb(200,200,200)" stroke-width="0.5"/>""");
                w.WriteLine("<g>");
                double ix = bx + lpad;
                double midY = by + lpad + itemH / 2;
                foreach (var (s, c) in named)
                {
                    Icon(ix, midY, s.Style, c, s.LineWidth);
                    w.WriteLine($"""  <text x="{N(ix+iconW+gap)}" y="{N(midY + d.LegendFontSize*0.35)}" font-family="Consolas,monospace" font-size="{N(d.LegendFontSize)}" fill="black">{Esc(s.Name)}</text>""");
                    ix += iconW + gap + EstW(s.Name) + itemGap;
                }
                if (hasFit)
                {
                    FitIcon(ix, midY);
                    w.WriteLine($"""  <text x="{N(ix+iconW+gap)}" y="{N(midY + d.LegendFontSize*0.35)}" font-family="Consolas,monospace" font-size="{N(d.LegendFontSize)}" fill="black">{Esc(d.FitOverlayName!)}</text>""");
                }
                w.WriteLine("</g>");
            }
            else // InsetTopRight
            {
                double maxTW = named.Count > 0 ? named.Max(e => EstW(e.Series.Name)) : 0;
                if (hasFit) maxTW = Math.Max(maxTW, EstW(d.FitOverlayName!));
                double boxW  = lpad * 2 + iconW + gap + maxTW;
                double boxH  = lpad * 2 + totalItems * itemH + Math.Max(0, totalItems - 1) * 3;
                double bx = pa.Right - boxW - 8, by = pa.Y + 8;
                w.WriteLine($"""<rect x="{N(bx)}" y="{N(by)}" width="{N(boxW)}" height="{N(boxH)}" rx="3" fill="rgba(255,255,255,0.82)" stroke="rgb(200,200,200)" stroke-width="0.5"/>""");
                w.WriteLine("<g>");
                double y = by + lpad;
                foreach (var (s, c) in named)
                {
                    Icon(bx + lpad, y + itemH / 2, s.Style, c, s.LineWidth);
                    w.WriteLine($"""  <text x="{N(bx+lpad+iconW+gap)}" y="{N(y + itemH/2 + d.LegendFontSize*0.35)}" font-family="Consolas,monospace" font-size="{N(d.LegendFontSize)}" fill="black">{Esc(s.Name)}</text>""");
                    y += itemH + 3;
                }
                if (hasFit)
                {
                    FitIcon(bx + lpad, y + itemH / 2);
                    w.WriteLine($"""  <text x="{N(bx+lpad+iconW+gap)}" y="{N(y + itemH/2 + d.LegendFontSize*0.35)}" font-family="Consolas,monospace" font-size="{N(d.LegendFontSize)}" fill="black">{Esc(d.FitOverlayName!)}</text>""");
                }
                w.WriteLine("</g>");
            }
        }
    }
}
