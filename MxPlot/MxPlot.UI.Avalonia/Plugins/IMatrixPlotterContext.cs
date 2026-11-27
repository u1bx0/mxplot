using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Threading;

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

        /// <summary>
        /// The title of the host window. Use it as the <c>from</c> of a history entry
        /// (<see cref="Metadata.MatrixDataHistoryExtensions.AppendHistory"/>) or as the base of a result's title.
        /// </summary>
        string Title { get; }

        /// <summary>Service for opening new plot windows from within the plugin.</summary>
        IPlotWindowService WindowService { get; }

        /// <summary>
        /// Opens a window that follows this one: it first shows <paramref name="initial"/>, then is
        /// recomputed by <paramref name="transform"/> whenever this window's active frame changes or its
        /// data is refreshed. It closes together with this window.
        /// </summary>
        /// <param name="initial">What the window shows until the first recompute.</param>
        /// <param name="title">The window title.</param>
        /// <param name="transform">
        /// Computes the new content from a <see cref="PlotSyncInput"/>. It runs on a background thread, and
        /// a call still running when this window changes again is cancelled through its token, so it
        /// should observe the token. Return <c>null</c> to leave the window as it is for this change.
        /// The result may have a different type or shape from <paramref name="initial"/>. A type-preserving
        /// transform needs the element type, which is reached through an <see cref="IMatrixDataOperation"/>.
        /// It must not modify its input.
        /// </param>
        /// <returns>A handle whose disposal closes the window and ends the following.</returns>
        /// <remarks>Call on the UI thread.</remarks>
        IDisposable ShowSyncedMatrixPlotter(
            IMatrixData initial,
            string title,
            Func<PlotSyncInput, CancellationToken, IMatrixData?> transform);

        // ── Frame rendering ───────────────────────────────────────────────────

        /// <summary>
        /// The frame index currently displayed in the main view.
        /// </summary>
        int ActiveFrameIndex { get; }

        /// <summary>
        /// The lookup table (color theme) currently applied to the main view.
        /// </summary>
        LookupTable? CurrentLut { get; }

        /// <summary>
        /// Whether the current LUT is displayed with inverted colors.
        /// </summary>
        bool IsLutInverted { get; }

        /// <summary>
        /// Renders the specified frame using the plotter's current LUT and value range,
        /// and returns a new <see cref="WriteableBitmap"/>.
        /// </summary>
        /// <param name="frameIndex">
        /// Frame index to render. Use <see cref="ActiveFrameIndex"/> to render the currently
        /// displayed frame, or pass any valid index to render an arbitrary frame.
        /// </param>
        /// <param name="valueMin">
        /// Minimum value for color mapping. Pass <see cref="double.NaN"/> (default)
        /// to use the plotter's current display minimum.
        /// </param>
        /// <param name="valueMax">
        /// Maximum value for color mapping. Pass <see cref="double.NaN"/> (default)
        /// to use the plotter's current display maximum.
        /// </param>
        /// <returns>
        /// A new <see cref="WriteableBitmap"/> at the data's native resolution (<c>XCount × YCount</c>).
        /// The caller is responsible for disposing it.
        /// </returns>
        /// <remarks>
        /// This method is the primary building block for frame-export plugins.
        /// Loop over all frame indices and call this method on each to generate
        /// the sequence needed for video or animated GIF export.
        /// <para>
        /// <b>Composite mode (future):</b> this method always uses the single-frame
        /// <c>LutBitmapWriter</c> path and is intentionally kept that way.
        /// When composite rendering is active, call <c>RenderFrameAsComposite</c> instead.
        /// See the design note below.
        /// </para>
        /// </remarks>
        WriteableBitmap RenderFrame(int frameIndex,
                                    double valueMin = double.NaN,
                                    double valueMax = double.NaN);

        // ── Future API: composite export (planned for v0.2.0) ─────────────────
        //
        // Problem: RenderFrame(frameIndex) cannot represent composite output because:
        //   - Composite blends N slices along a composite axis into one image.
        //   - "frameIndex" alone is ambiguous: it could mean the position along the
        //     non-composite axis (e.g., time step T when compositing Z) or a raw frame index.
        //   - The blend recipe (which axis, which slices, blend mode, per-channel LUTs)
        //     is owned by the plotter's composite state, not derivable from a plain int.
        //
        // Proposed solution: a separate method that reads the current composite recipe
        // from the plotter state and renders accordingly.
        //
        //   WriteableBitmap RenderFrameAsComposite(int activeIndex,
        //                                          double valueMin = double.NaN,
        //                                          double valueMax = double.NaN);
        //
        // Contract:
        //   - activeIndex: a flat frame index that DimensionStructure can decompose into
        //     per-axis indices via GetAxisIndices(activeIndex). The composite axis is then
        //     fixed (iterated over all its slices for blending) while the other axes use
        //     the positions decoded from activeIndex.
        //
        //     To build an activeIndex for a specific non-composite axis position, use:
        //       data.Dimensions.GetFrameIndexFor(nonCompositeAxis, axisSliceIndex)
        //     This returns the flat frame index with all other axes at their current position
        //     and nonCompositeAxis set to axisSliceIndex.
        //
        //     Example — export loop over T when compositing Z:
        //       for (int t = 0; t < tAxis.Count; t++)
        //       {
        //           int idx = data.Dimensions.GetFrameIndexFor(tAxis, t);
        //           using var bmp = context.RenderFrameAsComposite(idx);
        //           // save bmp as frame t
        //       }
        //
        //   - Throws InvalidOperationException if no composite recipe is configured
        //     (i.e., composite mode is not active). Callers should check IsCompositeActive
        //     before calling, or handle the exception to fall back to RenderFrame.
        //   - valueMin/valueMax override the per-composite global range when provided;
        //     NaN means "use the recipe's own range" (which may be per-channel).
        //
        // Additional members needed on IMatrixPlotterContext at that time:
        //   bool IsCompositeActive { get; }
        //   Axis? CompositeAxis { get; }         // null when not active
        //   CompositeBlendMode BlendMode { get; }
    }
}
