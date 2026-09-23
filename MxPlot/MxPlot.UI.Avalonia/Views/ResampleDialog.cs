using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for configuring a Resample (change the pixel count) operation.
    /// Returns the <see cref="ResampleParameters"/> and the dialog's checkboxes on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class ResampleDialog : ProcessingDialogBase
    {
        internal sealed record ResampleParameters(int Width, int Height, ResampleMethod Method);

        // ── Factory ───────────────────────────────────────────────────────────

        /// <param name="owner">The window the dialog is shown over.</param>
        /// <param name="isMultiFrame">Whether the source data has more than one frame.</param>
        /// <param name="isLinkWindow">
        /// Whether the owning window is itself being kept live by a sync — if so, "Replace data"
        /// is disabled (see <see cref="ProcessingDialogBase"/>).
        /// </param>
        /// <param name="src">Source data: pre-fills the current pixel counts and physical extent.</param>
        internal static Task<DialogAnswer<ResampleParameters>?> ShowAsync(
            Window owner, bool isMultiFrame, bool isLinkWindow, IMatrixData src)
        {
            var dlg = new ResampleDialog(isMultiFrame, isLinkWindow, src);
            return dlg.ShowDialog<DialogAnswer<ResampleParameters>?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private ResampleDialog(bool isMultiFrame, bool isLinkWindow, IMatrixData src)
            : base("Resample", width: 300, canResize: false, src: src,
                   thisFrameOnlyDefault: isMultiFrame ? false : (bool?)null, showSyncSource: true)
        {
            const double LW = 60;

            var widthNud = ControlFactory.MakeNumericUpDown(src.XCount, 1m, 1_000_000m, 1m, width: 80);
            var heightNud = ControlFactory.MakeNumericUpDown(src.YCount, 1m, 1_000_000m, 1m, width: 80);

            // Locks Height to Width's own pixel-count ratio (source YCount / XCount), independent of the
            // physical scale - the conventional meaning of "keep ratio" in a pixel-resize dialog.
            double srcRatio = (double)src.YCount / src.XCount;
            var keepRatioCheck = ControlFactory.MakeCheckBox("Keep ratio");
            widthNud.ValueChanged += (_, _) =>
            {
                if (keepRatioCheck.IsChecked != true || widthNud.Value is not { } w) return;
                heightNud.Value = Math.Max(1m, Math.Round(w * (decimal)srcRatio));
            };
            keepRatioCheck.IsCheckedChanged += (_, _) =>
            {
                heightNud.IsEnabled = keepRatioCheck.IsChecked != true;
                if (keepRatioCheck.IsChecked == true && widthNud.Value is { } w)
                    heightNud.Value = Math.Max(1m, Math.Round(w * (decimal)srcRatio));
            };

            var methodCombo = new ComboBox { Width = 120, Height = 20, MinHeight = 0, FontSize = 11, Padding = new Thickness(4, 0) };
            methodCombo.Items.Add("Bilinear");
            methodCombo.Items.Add("Nearest neighbor");
            methodCombo.SelectedIndex = 0;

            var infoText = new TextBlock { FontSize = 10, Opacity = 0.65, TextWrapping = TextWrapping.Wrap };

            void UpdateInfo()
            {
                int w = (int)(widthNud.Value ?? src.XCount);
                int h = (int)(heightNud.Value ?? src.YCount);
                double pitchX = w > 1 ? (src.XMax - src.XMin) / (w - 1) : 0;
                double pitchY = h > 1 ? (src.YMax - src.YMin) / (h - 1) : 0;
                string unitX = string.IsNullOrEmpty(src.XUnit) ? "px" : src.XUnit;
                string unitY = string.IsNullOrEmpty(src.YUnit) ? "px" : src.YUnit;
                long bytes = (long)w * h * src.ElementSize;
                infoText.Text = $"Pitch: {pitchX:G4} {unitX}/px, {pitchY:G4} {unitY}/px  •  {FormatFrameBytes(bytes)}/frame";
            }
            widthNud.ValueChanged += (_, _) => UpdateInfo();
            heightNud.ValueChanged += (_, _) => UpdateInfo();
            UpdateInfo();

            // ── Layout ────────────────────────────────────────────────────────
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(ControlFactory.MakeNudRow("Width:", widthNud, "px", labelWidth: LW));
            var heightRow = (Panel)ControlFactory.MakeNudRow("Height:", heightNud, "px", labelWidth: LW);
            keepRatioCheck.Margin = new Thickness(8, 0, 0, -7);
            heightRow.Children.Add(keepRatioCheck);
            content.Children.Add(heightRow);
            var methodRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            methodRow.Children.Add(new TextBlock { Text = "Method:", FontSize = 11, Width = LW, VerticalAlignment = VerticalAlignment.Center });
            methodRow.Children.Add(methodCombo);
            content.Children.Add(methodRow);
            content.Children.Add(infoText);
            content.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));
            var frameOptions = BuildFrameOptions();
            if (frameOptions != null)
                content.Children.Add(frameOptions);

            FinalizeContent(content, onOk: () =>
            {
                int w = (int)(widthNud.Value ?? src.XCount);
                int h = (int)(heightNud.Value ?? src.YCount);
                var method = methodCombo.SelectedIndex == 1 ? ResampleMethod.Nearest : ResampleMethod.Bilinear;
                Close(Answer(new ResampleParameters(w, h, method)));
            }, okLabel: "Apply");
        }

        /// <summary>Bytes of a single resampled frame, formatted for the info line (KB granularity up).</summary>
        private static string FormatFrameBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024L) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
    }
}
