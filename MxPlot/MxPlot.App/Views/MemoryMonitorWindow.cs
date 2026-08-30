using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.Text;

namespace MxPlot.App.Views
{
    /// <summary>
    /// App-wide singleton window showing live process/GC memory statistics, refreshed every
    /// 500ms. UI is built entirely in code, matching MxPlot.UI.Avalonia's CacheMonitorWindow style
    /// -- unlike that window, this one is not tied to any single document, so it lives here in
    /// MxPlot.App rather than the library.
    /// </summary>
    internal sealed class MemoryMonitorWindow : Window
    {
        private static MemoryMonitorWindow? _instance;

        private readonly TextBlock _outputBlock;
        private readonly DispatcherTimer _timer;
        private readonly Process _process = Process.GetCurrentProcess();

        // Deltas since the previous tick -- a raw cumulative count (e.g. "Gen2: 42") says little on
        // its own; how much it moved in the last 500ms is what actually shows GC pressure.
        private long _lastAllocatedBytes;
        private int _lastGen0, _lastGen1, _lastGen2;
        private DateTime _lastTick;

        private MemoryMonitorWindow()
        {
            Title = "Memory Monitor";
            Width = 480;
            Height = 300;

            _outputBlock = new TextBlock
            {
                FontFamily = new FontFamily("Consolas,Courier New,monospace"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(8),
            };

            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = _outputBlock,
            };

            _lastAllocatedBytes = GC.GetTotalAllocatedBytes();
            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            _lastTick = DateTime.UtcNow;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => UpdateStatus();
            _timer.Start();
            Closed += (_, _) =>
            {
                _timer.Stop();
                _process.Dispose();
                if (_instance == this) _instance = null;
            };

            UpdateStatus();
        }

        /// <summary>Shows the singleton Memory Monitor window, or brings it to front if already open.</summary>
        internal static void ShowOrActivate()
        {
            if (_instance == null)
            {
                _instance = new MemoryMonitorWindow();
                _instance.Show();
            }
            else
            {
                _instance.Activate();
            }
        }

        private static string FormatBytes(long bytes) => $"{bytes / (1024.0 * 1024):F1} MB";

        private void UpdateStatus()
        {
            var now = DateTime.UtcNow;
            double elapsedSec = Math.Max((now - _lastTick).TotalSeconds, 0.001);

            long totalAllocated = GC.GetTotalAllocatedBytes();
            long allocRate = (long)((totalAllocated - _lastAllocatedBytes) / elapsedSec);

            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);

            var gcInfo = GC.GetGCMemoryInfo();
            _process.Refresh();

            var sb = new StringBuilder();
            sb.AppendLine($"[Managed Heap]    {FormatBytes(GC.GetTotalMemory(false))}");
            sb.AppendLine($"[Total Allocated] {FormatBytes(totalAllocated)}   (+{FormatBytes(allocRate)}/s)");
            sb.AppendLine($"[GC Collections]  Gen0: {gen0} (+{gen0 - _lastGen0})   Gen1: {gen1} (+{gen1 - _lastGen1})   Gen2: {gen2} (+{gen2 - _lastGen2})");
            sb.AppendLine();
            sb.AppendLine($"[GC Heap]         Size: {FormatBytes(gcInfo.HeapSizeBytes)}   Fragmented: {FormatBytes(gcInfo.FragmentedBytes)}   Committed: {FormatBytes(gcInfo.TotalCommittedBytes)}");
            sb.AppendLine($"[Memory Load]     {FormatBytes(gcInfo.MemoryLoadBytes)} / {FormatBytes(gcInfo.TotalAvailableMemoryBytes)}   (High threshold: {FormatBytes(gcInfo.HighMemoryLoadThresholdBytes)})");
            sb.AppendLine();
            sb.AppendLine($"[Process]         Working Set: {FormatBytes(_process.WorkingSet64)}   Private: {FormatBytes(_process.PrivateMemorySize64)}");

            _outputBlock.Text = sb.ToString();

            _lastAllocatedBytes = totalAllocated;
            _lastGen0 = gen0; _lastGen1 = gen1; _lastGen2 = gen2;
            _lastTick = now;
        }
    }
}
