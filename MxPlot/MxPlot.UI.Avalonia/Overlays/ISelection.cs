namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Implemented by rubber-band selection tools.
    /// The object evaluates whether other overlay objects fall within its bounds.
    /// </summary>
    public interface ISelection
    {
        bool Contains(OverlayObjectBase obj);
    }
}
