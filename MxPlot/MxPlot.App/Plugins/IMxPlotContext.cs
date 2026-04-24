using Avalonia.Controls;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Plugins;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MxPlot.App.Plugins
{
    /// <summary>
    /// Exposes the current state of the MxPlot main window to a plugin.
    /// </summary>
    public interface IMxPlotContext
    {
        /// <summary>All datasets currently open in the main window.</summary>
        IReadOnlyList<IMatrixData> OpenDatasets { get; }

        /// <summary>All datasets whose cards are currently selected in the list.</summary>
        IReadOnlyList<IMatrixData> SelectedDatasets { get; }

        /// <summary>The last-focused selected dataset, or <c>null</c> when nothing is selected.</summary>
        IMatrixData? PrimarySelection { get; }

        /// <summary>The main window; use as a dialog owner.</summary>
        TopLevel? Owner { get; }

        /// <summary>Service for opening new plot windows.</summary>
        IPlotWindowService WindowService { get; }

        /// <summary>Loads and opens a file, as if the user had used Open File…</summary>
        Task OpenFileAsync(string path);
    }
}
