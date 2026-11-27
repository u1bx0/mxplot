using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    public enum TargetStyle
    {
        /// <summary>An ellipse with four crosshair arms and an optional centre gap.</summary>
        Reticle,
        /// <summary>A "+" marker spanning the bounding box.</summary>
        Plus,
        /// <summary>An "x" marker spanning the bounding box.</summary>
        Cross,
    }

    /// <summary>
    /// A targeting overlay with a <see cref="Style"/>.
    /// <para>
    /// <see cref="TargetStyle.Reticle"/>: an ellipse with four crosshair arms that pass through the
    /// centre and extend beyond the oval by 10% of the larger dimension. A gap of
    /// <see cref="CenterGap"/> pixels (diameter) is left at the exact centre so the target point is
    /// clearly visible.
    /// </para>
    /// <para>
    /// <see cref="TargetStyle.Plus"/> / <see cref="TargetStyle.Cross"/>: a square marker whose
    /// lines span the bounding box. Its handles and hit area are enlarged on screen when the marker
    /// is too small to grab.
    /// </para>
    /// Default snap mode is <see cref="PixelSnapMode.None"/>.
    /// </summary>
    public sealed class TargetingObject : BoundingBoxBase
    {
        public TargetingObject()
        {
            SnapMode = PixelSnapMode.None;
            PenColor = Color.FromRgb(255,128,128);
        }

        // Fraction of max
        private const double ArmFraction = 0.1;

        public const double DefaultCenterGap = 3.0;

        // Smallest side of a marker in pixel-index units.
        public const double MinMarkerSize = 1.0;

        // Minimum on-screen side (px) of the handle / hit box of a marker.
        private const double MarkerMinScreenSize = 14.0;

        private TargetStyle _style = TargetStyle.Reticle;

        public TargetStyle Style
        {
            get => _style;
            set
            {
                if (_style == value) return;
                _style = value;
                if (value != TargetStyle.Reticle) MakeSquareAboutCenter();
            }
        }

        public bool IsMarker => _style != TargetStyle.Reticle;

        // Dragging snaps the target point (the centre), not the pointer.
        public override Point? GetSnapAnchor() => new Point(X + Width / 2, Y + Height / 2);

        protected override double MinInteractionScreenSize => IsMarker ? MarkerMinScreenSize : 0;

        /// <summary>
        /// Resizes about the centre. A marker is always square, so it takes <paramref name="width"/>
        /// for both sides (and never goes below <see cref="MinMarkerSize"/>).
        /// </summary>
        public void ResizeAboutCenter(double width, double height)
        {
            if (IsMarker) width = height = Math.Max(MinMarkerSize, width);
            double cx = X + Width / 2, cy = Y + Height / 2;
            Width = width;
            Height = height;
            X = cx - width / 2;
            Y = cy - height / 2;
        }

        private void MakeSquareAboutCenter()
        {
            double side = Math.Max(Math.Abs(Width), Math.Abs(Height));
            if (side == 0) return;
            ResizeAboutCenter(side, side);
        }

        private double _centerGap = DefaultCenterGap;

        /// <summary>
        /// Diameter, in pixel-index units, of the undrawn area at the centre of the crosshair.
        /// The horizontal arms are hidden while the gap is at least as wide as the overlay, and the
        /// vertical arms while it is at least as tall.
        /// </summary>
        public double CenterGap
        {
            get => _centerGap;
            set => _centerGap = Math.Max(0, value);
        }

        // The start point (the click) is the centre and the drag distance is the half extent, so a
        // snapped start point is the snapped target point.
        public override void SetCreationBounds(Point start, Point end)
            => SetCreationBounds(start, end, KeyModifiers.None);

        public override void SetCreationBounds(Point start, Point end, KeyModifiers modifiers)
        {
            double halfW = Math.Abs(end.X - start.X);
            double halfH = Math.Abs(end.Y - start.Y);
            if (IsMarker || modifiers.HasFlag(KeyModifiers.Shift))
                halfW = halfH = Math.Max(halfW, halfH);
            X = start.X - halfW;
            Y = start.Y - halfH;
            Width = 2 * halfW;
            Height = 2 * halfH;
        }

        public override string? GetInfo(IMatrixData? data)
        {
            double cx = X + Width / 2;
            double cy = Y + Height / 2;

            if (data == null || (data.XStep == 0 && data.YStep == 0))
                return $"Target: ({FmtLen(cx)}, {FmtLen(cy)}) px";

            // Convert pixel-index centre to physical coordinates (standard flipY convention)
            double physX = data.XMin + cx * Math.Abs(data.XStep);
            double physY = data.YMax - cy * Math.Abs(data.YStep);
            string xu = data.XUnit ?? "";
            string yu = data.YUnit ?? "";

            if (xu == yu && xu.Length > 0)
                return $"Target: ({FmtLen(physX)}, {FmtLen(physY)}) {xu}";
            else if (xu == yu)
                return $"Target: ({FmtLen(physX)}, {FmtLen(physY)})";
            else
                return $"Target: ({FmtLen(physX)} {xu}, {FmtLen(physY)} {yu})";
        }

        public override void Draw(AvaloniaOverlayGraphics g)
        {
            if (Width == 0 || Height == 0) return;

            var (color, dash) = GetDrawingPen();
            if (IsMarker)
            {
                Span<(double X1, double Y1, double X2, double Y2)> segs = stackalloc (double, double, double, double)[2];
                GetMarkerSegments(segs);
                foreach (var sg in segs)
                    g.DrawLine(color, PenWidth, dash, sg.X1, sg.Y1, sg.X2, sg.Y2, IsScaledPenWidth);
                DrawHandles(g);
                return;
            }

            double cx = X + Width / 2;
            double cy = Y + Height / 2;
            double arm = Math.Max(Math.Abs(Width), Math.Abs(Height)) * ArmFraction;

            // The fill goes underneath so that it never covers the crosshair.
            if (IsFilled)
                g.FillEllipse(FillColor, X, Y, Width, Height);

            // Four half-segments — each stops half the gap short of the centre.
            // An axis whose arms would start outside the oval (gap >= extent) is not drawn at all.
            double half = _centerGap / 2;
            if (_centerGap < Math.Abs(Width))
            {
                g.DrawLine(color, PenWidth, dash, X - arm, cy, cx - half, cy, IsScaledPenWidth); // left
                g.DrawLine(color, PenWidth, dash, cx + half, cy, X + Width + arm, cy, IsScaledPenWidth); // right
            }
            if (_centerGap < Math.Abs(Height))
            {
                g.DrawLine(color, PenWidth, dash, cx, Y - arm, cx, cy - half, IsScaledPenWidth); // top
                g.DrawLine(color, PenWidth, dash, cx, cy + half, cx, Y + Height + arm, IsScaledPenWidth); // bottom
            }

            g.DrawEllipse(color, PenWidth, dash, X, Y, Width, Height, IsScaledPenWidth);

            DrawHandles(g);
        }

        public override HandleType HitTest(Point location, AvaloniaViewport vp)
        {
            // Handles (only when selected)
            var handle = HitTestBoundingBox(location, vp, testEdges: false, testInterior: false);
            if (handle != HandleType.None) return handle;

            if (IsMarker) return HitTestMarker(location, vp) ? HandleType.Body : HandleType.None;

            // Ellipse perimeter / filled interior
            if (HitTestEllipseLocal(location, vp)) return HandleType.Body;

            // Crosshair arm segments
            if (HitTestArms(location, vp)) return HandleType.Body;

            return HandleType.None;
        }

        private bool HitTestEllipseLocal(Point location, AvaloniaViewport vp, double threshold = 5.0)
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

        private void GetMarkerSegments(Span<(double X1, double Y1, double X2, double Y2)> segs)
        {
            double cx = X + Width / 2, cy = Y + Height / 2;
            if (_style == TargetStyle.Plus)
            {
                segs[0] = (X, cy, X + Width, cy);
                segs[1] = (cx, Y, cx, Y + Height);
            }
            else
            {
                segs[0] = (X, Y, X + Width, Y + Height);
                segs[1] = (X + Width, Y, X, Y + Height);
            }
        }

        private bool HitTestMarker(Point location, AvaloniaViewport vp, double threshold = 5.0)
        {
            var (bx, by, bw, bh, expanded) = GetInteractionBounds(vp);
            if (expanded)
            {
                var w = vp.ScreenToWorld(location);
                return w.X >= bx && w.X <= bx + bw && w.Y >= by && w.Y <= by + bh;
            }

            Span<(double X1, double Y1, double X2, double Y2)> segs = stackalloc (double, double, double, double)[2];
            GetMarkerSegments(segs);
            foreach (var sg in segs)
            {
                if (DistanceToSegment(location, vp.WorldToScreen(new Point(sg.X1, sg.Y1)),
                        vp.WorldToScreen(new Point(sg.X2, sg.Y2))) <= threshold)
                    return true;
            }
            return false;
        }

        private bool HitTestArms(Point location, AvaloniaViewport vp, double threshold = 5.0)
        {
            double cx = X + Width / 2;
            double cy = Y + Height / 2;
            double arm = Math.Max(Math.Abs(Width), Math.Abs(Height)) * ArmFraction;

            var toScreen = (double wx, double wy) => vp.WorldToScreen(new Point(wx, wy));

            // Arms that are not drawn (gap at least as large as the extent) are not hit either.
            return (_centerGap < Math.Abs(Width) &&
                    DistanceToSegment(location, toScreen(X - arm, cy), toScreen(X + Width + arm, cy)) <= threshold) ||
                   (_centerGap < Math.Abs(Height) &&
                    DistanceToSegment(location, toScreen(cx, Y - arm), toScreen(cx, Y + Height + arm)) <= threshold);
        }

        public override void Resize(HandleType handle, Point worldNewPos)
        {
            // Resizing always keeps the centre fixed (the same behaviour as holding Ctrl).
            CurrentModifiers |= KeyModifiers.Control;
            if (!IsMarker)
            {
                base.Resize(handle, worldNewPos);
                return;
            }

            ResizeBoundingBoxCtrl(handle, worldNewPos);

            // A marker stays square: the larger extent defines the side.
            double side = Math.Max(Math.Max(Width, Height), MinMarkerSize);
            double cx = X + Width / 2, cy = Y + Height / 2;
            X = cx - side / 2;
            Y = cy - side / 2;
            Width = Height = side;
            RaiseGeometryChanged();
        }

        public override void ResizeConstrained(HandleType handle, Point worldNewPos)
        {
            CurrentModifiers |= KeyModifiers.Control;
            base.ResizeConstrained(handle, worldNewPos);
        }

        public override Cursor GetCursor(HandleType handle, AvaloniaViewport vp) =>
            GetResizeCursor(handle, vp);

        public override IEnumerable<OverlayMenuEntry>? GetContextMenuItems()
        {
            yield return OverlayMenuEntry.Separator();
            foreach (var item in base.GetContextMenuItems() ?? [])
                yield return item;
        }
    }
}
