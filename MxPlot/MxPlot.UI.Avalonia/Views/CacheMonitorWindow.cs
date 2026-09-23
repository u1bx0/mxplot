using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.Core.IO.CacheStrategies;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Avalonia equivalent of WinForms CacheMonitorForm.
    /// Displays a live ASCII-art view of the on-demand frame cache for a virtual IMatrixData.
    /// UI is built entirely in code — no .axaml file required.
    /// </summary>
    internal sealed class CacheMonitorWindow : Window
    {
        private readonly IMatrixData     _matrixData;
        private readonly IMmfFrameList _virtualList;
        private readonly TextBlock       _outputBlock;
        private readonly CacheGridControl _grid;
        private readonly ScrollViewer    _scrollViewer;
        private readonly CheckBox        _flatModeCheck;
        private readonly CheckBox        _wrapCheck;
        private readonly DispatcherTimer  _timer;
        private readonly long            _frameSize;

        public CacheMonitorWindow(IMatrixData matrixData)
        {
            // This window is deliberately MMF-only (its ASCII cache-grid assumes a bounded,
            // physically-addressable frame set) -- pattern-match the generic diagnostic accessor
            // against the MMF marker interface rather than needing a separate narrower method.
            var virtualList = matrixData.GetDiagnosticCacheableList() as IMmfFrameList
                ?? throw new InvalidOperationException("Target is not in virtual mode.");

            _matrixData  = matrixData;
            _virtualList = virtualList;
            _frameSize   = (long)matrixData.XCount * matrixData.YCount * matrixData.ElementSize;

            Title  = $"Cache Monitor — {matrixData.ValueTypeName}  [{matrixData.XCount}×{matrixData.YCount}]";
            Width  = 600;
            Height = 500;

            _outputBlock = new TextBlock
            {
                FontFamily   = new FontFamily("Consolas,Courier New,monospace"),
                FontSize     = 12,
                TextWrapping = TextWrapping.NoWrap,
                Margin       = new Thickness(4),
            };

            _grid = new CacheGridControl
            {
                Margin              = new Thickness(4, 0, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            _scrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { _outputBlock, _grid } },
            };

            _flatModeCheck = ControlFactory.MakeCheckBox("Flat grid", hint: "Show one square-ish 2D grid of every frame instead of grouping by axis.");
            _wrapCheck     = ControlFactory.MakeCheckBox("Wrap to window", hint: "Flat grid only: size the grid to the window width instead of a fixed square.");
            _flatModeCheck.IsCheckedChanged += (_, _) => UpdateStatus();
            _wrapCheck.IsCheckedChanged     += (_, _) => UpdateStatus();

            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 12,
                Margin      = new Thickness(4, 4, 4, 0),
                Children    = { _flatModeCheck, _wrapCheck },
            };

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(_scrollViewer);
            Content = root;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (_, _) => UpdateStatus();
            _timer.Start();
            Closed += (_, _) => _timer.Stop();

            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_virtualList.IsDisposed) return;

            // Applies to both modes, not just the flat grid -- the axis-grouped view can produce
            // very long single lines too (one per outer-axis value), and forcing a horizontal
            // scrollbar there is exactly the same readability problem the flat grid was built to fix.
            bool wrap = _wrapCheck.IsChecked == true;
            _outputBlock.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

            // A ScrollViewer with HorizontalScrollBarVisibility=Auto hands its content unconstrained
            // width to measure against (so it CAN scroll horizontally) -- which also means a wrapping
            // TextBlock inside it never actually wraps, since it's never given a width to wrap at.
            // Disabling the horizontal bar while wrapping is on forces the real available width
            // through instead. The flat grid doesn't depend on this (it hand-wraps into its own
            // fixed-width lines), only the axis-grouped view's long lines do.
            _scrollViewer.HorizontalScrollBarVisibility = wrap
                ? ScrollBarVisibility.Disabled
                : ScrollBarVisibility.Auto;

            var snapshot    = _virtualList.GetCacheStatus();
            var cachedSet   = new HashSet<int>(snapshot.CachedIndices);
            int capacity    = _virtualList.CacheCapacity;
            var strategy    = _virtualList.CacheStrategy;
            double cachedMb   = cachedSet.Count * _frameSize / (1024.0 * 1024);
            double capacityMb = capacity        * _frameSize / (1024.0 * 1024);

            var sb       = new StringBuilder();
            var dims     = _matrixData.Dimensions;
            var axes     = dims.Axes;
            int axisCount = axes.Count;

            sb.AppendLine($"[Cache Status] {cachedSet.Count} / {capacity} frames  —  {cachedMb:F1} MB / {capacityMb:F1} MB");
            sb.AppendLine($"[Backend]      {_virtualList}  ({(_matrixData.IsWritable ? "Writable" : "ReadOnly")})");
            sb.AppendLine($"[Strategy]     {strategy?.GetType().Name ?? "none"}");

            // Capacity is derived from available memory (VirtualCachePolicy), not a fixed number,
            // and which budget fraction applies depends on whether Volume mode (orthogonal viewing)
            // is currently elevating it -- surface that live so a low/high capacity is explainable
            // from this window alone instead of having to go read VirtualCachePolicy's source.
            bool isVolumeMode = strategy is DimensionStrategy { Mode: DimensionStrategy.CacheMode.Volume };
            double activeFraction = isVolumeMode ? VirtualCachePolicy.VolumeModeMemoryBudgetFraction : VirtualCachePolicy.MemoryBudgetFraction;
            long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            double availableMb = availableBytes / (1024.0 * 1024);
            double budgetMb = availableMb * activeFraction;
            long budgetFrames = _frameSize > 0 ? (long)(budgetMb * 1024 * 1024 / _frameSize) : 0;

            sb.AppendLine($"[Memory]       Available: {availableMb:F0} MB   " +
                          $"Budget: {activeFraction:P0} ({(isVolumeMode ? "Volume mode, elevated" : "baseline")}) " +
                          $"= {budgetMb:F0} MB (~{budgetFrames} frames)");
            sb.AppendLine($"               (baseline {VirtualCachePolicy.MemoryBudgetFraction:P0} / Volume-mode {VirtualCachePolicy.VolumeModeMemoryBudgetFraction:P0} " +
                          $"of available; floor {VirtualCachePolicy.MinCapacity} / ceiling {VirtualCachePolicy.MaxCapacity} frames)");

            // How much of the *actual* Volume-mode working set (TargetAxis x CompositeAxis, at the
            // current position on every other axis) is cached right now -- the number that matters
            // for whether orthogonal slicing/crosshair moves will thrash, as opposed to the raw
            // cached-count/capacity ratio above, which says nothing about whether the cached frames
            // are the RIGHT ones.
            if (strategy is DimensionStrategy { Mode: DimensionStrategy.CacheMode.Volume } ds)
            {
                // GetVolumeWorkingSet already includes the current frame (unlike GetPreloadIndices,
                // which deliberately omits it) -- no separate "+1" needed here.
                int workingSetTotal = 0, workingSetCached = 0;
                foreach (int idx in ds.GetVolumeWorkingSet(_matrixData.ActiveIndex))
                {
                    workingSetTotal++;
                    if (cachedSet.Contains(idx)) workingSetCached++;
                }

                double pct = workingSetTotal > 0 ? 100.0 * workingSetCached / workingSetTotal : 0;
                sb.AppendLine($"[Working Set]  {workingSetCached} / {workingSetTotal} frames  ({pct:F1}%)  " +
                              $"-- TargetAxis={ds.TargetAxis?.Name ?? "(none)"} x CompositeAxis={ds.CompositeAxis?.Name ?? "(none)"}");
            }

            bool flatMode = _flatModeCheck.IsChecked == true;
            int total = _matrixData.FrameCount;
            _grid.IsVisible = flatMode && total > 0;

            if (flatMode)
            {
                // The flat grid draws its own legend, so the header text ends here.
                if (total <= 0) sb.AppendLine("(no frames)");
                else UpdateFlatGrid(total, cachedSet);

                if (_outputBlock.Text != sb.ToString())
                    _outputBlock.Text = sb.ToString();
                return;
            }

            sb.AppendLine("[Legend]       # = cached in RAM    - = not cached (reads from disk on next access)");
            sb.AppendLine();

            if (axisCount == 0)
            {
                _outputBlock.Text = sb.ToString();
                return;
            }

            if (axisCount == 1)
            {
                // A single axis has nothing to group by -- it IS the whole frame index space, so
                // just render it as one flat bar. The multi-axis grouping below assumes the outer
                // ("topIdx") and inner ("lastIdx") axes differ; with only one axis they are the
                // same axis, and the inner loop would silently overwrite the outer loop's index
                // before it's ever read, producing an identical (and wrong) bar for every row.
                var axis = axes[0];
                sb.AppendLine($"{axis.Name} (frame 0..{axis.Count - 1}):");
                sb.Append('[');
                var singleAxisCoords = new int[1];
                for (int i = 0; i < axis.Count; i++)
                {
                    singleAxisCoords[0] = i;
                    int idx = dims.GetFrameIndexAt(singleAxisCoords);
                    sb.Append(cachedSet.Contains(idx) ? '#' : '-');
                }
                sb.AppendLine("]");

                if (_outputBlock.Text != sb.ToString())
                    _outputBlock.Text = sb.ToString();
                return;
            }

            int topIdx  = axisCount - 1;
            int lastIdx = 0;
            int[] coords = new int[axisCount];

            for (int i = 0; i < axes[topIdx].Count; i++)
            {
                coords[topIdx] = i;

                sb.Append($"{axes[topIdx].Name}-{i}: [");
                for (int a = topIdx - 1; a >= 0; a--)
                    sb.Append($"{axes[a].Name}({axes[a].Count}){(a > 0 ? "-" : "")}");
                sb.AppendLine("]");

                sb.Append("  [");
                int midCombinations = 1;
                for (int a = topIdx - 1; a > 0; a--)
                    midCombinations *= axes[a].Count;

                for (int m = 0; m < midCombinations; m++)
                {
                    int tempM = m;
                    for (int a = 1; a <= topIdx - 1; a++)
                    {
                        coords[a] = tempM % axes[a].Count;
                        tempM    /= axes[a].Count;
                    }

                    sb.Append("[");
                    for (int t = 0; t < axes[lastIdx].Count; t++)
                    {
                        coords[lastIdx] = t;
                        int idx = dims.GetFrameIndexAt(coords);
                        sb.Append(cachedSet.Contains(idx) ? '#' : '-');
                    }
                    sb.Append("]");
                }

                sb.AppendLine("]");
                sb.AppendLine();
            }

            if (_outputBlock.Text != sb.ToString())
                _outputBlock.Text = sb.ToString();
        }

        /// <summary>
        /// Shows every frame's cache state as one 2D grid (frame index = x + y*columns), instead of
        /// grouping by axis -- readable at a glance for large frame counts, where the axis-grouped
        /// view degenerates into either very long lines or a wall of short ones. The grid is roughly
        /// square, or as wide as the window when wrapping is on -- in either case with a whole number
        /// of blocks per row.
        /// </summary>
        private void UpdateFlatGrid(int total, HashSet<int> cachedSet)
        {
            // Leaves room for the vertical scroll bar, which the viewport width includes.
            const double ScrollBarAllowance = 20;

            double viewportPx = _scrollViewer.Bounds.Width - ScrollBarAllowance;
            int columns = _wrapCheck.IsChecked == true && viewportPx > 0
                ? _grid.ColumnsFitting(viewportPx, total)
                : CacheGridControl.PreferredColumns(total);

            _grid.SetState(total, columns, cachedSet);
        }
    }
}
