using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Views
{
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
        Dictionary<string, int> AxisIndices);

    public partial class MatrixPlotter
    {
        // Guard: set during SyncApply* calls to suppress re-firing of sync events.
        private bool _syncApplying;

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
            if (_currentData != null)
            {
                foreach (var axis in _currentData.Axes)
                    axisIndices[axis.Name] = axis.Index;
            }
            return new PlotterSnapshot(
                Lut: _view.Lut,
                LutDepth: _view.LutDepth,
                IsInverted: _view.IsInvertedColor,
                RangeMode: _rangeBar.Mode,
                FixedMin: _view.FixedMin,
                FixedMax: _view.FixedMax,
                AxisIndices: axisIndices);
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
        }

        // ── Internal sync apply methods ──────────────────────────────────────
        // Called by MatrixPlotterSyncGroup to propagate a change from another
        // plotter without re-triggering sync events.

        internal void SyncApplyLut(LookupTable lut)
        {
            _syncApplying = true;
            _lutSelector.SelectLut(lut);
            _syncApplying = false;
        }

        internal void SyncApplyLutDepth(int depth)
        {
            if (_levelNud == null) return;
            _syncApplying = true;
            _levelNud.Value = (decimal)depth;
            _syncApplying = false;
        }

        internal void SyncApplyInverted(bool inverted)
        {
            if (_invertLutChk == null) return;
            _syncApplying = true;
            _invertLutChk.IsChecked = inverted;
            _syncApplying = false;
        }

        internal void SyncApplyRangeMode(ValueRangeMode mode)
        {
            _syncApplying = true;
            _rangeBar.SetMode(mode);
            _syncApplying = false;
        }

        internal void SyncApplyFixedRange(double min, double max)
        {
            _syncApplying = true;
            _rangeBar.SetRange(min, max);
            _view.FixedMin = min;
            _view.FixedMax = max;
            _orthoController.SyncRenderSettings();
            _syncApplying = false;
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
            var axis = _currentData.Axes.FirstOrDefault(a => a.Name == axisName);
            if (axis == null) return;
            int clamped = Math.Clamp(index, 0, axis.Count - 1);
            _syncApplying = true;
            axis.Index = clamped;
            _syncApplying = false;
        }
    }
}
