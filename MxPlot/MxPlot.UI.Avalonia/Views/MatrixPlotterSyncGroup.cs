using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Controls;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Propagates display setting changes — LUT, level (LutDepth), invert, value range mode,
    /// fixed range min/max, and axis index — across a set of <see cref="MatrixPlotter"/>
    /// windows while sync mode is active.
    /// <para/>
    /// Axis index sync is name-based: changing axis "Time" on one plotter propagates only to
    /// plotters that also have a "Time" axis; targets with a smaller count are clamped.
    /// <para/>
    /// On construction, a <see cref="PlotterSnapshot"/> is captured for every member.
    /// The first <see cref="Propagate"/> call sets <see cref="IsDirty"/> to <c>true</c> and
    /// raises <see cref="DirtyChanged"/>. Call <see cref="Revert"/> to restore all members
    /// to their initial state.
    /// <para/>
    /// Create an instance with the target plotters and call <see cref="Dispose"/> to detach.
    /// </summary>
    public sealed class MatrixPlotterSyncGroup : IDisposable
    {
        private readonly List<MatrixPlotter> _plotters;
        private readonly Dictionary<MatrixPlotter, PlotterSnapshot> _snapshots;
        private bool _propagating;
        private bool _disposed;
        private bool _isDirty;

        /// <summary><c>true</c> after the first propagation; <c>false</c> initially and after <see cref="Revert"/>.</summary>
        public bool IsDirty => _isDirty;

        /// <summary>Raised when <see cref="IsDirty"/> transitions between <c>true</c> and <c>false</c>.</summary>
        public event EventHandler<bool>? DirtyChanged;

        /// <param name="plotters">
        /// The plotters to keep in sync. Must contain at least two items to be meaningful.
        /// </param>
        public MatrixPlotterSyncGroup(IEnumerable<MatrixPlotter> plotters)
        {
            _plotters = [.. plotters];
            _snapshots = new(_plotters.Count);
            foreach (var p in _plotters)
            {
                _snapshots[p] = p.CaptureSnapshot();
                Subscribe(p);
            }
        }

        // ── Subscribe / Unsubscribe ──────────────────────────────────────────

        private void Subscribe(MatrixPlotter p)
        {
            p.SyncLutChanged        += OnLutChanged;
            p.SyncLutDepthChanged   += OnLutDepthChanged;
            p.SyncInvertedChanged   += OnInvertedChanged;
            p.SyncRangeModeChanged  += OnRangeModeChanged;
            p.SyncFixedRangeChanged += OnFixedRangeChanged;
            p.SyncAxisIndexChanged  += OnAxisIndexChanged;
            p.SyncCropStarted       += OnCropStarted;
            p.SyncCropRoiChanged    += OnCropRoiChanged;
            p.SyncCropCompleted     += OnCropCompleted;
            p.SyncCropCancelled     += OnCropCancelled;
            p.SyncCropReverted      += OnCropReverted;
            p.Closed                += OnPlotterClosed;
        }

        private void Unsubscribe(MatrixPlotter p)
        {
            p.SyncLutChanged        -= OnLutChanged;
            p.SyncLutDepthChanged   -= OnLutDepthChanged;
            p.SyncInvertedChanged   -= OnInvertedChanged;
            p.SyncRangeModeChanged  -= OnRangeModeChanged;
            p.SyncFixedRangeChanged -= OnFixedRangeChanged;
            p.SyncAxisIndexChanged  -= OnAxisIndexChanged;
            p.SyncCropStarted       -= OnCropStarted;
            p.SyncCropRoiChanged    -= OnCropRoiChanged;
            p.SyncCropCompleted     -= OnCropCompleted;
            p.SyncCropCancelled     -= OnCropCancelled;
            p.SyncCropReverted      -= OnCropReverted;
            p.Closed                -= OnPlotterClosed;
        }

        // ── Propagation helper ───────────────────────────────────────────────

        private void Propagate(MatrixPlotter source, Action<MatrixPlotter> apply)
        {
            if (_propagating) return;
            _propagating = true;
            try
            {
                foreach (var p in _plotters)
                {
                    if (ReferenceEquals(p, source)) continue;
                    apply(p);
                }
                SetDirty(true);
            }
            finally { _propagating = false; }
        }

        private void SetDirty(bool value)
        {
            if (_isDirty == value) return;
            _isDirty = value;
            DirtyChanged?.Invoke(this, value);
        }

        // ── Revert ───────────────────────────────────────────────────────────

        /// <summary>
        /// Restores all sync members to the display settings captured when this
        /// group was created. Resets <see cref="IsDirty"/> to <c>false</c>.
        /// </summary>
        public void Revert()
        {
            _propagating = true;
            try
            {
                foreach (var p in _plotters)
                {
                    p.RevertCrop();
                    if (_snapshots.TryGetValue(p, out var snap))
                        p.RestoreSnapshot(snap);
                }
                SetDirty(false);
            }
            finally { _propagating = false; }
        }

        // ── Event handlers ───────────────────────────────────────────────────

        private void OnLutChanged(object? sender, LookupTable lut) =>
            Propagate((MatrixPlotter)sender!, p => p.SyncApplyLut(lut));

        private void OnLutDepthChanged(object? sender, int depth) =>
            Propagate((MatrixPlotter)sender!, p => p.SyncApplyLutDepth(depth));

        private void OnInvertedChanged(object? sender, bool inverted) =>
            Propagate((MatrixPlotter)sender!, p => p.SyncApplyInverted(inverted));

        private void OnRangeModeChanged(object? sender, ValueRangeMode mode) =>
            Propagate((MatrixPlotter)sender!, p => p.SyncApplyRangeMode(mode));

        private void OnFixedRangeChanged(object? sender, (double Min, double Max) range) =>
            Propagate((MatrixPlotter)sender!, p => p.SyncApplyFixedRange(range.Min, range.Max));

        private void OnAxisIndexChanged(object? sender, (string AxisName, int Index) args) =>
            Propagate((MatrixPlotter)sender!, p => p.SyncApplyAxisIndex(args.AxisName, args.Index));

        // Crop sync uses a separate helper that does NOT call SetDirty for ROI-only changes.
        // Replace-data crops DO call SetDirty(true) so the App Revert button becomes active,
        // and Revert() calls RevertCrop() on every member to undo the data replacement.
        private void PropagateCrop(MatrixPlotter source, Action<MatrixPlotter> apply)
        {
            if (_propagating) return;
            _propagating = true;
            try
            {
                foreach (var p in _plotters)
                {
                    if (!ReferenceEquals(p, source)) apply(p);
                }
            }
            finally { _propagating = false; }
        }

        private void OnCropStarted(object? sender, CropRoiBounds bounds) =>
            PropagateCrop((MatrixPlotter)sender!, p => p.SyncApplyCropStart(bounds));

        private void OnCropRoiChanged(object? sender, CropRoiBounds bounds) =>
            PropagateCrop((MatrixPlotter)sender!, p => p.SyncApplyCropRoiChanged(bounds));

        private void OnCropCompleted(object? sender, CropRoiBounds bounds)
        {
            PropagateCrop((MatrixPlotter)sender!, p => p.SyncApplyCropExecute(bounds));
            if (bounds.ReplaceData) SetDirty(true);
        }

        private void OnCropCancelled(object? sender, EventArgs e) =>
            PropagateCrop((MatrixPlotter)sender!, p => p.SyncApplyCropCancel());

        private void OnCropReverted(object? sender, EventArgs e)
        {
            if (_propagating) return;
            _propagating = true;
            try
            {
                foreach (var p in _plotters)
                    if (!ReferenceEquals(p, (MatrixPlotter)sender!))
                        p.RevertCrop();
            }
            finally { _propagating = false; }
        }

        private void OnPlotterClosed(object? sender, EventArgs e)
        {
            if (sender is not MatrixPlotter closed) return;

            // If the closing plotter had an active Sync Crop, cancel all remaining members
            // to avoid orphaned Leader/Follower ROI panels in the surviving windows.
            bool closedHadCrop = closed.HasActiveCropAction;
            Unsubscribe(closed);
            _snapshots.Remove(closed);
            _plotters.Remove(closed);

            if (closedHadCrop)
            {
                foreach (var p in _plotters)
                    p.CancelActiveCropAction();
            }
        }

        // ── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Cancel any active Sync Crop on all members before disconnecting events,
            // so neither Leader nor Follower panels are left orphaned after Unsync.
            foreach (var p in _plotters)
                p.CancelActiveCropAction();
            foreach (var p in _plotters)
                Unsubscribe(p);
            _plotters.Clear();
            _snapshots.Clear();
        }
    }
}
