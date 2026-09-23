using MxPlot.UI.Avalonia.Overlays.Shapes;
using Avalonia;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Maps overlay objects onto the XY-transposed data.
    /// <para>
    /// Overlays live in world space (left-top origin, Y-down, integer = pixel centre), while the
    /// data transposes in data-index space (left-bottom origin, Y-up). For a source of
    /// <c>W × H</c> pixels this makes a world point <c>(x, y)</c> land on <c>(H − 1 − y, W − 1 − x)</c>.
    /// </para>
    /// </summary>
    internal static class OverlayTranspose
    {
        /// <summary>
        /// Returns <paramref name="overlayJson"/> (see <see cref="OverlaySerializer"/>) with every
        /// object moved onto the transposed data. <paramref name="srcWidth"/> and
        /// <paramref name="srcHeight"/> are the pixel counts before the transpose.
        /// </summary>
        public static string TransposeXY(string overlayJson, int srcWidth, int srcHeight)
        {
            var objects = OverlaySerializer.Deserialize(overlayJson);
            TransposeXY(objects, srcWidth, srcHeight);
            return OverlaySerializer.Serialize(objects);
        }

        internal static void TransposeXY(IEnumerable<OverlayObjectBase> objects, int srcWidth, int srcHeight)
        {
            foreach (var obj in objects)
            {
                switch (obj)
                {
                    case LineObject line:
                        line.P1 = MapPoint(line.P1, srcWidth, srcHeight);
                        line.P2 = MapPoint(line.P2, srcWidth, srcHeight);
                        break;

                    case TextObject text:
                        // The text is not rotated, so keep the box size and move only its centre.
                        var c = MapPoint(new Point(text.X + text.Width / 2, text.Y + text.Height / 2), srcWidth, srcHeight);
                        text.X = c.X - text.Width / 2;
                        text.Y = c.Y - text.Height / 2;
                        break;

                    case BoundingBoxBase box:
                        double w = box.Width;
                        double h = box.Height;
                        double x = srcHeight - 1 - (box.Y + h);
                        double y = srcWidth - 1 - (box.X + w);
                        box.X = x;
                        box.Y = y;
                        box.Width = h;
                        box.Height = w;
                        break;
                }
            }
        }

        private static Point MapPoint(Point p, int srcWidth, int srcHeight)
            => new(srcHeight - 1 - p.Y, srcWidth - 1 - p.X);
    }
}
