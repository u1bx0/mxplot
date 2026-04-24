using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Actions;
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
                SyncCropCompleted?.Invoke(this, b with { ReplaceData = p.ReplaceData });

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

            if (isMultiFrame)
            {
                var progress = BeginProgress("Cropping…", blockInput: true);
                _cropCts?.Dispose();
                _cropCts = new CancellationTokenSource();
                var ct = _cropCts.Token;
                try
                {
                    IMatrixData result;
                    if (p.ThisFrameOnly)
                    {
                        int frameIdx = _currentData.ActiveIndex;
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
                // Single frame — synchronous, no progress needed
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
            AppendHistory(result, "Crop", Title,
                $"X={p.X} Y={p.Y} W={p.Width} H={p.Height}{frameNote}");

            if (p.ReplaceData)
            {
                _cropUndoData = _currentData;
                _cropUndoModified = _isModified;
                _cropUndoTitle = Title;
                var newTitle = $"Crop of {Title}";
                SetMatrixData(result);
                Title = newTitle;
                SetModified(true);
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
            var undoModified = _cropUndoModified;
            var undoTitle = _cropUndoTitle;
            _cropUndoData = null;
            SetMatrixData(undo);
            Title = undoTitle ?? Title;
            SetModified(undoModified);
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

        private async void OnSyncedCropCompleted(object? sender, IMatrixData? _)
        {
            if (sender is not CropAction crop) return;
            crop.Completed -= OnSyncedCropCompleted;
            _activeAction = null;

            var p = crop.Parameters;
            if (p == null) return; // zero-dimension after clamping — skip crop for this window

            await ExecuteCropAsync(p);
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
                SetModified(true);
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
