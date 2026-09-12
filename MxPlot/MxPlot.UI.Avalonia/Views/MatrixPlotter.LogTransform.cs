using MxPlot.Core;
using MxPlot.Core.Processing;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Log Transform ──────────────────────────────────────────────────────
        //
        // Output is always MatrixData<double> regardless of source T.
        //
        // Negative/zero handling:
        //   Shift: per-frame shift of |frameMin| + ε ensures log(ε) as the floor.
        //         Each frame is shifted independently — cross-frame magnitudes are
        //         not comparable, but each frame looks correct individually.
        //   Clamp: universal ε floor — consistent across frames.
        //
        // Sync source (single-frame mode):
        //   Same pattern as SpatialFilter sync. Subscribes to source.Refreshed
        //   and source.MatrixData.ActiveIndexChanged; re-runs on each trigger.

        private CancellationTokenSource? _logCts;


        private async Task InvokeLogTransformAsync()
        {
            if (_currentData == null) return;
            HideMenuPanel();

            bool isMultiFrame = IsThisFrameOnlyAChoice(_currentData);
            var (minVal, _) = _currentData.GetValueRange(_currentData.ActiveIndex);
            bool hasNegOrZero = !double.IsNaN(minVal) && minVal <= 0;

            var p = await LogTransformDialog.ShowAsync(this, isMultiFrame, hasNegOrZero, IsReplaceDataBlocked, _currentData);
            if (p == null) return;

            // Forced on when Composite mode has no surviving axis besides the composited one --
            // see IsThisFrameOnlyAChoice's remarks: the checkbox is hidden in that case, but the
            // composite-cube extraction below must still run so the result re-enters Composite mode.
            bool thisFrameOnly = p.ThisFrameOnly || (_isCompositeMode && !isMultiFrame);
            bool singleFrame = thisFrameOnly || !isMultiFrame;
            int frameIdx = _currentData.ActiveIndex;
            // Composite + This Frame Only: process every channel at the current position
            // instead of collapsing to whichever one ActiveIndex is pinned to (channel 0).
            var compositeCube = thisFrameOnly ? TryExtractCompositeFrameCube(_currentData) : null;

            string baseLabel = p.Base switch
            {
                LogBase.Log10 => "Log\u2081\u2080",
                LogBase.Log2 => "Log\u2082",
                _ => "Ln",
            };
            string label = $"Log Transform ({baseLabel})";
            string detail = compositeCube != null
                ? $"[{BuildCompositeCubeLabel(_currentData, compositeCube.Value.ChannelAxisName)}], base={baseLabel}, handling={p.Handling}"
                : singleFrame
                    ? $"frame {frameIdx}, base={baseLabel}, handling={p.Handling}"
                    : $"base={baseLabel}, handling={p.Handling}";

            var execProgress = BeginProgress("Applying log transform\u2026", blockInput: true);
            _logCts?.Dispose();
            _logCts = new CancellationTokenSource();
            var ct = _logCts.Token;

            IMatrixData result;
            try
            {
                var op = new LogTransformOperation(
                    Base: p.Base,
                    Handling: p.Handling,
                    SingleFrameIndex: compositeCube != null ? -1 : (singleFrame ? frameIdx : -1),
                    Progress: execProgress,
                    CancellationToken: ct);

                var data = compositeCube?.Cube ?? _currentData;
                result = await Task.Run(() => data.Apply(op), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { await ShowMessageDialogAsync("Log Transform Failed", ex.Message); return; }
            finally { _logCts?.Dispose(); _logCts = null; EndProgress(); }

            result.CopyPropertiesFrom(_currentData, copyScale: true, copyDimensions: !singleFrame);
            AppendHistory(result, label, Title, detail);

            if (p.ReplaceData)
            {
                if (compositeCube != null)
                {
                    var snapshot = CaptureCompositeCubeState();
                    SetMatrixData(result, closeSyncFollowers: true);
                    if (snapshot != null) ReenterCompositeMode(snapshot.Value, compositeCube.Value.ChannelAxisName);
                }
                else
                {
                    SetMatrixData(result, closeSyncFollowers: true);
                }
            }
            else
            {
                string resultTitle = $"{baseLabel} of {Title}";
                MatrixPlotter resultPlotter;
                if (p.SyncSource && singleFrame)
                {
                    // CreateLinked marks the child as secondary (IsSecondaryWindow = true), which
                    // suppresses the unsaved-changes confirmation on close — a sync-driven window
                    // is fully derived and recomputed on demand, so it never needs an independent
                    // save prompt (matches the Spatial Filter sync branch in ExecuteFilterAsync).
                    resultPlotter = CreateLinked(result, _view.Lut, resultTitle, linkRefresh: false);
                    if (compositeCube != null) SeedChildCompositeMode(resultPlotter, result, compositeCube.Value.ChannelAxisName);
                    resultPlotter.Show();
                    StartLogSync(resultPlotter, this, p);
                }
                else
                {
                    resultPlotter = MatrixPlotter.Create(result, _view.Lut, resultTitle);
                    if (compositeCube != null) SeedChildCompositeMode(resultPlotter, result, compositeCube.Value.ChannelAxisName);
                    resultPlotter.Show();
                }
            }
        }

        // ── Sync source ───────────────────────────────────────────────────────

        /// <summary>
        /// Keeps <paramref name="follower"/> showing the log transform applied to whatever
        /// <paramref name="source"/> currently displays.
        /// </summary>
        private static void StartLogSync(
            MatrixPlotter follower, MatrixPlotter source, LogTransformDialog.LogTransformParameters p)
        {
            _ = new LinkedView(follower, source, (src, ct) =>
            {
                var sourceData = src.MatrixData;
                if (sourceData == null) return Task.FromResult<LinkedViewUpdate?>(null);

                // Composite-aware: while the source is in Composite mode, take the whole
                // Channel-axis cube instead of a single-frame slice, so the follower keeps
                // blending every channel rather than collapsing to channel 0.
                var compositeCube = src.TryExtractCompositeFrameCube(sourceData);
                IMatrixData opSource = compositeCube?.Cube
                    ?? sourceData.Apply(new SliceAtOperation(sourceData.ActiveIndex));

                return Task.Run<LinkedViewUpdate?>(() =>
                {
                    // opSource is already a single frame (or a channel cube), so neither
                    // SingleFrameIndex nor Progress applies here.
                    var updated = opSource.Apply(
                        new LogTransformOperation(p.Base, p.Handling, CancellationToken: ct));
                    updated.CopyPropertiesFrom(sourceData, copyScale: true, copyDimensions: false);
                    return new LinkedViewUpdate(updated, compositeCube?.ChannelAxisName);
                }, ct);
            });
        }
    }
}
