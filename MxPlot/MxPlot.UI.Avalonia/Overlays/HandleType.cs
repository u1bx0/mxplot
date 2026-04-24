namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Identifies which part of an overlay object the user is interacting with.
    /// </summary>
    public enum HandleType
    {
        None,
        Body,
        TopLeft, TopCenter, TopRight,
        MiddleLeft, MiddleRight,
        BottomLeft, BottomCenter, BottomRight,
        StartPoint, EndPoint
    }
}
