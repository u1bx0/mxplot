using Avalonia;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Overlays.Shapes;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Rubber-band selection rectangle. Drawn and then discarded without being added to the object list.
    /// Implements <see cref="ISelection"/> to select objects that fall within its bounds.
    /// </summary>
    public sealed class SelectionRect : RectObject, ISelection, ISystemOverlay
    {
        public SelectionRect()
        {
            PenColor = Color.FromArgb(255, 0, 120, 215);
            PenDash  = OverlayDashStyle.Dot;
        }

        public bool Contains(OverlayObjectBase obj)
        {
            var myRect = GetNormalizedRect();
            return obj switch
            {
                LineObject l => myRect.Contains(l.P1) && myRect.Contains(l.P2),
                BoundingBoxBase bb => myRect.Contains(bb.GetNormalizedRect()),
                _ => false,
            };
        }

        public override void Draw(AvaloniaOverlayGraphics g)
        {
            var fill = Color.FromArgb(50, PenColor.R, PenColor.G, PenColor.B);
            g.FillRectangle(fill, X, Y, Width, Height);
            g.DrawRectangle(PenColor, 1.0, OverlayDashStyle.Dot, X, Y, Width, Height);
        }
    }
}
