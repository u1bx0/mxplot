using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MxPlot.Core;
using MxPlot.Core.Imaging;

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
        /// <c>BitmapWriter</c> path and is intentionally kept that way.
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
