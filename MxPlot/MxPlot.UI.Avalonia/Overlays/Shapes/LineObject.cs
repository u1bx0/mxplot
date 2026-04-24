using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    public sealed class LineObject : OverlayObjectBase
    {
        public Point P1 { get; set; }
        public Point P2 { get; set; }

        private Point _originalP1;
        private Point _originalP2;
        private bool _hasResizeState;

        public override void BeginResize()
        {
            _originalP1 = P1;
            _originalP2 = P2;
            _hasResizeState = true;
        }

        public override void ResetResizeState() => _hasResizeState = false;

        /// <summary>Raised when the user selects "Plot Profile" from the context menu.</summary>
        public event EventHandler? PlotProfileRequested;

        /// <summary>
        /// Raised when the user selects "Calibrate Scale" from the context menu. 
        /// The handler should open a dialog to set the scale based on the line endpoints.
        /// This allows using the line as a reference for physical dimensions in the plot.
        /// </summary>
        public event EventHandler? CalibrateScaleRequested;

        public override Point? GetApproxWorldCenter() =>
            new Point((P1.X + P2.X) / 2, (P1.Y + P2.Y) / 2);

        public override string? GetInfo(IMatrixData? data)
        {
            double dx = P2.X - P1.X;
            double dy = P2.Y - P1.Y;

            if (data == null || (data.XStep == 0 && data.YStep == 0))
            {
                double len = Math.Sqrt(dx * dx + dy * dy);
                return $"Line: Len={FmtLen(len)}";
            }

            double physDx = dx * data.XStep;   // signed, preserves direction
            double physDy = dy * data.YStep;
            double physW = Math.Abs(physDx);
            double physH = Math.Abs(physDy);
            string xu = data.XUnit ?? "";
            string yu = data.YUnit ?? "";

            if (xu == yu)
            {
                double len = Math.Sqrt(physDx * physDx + physDy * physDy);
                string unitStr = xu.Length > 0 ? $" {xu}" : "";
                // Angle: P1→P2 in data-index convention (left-bottom origin, Y-up).
                // World Y is down, so flip dy for the physical Y direction (Y-up).
                physDy = -physDy;
                if (physDy == 0) //if physDy == -0, minus sign is removed.
                    physDy = 0.0;
                double angleDeg = Math.Atan2(physDy, physDx) * (180.0 / Math.PI);
                if (angleDeg < 0) //0 to 360 instead of -180 to 180
                    angleDeg += 360;
                
                return $"Line: Len={FmtLen(len)}{unitStr}, Angle={angleDeg:F1}°";
            }
            else
            {
                string xuStr = xu.Length > 0 ? $" {xu}" : "";
                string yuStr = yu.Length > 0 ? $" {yu}" : "";
                return $"Line: W={FmtLen(physW)}{xuStr}, H={FmtLen(physH)}{yuStr}";
            }
        }

        public LineObject() { SnapMode = PixelSnapMode.Center; }
        public LineObject(Point p1, Point p2) { P1 = p1; P2 = p2; }
        public LineObject(double x1, double y1, double x2, double y2)
        {
            P1 = new Point(x1, y1);
            P2 = new Point(x2, y2);
        }

        public event EventHandler<(Point P1, Point P2)>? GeometryChanged;

        public override void SetCreationBounds(Point start, Point end)
        {
            P1 = start;
            P2 = end;
        }

        public override void SetCreationBounds(Point start, Point end, KeyModifiers modifiers)
        {
            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                double dx = end.X - start.X;
                double dy = end.Y - start.Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > 0)
                {
                    double angle = Math.Atan2(dy, dx);
                    double snapped = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
                    end = new Point(start.X + dist * Math.Cos(snapped),
                                    start.Y + dist * Math.Sin(snapped));
                }
            }

            if (modifiers.HasFlag(KeyModifiers.Control))
            {
                // start becomes the midpoint; P1 mirrors end through start
                P1 = new Point(2 * start.X - end.X, 2 * start.Y - end.Y);
                P2 = end;
            }
            else
            {
                P1 = start;
                P2 = end;
            }
        }

        public override void Resize(HandleType handle, Point worldNewPos)
        {
            if (CurrentModifiers.HasFlag(KeyModifiers.Control))
            {
                // Symmetric around the original midpoint so the center stays fixed
                var center = _hasResizeState
                    ? new Point((_originalP1.X + _originalP2.X) / 2, (_originalP1.Y + _originalP2.Y) / 2)
                    : new Point((P1.X + P2.X) / 2, (P1.Y + P2.Y) / 2);
                var mirror = new Point(2 * center.X - worldNewPos.X, 2 * center.Y - worldNewPos.Y);
                if (handle == HandleType.TopLeft) { P1 = worldNewPos; P2 = mirror; }
                else                              { P2 = worldNewPos; P1 = mirror; }
            }
            else
            {
                // Ctrl released mid-drag: restore the non-dragged endpoint to its pre-drag position
                if (handle == HandleType.TopLeft)
                {
                    P1 = worldNewPos;
                    if (_hasResizeState) P2 = _originalP2;
                }
                else
                {
                    P2 = worldNewPos;
                    if (_hasResizeState) P1 = _originalP1;
                }
            }
            GeometryChanged?.Invoke(this, (P1, P2));
        }

        /// <summary>
        /// Shift: constrains the dragged endpoint along the <em>original</em> line direction
        /// (direction at drag start, not the current direction at the moment Shift is pressed).
        /// Ctrl: also moves the opposite endpoint symmetrically around the original midpoint.
        /// Ctrl released mid-drag: restores the non-dragged endpoint to its pre-drag position.
        /// Shift+Ctrl: both constraints combined.
        /// </summary>
        public override void ResizeConstrained(HandleType handle, Point worldNewPos)
        {
            // Shift: project onto the original line direction (locked at drag start)
            Point refP1 = _hasResizeState ? _originalP1 : P1;
            Point refP2 = _hasResizeState ? _originalP2 : P2;
            Point anchor = handle == HandleType.TopLeft ? refP2 : refP1;
            double ldx = refP2.X - refP1.X;
            double ldy = refP2.Y - refP1.Y;
            double len2 = ldx * ldx + ldy * ldy;
            if (len2 < 1e-10) { Resize(handle, worldNewPos); return; }

            double tx = worldNewPos.X - anchor.X;
            double ty = worldNewPos.Y - anchor.Y;
            double t = (tx * ldx + ty * ldy) / len2;
            var projected = new Point(anchor.X + t * ldx, anchor.Y + t * ldy);

            if (CurrentModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl: symmetric around the original midpoint (after axis constraint)
                var center = _hasResizeState
                    ? new Point((_originalP1.X + _originalP2.X) / 2, (_originalP1.Y + _originalP2.Y) / 2)
                    : new Point((P1.X + P2.X) / 2, (P1.Y + P2.Y) / 2);
                var mirror = new Point(2 * center.X - projected.X, 2 * center.Y - projected.Y);
                if (handle == HandleType.TopLeft) { P1 = projected; P2 = mirror; }
                else                              { P2 = projected; P1 = mirror; }
                GeometryChanged?.Invoke(this, (P1, P2));
            }
            else
            {
                // Ctrl released mid-drag: restore the non-dragged endpoint to its pre-drag position
                if (handle == HandleType.TopLeft)
                {
                    P1 = projected;
                    if (_hasResizeState) P2 = _originalP2;
                }
                else
                {
                    P2 = projected;
                    if (_hasResizeState) P1 = _originalP1;
                }
                GeometryChanged?.Invoke(this, (P1, P2));
            }
        }

        public override void Draw(AvaloniaOverlayGraphics g)
        {
            var (color, dash) = GetDrawingPen();
            g.DrawLine(color, PenWidth, dash, P1.X, P1.Y, P2.X, P2.Y, IsScaledPenWidth);

            if (IsSelected && !g.SuppressHandles)
            {
                g.DrawHandle(Colors.White, Colors.Black, P1.X, P1.Y);
                // When P1==P2 (zero-length line), draw only one handle to avoid invisible stacking
                if (P1 != P2)
                    g.DrawHandle(Colors.White, Colors.Black, P2.X, P2.Y);
            }
#if DEBUG
            if (IsSelected)
            {
                g.DrawString($"P1=({P1.X:F1}, {P1.Y:F1})", Colors.Red, P1.X, P1.Y +0.5, 10, scaleWithZoom: false);
                g.DrawString($"P2=({P2.X:F1}, {P2.Y:F1})", Colors.Red, P2.X, P2.Y + 0.5, 10, scaleWithZoom: false);
            }
#endif
        }

        public override HandleType HitTest(Point location, AvaloniaViewport vp)
        {
            var sP1 = vp.WorldToScreen(P1);
            var sP2 = vp.WorldToScreen(P2);
            if (Distance(location, sP1) <= HandleSize) return HandleType.TopLeft;
            if (Distance(location, sP2) <= HandleSize) return HandleType.BottomRight;
            if (DistanceToSegment(location, sP1, sP2) <= 5.0) return HandleType.Body;
            return HandleType.None;
        }

        public override void Move(double dx, double dy)
        {
            P1 = new Point(P1.X + dx, P1.Y + dy);
            P2 = new Point(P2.X + dx, P2.Y + dy);
            GeometryChanged?.Invoke(this, (P1, P2));
        }

        public override IEnumerable<OverlayMenuItem>? GetContextMenuItems()
        {
            yield return new OverlayMenuItem("Plot Profile",
                () => PlotProfileRequested?.Invoke(this, EventArgs.Empty),
                icon: MenuIcons.LineChart, tooltip: "Open profile plotter");
            yield return new OverlayMenuItem("Calibrate Scale",
                () => CalibrateScaleRequested?.Invoke(this, EventArgs.Empty),
                icon: MenuIcons.Ruler, tooltip: "Calibrate scale from the two end points.");
            yield return OverlayMenuItem.Separator();
            foreach (var item in base.GetContextMenuItems() ?? Enumerable.Empty<OverlayMenuItem>())
                yield return item;
        }

        public override Cursor GetCursor(HandleType handle, AvaloniaViewport vp)
        {
            if (handle == HandleType.Body) return new Cursor(StandardCursorType.SizeAll);
            var sP1 = vp.WorldToScreen(P1);
            var sP2 = vp.WorldToScreen(P2);
            Point dir = new(sP2.X - sP1.X, sP2.Y - sP1.Y);
            double angle = (Math.Atan2(dir.Y, dir.X) * 180 / Math.PI + 360 + 22.5) % 360;
            return (int)(angle / 45) switch
            {
                0 or 4 => new Cursor(StandardCursorType.SizeWestEast),
                1 or 5 => new Cursor(StandardCursorType.TopLeftCorner),
                2 or 6 => new Cursor(StandardCursorType.SizeNorthSouth),
                3 or 7 => new Cursor(StandardCursorType.TopRightCorner),
                _      => new Cursor(StandardCursorType.Cross),
            };
        }
    }
}
