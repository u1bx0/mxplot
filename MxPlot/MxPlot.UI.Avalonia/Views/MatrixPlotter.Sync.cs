using MxPlot.Core;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Identifies which scale parameter (Min, Max, or Step) was changed.
    /// </summary>
    public enum ScaleParameter
    {
        Min,
        Max,
        Step
    }

    /// <summary>
    /// Captures the display settings of a <see cref="MatrixPlotter"/> at a point in time.
    /// Used by <see cref="MatrixPlotterSyncGroup"/> to support Revert-to-initial-state.
    /// Intentionally excludes <see cref="IMatrixData"/> itself — data-level revert
    /// (e.g. Crop undo) is a separate concern handled by the action/operation layer.
    /// </summary>
    internal readonly record struct PlotterSnapshot(
        LookupTable Lut,
        int LutDepth,
        bool IsInverted,
        ValueRangeMode RangeMode,
        double FixedMin,
        double FixedMax,
        Dictionary<string, int> AxisIndices,
        Dictionary<string, (double Min, double Max, double Step)> ScaleCache);

    public partial class MatrixPlotter
    {
        // Guard: set during SyncApply* calls to suppress re-firing of sync events.
        //private bool _syncApplying; // -> Changed to ReentrancyGuard with GuardContext.SyncApply flag.

        // ── Internal sync events ─────────────────────────────────────────────
        // Each fires on the UI thread when the user changes the corresponding
        // setting, but NOT when the change originates from a sync apply call.

        internal event EventHandler<LookupTable>? SyncLutChanged;
        internal event EventHandler<int>? SyncLutDepthChanged;
        internal event EventHandler<bool>? SyncInvertedChanged;
        internal event EventHandler<ValueRangeMode>? SyncRangeModeChanged;
        internal event EventHandler<(double Min, double Max)>? SyncFixedRangeChanged;
        /// <summary>
        /// Fired when the user moves an AxisTracker slider.
        /// Carries the axis name and the new 0-based index so that only targets
        /// that share an axis with the same name are updated.
        /// </summary>
        internal event EventHandler<(string AxisName, int Index)>? SyncAxisIndexChanged;

        /// <summary>
        /// Fired when a scale parameter (Min/Max/Step) changes for X, Y, or any Axis.
        /// <para/>
        /// TargetAxis: "X", "Y", or an Axis name (e.g., "Time", "Z").
        /// Parameter: which parameter changed (Min, Max, or Step).
        /// Value: the new value.
        /// <para/>
        /// Fired from MatrixPlotter.InfoTab.cs Wire() handlers when the user edits
        /// scale values via the Info tab UI.
        /// </summary>
        internal event EventHandler<(string TargetAxis, ScaleParameter Parameter, double Value)>? SyncScaleChanged;

        /// <summary>Fired when this plotter (Primary role) starts an interactive crop.</summary>
        internal event EventHandler<CropRoiBounds>? SyncCropStarted;
        /// <summary>Fired when the primary crop ROI moves or resizes.</summary>
        internal event EventHandler<CropRoiBounds>? SyncCropRoiChanged;
        /// <summary>Fired when the user confirms the crop. Carries the final primary ROI bounds.</summary>
        internal event EventHandler<CropRoiBounds>? SyncCropCompleted;
        /// <summary>Fired when the crop is cancelled by the primary.</summary>
        internal event EventHandler? SyncCropCancelled;
        /// <summary>Fired when a Replace crop is reverted via the Processing menu on this plotter.</summary>
        internal event EventHandler? SyncCropReverted;

        // ── Snapshot capture / restore ───────────────────────────────────────

        /// <summary>
        /// Captures the current display settings as a lightweight snapshot.
        /// </summary>
        internal PlotterSnapshot CaptureSnapshot()
        {
            var axisIndices = new Dictionary<string, int>();
            var scaleCache = new Dictionary<string, (double, double, double)>();

            if (_currentData != null)
            {
                foreach (var axis in _currentData.Axes)
                {
                    axisIndices[axis.Name] = axis.Index;
                    scaleCache[axis.Name] = (axis.Min, axis.Max, axis.Step);
                }

                var scale = _currentData.GetScale();
                scaleCache["X"] = (scale.XMin, scale.XMax, scale.XStep);
                scaleCache["Y"] = (scale.YMin, scale.YMax, scale.YStep);
            }

            return new PlotterSnapshot(
                Lut: _view.Lut,
                LutDepth: _view.LutDepth,
                IsInverted: _view.IsInvertedColor,
                RangeMode: _rangeBar.Mode,
                FixedMin: _view.FixedMin,
                FixedMax: _view.FixedMax,
                AxisIndices: axisIndices,
                ScaleCache: scaleCache);
        }

        /// <summary>
        /// Restores display settings from a previously captured snapshot.
        /// </summary>
        internal void RestoreSnapshot(PlotterSnapshot snap)
        {
            SyncApplyLut(snap.Lut);
            SyncApplyLutDepth(snap.LutDepth);
            SyncApplyInverted(snap.IsInverted);
            SyncApplyRangeMode(snap.RangeMode);
            SyncApplyFixedRange(snap.FixedMin, snap.FixedMax);

            foreach (var (axisName, index) in snap.AxisIndices)
                SyncApplyAxisIndex(axisName, index);

            foreach (var (targetAxis, (min, max, step)) in snap.ScaleCache)
            {
                SyncApplyScale(targetAxis, ScaleParameter.Min, min);
                SyncApplyScale(targetAxis, ScaleParameter.Max, max);
            }
        }

        // ── Internal sync apply methods ──────────────────────────────────────
        // Called by MatrixPlotterSyncGroup to propagate a change from another
        // plotter without re-triggering sync events.

        internal void SyncApplyLut(LookupTable lut)
        {
            using var _ = _reentrancy.Begin(GuardContext.SyncApply);
            _lutSelector.SelectLut(lut);
        }

        internal void SyncApplyLutDepth(int depth)
        {
            using var _ = _reentrancy.Begin(GuardContext.SyncApply);
            ApplyLutDepthToLayers(depth);
        }

        internal void SyncApplyInverted(bool inverted)
        {
            using var _ = _reentrancy.Begin(GuardContext.SyncApply);
            ApplyInvertedToLayers(inverted);
        }

        internal void SyncApplyRangeMode(ValueRangeMode mode)
        {
            using var _ = _reentrancy.Begin(GuardContext.SyncApply);
            ApplyRangeModeToLayers(mode);
        }

        internal void SyncApplyFixedRange(double min, double max)
        {
            using var _ = _reentrancy.Begin(GuardContext.SyncApply);
            ApplyFixedRangeToLayers(min, max);
        }

        /// <summary>
        /// Applies a value-range mode change across the RangeBar/View layers. This is the shared
        /// mechanism used by both external control (VM/Facade-driven changes, in
        /// <see cref="OnDataContextChanged"/>) and cross-plotter Linked Sync (<see cref="SyncApplyRangeMode"/>) —
        /// they are two independent callers of the same operation, not one built on top of the
        /// other. Cross-plotter re-broadcast suppression (<see cref="GuardContext.SyncApply"/>) is
        /// specifically <see cref="SyncApplyRangeMode"/>'s concern, not this method's — calling this
        /// directly (as external control does) lets <c>SyncRangeModeChanged</c>/<c>SyncFixedRangeChanged</c>
        /// still propagate normally to any Linked Plotter Sync group.
        /// </summary>
        internal void ApplyRangeModeToLayers(ValueRangeMode mode)
        {
            // All and Current are multi-frame-only concepts.
            // Downgrade to Current (displayed as "Auto") on single-frame targets.
            bool isMultiFrame = _currentData is { FrameCount: > 1 };
            if (!isMultiFrame && (mode == ValueRangeMode.All || mode == ValueRangeMode.Current))
                mode = ValueRangeMode.Current;

            // Roi only means anything while an overlay is designated as the range source.
            // ValueRangeBar.SetMode does not check that itself (SetRoiAvailable is the bar's own
            // route in, and it is only ever called from the overlay code), so guard it here rather
            // than let an external caller park the bar in a mode with nothing behind it.
            if (mode == ValueRangeMode.Roi && _valueRangeOverlay == null)
                mode = ValueRangeMode.Current;

            _rangeBar.SetMode(mode);
        }

        /// <summary>
        /// Applies a LUT quantization level across the settings-panel/View layers. Shared between
        /// external control and Linked Sync (<see cref="SyncApplyLutDepth"/>) — see
        /// <see cref="ApplyRangeModeToLayers"/> for the rationale.
        /// </summary>
        /// <remarks>
        /// Drives the level spinner rather than <c>_view</c> directly, because the spinner's
        /// <c>ValueChanged</c> handler owns the rest of the change (orthogonal sync, saved view
        /// settings, the LUT dirty flag, histogram rebuild, Linked Sync broadcast). Note that the
        /// spinner does not clamp a programmatic assignment to its own 2–4096 bounds — the value
        /// reaches <c>_view</c> as given, deliberately, so that this route and a direct
        /// <c>MainView.LutDepth</c> assignment stay equivalent. Falls back to <c>_view</c> only in
        /// the defensive case where the panel has not been built.
        /// </remarks>
        internal void ApplyLutDepthToLayers(int depth)
        {
            if (_levelNud == null) { _view.LutDepth = depth; return; }
            _levelNud.Value = (decimal)depth;
        }

        /// <summary>
        /// Applies LUT inversion across the settings-panel/View layers. Shared between external
        /// control and Linked Sync (<see cref="SyncApplyInverted"/>) — see
        /// <see cref="ApplyLutDepthToLayers"/> for why the widget is driven rather than the view.
        /// </summary>
        internal void ApplyInvertedToLayers(bool inverted)
        {
            if (_invertLutChk == null) { _view.IsInvertedColor = inverted; return; }
            _invertLutChk.IsChecked = inverted;
        }

        /// <summary>
        /// Applies a fixed value range (min/max) across the RangeBar/View layers. Shared between
        /// external control and Linked Sync (<see cref="SyncApplyFixedRange"/>) — see
        /// <see cref="ApplyRangeModeToLayers"/> for the rationale.
        /// </summary>
        internal void ApplyFixedRangeToLayers(double min, double max)
        {
            _rangeBar.SetRange(min, max);
            _view.FixedMin = min;
            _view.FixedMax = max;
            _orthoController.SyncRenderSettings();
        }

        /// <summary>
        /// Applies a sync'd axis-index change from another plotter.
        /// Finds the axis with <paramref name="axisName"/> in the current data;
        /// no-op if no such axis exists. The index is clamped to the target axis's
        /// own count, so sources and targets can have differently-sized axes.
        /// Setting <c>axis.Index</c> cascades through DimensionStructure:
        ///   AxisIndex_Changed → ActiveIndex → ActiveIndexChanged
        ///   → _activeIndexHandler → _view.FrameIndex + ortho/profile refresh
        ///   + AxisTracker slider/indicator update via Axis.IndexChanged.
        /// </summary>
        internal void SyncApplyAxisIndex(string axisName, int index)
        {
            if (_currentData == null) return;
            var axis = _currentData.Axes.FindAxis(axisName);
            if (axis == null) return;
            int clamped = Math.Clamp(index, 0, axis.Count - 1);
            using var _ = _reentrancy.Begin(GuardContext.SyncApply);
            axis.Index = clamped;
        }

        /// <summary>
        /// Applies a scale parameter change (Min/Max/Step) to either XY or a named Axis.
        /// <para/>
        /// <paramref name="targetAxis"/>: "X" or "Y" for XY plane scaling, or any Axis name (e.g., "Time", "Z").
        /// <paramref name="parameter"/>: which parameter changed (Min, Max, or Step).
        /// <paramref name="value"/>: the new value.
        /// <para/>
        /// For Axis targets: no-op if the axis doesn't exist, is index-based, or has Count≤1.
        /// For XY targets: applies to the underlying MatrixData scale.
        /// <para/>
        /// When Step is applied, Max is recalculated as: newMax = Min + Step × (Count - 1).
        /// This matches the existing behavior in MatrixPlotter.InfoTab.cs.
        /// </summary>
        /// <returns><c>true</c> if the scale was actually applied; <c>false</c> if skipped (e.g., axis not found).</returns>
        internal bool SyncApplyScale(string targetAxis, ScaleParameter parameter, double value)
        {
            if (_currentData == null) return false;

            using var _ = _reentrancy.Begin(GuardContext.SyncApply);

            bool applied;
            if (targetAxis == "X" || targetAxis == "Y")
            {
                ApplyXYScale(targetAxis, parameter, value);
                applied = true;
            }
            else
            {
                applied = ApplyAxisScale(targetAxis, parameter, value);
            }

            if (applied)
            {
                // Update display after applying scale changes
                SetScaleDirty(true);
                if (_view.IsFitToView) _view.FitToView(); else _view.InvalidateSurface();
                _orthoController.RefreshCrosshairAndSlices();
            }

            return applied;
        }

        private void ApplyXYScale(string axis, ScaleParameter parameter, double value)
        {
            if (_currentData == null) return;

            double xMin = _currentData.XMin;
            double xMax = _currentData.XMax;
            double yMin = _currentData.YMin;
            double yMax = _currentData.YMax;

            if (axis == "X")
            {
                switch (parameter)
                {
                    case ScaleParameter.Min:
                        xMin = value;
                        break;
                    case ScaleParameter.Max:
                        xMax = value;
                        break;
                    case ScaleParameter.Step:
                        xMax = xMin + value * (_currentData.XCount - 1);
                        break;
                }
            }
            else if (axis == "Y")
            {
                switch (parameter)
                {
                    case ScaleParameter.Min:
                        yMin = value;
                        break;
                    case ScaleParameter.Max:
                        yMax = value;
                        break;
                    case ScaleParameter.Step:
                        yMax = yMin + value * (_currentData.YCount - 1);
                        break;
                }
            }

            _currentData.SetXYScale(xMin, xMax, yMin, yMax);
        }

        private bool ApplyAxisScale(string axisName, ScaleParameter parameter, double value)
        {
            if (_currentData == null) return false;

            var axis = _currentData.Axes.FindAxis(axisName);
            if (axis == null || axis.IsIndexBased || axis.Count <= 1)
                return false;

            switch (parameter)
            {
                case ScaleParameter.Min:
                    axis.Min = value;
                    break;
                case ScaleParameter.Max:
                    axis.Max = value;
                    break;
                case ScaleParameter.Step:
                    axis.Step = value;
                    break;
            }
            TryUpdateTimeAxisAnimationInterval(axis, showNotice: true);
            return true;
        }


    }
}
