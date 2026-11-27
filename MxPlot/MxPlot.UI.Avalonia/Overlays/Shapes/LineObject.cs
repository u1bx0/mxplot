using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    /// <summary>Where the position axis of a line profile is zero.</summary>
    public enum LineProfileOrigin
    {
        /// <summary>The end the profile starts from (the left end of the line; see <see cref="LineObject.RunsFromP1"/>).</summary>
        LeftEnd,
        /// <summary>The midpoint of the line.</summary>
        Center,
        /// <summary>A point on the line chosen by <see cref="LineObject.OriginT"/>.</summary>
        Custom,
    }

    public sealed class LineObject : OverlayObjectBase
    {
        public Point P1 { get; set; }
        public Point P2 { get; set; }

        private Point _originalP1;
        private Point _originalP2;
        private Point _originalOrigin;
        private bool _hasResizeState;

        private LineProfileOrigin _profileOrigin = LineProfileOrigin.LeftEnd;
        private double _originT = 0.5;
        private LineProfileOption _profileInterpolation = LineProfileOption.Bilinear;

        // Direction in which the physical X / Y coordinate grows with the world X / Y coordinate
        // (+1 or -1). The host sets them from the source view, since the profile runs from the
        // smaller physical X (then smaller physical Y) to the larger one.
        private int _profileXSign = 1;
        private int _profileYSign = -1;

        // Physical length of one world unit along X / Y. Distances along the line are measured in
        // these units, the units of the profile axis, so that they stay meaningful when the data
        // has different X and Y steps (e.g. an XZ view).
        private double _profileXScale = 1.0;
        private double _profileYScale = 1.0;

        /// <summary>
        /// Where the position axis of the line profile is zero. Switching to
        /// <see cref="LineProfileOrigin.Custom"/> keeps the origin where it currently is.
        /// </summary>
        public LineProfileOrigin ProfileOrigin
        {
            get => _profileOrigin;
            set
            {
                if (_profileOrigin == value) return;
                if (value == LineProfileOrigin.Custom) _originT = EffectiveOriginT;
                _profileOrigin = value;
                NotifyChanged();
            }
        }

        /// <summary>
        /// Position of the <see cref="LineProfileOrigin.Custom"/> origin as a fraction of the way from
        /// <see cref="P1"/> (0) to <see cref="P2"/> (1). Dragging one end of the line recomputes it so
        /// the origin keeps its distance from the other end.
        /// </summary>
        public double OriginT
        {
            get => _originT;
            set
            {
                value = Math.Clamp(value, 0.0, 1.0);
                if (_originT == value) return;
                _originT = value;
                if (_profileOrigin == LineProfileOrigin.Custom) NotifyChanged();
            }
        }

        /// <summary>How a profile value between pixels is obtained.</summary>
        public LineProfileOption ProfileInterpolation
        {
            get => _profileInterpolation;
            set
            {
                if (_profileInterpolation == value) return;
                _profileInterpolation = value;
                NotifyChanged();
            }
        }

        /// <summary>
        /// <c>true</c> while a profile of this line is shown. The host sets it. The profile origin is
        /// drawn only then, and Ctrl-dragging an end rotates the line about that origin.
        /// </summary>
        public bool IsProfileActive { get; set; }

        /// <summary>
        /// Sets how the physical axes of the source view run (see <see cref="RunsFromP1"/>) and the
        /// physical length of one world unit along each of them.
        /// </summary>
        internal void SetProfileAxes(int xSign, int ySign, double xScale, double yScale)
        {
            _profileXSign = xSign < 0 ? -1 : 1;
            _profileYSign = ySign < 0 ? -1 : 1;
            _profileXScale = xScale > 0 ? xScale : 1.0;
            _profileYScale = yScale > 0 ? yScale : 1.0;
        }

        /// <summary>The distance between two world points in physical units.</summary>
        private double PhysicalDistance(Point a, Point b)
        {
            double dx = (b.X - a.X) * _profileXScale;
            double dy = (b.Y - a.Y) * _profileYScale;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>
        /// <c>true</c> when the profile starts at <see cref="P1"/>. The profile starts at the end with the
        /// smaller physical X, and at the one with the smaller physical Y when the X values are equal.
        /// </summary>
        public bool RunsFromP1
        {
            get
            {
                double dx = _profileXSign * (P2.X - P1.X);
                if (dx != 0) return dx > 0;
                return _profileYSign * (P2.Y - P1.Y) >= 0;
            }
        }

        /// <summary>
        /// The origin of the profile as a fraction of the way from <see cref="P1"/> to <see cref="P2"/>,
        /// whichever <see cref="ProfileOrigin"/> is in force.
        /// </summary>
        public double EffectiveOriginT => _profileOrigin switch
        {
            LineProfileOrigin.Center => 0.5,
            LineProfileOrigin.Custom => _originT,
            _ => RunsFromP1 ? 0.0 : 1.0,
        };

        /// <summary>The origin of the profile in world coordinates.</summary>
        public Point OriginPoint
        {
            get
            {
                double t = EffectiveOriginT;
                return new Point(P1.X + t * (P2.X - P1.X), P1.Y + t * (P2.Y - P1.Y));
            }
        }

        private void NotifyChanged() => GeometryChanged?.Invoke(this, (P1, P2));

        public override void BeginResize()
        {
            _originalP1 = P1;
            _originalP2 = P2;
            _originalOrigin = OriginPoint;
            _hasResizeState = true;
        }

        public override void ResetResizeState() => _hasResizeState = false;

        /// <summary>
        /// Menu entry for "Plot Profile".
        /// The host assigns <see cref="OverlayMenuEntry.Handler"/> after the object is added;
        /// the entry is hidden when no handler is assigned.
        /// </summary>
        public OverlayMenuEntry PlotProfile { get; } = new("Plot Profile", icon: MenuIcons.LineChart, tooltip: "Open profile plotter");

        /// <summary>
        /// Menu entry for "Calibrate Scale".
        /// The host assigns <see cref="OverlayMenuEntry.Handler"/> after the object is added;
        /// the entry is hidden when no handler is assigned.
        /// </summary>
        public OverlayMenuEntry CalibrateScale { get; } = new("Calibrate Scale", icon: MenuIcons.Ruler, tooltip: "Calibrate scale from the two end points.");

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

        public LineObject() { SnapMode = PixelSnapMode.Both; }
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
            if (handle == HandleType.Origin) MoveOriginTo(worldNewPos);
            else DragEnd(handle, worldNewPos);
        }

        /// <summary>
        /// Shift: constrains the dragged endpoint along the <em>original</em> line direction
        /// (direction at drag start, not the current direction at the moment Shift is pressed).
        /// Combines with the Ctrl behavior of <see cref="DragEnd"/>.
        /// </summary>
        public override void ResizeConstrained(HandleType handle, Point worldNewPos)
        {
            if (handle == HandleType.Origin) { MoveOriginTo(worldNewPos); return; }

            // Shift: project onto the original line direction (locked at drag start)
            Point refP1 = _hasResizeState ? _originalP1 : P1;
            Point refP2 = _hasResizeState ? _originalP2 : P2;
            Point anchor = handle == HandleType.TopLeft ? refP2 : refP1;
            double ldx = refP2.X - refP1.X;
            double ldy = refP2.Y - refP1.Y;
            double len2 = ldx * ldx + ldy * ldy;
            if (len2 < 1e-10) { DragEnd(handle, worldNewPos); return; }

            double tx = worldNewPos.X - anchor.X;
            double ty = worldNewPos.Y - anchor.Y;
            double t = (tx * ldx + ty * ldy) / len2;
            DragEnd(handle, new Point(anchor.X + t * ldx, anchor.Y + t * ldy));
        }

        /// <summary>
        /// Moves the end named by <paramref name="handle"/> to <paramref name="dragged"/>. Every call
        /// starts again from the geometry at <see cref="BeginResize"/>, so a modifier released
        /// mid-drag restores the other end.
        /// <list type="bullet">
        /// <item>Plain drag: the other end stays. A <see cref="LineProfileOrigin.Custom"/> origin keeps its
        /// distance from that end, so lengthening the dragged side extends the profile without moving the origin.</item>
        /// <item>Ctrl: the line turns and stretches about a pivot - the profile origin while a profile is
        /// shown, otherwise the midpoint. The other end moves to the opposite side of the pivot and keeps
        /// its distance ratio, so a <see cref="LineProfileOrigin.Custom"/> origin stays where it is.
        /// With the pivot on the dragged end there is nothing to turn about, and the drag is a plain one.</item>
        /// </list>
        /// </summary>
        private void DragEnd(HandleType handle, Point dragged)
        {
            bool dragP1 = handle == HandleType.TopLeft;
            Point p1 = _hasResizeState ? _originalP1 : P1;
            Point p2 = _hasResizeState ? _originalP2 : P2;
            Point origin0 = _hasResizeState ? _originalOrigin : OriginPoint;
            Point fixedEnd = dragP1 ? p2 : p1;
            Point dragged0 = dragP1 ? p1 : p2;

            if (CurrentModifiers.HasFlag(KeyModifiers.Control))
            {
                Point pivot = IsProfileActive
                    ? origin0
                    : new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
                double arm0 = Distance(dragged0, pivot);
                if (arm0 > 1e-9)
                {
                    double ratio = Distance(fixedEnd, pivot) / arm0;
                    var other = new Point(pivot.X - (dragged.X - pivot.X) * ratio,
                                          pivot.Y - (dragged.Y - pivot.Y) * ratio);
                    if (dragP1) { P1 = dragged; P2 = other; }
                    else        { P2 = dragged; P1 = other; }
                    if (_profileOrigin == LineProfileOrigin.Custom && IsProfileActive)
                        _originT = FractionOf(pivot);
                    NotifyChanged();
                    return;
                }
            }

            if (dragP1) { P1 = dragged; P2 = fixedEnd; }
            else        { P2 = dragged; P1 = fixedEnd; }

            if (_profileOrigin == LineProfileOrigin.Custom)
            {
                // Physical distances: the profile axis is physical, and the line turns while it is dragged.
                double newLen = PhysicalDistance(dragged, fixedEnd);
                double fromFixed = newLen > 1e-12
                    ? Math.Min(PhysicalDistance(origin0, fixedEnd) / newLen, 1.0)
                    : 0.0;
                _originT = dragP1 ? 1.0 - fromFixed : fromFixed;
            }
            NotifyChanged();
        }

        /// <summary>Moves a <see cref="LineProfileOrigin.Custom"/> origin to the point of the line nearest <paramref name="world"/>.</summary>
        private void MoveOriginTo(Point world)
        {
            if (_profileOrigin != LineProfileOrigin.Custom) return;
            _originT = FractionOf(world);
            NotifyChanged();
        }

        /// <summary>
        /// The fraction of the way from <see cref="P1"/> to <see cref="P2"/> of the point of the line
        /// nearest <paramref name="world"/>, nearest in physical units.
        /// </summary>
        private double FractionOf(Point world)
        {
            double dx = (P2.X - P1.X) * _profileXScale, dy = (P2.Y - P1.Y) * _profileYScale;
            double len2 = dx * dx + dy * dy;
            if (len2 < 1e-24) return _originT;
            double wx = (world.X - P1.X) * _profileXScale, wy = (world.Y - P1.Y) * _profileYScale;
            return Math.Clamp((wx * dx + wy * dy) / len2, 0.0, 1.0);
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

            if (IsProfileActive && !g.SuppressHandles)
            {
                var o = OriginPoint;
                g.DrawCircleHandle(Colors.Yellow, Colors.Black, o.X, o.Y);
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
            // Where the origin sits on an end, that end's handle wins (see above); move the origin
            // from the Properties dialog in that case.
            if (IsProfileActive && _profileOrigin == LineProfileOrigin.Custom
                && Distance(location, vp.WorldToScreen(OriginPoint)) <= HandleSize)
                return HandleType.Origin;
            if (DistanceToSegment(location, sP1, sP2) <= 5.0) return HandleType.Body;
            return HandleType.None;
        }

        public override void Move(double dx, double dy)
        {
            P1 = new Point(P1.X + dx, P1.Y + dy);
            P2 = new Point(P2.X + dx, P2.Y + dy);
            GeometryChanged?.Invoke(this, (P1, P2));
        }

        public override IEnumerable<OverlayMenuEntry>? GetContextMenuItems()
        {
            yield return PlotProfile;
            yield return CalibrateScale;
            yield return OverlayMenuEntry.Separator();
            foreach (var item in base.GetContextMenuItems() ?? Enumerable.Empty<OverlayMenuEntry>())
                yield return item;
        }

        public override Cursor GetCursor(HandleType handle, AvaloniaViewport vp)
        {
            if (handle == HandleType.Body) return new Cursor(StandardCursorType.SizeAll);
            if (handle == HandleType.Origin) return new Cursor(StandardCursorType.Hand);
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
