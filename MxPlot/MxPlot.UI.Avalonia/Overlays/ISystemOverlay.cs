namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Marker interface for system/functional overlays (e.g. crosshair indicator).
    /// Objects implementing this interface are always drawn regardless of
    /// <see cref="OverlayManager.OverlaysVisible"/>.
    /// </summary>
    public interface ISystemOverlay { }
}
