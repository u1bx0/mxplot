using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    /// <summary>
    /// An ellipse overlay with 8 resize handles on the bounding box.
    /// Default snap mode is <see cref="PixelSnapMode.Center"/> (drag from centre outward).
    /// </summary>
    public sealed class OvalObject : BoundingBoxBase, IAnalyzableOverlay
    {
        public OvalObject() { SnapMode = PixelSnapMode.Center; }

        // ── IAnalyzableOverlay ────────────────────────────────────

        public event EventHandler? FindMinMaxRequested;
        public event EventHandler? ToggleShowStatisticsRequested;
        public event EventHandler? UseRoiForValueRangeRequested;
        public bool ShowStatistics { get; set; } = false;
        public RegionStatistics? CachedStatistics { get; set; }
        public bool IsValueRangeRoi { get; set; } = false;

        public void RaiseFindMinMaxRequested() => FindMinMaxRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseToggleShowStatisticsRequested() => ToggleShowStatisticsRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseUseRoiForValueRangeRequested() => UseRoiForValueRangeRequested?.Invoke(this, EventArgs.Empty);

        public bool ContainsWorldPoint(Point worldPoint)
        {
            if (Width < 1e-10 || Height < 1e-10) return false;
            double cx = X + Width / 2, cy = Y + Height / 2;
            double rx = Width / 2, ry = Height / 2;
            double dx = worldPoint.X - cx, dy = worldPoint.Y - cy;
            return (dx * dx) / (rx * rx) + (dy * dy) / (ry * ry) <= 1.0;
        }

        public event EventHandler<(Point Origin, double Width, double Height)>? GeometryChanged;

        public override void SetCreationBounds(Point start, Point end)
            => SetCreationBoundsInternal(start, end, forceSquare: false);

        public override void SetCreationBounds(Point start, Point end, KeyModifiers modifiers)
        {
            bool isShift = modifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl = modifiers.HasFlag(KeyModifiers.Control);
            if (isCtrl)
            {
                // Ctrl: start = centre
                double halfW = Math.Abs(end.X - start.X);
                double halfH = Math.Abs(end.Y - start.Y);
                if (isShift) { double s = Math.Max(halfW, halfH); halfW = halfH = s; }
                X = start.X - halfW;
                Y = start.Y - halfH;
                Width = 2 * halfW;
                Height = 2 * halfH;
            }
            else
            {
                SetCreationBoundsInternal(start, end, forceSquare: isShift);
            }
        }

        public override string? GetInfo(IMatrixData? data)
        {
            if (data == null || (data.XStep == 0 && data.YStep == 0))
                return $"Oval: W={FmtLen(Math.Abs(Width))}, H={FmtLen(Math.Abs(Height))}";

            double physW = Math.Abs(Width * data.XStep);
            double physH = Math.Abs(Height * data.YStep);
            string xu = data.XUnit ?? "";
            string yu = data.YUnit ?? "";
            string xuStr = xu.Length > 0 ? $" {xu}" : "";
            string yuStr = yu.Length > 0 ? $" {yu}" : "";
            return $"Oval: W={FmtLen(physW)}{xuStr} [{FmtLen(Width)}], H={FmtLen(physH)}{yuStr} [{FmtLen(Height)}]";
        }

        public override void Draw(AvaloniaOverlayGraphics g)
        {
            if (Width == 0 || Height == 0) return;
            if (IsFilled)
                g.FillEllipse(FillColor, X, Y, Width, Height);
            var (color, dash) = GetDrawingPen();
            g.DrawEllipse(color, PenWidth, dash, X, Y, Width, Height, IsScaledPenWidth);
            DrawHandles(g);
            if (ShowStatistics && CachedStatistics.HasValue)
                DrawStatisticsLabel(g, CachedStatistics.Value);
            if (IsValueRangeRoi)
                DrawRoiLabel(g);
        }

        public override HandleType HitTest(Point location, AvaloniaViewport vp)
        {
            // Handles first (only when selected)
            var handle = HitTestBoundingBox(location, vp, testEdges: false, testInterior: false);
            if (handle != HandleType.None) return handle;

            return HitTestEllipse(location, vp) ? HandleType.Body : HandleType.None;
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="location"/> is near the ellipse perimeter
        /// (within <paramref name="threshold"/> screen pixels) or inside a filled ellipse.
        /// </summary>
        internal bool HitTestEllipse(Point location, AvaloniaViewport vp, double threshold = 5.0)
        {
            double cx = X + Width / 2, cy = Y + Height / 2;
            var sc = vp.WorldToScreen(new Point(cx, cy));
            var se = vp.WorldToScreen(new Point(cx + Math.Abs(Width) / 2, cy));
            var st = vp.WorldToScreen(new Point(cx, cy - Math.Abs(Height) / 2));
            double rx = Math.Abs(se.X - sc.X);
            double ry = Math.Abs(st.Y - sc.Y);
            if (rx < 0.5 || ry < 0.5) return false;

            double u = (location.X - sc.X) / rx;
            double v = (location.Y - sc.Y) / ry;
            double d = Math.Sqrt(u * u + v * v);

            if (Math.Abs(d - 1.0) * Math.Min(rx, ry) <= threshold) return true;
            if (IsFilled && d <= 1.0) return true;
            return false;
        }

        public override Cursor GetCursor(HandleType handle, AvaloniaViewport vp) =>
            GetResizeCursor(handle, vp);

        public override void Move(double dx, double dy)
        {
            X += dx; Y += dy;
            GeometryChanged?.Invoke(this, (new Point(X, Y), Width, Height));
        }

        public override void Resize(HandleType handle, Point worldNewPos)
        {
            if (CurrentModifiers.HasFlag(KeyModifiers.Control))
                ResizeBoundingBoxCtrl(handle, worldNewPos);
            else
                ResizeBoundingBox(handle, worldNewPos);
            GeometryChanged?.Invoke(this, (new Point(X, Y), Width, Height));
        }

        public override IEnumerable<OverlayMenuItem>? GetContextMenuItems()
        {
            foreach (var item in base.GetContextMenuItems() ?? [])
                yield return item;
        }
    }
}
