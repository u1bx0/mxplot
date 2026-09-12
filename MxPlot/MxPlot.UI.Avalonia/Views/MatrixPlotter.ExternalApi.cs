using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.ViewModels;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;

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
        /// Gets or sets the per-channel Composite rendering recipes (color, value range, gain,
        /// gamma) - one entry per channel, in the same order <see cref="EnterCompositeMode"/>'s
        /// axis enumerates them.
        /// </summary>
        /// <remarks>
        /// Assigning explicit values here means "stop auto-computing these" for every channel it
        /// touches - the same contract as a user dragging a range handle directly (which switches
        /// that channel to <see cref="ValueRangeMode.Fixed"/> the same way). This also switches
        /// the composite range scope to per-channel (<c>ChannelWise</c>, not <c>Global</c>): a
        /// caller handing in per-channel values almost certainly wants them kept independent
        /// rather than merged into one shared range on the next <see cref="Refresh"/>. Each
        /// channel's own UI row (color swatch, gain/gamma sliders, range bar, histogram view)
        /// is synced via <see cref="Controls.BlendRecipeBar.SetRecipe"/>, the same call the
        /// settings panel's own internal range-application code already uses - there is no
        /// separate state for "what the UI shows" versus "what got assigned here".
        /// <para>
        /// Not a general-purpose default: MxPlot itself never infers or auto-applies a Fixed
        /// range on a caller's behalf (e.g. when opening a plain RGB bitmap) - only an explicit
        /// assignment through this property does. A live RGB source that never needs auto-ranging
        /// (a camera feed, say) is exactly the case this exists for; that judgment belongs to
        /// whichever code understands the data, not to MxPlot.
        /// </para>
        /// </remarks>
        /// <exception cref="InvalidOperationException">Not currently in Composite mode.</exception>
        /// <exception cref="ArgumentException">
        /// The count does not match the number of channels Composite mode was entered with.
        /// </exception>
        public IReadOnlyList<BlendRecipe>? CompositeRecipes
        {
            get => _isCompositeMode ? _compositeRecipes : null;
            set
            {
                if (!_isCompositeMode)
                    throw new InvalidOperationException(
                        $"Cannot set {nameof(CompositeRecipes)}: not currently in Composite mode. " +
                        $"Call {nameof(EnterCompositeMode)} first.");
                if (value == null || value.Count != _compositeRecipes.Count)
                    throw new ArgumentException(
                        $"{nameof(CompositeRecipes)} must have exactly {_compositeRecipes.Count} " +
                        "entries (one per channel), matching the axis EnterCompositeMode was " +
                        "called with.",
                        nameof(value));

                _compositeRecipes = value.ToList();

                if (_compositeBars != null)
                {
                    for (int i = 0; i < _compositeRecipes.Count && i < _compositeBars.Length; i++)
                    {
                        _compositeBars[i].SetRecipe(_compositeRecipes[i]);
                        _compositeBars[i].SetRangeMode(ValueRangeMode.Fixed);
                    }
                }

                // Already a no-op if the scope was ChannelWise - SetCompositeScope short-circuits
                // on an unchanged value, so this is safe to call on every assignment.
                SetCompositeScope(CompositeRangeScope.ChannelWise);
                PushCompositeRecipes();
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

        /// <summary>
        /// Gets or sets whether Processing dialogs (Crop, Filter, Normalize, Log Transform, etc.)
        /// offer their "Replace data" option for this window. <c>true</c> by default.
        /// </summary>
        /// <remarks>
        /// This is a UI-only permission: it does not restrict how <see cref="MatrixData"/> can be
        /// replaced from code (e.g. <c>MainView.MatrixData = newData</c> remains unaffected), and it
        /// is independent of <see cref="IsSyncFollower"/> (the internal state for windows kept live
        /// by a Log Transform / Spatial Filter sync, which cannot be overridden through this
        /// property). Intended for hosts that repurpose a <see cref="MatrixPlotter"/> as a live
        /// viewer for their own continuously-updated data (e.g. a camera preview) and want to
        /// suppress the in-app Processing menu's own "Replace data" option, which would otherwise
        /// just be overwritten again on the next update from the host.
        /// </remarks>
        public bool AllowDataReplace { get; set; } = true;

        /// <summary>
        /// Gets or sets whether this window shows the unsaved-changes indicator (status bar dot)
        /// and any "Revert" button (LUT/VR/Composite header toolbar, Scale tab). <c>true</c> by
        /// default.
        /// </summary>
        /// <remarks>
        /// UI-only, like <see cref="AllowDataReplace"/>: the underlying dirty-tracking and revert
        /// snapshots keep working internally exactly as before -- only the user-facing path to
        /// them (seeing that something changed, and reverting it) is hidden. Intended for the same
        /// kind of host as <see cref="AllowDataReplace"/> -- e.g. a live camera preview -- where
        /// "revert to the state when this was loaded/saved" has no meaning because there is no
        /// load/save concept at all for the window's data.
        /// </remarks>
        public bool RevertUiEnabled { get; set; } = true;

        /// <summary>
        /// Gets or sets whether this window's titlebar icon automatically follows the current LUT
        /// (or the Composite icon while in Composite mode). <c>true</c> by default.
        /// </summary>
        /// <remarks>
        /// Set to <c>false</c> to pin a custom icon: disable this first, then assign
        /// <c>Icon</c> directly, the same pattern as any other Avalonia Window.
        /// </remarks>
        public bool AutoUpdateWindowIcon { get; set; } = true;

        // ActiveIndex is deliberately NOT mirrored here: MatrixData.ActiveIndex already is the
        // canonical Model-layer source of truth (bounds-checked, has its own ActiveIndexChanged
        // event) — see MatrixPlotter_ExternalBinding_Design.md. Use plotter.MatrixData.ActiveIndex
        // directly; it throws IndexOutOfRangeException on invalid input rather than silently
        // clamping.

        /// <summary>
        /// Copies this plotter's LUT-adjacent display state -- range mode, fixed range, inverted
        /// color, and LUT depth -- onto <paramref name="target"/>.
        /// </summary>
        /// <remarks>
        /// A Processing result window is typically created via
        /// <c>MatrixPlotter.Create(result, _view.Lut, ...)</c> or <see cref="CreateLinked"/>, both
        /// of which only carry the <see cref="LookupTable"/> color table itself; this covers the
        /// state that lives alongside it in the ViewModel and would otherwise silently reset to
        /// Current/un-inverted/default-depth. Call it after <paramref name="target"/>'s data is
        /// set (i.e. after <c>Create</c>/<see cref="CreateLinked"/> returns), so <see cref="RangeMode"/>'s
        /// own downgrade logic (e.g. All on now-single-frame data) evaluates against the new data
        /// rather than the source's.
        /// </remarks>
        internal void CopyRangeAndLutStateTo(MatrixPlotter target)
        {
            target.FixedRange = FixedRange;
            target.RangeMode = RangeMode;
            target.IsInvertedColor = IsInvertedColor;
            target.LutDepth = LutDepth;
        }
    }
}
