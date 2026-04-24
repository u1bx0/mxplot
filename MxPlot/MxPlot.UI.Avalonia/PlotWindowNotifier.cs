using Avalonia.Controls;
using MxPlot.Core;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia
{
    /// <summary>
    /// Static event hub and parent-link registry for notifying dashboard-style consumers
    /// whenever a plot window (<see cref="Views.MatrixPlotter"/>, <see cref="Views.ProfilePlotter"/>, etc.)
    /// is created anywhere in the application.
    /// </summary>
    /// <remarks>
    /// Subscribe to <see cref="PlotWindowCreated"/> at application startup to receive
    /// notifications from any part of the UI layer (including grandchild windows).
    /// <para/>
    /// To declare a newly created window as a child of an existing window, call
    /// <see cref="SetParentLink"/> after construction and before <see cref="Window.Show"/>.
    /// The association is consumed exactly once by the dashboard's <c>RegisterWindow</c>.
    /// Windows without a parent link are treated as independent top-level windows.
    /// </remarks>
    public static class PlotWindowNotifier
    {
        /// <summary>
        /// Raised when a new plot-related window is created and about to be shown.
        /// The first parameter is the <see cref="Window"/> instance;
        /// the second is the associated <see cref="IMatrixData"/> (may be <c>null</c>
        /// for non-matrix windows such as <see cref="Views.ProfilePlotter"/>).
        /// </summary>
        public static event Action<Window, IMatrixData?>? PlotWindowCreated;

        /// <summary>
        /// Call this to broadcast a window creation event.
        /// Typically invoked from within <see cref="Views.MatrixPlotter"/> / <see cref="Views.ProfilePlotter"/>
        /// constructors or factory methods.
        /// </summary>
        internal static void NotifyCreated(Window window, IMatrixData? data = null)
            => PlotWindowCreated?.Invoke(window, data);

        // ── Parent-link registry ──────────────────────────────────────────────────

        private static readonly Dictionary<Window, Window> _parentMap = [];

        /// <summary>
        /// Declares <paramref name="child"/> as a linked child of <paramref name="parent"/>.
        /// Call this after constructing the child window and before <see cref="Window.Show"/>.
        /// </summary>
        /// <remarks>
        /// The association is consumed exactly once by the dashboard's <c>RegisterWindow</c>
        /// via <see cref="ConsumeParentLink"/>.
        /// <para/>
        /// Windows created without calling this method (e.g. via Duplicate) are treated as
        /// independent top-level windows and inserted at the root level in the dashboard list.
        /// </remarks>
        public static void SetParentLink(Window child, Window parent)
            => _parentMap[child] = parent;

        /// <summary>
        /// Removes and returns the parent previously registered via <see cref="SetParentLink"/>.
        /// Returns <c>null</c> when no parent link was registered for <paramref name="child"/>.
        /// Intended for use by dashboard implementations (e.g. <c>MxPlot.App</c>).
        /// </summary>
        public static Window? ConsumeParentLink(Window child)
        {
            if (_parentMap.Remove(child, out var parent)) return parent;
            return null;
        }
    }
}
