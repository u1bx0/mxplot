namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Controls whether overlay world-coordinate positions are snapped to pixel boundaries.
    /// World integer values = pixel centres; half-integers = pixel edges.
    /// </summary>
    public enum PixelSnapMode
    {
        None,
        Center,
        Corner,
        Both,
    }

    /// <summary>Pen dash style for overlay objects.</summary>
    public enum OverlayDashStyle { Solid, Dash, Dot }
}
