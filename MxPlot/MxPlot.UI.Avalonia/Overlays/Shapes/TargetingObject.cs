using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    /// <summary>
    /// A targeting-reticle overlay: an ellipse with four crosshair arms that pass through the
    /// centre and extend beyond the oval by 10% of the larger dimension.
    /// A 1.5-pixel gap is left at the exact centre so the target point is clearly visible.
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

        public override void SetCreationBounds(Point start, Point end)
            => SetCreationBoundsInternal(start, end, forceSquare: false);

        public override void SetCreationBounds(Point start, Point end, KeyModifiers modifiers)
        {
            bool isShift = modifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl = modifiers.HasFlag(KeyModifiers.Control);
            if (isCtrl)
            {
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
            double cx = X + Width / 2;
            double cy = Y + Height / 2;
            double arm = Math.Max(Math.Abs(Width), Math.Abs(Height)) * ArmFraction;

            // Gap at centre: 1.5 pixel-index units in each axis direction
            const double gx = 1.5;
            const double gy = 1.5;

            // Four half-segments — each stops 1.5 index units short of the centre
            g.DrawLine(color, PenWidth, dash, X - arm, cy, cx - gx, cy, IsScaledPenWidth); // left
            g.DrawLine(color, PenWidth, dash, cx + gx, cy, X + Width + arm, cy, IsScaledPenWidth); // right
            g.DrawLine(color, PenWidth, dash, cx, Y - arm, cx, cy - gy, IsScaledPenWidth); // top
            g.DrawLine(color, PenWidth, dash, cx, cy + gy, cx, Y + Height + arm, IsScaledPenWidth); // bottom

            // Ellipse drawn last so it sits on top of the crosshair lines
            if (IsFilled)
                g.FillEllipse(FillColor, X, Y, Width, Height);
            g.DrawEllipse(color, PenWidth, dash, X, Y, Width, Height, IsScaledPenWidth);

            DrawHandles(g);
        }

        public override HandleType HitTest(Point location, AvaloniaViewport vp)
        {
            // Handles (only when selected)
            var handle = HitTestBoundingBox(location, vp, testEdges: false, testInterior: false);
            if (handle != HandleType.None) return handle;

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

        private bool HitTestArms(Point location, AvaloniaViewport vp, double threshold = 5.0)
        {
            double cx = X + Width / 2;
            double cy = Y + Height / 2;
            double arm = Math.Max(Math.Abs(Width), Math.Abs(Height)) * ArmFraction;

            var toScreen = (double wx, double wy) => vp.WorldToScreen(new Point(wx, wy));

            return DistanceToSegment(location, toScreen(X - arm, cy), toScreen(X + Width + arm, cy)) <= threshold ||
                   DistanceToSegment(location, toScreen(cx, Y - arm), toScreen(cx, Y + Height + arm)) <= threshold;
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
