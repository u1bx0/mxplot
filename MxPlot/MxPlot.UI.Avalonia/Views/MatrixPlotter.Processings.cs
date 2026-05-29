using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Processing operations ─────────────────────────────────────────────

        private CancellationTokenSource? _cropCts;
        private static CropRoiBounds? _lastCropBounds;

        /// <summary>
        /// Entry point for the interactive crop action.
        /// If a <see cref="CropAction"/> is already active, disposes it (toggle-off).
        /// Otherwise creates a new <see cref="CropAction"/> with a custom Completed handler
        /// that respects the user's output options (new window vs. replace, all frames vs. single).
        /// </summary>
        private void InvokeCropAction()
        {
            // Toggle off: Leader crop active → cancel and notify follower windows
            if (_activeAction is CropAction { Role: CropRole.Leader })
            {
                _activeAction.Dispose();
                _activeAction = null;
                SyncCropCancelled?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Follower mode: user cannot start a new crop manually while controlled by the leader
            if (_activeAction is CropAction { Role: CropRole.Follower })
                return;

            var crop = new CropAction(CropRole.Leader);
            if (_lastCropBounds is { } lb)
                crop.InitialLeaderBounds = lb;
            crop.RoiBoundsChanged += OnCropRoiBoundsChanged;
            crop.Completed += OnCropCompleted;
            crop.Cancelled += OnCropCancelled;
            InvokeAction(crop);

            // Notify synced windows with the initial ROI bounds (ROI is set up synchronously in Invoke)
            if (crop.CurrentBounds is { } b)
                SyncCropStarted?.Invoke(this, b);
        }

        private async void OnCropCompleted(object? sender, IMatrixData? _)
        {
            if (sender is not CropAction crop) return;
            crop.RoiBoundsChanged -= OnCropRoiBoundsChanged;
            crop.Completed -= OnCropCompleted;
            crop.Cancelled -= OnCropCancelled;

            var p = crop.Parameters;
            if (p == null) return;

            // Notify synced windows before executing locally
            if (crop.FinalBounds is { } b)
                SyncCropCompleted?.Invoke(this, b with
                {
                    ReplaceData = p.ReplaceData,
                    ThisFrameOnly = p.ThisFrameOnly,
                    LeaderFrameIndex = p.ThisFrameOnly ? p.FrameIndex : 0,
                });

            await ExecuteCropAsync(p);
        }

        private void OnCropCancelled(object? sender, EventArgs e)
        {
            if (sender is not CropAction crop) return;
            crop.RoiBoundsChanged -= OnCropRoiBoundsChanged;
            crop.Completed -= OnCropCompleted;
            crop.Cancelled -= OnCropCancelled;
            SyncCropCancelled?.Invoke(this, EventArgs.Empty);
        }

        private void OnCropRoiBoundsChanged(object? sender, CropRoiBounds bounds)
        {
            SyncCropRoiChanged?.Invoke(this, bounds);
        }

        /// <summary>
        /// Executes the crop operation using the parameters collected by <see cref="CropAction"/>.
        /// Multi-frame crops run on a background thread with a progress overlay.
        /// </summary>
        private async Task ExecuteCropAsync(CropAction.CropParameters p)
        {
            if (_currentData == null) return;

            bool isMultiFrame = _currentData.FrameCount > 1;

            // Resolve the frame index for ThisFrameOnly crops.
            // FrameIndex == -1 means "use local ActiveIndex" (leader, or no hint).
            // Otherwise it is the leader's frame index (follower path): clamp to local frame count
            // and fall back to ActiveIndex when out of range.
            int ResolveFrameIndex()
            {
                if (p.FrameIndex < 0)
                    return _currentData.ActiveIndex;
                if (p.FrameIndex < _currentData.FrameCount)
                    return p.FrameIndex;
                // Leader's frame index is out of range for this follower — use own active index.
                return _currentData.ActiveIndex;
            }

            // ── Substack / Volume (3D Crop) ───────────────────────────────────
            if (p.Mode == CropMode.Substack || p.Mode == CropMode.Volume)
            {
                string axisName = p.ZAxisName ?? string.Empty;
                if (string.IsNullOrEmpty(axisName))
                {
                    await ShowMessageDialogAsync("Crop Failed", "Depth axis name is not set.");
                    return;
                }
                int zStart = p.ZStart;
                int zCount = Math.Max(1, p.ZCount);

                // Clamp Z range to the actual data depth.
                var depthAxis = _currentData.Dimensions[axisName];
                if (depthAxis != null)
                {
                    zCount = Math.Min(zCount, depthAxis.Count - zStart);
                    zStart = Math.Clamp(zStart, 0, depthAxis.Count - 1);
                    zCount = Math.Max(1, Math.Min(zCount, depthAxis.Count - zStart));
                }

                string progressLabel = p.Mode == CropMode.Substack ? "Substack…" : "3D Crop…";
                var progress = BeginProgress(progressLabel, blockInput: true);
                _cropCts?.Dispose();
                _cropCts = new CancellationTokenSource();
                var ct = _cropCts.Token;
                int sourceActiveIndex = _currentData.ActiveIndex;
                try
                {
                    IMatrixData result;
                    if (p.Mode == CropMode.Substack)
                    {
                        result = await Task.Run(() =>
                            _currentData.Apply(new SubstackOperation(axisName, zStart, zCount)), ct);
                    }
                    else // Volume
                    {
                        result = await Task.Run(() =>
                            _currentData.Apply(new VolumeCropOperation(
                                axisName, zStart, zCount, p.X, p.Y, p.Width, p.Height, progress, ct)), ct);
                    }
                    // Preserve active frame index where still valid.
                    result.ActiveIndex = Math.Min(sourceActiveIndex, result.FrameCount - 1);
                    ApplyCropResult(result, p);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    await ShowMessageDialogAsync("Crop Failed", ex.Message);
                }
                finally
                {
                    _cropCts?.Dispose();
                    _cropCts = null;
                    EndProgress();
                }
                return;
            }

            // ── XY Crop ───────────────────────────────────────────────────────
            if (isMultiFrame)
            {
                var progress = BeginProgress("Cropping…", blockInput: true);
                _cropCts?.Dispose();
                _cropCts = new CancellationTokenSource();
                var ct = _cropCts.Token;
                int sourceActiveIndex = _currentData.ActiveIndex;
                try
                {
                    IMatrixData result;
                    if (p.ThisFrameOnly)
                    {
                        int frameIdx = ResolveFrameIndex();
                        p = p with { FrameIndex = frameIdx };
                        result = await Task.Run(() =>
                        {
                            var single = _currentData.Apply(new SliceAtOperation(frameIdx));
                            return single.Apply(new CropOperation(p.X, p.Y, p.Width, p.Height, progress, ct));
                        }, ct);
                    }
                    else
                    {
                        result = await Task.Run(() =>
                            _currentData.Apply(new CropOperation(p.X, p.Y, p.Width, p.Height, progress, ct)), ct);
                        // Preserve the active frame index in the result for all-frames crop.
                        int clampedIdx = Math.Min(sourceActiveIndex, result.FrameCount - 1);
                        result.ActiveIndex = clampedIdx;
                    }

                    ApplyCropResult(result, p);
                }
                catch (OperationCanceledException)
                {
                    // User cancelled — nothing to do
                }
                catch (Exception ex)
                {
                    await ShowMessageDialogAsync("Crop Failed", ex.Message);
                }
                finally
                {
                    _cropCts?.Dispose();
                    _cropCts = null;
                    EndProgress();
                }
            }
            else
            {
                // Single frame — synchronous, no progress needed.
                // ThisFrameOnly is a no-op for single-frame data; just crop it.
                try
                {
                    var result = _currentData.Apply(new CropOperation(p.X, p.Y, p.Width, p.Height));
                    ApplyCropResult(result, p);
                }
                catch (Exception ex)
                {
                    await ShowMessageDialogAsync("Crop Failed", ex.Message);
                }
            }
        }

        private void ApplyCropResult(IMatrixData result, CropAction.CropParameters p)
        {
            // When replacing data the dimensions change, so the saved bounds would be invalid.
            if (!p.ReplaceData)
                _lastCropBounds = new CropRoiBounds(p.X, p.Y, p.Width, p.Height);
            string frameNote = p.ThisFrameOnly ? $" (frame {p.FrameIndex})" : "";
            string zNote = p.Mode != CropMode.XY && p.ZCount > 0
                ? $" Z={p.ZAxisName}[{p.ZStart}+{p.ZCount}]" : "";
            string opLabel = p.Mode switch
            {
                CropMode.Substack => "Substack",
                CropMode.Volume => "3D Crop",
                _ => "Crop",
            };
            AppendHistory(result, opLabel, Title,
                $"X={p.X} Y={p.Y} W={p.Width} H={p.Height}{zNote}{frameNote}");

            // Overlays are not copied to the crop result. Remove overlay metadata so that
            // RestoreViewSettings does not re-apply stale pre-crop coordinates via
            // LoadOverlays(clearExisting:true), which would corrupt live overlays in Replace
            // mode or restore wrong-coordinate overlays in New Window mode.
            result.Metadata.Remove(KeyOverlays);

            if (!p.ReplaceData)
            {
                // New Window: overlays are not carried over, so ROI value-range mode would
                // have no overlay to reference. Convert it to Fixed using the current
                // displayed min/max so the new window opens with the same visual range.
                if (_rangeBar.Mode == ValueRangeMode.Roi)
                {
                    result.Metadata[KeyVrMode] = "Fixed";
                    result.Metadata[KeyVrMin] = _view.FixedMin.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                    result.Metadata[KeyVrMax] = _view.FixedMax.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            // Replace mode: live overlays remain in _view.OverlayManager (not cleared by
            // SetMatrixData), and the ROI overlay is still present, so ROI mode is preserved.

            if (p.ReplaceData)
            {
                _cropUndoData = _currentData;
                _cropUndoDirty = _dirty;
                _cropUndoTitle = Title;
                _cropUndoLutVrSnapshot = _lutVrSnapshot;
                _cropUndoScaleSnapshot = _scaleSnapshot;
                var newTitle = $"Crop of {Title}";
                SetMatrixData(result);
                Title = newTitle;
                SetDirty(DirtyFlags.Data, true);
            }
            else
            {
                MatrixPlotter.Create(result, _view.Lut, $"Crop of {Title}").Show();
            }
        }

        internal void RevertCrop()
        {
            if (_cropUndoData == null) return;
            var undo = _cropUndoData;
            var undoDirty = _cropUndoDirty;
            var undoTitle = _cropUndoTitle;
            var undoLutVr = _cropUndoLutVrSnapshot;
            var undoScale = _cropUndoScaleSnapshot;
            _cropUndoData = null;
            _cropUndoLutVrSnapshot = null;
            _cropUndoScaleSnapshot = null;
            SetMatrixData(undo);
            // Restore the snapshots and dirty flags from before the crop was applied.
            // SetMatrixData has already replaced them with crop-data snapshots; overwrite here.
            _lutVrSnapshot = undoLutVr;
            _scaleSnapshot = undoScale;
            _dirty = undoDirty;
            if (_dirty != DirtyFlags.None) IsModifiedChanged?.Invoke(this, EventArgs.Empty);
            if ((_dirty & (DirtyFlags.Lut | DirtyFlags.Vr)) != 0 && _lutVrRevertBtn != null) _lutVrRevertBtn.IsVisible = _lutVrSnapshot != null;
            UpdateScaleRevertButton();
            Title = undoTitle ?? Title;
            SyncCropReverted?.Invoke(this, EventArgs.Empty);
        }

        // ── Sync Crop apply methods (called by MatrixPlotterSyncGroup) ────────

        /// <summary>
        /// Starts a follower crop action on this plotter using the leader window's initial bounds.
        /// The ROI is move-only and colored with the sync accent color.
        /// </summary>
        internal void SyncApplyCropStart(CropRoiBounds bounds)
        {
            var crop = new CropAction(CropRole.Follower) { InitialBounds = bounds };
            crop.Completed += OnSyncedCropCompleted;
            InvokeAction(crop);
        }

        /// <summary>
        /// Updates the follower ROI to follow the leader window's new bounds while preserving
        /// this window's pixel offset.
        /// </summary>
        internal void SyncApplyCropRoiChanged(CropRoiBounds bounds)
        {
            if (_activeAction is CropAction { Role: CropRole.Follower } crop)
                crop.SyncUpdateLeaderBounds(bounds);
        }

        /// <summary>
        /// Executes the crop on this follower window using its current ROI position.
        /// Skipped (no-op) if the ROI has zero width or height after clamping.
        /// </summary>
        internal void SyncApplyCropExecute(CropRoiBounds finalLeaderBounds)
        {
            if (_activeAction is CropAction { Role: CropRole.Follower } crop)
            {
                crop.ReplaceData = finalLeaderBounds.ReplaceData;
                crop.ThisFrameOnly = finalLeaderBounds.ThisFrameOnly;
                crop.LeaderFrameIndex = finalLeaderBounds.LeaderFrameIndex;
                crop.SyncUpdateLeaderBounds(finalLeaderBounds);
                crop.ForceApply();
            }
        }

        /// <summary>Cancels the follower crop action on this window.</summary>
        internal void SyncApplyCropCancel()
        {
            if (_activeAction is CropAction { Role: CropRole.Follower } crop)
                crop.ForceCancel();
        }

        /// <summary>
        /// Cancels any active <see cref="CropAction"/> regardless of role.
        /// Used when the sync group is dissolved (Unsync or member window closed) while a
        /// Sync Crop is in progress, to avoid orphaned Leader or Follower ROI panels.
        /// </summary>
        internal void CancelActiveCropAction()
        {
            if (_activeAction is CropAction crop)
                crop.ForceCancel();
        }

        /// <summary><c>true</c> when a <see cref="CropAction"/> (any role) is currently active.</summary>
        internal bool HasActiveCropAction => _activeAction is CropAction;

        private async void OnSyncedCropCompleted(object? sender, IMatrixData? _)
        {
            if (sender is not CropAction crop) return;
            crop.Completed -= OnSyncedCropCompleted;
            _activeAction = null;

            var p = crop.Parameters;
            if (p == null) return; // zero-dimension after clamping — skip crop for this window

            await ExecuteCropAsync(p);
        }

        // ── Extract Frame ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds a human-readable frame label for the title of an extracted frame window.
        /// For Hyperstack data, formats as "[A=1, B=3]"; for flat multi-frame, "[i=8]".
        /// </summary>
        private static string BuildFrameLabel(IMatrixData data, int frameIndex)
        {
            var axes = data.Axes;
            if (axes.Count == 0)
                return $"[i={frameIndex}]";

            var coords = data.Dimensions.GetAxisIndices(frameIndex);
            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < axes.Count; i++)
            {
                if (i > 0) parts.Append(", ");
                parts.Append($"{axes[i].Name}={coords[i] + 1}");
            }
            return $"[{parts}]";
        }

        /// <summary>
        /// Builds the label for an orthogonal (XZ or YZ) extracted frame window.
        /// Format: "[{horizAxis}-{vertAxis}, {fixedAxisName}={fixedIndex+1}]"
        /// When the source data has additional hyperstack axes, they are appended as "Name=val".
        /// Example: "[X-Time, Y=5, Channel=2]"
        /// </summary>
        private static string BuildOrthoLabel(string horizAxis, string vertAxis, string fixedAxisName, int fixedIndex, IMatrixData? sourceData)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"{horizAxis}-{vertAxis}, {fixedAxisName}i={fixedIndex}");

            // Append other hyperstack axis indices (axes that are neither the depth axis (vertAxis/horizAxis) nor the fixed axis)
            if (sourceData != null)
            {
                foreach (var axis in sourceData.Axes)
                {
                    if (axis.Name == fixedAxisName || axis.Name == horizAxis || axis.Name == vertAxis)
                        continue;
                    sb.Append($", {axis.Name}={axis.Index + 1}");
                }
            }
            return $"[{sb}]";
        }

        /// <summary>
        /// copy and opens it in a new <see cref="MatrixPlotter"/> window.
        /// For the main view, <see cref="MxView.FrameIndex"/> is used to slice the current frame.
        /// For orthogonal views (XZ / YZ), the already-sliced data is used directly.
        /// </summary>
        private void InvokeExtractFrame(MxView sourceView)
        {
            var data = sourceView.MatrixData;
            if (data == null) return;

            IMatrixData result;
            string frameLabel;

            if (sourceView == _orthoPanel.BottomView)
            {
                // XZ view: slice is fixed at current Y (iy), scans X vs depth axis
                result = data;
                int iy = _orthoController.CurrentIY;
                string axisName = _orthoController.ActiveAxisName ?? "Z";
                frameLabel = BuildOrthoLabel("X", axisName, "Y", iy, _currentData);
            }
            else if (sourceView == _orthoPanel.RightView)
            {
                // YZ view: slice is fixed at current X (ix), scans depth axis vs Y
                result = data;
                int ix = _orthoController.CurrentIX;
                string axisName = _orthoController.ActiveAxisName ?? "Z";
                frameLabel = BuildOrthoLabel("Y", axisName, "X", ix, _currentData);
            }
            else
            {
                // Main view: slice the current frame
                int frameIndex = sourceView.FrameIndex;
                result = data.Apply(new SliceAtOperation(frameIndex, DeepCopy: false));
                frameLabel = BuildFrameLabel(data, frameIndex);
            }

            var child = CreateLinked(result, _view.Lut, $"{Title} {frameLabel}");
            child.Show();
        }

        // ── Reverse Stack ─────────────────────────────────────────────────────

        private async Task InvokeReverseStackAsync()
        {
            if (_currentData == null) return;
            HideMenuPanel();

            var axes = _currentData.Axes;
            var p = await ReverseStackDialog.ShowAsync(this, axes);
            if (p == null) return;

            if (!await ConfirmLargeVirtualOperationAsync(_currentData, "Reverse Stack")) return;

            IMatrixData result;
            try
            {
                result = _currentData.Apply(new ReverseStackOperation(p.AxisName));
            }
            catch (OutOfMemoryException)
            {
                await ShowMessageDialogAsync("Out of Memory",
                    "Not enough memory to process this virtual dataset.\nOperation cancelled.");
                return;
            }
            catch (Exception ex)
            {
                await ShowMessageDialogAsync("Reverse Stack Failed", ex.Message);
                return;
            }

            string detail = p.AxisName == null ? "all frames" : $"axis: {p.AxisName}";
            AppendHistory(result, "Reverse Stack", Title, detail);

            if (p.ReplaceData)
            {
                var newTitle = Title;
                SetMatrixData(result);
                Title = newTitle;
                SetDirty(DirtyFlags.Data, true);
            }
            else
            {
                MatrixPlotter.Create(result, _view.Lut, $"Reversed {Title}").Show();
            }
        }

        // ── Virtual OOM guard ─────────────────────────────────────────────────

        /// <summary>
        /// When <paramref name="data"/> is virtual and its total uncompressed size exceeds
        /// <see cref="VirtualPolicy.ThresholdBytes"/>, shows a warning dialog and returns
        /// <c>false</c> if the user chooses to cancel the operation.
        /// Always returns <c>true</c> for in-memory data.
        /// </summary>
        /// <remarks>
        /// This is a temporary guard until a Virtual→Virtual processing path is implemented.
        /// The current deep-copy fallback for virtual data loads all frames into heap memory,
        /// which risks OOM for large datasets.
        /// </remarks>
        private async Task<bool> ConfirmLargeVirtualOperationAsync(IMatrixData data, string operationName)
        {
            if (!data.IsVirtual) return true;

            long totalBytes = 1L * data.FrameCount * data.XCount * data.YCount * data.ElementSize;
            if (totalBytes <= VirtualPolicy.ThresholdBytes) return true;

            string sizeStr = totalBytes switch
            {
                >= 1L << 30 => $"{totalBytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
                >= 1L << 20 => $"{totalBytes / (1024.0 * 1024.0):F1} MB",
                _ => $"{totalBytes / 1024.0:F1} KB",
            };

            return await ShowConfirmDialogAsync(
                $"{operationName} — Large Virtual Data",
                $"This dataset is virtual (MMF-backed) and its total size is {sizeStr}.\n\n" +
                $"{operationName} will load all frames into memory, which may cause an out-of-memory error.\n\n" +
                $"Continue anyway?");
        }
    }
}
