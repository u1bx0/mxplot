using Avalonia;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Controls;
using System;

namespace MxPlot.UI.Avalonia.Actions
{
    /// <summary>
    /// Provides context passed to an <see cref="IPlotterAction"/> on invocation.
    /// </summary>
    public sealed class PlotterActionContext
    {
        /// <summary>The main view that the action operates on.</summary>
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
    /// Represents an interactive plotter action with a defined lifecycle:<br/>
    /// <see cref="Invoke"/> → [user interaction] → <see cref="Completed"/> | <see cref="Cancelled"/> → <see cref="IDisposable.Dispose"/>.
    /// </summary>
    /// <remarks>
    /// The host (<c>MatrixPlotter</c>) calls <see cref="Invoke"/> once to start the action.
    /// The action manages its own UI elements (ROIs, panels) and fires either
    /// <see cref="Completed"/> or <see cref="Cancelled"/> when the user finishes.
    /// <see cref="IDisposable.Dispose"/> may be called by the host at any time to force-cancel
    /// a running action without firing events.
    /// </remarks>
    public interface IPlotterAction : IDisposable
    {
        /// <summary>
        /// Fired when the action completes successfully.
        /// The argument carries the result <see cref="IMatrixData"/>, or <c>null</c> if no data change occurred.
        /// </summary>
        event EventHandler<IMatrixData?>? Completed;

        /// <summary>Fired when the user explicitly cancels the action.</summary>
        event EventHandler? Cancelled;

        /// <summary>
        /// Starts the action: creates required overlay objects and enters the interaction phase.
        /// Must be called exactly once.
        /// </summary>
        void Invoke(PlotterActionContext context);

        /// <summary>
        /// Called by the host when the action context changes while the action is running
        /// (e.g., the depth axis is switched or the data is replaced).
        /// The action should re-validate and clamp any out-of-bounds ROIs, then refresh overlays.
        /// Default implementation is a no-op; override when the action holds side-view state.
        /// </summary>
        void NotifyContextChanged(PlotterActionContext newContext) { }
    }
}
