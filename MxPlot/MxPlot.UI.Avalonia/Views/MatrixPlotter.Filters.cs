using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Spatial filter ────────────────────────────────────────────────────
        //
        // Entry point:  InvokeFilterAsync()
        // Shows SpatialFilterDialog, then executes the chosen kernel via
        // SpatialFilterOperation (+ optional SliceAtOperation for single-frame).
        // Progress overlay and CancellationToken follow the same pattern as Crop.
        //
        // Sync source data:
        //   When the user checks "Sync source data" the result window subscribes to
        //   two triggers on the *source* MatrixPlotter:
        //     • source.Refreshed       — fires when plotter.Refresh() is called
        //                                (covers explicit data-content changes)
        //     • sourceData.ActiveIndexChanged — fires on frame-slider navigation
        //                                (for multi-frame + This frame only)
        //
        //   Each trigger cancels any in-flight filter Task (via CancellationToken),
        //   then starts a fresh computation on the new active frame.  If the filter
        //   is slow and triggers arrive rapidly only the last one completes.
        //
        //   The result window is registered as a parent-linked child via
        //   PlotWindowNotifier so dashboards can nest it correctly.
        //   When the source window closes the result window closes automatically.

        private CancellationTokenSource? _filterCts;

        // Fields populated in the result window when SyncSource = true
        private MatrixPlotter? _filterSyncSourceWindow;
        private IFilterKernel? _filterSyncKernel;
        private EventHandler? _filterSyncRefreshedHandler;
        private EventHandler? _filterSyncActiveIndexHandler;
        private CancellationTokenSource? _filterSyncCts;

        // ── Entry point ───────────────────────────────────────────────────────

        private Task InvokeMedianFilterAsync()
            => InvokeFilterAsync(SpatialFilterDialog.KernelType.Median);

        private Task InvokeGaussianFilterAsync()
            => InvokeFilterAsync(SpatialFilterDialog.KernelType.Gaussian);

        private async Task InvokeFilterAsync(SpatialFilterDialog.KernelType kernelType)
        {
            if (_currentData == null) return;
            HideMenuPanel();

            bool isMultiFrame = _currentData.FrameCount > 1;
            var p = await SpatialFilterDialog.ShowAsync(this, isMultiFrame, kernelType);
            if (p == null) return;

            await ExecuteFilterAsync(p);
        }

        // ── Execution ─────────────────────────────────────────────────────────

        private async Task ExecuteFilterAsync(SpatialFilterDialog.SpatialFilterParameters p)
        {
            if (_currentData == null) return;

            bool isMultiFrame = _currentData.FrameCount > 1;
            bool singleFrame = !isMultiFrame || p.ThisFrameOnly;
            int frameIdx = _currentData.ActiveIndex;

            string kernelLabel = KernelLabel(p.Kernel);
            string detailSuffix = singleFrame ? $" (frame {frameIdx})" : "";

            IMatrixData result;

            if (isMultiFrame && !singleFrame)
            {
                // All frames — stepped progress with cancellation
                var progress = BeginProgress($"Applying {kernelLabel}…", blockInput: true);
                _filterCts?.Dispose();
                _filterCts = new CancellationTokenSource();
                var ct = _filterCts.Token;
                try
                {
                    result = await Task.Run(() =>
                        _currentData.Apply(new SpatialFilterOperation(p.Kernel, progress, ct)), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { await ShowMessageDialogAsync("Filter Failed", ex.Message); return; }
                finally { _filterCts?.Dispose(); _filterCts = null; EndProgress(); }
            }
            else
            {
                // Single frame (or ThisFrameOnly) — marquee progress
                BeginProgress($"Applying {kernelLabel}…", blockInput: true);
                try
                {
                    result = singleFrame && isMultiFrame
                        ? await Task.Run(() =>
                        {
                            var single = _currentData.Apply(new SliceAtOperation(frameIdx));
                            return single.Apply(new SpatialFilterOperation(p.Kernel));
                        })
                        : await Task.Run(() =>
                            _currentData.Apply(new SpatialFilterOperation(p.Kernel)));
                }
                catch (Exception ex) { await ShowMessageDialogAsync("Filter Failed", ex.Message); return; }
                finally { EndProgress(); }
            }

            result.CopyPropertiesFrom(_currentData, copyScale: true, copyDimensions: false);
            AppendHistory(result, kernelLabel, Title,
                $"radius={p.Kernel.Radius}{detailSuffix}");

            string resultTitle = $"{kernelLabel} of {Title}";
            var resultPlotter = MatrixPlotter.Create(result, _view.Lut, resultTitle);

            if (p.SyncSource && singleFrame)
            {
                // Register as linked child before Show() so dashboard can nest it
                PlotWindowNotifier.SetParentLink(resultPlotter, this);
                resultPlotter.Show();
                resultPlotter.StartFilterSync(this, p.Kernel);
            }
            else
            {
                resultPlotter.Show();
            }
        }

        // ── Sync source data ──────────────────────────────────────────────────

        /// <summary>
        /// Attaches live-update sync to this (result) window.
        /// Subscribes to <paramref name="sourceWindow"/>.Refreshed (data-content changes)
        /// and to the source MatrixData.ActiveIndexChanged (frame-slider navigation).
        /// Closes this window when the source closes.
        /// </summary>
        internal void StartFilterSync(MatrixPlotter sourceWindow, IFilterKernel kernel)
        {
            StopFilterSync();

            _filterSyncSourceWindow = sourceWindow;
            _filterSyncKernel = kernel;

            _filterSyncRefreshedHandler = (_, _) => _ = FireSyncUpdateAsync();
            sourceWindow.Refreshed += _filterSyncRefreshedHandler;

            // Also track frame-slider navigation on multi-frame source data
            if (sourceWindow.MatrixData is { FrameCount: > 1 } md)
            {
                _filterSyncActiveIndexHandler = (_, _) => _ = FireSyncUpdateAsync();
                md.ActiveIndexChanged += _filterSyncActiveIndexHandler;
            }

            // Close result when source closes
            sourceWindow.Closed += OnFilterSyncSourceClosed;
            Closed += OnFilterSyncWindowClosed;
        }

        private async Task FireSyncUpdateAsync()
        {
            // Cancel any previous in-flight computation so only the latest frame wins
            _filterSyncCts?.Cancel();
            _filterSyncCts?.Dispose();
            _filterSyncCts = new CancellationTokenSource();
            var ct = _filterSyncCts.Token;

            var sourceData = _filterSyncSourceWindow?.MatrixData;
            if (sourceData == null || _filterSyncKernel == null) return;
            int frameIdx = sourceData.ActiveIndex;

            IMatrixData updated;
            try
            {
                updated = await Task.Run(() =>
                {
                    var single = sourceData.Apply(new SliceAtOperation(frameIdx));
                    return single.Apply(new SpatialFilterOperation(_filterSyncKernel, CancellationToken: ct));
                }, ct);
            }
            catch (OperationCanceledException) { return; }
            catch { return; }

            if (ct.IsCancellationRequested) return;

            updated.CopyPropertiesFrom(sourceData, copyScale: true, copyDimensions: false);
            SetMatrixData(updated);
        }

        private void StopFilterSync()
        {
            _filterSyncCts?.Cancel();
            _filterSyncCts?.Dispose();
            _filterSyncCts = null;

            if (_filterSyncSourceWindow != null)
            {
                if (_filterSyncRefreshedHandler != null)
                    _filterSyncSourceWindow.Refreshed -= _filterSyncRefreshedHandler;

                if (_filterSyncActiveIndexHandler != null && _filterSyncSourceWindow.MatrixData != null)
                    _filterSyncSourceWindow.MatrixData.ActiveIndexChanged -= _filterSyncActiveIndexHandler;

                _filterSyncSourceWindow.Closed -= OnFilterSyncSourceClosed;
            }

            _filterSyncSourceWindow = null;
            _filterSyncKernel = null;
            _filterSyncRefreshedHandler = null;
            _filterSyncActiveIndexHandler = null;
        }

        private void OnFilterSyncSourceClosed(object? sender, EventArgs e) => Close();

        private void OnFilterSyncWindowClosed(object? sender, EventArgs e)
        {
            StopFilterSync();
            Closed -= OnFilterSyncWindowClosed;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string KernelLabel(IFilterKernel kernel) => kernel switch
        {
            MedianKernel mk => $"Median {2 * mk.Radius + 1}\u00d7{2 * mk.Radius + 1}",
            GaussianKernel gk => $"Gaussian {2 * gk.Radius + 1}\u00d7{2 * gk.Radius + 1} \u03c3={gk.Sigma:G3}",
            _ => kernel.GetType().Name,
        };
    }
}
