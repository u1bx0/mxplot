// MatrixPlotter.ColorCoded.cs
//
// ColorCoded projection *child-window* UI (RenderingMode.ColorCoded). This is not a peer of
// MatrixPlotter.Composite.cs's Enter/Exit pair -- ColorCoded has no "mode a user enters on an
// ordinary window" the way Composite does. It only ever appears on the ephemeral XY-projection
// child window created by MatrixPlotter.VolumeOperation.cs's OnXYProjectionChanged.
//
// This window's own MatrixData is the real winner-*value* matrix from ExtremumIndexOperation --
// an ordinary projection-shaped result, same type as the source data -- so Duplicate/Convert/
// Filter/Overlay analysis/Save all just work on it like any other projection. Only the *display*
// is special: ColorCodedBitmapWriter combines that data with the winner-index/depth-palette/
// range/invert bundle (ColorCodedRenderInfo) pushed via UpdateColorCodedInfo, at render time, on
// the *parent*'s OrthogonalViewController, which owns the ExtremumIndexOperation scan. So this
// file's job is: reuse the ordinary LUT header's existing LutSelector/ValueRangeBar controls
// (design doc section 3.3.2: "差し替え不要", they are never rebuilt or swapped) but reinterpret
// what changing them means -- report the new value upward via ColorCodedParamsChanged instead of
// touching _view state that would double-apply what ColorCodedBitmapWriter already does -- and
// swap in a dedicated details panel (Start/End + Invert, replacing Level/Histogram, which has no
// meaning for a depth-coded projection).
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        /// <summary>
        /// The ColorCoded scan/colorize parameters a projection child window's own header/details
        /// UI can change: Start/End (scan range), depth palette, intensity range (Fixed pins
        /// Min/Max; not-Fixed means Auto), and Invert. Mirrors <c>OrthogonalViewController</c>'s
        /// private ColorCoded fields one-for-one -- see ColorCoded_View_InitialDesign.md section 3.3.2.
        /// </summary>
        internal readonly record struct ColorCodedProjectionParams(
            int Start, int End, LookupTable? DepthLut,
            bool RangeFixed, double FixedMin, double FixedMax, bool Invert);

        /// <summary>
        /// True once this window has become a ColorCoded projection child via
        /// <see cref="EnterColorCodedProjectionMode"/>, until <see cref="ExitColorCodedProjectionMode"/>.
        /// Always false for an ordinary window. Gates the shared LutSelector/ValueRangeBar handlers
        /// in MatrixPlotter.Initialization.cs (see this file's header comment for why).
        /// </summary>
        internal bool IsColorCodedProjectionChild { get; private set; }

        /// <summary>
        /// Fired whenever the user changes Start/End, the depth palette, the intensity range, or
        /// Invert while this window is a ColorCoded projection child. The parent -- whose
        /// OnXYProjectionChanged subscribes to this right after creating/entering the child --
        /// forwards the new values to <see cref="OrthogonalViewController.SetColorCodedParams"/>,
        /// which redoes the scan+colorize and pushes a fresh frame back down through
        /// <c>UpdateProjectionData</c>.
        /// </summary>
        internal event EventHandler<ColorCodedProjectionParams>? ColorCodedParamsChanged;

        // ── Details-panel controls (built once, lazily, on first entry) ────────
        private NumericUpDown? _colorCodedStartNud;
        private NumericUpDown? _colorCodedEndNud;
        private ToggleButton? _colorCodedInvertChk;
        private HistogramPlotControl? _colorCodedHistogram;
        private BusyIndicator? _colorCodedBusyIndicator;
        private Border? _colorCodedDetails;

        // Set while EnterColorCodedProjectionMode is seeding initial values into the shared
        // LutSelector/ValueRangeBar/Start/End/Invert controls, so their change handlers don't
        // read those programmatic writes back as a user edit and bounce a redundant recompute
        // request straight back up to the parent.
        private bool _suppressColorCodedEvents;

        // The natural (Auto) range from the most recent parent recompute, pushed by
        // UpdateColorCodedAutoRange -- cached here (not just displayed) so the 🔍 Search Min/Max
        // buttons have something to search for even while Fixed mode is selected. Null until the
        // first ColorCoded recompute completes.
        private double? _colorCodedLastAutoMin;
        private double? _colorCodedLastAutoMax;

        // The real LUT (_view.Lut) that was active right before EnterColorCodedProjectionMode most
        // recently forced it to Grayscale, so ExitColorCodedProjectionMode can put it back. _view.Lut
        // itself cannot be that memory: ColorCodedBitmapWriter never reads it, so forcing it to
        // Grayscale while ColorCoded is invisible to this window's own rendering, but every
        // Duplicate/Filter/Convert/... call site (MatrixPlotter.Actions.cs and friends) passes
        // _view.Lut straight through to the new window it creates -- forcing Grayscale here is what
        // makes those results open as plain grayscale instead of misleadingly showing the (now
        // meaningless, this window was never re-rendered through it) depth palette or whatever LUT
        // happened to be active before. Deliberately a field, not IMatrixData.Metadata: the child
        // window's own MatrixData is replaced wholesale by a fresh instance (blank Metadata) on
        // every ColorCoded recompute (Start/End edit, palette change, ...), so anything stashed in
        // Metadata at Enter time would already be gone by the time Exit runs if the user touched
        // any control in between.
        private string? _colorCodedRevertLutName;

        /// <summary>
        /// Whichever details panel (<see cref="_lutModeDetails"/> or <see cref="_colorCodedDetails"/>)
        /// is currently hosted in <see cref="_detailsContainer"/>. The ▾ settings-button toggle,
        /// wired once in MatrixPlotter.Initialization.cs, reads/animates this instead of a
        /// hardcoded <c>_lutModeDetails</c> so it keeps working after a ColorCoded swap. Always
        /// <c>_lutModeDetails</c> for an ordinary window.
        /// </summary>
        private Border? ActiveDetailsPanel => IsColorCodedProjectionChild ? _colorCodedDetails : _lutModeDetails;

        /// <summary>
        /// Switches this window into ColorCoded projection-child mode: sets
        /// <see cref="RenderingMode.ColorCoded"/>, swaps the details panel to Start/End + Invert,
        /// and seeds the header's existing LutSelector/ValueRangeBar from <paramref name="initial"/>
        /// without firing the ordinary LUT-mode side effects (dirty tracking, histogram, sync
        /// events -- these controls now parameterize ColorCodedBitmapWriter instead of _view.Lut/
        /// FixedMin/FixedMax directly, so applying them the ordinary way would be a no-op at best).
        /// The header row itself is left exactly as-is; only what its controls mean changes.
        /// <para>
        /// Safe to call again while already active (a later parent recompute with new
        /// Start/End/etc. does not need to call this again -- only a *mode* transition into
        /// ColorCoded does); doing so simply reseeds the controls from <paramref name="initial"/>.
        /// </para>
        /// </summary>
        internal void EnterColorCodedProjectionMode(int axisCount, ColorCodedProjectionParams initial)
        {
            _suppressColorCodedEvents = true;
            try
            {
                // Only on a genuine LUT/Max/Min -> ColorCoded transition, not a hypothetical re-entry
                // while already active (see this method's "safe to call again" note) -- otherwise
                // we'd capture the Grayscale we ourselves just forced instead of the real LUT.
                if (!IsColorCodedProjectionChild)
                {
                    _colorCodedRevertLutName = _view.Lut?.Name;
                    _view.Lut = ColorThemes.Grayscale;
                }
                _view.RenderingMode = RenderingMode.ColorCoded;
                IsColorCodedProjectionChild = true;

                if (_colorCodedDetails == null)
                    _colorCodedDetails = BuildColorCodedModeDetails();
                _lutModeDetails.IsVisible = false;
                if (_detailsContainer != null) _detailsContainer.Content = _colorCodedDetails;
                _settingsBtn.Content = "▾";
                _settingsBtn.Background = Brushes.Transparent;

                int maxIndex = Math.Max(0, axisCount - 1);
                _colorCodedStartNud!.Maximum = maxIndex;
                _colorCodedEndNud!.Maximum = maxIndex;
                _colorCodedStartNud.Value = Math.Clamp(initial.Start, 0, maxIndex);
                _colorCodedEndNud.Value = Math.Clamp(initial.End, 0, maxIndex);
                ClampColorCodedRangeInputs(apply: true);
                _colorCodedInvertChk!.IsChecked = initial.Invert;

                // "LUT:" would read as an ordinary value→colour LUT and be indistinguishable from
                // real LUT mode at a glance -- relabel so the repurposed meaning is visible.
                _lutSelector.LabelText = "Coded LUT:";
                _lutSelector.SelectLut(initial.DepthLut ?? ColorThemes.Spectrum);
                _rangeBar.SetMultiFrame(false);
                _rangeBar.SetMode(initial.RangeFixed ? ValueRangeMode.Fixed : ValueRangeMode.Current);
                if (initial.RangeFixed) _rangeBar.SetRange(initial.FixedMin, initial.FixedMax);
                // Window icon tracks the LutSelector's current selection the same way ordinary LUT
                // mode does (UpdateWindowIcon() -> _lutSelector.SelectedIcon) -- now the depth
                // palette's gradient instead of a value→colour LUT's, which is exactly what should
                // show for a linked ColorCoded window.
                UpdateWindowIcon();
                UpdateColorCodedRangeToolTips();
            }
            finally { _suppressColorCodedEvents = false; }
        }

        /// <summary>
        /// Leaves ColorCoded projection-child mode and restores the ordinary LUT details panel.
        /// No-op if not currently a ColorCoded child. Does not touch <see cref="RenderingMode"/> --
        /// the caller (<c>OnXYProjectionChanged</c>) sets that itself alongside the plain-mode data
        /// swap, the same way it already did before this file existed.
        /// </summary>
        internal void ExitColorCodedProjectionMode()
        {
            if (!IsColorCodedProjectionChild) return;
            IsColorCodedProjectionChild = false;
            if (_colorCodedDetails != null) _colorCodedDetails.IsVisible = false;
            if (_detailsContainer != null) _detailsContainer.Content = _lutModeDetails;
            _settingsBtn.Content = "▾";
            _settingsBtn.Background = Brushes.Transparent;
            // EnterColorCodedProjectionMode forced _view.Lut to Grayscale (see
            // _colorCodedRevertLutName's doc comment for why) -- put back whatever was really
            // active before that, falling back to Grayscale itself if the name is somehow no longer
            // a known theme (e.g. an .mlut file removed mid-session).
            _lutSelector.LabelText = "LUT:";
            LookupTable? revertLut = null;
            if (_colorCodedRevertLutName != null)
            {
                try { revertLut = ColorThemes.Get(_colorCodedRevertLutName); }
                catch (KeyNotFoundException) { /* e.g. an .mlut file removed mid-session -- fall back */ }
            }
            _view.Lut = revertLut ?? ColorThemes.Grayscale;
            _lutSelector.SelectLut(_view.Lut);
            _colorCodedRevertLutName = null;
            _view.ColorCodedInfo = null;
            UpdateWindowIcon();
        }

        /// <summary>
        /// Pushes the winner-index/depth-palette/resolved-range/invert bundle for the most recent
        /// recompute -- drives <see cref="ColorCodedBitmapWriter"/>'s rendering, the pointer
        /// read-out's depth position (<c>RenderSurface.DrawPositionOverlay</c>), and the details
        /// panel's depth histogram. Called by the parent after every recompute; safe to call with
        /// <c>null</c> (clears all three).
        /// </summary>
        internal void UpdateColorCodedInfo(ColorCodedRenderInfo? info)
        {
            _view.ColorCodedInfo = info;
            UpdateColorCodedHistogram(info);
            UpdateColorCodedRangeToolTips();
        }

        /// <summary>
        /// Shows/hides the busy indicator over the depth histogram while a Start/End (or palette/
        /// range/Invert) edit made in this window is being recomputed by the parent's
        /// <c>OrthogonalViewController</c>. The parent's own <c>MainView.IsBusy</c> spinner lives on
        /// the *parent* window, out of sight while the user is looking at this child's histogram --
        /// this gives feedback where the user is actually interacting. Called by the parent around
        /// <c>OrthogonalViewController.SetColorCodedParams</c> (see
        /// <c>MatrixPlotter.VolumeOperation.cs</c>'s <c>OnXYProjectionColorCodedParamsChanged</c> /
        /// <c>OnXYProjectionChanged</c>); no-op if the details panel hasn't been built yet.
        /// </summary>
        internal void SetColorCodedBusy(bool busy)
        {
            if (_colorCodedBusyIndicator != null) _colorCodedBusyIndicator.IsActive = busy;
        }

        /// <summary>
        /// Rebuilds the depth histogram from <paramref name="info"/>.WinnerIndex: one bin per axis
        /// index across the *whole* axis (<c>[0, info.Axis.Count-1]</c>, fixed regardless of the
        /// current Start/End), tinted with the same DepthColors the image itself is coloured with
        /// wherever a bin falls inside [Start, End] -- literally "how many pixels chose each depth",
        /// the same question <see cref="HistogramPlotControl"/> already answers for ordinary
        /// LUT-mode data, just asked of the index map instead of the value map.
        /// <para>
        /// The plot window is deliberately the full axis, not just [Start, End]: WinnerIndex
        /// physically cannot contain a value outside the currently-scanned range (see
        /// <see cref="Core.Processing.ExtremumIndexOperation"/>), so bins outside it are always
        /// exactly zero and simply don't draw a bar -- accepted as fine (better than the alternative
        /// of resizing the plot to hug the current selection, which glues the red lines to the plot
        /// edges and leaves no way to drag them *outward* to widen Start/End again).
        /// </para>
        /// <para>
        /// The actual scan (<see cref="System.Collections.Generic.IReadOnlyList{T}"/> -- one pass
        /// over WinnerIndex) is precomputed on the background thread that produced
        /// <paramref name="info"/> itself (<c>OrthogonalViewController.ComputeXYProjectionAsync</c>),
        /// not here -- this was previously a synchronous full-frame scan on the UI thread and, for a
        /// large XY / long-axis dataset, the dominant cause of the UI freezing on every Start/End
        /// edit. This method now only hands the already-computed array to
        /// <see cref="HistogramPlotControl"/>.
        /// </para>
        /// </summary>
        private void UpdateColorCodedHistogram(ColorCodedRenderInfo? info)
        {
            if (_colorCodedHistogram == null) return;
            if (info?.Histogram == null) { _colorCodedHistogram.IsVisible = false; return; }

            int start = info.Start;
            int sliceCount = info.DepthColors.Count;
            int end = start + sliceCount - 1;
            int axisCount = info.Axis.Count;

            var hist = info.Histogram;

            // Pass depthColors itself (sized sliceCount, not axisCount) -- HistogramPlotControl's
            // UpdateBrushCache does NOT index aRgbs by raw bin position; it normalizes each bin's
            // value into [viewMin, viewMax] (t = (binValue - viewMin) / (viewMax - viewMin)) and
            // indexes aRgbs by that t, i.e. aRgbs is always taken to span the *view* range end to
            // end. Padding it out to axisCount (an earlier version of this method did) broke that
            // assumption -- t no longer reached 1.0 until the *padded* array's last slot, so the
            // real depth colours only ever occupied a squeezed-together sliver instead of the whole
            // [Start, End] view range, which is exactly the "shifted" colouring seen on the real
            // machine. Bins outside [Start, End] still get some (clamped, meaningless) colour from
            // this array, but always at count 0, so nothing is ever drawn for them.
            var depthColors = info.DepthColors as int[] ?? new List<int>(info.DepthColors).ToArray();

            // preservePlotWindow: true is load-bearing, not cosmetic -- SetHistogram's default
            // (false) resets the *actual* rendered plot window (_plotMin/_plotMax) to exactly
            // viewMin/viewMax on every call, regardless of valueMin/valueMax (those only feed a
            // separate badge calculation). Without this, the plot window collapses to the current
            // Start..End on every recompute, so the red lines always render glued to its edges --
            // exactly the bug this whole change was meant to fix. true instead only ever widens the
            // window to cover viewMin/viewMax, never narrows it, so the [0, axisCount-1] window
            // established on the first call (always the full range -- see OnProjectionSelectionChanged's
            // reset-on-fresh-enable) is preserved through every later narrower Start/End.
            _colorCodedHistogram.IsVisible = true;
            _colorCodedHistogram.SetHistogram(hist, 0, axisCount - 1, start, end, depthColors, preservePlotWindow: true);
        }

        /// <summary>
        /// Called by the parent (<c>OnXYProjectionChanged</c>) after every ColorCoded recompute with
        /// the natural (winnerValue) range that recompute actually found -- <c>null</c>/<c>null</c>
        /// is never passed once the first recompute has completed. Caches it for the Search buttons
        /// regardless of mode, and additionally pushes it into the visible ValueRangeBar display
        /// while in Auto mode (Fixed mode owns its own pinned values and is left untouched).
        /// </summary>
        internal void UpdateColorCodedAutoRange(double? min, double? max)
        {
            _colorCodedLastAutoMin = min;
            _colorCodedLastAutoMax = max;
            if (!IsColorCodedProjectionChild || _rangeBar.Mode != ValueRangeMode.Current) return;
            if (min is not double m1 || max is not double m2) return;
            _suppressColorCodedEvents = true;
            try { _rangeBar.SetRange(m1, m2); }
            finally { _suppressColorCodedEvents = false; }
        }

        /// <summary>
        /// Handles the ValueRangeBar's 🔍 Search Min button while in ColorCoded mode: seeds the
        /// Min box from the last known natural (Auto) range, keeping the Max box as-is -- mirrors
        /// the ordinary LUT-mode search's "replace only this side" behaviour. No-op until at least
        /// one recompute has completed.
        /// </summary>
        private void SearchColorCodedMin()
        {
            if (_colorCodedLastAutoMin is not double m) return;
            _rangeBar.SetRange(m, _rangeBar.DisplayedMaxValue);
            RaiseColorCodedParamsChanged();
        }

        /// <summary>Search Max counterpart of <see cref="SearchColorCodedMin"/>.</summary>
        private void SearchColorCodedMax()
        {
            if (_colorCodedLastAutoMax is not double m) return;
            _rangeBar.SetRange(_rangeBar.DisplayedMinValue, m);
            RaiseColorCodedParamsChanged();
        }

        private (int Start, int End) ClampColorCodedRangeInputs(bool apply)
        {
            if (_colorCodedStartNud == null || _colorCodedEndNud == null) return (0, 0);

            int min = (int)_colorCodedStartNud.Minimum;
            int max = (int)_colorCodedStartNud.Maximum;

            int start = Math.Clamp((int)Math.Round(_colorCodedStartNud.Value ?? min), min, max);
            int end = Math.Clamp((int)Math.Round(_colorCodedEndNud.Value ?? start), start, max);

            if (apply)
            {
                _suppressColorCodedEvents = true;
                try
                {
                    _colorCodedStartNud.Value = start;
                    _colorCodedEndNud.Value = end;
                }
                finally { _suppressColorCodedEvents = false; }
            }

            return (start, end);
        }

        private void UpdateColorCodedRangeToolTips()
        {
            if (_colorCodedStartNud == null || _colorCodedEndNud == null) return;

            var (start, end) = ClampColorCodedRangeInputs(apply: false);
            string tip;
            if (_view.ColorCodedInfo?.Axis is { } axis && axis.Count > 0)
            {
                int axisMax = axis.Count - 1;
                start = Math.Clamp(start, 0, axisMax);
                end = Math.Clamp(end, start, axisMax);
                string unit = axis.Unit.Length > 0 ? $" {axis.Unit}" : "";
                tip = $"Color-coded range index: [{start}-{end}]\nRange = {axis.ValueAt(start):G4} to {axis.ValueAt(end):G4}{unit}";
            }
            else
            {
                tip = $"Color-coded range index: [{start}-{end}]";
            }

            ToolTip.SetTip(_colorCodedStartNud, tip);
            ToolTip.SetTip(_colorCodedEndNud, tip);
        }

        private void RaiseColorCodedParamsChanged()
        {
            if (_suppressColorCodedEvents || !IsColorCodedProjectionChild) return;

            var (start, end) = ClampColorCodedRangeInputs(apply: true);
            UpdateColorCodedRangeToolTips();

            var p = new ColorCodedProjectionParams(
                start,
                end,
                _lutSelector.SelectedLut,
                _rangeBar.Mode == ValueRangeMode.Fixed,
                _rangeBar.DisplayedMinValue,
                _rangeBar.DisplayedMaxValue,
                _colorCodedInvertChk!.IsChecked == true);
            ColorCodedParamsChanged?.Invoke(this, p);
        }

        /// <summary>
        /// Builds the ColorCoded details panel: Start / End numeric boxes (design doc section
        /// 3.3.2 -- plain textboxes are the primary input, deliberately not a dual-thumb range
        /// slider), an Invert toggle, and a depth histogram (<see cref="UpdateColorCodedHistogram"/>)
        /// that doubles as a drag-to-narrow control for Start/End. Replaces the ordinary LUT panel's
        /// Level spinner, which has no equivalent here -- there is no value→colour LUT to resample,
        /// ColorCodedBitmapWriter uses the depth palette directly.
        /// </summary>
        private Border BuildColorCodedModeDetails()
        {
            _colorCodedStartNud = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 0,
                Value = 0,
                Increment = 1,
                ClipValueToMinMax = true,
                Width = 60,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(4, 0),
            };
            _colorCodedStartNud.Classes.Add("compact");
            _colorCodedStartNud.ValueChanged += (_, _) =>
            {
                if (_suppressColorCodedEvents) return;
                var (start, end) = ClampColorCodedRangeInputs(apply: true);
                // Keep the histogram's red lines following manual Start/End edits too, not just
                // drags on the histogram itself -- SetViewValueRange does not fire ViewRangeChanged,
                // so this cannot loop back into RaiseColorCodedParamsChanged a second time.
                _colorCodedHistogram?.SetViewValueRange(start, end);
                RaiseColorCodedParamsChanged();
            };

            _colorCodedEndNud = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 0,
                Value = 0,
                Increment = 1,
                ClipValueToMinMax = true,
                Width = 60,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(4, 0),
            };
            _colorCodedEndNud.Classes.Add("compact");
            _colorCodedEndNud.ValueChanged += (_, _) =>
            {
                if (_suppressColorCodedEvents) return;
                var (start, end) = ClampColorCodedRangeInputs(apply: true);
                _colorCodedHistogram?.SetViewValueRange(start, end);
                RaiseColorCodedParamsChanged();
            };

            _colorCodedInvertChk = new ToggleButton
            {
                // Same ◑ half-filled-circle glyph as the ordinary LUT panel's Invert toggle
                // (BuildLutModeDetails) -- a fresh instance rather than the same control reused,
                // since this details panel is an entirely separate tree from _lutModeDetails.
                Content = new PathIcon
                {
                    Data = Geometry.Parse(
                        "F1 " +
                        "M 8,1 A 7,7 0 0,1 8,15 A 7,7 0 0,1 8,1 Z " +
                        "M 8,2 A 6,6 0 0,0 8,14 L 8,2 Z"),
                    Width = 14,
                    Height = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(5, 0),
            };
            ToolTip.SetTip(_colorCodedInvertChk, "Invert depth palette direction");
            _colorCodedInvertChk.IsCheckedChanged += (_, _) => RaiseColorCodedParamsChanged();

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(30, 2, 10, 2),
            };
            row.Children.Add(new TextBlock { Text = "Start:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(_colorCodedStartNud);
            row.Children.Add(new TextBlock
            {
                Text = "End:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
            row.Children.Add(_colorCodedEndNud);
            row.Children.Add(new Border { Width = 1, Background = Brushes.Gray, Margin = new Thickness(9, 3) });
            row.Children.Add(_colorCodedInvertChk);
            row.Children.Add(new Border { Width = 1, Background = Brushes.Gray, Margin = new Thickness(5, 3) });

            // Depth histogram: one bin per axis index, tinted with the depth palette -- see
            // UpdateColorCodedHistogram's doc comment. Initial width matches _lutModeDetails's
            // _histogramPlot; resized dynamically below.
            _colorCodedHistogram = new HistogramPlotControl
            {
                Width = 200,
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 4),
                IsVisible = false,
            };
            _colorCodedHistogram.ViewRangeChanged += (min, max) =>
            {
                if (_colorCodedStartNud == null || _colorCodedEndNud == null) return;
                int maxIndex = (int)_colorCodedStartNud.Maximum; // Start/End share the same Maximum (axisCount-1)
                int newStart = Math.Clamp((int)Math.Round(min), 0, maxIndex);
                int newEnd = Math.Clamp((int)Math.Round(max), newStart, maxIndex);
                _suppressColorCodedEvents = true;
                try
                {
                    _colorCodedStartNud.Value = newStart;
                    _colorCodedEndNud.Value = newEnd;
                }
                finally { _suppressColorCodedEvents = false; }
                RaiseColorCodedParamsChanged();
            };

            // Overlaid on the histogram (not the parent's MainView, which is out of sight while the
            // user is looking at this child window) so a Start/End edit's recompute -- now
            // backgrounded, see SetColorCodedBusy's doc comment -- still gives feedback right where
            // the user is interacting.
            _colorCodedBusyIndicator = new BusyIndicator
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Text = "Recomputing…",
            };
            var histogramOverlay = new Panel();
            histogramOverlay.Children.Add(_colorCodedHistogram);
            histogramOverlay.Children.Add(_colorCodedBusyIndicator);
            row.Children.Add(histogramOverlay);

            // Fixed elements (label/box/separator/Invert widths) are a rough estimate, same
            // approach BuildLutModeDetails's settingsRow.SizeChanged uses for its own histogram.
            const double FixedElementsWidth = 300.0;
            const double MaxHistogramWidth = 200.0;
            const double MinHistogramWidth = 50.0;
            row.SizeChanged += (_, e) =>
            {
                if (_colorCodedHistogram == null) return;
                double available = e.NewSize.Width - FixedElementsWidth;
                _colorCodedHistogram.Width = Math.Clamp(available, MinHistogramWidth, MaxHistogramWidth);
            };

            return new Border
            {
                Child = row,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = false,
            };
        }
    }
}
