using Avalonia.Animation.Easings;
using System;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Shared motion timings for the dashboard. Three tiers, chosen by what is moving rather than by
    /// how each site happens to feel in isolation: a consistent set reads as deliberate, whereas
    /// per-site values tuned separately read as noise.
    /// </summary>
    /// <remarks>
    /// The XAML styles in MxPlotAppWindow.axaml carry these same durations as literals, since a
    /// <c>Transitions</c> setter cannot reference them; they are marked with the tier they belong to
    /// and must be changed together with the values here.
    /// </remarks>
    internal static class Motion
    {
        /// <summary>Colour, opacity, hover — changes that should feel immediate but not abrupt.</summary>
        public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(80);

        /// <summary>Items moving to a new position: the drop gap, reordering, the dragged card landing.</summary>
        public static readonly TimeSpan Base = TimeSpan.FromMilliseconds(140);

        /// <summary>Things appearing or disappearing.</summary>
        public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(220);

        /// <summary>Easing for anything travelling to a resting position.</summary>
        public static Easing Travel => new CubicEaseOut();

        /// <summary>
        /// Easing for something being set down. The slight overshoot reads as weight on a landing,
        /// but as sloppiness on a gap opening or a colour change, so it is deliberately not the default.
        /// </summary>
        public static Easing Land => new BackEaseOut();
    }
}
