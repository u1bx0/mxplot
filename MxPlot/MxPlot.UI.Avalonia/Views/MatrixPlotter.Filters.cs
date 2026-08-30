using MxPlot.Core;
using MxPlot.Core.Processing;
using System;
using System.Collections.Generic;
using System.Linq;
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
            // Composite + This Frame Only: process every channel at the current position
            // instead of collapsing to whichever one ActiveIndex is pinned to (channel 0).
            var compositeCube = p.ThisFrameOnly ? TryExtractCompositeFrameCube(_currentData) : null;

            string kernelLabel = KernelLabel(p.Kernel);
            string detailSuffix = compositeCube != null
                ? $" ([{BuildCompositeCubeLabel(_currentData, compositeCube.Value.ChannelAxisName)}])"
                : singleFrame ? $" (frame {frameIdx})" : "";

            IMatrixData result;

            if (isMultiFrame && !singleFrame)
            {
                // All frames — stepped progress with cancellation
                _filterCts?.Dispose();
                _filterCts = new CancellationTokenSource();
                var ct = _filterCts.Token;
                var progress = BeginProgress($"Applying {kernelLabel}…", blockInput: true, _filterCts);
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
                // Single frame (or ThisFrameOnly, or Composite channel-cube) — marquee progress
                BeginProgress($"Applying {kernelLabel}…", blockInput: true);
                try
                {
                    result = compositeCube != null
                        ? await Task.Run(() => compositeCube.Value.Cube.Apply(new SpatialFilterOperation(p.Kernel)))
                        : singleFrame && isMultiFrame
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
            MatrixPlotter resultPlotter;
            if (p.SyncSource && singleFrame)
            {
                resultPlotter = CreateLinked(result, _view.Lut, resultTitle, linkRefresh: false);
                if (compositeCube != null) SeedChildCompositeMode(resultPlotter, result, compositeCube.Value.ChannelAxisName);
                resultPlotter.Show();
                StartFilterSync(resultPlotter, this, p.Kernel);
            }
            else
            {
                resultPlotter = MatrixPlotter.Create(result, _view.Lut, resultTitle);
                if (compositeCube != null) SeedChildCompositeMode(resultPlotter, result, compositeCube.Value.ChannelAxisName);
                resultPlotter.Show();
            }
        }

        // ── Sync source data ──────────────────────────────────────────────────

        /// <summary>
        /// Keeps <paramref name="follower"/> showing <paramref name="kernel"/> applied to whatever
        /// <paramref name="source"/> currently displays.
        /// </summary>
        private static void StartFilterSync(MatrixPlotter follower, MatrixPlotter source, IFilterKernel kernel)
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
                    var updated = opSource.Apply(new SpatialFilterOperation(kernel, CancellationToken: ct));
                    updated.CopyPropertiesFrom(sourceData, copyScale: true, copyDimensions: false);
                    return new LinkedViewUpdate(updated, compositeCube?.ChannelAxisName);
                }, ct);
            });
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
