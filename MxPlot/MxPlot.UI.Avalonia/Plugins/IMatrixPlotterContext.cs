using Avalonia.Controls;
using MxPlot.Core;

namespace MxPlot.UI.Avalonia.Plugins
{
    /// <summary>
    /// Provides access to the current state of a <see cref="Views.MatrixPlotter"/> window
    /// and services that a plugin can use to inspect or modify the display.
    /// </summary>
    public interface IMatrixPlotterContext
    {
        /// <summary>The data currently displayed in the plotter.</summary>
        IMatrixData Data { get; }

        /// <summary>
        /// The lower bound of the fixed display range (LUT minimum).
        /// Setting this value also enables fixed-range mode.
        /// </summary>
        double DisplayMinValue { get; set; }

        /// <summary>
        /// The upper bound of the fixed display range (LUT maximum).
        /// Setting this value also enables fixed-range mode.
        /// </summary>
        double DisplayMaxValue { get; set; }

        /// <summary>The host window; use as dialog owner.</summary>
        TopLevel? Owner { get; }

        /// <summary>Service for opening new plot windows from within the plugin.</summary>
        IPlotWindowService WindowService { get; }
    }
}
