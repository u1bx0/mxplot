using MxPlot.Core;
using MxPlot.UI.Avalonia.Tools;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Interactive tool lifecycle ────────────────────────────────────────

        /// <summary>
        /// Starts an <see cref="IPlotterTool"/>, disposing any currently active tool first.
        /// Subscribes to <see cref="IPlotterTool.Completed"/> and <see cref="IPlotterTool.Cancelled"/>
        /// to update <see cref="_activeTool"/> and apply result data when the tool finishes.
        /// </summary>
        private void InvokeTool(IPlotterTool tool)
        {
            _activeTool?.Dispose();
            _activeTool = tool;
            tool.Completed += OnToolCompleted;
            tool.Cancelled += OnToolCancelled;
            tool.Invoke(CreateToolContext());
        }

        private void OnToolCompleted(object? sender, IMatrixData? result)
        {
            if (sender is IPlotterTool t)
            {
                t.Completed -= OnToolCompleted;
                t.Cancelled -= OnToolCancelled;
            }
            _activeTool = null;
            if (result != null) SetMatrixData(result);
        }

        private void OnToolCancelled(object? sender, EventArgs e)
        {
            if (sender is IPlotterTool t)
            {
                t.Completed -= OnToolCompleted;
                t.Cancelled -= OnToolCancelled;
            }
            _activeTool = null;
        }

        private PlotterToolContext CreateToolContext() => new()
        {
            MainView = _view,
            HostVisual = this,
            Data = _currentData,
            OrthoPanel = _orthoPanel.ShowRight ? _orthoPanel : null,
            DepthAxisName = _orthoController.ActiveAxisName,
        };
    }
}
