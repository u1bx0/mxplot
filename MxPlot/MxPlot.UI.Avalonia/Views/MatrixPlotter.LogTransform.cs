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

        // Sync state (populated in the result window when SyncSource = true)
        private MatrixPlotter? _logSyncSourceWindow;
        private LogTransformDialog.LogTransformParameters? _logSyncParams;
        private EventHandler? _logSyncRefreshedHandler;
        private EventHandler? _logSyncActiveIndexHandler;
        private CancellationTokenSource? _logSyncCts;

        private async Task InvokeLogTransformAsync()
        {
            if (_currentData == null) return;
            HideMenuPanel();

            bool isMultiFrame = _currentData.FrameCount > 1;
            var (minVal, _) = _currentData.GetValueRange(_currentData.ActiveIndex);
            bool hasNegOrZero = !double.IsNaN(minVal) && minVal <= 0;

            var p = await LogTransformDialog.ShowAsync(this, isMultiFrame, hasNegOrZero);
            if (p == null) return;

            bool singleFrame = p.ThisFrameOnly || !isMultiFrame;
            int frameIdx = _currentData.ActiveIndex;
            string baseLabel = p.Base switch
            {
                LogBase.Log10 => "Log\u2081\u2080",
                LogBase.Log2 => "Log\u2082",
                _ => "Ln",
            };
            string label = $"Log Transform ({baseLabel})";
            string detail = singleFrame
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
                    SingleFrameIndex: singleFrame ? frameIdx : -1,
                    Progress: execProgress,
                    CancellationToken: ct);

                var data = _currentData;
                result = await Task.Run(() => data.Apply(op), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { await ShowMessageDialogAsync("Log Transform Failed", ex.Message); return; }
            finally { _logCts?.Dispose(); _logCts = null; EndProgress(); }

            result.CopyPropertiesFrom(_currentData, copyScale: true, copyDimensions: !singleFrame);
            AppendHistory(result, label, Title, detail);

            if (p.ReplaceData)
            {
                SetMatrixData(result);
            }
            else
            {
                var resultPlotter = MatrixPlotter.Create(result, _view.Lut, $"{baseLabel} of {Title}");

                if (p.SyncSource && singleFrame)
                {
                    PlotWindowNotifier.SetParentLink(resultPlotter, this);
                    resultPlotter.Show();
                    resultPlotter.StartLogSync(this, p);
                }
                else
                {
                    resultPlotter.Show();
                }
            }
        }

        // ── Sync source ───────────────────────────────────────────────────────

        internal void StartLogSync(
            MatrixPlotter sourceWindow,
            LogTransformDialog.LogTransformParameters p)
        {
            StopLogSync();

            _logSyncSourceWindow = sourceWindow;
            _logSyncParams = p;

            _logSyncRefreshedHandler = (_, _) => _ = FireLogSyncUpdateAsync();
            sourceWindow.Refreshed += _logSyncRefreshedHandler;

            if (sourceWindow.MatrixData is { FrameCount: > 1 } md)
            {
                _logSyncActiveIndexHandler = (_, _) => _ = FireLogSyncUpdateAsync();
                md.ActiveIndexChanged += _logSyncActiveIndexHandler;
            }

            sourceWindow.Closed += OnLogSyncSourceClosed;
            Closed += OnLogSyncWindowClosed;
        }

        private async Task FireLogSyncUpdateAsync()
        {
            _logSyncCts?.Cancel();
            _logSyncCts?.Dispose();
            _logSyncCts = new CancellationTokenSource();
            var ct = _logSyncCts.Token;

            var sourceData = _logSyncSourceWindow?.MatrixData;
            if (sourceData == null || _logSyncParams == null) return;
            int frameIdx = sourceData.ActiveIndex;
            var p = _logSyncParams;

            IMatrixData updated;
            try
            {
                updated = await Task.Run(() =>
                {
                    var single = sourceData.Apply(new SliceAtOperation(frameIdx));
                    return single.Apply(new LogTransformOperation(p.Base, p.Handling,
                        CancellationToken: ct));
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { return; }

            if (ct.IsCancellationRequested) return;

            updated.CopyPropertiesFrom(sourceData, copyScale: true, copyDimensions: false);
            SetMatrixData(updated);
        }

        private void StopLogSync()
        {
            _logSyncCts?.Cancel();
            _logSyncCts?.Dispose();
            _logSyncCts = null;

            if (_logSyncSourceWindow != null)
            {
                if (_logSyncRefreshedHandler != null)
                    _logSyncSourceWindow.Refreshed -= _logSyncRefreshedHandler;
                if (_logSyncActiveIndexHandler != null && _logSyncSourceWindow.MatrixData != null)
                    _logSyncSourceWindow.MatrixData.ActiveIndexChanged -= _logSyncActiveIndexHandler;
                _logSyncSourceWindow.Closed -= OnLogSyncSourceClosed;
            }

            _logSyncSourceWindow = null;
            _logSyncParams = null;
            _logSyncRefreshedHandler = null;
            _logSyncActiveIndexHandler = null;
        }

        private void OnLogSyncSourceClosed(object? sender, EventArgs e) => Close();

        private void OnLogSyncWindowClosed(object? sender, EventArgs e)
        {
            StopLogSync();
            Closed -= OnLogSyncWindowClosed;
        }
    }
}
