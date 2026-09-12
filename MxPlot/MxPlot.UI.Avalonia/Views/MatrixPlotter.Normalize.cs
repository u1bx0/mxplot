using MxPlot.Core;
using MxPlot.Core.Processing;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Normalize ──────────────────────────────────────────────────────────
        //
        // Scope = PerFrame : each frame normalized independently (fast, no pre-scan).
        // Scope = Global   : one pre-scan pass to find the global max, then normalize.
        //                    For virtual data the pre-scan may take several seconds;
        //                    the dialog warns the user before they click Apply.
        //
        // "This frame only" skips multi-frame processing; scope becomes irrelevant
        // (single frame max is always the per-frame max).

        private CancellationTokenSource? _normalizeCts;

        private async Task InvokeNormalizeAsync()
        {
            if (_currentData == null) return;
            HideMenuPanel();

            bool isMultiFrame = IsThisFrameOnlyAChoice(_currentData);

            var p = await NormalizeDialog.ShowAsync(this, isMultiFrame, _currentData, IsReplaceDataBlocked);
            if (p == null) return;

            // Forced on when Composite mode has no surviving axis besides the composited one --
            // see IsThisFrameOnlyAChoice's remarks: the checkbox is hidden in that case, but the
            // composite-cube extraction below must still run so the result re-enters Composite mode.
            bool thisFrameOnly = p.ThisFrameOnly || (_isCompositeMode && !isMultiFrame);
            // Composite + This Frame Only: process every channel at the current position
            // instead of collapsing to whichever one ActiveIndex is pinned to (channel 0).
            var compositeCube = thisFrameOnly ? TryExtractCompositeFrameCube(_currentData) : null;

            // ── Pre-scan global max if needed ─────────────────────────────────
            double globalMax = double.NaN;
            if (p.Scope == NormalizeScope.Global && isMultiFrame && (!thisFrameOnly || compositeCube != null))
            {
                var scanProgress = BeginProgress("Scanning max value\u2026", blockInput: true);
                _normalizeCts?.Dispose();
                _normalizeCts = new CancellationTokenSource();
                var scanCt = _normalizeCts.Token;
                try
                {
                    var data = compositeCube?.Cube ?? _currentData;
                    globalMax = await Task.Run(() => ScanGlobalMaxValue(data, scanCt), scanCt);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { await ShowMessageDialogAsync("Normalize Failed", ex.Message); return; }
                finally { _normalizeCts?.Dispose(); _normalizeCts = null; EndProgress(); }
            }

            // ── Execute normalization ─────────────────────────────────────────
            int frameIdx = _currentData.ActiveIndex;
            bool singleFrame = thisFrameOnly || !isMultiFrame;
            string label = $"Normalize (max\u2192{p.Target:G4})";
            string detail = compositeCube != null
                ? $"[{BuildCompositeCubeLabel(_currentData, compositeCube.Value.ChannelAxisName)}], scope={p.Scope}, target={p.Target:G4}"
                : singleFrame
                    ? $"frame {frameIdx}, target={p.Target:G4}"
                    : $"scope={p.Scope}, target={p.Target:G4}";

            var execProgress = BeginProgress($"Normalizing\u2026", blockInput: true);
            _normalizeCts?.Dispose();
            _normalizeCts = new CancellationTokenSource();
            var ct = _normalizeCts.Token;

            IMatrixData result;
            try
            {
                var op = new NormalizeOperation(
                    Target: p.Target,
                    Scope: p.Scope,
                    SingleFrameIndex: compositeCube != null ? -1 : (singleFrame ? frameIdx : -1),
                    PrecomputedGlobalMax: globalMax,
                    Progress: execProgress,
                    CancellationToken: ct);

                var data = compositeCube?.Cube ?? _currentData;
                result = await Task.Run(() => data.Apply(op), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { await ShowMessageDialogAsync("Normalize Failed", ex.Message); return; }
            finally { _normalizeCts?.Dispose(); _normalizeCts = null; EndProgress(); }

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
                var resultPlotter = MatrixPlotter.Create(result, _view.Lut, $"Normalized {Title}");
                if (compositeCube != null) SeedChildCompositeMode(resultPlotter, result, compositeCube.Value.ChannelAxisName);
                resultPlotter.Show();
            }
        }

        /// <summary>Scans all frames of <paramref name="data"/> and returns the global maximum value.</summary>
        private static double ScanGlobalMaxValue(IMatrixData data, CancellationToken ct)
        {
            double max = double.NegativeInfinity;
            for (int i = 0; i < data.FrameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (_, frameMax) = data.GetValueRange(i);
                if (frameMax > max) max = frameMax;
            }
            return max;
        }
    }
}
