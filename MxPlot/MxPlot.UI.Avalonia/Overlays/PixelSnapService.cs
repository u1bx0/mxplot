using Avalonia;
using System;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Snaps world-space coordinates (pixel-index space, integer = pixel centre)
    /// to the nearest pixel centre or pixel edge.
    /// Logic is identical to the WinForms PixelSnapService.
    /// </summary>
    public sealed class PixelSnapService
    {
        public Point Snap(Point worldPos, PixelSnapMode mode) => mode switch
        {
            PixelSnapMode.Center => new Point(
                Math.Floor(worldPos.X + 0.5),
                Math.Floor(worldPos.Y + 0.5)),
            PixelSnapMode.Corner => new Point(
                Math.Floor(worldPos.X) + 0.5,
                Math.Floor(worldPos.Y) + 0.5),
            PixelSnapMode.Both => SnapToBoth(worldPos),
            _ => worldPos,
        };

        private static Point SnapToBoth(Point p) =>
            new(SnapAxis(p.X), SnapAxis(p.Y));

        private static double SnapAxis(double x)
        {
            double center = Math.Floor(x + 0.5);
            double corner = Math.Floor(x) + 0.5;
            return Math.Abs(x - center) <= Math.Abs(x - corner) ? center : corner;
        }
    }
}
