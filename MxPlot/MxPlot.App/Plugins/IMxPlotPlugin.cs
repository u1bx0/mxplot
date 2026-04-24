namespace MxPlot.App.Plugins
{
    /// <summary>
    /// A plugin that adds a command to the MxPlot main-window hamburger menu.
    /// </summary>
    public interface IMxPlotPlugin
    {
        /// <summary>Label shown in the Plugins section of the hamburger menu.</summary>
        string CommandName { get; }

        /// <summary>Short description shown as a tooltip.</summary>
        string Description { get; }

        /// <summary>
        /// Called when the user invokes the command.
        /// Runs on the UI thread; do heavy work on a background thread if needed.
        /// </summary>
        void Run(IMxPlotContext context);
    }
}
