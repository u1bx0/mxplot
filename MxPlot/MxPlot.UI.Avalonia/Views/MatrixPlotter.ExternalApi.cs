using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.ViewModels;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// A fixed value range (see <see cref="MatrixPlotter.FixedRange"/>), analogous to how
    /// <c>Window.Position</c> bundles X/Y into a single <c>PixelPoint</c>.
    /// </summary>
    public readonly record struct RangeInfo(double Min, double Max);

    public partial class MatrixPlotter
    {
        // ── External control Facade properties ─────────────────────────────────
        //
        // Plain get/set properties, mirroring how Window.Position/WindowState are properties
        // rather than SetXxx() methods. Each is a thin delegation to ViewModel (the SSOT for
        // display state) — see MatrixPlotter_ExternalBinding_Design.md for the full rationale.
        // Setting via this Facade, via ViewModel directly, or via MainView directly all converge
        // to the same state.

        /// <summary>Gets or sets the color lookup table used to render the current frame.</summary>
        public LookupTable? Lut
        {
            get => ViewModel?.Lut;
            set
            {
                if (ViewModel == null)
                    throw new InvalidOperationException(
                        $"Cannot set {nameof(Lut)}: {nameof(DataContext)} has not been set to a " +
                        $"{nameof(MatrixPlotterViewModel)}. Use {nameof(MatrixPlotter)}.Create(...), " +
                        $"or set {nameof(DataContext)} first.");
                if (value != null) ViewModel.Lut = value;
            }
        }

        /// <summary>
        /// Gets or sets the value-range mode: <see cref="ValueRangeMode.Current"/> (auto-scan of
        /// the displayed frame), <see cref="ValueRangeMode.All"/> (whole stack),
        /// <see cref="ValueRangeMode.Roi"/>, or <see cref="ValueRangeMode.Fixed"/>.
        /// </summary>
        /// <remarks>
        /// Prefer this over <see cref="IsFixedRange"/>, which can only say whether the range is
        /// pinned. A mode the current data cannot support is downgraded rather than rejected —
        /// All on single-frame data, and Roi with no ROI overlay designated, both fall back to
        /// <see cref="ValueRangeMode.Current"/> — and reading the property back afterwards
        /// reports the mode actually in effect.
        /// <para>
        /// Setting <see cref="ValueRangeMode.Fixed"/> keeps whatever <see cref="FixedRange"/>
        /// holds; assign <see cref="FixedRange"/> to change it.
        /// </para>
        /// </remarks>
        public ValueRangeMode RangeMode
        {
            get => ViewModel?.RangeMode ?? ValueRangeMode.Current;
            set
            {
                if (ViewModel == null)
                    throw new InvalidOperationException(
                        $"Cannot set {nameof(RangeMode)}: {nameof(DataContext)} has not been set " +
                        $"to a {nameof(MatrixPlotterViewModel)}.");
                ViewModel.RangeMode = value;
            }
        }

        /// <summary>
        /// Gets or sets whether the display range is pinned rather than following the current frame.
        /// </summary>
        /// <remarks>
        /// Reads <c>true</c> for Fixed, All, and Roi range modes alike (only <c>false</c> for
        /// auto-scan/Current mode) — <see cref="MxView.IsFixedRange"/> itself does not distinguish
        /// them. Setting <c>true</c> while already in All or Roi mode is therefore a no-op: it
        /// does not force Fixed mode. Use <see cref="RangeMode"/> to select a specific mode.
        /// Setting <c>false</c> from any pinned mode returns to <see cref="ValueRangeMode.Current"/>.
        /// See MatrixPlotter_ExternalBinding_Design.md for the full property list and rationale.
        /// </remarks>
        public bool IsFixedRange
        {
            get => ViewModel?.IsFixedRange ?? false;
            set
            {
                if (ViewModel == null)
                    throw new InvalidOperationException(
                        $"Cannot set {nameof(IsFixedRange)}: {nameof(DataContext)} has not been set " +
                        $"to a {nameof(MatrixPlotterViewModel)}.");
                ViewModel.IsFixedRange = value;
            }
        }

        /// <summary>Gets or sets the fixed display value range (min/max), applied atomically.</summary>
        public RangeInfo FixedRange
        {
            get => ViewModel != null ? new RangeInfo(ViewModel.FixedMin, ViewModel.FixedMax) : default;
            set
            {
                if (ViewModel == null)
                    throw new InvalidOperationException(
                        $"Cannot set {nameof(FixedRange)}: {nameof(DataContext)} has not been set " +
                        $"to a {nameof(MatrixPlotterViewModel)}.");
                ViewModel.ApplyFixedRange(value.Min, value.Max);
            }
        }

        /// <summary>Gets or sets whether the LUT is applied inverted.</summary>
        public bool IsInvertedColor
        {
            get => ViewModel?.IsInvertedColor ?? false;
            set
            {
                if (ViewModel == null)
                    throw new InvalidOperationException(
                        $"Cannot set {nameof(IsInvertedColor)}: {nameof(DataContext)} has not been set " +
                        $"to a {nameof(MatrixPlotterViewModel)}.");
                ViewModel.IsInvertedColor = value;
            }
        }

        /// <summary>
        /// Gets or sets the LUT quantization level — the number of distinct colors the lookup
        /// table is resampled to before rendering. 256 by default.
        /// </summary>
        /// <remarks>
        /// The level spinner in the LUT settings panel offers 2–4096, but a value assigned here is
        /// passed through unclamped, so that this and a direct <see cref="MxView.LutDepth"/>
        /// assignment stay equivalent. A value of 1 or less means "use the LUT's own level count".
        /// </remarks>
        public int LutDepth
        {
            get => ViewModel?.LutDepth ?? 256;
            set
            {
                if (ViewModel == null)
                    throw new InvalidOperationException(
                        $"Cannot set {nameof(LutDepth)}: {nameof(DataContext)} has not been set " +
                        $"to a {nameof(MatrixPlotterViewModel)}.");
                ViewModel.LutDepth = value;
            }
        }

        // ActiveIndex is deliberately NOT mirrored here: MatrixData.ActiveIndex already is the
        // canonical Model-layer source of truth (bounds-checked, has its own ActiveIndexChanged
        // event) — see MatrixPlotter_ExternalBinding_Design.md. Use plotter.MatrixData.ActiveIndex
        // directly; it throws IndexOutOfRangeException on invalid input rather than silently
        // clamping.
    }
}
