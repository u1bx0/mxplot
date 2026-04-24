using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for configuring a spatial filter (Median or Gaussian).
    /// Returns a <see cref="SpatialFilterParameters"/> record on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class SpatialFilterDialog : Window
    {
        /// <summary>
        /// Parameters collected from the dialog.
        /// <see cref="ThisFrameOnly"/> and <see cref="SyncSource"/> are only meaningful
        /// when the source data has more than one frame, or when running in single-frame mode
        /// respectively — callers should guard accordingly.
        /// </summary>
        internal sealed record SpatialFilterParameters(
            IFilterKernel Kernel,
            bool ThisFrameOnly,
            bool SyncSource);

        // ── Kernel type ───────────────────────────────────────────────────────

        internal enum KernelType { Median, Gaussian }

        // ── Result ────────────────────────────────────────────────────────────

        private SpatialFilterParameters? _result;

        // ── Factory ───────────────────────────────────────────────────────────

        /// <summary>
        /// Shows the dialog modally and returns the user's parameters, or <c>null</c> if cancelled.
        /// </summary>
        /// <param name="owner">Parent window (used for centering).</param>
        /// <param name="isMultiFrame">
        /// When <c>true</c>, shows the "This frame only" / "Sync source data" options.
        /// When <c>false</c>, only "Sync source data" is shown (single-frame mode).
        /// </param>
        /// <param name="defaultKernel">Pre-selected kernel type when the dialog opens.</param>
        internal static Task<SpatialFilterParameters?> ShowAsync(
            Window owner, bool isMultiFrame, KernelType defaultKernel = KernelType.Median)
        {
            var dlg = new SpatialFilterDialog(isMultiFrame, defaultKernel);
            return dlg.ShowDialog<SpatialFilterParameters?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private SpatialFilterDialog(bool isMultiFrame, KernelType defaultKernel)
        {
            Title = "Spatial Filter";
            Width = 280;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Content = BuildContent(isMultiFrame, defaultKernel);
        }

        private Control BuildContent(bool isMultiFrame, KernelType defaultKernel)
        {
            const double LW = 64;

            // ── Kernel selector ───────────────────────────────────────────────
            var kernelCombo = new ComboBox { Width = 110, Height = 20, MinHeight = 0, FontSize = 11, Padding = new Thickness(4, 0) };
            kernelCombo.Items.Add("Median");
            kernelCombo.Items.Add("Gaussian");
            kernelCombo.SelectedIndex = (int)defaultKernel;

            // ── Radius ────────────────────────────────────────────────────────
            var radiusNud = ControlFactory.MakeNumericUpDown(1m, 1m, 10m, 1m, width: 60);

            // ── Sigma (Gaussian only) ─────────────────────────────────────────
            var sigmaLabel = new TextBlock { Text = "Sigma:", FontSize = 11, Width = LW, VerticalAlignment = VerticalAlignment.Center };
            var sigmaNud = ControlFactory.MakeNumericUpDown(0m, 0m, 20m, 0.1m, width: 60);
            var sigmaUnit = new TextBlock { Text = "(0 = auto)", FontSize = 10, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
            var sigmaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = defaultKernel == KernelType.Gaussian };
            sigmaRow.Children.Add(sigmaLabel);
            sigmaRow.Children.Add(sigmaNud);
            sigmaRow.Children.Add(sigmaUnit);

            kernelCombo.SelectionChanged += (_, _) =>
                sigmaRow.IsVisible = kernelCombo.SelectedIndex == (int)KernelType.Gaussian;

            // ── This frame only (multi-frame only) ────────────────────────────
            var thisFrameCheck = ControlFactory.MakeCheckBox(
                "This frame only",
                hint: "Apply the filter only to the currently active frame");
            thisFrameCheck.Margin = new Thickness(26, 2, 0, -10);
            thisFrameCheck.IsVisible = isMultiFrame;

            // ── Sync source data ──────────────────────────────────────────────
            // For single-frame: always visible and enabled.
            // For multi-frame: only enabled when "This frame only" is checked.
            var syncCheck = ControlFactory.MakeCheckBox(
                "Sync source data",
                hint: "Keep the result live: re-apply the filter whenever the source frame changes");
            syncCheck.Margin = new Thickness(26, 0, 0, -10);
            if (isMultiFrame)
            {
                syncCheck.IsEnabled = false;
                thisFrameCheck.IsCheckedChanged += (_, _) =>
                    syncCheck.IsEnabled = thisFrameCheck.IsChecked == true;
            }

            // ── Apply / Cancel (Windows order) ────────────────────────────────
            var okBtn = new Button
            {
                Content = "Apply",
                Width = 80,
                MinHeight = 26,
                Padding = new Thickness(8, 4),
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            okBtn.Classes.Add("accent");
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 80,
                MinHeight = 26,
                Padding = new Thickness(8, 4),
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            okBtn.Click += (_, _) =>
            {
                int radius = (int)(radiusNud.Value ?? 1m);
                IFilterKernel kernel = kernelCombo.SelectedIndex == (int)KernelType.Gaussian
                    ? new GaussianKernel(radius, (double)(sigmaNud.Value ?? 0m))
                    : new MedianKernel(radius);
                bool thisFrameOnly = thisFrameCheck.IsChecked == true;
                bool syncSource = syncCheck.IsChecked == true;
                _result = new SpatialFilterParameters(kernel, thisFrameOnly, syncSource);
                Close(_result);
            };
            cancelBtn.Click += (_, _) => Close(null);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);

            // ── Layout ────────────────────────────────────────────────────────
            var kernelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            kernelRow.Children.Add(new TextBlock { Text = "Kernel:", FontSize = 11, Width = LW, VerticalAlignment = VerticalAlignment.Center });
            kernelRow.Children.Add(kernelCombo);

            var content = new StackPanel { Spacing = 4, Margin = new Thickness(16, 14, 16, 14) };
            content.Children.Add(kernelRow);
            content.Children.Add(ControlFactory.MakeNudRow("Radius:", radiusNud, "px", labelWidth: LW));
            content.Children.Add(sigmaRow);
            content.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));
            content.Children.Add(thisFrameCheck);
            content.Children.Add(syncCheck);
            content.Children.Add(btnRow);
            return content;
        }
    }
}
