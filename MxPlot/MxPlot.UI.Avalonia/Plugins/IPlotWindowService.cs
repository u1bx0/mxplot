using MxPlot.Core;

namespace MxPlot.UI.Avalonia.Plugins
{
    /// <summary>
    /// Provides window-creation services that plugins can invoke to open new plot windows.
    /// Implementations are responsible for dispatching to the UI thread and wiring
    /// the new window into the application's window management infrastructure.
    /// </summary>
    public interface IPlotWindowService
    {
        /// <summary>
        /// Opens a new <see cref="Views.MatrixPlotter"/> window for the supplied data.
        /// The call is fire-and-forget; the window is shown asynchronously on the UI thread.
        /// </summary>
        void ShowMatrixPlotter(IMatrixData data, string? title = null);
    }
}
