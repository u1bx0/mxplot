using MxPlot.Core;

namespace MxPlot.UI.Avalonia.Plugins
{
    /// <summary>
    /// What a synced Window's transform is given each time its source Window changes; see
    /// <see cref="IPlotWindowService.ShowSyncedMatrixPlotter"/>.
    /// </summary>
    /// <param name="Frame">
    /// The part of the source that is on display: the active frame, or the whole channel cube while the
    /// source is in Composite mode. Treat it as read-only.
    /// </param>
    /// <param name="Source">
    /// The source's complete data, for a transform that also needs frames other than the active one.
    /// Treat it as read-only.
    /// </param>
    public readonly record struct PlotSyncInput(IMatrixData Frame, IMatrixData Source);
}
