using Avalonia;
using MxPlot.UI.Avalonia.Controls;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Snapshot of <see cref="RenderSurface"/> display state used by the overlay system.
    /// Converts between screen coordinates and world coordinates (bitmap pixel-index space),
    /// where an integer world value equals a pixel centre (same convention as the WinForms Viewport).
    /// All <see cref="ViewTransform"/> orientations are handled.
    /// </summary>
    public readonly struct AvaloniaViewport
    {
        public double Zoom { get; init; }
        /// <summary>Render-time (elastic-corrected) X translation in screen pixels.</summary>
        public double TransX { get; init; }
        /// <summary>Render-time (elastic-corrected) Y translation in screen pixels.</summary>
        public double TransY { get; init; }
        public double Ax { get; init; }
        public double Ay { get; init; }
        /// <summary>Effective rendered width in screen pixels (after transform swap).</summary>
        public double EffW { get; init; }
        /// <summary>Effective rendered height in screen pixels (after transform swap).</summary>
        public double EffH { get; init; }
        public ViewTransform Transform { get; init; }

        /// <summary>Approximate scale (screen pixels per world unit) for pen-width scaling.</summary>
        public double CurrentScale => Zoom;

        // ── Screen ↔ World ────────────────────────────────────────────────────

        /// <summary>
        /// Converts a screen position to world coordinates (bitmap pixel-index space,
        /// integer = pixel centre). Accounts for <see cref="ViewTransform"/>.
        /// </summary>
        public Point ScreenToWorld(Point s)
        {
            double bx, by;
            switch (Transform)
            {
                case ViewTransform.FlipH:
                    bx = (TransX + EffW - s.X) / (Zoom * Ax);
                    by = (s.Y - TransY) / (Zoom * Ay);
                    break;
                case ViewTransform.FlipV:
                    bx = (s.X - TransX) / (Zoom * Ax);
                    by = (TransY + EffH - s.Y) / (Zoom * Ay);
                    break;
                case ViewTransform.Rotate180:
                    bx = (TransX + EffW - s.X) / (Zoom * Ax);
                    by = (TransY + EffH - s.Y) / (Zoom * Ay);
                    break;
                case ViewTransform.Rotate90CW:
                    bx = (s.Y - TransY) / (Zoom * Ax);
                    by = (TransX + EffW - s.X) / (Zoom * Ay);
                    break;
                case ViewTransform.Rotate90CCW:
                    bx = (TransY + EffH - s.Y) / (Zoom * Ax);
                    by = (s.X - TransX) / (Zoom * Ay);
                    break;
                case ViewTransform.Transpose:
                    bx = (s.Y - TransY) / (Zoom * Ax);
                    by = (s.X - TransX) / (Zoom * Ay);
                    break;
                default: // None
                    bx = (s.X - TransX) / (Zoom * Ax);
                    by = (s.Y - TransY) / (Zoom * Ay);
                    break;
            }
            // Shift so that integer world coords land on pixel centres
            return new Point(bx - 0.5, by - 0.5);
        }

        /// <summary>
        /// Converts world coordinates (bitmap pixel-index space) back to screen coordinates.
        /// </summary>
        public Point WorldToScreen(Point w)
        {
            double bx = w.X + 0.5;
            double by = w.Y + 0.5;
            double sx, sy;
            switch (Transform)
            {
                case ViewTransform.FlipH:
                    sx = TransX + EffW - bx * Zoom * Ax;
                    sy = by * Zoom * Ay + TransY;
                    break;
                case ViewTransform.FlipV:
                    sx = bx * Zoom * Ax + TransX;
                    sy = TransY + EffH - by * Zoom * Ay;
                    break;
                case ViewTransform.Rotate180:
                    sx = TransX + EffW - bx * Zoom * Ax;
                    sy = TransY + EffH - by * Zoom * Ay;
                    break;
                case ViewTransform.Rotate90CW:
                    sx = TransX + EffW - by * Zoom * Ay;
                    sy = bx * Zoom * Ax + TransY;
                    break;
                case ViewTransform.Rotate90CCW:
                    sx = by * Zoom * Ay + TransX;
                    sy = TransY + EffH - bx * Zoom * Ax;
                    break;
                case ViewTransform.Transpose:
                    sx = by * Zoom * Ay + TransX;
                    sy = bx * Zoom * Ax + TransY;
                    break;
                default: // None
                    sx = bx * Zoom * Ax + TransX;
                    sy = by * Zoom * Ay + TransY;
                    break;
            }
            return new Point(sx, sy);
        }

        // ── Distance helpers ──────────────────────────────────────────────────

        /// <summary>Screen pixels → world units along the bitmap X axis.</summary>
        public double ScreenToWorldDistX(double dist) => dist / (Zoom * Ax);
        /// <summary>Screen pixels → world units along the bitmap Y axis.</summary>
        public double ScreenToWorldDistY(double dist) => dist / (Zoom * Ay);
        /// <summary>World units → screen pixels along the bitmap X axis.</summary>
        public double WorldToScreenDistX(double dist) => dist * Zoom * Ax;
        /// <summary>World units → screen pixels along the bitmap Y axis.</summary>
        public double WorldToScreenDistY(double dist) => dist * Zoom * Ay;
    }
}
