using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Tools;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Overlays;
using System;
using System.Linq;
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
        /// If a <see cref="CropTool"/> is already active, disposes it (toggle-off).
        /// Otherwise creates a new <see cref="CropTool"/> with a custom Completed handler
        /// that respects the user's output options (new window vs. replace, all frames vs. single).
        /// </summary>
        private void InvokeCropTool()
        {
            // Toggle off: Leader crop active → cancel and notify follower windows
            if (_activeTool is CropTool { Role: CropRole.Leader })
            {
                _activeTool.Dispose();
                _activeTool = null;
                SyncCropCancelled?.Invoke(this, EventArgs.Empty);
                return;
            }

            // Follower mode: user cannot start a new crop manually while controlled by the leader
            if (_activeTool is CropTool { Role: CropRole.Follower })
                return;

            var crop = new CropTool(CropRole.Leader) { IsReplaceDataBlocked = IsReplaceDataBlocked };
            if (_lastCropBounds is { } lb)
                crop.InitialLeaderBounds = lb;
            crop.RoiBoundsChanged += OnCropRoiBoundsChanged;
            crop.Completed += OnCropCompleted;
            crop.Cancelled += OnCropCancelled;
            InvokeTool(crop);

            // Notify synced windows with the initial ROI bounds (ROI is set up synchronously in Invoke)
            if (crop.CurrentBounds is { } b)
                SyncCropStarted?.Invoke(this, b);
        }

        private async void OnCropCompleted(object? sender, IMatrixData? _)
        {
            if (sender is not CropTool crop) return;
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
            if (sender is not CropTool crop) return;
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
        /// Executes the crop operation using the parameters collected by <see cref="CropTool"/>.
        /// Multi-frame crops run on a background thread with a progress overlay.
        /// </summary>
        private async Task ExecuteCropAsync(CropTool.CropParameters p)
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

                // Axis.Slice() (used by Substack to shrink this axis) has no way to sub-range a
                // FovAxis's tile layout - an arbitrary contiguous index range is not generally a
                // valid tile rectangle - so it deliberately degrades to a plain Axis. Warn before
                // that happens, mirroring RenameAxisAsync's confirmation for the same kind of
                // specialized-axis downgrade.
                if (depthAxis is FovAxis)
                {
                    string opName = p.Mode == CropMode.Substack ? "Substack" : "3D Crop";
                    bool ok = await ShowConfirmDialogAsync(
                        opName,
                        $"The “{axisName}” axis has a specialized type (FOV). {opName} will convert it "
                        + "to a plain axis, discarding its tile layout.\n\nContinue?");
                    if (!ok) return;
                }

                string progressLabel = p.Mode == CropMode.Substack ? "Substack…" : "3D Crop…";
                _cropCts?.Dispose();
                _cropCts = new CancellationTokenSource();
                var ct = _cropCts.Token;
                var progress = BeginProgress(progressLabel, blockInput: true, _cropCts);
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
                _cropCts?.Dispose();
                _cropCts = new CancellationTokenSource();
                var ct = _cropCts.Token;
                var progress = BeginProgress("Cropping…", blockInput: true, _cropCts);
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

        private void ApplyCropResult(IMatrixData result, CropTool.CropParameters p)
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
                _cropUndoRenderSnapshot = _renderSnapshot;
                _cropUndoScaleSnapshot = _scaleSnapshot;
                var newTitle = $"Crop of {Title}";
                // Replace-data crop discards the old instance, so live followers must be closed
                // first - see CloseSyncFollowers.
                SetMatrixData(result, closeDerivedWindows: true);
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
            var undoLutVr = _cropUndoRenderSnapshot;
            var undoScale = _cropUndoScaleSnapshot;
            _cropUndoData = null;
            _cropUndoRenderSnapshot = null;
            _cropUndoScaleSnapshot = null;
            SetMatrixData(undo);
            // Restore the snapshots and dirty flags from before the crop was applied.
            // SetMatrixData has already replaced them with crop-data snapshots; overwrite here.
            _renderSnapshot = undoLutVr;
            _scaleSnapshot = undoScale;
            _dirty = undoDirty;
            if (_dirty != DirtyFlags.None) IsModifiedChanged?.Invoke(this, EventArgs.Empty);
            UpdateRenderRevertButtons();
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
            var crop = new CropTool(CropRole.Follower) { InitialBounds = bounds };
            crop.Completed += OnSyncedCropCompleted;
            InvokeTool(crop);
        }

        /// <summary>
        /// Updates the follower ROI to follow the leader window's new bounds while preserving
        /// this window's pixel offset.
        /// </summary>
        internal void SyncApplyCropRoiChanged(CropRoiBounds bounds)
        {
            if (_activeTool is CropTool { Role: CropRole.Follower } crop)
                crop.SyncUpdateLeaderBounds(bounds);
        }

        /// <summary>
        /// Executes the crop on this follower window using its current ROI position.
        /// Skipped (no-op) if the ROI has zero width or height after clamping.
        /// </summary>
        internal void SyncApplyCropExecute(CropRoiBounds finalLeaderBounds)
        {
            if (_activeTool is CropTool { Role: CropRole.Follower } crop)
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
            if (_activeTool is CropTool { Role: CropRole.Follower } crop)
                crop.ForceCancel();
        }

        /// <summary>
        /// Cancels any active <see cref="CropTool"/> regardless of role.
        /// Used when the sync group is dissolved (Unsync or member window closed) while a
        /// Sync Crop is in progress, to avoid orphaned Leader or Follower ROI panels.
        /// </summary>
        internal void CancelActiveCropAction()
        {
            if (_activeTool is CropTool crop)
                crop.ForceCancel();
        }

        /// <summary><c>true</c> when a <see cref="CropTool"/> (any role) is currently active.</summary>
        internal bool HasActiveCropTool => _activeTool is CropTool;

        private async void OnSyncedCropCompleted(object? sender, IMatrixData? _)
        {
            if (sender is not CropTool crop) return;
            crop.Completed -= OnSyncedCropCompleted;
            _activeTool = null;

            var p = crop.Parameters;
            if (p == null) return; // zero-dimension after clamping — skip crop for this window

            await ExecuteCropAsync(p);
        }

        // ── Extract Frame ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds a human-readable frame label for the title of an extracted frame window.
        /// For Hyperstack data, formats as "[A=0, B=2]"; for flat multi-frame, "[i:7]".
        /// </summary>
        /// <summary>
        /// Formats an axis value at the given index as a short string suitable for window titles.
        /// Index-based axes use "i:N" (0-based, matching <see cref="AxisTracker.BuildPositionText"/>'s
        /// convention and every 0-based index elsewhere in the codebase). Scaled axes use the value
        /// formatted to 3 decimal places with unit.
        /// Example: "Z=12.500 um", "Time=0.100 s", "Channel=i:2"
        /// </summary>
        /// <param name="verbose">
        /// When <c>true</c>, appends the 0-based index suffix for scaled axes: "12.500 um (idx:22)".
        /// When <c>false</c>, returns the value only: "12.500 um". Index-based axes always use "i:N".
        /// </param>
        internal static string FormatAxisValue(Axis axis, int index, bool verbose = false)
        {
            if (axis.IsIndexBased)
                return $"i:{index}";
            double val = axis.ValueAt(index);
            string unit = string.IsNullOrEmpty(axis.Unit) ? "" : $" {axis.Unit}";
            return verbose ? $"{val:F3}{unit} (idx:{index})" : $"{val:F3}{unit}";
        }

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
                parts.Append($"{axes[i].Name}={FormatAxisValue(axes[i], coords[i])}");
            }
            return $"[{parts}]";
        }

        private static string BuildFrameHistoryDetail(IMatrixData data, int frameIndex)
        {
            var axes = data.Axes;
            if (axes.Count == 0)
                return $"i={frameIndex}";

            var coords = data.Dimensions.GetAxisIndices(frameIndex);
            return string.Join(", ", Enumerable.Range(0, axes.Count)
                .Select(i => $"{axes[i].Name}={FormatAxisValue(axes[i], coords[i], verbose: true)}"));
        }

        /// <summary>
        /// Builds the label for an orthogonal (XZ or YZ) extracted frame window.
        /// Format: "[{horizAxis}-{vertAxis}, {fixedAxisName}={fixedIndex+1}]"
        /// When the source data has additional hyperstack axes, they are appended as "Name=val".
        /// Example: "[X-Time, Y=5, Channel=2]"
        /// </summary>
        /// <param name="horizAxis">Horizontal spatial axis name ("X" or "Y").</param>
        /// <param name="vertAxis">Vertical/depth hyperstack axis name shown in the plane (e.g. "Z", "T").</param>
        /// <param name="fixedAxisName">Fixed spatial axis name ("Y" for XZ, "X" for YZ).</param>
        /// <param name="fixedPixelIndex">0-based pixel index of the fixed spatial axis.</param>
        /// <param name="sourceData">Source data for resolving additional hyperstack axis values.</param>
        /// <param name="skipAxisName">
        /// Extra axis to omit — used when Composite mode extracts the whole Channel axis instead
        /// of fixing it, so listing its (no-longer-fixed) index would be misleading.
        /// </param>
        private static string BuildOrthoLabel(string horizAxis, string vertAxis, string fixedAxisName, int fixedPixelIndex, IMatrixData? sourceData, string? skipAxisName = null)
        {
            var sb = new System.Text.StringBuilder();
            // iy=/ix= is a 0-based pixel coordinate, grouped with the plane name as an attribute
            string pixelTag = fixedAxisName.ToLowerInvariant();
            sb.Append($"{horizAxis}-{vertAxis}(i{pixelTag}={fixedPixelIndex})");

            // Append additional hyperstack axes (depth axis already encoded in plane name)
            if (sourceData != null)
            {
                foreach (var axis in sourceData.Axes)
                {
                    if (string.Equals(axis.Name, vertAxis, StringComparison.OrdinalIgnoreCase) || string.Equals(axis.Name, skipAxisName, StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append($", {axis.Name}={FormatAxisValue(axis, axis.Index)}");
                }
            }
            return $"[{sb}]";
        }

        private static string BuildOrthoHistoryDetail(string planeName, string fixedAxisName, int fixedPixelIndex, IMatrixData? sourceData, string depthAxisName, string? skipAxisName = null)
        {
            var sb = new System.Text.StringBuilder();
            string pixelTag = fixedAxisName.ToLowerInvariant();
            sb.Append($"{planeName} plane; i{pixelTag}={fixedPixelIndex}");
            if (sourceData != null)
            {
                foreach (var axis in sourceData.Axes)
                {
                    if (string.Equals(axis.Name, depthAxisName, StringComparison.OrdinalIgnoreCase) || string.Equals(axis.Name, skipAxisName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    sb.Append($", {axis.Name}={FormatAxisValue(axis, axis.Index, verbose: true)}");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Extracts the current frame or orthogonal slice and opens it in a new
        /// <see cref="MatrixPlotter"/> window.
        /// For the main view, <see cref="MxView.FrameIndex"/> is used to slice the current frame.
        /// For orthogonal views (XZ / YZ), a live fixed-plane extract window is created. Everything
        /// that identifies the frame is captured at extraction - the slice position (iy / ix) and
        /// the position of every axis - so the window keeps showing the plane named in its title
        /// however the parent is navigated afterwards. What it does follow is pixel edits: the
        /// captured volume shares the parent's frame buffers, and each parent refresh re-slices it.
        /// The window closes when the parent replaces its data instance outright, since the volume
        /// it captured belongs to the old one.
        /// </summary>
        private void InvokeExtractFrame(MxView sourceView)
        {
            if (sourceView == _orthoPanel.BottomView)
            {
                // XZ view: fixed Y slice — live extract. In Composite mode, extract every
                // channel (not just whichever one ActiveIndex is pinned to) via the same
                // per-channel-extract-and-merge OrthogonalViewController already uses to render
                // this very side view, so the live extract keeps showing a proper Composite blend.
                var capturedData = _currentData;
                if (capturedData == null) return;
                int capturedIY = _orthoController.CurrentIY;
                string capturedAxis = _orthoController.ActiveAxisName ?? "Z";
                bool useComposite = _isCompositeMode && _compositeAxisDimIndex >= 0;
                int capturedChannelDim = _compositeAxisDimIndex;
                string? channelAxisName = useComposite ? capturedData.Dimensions[capturedChannelDim].Name : null;

                // Freeze every axis, not just the slice position: "Extract" means this frame, and
                // the window title and history entry below record the full coordinate. Capturing
                // the volume rather than re-deriving it per rebuild is what pins the other axes -
                // ExtractAlong with deepCopy:false shares the source's frame buffers by reference,
                // so the extract still follows pixel edits while staying on its own (C, T, ...).
                var capturedIndices = capturedData.Dimensions.GetAxisIndices();
                IMatrixData CaptureVolume(int[] indices) =>
                    capturedData.Apply(new ExtractAlongOperation(capturedAxis, indices, DeepCopy: false));

                IMatrixData[] volumes;
                if (useComposite)
                {
                    int channelCount = capturedData.Dimensions[capturedChannelDim].Count;
                    volumes = new IMatrixData[channelCount];
                    for (int c = 0; c < channelCount; c++)
                    {
                        var bi = (int[])capturedIndices.Clone();
                        bi[capturedChannelDim] = c;
                        volumes[c] = CaptureVolume(bi);
                    }
                }
                else
                {
                    volumes = [CaptureVolume(capturedIndices)];
                }

                // The captured volumes carry a single axis, so the slice needs no axis name.
                // dst is null on the first call (nothing to write into yet) and the window's own
                // data thereafter, so every later slice lands in the frames already on screen.
                IMatrixData BuildXZ(IMatrixData? dst)
                {
                    if (!useComposite)
                        return volumes[0].Apply(new SliceOperation(ViewFrom.Y, capturedIY, Dst: dst));

                    var perChannel = new IMatrixData[volumes.Length];
                    for (int c = 0; c < volumes.Length; c++)
                        perChannel[c] = volumes[c].Apply(new SliceOperation(ViewFrom.Y, capturedIY, Dst: dst, DstIndex: c));
                    // With dst supplied the per-channel slices already share its frames, so the
                    // merge just rebuilds an equivalent wrapper and the caller keeps using dst.
                    return OrthogonalViewController.MergeChannelComposite(
                        capturedData, capturedChannelDim, perChannel);
                }

                var result = BuildXZ(null);
                string frameLabel = BuildOrthoLabel("X", capturedAxis, "Y", capturedIY, capturedData, channelAxisName);
                AppendHistory(result, "Extract Frame (XZ)", Title,
                    BuildOrthoHistoryDetail("XZ", "Y", capturedIY, capturedData, capturedAxis, channelAxisName));
                var child = CreateLinked(result, _view.Lut, $"{Title} {frameLabel}", linkRefresh: false);
                if (useComposite) SeedChildCompositeMode(child, result, channelAxisName!);

                // The extract is pinned to its captured volumes and slice position, so frame
                // navigation must not rebuild it - only a content change (Refreshed) may. And
                // because it closed over those buffers, it cannot outlive the source swapping data.
                //
                // Re-slicing into the window's own frames keeps one IMatrixData on screen for its
                // whole life: no per-update allocation, and nothing downstream has to re-resolve
                // the instance. The cost is that the renderer can read a frame mid-write, so the
                // image may tear briefly under a fast feed. Building a fresh slice and swapping it
                // in would avoid that, at the price of a full-frame allocation per update and a new
                // instance each time; slicing on the UI thread would avoid it too, but a Virtual
                // source pulls every depth frame through the MMF and would stall the window.
                var windowData = child.MatrixData;
                _ = new LinkedView(child, this,
                    (_, ct) => Task.Run<LinkedViewUpdate?>(() =>
                    {
                        var updated = BuildXZ(windowData);
                        // GetArray invalidated each frame as the slice started writing it. Doing it
                        // again now closes the gap where a render in between could have cached a
                        // range measured from a half-written frame.
                        if (windowData != null)
                            for (int i = 0; i < windowData.FrameCount; i++) windowData.Invalidate(i);
                        return new LinkedViewUpdate(updated, useComposite ? channelAxisName : null);
                    }, ct),
                    LinkedViewCommit.RefreshInPlace,
                    trackActiveIndex: false,
                    closeOnSourceDataReplaced: true);

                child.Show();
                return;
            }

            if (sourceView == _orthoPanel.RightView)
            {
                // YZ view: fixed X slice — live extract. Same Composite handling as the XZ branch
                // above (see comment there).
                var capturedData = _currentData;
                if (capturedData == null) return;
                int capturedIX = _orthoController.CurrentIX;
                string capturedAxis = _orthoController.ActiveAxisName ?? "Z";
                bool useComposite = _isCompositeMode && _compositeAxisDimIndex >= 0;
                int capturedChannelDim = _compositeAxisDimIndex;
                string? channelAxisName = useComposite ? capturedData.Dimensions[capturedChannelDim].Name : null;

                // Freeze every axis, not just the slice position: "Extract" means this frame, and
                // the window title and history entry below record the full coordinate. Capturing
                // the volume rather than re-deriving it per rebuild is what pins the other axes -
                // ExtractAlong with deepCopy:false shares the source's frame buffers by reference,
                // so the extract still follows pixel edits while staying on its own (C, T, ...).
                var capturedIndices = capturedData.Dimensions.GetAxisIndices();
                IMatrixData CaptureVolume(int[] indices) =>
                    capturedData.Apply(new ExtractAlongOperation(capturedAxis, indices, DeepCopy: false));

                IMatrixData[] volumes;
                if (useComposite)
                {
                    int channelCount = capturedData.Dimensions[capturedChannelDim].Count;
                    volumes = new IMatrixData[channelCount];
                    for (int c = 0; c < channelCount; c++)
                    {
                        var bi = (int[])capturedIndices.Clone();
                        bi[capturedChannelDim] = c;
                        volumes[c] = CaptureVolume(bi);
                    }
                }
                else
                {
                    volumes = [CaptureVolume(capturedIndices)];
                }

                // The captured volumes carry a single axis, so the slice needs no axis name.
                // dst is null on the first call (nothing to write into yet) and the window's own
                // data thereafter, so every later slice lands in the frames already on screen.
                IMatrixData BuildYZ(IMatrixData? dst)
                {
                    if (!useComposite)
                        return volumes[0].Apply(new SliceOperation(ViewFrom.X, capturedIX, Dst: dst));

                    var perChannel = new IMatrixData[volumes.Length];
                    for (int c = 0; c < volumes.Length; c++)
                        perChannel[c] = volumes[c].Apply(new SliceOperation(ViewFrom.X, capturedIX, Dst: dst, DstIndex: c));
                    // With dst supplied the per-channel slices already share its frames, so the
                    // merge just rebuilds an equivalent wrapper and the caller keeps using dst.
                    return OrthogonalViewController.MergeChannelComposite(
                        capturedData, capturedChannelDim, perChannel);
                }

                var result = BuildYZ(null);
                string frameLabel = BuildOrthoLabel("Y", capturedAxis, "X", capturedIX, capturedData, channelAxisName);
                AppendHistory(result, "Extract Frame (YZ)", Title,
                    BuildOrthoHistoryDetail("YZ", "X", capturedIX, capturedData, capturedAxis, channelAxisName));
                var child = CreateLinked(result, _view.Lut, $"{Title} {frameLabel}", linkRefresh: false);
                if (useComposite) SeedChildCompositeMode(child, result, channelAxisName!);

                // The extract is pinned to its captured volumes and slice position, so frame
                // navigation must not rebuild it - only a content change (Refreshed) may. And
                // because it closed over those buffers, it cannot outlive the source swapping data.
                //
                // Re-slicing into the window's own frames keeps one IMatrixData on screen for its
                // whole life: no per-update allocation, and nothing downstream has to re-resolve
                // the instance. The cost is that the renderer can read a frame mid-write, so the
                // image may tear briefly under a fast feed. Building a fresh slice and swapping it
                // in would avoid that, at the price of a full-frame allocation per update and a new
                // instance each time; slicing on the UI thread would avoid it too, but a Virtual
                // source pulls every depth frame through the MMF and would stall the window.
                var windowData = child.MatrixData;
                _ = new LinkedView(child, this,
                    (_, ct) => Task.Run<LinkedViewUpdate?>(() =>
                    {
                        var updated = BuildYZ(windowData);
                        // GetArray invalidated each frame as the slice started writing it. Doing it
                        // again now closes the gap where a render in between could have cached a
                        // range measured from a half-written frame.
                        if (windowData != null)
                            for (int i = 0; i < windowData.FrameCount; i++) windowData.Invalidate(i);
                        return new LinkedViewUpdate(updated, useComposite ? channelAxisName : null);
                    }, ct),
                    LinkedViewCommit.RefreshInPlace,
                    trackActiveIndex: false,
                    closeOnSourceDataReplaced: true);

                child.Show();
                return;
            }

            {
                // Main view: slice the current frame (shallow copy). In Composite mode, a plain
                // single-frame slice would silently collapse every channel down to whichever one
                // ActiveIndex happens to be pinned to - extract the whole Channel axis instead
                // (fixing every other axis at its current index), reusing the same
                // ExtractAlongOperation "Extract Dimension" already applies for any axis.
                var data = sourceView.MatrixData;
                if (data == null) return;
                int frameIndex = sourceView.FrameIndex;

                IMatrixData result;
                string frameLabel;
                string historyLabel;
                string? historyDetail;
                var compositeCube = TryExtractCompositeFrameCube(data);
                if (compositeCube != null)
                {
                    result = compositeCube.Value.Cube;
                    string channelAxisName = compositeCube.Value.ChannelAxisName;
                    frameLabel = $"[{BuildCompositeCubeLabel(data, channelAxisName)}]";
                    historyLabel = "Extract Frame (Composite)";
                    var otherAxes = data.Axes.Where(a => !string.Equals(a.Name, channelAxisName, StringComparison.OrdinalIgnoreCase));
                    historyDetail = $"axis={channelAxisName}; fixed: " + string.Join(", ",
                        otherAxes.Select(a => $"{a.Name}={FormatAxisValue(a, a.Index, verbose: true)}"));
                }
                else
                {
                    bool isDeepCopy = !data.IsWritable; //true only if data is Writable (e.g., read-only MMF)
                    result = data.Apply(new SliceAtOperation(frameIndex, DeepCopy: isDeepCopy));
                    frameLabel = BuildFrameLabel(data, frameIndex);
                    historyLabel = "Extract Frame" + (isDeepCopy ? "(deep copy)" : "");
                    historyDetail = data.FrameCount > 1 ? BuildFrameHistoryDetail(data, frameIndex) : null;
                }
                AppendHistory(result, historyLabel, Title, historyDetail);
                var child = CreateLinked(result, _view.Lut, $"{Title} {frameLabel}");
                if (compositeCube != null) SeedChildCompositeMode(child, result, compositeCube.Value.ChannelAxisName);
                child.Show();
            }
        }

        /// <summary>
        /// Resolves the Channel axis on a freshly extracted child's data by name and delegates to
        /// <see cref="SeedChildCompositeState"/> for the actual state hand-off. Extract's three call
        /// sites (XY/XZ/YZ) only ever have the axis name in hand (not the axis object itself), which
        /// is the one thing <see cref="SeedChildCompositeState"/> needs a resolved <see cref="Axis"/>
        /// for.
        /// </summary>
        private void SeedChildCompositeMode(MatrixPlotter child, IMatrixData childData, string channelAxisName)
        {
            var channelAxis = childData.Axes.FindAxis(channelAxisName);
            if (channelAxis == null) return;
            SeedChildCompositeState(child, channelAxis);
        }

        /// <summary>
        /// Keeps an already-Composite child window's frame-index/range/histogram bookkeeping in
        /// sync after its data was swapped via <see cref="UpdateProjectionData"/> - see
        /// <see cref="SyncCurrentDataFromView"/> for why that swap alone isn't enough.
        /// </summary>
        private void RefreshCompositeAfterDataSwap()
        {
            SyncCurrentDataFromView();
            ApplyCompositeFrameIndices();
        }
    }
}
