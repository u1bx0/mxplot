// MatrixPlotter.Composite.cs
//
// Composite rendering mode (RenderingMode.Composite). Originally built for the "Channel" axis
// only; generalized to any axis.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.Core.Utils;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ---- State ----------------------------------------------------------

        /// <summary>
        /// Master on/off switch for the Composite feature, kept while it is still being
        /// stabilized. <c>false</c> (default): the Channel-axis AxisTracker never shows its
        /// Composite entry button, <see cref="EnterCompositeMode"/> is a no-op, and restoring
        /// a file whose metadata has <c>RenderingMode.Composite</c> saved falls back to LUT
        /// instead. Flip to <c>true</c> (e.g. in a debug session) to re-enable the feature.
        /// </summary>
        internal static bool CompositeModeEnabled = true;

        /// <summary>
        /// Titlebar icon shown while a window is in Composite mode, in place of the LUT-gradient
        /// icon <see cref="_lutSelector"/> normally supplies. One immutable bitmap shared by every
        /// window; built once on first use rather than per instance.
        /// </summary>
        private static readonly WindowIcon CompositeWindowIcon = ControlFactory.CreateCompositeWindowIcon();

        /// <summary>
        /// Sets the titlebar <see cref="Window.Icon"/> for the current mode: the Composite Venn
        /// icon while <see cref="_isCompositeMode"/>, otherwise whatever LUT is currently selected.
        /// Every LUT-change call site used to set <c>Icon = _lutSelector.SelectedIcon</c> directly;
        /// they now all go through here so a LUT change made while Composite is active does not
        /// silently switch the titlebar back to a LUT icon.
        /// No-op when <see cref="AutoUpdateWindowIcon"/> is <c>false</c>, so a host pinning its own
        /// icon via <see cref="Window.Icon"/> is not overwritten on the next LUT/Composite change.
        /// </summary>
        private void UpdateWindowIcon()
        {
            if (!AutoUpdateWindowIcon) return;
            Icon = _isCompositeMode ? CompositeWindowIcon : _lutSelector.SelectedIcon;
        }

        private bool _isCompositeMode;
        private int _compositeAxisDimIndex = -1;
        private List<BlendRecipe> _compositeRecipes = new();

        /// <summary>
        /// Always <see cref="BlendMode.Additive"/> for channel composites — adding pure primaries
        /// is what reconstructs a normal colour image, so there is deliberately no UI to change it.
        /// The field (and the writer/persistence support behind it) is kept because
        /// <see cref="BlendMode.Maximum"/> is what a future depth/time colour-coded mode needs, and
        /// because a file saved by an older build may still carry a Maximum setting to restore.
        /// </summary>
        private BlendMode _compositeBlendMode = BlendMode.Additive;
        // The real source-data frame index each _compositeRecipes[i] currently maps to
        // (i.e. this Channel's frame at the current Z/T position) -- kept in sync with
        // _view.CompositeFrameIndices by ApplyCompositeFrameIndices. Used by the channel
        // rows' histogram/Auto/search-button logic to read the right frame's pixel data.
        private int[] _compositeFrameIndices = Array.Empty<int>();

        private Button? _compositeSettingsBtn;
        private Border? _compositeSettingsPanel;
        private Button? _compositeHamburgerBtn;

        /// <summary>
        /// The Composite header's revert button — the twin of <see cref="_lutVrRevertBtn"/>, in the
        /// same toolbar slot and firing the same <see cref="RevertRenderState"/>.
        /// </summary>
        private Button? _compositeRevertBtn;

        /// <summary>Whether one shared range drives every channel, or each channel owns its own.</summary>
        private enum CompositeRangeScope
        {
            /// <summary>A single Min/Max, owned by the header bar, applied to every channel.</summary>
            Global,
            /// <summary>Each channel evaluates and owns its own Min/Max.</summary>
            ChannelWise,
        }

        private CompositeRangeScope _compositeScope = CompositeRangeScope.Global;

        /// <summary>
        /// The Scope flyout's own two radio buttons, kept so <see cref="SetCompositeScope"/> can
        /// re-sync their IsChecked state when scope changes from somewhere other than the user
        /// clicking one of them (e.g. <see cref="MatrixPlotter.CompositeRecipes"/> assigned
        /// externally) -- built fresh each <see cref="BuildCompositeModeHeader"/> call, so these
        /// are only valid while that header instance is the one currently shown.
        /// </summary>
        private RadioButton? _compositeScopeGlobalRadio;
        private RadioButton? _compositeScopeChannelWiseRadio;

        /// <summary>
        /// The header range bar -- the very same control the LUT toolbar uses, so Composite mode gets
        /// an identical Fixed/Current/All menu and imperfect badge. In Global scope it owns the
        /// shared range; in Channel-wise scope it shows a read-only union of the per-channel ranges
        /// (see <see cref="ValueRangeBar.SetRangeEditable"/>).
        /// </summary>
        private ValueRangeBar? _compositeRangeBar;

        /// <summary>The range mode used in Global scope, i.e. the header bar's mode.</summary>
        private ValueRangeMode _compositeGlobalMode = ValueRangeMode.Current;

        /// <summary>
        /// Per-channel range modes recovered from metadata, held until the bars exist.
        /// <see cref="EnterCompositeMode"/> applies and clears it right after building them.
        /// </summary>
        private ValueRangeMode[]? _compositeRestoredModes;

        /// <summary>One BlendRecipeBar per channel, living in the expandable settings panel.</summary>
        private BlendRecipeBar[]? _compositeBars;

        /// <summary>
        /// Drives <see cref="UpdateCompositeHistograms"/>'s coalescing dirty/running loop. Kept
        /// separate from <c>MatrixPlotter.cs</c>'s own LUT-histogram pair -- see that field's
        /// comment for why a shared flag pair would silently drop one mode's updates.
        /// </summary>
        private bool _compositeHistogramDirty;
        private bool _compositeHistogramRunning;

        internal bool IsCompositeMode => _isCompositeMode;

        /// <summary>
        /// The hamburger button currently attached to the visual tree (LUT header's
        /// <see cref="_hamburgerBtn"/>, or the Composite header's own button when active).
        /// <see cref="ShowMenuPanel"/>/<see cref="HideMenuPanel"/>/<see cref="OnMenuLightDismiss"/>
        /// (MatrixPlotter.Menu.cs) must use this instead of <see cref="_hamburgerBtn"/> directly —
        /// positioning/hit-testing against a detached control (the LUT header while Composite is
        /// active) produces a stale/zero position.
        /// </summary>
        private Button? ActiveHamburgerButton => _isCompositeMode ? _compositeHamburgerBtn : _hamburgerBtn;

        // ---- Fixed default color palettes ------------------------------------

        // Fluorescence-microscopy-conventional channel colors, ordered so that the channels a
        // dataset is most likely to have are the furthest apart in hue (most datasets have 2-4):
        //   1-2ch  Green + Magenta  -- 180° apart, the maximum possible, and the standard
        //          colour-blind-safe substitute for the red/green pair.
        //   1-3ch  + Cyan           -- the common green/magenta/cyan publication trio.
        //   1-4ch  + Yellow         -- every pair still >= 60° apart.
        // Pure primaries only: additive compositing relies on them, so desaturated tints would
        // bleed across channels (same constraint as the clipboard RGB loader).
        // Beyond this length, colors continue via even hue rotation.
        private static readonly int[] FluorescenceDefaultColors =
        {
            unchecked((int)0xFF00FF00), // Green    120°
            unchecked((int)0xFFFF00FF), // Magenta  300°
            unchecked((int)0xFF00FFFF), // Cyan     180°
            unchecked((int)0xFFFFFF00), // Yellow    60°
            unchecked((int)0xFFFF0000), // Red        0°
            unchecked((int)0xFF0000FF), // Blue     240°
            unchecked((int)0xFFFF8000), // Orange    30°
            unchecked((int)0xFF8000FF), // Violet   270°
        };

        private static int[] DefaultCompositeColors(int channelCount, Type valueType)
        {
            // byte + 3 channels reads as a classic RGB color image far more often than
            // a 3-color fluorescence composite, so it gets the conventional RGB triplet.
            if (valueType == typeof(byte) && channelCount == 3)
            {
                return new[]
                {
                    unchecked((int)0xFFFF0000), // Red
                    unchecked((int)0xFF00FF00), // Green
                    unchecked((int)0xFF0000FF), // Blue
                };
            }

            var colors = new int[channelCount];
            for (int i = 0; i < channelCount; i++)
            {
                colors[i] = i < FluorescenceDefaultColors.Length
                    ? FluorescenceDefaultColors[i]
                    : HsvToArgb(360.0 * i / channelCount);
            }
            return colors;
        }

        private static int HsvToArgb(double hueDegrees)
        {
            double h = hueDegrees / 60.0;
            int hi = (int)Math.Floor(h) % 6;
            double f = h - Math.Floor(h);
            byte v = 255;
            byte p = 0;
            byte q = (byte)(255 * (1 - f));
            byte t = (byte)(255 * f);
            (byte r, byte g, byte b) = hi switch
            {
                0 => (v, t, p),
                1 => (q, v, p),
                2 => (p, v, t),
                3 => (p, q, v),
                4 => (t, p, v),
                _ => (v, p, q),
            };
            return unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }

        // ---- Dirty tracking / snapshot ----------------------------------------

        /// <summary>
        /// Records a user-driven Composite change: marks the document modified, refreshes the revert
        /// buttons and mirrors the new state into metadata.
        /// <para>
        /// Deliberately called only from the handful of places that used to call
        /// <c>SaveViewSettings()</c> directly — i.e. genuine user actions. It must never be called
        /// from <see cref="ApplyCompositeRanges"/> or <see cref="PushCompositeRecipes"/>, which also
        /// run on every frame-navigation step and would otherwise keep the document permanently
        /// dirty while a Z/T slider is dragged.
        /// </para>
        /// </summary>
        private void MarkCompositeDirty()
        {
            SetDirty(DirtyFlags.Composite, true);
            UpdateRenderRevertButtons();
            SaveViewSettings();
        }

        /// <summary>
        /// Captures the Composite half of <see cref="RenderSnapshot"/>, or <c>null</c> when Composite
        /// mode is not active — a snapshot taken in LUT mode has no Composite state to restore.
        /// </summary>
        private CompositeSnapshot? CaptureCompositeSnapshot()
        {
            if (!_isCompositeMode || _currentData == null || _compositeAxisDimIndex < 0) return null;
            var channelModes = _compositeBars != null
                ? _compositeBars.Select(b => b.RangeMode).ToArray()
                : Array.Empty<ValueRangeMode>();
            return new CompositeSnapshot(
                _compositeScope, _compositeGlobalMode, _compositeBlendMode,
                _compositeRecipes.ToArray(), channelModes,
                _currentData.Dimensions[_compositeAxisDimIndex].Name);
        }

        /// <summary>
        /// Undoes the plain-Axis-to-<see cref="ColorAxis"/> promotion that
        /// <see cref="PromoteToColorAxisIfNeeded"/> performs on entering Composite mode, so that a
        /// revert really does leave the dataset as it was found.
        /// <para>
        /// Runs only when the snapshot recorded a plain (non-tagged) axis at this position, which
        /// can only be true of a dataset that was never saved in Composite mode — so this fires
        /// exclusively on a revert back to LUT, never on a revert that stays in Composite.
        /// </para>
        /// <para>
        /// Reconstructs the demoted axis from <see cref="_scaleSnapshot"/>'s recorded
        /// Min/Max/Unit/IsIndexBased rather than from the live (already-promoted) axis instance --
        /// for a plain Channel axis those happen to be identical before and after promotion (both
        /// are index-based with Min=0/Max=Count-1), which is what let the previous version of this
        /// method get away with copying from the live instance. Generalized to any axis (a real
        /// scaled Z or Time axis, say), promotion actually discards Min/Max/Unit/IsIndexBased, so
        /// copying from the live instance would silently make the revert permanent instead of
        /// undoing it. Only covers reverting within the session the promotion happened in --
        /// _scaleSnapshot doesn't survive a save/close/reopen, so a Composite-promoted axis saved to
        /// disk and reopened later can't be demoted back to its original scale (deferred; would need
        /// persisting the original scale to file metadata).
        /// </para>
        /// </summary>
        private void DemoteColorAxisIfPromoted()
        {
            if (_currentData == null || _scaleSnapshot == null) return;

            var data = _currentData;
            var axes = data.Axes;
            int index = -1;
            for (int i = 0; i < axes.Count && i < _scaleSnapshot.Axes.Length; i++)
            {
                // Snapshot has no tags for this axis (it was plain) but the live axis now has them.
                if (_scaleSnapshot.Axes[i].Tags == null && axes[i] is TaggedAxis) { index = i; break; }
            }
            if (index < 0) return;

            var current = axes[index];
            int savedIndex = current.Index;
            string? frozenAxisName = _orthoController.ActiveAxisName;
            bool refreezeAfterwards = frozenAxisName != null
                && !frozenAxisName.Equals(current.Name, StringComparison.OrdinalIgnoreCase);

            var snap = _scaleSnapshot.Axes[index];
            var demoted = new Axis(current.Count, snap.Min, snap.Max, snap.Name, snap.Unit, snap.IsIndexBased);

            var newAxes = data.Axes
                .Select(a => ReferenceEquals(a, current) ? demoted : a)
                .ToArray();
            data.DefineDimensions(newAxes);
            demoted.Index = savedIndex;

            RebuildTrackerPanel(data);
            if (refreezeAfterwards) SetOrthogonalView(frozenAxisName);
        }

        // ---- Mode transitions -------------------------------------------------

        /// <summary>
        /// Resets the Composite chrome (header/panel swap, in-memory recipe state) and the view's
        /// rendering mode back to the LUT-mode default. Called at the start of a full tracker rebuild
        /// (<see cref="SetMatrixData"/>, <see cref="RebuildTrackerPanel"/>) so a freshly loaded
        /// dataset never inherits stale Composite state from the previous one --
        /// <see cref="SyncCompositeUiAfterRestore"/> re-enters Composite mode explicitly if the new
        /// data's metadata calls for it.
        /// <para>
        /// Runs before the new <c>MatrixData</c> is assigned to <see cref="_view"/>, so dropping back
        /// to <see cref="RenderingMode.Lut"/> here only re-renders the outgoing dataset, which is
        /// always valid to draw through the LUT writer.
        /// </para>
        /// </summary>
        private void ResetCompositeUiState()
        {
            _isCompositeMode = false;
            _compositeAxisDimIndex = -1;
            // Composite consumes the Channel axis, so the export menu's item set changes with it.
            InvalidateMenuPanel();
            _compositeRecipes = new List<BlendRecipe>();
            // Back to the documented defaults, so a dataset whose metadata omits these keys is not
            // silently rendered with the previous dataset's scope/mode.
            _compositeScope = CompositeRangeScope.Global;
            _compositeGlobalMode = ValueRangeMode.Current;
            _compositeBlendMode = BlendMode.Additive;
            _compositeRestoredModes = null;
            _view.RenderingMode = RenderingMode.Lut;
            if (_headerContainer != null) _headerContainer.Content = _lutHeaderRow;
            if (_detailsContainer != null) _detailsContainer.Content = _lutModeDetails;
            // The Composite header (and its buttons) is detached now; drop the stale references so
            // UpdateRenderRevertButtons does not poke at controls that are no longer in the tree.
            _compositeRevertBtn = null;
            _compositeRangeBar = null;
            _compositeBars = null;
            _compositeSettingsPanel = null;
            _compositeSettingsBtn = null;
            _compositeScopeGlobalRadio = null;
            _compositeScopeChannelWiseRadio = null;
            UpdateWindowIcon();
        }

        /// <summary>
        /// Enters Composite mode for <paramref name="channelAxis"/> (any axis of
        /// <see cref="_currentData"/> -- no longer restricted to one literally named "Channel"). Safe
        /// to call again while already active (e.g. from <see cref="SyncCompositeUiAfterRestore"/>)
        /// -- recomputes frame indices and re-applies the current recipe list without discarding it.
        /// </summary>
        /// <remarks>
        /// Public so hosts can drive Composite mode programmatically (e.g. a live camera preview
        /// pushing an RGB channel cube). <paramref name="channelAxis"/> must be one of
        /// <see cref="MatrixData"/>'s own axis instances -- e.g.
        /// <c>MatrixData.Axes.FindAxis("Channel")</c> after setting the data -- not a freshly
        /// constructed <see cref="Axis"/>. No-ops silently if <see cref="MatrixData"/> is
        /// <see langword="null"/> or <see cref="CompositeModeEnabled"/> is <see langword="false"/>,
        /// the same as every internal call site.
        /// </remarks>
        public void EnterCompositeMode(Axis channelAxis)
        {
            if (!CompositeModeEnabled) return;
            if (_currentData == null) return;

            // Composite pins the composited axis's index to 0 (below) and blends across it every
            // recompute; a Play animation left running on that axis -- or on any other axis, since
            // Composite recomputes the whole blend on every frame change -- would either fight the
            // pin or just churn expensively for a view no longer being used to explore that axis.
            // Stop every axis's Play unconditionally, not only the one about to be composited.
            foreach (var t in _axisTrackers.Values)
                t.StopAnimation();

            // Switching the composite axis (Z was composited, now Channel is requested, say) must
            // revert the old axis to LUT first, not just overwrite _compositeAxisDimIndex in place --
            // otherwise the old axis's AxisTracker stays hidden forever (EnterCompositeMode hides it
            // but only ExitCompositeMode restores it) and its recipes can get silently reused for the
            // new axis if the channel counts happen to match. A same-axis re-entry (the "safe to call
            // again while already active" case documented below, e.g. from
            // SyncCompositeUiAfterRestore) passes back the very same promoted axis instance, so the
            // reference check correctly skips this for that case.
            if (_isCompositeMode && _compositeAxisDimIndex >= 0
                && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount
                && !ReferenceEquals(_currentData.Dimensions[_compositeAxisDimIndex], channelAxis))
            {
                ExitCompositeMode();
                _compositeRecipes = new List<BlendRecipe>();
            }

            // Channel identity (per-channel name and colour) only becomes meaningful once the
            // data is composited, so that is where a plain axis earns its promotion to a
            // ColorAxis. Doing it here means everything downstream - renaming, colour
            // round-tripping to OME-TIFF - can assume tags exist.
            // Promotion rebuilds the tracker panel the first time a plain axis is composited, and
            // that rebuild tears the orthogonal views down - closing any open XY projection window
            // on the way out. It re-freezes the orthogonal axis afterwards, but the projection is
            // not its to restore. Note it here and put it back at the very end, once the Composite
            // state below is live so the recomputed projection is a Composite payload. Without
            // this, compositing an axis behaved differently the first time (plain axis -> promotion
            // -> projection closed) than every time after (already tagged, no rebuild, projection
            // survived) - the "first open vs. second open" inconsistency.
            bool xyProjectionWasOpen = _orthoPanel.ProjectionSelector.IsProjectionEnabled(ProjectionPlane.XY);
            channelAxis = PromoteToColorAxisIfNeeded(channelAxis);
            bool restoreXyProjection = xyProjectionWasOpen
                && !_orthoPanel.ProjectionSelector.IsProjectionEnabled(ProjectionPlane.XY);

            // Ortho/XY-Projection along the same axis about to be composited is not a coherent
            // combination: BuildChannelComposite pins bi[channelAxisDimIndex] to each channel, but
            // that slot is immediately overwritten by ExtractAlong's own sweep over that identical
            // axis (AsVolume(axisName, bi) -> ExtractAlong(axisName, bi, ...) iterates every index
            // of axisName regardless of what bi held there) - so every "channel" in the side views
            // and XY-Projection ends up showing the same full-axis projection, just tinted a
            // different color. Unfreezing here, the same way the Freeze button itself does, tears
            // Ortho (and any open XY-Projection window, via OrthogonalViewController.Deactivate's
            // XYProjectionChanged) down the normal way before Composite takes over the axis.
            // PromoteToColorAxisIfNeeded already does this as a side effect of RebuildTrackerPanel
            // the first time a plain axis is promoted, so ActiveAxisName is already null by here in
            // that case - this only fires on a later re-entry, once the axis is already tagged.
            if (_orthoController.ActiveAxisName?.Equals(channelAxis.Name, StringComparison.OrdinalIgnoreCase) == true
                && _axisTrackers.TryGetValue(channelAxis.Name, out var frozenTracker))
            {
                frozenTracker.FreezeButton.IsChecked = false;
            }

            var dims = _currentData.Dimensions;
            int dimIndex = -1;
            for (int i = 0; i < dims.AxisCount; i++)
            {
                if (ReferenceEquals(dims[i], channelAxis)) { dimIndex = i; break; }
            }
            if (dimIndex < 0) return;

            _compositeAxisDimIndex = dimIndex;
            _isCompositeMode = true;
            // Composite consumes the Channel axis, so the export menu's item set changes with it.
            InvalidateMenuPanel();

            var frameIndices = ComputeCompositeFrameIndices(_currentData, dimIndex);
            if (_compositeRecipes.Count != frameIndices.Length)
                _compositeRecipes = BuildDefaultCompositeRecipes(_currentData, channelAxis, frameIndices);

            _compositeFrameIndices = frameIndices;
            _view.CompositeFrameIndices = frameIndices;
            _view.CompositeBlendMode = _compositeBlendMode;
            _view.CompositeRecipes = _compositeRecipes.ToList();
            _view.RenderingMode = RenderingMode.Composite;

            if (_headerContainer != null) _headerContainer.Content = BuildCompositeModeHeader();
            if (_detailsContainer != null) _detailsContainer.Content = BuildCompositeModeDetails();
            // _lutModeDetails is a long-lived instance (built once, reused across mode switches) --
            // swapping it out of _detailsContainer.Content detaches it but leaves its own IsVisible
            // flag untouched. Every _lutModeDetails?.IsVisible == true check elsewhere (gating
            // UpdateHistogram()) would otherwise stay stale-true for the rest of this Composite
            // session if the user had opened the LUT details panel even once before switching.
            _lutModeDetails.IsVisible = false;

            if (_axisTrackers.TryGetValue(channelAxis.Name, out var tracker))
                tracker.IsVisible = false;

            _orthoController.SetCompositeState(true, dimIndex, frameIndices.Length, _compositeRecipes, _compositeBlendMode);

            // Pin the Channel axis to 0 for the whole session. The blend itself never reads it
            // (CompositeFrameIndices drives rendering), but ActiveIndex still feeds the pixel
            // readout, overlay analysis and export naming. Leaving it wherever the user happened
            // to be would make those depend on *when* Composite was entered, and would persist
            // that arbitrary value through mxplot.axes.indices. Fixing it at 0 makes the rule
            // simply: ActiveIndex is channel 0 at the current (Z, T). Exiting leaves it there -
            // restoring the old index would contradict the axes.indices that was just saved.
            //
            // Deliberately done *after* SetCompositeState, not before: changing the axis's Index
            // fires ActiveIndexChanged synchronously, which reaches
            // OrthogonalViewController.RefreshSlicesIfAxisChanged (via MatrixPlotter's
            // _activeIndexHandler). Pinning before SetCompositeState had that refresh capture
            // _orthoController's Composite shape (active/channelCount) while it was still the old
            // (not-yet-composited) state -- correct only for the single frame this pin itself moved
            // away from, not for the merged multi-channel result about to replace it. That captured-
            // stale run's completion would then briefly reassert LUT mode on the side views (with a
            // frame-index/frame-count mismatch, since it built its result before the merge -- see
            // ApplyCompositeViewState's captured-shape comment in UpdateSlicesAsync), which is what
            // used to make Bottom/Right stay in LUT mode until an unrelated refresh (e.g. moving
            // Time) happened to run UpdateSlicesAsync again with live state. Pinning after
            // SetCompositeState means every refresh this triggers already sees Composite active.
            if (channelAxis.Index != 0) channelAxis.Index = 0;

            // Adopt any per-channel modes recovered from metadata before the ranges are evaluated.
            if (_compositeRestoredModes != null && _compositeBars != null)
            {
                using (_reentrancy.Begin(GuardContext.UiSync))
                    for (int i = 0; i < _compositeBars.Length && i < _compositeRestoredModes.Length; i++)
                        _compositeBars[i].SetRangeMode(_compositeRestoredModes[i]);
                _compositeRestoredModes = null;
            }

            // Apply the All-entry availability / Global-scope disabling to the bars that were just
            // built, then evaluate the ranges those bars should be showing.
            ConfigureCompositeRangeBars();
            ApplyCompositeRanges();
            UpdateChannelVisibilityLocks();

            MarkCompositeDirty();
            UpdateWindowIcon();
            UpdateExtractFrameAllowed();
            // Show Statistics labels depend on RenderingMode (single-frame vs. per-channel
            // breakdown, see ComputeCompositeStatisticsLabel) -- without this, a ROI whose label
            // was already visible before the switch would keep showing the pre-Composite text
            // until some unrelated geometry/frame change happened to refresh it.
            RefreshAllRegionStatistics();

            // Last, so the recompute this kicks off sees Composite fully established.
            if (restoreXyProjection)
            {
                _orthoPanel.ProjectionSelector.SetEnabled(ProjectionPlane.XY, true);
                _orthoPanel.ProjectionSelector.RaiseXySelection();
            }

            // Fires Refreshed, which is what LinkedView-based followers (ROI View, Log Transform
            // sync, Spatial Filter sync, ...) subscribe to - without it, switching LUT -> Composite
            // never reaches them, and they keep showing whatever they had before the switch until
            // some unrelated trigger (geometry change, frame move) happens to fire a recompute.
            Refresh();
        }

        /// <summary>
        /// Recomputes whether "Extract Frame" is offered on the main view (and orthogonal side
        /// views). When Composite mode is active and Channel is the dataset's only axis, the
        /// Main-view branch of <see cref="InvokeExtractFrame"/> has no other axes left to fix, so
        /// it ends up extracting the whole Channel axis unchanged — a plain Duplicate of the
        /// entire dataset rather than a useful frame extraction. Hide the item in that case.
        /// </summary>
        private void UpdateExtractFrameAllowed()
        {
            bool isMultiFrame = _currentData is { FrameCount: > 1 };
            bool compositeOnlyChannelAxis = _isCompositeMode && _currentData is { Axes.Count: 1 };
            bool allowed = isMultiFrame && !compositeOnlyChannelAxis;
            _view.ExtractFrameAllowed = allowed;
            _orthoPanel.BottomView.ExtractFrameAllowed = allowed;
            _orthoPanel.RightView.ExtractFrameAllowed = allowed;
        }

        // ── "This Frame Only" + Composite helpers ──────────────────────────────
        //
        // Log Transform / Normalize / Spatial Filter (MatrixPlotter.LogTransform.cs,
        // .Normalize.cs, .Filters.cs) all offer a "This Frame Only" option that, for plain
        // LUT data, restricts the operation to MatrixData.ActiveIndex. While Composite mode
        // is active, ActiveIndex is pinned to channel 0 (see EnterCompositeMode), so that
        // naive restriction would process only one channel and silently drop the rest.
        // TryExtractCompositeFrameCube gives those call sites the whole Channel axis (every
        // channel at the current position of every other axis) to operate on instead, and
        // CaptureCompositeCubeState/ReenterCompositeMode let them put the result straight back
        // into Composite mode (in place, or on a synced link window) instead of falling back
        // to a plain LUT view of channel 0.

        /// <summary>
        /// When Composite mode is active, extracts the whole Channel axis (every channel at
        /// the current position of every other axis) from <paramref name="data"/>, mirroring
        /// the Main-view branch of <see cref="InvokeExtractFrame"/>. Returns <see langword="null"/>
        /// when Composite mode is not active, so callers fall back to their plain
        /// single-frame path unchanged.
        /// </summary>
        private (IMatrixData Cube, string ChannelAxisName)? TryExtractCompositeFrameCube(IMatrixData data)
        {
            if (!_isCompositeMode || _compositeAxisDimIndex < 0) return null;
            if (_compositeAxisDimIndex >= data.Dimensions.AxisCount) return null;
            string channelAxisName = data.Dimensions[_compositeAxisDimIndex].Name;
            int[] baseIndices = data.Axes.Select(a => a.Index).ToArray();
            var cube = data.Apply(new ExtractAlongOperation(channelAxisName, baseIndices));
            return (cube, channelAxisName);
        }

        /// <summary>
        /// Whether "This frame only" is a real choice for <paramref name="data"/> right now --
        /// distinct from running on the whole stack, not just whether the checkbox should be
        /// shown. Ordinarily that's simply <c>data.FrameCount &gt; 1</c> (ActiveIndex picks out a
        /// genuine subset). But while Composite mode is active, "this frame" for these dialogs
        /// means <see cref="TryExtractCompositeFrameCube"/>'s *whole* Channel-axis cube at the
        /// current position of every OTHER axis -- so when there is no axis besides the
        /// composited one (no "surviving" axis), that cube already IS every frame, and offering
        /// the checkbox as a real choice would be misleading (there is no "which frame?" to ask).
        /// Mirrors why <c>CreateProjectionDialog</c> hides "This position only" when it has no
        /// surviving axes either.
        /// </summary>
        /// <remarks>
        /// Callers should still force the underlying "this frame only" flag on when this returns
        /// <see langword="false"/> under Composite mode (<c>_isCompositeMode &amp;&amp; !result</c>)
        /// rather than just hiding the checkbox -- otherwise the composite-cube extraction (and
        /// so <see cref="SeedChildCompositeMode"/> re-entering Composite mode on the result) would
        /// never trigger for this data shape, since it was previously only reached via the
        /// checkbox being checked.
        /// </remarks>
        private bool IsThisFrameOnlyAChoice(IMatrixData data)
        {
            if (!_isCompositeMode || _compositeAxisDimIndex < 0 || _compositeAxisDimIndex >= data.Dimensions.AxisCount)
                return data.FrameCount > 1;
            string channelAxisName = data.Dimensions[_compositeAxisDimIndex].Name;
            return data.Axes.Any(a => !string.Equals(a.Name, channelAxisName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Builds a "[Axis=value, ...]" label describing the other-axes position a Channel-axis
        /// cube was extracted at, mirroring the label built inline in the Main-view branch of
        /// <see cref="InvokeExtractFrame"/>. Used in window titles / history details wherever
        /// <see cref="TryExtractCompositeFrameCube"/> is used.
        /// </summary>
        private string BuildCompositeCubeLabel(IMatrixData sourceData, string channelAxisName)
        {
            var otherAxes = sourceData.Axes.Where(a => !string.Equals(a.Name, channelAxisName, StringComparison.OrdinalIgnoreCase));
            return string.Join(", ", otherAxes.Select(a => $"{a.Name}={FormatAxisValue(a, a.Index)}"));
        }

        /// <summary>Recipes/blend/scope captured by <see cref="CaptureCompositeCubeState"/> for replay via <see cref="ReenterCompositeMode"/>.</summary>
        private readonly record struct CompositeCubeState(
            List<BlendRecipe> Recipes,
            BlendMode BlendMode,
            CompositeRangeScope Scope,
            ValueRangeMode GlobalMode,
            ValueRangeMode[]? PerChannelModes);

        /// <summary>
        /// Captures this window's current Composite state so it can be restored after a
        /// <see cref="SetMatrixData"/> call, which always exits Composite mode first
        /// (<see cref="ResetCompositeUiState"/> clears <see cref="_compositeRecipes"/> and
        /// friends). Returns <see langword="null"/> when this window is not currently in
        /// Composite mode — callers should treat that as "leave it in LUT mode", not force
        /// Composite back on (e.g. the user may have just exited it manually on a link window
        /// mid-sync).
        /// </summary>
        private CompositeCubeState? CaptureCompositeCubeState()
        {
            if (!_isCompositeMode) return null;
            return new CompositeCubeState(
                _compositeRecipes.ToList(),
                _compositeBlendMode,
                _compositeScope,
                _compositeGlobalMode,
                _compositeScope == CompositeRangeScope.ChannelWise && _compositeBars != null
                    ? _compositeBars.Select(b => b.RangeMode).ToArray()
                    : null);
        }

        /// <summary>
        /// Restores a snapshot captured via <see cref="CaptureCompositeCubeState"/> and re-enters
        /// Composite mode on the Channel axis named <paramref name="channelAxisName"/> in
        /// <see cref="_currentData"/> (the data just swapped in by <see cref="SetMatrixData"/>).
        /// No-op if that axis is no longer present.
        /// </summary>
        private void ReenterCompositeMode(CompositeCubeState snapshot, string channelAxisName)
        {
            var channelAxis = _currentData?.Axes.FindAxis(channelAxisName);
            if (channelAxis == null) return;
            _compositeRecipes = snapshot.Recipes;
            _compositeBlendMode = snapshot.BlendMode;
            _compositeScope = snapshot.Scope;
            _compositeGlobalMode = snapshot.GlobalMode;
            if (snapshot.PerChannelModes != null) _compositeRestoredModes = snapshot.PerChannelModes;
            EnterCompositeMode(channelAxis);
        }

        /// <summary>
        /// One-time Composite seed for a freshly created child window - the XY projection window
        /// (<see cref="ApplyCompositeStateToProjectionWindow"/>) and Extract's XY/XZ/YZ live-extract
        /// windows (<see cref="InvokeExtractFrame"/>) both call this. Copies the current
        /// recipes/blend mode/range scope so the window opens already blended with a Fixed range
        /// staying Fixed, then leaves the child fully independent from then on: the child has its
        /// own full settings panel for the user to retune it, and a projection/extract's pixel
        /// statistics differ from the parent's, so the "right" saturating range often differs too.
        /// <para>
        /// Recipes alone are not enough to reproduce a Fixed range: <see cref="EnterCompositeMode"/>
        /// ends by calling <see cref="ApplyCompositeRanges"/>, which (Global scope) reads
        /// <see cref="_compositeGlobalMode"/> or (ChannelWise scope) each bar's own mode to decide
        /// whether to keep the copied Min/Max or immediately recompute and overwrite them -
        /// defaulting to <see cref="ValueRangeMode.Current"/> would silently discard a copied Fixed
        /// recipe the instant the window opens. Scope/global-mode and, for ChannelWise, each
        /// channel's mode (via <see cref="_compositeRestoredModes"/>, the same mechanism metadata
        /// restore uses) must be copied alongside the recipes so that decision comes out the same
        /// way it did in the parent. A single shared method for both call sites means this can't
        /// silently drift out of sync between them again, the way the projection window's copy
        /// previously missed scope/global-mode while Extract's had already picked it up.
        /// </para>
        /// </summary>
        internal void SeedChildCompositeState(MatrixPlotter child, Axis channelAxis)
        {
            child._compositeRecipes = _compositeRecipes.ToList();
            child._compositeBlendMode = _compositeBlendMode;
            child._compositeScope = _compositeScope;
            child._compositeGlobalMode = _compositeGlobalMode;
            if (_compositeScope == CompositeRangeScope.ChannelWise && _compositeBars != null)
                child._compositeRestoredModes = _compositeBars.Select(b => b.RangeMode).ToArray();
            child.EnterCompositeMode(channelAxis);
        }

        /// <summary>
        /// Converts a plain Channel axis into a <see cref="ColorAxis"/> so it can carry per-channel
        /// tags and colours, and returns the axis to use from here on. Already-specialised axes
        /// (an OME-TIFF or FITS <see cref="ColorAxis"/>) are returned untouched.
        /// <para>
        /// The axis object is replaced, which forces a <c>DefineDimensions</c> and a tracker rebuild,
        /// so three pieces of state have to be carried across it by hand:
        /// </para>
        /// <list type="number">
        ///   <item>The Composite settings (recipes, range scope, global/per-channel range modes and
        ///         blend mode), because <see cref="RebuildTrackerPanel"/> calls
        ///         <see cref="ResetCompositeUiState"/> - without this a restore-from-metadata would
        ///         silently lose the saved colours, ranges, gains and scope.</item>
        ///   <item>The frozen (orthogonal) axis, because that same rebuild deactivates the
        ///         orthogonal views unconditionally. A frozen Z/Time axis is re-frozen afterwards so
        ///         promotion is invisible; a frozen Channel axis is deliberately left released,
        ///         since side views along an axis that is being blended away are meaningless.</item>
        ///   <item>The axis index, which does not survive the new axis object.</item>
        /// </list>
        /// </summary>
        /// <summary>
        /// Maps a composited axis's <see cref="Axis.Name"/> to the prefix used for its
        /// auto-generated, 0-based tags (e.g. "Ch0", "Ch1", ...) when no tags/recipe were supplied.
        /// Falls back to the axis's own name for axes with no dedicated prefix (e.g. "FOV0", "FOV1").
        /// </summary>
        private static string DefaultCompositeTagPrefix(string axisName) => axisName switch
        {
            "Channel" => "Ch",
            "Z" => "Z",
            "Time" => "T",
            _ => axisName,
        };

        private Axis PromoteToColorAxisIfNeeded(Axis channelAxis)
        {
            if (_currentData == null) return channelAxis;
            if (channelAxis is TaggedAxis) return channelAxis;   // ColorAxis or another tagged axis

            var data = _currentData;
            int savedIndex = channelAxis.Index;
            // ResetCompositeUiState (reached via RebuildTrackerPanel below) puts every Composite
            // field back to its default, which is right for a data swap but not here: these values
            // were just recovered from metadata, or carried over from the session, and must survive.
            var savedRecipes = _compositeRecipes;
            var savedScope = _compositeScope;
            var savedGlobalMode = _compositeGlobalMode;
            var savedBlendMode = _compositeBlendMode;
            var savedChannelModes = _compositeRestoredModes;
            string? frozenAxisName = _orthoController.ActiveAxisName;
            bool refreezeAfterwards = frozenAxisName != null
                && !frozenAxisName.Equals(channelAxis.Name, StringComparison.OrdinalIgnoreCase);

            string prefix = DefaultCompositeTagPrefix(channelAxis.Name);
            var tags = Enumerable.Range(0, channelAxis.Count).Select(i => $"{prefix}{i}").ToArray();
            // ColorAxis's constructor defaults Name to "Channel" -- overridden here so promoting an
            // arbitrary axis (Z, Time, ...) keeps its own identity instead of silently becoming
            // "Channel". A plain Channel axis is already named "Channel", so this is a no-op there.
            var promoted = new ColorAxis(tags) { Unit = channelAxis.Unit, Name = channelAxis.Name };

            // Reuse every other axis object by reference so their trackers and indices survive.
            var newAxes = data.Axes
                .Select(a => ReferenceEquals(a, channelAxis) ? (Axis)promoted : a)
                .ToArray();
            data.DefineDimensions(newAxes);
            promoted.Index = savedIndex;

            RebuildTrackerPanel(data);

            _compositeRecipes = savedRecipes;
            _compositeScope = savedScope;
            _compositeGlobalMode = savedGlobalMode;
            _compositeBlendMode = savedBlendMode;
            _compositeRestoredModes = savedChannelModes;
            if (refreezeAfterwards) SetOrthogonalView(frozenAxisName);

            SetScaleDirty(true);
            return promoted;
        }

        /// <summary>
        /// Leaves Composite mode and restores the LUT header/panel and the Channel
        /// <see cref="AxisTracker"/> visibility. No-op if Composite mode is not active.
        /// </summary>
        public void ExitCompositeMode()
        {
            if (!_isCompositeMode) return;

            // Recipes survive this call unchanged (see the "ResetCompositeUiState is what clears
            // it, not this method" note below), but each bar's own RangeMode does not -- the next
            // EnterCompositeMode rebuilds _compositeBars from scratch (BuildCompositeModeDetails),
            // and a freshly constructed ValueRangeBar defaults to Current. Capture ChannelWise modes
            // here the same way SeedChildCompositeState/CaptureCompositeCubeState/RestoreRenderMode
            // already do, so a plain in-session Composite -> LUT -> Composite round trip (the "switch
            // to LUT mode" header button, or an AxisTracker's Channel -> Composite toggle) does not
            // silently discard a Fixed range and force a rescan on the next Refresh -- exactly the
            // "defaulting to Current would silently discard a copied Fixed recipe" concern documented
            // on SeedChildCompositeState above. Global scope needs no help: _compositeGlobalMode is a
            // plain field that already survives untouched.
            if (_compositeScope == CompositeRangeScope.ChannelWise && _compositeBars != null)
                _compositeRestoredModes = _compositeBars.Select(b => b.RangeMode).ToArray();

            _isCompositeMode = false;
            // Composite consumes the Channel axis, so the export menu's item set changes with it.
            InvalidateMenuPanel();

            // Multi-series (per-channel) profiles describe a view that no longer exists.
            CloseChannelGroupedLineProfiles();

            _view.RenderingMode = RenderingMode.Lut;

            if (_headerContainer != null) _headerContainer.Content = _lutHeaderRow;
            if (_detailsContainer != null) _detailsContainer.Content = _lutModeDetails;
            // The Composite header is detached; its revert button must stop answering to
            // UpdateRenderRevertButtons, which now drives the LUT header's button instead.
            _compositeRevertBtn = null;
            // Mirror the EnterCompositeMode fix: _compositeSettingsPanel is about to be detached
            // (and is not nulled out here -- EnterCompositeMode always rebuilds it fresh), so its
            // own IsVisible flag would otherwise stay stale-true in the meantime.
            if (_compositeSettingsPanel != null) _compositeSettingsPanel.IsVisible = false;

            // _compositeAxisDimIndex is still the axis just exited -- ResetCompositeUiState (called
            // separately, on a full tracker rebuild) is what clears it, not this method.
            if (_currentData != null && _compositeAxisDimIndex >= 0
                && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount)
            {
                var channelAxis = _currentData.Dimensions[_compositeAxisDimIndex];
                if (_axisTrackers.TryGetValue(channelAxis.Name, out var tracker))
                    tracker.IsVisible = true;
            }

            _orthoController.SetCompositeState(false, -1, 0, null, _compositeBlendMode);

            MarkCompositeDirty();
            UpdateWindowIcon();
            UpdateExtractFrameAllowed();
            // See EnterCompositeMode's matching call for why.
            RefreshAllRegionStatistics();

            // See EnterCompositeMode's matching call: fires Refreshed for LinkedView-based
            // followers (ROI View, Log Transform sync, Spatial Filter sync, ...), which otherwise
            // never learn that Composite -> LUT just happened here.
            Refresh();
        }

        /// <summary>
        /// Called from <see cref="RestoreViewSettings"/> right after <c>RestoreRenderModeSettings</c>
        /// has rehydrated the Composite fields from metadata. <paramref name="mode"/> is the
        /// persisted <c>mxplot.render.mode</c> — the "which mode does this file open in?" flag — and
        /// this is the single place that acts on it.
        /// <para>
        /// When <see cref="CompositeModeEnabled"/> is <c>false</c> the file simply opens in LUT mode:
        /// the kill switch must win over persisted state, not just block new manual entry.
        /// </para>
        /// </summary>
        private void SyncCompositeUiAfterRestore(IMatrixData data, RenderingMode mode)
        {
            if (mode != RenderingMode.Composite || !CompositeModeEnabled) return;

            if (!data.Metadata.TryGetValue(KeyCompositeAxis, out string? axisName)
                || string.IsNullOrEmpty(axisName)) return;
            var channelAxis = data.Axes.FindAxis(axisName);
            if (channelAxis == null) return;

            // Recipes may legitimately be empty here (the flag alone was saved); EnterCompositeMode
            // fills in defaults whenever the count does not match the channel count.
            EnterCompositeMode(channelAxis);
        }

        /// <summary>
        /// Opens an RGB colour image (a byte matrix whose Channel axis is tagged R/G/B) directly in
        /// Composite mode. Returns <c>false</c> — leaving the caller to fall back to LUT mode — for
        /// anything else, including a three-channel fluorescence stack, which is why the decision
        /// looks at <see cref="ColorAxis.IsRgbTriplet"/> and not merely at the channel count.
        /// </summary>
        /// <remarks>
        /// Runs under <see cref="GuardContext.Initializing"/> (the whole of <see cref="SetMatrixData"/>
        /// is), so the <see cref="MarkCompositeDirty"/> calls reached from here are no-ops and the
        /// snapshot taken afterwards records this as the file's clean baseline — which is also where
        /// the revert button returns to.
        /// </remarks>
        private bool TryAutoEnterRgbComposite(IMatrixData data)
        {
            if (!CompositeModeEnabled || data.ValueType != typeof(byte)) return false;

            var channelAxis = data.Axes.FindAxis("Channel");
            if (channelAxis == null || !ColorAxis.IsRgbTriplet(channelAxis)) return false;

            // Fixed has to be in place before EnterCompositeMode: ApplyCompositeRanges runs inside
            // it and, in Fixed mode, EvaluateCompositeRange returns NaN and leaves the recipes alone.
            _compositeScope = CompositeRangeScope.Global;
            _compositeGlobalMode = ValueRangeMode.Fixed;
            _compositeBlendMode = BlendMode.Additive;
            EnterCompositeMode(channelAxis);
            if (!_isCompositeMode) return false;

            // The default recipes carry each channel's *measured* range; replace it with the full
            // byte range so the composite reproduces the original colours instead of stretching
            // them (an image with no bright blue would otherwise come out yellow).
            ApplySharedCompositeRange(0, byte.MaxValue);
            return true;
        }

        /// <summary>
        /// Recomputes <see cref="MxView.CompositeFrameIndices"/> for the current Composite
        /// axis. Called whenever any axis index changes while Composite mode is active
        /// (<see cref="_activeIndexHandler"/> in <see cref="SetMatrixData"/>), since a Z/T
        /// move changes which underlying frame each channel maps to.
        /// </summary>
        private void ApplyCompositeFrameIndices()
        {
            if (_currentData == null || _compositeAxisDimIndex < 0) return;
            _compositeFrameIndices = ComputeCompositeFrameIndices(_currentData, _compositeAxisDimIndex);
            _view.CompositeFrameIndices = _compositeFrameIndices;
            // Each channel now points at a different source frame: re-evaluate every non-Fixed
            // range and refresh the (visible) histograms so the panel reflects the new position.
            // save:false — frame navigation must not mark the document dirty on every step.
            ApplyCompositeRanges(save: false);
            UpdateCompositeHistograms();
        }

        private static int[] ComputeCompositeFrameIndices(IMatrixData data, int axisDimIndex)
        {
            var dims = data.Dimensions;
            int count = dims[axisDimIndex].Count;
            var baseIndices = dims.GetAxisIndices();
            var frameIndices = new int[count];
            for (int c = 0; c < count; c++)
            {
                baseIndices[axisDimIndex] = c;
                frameIndices[c] = dims.GetFrameIndexAt(baseIndices);
            }
            return frameIndices;
        }

        /// <summary>
        /// Builds the default recipe list for a freshly entered Composite session.
        /// Color priority: <see cref="ColorAxis.AssignedColors"/> when present, else the
        /// byte+3ch RGB / fluorescence-palette rule in <see cref="DefaultCompositeColors"/>.
        /// Min/Max per channel use the *current* frame's range for that channel (same
        /// definition as LUT mode's "Current" value-range mode) rather than a full Z/T-stack
        /// scan -- simpler and unambiguous when more than one non-Channel axis exists.
        /// </summary>
        private static List<BlendRecipe> BuildDefaultCompositeRecipes(
            IMatrixData data, Axis channelAxis, int[] frameIndices)
        {
            int count = channelAxis.Count;
            int[] colors = channelAxis is ColorAxis { HasAssignedColors: true } cc && cc.AssignedColors!.Count == count
                ? cc.AssignedColors!.ToArray()
                : DefaultCompositeColors(count, data.ValueType);

            var recipes = new List<BlendRecipe>(count);
            for (int c = 0; c < count; c++)
            {
                var (min, max) = data.GetValueRange(frameIndices[c]);
                recipes.Add(new BlendRecipe(true, colors[c], min, max));
            }
            return recipes;
        }

        // ---- UI construction ------------------------------------------------------

        private const double CompControlHeight = 20;
        private const int CompHistogramBins = 256;

        /// <summary>
        /// Builds the Composite-mode header row, deliberately mirroring the LUT toolbar's shape so
        /// the two modes feel the same:
        /// <code>
        /// LUT:       [☰][LutSelector ][===== ValueRangeBar =====][↩][▾]
        /// Composite: [☰][Composite ▾][===== ValueRangeBar =====]    [▾]
        /// </code>
        /// Channel on/off deliberately lives in the settings panel's channel rows, not here, which
        /// frees the width the range bar needs.
        /// <para>
        /// Fully self-contained: it does not reuse <see cref="_hamburgerBtn"/>,
        /// <see cref="_settingsBtn"/> or <see cref="_rangeBar"/>, so <c>BuildToolbar()</c> and all
        /// of LUT mode's event wiring stay untouched.
        /// </para>
        /// </summary>
        private Control BuildCompositeModeHeader()
        {
            _compositeHamburgerBtn = new Button
            {
                Content = "☰",
                Width = 26,
                Height = 20,
                FontSize = 14,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            _compositeHamburgerBtn.Click += (_, _) =>
            {
                if (_menuPanel == null) _menuPanel = BuildMenuPanel();
                if (_menuPanel.IsVisible) HideMenuPanel(); else ShowMenuPanel();
            };

            var compositeMenuBtn = BuildCompositeMenuButton();

            _compositeSettingsBtn = new Button
            {
                Content = "▾",
                Width = 22,
                Height = 20,
                MinHeight = 20,
                FontSize = 18,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Margin = new Thickness(4, 0),
            };
            ToolTip.SetTip(_compositeSettingsBtn, "Show / hide per-channel blend settings");
            _compositeSettingsBtn.Click += OnCompositeSettingsBtnClick;

            // Same button, same slot, same command as LUT mode's revert -- see _lutVrRevertBtn in
            // MatrixPlotter.Initialization.cs. UpdateRenderRevertButtons drives both.
            _compositeRevertBtn = new Button
            {
                Content = new PathIcon
                {
                    Data = MenuIcons.Undo,
                    Width = 12,
                    Height = 12,
                    Foreground = MenuIcons.DefaultBrush(MenuIcons.Undo),
                },
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1, 0),
                IsVisible = false,
            };
            ToolTip.SetTip(_compositeRevertBtn, "Revert display settings (LUT / Composite / Value Range) to initial state");
            _compositeRevertBtn.Click += (_, _) => RevertRenderState();

            _compositeRangeBar = new ValueRangeBar();
            _compositeRangeBar.SetRoiAvailable(false);   // ROI ranges are out of scope for Composite
            _compositeRangeBar.SetMode(_compositeGlobalMode);
            // The scan is dataset-wide, so the header owns the only button; the per-channel rows
            // keep their "*" as an indicator of which channels are still incomplete.
            _compositeRangeBar.FullScanRequested += async (_, _) => await RequestFullValueRangeScanAsync();
            _compositeRangeBar.ModeChanged += (_, mode) =>
            {
                if (_reentrancy.IsActive(GuardContext.UiSync)) return;
                // Only reachable in Global scope: SetRangeEditable(false) disables the mode menu
                // in Channel-wise scope, where each row owns its own mode instead.
                _compositeGlobalMode = mode;
                ApplyCompositeRanges();
            };
            _compositeRangeBar.RangeChanged += (_, r) =>
            {
                if (_reentrancy.IsActive(GuardContext.UiSync)) return;
                if (_compositeScope != CompositeRangeScope.Global) return;   // read-only union
                ApplySharedCompositeRange(r.Min, r.Max);
            };
            _compositeRangeBar.SearchMinRequested += (_, _) =>
            {
                var (min, _, _) = EvaluateCompositeRange(ValueRangeMode.Current, -1);
                if (!double.IsNaN(min)) ApplySharedCompositeRange(min, CurrentSharedMax());
            };
            _compositeRangeBar.SearchMaxRequested += (_, _) =>
            {
                var (_, max, _) = EvaluateCompositeRange(ValueRangeMode.Current, -1);
                if (!double.IsNaN(max)) ApplySharedCompositeRange(CurrentSharedMin(), max);
            };

            var row = new DockPanel
            {
                Margin = new Thickness(2, 0),
                LastChildFill = true
            };
            DockPanel.SetDock(_compositeHamburgerBtn, Dock.Left);
            DockPanel.SetDock(compositeMenuBtn, Dock.Left);
            DockPanel.SetDock(_compositeSettingsBtn, Dock.Right);
            DockPanel.SetDock(_compositeRevertBtn, Dock.Right);
            row.Children.Add(_compositeHamburgerBtn);
            row.Children.Add(compositeMenuBtn);
            row.Children.Add(_compositeSettingsBtn);
            row.Children.Add(_compositeRevertBtn);   // added after the settings button -> sits to its left
            row.Children.Add(_compositeRangeBar);

            // The button is created hidden; ask for the real state now that it is in the tree.
            UpdateRenderRevertButtons();
            return row;
        }

        /// <summary>
        /// The <c>[Composite ▾]</c> button and its flyout: the Shared / Per-Channel range scope
        /// plus the "Switch to LUT mode" command. It sits where LUT mode puts its LUT selector.
        /// </summary>
        private Button BuildCompositeMenuButton()
        {
            var menuBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock { Text = "Composite", FontSize = 11, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "▾", FontSize = 9, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                Height = CompControlHeight,
                MinHeight = 0,
                Padding = new Thickness(6, 0),
                Margin = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
            };
            // Which axis is composited is no longer implied by the button's own label the way it
            // was when Composite was Channel-only, so it needs to be spelled out for the user.
            string compositedAxisName = _compositeAxisDimIndex >= 0 && _currentData != null
                && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount
                ? _currentData.Dimensions[_compositeAxisDimIndex].Name
                : "?";
            ToolTip.SetTip(menuBtn, $"Composite axis: {compositedAxisName}\nValue range scope / switch back to LUT mode");

            // Display text only — CompositeRangeScope's member names ("Global"/"ChannelWise") are
            // also what gets written to mxplot.composite.scope via ToString(), so they stay as-is;
            // only the label a user reads needs to be clearer.
            var globalRadio = _compositeScopeGlobalRadio = new RadioButton
            {
                Content = "Shared",
                GroupName = "CompositeRangeScope",
                FontSize = 11,
                MinHeight = 0,
                IsChecked = _compositeScope == CompositeRangeScope.Global,
            };
            globalRadio.Classes.Add("compact");
            ToolTip.SetTip(globalRadio, "One Min/Max shared by every channel");

            var channelWiseRadio = _compositeScopeChannelWiseRadio = new RadioButton
            {
                Content = "Per-Channel",
                GroupName = "CompositeRangeScope",
                FontSize = 11,
                MinHeight = 0,
                IsChecked = _compositeScope == CompositeRangeScope.ChannelWise,
            };
            channelWiseRadio.Classes.Add("compact");
            ToolTip.SetTip(channelWiseRadio, "Each channel evaluates and keeps its own Min/Max");

            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

            // Both scope choices act immediately (no separate "Apply"), so closing the flyout on
            // click - like switchToLutItem below - avoids leaving it open after the pick is done.
            // Hide() is deferred a tick (Post, not called inline): calling it synchronously inside
            // IsCheckedChanged interrupts RadioButton's own same-tick "uncheck my group sibling"
            // step (Hide() tears down the flyout's visual tree mid-toggle), which left both radios
            // showing checked next time the flyout opened.
            globalRadio.IsCheckedChanged += (_, _) =>
            {
                if (_reentrancy.IsActive(GuardContext.UiSync)) return;
                if (globalRadio.IsChecked == true)
                {
                    SetCompositeScope(CompositeRangeScope.Global);
                    Dispatcher.UIThread.Post(() => flyout.Hide());
                }
            };
            channelWiseRadio.IsCheckedChanged += (_, _) =>
            {
                if (_reentrancy.IsActive(GuardContext.UiSync)) return;
                if (channelWiseRadio.IsChecked == true)
                {
                    SetCompositeScope(CompositeRangeScope.ChannelWise);
                    Dispatcher.UIThread.Post(() => flyout.Hide());
                }
            };

            // The radios are only in the visual tree while the flyout is open, and RadioButton's own
            // group bookkeeping (attach/detach, sibling-uncheck) can leave neither - or both -
            // checked by the time it opens (seen intermittently). _compositeScope is the single
            // source of truth, so re-derive both radios from it on every open: once before the
            // popup shows, and again after the content is attached and the group has settled.
            flyout.Opening += (_, _) => SyncCompositeScopeRadios();
            flyout.Opened += (_, _) => SyncCompositeScopeRadios();

            var switchToLutItem = ControlFactory.MakeMenuItem("Switch to LUT mode", () =>
            {
                flyout.Hide();
                ExitCompositeMode();
            }, 
            icon: MenuIcons.Palette, 
            fontSize: 11
            );
            switchToLutItem.Height = 24;

            flyout.Content = new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(4),
                MinWidth = 160,
                Children =
                {
                    // "Value Range" collided with the Fixed/Current/All mode dropdown that also
                    // lives in this header - a reader could easily mistake this section for that
                    // one. "Range Scope" names what these two options actually decide: whether the
                    // Min/Max is one value shared across channels, or tracked per channel.
                    new TextBlock
                    {
                        Text = "Range Scope",
                        FontSize = 11,
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(2, 2),
                    },
                    globalRadio,
                    channelWiseRadio,
                    ControlFactory.MakeSep(),
                    switchToLutItem,
                },
            };
            FlyoutBase.SetAttachedFlyout(menuBtn, flyout);
            menuBtn.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(menuBtn);
            return menuBtn;
        }

        /// <summary>
        /// Returns the composited axis's tag for <paramref name="index"/> (e.g. "Red", "GFP") when
        /// the axis is a <see cref="TaggedAxis"/>, else falls back to the same
        /// <see cref="DefaultCompositeTagPrefix"/>-based name <see cref="PromoteToColorAxisIfNeeded"/>
        /// would have generated (e.g. "Ch0", "Z0").
        /// </summary>
        private string GetChannelDisplayName(int index)
        {
            var channelAxis = _compositeAxisDimIndex >= 0 && _currentData != null
                && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount
                ? _currentData.Dimensions[_compositeAxisDimIndex]
                : null;
            if (channelAxis is TaggedAxis tagged && index < tagged.Tags.Count)
                return tagged.Tags[index];
            return $"{DefaultCompositeTagPrefix(channelAxis?.Name ?? "Channel")}{index}";
        }

        /// <summary>
        /// Builds the Composite settings panel: one <see cref="BlendRecipeBar"/> per channel.
        /// Collapsed by default, matching the LUT settings panel.
        /// </summary>
        private Border BuildCompositeModeDetails()
        {
            var channelList = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
            _compositeBars = new BlendRecipeBar[_compositeRecipes.Count];

            for (int i = 0; i < _compositeRecipes.Count; i++)
            {
                int idx = i;
                var bar = new BlendRecipeBar(GetChannelDisplayName(i), _compositeRecipes[i]);
                bar.RecipeChanged += (_, updated) => OnBarRecipeChanged(idx, updated);
                bar.RangeModeChanged += (_, _) => OnBarRangeModeChanged();
                bar.RenameRequested += (_, _) => _ = RenameChannelAsync(idx);
                bar.SearchMinRequested += (_, _) =>
                {
                    var (min, _, _) = EvaluateCompositeRange(ValueRangeMode.Current, idx);
                    if (!double.IsNaN(min)) ApplyChannelRange(idx, min, _compositeRecipes[idx].ValueMax);
                };
                bar.SearchMaxRequested += (_, _) =>
                {
                    var (_, max, _) = EvaluateCompositeRange(ValueRangeMode.Current, idx);
                    if (!double.IsNaN(max)) ApplyChannelRange(idx, _compositeRecipes[idx].ValueMin, max);
                };
                _compositeBars[i] = bar;
                channelList.Children.Add(bar);
                if (i < _compositeRecipes.Count - 1)
                    channelList.Children.Add(ControlFactory.MakeSep(new Thickness(0)));
            }

            // Composite is no longer restricted to the Channel axis (see ColorCoded_View_InitialDesign.md
            // section 3.3.7), so this panel can now realistically see far more than the 2-4 rows it was
            // originally sized for. A ScrollViewer keeps it usable instead of pushing the rest of the
            // window off-screen; DefaultCompositeColors already degrades gracefully past 8 channels
            // (even hue rotation), so no hard cap on channel count is needed alongside this.
            var scroller = new ScrollViewer
            {
                Content = channelList,
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            _compositeSettingsPanel = new Border
            {
                Child = scroller,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = false,
            };
            return _compositeSettingsPanel;
        }

        /// <summary>
        /// Renames one channel by editing the Channel axis tag in place through
        /// <see cref="TaggedAxis.SetTag"/>. Unlike renaming the axis itself (which downgrades a
        /// specialised axis, see <c>RenameAxisAsync</c>) this needs no <c>DefineDimensions</c>,
        /// so no axis object is replaced and no tracker has to be rebuilt.
        /// <para>
        /// The tag is the durable home for the name: it round-trips through the .mxd axis
        /// serialization and through OME-TIFF's <c>Channel/@Name</c>.
        /// </para>
        /// </summary>
        private async Task RenameChannelAsync(int index)
        {
            if (_currentData == null || _compositeAxisDimIndex < 0) return;
            if (_currentData.Dimensions[_compositeAxisDimIndex] is not TaggedAxis tagged) return;
            if (index < 0 || index >= tagged.Tags.Count) return;

            var dlg = new AxisRenameDialog(tagged.Tags[index], "Rename Channel");
            await dlg.ShowDialog(this);
            string? newName = dlg.Result?.Trim();
            if (string.IsNullOrEmpty(newName) || newName == tagged.Tags[index]) return;

            tagged.SetTag(index, newName);
            _compositeBars?[index].SetChannelName(newName);
            SetScaleDirty(true);
        }

        // ---- Range scope / evaluation ----------------------------------------------

        /// <summary>
        /// Applies the availability rules that depend on the dataset and the current scope:
        /// the All entry only makes sense when non-Channel axes actually exist, and in Global
        /// scope the per-channel bars are disabled because they merely mirror the header.
        /// </summary>
        private void ConfigureCompositeRangeBars()
        {
            var data = _currentData;
            if (data == null || _compositeAxisDimIndex < 0) return;
            int channelCount = data.Dimensions[_compositeAxisDimIndex].Count;

            // "All" differs from "Current" only when there is something to scan beyond the
            // Channel axis (i.e. a Z/Time/... axis with more than one index).
            bool multiFrame = data.FrameCount > channelCount;
            bool global = _compositeScope == CompositeRangeScope.Global;

            _compositeRangeBar?.SetMultiFrame(multiFrame);
            _compositeRangeBar?.SetRangeEditable(global);

            if (_compositeBars == null) return;
            foreach (var bar in _compositeBars)
            {
                bar.SetMultiFrame(multiFrame);
                bar.SetRangeEnabled(!global);
            }
        }

        /// <summary>
        /// Makes the Scope flyout's radios reflect <c>_compositeScope</c> exactly one-checked,
        /// whatever state the radio group bookkeeping left them in. Writes run under
        /// <see cref="GuardContext.UiSync"/> so the radios' own IsCheckedChanged handlers treat
        /// them as a display sync (no scope change, no flyout close), and the radio that should
        /// end up checked is written last so no group sibling-uncheck can clear it.
        /// </summary>
        private void SyncCompositeScopeRadios()
        {
            var global = _compositeScopeGlobalRadio;
            var channelWise = _compositeScopeChannelWiseRadio;
            if (global == null || channelWise == null) return;

            bool wantGlobal = _compositeScope == CompositeRangeScope.Global;
            using (_reentrancy.Begin(GuardContext.UiSync))
            {
                if (wantGlobal)
                {
                    channelWise.IsChecked = false;
                    global.IsChecked = true;
                }
                else
                {
                    global.IsChecked = false;
                    channelWise.IsChecked = true;
                }
            }
        }

        private void SetCompositeScope(CompositeRangeScope scope)
        {
            if (_compositeScope == scope) return;
            _compositeScope = scope;
            // Keeps the flyout's own radio buttons correct when scope changes from somewhere
            // other than the user clicking one of them (e.g. MatrixPlotter.CompositeRecipes
            // assigned externally). They are also re-synced every time the flyout opens.
            SyncCompositeScopeRadios();
            ConfigureCompositeRangeBars();
            ApplyCompositeRanges();
            MarkCompositeDirty();
        }

        /// <summary>Re-evaluates ranges after the user changed one channel's mode.</summary>
        private void OnBarRangeModeChanged()
        {
            if (_reentrancy.IsActive(GuardContext.UiSync)) return;
            ApplyCompositeRanges();
            MarkCompositeDirty();
        }

        private double CurrentSharedMin() => _compositeRecipes.Count > 0 ? _compositeRecipes[0].ValueMin : 0.0;
        private double CurrentSharedMax() => _compositeRecipes.Count > 0 ? _compositeRecipes[0].ValueMax : 1.0;

        /// <summary>Writes one shared Min/Max onto every channel (Global scope).</summary>
        private void ApplySharedCompositeRange(double min, double max)
        {
            using (_reentrancy.Begin(GuardContext.UiSync))
            {
                for (int i = 0; i < _compositeRecipes.Count; i++)
                {
                    _compositeRecipes[i] = _compositeRecipes[i] with { ValueMin = min, ValueMax = max };
                    if (_compositeBars != null && i < _compositeBars.Length)
                        _compositeBars[i].SetRange(min, max);
                }
                _compositeRangeBar?.SetRange(min, max);
            }
            PushCompositeRecipes();
            MarkCompositeDirty();
        }

        /// <summary>Writes one channel's Min/Max (Channel-wise scope) and refreshes the header union.</summary>
        private void ApplyChannelRange(int index, double min, double max)
        {
            if (index < 0 || index >= _compositeRecipes.Count) return;
            using (_reentrancy.Begin(GuardContext.UiSync))
            {
                _compositeRecipes[index] = _compositeRecipes[index] with { ValueMin = min, ValueMax = max };
                if (_compositeBars != null && index < _compositeBars.Length)
                    _compositeBars[index].SetRange(min, max);
                UpdateCompositeHeaderUnion();
            }
            PushCompositeRecipes();
            MarkCompositeDirty();
        }

        /// <summary>
        /// Evaluates a value range for the given mode. <paramref name="channelIndex"/> &lt; 0 means
        /// the Global (all-channel) range; otherwise it is that single channel's range.
        /// <para>
        /// All four combinations map onto a single existing Core call. Note the deliberate
        /// asymmetry between the two APIs: <c>GetValueRange(Axis, …)</c> <b>scans</b> the given axis
        /// and holds the others at their current position, while
        /// <c>GetGlobalValueRange(Axis, index, …)</c> <b>locks</b> the given axis and scans all others.
        /// </para>
        /// <para>
        /// Returns <c>(NaN, NaN, 0)</c> for Fixed (and any unsupported mode), meaning "keep the
        /// value the user already set".
        /// </para>
        /// </summary>
        private (double Min, double Max, int InvalidCount) EvaluateCompositeRange(ValueRangeMode mode, int channelIndex)
        {
            var data = _currentData;
            if (data == null || _compositeAxisDimIndex < 0) return (double.NaN, double.NaN, 0);

            var chAxis = data.Dimensions[_compositeAxisDimIndex];
            int valueMode = data.ValueType == typeof(System.Numerics.Complex)
                ? (int)_view.ComplexValueMode : 0;
            // Virtual: never force a full disk scan — use what is cached and report the rest as
            // imperfect, exactly as LUT mode's ApplyAllModeRange does. A large scan on non-Virtual
            // data runs in the background (DeferFullValueRangeScan), which re-applies these ranges
            // when it finishes. Evaluated only for All below, so Current never starts a scan.
            bool ForceScan() => !data.IsVirtual && !DeferFullValueRangeScan(data);

            switch (mode)
            {
                case ValueRangeMode.Current when channelIndex < 0:
                {
                    // Scan the Channel axis, holding every other axis at its current index.
                    var (min, max) = data.GetValueRange(chAxis, null, valueMode);
                    return (min, max, 0);
                }
                case ValueRangeMode.Current:
                {
                    int frameIndex = channelIndex < _compositeFrameIndices.Length
                        ? _compositeFrameIndices[channelIndex] : 0;
                    var (min, max) = data.GetValueRange(frameIndex, valueMode);
                    return (min, max, 0);
                }
                case ValueRangeMode.All when channelIndex < 0:
                    return GlobalRangeOverAll(data, valueMode, ForceScan());
                case ValueRangeMode.All:
                    return GlobalRangeOverChannel(data, chAxis, channelIndex, valueMode, ForceScan());
                default:
                    return (double.NaN, double.NaN, 0);   // Fixed / Roi: keep user values
            }
        }

        // The IMatrixData interface has no valueMode-aware GetGlobalValueRange overload, so
        // non-Magnitude Complex data goes through the typed MatrixData<Complex> overload — the
        // same split LUT mode's ApplyAllModeRange uses.
        private static (double Min, double Max, int InvalidCount) GlobalRangeOverAll(
            IMatrixData data, int valueMode, bool force)
        {
            if (valueMode == 0)
            {
                var (min, max) = data.GetGlobalValueRange(out var invalids, force);
                return (min, max, invalids.Count);
            }
            var complexData = (MatrixData<System.Numerics.Complex>)data;
            var (cmin, cmax) = complexData.GetGlobalValueRange(valueMode, out var cinvalids, force);
            return (cmin, cmax, cinvalids.Count);
        }

        private static (double Min, double Max, int InvalidCount) GlobalRangeOverChannel(
            IMatrixData data, Axis chAxis, int channelIndex, int valueMode, bool force)
        {
            if (valueMode == 0)
            {
                var (min, max) = data.GetGlobalValueRange(chAxis, channelIndex, out var invalids, force);
                return (min, max, invalids.Count);
            }
            var complexData = (MatrixData<System.Numerics.Complex>)data;
            var (cmin, cmax) = complexData.GetGlobalValueRange(chAxis, channelIndex, valueMode, out var cinvalids, force);
            return (cmin, cmax, cinvalids.Count);
        }

        /// <summary>
        /// Recomputes every channel's range according to the current scope and mode(s), pushes the
        /// result to the view and the orthogonal side views, and refreshes the header bar.
        /// Fixed channels keep whatever the user entered.
        /// </summary>
        /// <param name="save">
        /// <c>false</c> when called from frame navigation, where persisting on every slider step
        /// would be wasteful and would mark the document dirty continuously.
        /// </param>
        private void ApplyCompositeRanges(bool save = false)
        {
            if (!_isCompositeMode || _currentData == null) return;

            using (_reentrancy.Begin(GuardContext.UiSync))
            {
                if (_compositeScope == CompositeRangeScope.Global)
                {
                    var (min, max, invalid) = EvaluateCompositeRange(_compositeGlobalMode, -1);

                    // Mirror the shared mode onto the rows even when the evaluation returned nothing
                    // (Fixed), so a row chip never contradicts the header it is slaved to.
                    if (_compositeBars != null)
                        foreach (var bar in _compositeBars)
                            bar.SetRangeMode(_compositeGlobalMode);

                    if (!double.IsNaN(min))
                    {
                        for (int i = 0; i < _compositeRecipes.Count; i++)
                        {
                            _compositeRecipes[i] = _compositeRecipes[i] with { ValueMin = min, ValueMax = max };
                            if (_compositeBars != null && i < _compositeBars.Length)
                                _compositeBars[i].SetRange(min, max);
                        }
                        _compositeRangeBar?.SetRange(min, max);
                    }
                    else if (_compositeRecipes.Count > 0)
                    {
                        // Fixed: the recipes already hold the user's shared range, but the header
                        // bar has no constructor path for a value (it starts blank), so a freshly
                        // built header -- e.g. a child window seeded with a Fixed range -- would
                        // otherwise show empty Min/Max.
                        _compositeRangeBar?.SetRange(CurrentSharedMin(), CurrentSharedMax());
                    }
                    _compositeRangeBar?.SetImperfect(invalid > 0, invalid);
                    _compositeRangeBar?.SetFullScanAvailable(_currentData.IsVirtual); // see RefreshAllModeImperfectBadge
                }
                else
                {
                    int totalInvalid = 0;
                    for (int i = 0; i < _compositeRecipes.Count; i++)
                    {
                        var bar = _compositeBars != null && i < _compositeBars.Length ? _compositeBars[i] : null;
                        var mode = bar?.RangeMode ?? ValueRangeMode.Current;

                        var (min, max, invalid) = EvaluateCompositeRange(mode, i);
                        totalInvalid += invalid;
                        bar?.SetImperfect(invalid > 0, invalid);
                        if (double.IsNaN(min)) continue;   // Fixed: keep the user's values

                        _compositeRecipes[i] = _compositeRecipes[i] with { ValueMin = min, ValueMax = max };
                        bar?.SetRange(min, max);
                    }
                    UpdateCompositeHeaderUnion();
                    _compositeRangeBar?.SetImperfect(totalInvalid > 0, totalInvalid);
                    _compositeRangeBar?.SetFullScanAvailable(_currentData.IsVirtual);
                }
            }

            PushCompositeRecipes();
            if (save) MarkCompositeDirty();
        }

        /// <summary>
        /// Re-evaluates the visible value-range state after ComplexValueMode changes.
        /// Composite Global+Current uses the composite header range; other modes keep the existing
        /// single-view All-mode behavior.
        /// </summary>
        private void SyncComplexValueModeChanged()
        {
            if (_isCompositeMode && _compositeScope == CompositeRangeScope.Global
                && _compositeGlobalMode == ValueRangeMode.Current)
            {
                ApplyCompositeRanges();
            }
            else if (_rangeBar.Mode == ValueRangeMode.All)
            {
                ApplyAllModeRange();
            }

            _orthoController.SyncRenderSettings();
            UpdateStatusBar();

            // The projected value at every sample point depends on ComplexValueMode
            // (Magnitude/Real/Imaginary/Phase/Power) — re-extract open Line Profile plots
            // so they match what is now shown on screen. Channel membership is unaffected,
            // so the lightweight index-based update (no series add/remove) is sufficient.
            UpdateAllLineProfiles();
        }

        /// <summary>
        /// Shows the union of all channel ranges in the header bar (Channel-wise scope), where
        /// there is no single shared range to display. Caller must already hold the UiSync guard.
        /// </summary>
        private void UpdateCompositeHeaderUnion()
        {
            if (_compositeRangeBar == null || _compositeRecipes.Count == 0) return;
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            foreach (var r in _compositeRecipes)
            {
                if (r.ValueMin < min) min = r.ValueMin;
                if (r.ValueMax > max) max = r.ValueMax;
            }
            if (!double.IsInfinity(min) && !double.IsInfinity(max))
                _compositeRangeBar.SetRange(min, max);
        }

        // ---- Recipe plumbing --------------------------------------------------------

        /// <summary>
        /// Applies an edit made in a channel's <see cref="BlendRecipeBar"/> (visibility, colour,
        /// gain, or a typed Min/Max) and refreshes the header union.
        /// </summary>
        private void OnBarRecipeChanged(int index, BlendRecipe updated)
        {
            if (index < 0 || index >= _compositeRecipes.Count) return;

            var previous = _compositeRecipes[index];
            bool visibilityChanged = previous.IsVisible != updated.IsVisible;
            _compositeRecipes[index] = updated;

            if (_compositeScope == CompositeRangeScope.Global)
            {
                // The only way a row can change its range in Global scope is by dragging its
                // histogram (its range bar is disabled). That is an explicit choice about the
                // *shared* range, so broadcast it to every channel and pin the header to Fixed,
                // exactly as dragging the LUT histogram pins the LUT bar.
                if (previous.ValueMin != updated.ValueMin || previous.ValueMax != updated.ValueMax)
                {
                    _compositeGlobalMode = ValueRangeMode.Fixed;
                    using (_reentrancy.Begin(GuardContext.UiSync))
                        _compositeRangeBar?.SetMode(ValueRangeMode.Fixed);
                    UpdateChannelVisibilityLocks();
                    ApplySharedCompositeRange(updated.ValueMin, updated.ValueMax);
                    return;
                }
                // A row's Min/Max cannot be edited in Global scope, but colour/gain/visibility can —
                // the shared range must stay identical across channels regardless.
                using (_reentrancy.Begin(GuardContext.UiSync))
                    _compositeRangeBar?.SetRange(updated.ValueMin, updated.ValueMax);
            }
            else
            {
                using (_reentrancy.Begin(GuardContext.UiSync))
                    UpdateCompositeHeaderUnion();
            }

            UpdateChannelVisibilityLocks();
            PushCompositeRecipes();
            MarkCompositeDirty();

            // Channel on/off changes which series BuildProfileSeries returns, not just their
            // values, so open Line Profile plots need a full rebuild (not the lightweight
            // index-based UpdateAllLineProfiles used for e.g. ComplexValueMode changes).
            if (visibilityChanged)
            {
                RebuildChannelGroupedLineProfiles();
                // ComputeCompositeStatisticsLabel iterates only IsVisible recipes -- a toggled
                // channel changes which lines appear (and the "... (+N more)" count), unlike
                // Color/Gain/Gamma/ValueMin/Max, which are purely rendering parameters that never
                // touch the raw-pixel Min/Max/Avg statistics computed from the data itself.
                RefreshAllRegionStatistics();
            }
        }

        /// <summary>
        /// Locks the visibility toggle of whichever channel is the only one left visible, so a
        /// composite can never be reduced to an empty image. Every other toggle stays free.
        /// </summary>
        private void UpdateChannelVisibilityLocks()
        {
            if (_compositeBars == null) return;

            int visibleCount = 0, lastVisible = -1;
            for (int i = 0; i < _compositeRecipes.Count; i++)
                if (_compositeRecipes[i].IsVisible) { visibleCount++; lastVisible = i; }

            for (int i = 0; i < _compositeBars.Length; i++)
                _compositeBars[i].SetVisibilityLocked(visibleCount == 1 && i == lastVisible);
        }

        /// <summary>
        /// Mirrors the composite recipe colours back onto the Channel axis when it is a
        /// <see cref="ColorAxis"/>, keeping the axis the single source of truth for channel
        /// colour. Formats that store it -- OME-TIFF's <c>Channel/@Color</c> -- then export exactly
        /// what the user sees, instead of the colours the file was originally acquired with.
        /// <para>
        /// No-op for a plain Channel axis: changing an axis's type would require
        /// <c>DefineDimensions</c> and a full tracker rebuild, which is far too invasive to do as a
        /// side effect of editing a colour. Such data still keeps its colours in the
        /// <c>mxplot.composite.*</c> metadata, so nothing is lost inside MxPlot.
        /// </para>
        /// </summary>
        private void SyncRecipeColorsToChannelAxis()
        {
            if (_currentData == null || _compositeAxisDimIndex < 0) return;
            if (_currentData.Dimensions[_compositeAxisDimIndex] is not ColorAxis channelAxis) return;
            if (channelAxis.Tags.Count != _compositeRecipes.Count) return;

            var colors = new int[_compositeRecipes.Count];
            for (int i = 0; i < colors.Length; i++) colors[i] = _compositeRecipes[i].ColorArgb;

            // Skip the assignment (and its change event) when nothing actually moved.
            if (channelAxis.HasAssignedColors && channelAxis.AssignedColors!.SequenceEqual(colors)) return;
            channelAxis.AssignColors(colors);
        }

        /// <summary>
        /// Pushes the current recipe list to the main view and the orthogonal side views.
        /// Call after any change to <see cref="_compositeRecipes"/>.
        /// </summary>
        private void PushCompositeRecipes()
        {
            SyncRecipeColorsToChannelAxis();

            // Only assign when the recipes actually moved. ToList() produces a new instance every
            // call, so the styled property changed identity even when the values were untouched,
            // and RenderSurface's CompositeRecipes handler rebuilds the bitmap on every change.
            // Refresh() calls ApplyCompositeRanges (which ends here) and then _view.Refresh(), so
            // a live feed whose ranges never move - a camera on a fixed range - paid for two full
            // frame renders per refresh instead of one. Measured at 4096x4096 RGB: composite draw
            // 28.7 ms, of which 15.4 ms was this redundant render.
            // BlendRecipe is a record, so the comparison is exact, and it is per channel, not per
            // pixel. A recipe edit still differs and still pushes.
            var next = _compositeRecipes.ToList();
            if (!RecipeListEquals(_view.CompositeRecipes, next))
                _view.CompositeRecipes = next;
            _orthoController.SetCompositeState(
                true, _compositeAxisDimIndex, _compositeRecipes.Count, _compositeRecipes, _compositeBlendMode);

            // The separate XY-projection window (MatrixPlotter.VolumeOperation.cs) is a distinct
            // MatrixPlotter instance outside _orthoController's reach, so a recipe-only edit (e.g.
            // dragging a channel's histogram Max) never repaints it: OnXYProjectionChanged only
            // re-applies composite state on an actual projection recompute, not on every recipe
            // push. Call the window's own PushCompositeRecipes directly here instead of routing
            // through ApplyCompositeStateToProjectionWindow's ApplyCompositeFrameIndices path,
            // which also recomputes frame indices/ranges/histograms - far too expensive to run on
            // every tick of a histogram drag.
            if (_xyProjectionWindow is { IsVisible: true, _isCompositeMode: true })
                _xyProjectionWindow.PushCompositeRecipes();
        }

        private static bool RecipeListEquals(
            System.Collections.Generic.IReadOnlyList<BlendRecipe>? current,
            System.Collections.Generic.IReadOnlyList<BlendRecipe> next)
        {
            if (current == null || current.Count != next.Count) return false;
            for (int i = 0; i < next.Count; i++)
                if (!Equals(current[i], next[i])) return false;
            return true;
        }

        /// <summary>
        /// Requests a recompute of every channel's histogram: after the active frame changes, when
        /// the settings panel is opened, and from <see cref="Refresh"/> (data-content changes / the
        /// <see cref="MxView.RefreshRequested"/> bypass). Histograms are only computed while the
        /// settings panel is actually expanded — the same "don't pay for hidden UI" rule the LUT
        /// panel follows via <see cref="UpdateHistogram"/>.
        /// </summary>
        /// <remarks>
        /// Coalescing dirty-flag + single-in-flight loop, not cancel-and-restart -- see
        /// <see cref="UpdateHistogram"/>'s own doc comment for the full rationale (shared here
        /// verbatim: a computation slower than the caller's own call rate could otherwise never
        /// finish, always pre-empted before painting anything). This is called on every Z/T step
        /// while the panel is open (<see cref="ApplyCompositeFrameIndices"/>), so without
        /// coalescing, a slow Debug-build binning pass (see <see cref="UpdateCompositeHistogramsOnceAsync"/>'s
        /// own remarks) plus rapid frame-stepping was exactly the starvation case this replaces.
        /// </remarks>
        private async void UpdateCompositeHistograms()
        {
            _compositeHistogramDirty = true;
            if (_compositeHistogramRunning) return;
            _compositeHistogramRunning = true;
            try
            {
                while (_compositeHistogramDirty)
                {
                    _compositeHistogramDirty = false;
                    await UpdateCompositeHistogramsOnceAsync();
                }
            }
            finally
            {
                _compositeHistogramRunning = false;
            }
        }

        /// <summary>
        /// One histogram-recompute pass for every channel, always run to completion (see
        /// <see cref="UpdateCompositeHistograms"/>'s own doc comment for why this is never
        /// cancelled mid-flight).
        /// </summary>
        /// <remarks>
        /// The per-pixel binning in <c>HistogramAnalyzer.Compute</c> is only a few ms per channel
        /// in a Release build, but well over half a second per channel in Debug — the generic
        /// numeric conversion it calls per pixel simply isn't optimized away without the JIT's
        /// Release-mode passes. That's a normal day-to-day dev-loop condition (running via
        /// <c>dotnet run</c>/F5), not a shipped-build concern, but three channels run synchronously
        /// on the UI thread turns it into a multi-second freeze either way, which is the actual
        /// bug: nothing should block the UI thread for a duration this variable.
        /// <para>
        /// The three channels' binning — not their <see cref="IMatrixData.GetValueRange(int)"/>
        /// lookups, which share a plain <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
        /// cache and are not safe to race — run in parallel for in-memory data, each roughly
        /// halving further with every additional core.
        /// </para>
        /// </remarks>
        private async Task UpdateCompositeHistogramsOnceAsync()
        {
            if (!_isCompositeMode || _compositeBars == null || _currentData == null) return;
            if (_compositeSettingsPanel?.IsVisible != true) return;

            var data = _currentData;
            var bars = _compositeBars;
            var frameIndices = _compositeFrameIndices;
            int count = Math.Min(bars.Length, frameIndices.Length);

            try
            {
                var results = await Task.Run(() =>
                {
                    var slots = new (int[] Bins, double Min, double Max)[count];

                    // GetValueRange populates the shared cache above on first touch, so these run
                    // sequentially; CreateHistogram, given an explicit min/max, only reads pixels.
                    var ranges = new (double Min, double Max)[count];
                    for (int i = 0; i < count; i++)
                        ranges[i] = data.GetValueRange(frameIndices[i]);

                    void ComputeOne(int i)
                    {
                        var (min, max) = ranges[i];
                        slots[i] = (data.CreateHistogram(frameIndices[i], CompHistogramBins, min, max), min, max);
                    }

                    if (data.IsVirtual)
                        // MMF-backed frames route through an LRU cache that evicts/loads on access,
                        // which is not safe to touch from multiple threads at once.
                        for (int i = 0; i < count; i++) ComputeOne(i);
                    else
                        Parallel.For(0, count, ComputeOne);

                    return slots;
                });

                for (int i = 0; i < count; i++)
                    bars[i].SetHistogram(results[i].Bins, results[i].Min, results[i].Max);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateCompositeHistograms] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Mirrors <see cref="_settingsBtn"/>'s open/close + auto window-resize behavior
        /// (see <c>WireLutSettingsPanelEvents</c>) but targets <see cref="_compositeSettingsPanel"/>.
        /// Kept as a separate handler rather than generalizing the LUT one, so LUT mode's
        /// existing wiring is never touched.
        /// </summary>
        private void OnCompositeSettingsBtnClick(object? sender, RoutedEventArgs e)
        {
            if (_compositeSettingsPanel == null || _compositeSettingsBtn == null) return;

            bool opening = !_compositeSettingsPanel.IsVisible;
            double panelH = _compositeSettingsPanel.Bounds.Height;

            _compositeSettingsPanel.IsVisible = opening;
            _compositeSettingsBtn.Content = opening ? "▴" : "▾";
            _compositeSettingsBtn.Background = opening ? Brushes.LightGray : Brushes.Transparent;

            // Histograms are skipped while the panel is collapsed, so fill them in on open.
            if (opening) UpdateCompositeHistograms();

            if (WindowState == WindowState.Maximized) return;

            if (!opening && panelH > 0)
            {
                Height -= panelH;
            }
            else if (opening)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    double h = _compositeSettingsPanel.Bounds.Height;
                    if (h > 0) Height += h;
                }, DispatcherPriority.Background);
            }
        }
    }
}
