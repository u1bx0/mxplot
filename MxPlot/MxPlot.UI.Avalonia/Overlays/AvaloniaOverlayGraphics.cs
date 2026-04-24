using Avalonia;
using Avalonia.Media;
using System;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Wraps Avalonia <see cref="DrawingContext"/> with world-to-screen coordinate conversion,
    /// mirroring the role of WinForms <c>MxGraphics</c>.
    /// </summary>
    public sealed class AvaloniaOverlayGraphics
    {
        private readonly DrawingContext _ctx;
        private readonly AvaloniaViewport _vp;

        public AvaloniaOverlayGraphics(DrawingContext ctx, AvaloniaViewport vp)
        {
            _ctx = ctx;
            _vp  = vp;
        }

        /// <summary>
        /// When <c>true</c>, handle squares are suppressed (e.g. during clipboard capture).
        /// </summary>
        internal bool SuppressHandles { get; set; }

        // ── Coordinate helpers ────────────────────────────────────────────────

        public Point WorldToScreen(double wx, double wy) =>
            _vp.WorldToScreen(new Point(wx, wy));

        // ── Pen / Brush factories ─────────────────────────────────────────────

        public static IPen MakePen(Color color, double width, OverlayDashStyle dash = OverlayDashStyle.Solid,
            bool scaleWithZoom = false, double zoom = 1.0)
        {
            double w = scaleWithZoom ? width * zoom : width;
            DashStyle? ds = dash switch
            {
                OverlayDashStyle.Dash => new DashStyle([4, 2], 0),
                OverlayDashStyle.Dot  => new DashStyle([1, 2], 0),
                _                    => null,
            };
            return new Pen(new SolidColorBrush(color), w, ds);
        }

        public static IBrush MakeBrush(Color color) => new SolidColorBrush(color);

        // ── Drawing primitives ────────────────────────────────────────────────

        public void DrawLine(Color color, double width, OverlayDashStyle dash,
            double x1, double y1, double x2, double y2, bool scaleWithZoom = false)
        {
            var p1 = _vp.WorldToScreen(new Point(x1, y1));
            var p2 = _vp.WorldToScreen(new Point(x2, y2));
            _ctx.DrawLine(MakePen(color, width, dash, scaleWithZoom, _vp.CurrentScale), p1, p2);
        }

        public void DrawRectangle(Color penColor, double penWidth, OverlayDashStyle dash,
            double x, double y, double w, double h, bool scaleWithZoom = false)
        {
            var p1 = _vp.WorldToScreen(new Point(x,     y));
            var p2 = _vp.WorldToScreen(new Point(x + w, y + h));
            double sx = Math.Min(p1.X, p2.X), sy = Math.Min(p1.Y, p2.Y);
            double sw = Math.Abs(p2.X - p1.X), sh = Math.Abs(p2.Y - p1.Y);
            if (sw < 1 && sh < 1) return;
            _ctx.DrawRectangle(null, MakePen(penColor, penWidth, dash, scaleWithZoom, _vp.CurrentScale),
                new Rect(sx, sy, sw, sh));
        }

        public void FillRectangle(Color brushColor, double x, double y, double w, double h)
        {
            var p1 = _vp.WorldToScreen(new Point(x,     y));
            var p2 = _vp.WorldToScreen(new Point(x + w, y + h));
            double sx = Math.Min(p1.X, p2.X), sy = Math.Min(p1.Y, p2.Y);
            double sw = Math.Abs(p2.X - p1.X), sh = Math.Abs(p2.Y - p1.Y);
            _ctx.DrawRectangle(MakeBrush(brushColor), null, new Rect(sx, sy, sw, sh));
        }

        public void DrawEllipse(Color penColor, double penWidth, OverlayDashStyle dash,
            double x, double y, double w, double h, bool scaleWithZoom = false)
        {
            var p1 = _vp.WorldToScreen(new Point(x,     y));
            var p2 = _vp.WorldToScreen(new Point(x + w, y + h));
            double sx = Math.Min(p1.X, p2.X), sy = Math.Min(p1.Y, p2.Y);
            double sw = Math.Abs(p2.X - p1.X), sh = Math.Abs(p2.Y - p1.Y);
            if (sw < 1 && sh < 1) return;
            _ctx.DrawEllipse(null, MakePen(penColor, penWidth, dash, scaleWithZoom, _vp.CurrentScale),
                new Point(sx + sw / 2, sy + sh / 2), sw / 2, sh / 2);
        }

        public void FillEllipse(Color brushColor, double x, double y, double w, double h)
        {
            var p1 = _vp.WorldToScreen(new Point(x,     y));
            var p2 = _vp.WorldToScreen(new Point(x + w, y + h));
            double sx = Math.Min(p1.X, p2.X), sy = Math.Min(p1.Y, p2.Y);
            double sw = Math.Abs(p2.X - p1.X), sh = Math.Abs(p2.Y - p1.Y);
            if (sw < 1 || sh < 1) return;
            _ctx.DrawEllipse(MakeBrush(brushColor), null,
                new Point(sx + sw / 2, sy + sh / 2), sw / 2, sh / 2);
        }

        /// <summary>
        /// Draws a fixed-pixel-size handle square centred at the given world coordinate.
        /// The square is always <paramref name="screenSize"/> pixels regardless of zoom.
        /// </summary>
        public void DrawHandle(Color fillColor, Color borderColor, double wx, double wy,
            double screenSize = 8.0)
        {
            var pt = _vp.WorldToScreen(new Point(wx, wy));
            DrawHandleAtScreen(fillColor, borderColor, pt, screenSize);
        }

        /// <summary>Draws a line at screen-space coordinates (zoom-independent).</summary>
        public void DrawLineAtScreen(Color color, double width, Point p1, Point p2) =>
            _ctx.DrawLine(MakePen(color, width), p1, p2);

        /// <summary>
        /// Draws a text label at a fixed screen position with a filled background rectangle.
        /// Intended for zoom-independent annotations such as ROI size labels.
        /// </summary>
        public void DrawStringAtScreen(string text, Color foreground, Color background,
            Point screenPos, double fontSize = 11.0, double pad = 3.0)
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                fontSize,
                new SolidColorBrush(foreground));
            double w = ft.Width  + pad * 2;
            double h = ft.Height + pad * 2;
            _ctx.DrawRectangle(MakeBrush(background), null,
                new Rect(screenPos.X, screenPos.Y, w, h));
            _ctx.DrawText(ft, new Point(screenPos.X + pad, screenPos.Y + pad));
        }

        /// <summary>Draws a fixed-pixel-size handle square centred at a screen position.</summary>
        public void DrawHandleAtScreen(Color fillColor, Color borderColor, Point screenPos,
            double screenSize = 8.0)
        {
            double half = screenSize / 2.0;
            var rect = new Rect(screenPos.X - half, screenPos.Y - half, screenSize, screenSize);
            _ctx.DrawRectangle(MakeBrush(fillColor),
                               new Pen(new SolidColorBrush(borderColor), 1.0),
                               rect);
        }

        /// <summary>
        /// Draws text at the given world position. The font size is always in screen pixels
        /// (zoom-independent) unless <paramref name="scaleWithZoom"/> is true.
        /// </summary>
        public void DrawString(string text, Color color, double wx, double wy,
            double fontSize = 12.0, string fontFamily = "Arial", bool scaleWithZoom = false)
        {
            var screenPos = _vp.WorldToScreen(new Point(wx, wy));
            double size = scaleWithZoom ? fontSize * _vp.CurrentScale : fontSize;
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily),
                size,
                new SolidColorBrush(color));
            _ctx.DrawText(ft, screenPos);
        }

        /// <summary>
        /// Draws text clipped to a world-space bounding box.
        /// Text is left-top aligned with <paramref name="screenPad"/> pixels of inner padding
        /// and word-wrapped at the right edge. The bounding box clips any overflow via
        /// <see cref="DrawingContext.PushClip"/>, so text is always visible even when the box
        /// is smaller than one line of text.
        /// When <paramref name="scaleWithZoom"/> is <c>true</c>, font size is multiplied by the
        /// current viewport scale so the text appears the same physical size as the box grows.
        /// </summary>
        public void DrawStringInRect(string text, Color color,
            double wx, double wy, double ww, double wh,
            double screenPad = 4.0, double fontSize = 12.0, string fontFamily = "Arial",
            bool scaleWithZoom = false)
        {
            var sp1 = _vp.WorldToScreen(new Point(wx,      wy));
            var sp2 = _vp.WorldToScreen(new Point(wx + ww, wy + wh));
            double sx = Math.Min(sp1.X, sp2.X), sy = Math.Min(sp1.Y, sp2.Y);
            double sw = Math.Abs(sp2.X - sp1.X), sh = Math.Abs(sp2.Y - sp1.Y);
            if (sw < 1 || sh < 1) return;

            double size = scaleWithZoom ? fontSize * _vp.CurrentScale : fontSize;
            var ft = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily),
                size,
                new SolidColorBrush(color));
            // MaxTextWidth enables word-wrapping before the right-side padding zone.
            // MaxTextHeight is intentionally omitted: PushClip handles vertical overflow,
            // ensuring text is always rendered (and clipped) even when the box is tiny.
            ft.MaxTextWidth = Math.Max(1.0, sw - screenPad * 2);

            using var _ = _ctx.PushClip(new Rect(sx, sy, sw, sh));
            _ctx.DrawText(ft, new Point(sx + screenPad, sy + screenPad));
        }

        /// <summary>Returns the screen-space size of the given text (for background rect sizing).</summary>
        public Size MeasureString(string text, double fontSize = 12.0, string fontFamily = "Arial")
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(fontFamily),
                fontSize,
                Brushes.Black);
            return new Size(ft.Width, ft.Height);
        }
    }
}
