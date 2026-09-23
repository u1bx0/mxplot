using Avalonia;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Controls;
using System;

namespace MxPlot.UI.Avalonia.Tools
{
    /// <summary>
    /// Provides context passed to an <see cref="IPlotterTool"/> on invocation.
    /// </summary>
    public sealed class PlotterToolContext
    {
        /// <summary>The main view that the tool operates on.</summary>
        public required MxView MainView { get; init; }

        /// <summary>The host visual (the plotter window) used to locate the <c>OverlayLayer</c>.</summary>
        public required Visual HostVisual { get; init; }

        /// <summary>The currently displayed data, or <c>null</c> if no data is loaded.</summary>
        public IMatrixData? Data { get; init; }

        /// <summary>The orthogonal panel when orthogonal views are active, otherwise <c>null</c>.</summary>
        public OrthogonalPanel? OrthoPanel { get; init; }

        /// <summary>The name of the depth axis when orthogonal views are active, otherwise <c>null</c>.</summary>
        public string? DepthAxisName { get; init; }
    }

    /// <summary>
    /// Represents an interactive plotter tool, which stays active on its window until it completes or is cancelled:<br/>
    /// <see cref="Invoke"/> → [user interaction] → <see cref="Completed"/> | <see cref="Cancelled"/> → <see cref="IDisposable.Dispose"/>.
    /// </summary>
    /// <remarks>
    /// The host (<c>MatrixPlotter</c>) calls <see cref="Invoke"/> once to start the tool.
    /// The tool manages its own UI elements (ROIs, panels) and fires either
    /// <see cref="Completed"/> or <see cref="Cancelled"/> when the user finishes.
    /// <see cref="IDisposable.Dispose"/> may be called by the host at any time to force-cancel
    /// a running tool without firing events.
    /// </remarks>
    public interface IPlotterTool : IDisposable
    {
        /// <summary>
        /// Fired when the tool completes successfully.
        /// The argument carries the result <see cref="IMatrixData"/>, or <c>null</c> if no data change occurred.
        /// </summary>
        event EventHandler<IMatrixData?>? Completed;

        /// <summary>Fired when the user explicitly cancels the tool.</summary>
        event EventHandler? Cancelled;

        /// <summary>
        /// Starts the tool: creates required overlay objects and enters the interaction phase.
        /// Must be called exactly once.
        /// </summary>
        void Invoke(PlotterToolContext context);

        /// <summary>
        /// Called by the host when the tool context changes while the tool is running
        /// (e.g., the depth axis is switched or the data is replaced).
        /// The tool should re-validate and clamp any out-of-bounds ROIs, then refresh overlays.
        /// Default implementation is a no-op; override when the tool holds side-view state.
        /// </summary>
        void NotifyContextChanged(PlotterToolContext newContext) { }
    }
}
