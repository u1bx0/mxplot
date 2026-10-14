namespace MxPlot.App.Plugins
{
    /// <summary>
    /// A plugin that adds a command to the MxPlot main-window hamburger menu.
    /// </summary>
    public interface IMxPlotAppPlugin
    {
        /// <summary>Label shown in the Plugins section of the hamburger menu.</summary>
        string CommandName { get; }

        /// <summary>Short description shown as a tooltip.</summary>
        string Description { get; }

        /// <summary>
        /// Optional group label. Plugins with the same non-null group name are collected
        /// under a shared submenu in the Tools menu. Return <c>null</c> (default) to place
        /// the item at the top level.
        /// </summary>
        string? GroupName => null;

        /// <summary>
        /// Called when the user invokes the command.
        /// Runs on the UI thread; do heavy work on a background thread if needed.
        /// </summary>
        void Run(IMxPlotAppContext context);
    }
}
