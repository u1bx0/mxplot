using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Video
{
    /// <summary>
    /// Settings dialog shared by every <see cref="VideoExporterBase"/>-derived exporter: animation
    /// axis, interval/fps, output size (aspect-locked), and an overlay toggle. Format-specific
    /// output logic lives in each exporter's <see cref="IVideoFrameWriter"/>, not here -- this
    /// dialog only collects the settings every frame-sequence video export needs regardless of
    /// container/codec.
    /// </summary>
    public sealed class VideoExportDialog : Window
    {
        public bool Confirmed { get; private set; }
        public Axis SelectedAxis { get; private set; } = null!;
        public double IntervalMs { get; private set; } = 100;
        public int OutputWidth { get; private set; }
        public int OutputHeight { get; private set; }
        public bool WithOverlay { get; private set; }

        private static bool _lastWithOverlay = true;

        private readonly IReadOnlyList<Axis> _axes;
        private readonly double _aspect;
        private readonly bool _sizeEstimateIsExact;

        private readonly TextBlock _stepText;
        private readonly TextBlock _positionText;
        private readonly NumericUpDown _intervalNud;
        private readonly TextBlock _fpsText;
        private readonly TextBlock _summaryText;
        private readonly NumericUpDown _widthNud;
        private readonly NumericUpDown _heightNud;
        private readonly CheckBox _overlayChk;
        private readonly Button _exportBtn;

        private bool _syncSize;

        /// <param name="host">Supplies the axes, current render size, and overlay state to seed the dialog from.</param>
        /// <param name="filePath">Destination path, shown (file name only) for confirmation.</param>
        /// <param name="formatLabel">
        /// Short format name for the window title, e.g. <c>"AVI"</c> or <c>"MP4"</c> -- produces
        /// "Export &lt;view&gt; as &lt;formatLabel&gt;".
        /// </param>
        /// <param name="sizeEstimateIsExact">
        /// Whether the live "Total: ... / ~N MB" summary's byte estimate (computed from raw
        /// uncompressed BGR24 frame size) is meaningful for this format. <see langword="true"/> for
        /// an uncompressed container (AVI); pass <see langword="false"/> for anything encoder-
        /// compressed (H.264, etc.), where the raw estimate would be many times the real output size
        /// and is omitted rather than shown as a misleading number.
        /// </param>
        public VideoExportDialog(IRenderHost host, string filePath, string formatLabel, bool sizeEstimateIsExact = true)
        {
            _sizeEstimateIsExact = sizeEstimateIsExact;

            // Drop the axes the view already consumes: a side view's ortho depth axis, and the
            // Channel axis while Composite blends every channel into each frame.
            var allAxes = host.Data.Dimensions.Axes;
            var excluded = host.ExcludedAxisNames;
            _axes = excluded == null || excluded.Count == 0
                ? allAxes
                : allAxes.Where(a => !excluded.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToList();

            if (_axes.Count == 0)
                throw new InvalidOperationException(
                    "No axis is available to animate: every axis of this view is already consumed " +
                    "(the ortho slice axis, or the Channel axis while Composite rendering is active).");

            var renderSize = host.CurrentRenderSize;
            _aspect = renderSize.Height > 0 ? renderSize.Width / renderSize.Height : 1.0;
            int initW = SnapEven((int)Math.Round(renderSize.Width));
            int initH = SnapEven((int)Math.Round(renderSize.Height));

            string dialogTitle = host.ViewLabel != null
                ? $"Export {host.ViewLabel} as {formatLabel}"
                : $"Export as {formatLabel}";
            Title = dialogTitle;
            SizeToContent = SizeToContent.Height;
            Width = 380;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            const double FS = 11;

            var fileLabel = new TextBlock
            {
                Text = System.IO.Path.GetFileName(filePath),
                FontSize = FS,
                Opacity = 0.65,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            int defaultAxisIdx = GetDefaultAxisIndex();

            var axisCombo = new ComboBox
            {
                ItemsSource = _axes.Select(a => $"{a.Name} ({a.Count} frames)").ToList(),
                SelectedIndex = defaultAxisIdx,
                Height = 22,
                FontSize = FS,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            axisCombo.Classes.Add("compact");

            _stepText = new TextBlock { FontSize = FS, Opacity = 0.65, Margin = new Thickness(62, 0, 0, 0) };
            _positionText = new TextBlock { FontSize = FS, Opacity = 0.65, Margin = new Thickness(62, 0, 0, 0) };

            double defaultIntervalMs = GetDefaultIntervalMs(_axes[defaultAxisIdx]);
            _intervalNud = MakeNud((decimal)defaultIntervalMs, 1, 100000, 1, 88);
            _fpsText = new TextBlock { FontSize = FS, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
            _summaryText = new TextBlock { FontSize = FS, Opacity = 0.60, Margin = new Thickness(62, 0, 0, 0) };

            _widthNud = MakeNud(initW, 2, 16384, 2, 72);
            _heightNud = MakeNud(initH, 2, 16384, 2, 72);

            _overlayChk = new CheckBox
            {
                Content = "With overlays",
                FontSize = FS,
                IsChecked = _lastWithOverlay && host.IsOverlayVisible,
                Margin = new Thickness(0, 2, 0, 0),
            };

            _exportBtn = new Button
            {
                Content = "Export",
                FontSize = FS,
                Width = 72,
                IsDefault = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                FontSize = FS,
                Width = 72,
                IsCancel = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            axisCombo.SelectionChanged += (_, _) => UpdateFromAxis(axisCombo.SelectedIndex);
            _intervalNud.ValueChanged += (_, _) => UpdateSummary(axisCombo.SelectedIndex);
            _widthNud.ValueChanged += (_, _) =>
            {
                if (_syncSize) return;
                _syncSize = true;
                _heightNud.Value = (decimal)SnapEven((int)Math.Round((double)(_widthNud.Value ?? 2) / _aspect));
                _syncSize = false;
                UpdateSummary(axisCombo.SelectedIndex);
            };
            _heightNud.ValueChanged += (_, _) =>
            {
                if (_syncSize) return;
                _syncSize = true;
                _widthNud.Value = (decimal)SnapEven((int)Math.Round((double)(_heightNud.Value ?? 2) * _aspect));
                _syncSize = false;
                UpdateSummary(axisCombo.SelectedIndex);
            };
            _exportBtn.Click += (_, _) =>
            {
                Confirmed = true;
                SelectedAxis = _axes[axisCombo.SelectedIndex];
                IntervalMs = Math.Max(1, (double)(_intervalNud.Value ?? 100));
                OutputWidth = SnapEven((int)(_widthNud.Value ?? 2));
                OutputHeight = SnapEven((int)(_heightNud.Value ?? 2));
                WithOverlay = _overlayChk.IsChecked == true;
                _lastWithOverlay = WithOverlay;
                Close();
            };
            cancelBtn.Click += (_, _) => Close();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };

            UpdateFromAxis(defaultAxisIdx);

            // ── Layout ────────────────────────────────────────────────────────

            Func<Border> sep = () => new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(70, 128, 128, 128)),
                Margin = new Thickness(0, 4),
            };

            var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            intervalRow.Children.Add(new TextBlock { Text = "Interval", FontSize = FS, Width = 56, VerticalAlignment = VerticalAlignment.Center });
            intervalRow.Children.Add(_intervalNud);
            intervalRow.Children.Add(new TextBlock { Text = "ms", FontSize = FS, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
            intervalRow.Children.Add(_fpsText);

            var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            sizeRow.Children.Add(new TextBlock { Text = "Size", FontSize = FS, Width = 56, VerticalAlignment = VerticalAlignment.Center });
            sizeRow.Children.Add(_widthNud);
            sizeRow.Children.Add(new TextBlock { Text = "×", FontSize = FS, VerticalAlignment = VerticalAlignment.Center });
            sizeRow.Children.Add(_heightNud);
            sizeRow.Children.Add(new TextBlock { Text = "(even, aspect locked)", FontSize = FS - 1, Opacity = 0.45, VerticalAlignment = VerticalAlignment.Center });

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 10, 0, 0),
            };
            btnRow.Children.Add(_exportBtn);
            btnRow.Children.Add(cancelBtn);

            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 5 };
            panel.Children.Add(LabelRow(FS, "File", fileLabel));
            panel.Children.Add(sep());
            panel.Children.Add(LabelRow(FS, "Axis", axisCombo));
            panel.Children.Add(_stepText);
            panel.Children.Add(_positionText);
            panel.Children.Add(sep());
            panel.Children.Add(intervalRow);
            panel.Children.Add(_summaryText);
            panel.Children.Add(sep());
            panel.Children.Add(sizeRow);
            panel.Children.Add(_overlayChk);
            panel.Children.Add(btnRow);

            Content = panel;
        }

        private void UpdateFromAxis(int idx)
        {
            if (idx < 0 || idx >= _axes.Count) return;
            var axis = _axes[idx];

            double step = axis.Step;
            string unit = axis.Unit;
            _stepText.Text = step > 0
                ? $"Step: {step:G4}{(unit.Length > 0 ? " " + unit : "")}"
                : "";
            _stepText.IsVisible = step > 0;

            if (_axes.Count > 1)
            {
                var others = _axes
                    .Where(a => a != axis)
                    .Select(a => $"{a.Name} = i:{a.Index} [{a.Count}]");
                _positionText.Text = "Position: " + string.Join(", ", others);
                _positionText.IsVisible = true;
            }
            else
            {
                _positionText.IsVisible = false;
            }

            _intervalNud.Value = (decimal)GetDefaultIntervalMs(axis);
            UpdateSummary(idx);
        }

        private void UpdateSummary(int axisIdx)
        {
            if (axisIdx < 0 || axisIdx >= _axes.Count) return;
            var axis = _axes[axisIdx];
            double ms = Math.Max(1, (double)(_intervalNud.Value ?? 100));
            double fps = 1000.0 / ms;
            _fpsText.Text = $"→ {fps:G4} fps";

            int frames = axis.Count;
            double totalSec = ms * frames / 1000.0;
            int w = SnapEven((int)(_widthNud.Value ?? 2));
            int h = SnapEven((int)(_heightNud.Value ?? 2));

            if (_sizeEstimateIsExact)
            {
                // Raw uncompressed BGR24 size -- exact for an uncompressed container (AVI).
                long bytes = (long)w * h * 3 * frames;
                string sizeStr = bytes >= 1024 * 1024
                    ? $"~{bytes / (1024.0 * 1024):F1} MB"
                    : $"~{bytes / 1024.0:F0} KB";
                _summaryText.Text = $"Total: {totalSec:G4} s  /  {sizeStr}  ({frames} frames)";
            }
            else
            {
                // An encoder-compressed format's real output size depends on content and is not
                // worth guessing at from raw frame size alone -- showing the uncompressed byte count
                // here would read as the expected file size and be off by many times over.
                _summaryText.Text = $"Total: {totalSec:G4} s  ({frames} frames)";
            }

            _exportBtn.IsEnabled = ms > 0 && w >= 2 && h >= 2;
        }

        private int GetDefaultAxisIndex()
        {
            for (int i = 0; i < _axes.Count; i++)
                if (_axes[i].Name.Equals("Time", StringComparison.OrdinalIgnoreCase))
                    return i;
            return 0;
        }

        private static double GetDefaultIntervalMs(Axis axis)
        {
            if (axis.Step > 0)
            {
                var unit = axis.Unit.Trim().ToLowerInvariant();
                if (unit == "s") return Math.Round(axis.Step * 1000.0, 6);
                if (unit == "ms") return Math.Round(axis.Step, 6);
            }
            return 100.0;
        }

        internal static int SnapEven(int v) => v < 2 ? 2 : (v % 2 == 0 ? v : v + 1);

        private static NumericUpDown MakeNud(decimal value, decimal min, decimal max, decimal inc, double width)
        {
            var nud = new NumericUpDown
            {
                Value = value,
                Minimum = min,
                Maximum = max,
                Increment = inc,
                Width = width,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            nud.Classes.Add("compact");
            return nud;
        }

        private static StackPanel LabelRow(double fs, string label, Control content)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = fs,
                Width = 56,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(content);
            return row;
        }
    }
}
