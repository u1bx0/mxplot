using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.IO;
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
        private readonly IVirtualFrameList _virtualList;
        private readonly TextBlock       _outputBlock;
        private readonly DispatcherTimer  _timer;
        private readonly long            _frameSize;

        public CacheMonitorWindow(IMatrixData matrixData)
        {
            var virtualList = matrixData.GetDiagnosticVirtualList()
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

            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                Content = _outputBlock,
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (_, _) => UpdateStatus();
            _timer.Start();
            Closed += (_, _) => _timer.Stop();

            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_virtualList.IsDisposed) return;

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
            sb.AppendLine();

            if (axisCount == 0)
            {
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
    }
}
