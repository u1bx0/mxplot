using MxPlot.Core;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Everything the ColorCoded live-projection window's rendering and pointer read-out both need,
    /// pushed as one atomic unit by <c>OrthogonalViewController</c> after every recompute (via
    /// <c>MatrixPlotter.UpdateColorCodedInfo</c>). Bundled into a single record -- unlike Composite's
    /// three separate <c>CompositeRecipes</c>/<c>CompositeFrameIndices</c>/<c>CompositeBlendMode</c>
    /// properties -- specifically so a single assignment can never leave the properties in a
    /// mutually-inconsistent intermediate state the way Composite's <c>BuildCompositeContext</c> has
    /// to guard against.
    /// </summary>
    /// <param name="WinnerIndex">
    /// Single-frame <c>MatrixData&lt;int&gt;</c>: the winning axis index at each pixel, as produced
    /// by <c>ExtremumIndexOperation</c>. The window's own <c>MatrixData</c> is the winner *value*
    /// matrix directly (an ordinary projection-shaped result, unlike the packed-ARGB
    /// <see cref="MxPlot.Core.IMatrixData"/> the earlier <c>TrueColorBitmapWriter</c> design used) --
    /// this is the one piece <see cref="ColorCodedBitmapWriter"/> and the pointer read-out both need
    /// that the window's own data cannot supply.
    /// </param>
    /// <param name="Start">
    /// The <c>Start</c> passed to the <c>ExtremumIndexOperation</c> that produced
    /// <paramref name="WinnerIndex"/> -- needed to map an absolute axis index back to a position in
    /// <paramref name="DepthColors"/>.
    /// </param>
    /// <param name="DepthColors">
    /// ARGB colour per swept slice, indexed by <c>WinnerIndex - Start</c>. Invert is not a separate
    /// flag here: it means "reverse which end of the palette maps to which end of the axis" -- the
    /// same "reverse the LUT array" semantics ordinary LUT mode's Invert already has
    /// (<c>MatrixPlotter.cs</c>'s <c>BuildHistogramLutColors</c>) -- so the caller
    /// (<c>OrthogonalViewController.ComputeXYProjectionAsync</c>) bakes it into this array's order
    /// directly. Every consumer (<see cref="ColorCodedBitmapWriter"/>, the details panel's depth
    /// histogram) just uses the array as given and automatically respects it for free.
    /// </param>
    /// <param name="ValueMin">Intensity normalization lower bound (maps to fully dark).</param>
    /// <param name="ValueMax">Intensity normalization upper bound (maps to full brightness).</param>
    /// <param name="Axis">
    /// The scanned axis, captured at compute time -- used only for its immutable geometry
    /// (<see cref="MxPlot.Core.Axis.Name"/>, <see cref="MxPlot.Core.Axis.Unit"/>,
    /// <see cref="MxPlot.Core.Axis.ValueAt"/>) by the pointer read-out, never for its mutable
    /// <see cref="MxPlot.Core.Axis.Index"/>, so holding this reference after the source data's axis
    /// is later swapped or its index changes elsewhere is safe.
    /// </param>
    /// <param name="DepthProfile">
    /// The mean pixel value of every slice across the whole axis ([0, Axis.Count-1]) -- how the
    /// signal is distributed along the scanned axis, whatever Start/End currently select. Computed
    /// on the background thread that produced this record
    /// (<c>OrthogonalViewController.ComputeXYProjectionAsync</c>), so the details panel's profile plot
    /// (<c>MatrixPlotter.UpdateColorCodedHistogram</c>) only hands it to <c>HistogramPlotControl</c>.
    /// <see langword="null"/> only if <see cref="Axis"/> could not be resolved at compute time.
    /// </param>
    /// <param name="BlendedArgb">
    /// Color (RGB-Max) / (RGB-Add) only (and the RGB-Avg blend, which has no ComboBox item): the already-blended packed-ARGB image (one frame, row-major, the
    /// window's XY size) from <see cref="ColorCodedRgbBlender"/>. When non-<see langword="null"/>,
    /// <see cref="ColorCodedBitmapWriter"/> just copies it to the bitmap and ignores the
    /// winner-index/depth-color path; <see cref="WinnerIndex"/> still carries the peak depth for the
    /// pointer read-out. <see langword="null"/> for Color(Max)/(Min).
    /// </param>
    public sealed record ColorCodedRenderInfo(
        IMatrixData WinnerIndex, int Start, IReadOnlyList<int> DepthColors,
        double ValueMin, double ValueMax, Axis Axis, double[]? DepthProfile = null, int[]? BlendedArgb = null);
}
