using Avalonia;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Implemented by overlay objects that enclose a 2-D region and support
    /// statistical evaluation of the enclosed data points.
    /// <para>
    /// <b>Coordinate convention:</b> <see cref="ContainsWorldPoint"/> operates in overlay
    /// world space (left-top origin, Y-down). The caller (<c>MatrixPlotter.Overlays</c>)
    /// is responsible for applying the FlipY transform when mapping to data-index space:
    /// <c>dataY = (YCount - 1) - worldY</c>.
    /// </para>
    /// </summary>
    public interface IAnalyzableOverlay
    {
        /// <summary>
        /// Returns whether the given world-coordinate point falls inside this object's region.
        /// Used by the caller to filter data-index pixels within the bounding box.
        /// </summary>
        bool ContainsWorldPoint(Point worldPoint);

        /// <summary>
        /// Menu entry for "Find Min/Max". The host assigns <see cref="OverlayMenuEntry.Handler"/>
        /// after the object is added; the entry is hidden when no handler is assigned.
        /// </summary>
        OverlayMenuEntry FindMinMax { get; }

        /// <summary>
        /// Menu entry for the "Show Statistics" toggle. The host assigns the handler after
        /// the object is added; the entry is hidden when no handler is assigned.
        /// </summary>
        OverlayMenuEntry ToggleShowStatistics { get; }

        /// <summary>
        /// Menu entry for "Use ROI for value range". The host assigns the handler after
        /// the object is added; the entry is hidden when no handler is assigned.
        /// </summary>
        OverlayMenuEntry UseRoiForValueRange { get; }

        /// <summary>
        /// Menu entry for copying the data within this region to the clipboard.
        /// The host assigns the handler after the object is added;
        /// the entry is hidden when no handler is assigned.
        /// </summary>
        OverlayMenuEntry CopyData { get; }

        /// <summary>Whether the statistics overlay label is currently visible.</summary>
        bool ShowStatistics { get; set; }

        /// <summary>
        /// Cached statistics computed by the host (<c>MatrixPlotter</c>).
        /// Null when not yet computed or when <see cref="ShowStatistics"/> is false.
        /// </summary>
        RegionStatistics? CachedStatistics { get; set; }

        /// <summary>
        /// Whether this overlay is currently designated as the ROI for value-range computation.
        /// Set by <c>MatrixPlotter</c> when the user selects "Use ROI for value range" from the
        /// context menu, or by deserialization when restoring saved state.
        /// </summary>
        bool IsValueRangeRoi { get; set; }
    }
}
