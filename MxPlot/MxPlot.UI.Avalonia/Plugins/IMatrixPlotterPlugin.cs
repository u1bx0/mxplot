namespace MxPlot.UI.Avalonia.Plugins
{
    /// <summary>
    /// A plugin that adds a command to the <see cref="Views.MatrixPlotter"/> Plugins menu.
    /// </summary>
    public interface IMatrixPlotterPlugin
    {
        /// <summary>Label shown in the Plugins menu.</summary>
        string CommandName { get; }

        /// <summary>Short description shown as a tooltip.</summary>
        string Description { get; }

        /// <summary>
        /// Optional group label. Plugins with the same non-null group name are collected
        /// under a shared <see cref="Avalonia.Controls.MenuItem"/> sub-header in the Plugins tab.
        /// Return <c>null</c> (default) to place the item at the top level.
        /// </summary>
        string? GroupName => null;

        /// <summary>
        /// Called when the user invokes the command.
        /// Runs on the UI thread; do heavy work on a background thread if needed.
        /// </summary>
        void Run(IMatrixPlotterContext context);
    }
}
