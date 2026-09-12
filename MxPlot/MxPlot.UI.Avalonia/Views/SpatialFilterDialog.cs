using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for configuring a spatial filter (Median or Gaussian).
    /// Returns a <see cref="SpatialFilterParameters"/> record on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class SpatialFilterDialog : ProcessingDialogBase
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
        /// <param name="src">Source data, used only to decide whether to show the materialization warning.</param>
        internal static Task<SpatialFilterParameters?> ShowAsync(
            Window owner, bool isMultiFrame, KernelType defaultKernel = KernelType.Median, IMatrixData? src = null)
        {
            var dlg = new SpatialFilterDialog(isMultiFrame, defaultKernel, src);
            return dlg.ShowDialog<SpatialFilterParameters?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private SpatialFilterDialog(bool isMultiFrame, KernelType defaultKernel, IMatrixData? src)
            : base("Spatial Filter", width: 280, canResize: false, src: src,
                   thisFrameOnlyDefault: isMultiFrame ? false : (bool?)null, showReplaceData: false)
        {
            BuildContent(isMultiFrame, defaultKernel);
        }

        private void BuildContent(bool isMultiFrame, KernelType defaultKernel)
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

            // ── Sync source data ──────────────────────────────────────────────
            // For single-frame: always visible and enabled.
            // For multi-frame: only enabled when "This frame only" is checked.
            var syncCheck = ControlFactory.MakeCheckBox(
                "Sync source data",
                hint: "Keep the result live: re-apply the filter whenever the source frame changes");
            syncCheck.Margin = new Thickness(26, 0, 0, -10);
            if (isMultiFrame && ThisFrameOnlyCheckBox != null)
            {
                syncCheck.IsEnabled = false;
                ThisFrameOnlyCheckBox.IsCheckedChanged += (_, _) =>
                    syncCheck.IsEnabled = ThisFrameOnlyCheckBox.IsChecked == true;
            }

            // ── Layout ────────────────────────────────────────────────────────
            var kernelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            kernelRow.Children.Add(new TextBlock { Text = "Kernel:", FontSize = 11, Width = LW, VerticalAlignment = VerticalAlignment.Center });
            kernelRow.Children.Add(kernelCombo);

            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(kernelRow);
            content.Children.Add(ControlFactory.MakeNudRow("Radius:", radiusNud, "px", labelWidth: LW));
            content.Children.Add(sigmaRow);
            content.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));
            if (ThisFrameOnlyCheckBox != null)
            {
                ThisFrameOnlyCheckBox.Margin = new Thickness(26, 2, 0, -10);
                content.Children.Add(ThisFrameOnlyCheckBox);
            }
            content.Children.Add(syncCheck);

            FinalizeContent(content, onOk: () =>
            {
                int radius = (int)(radiusNud.Value ?? 1m);
                IFilterKernel kernel = kernelCombo.SelectedIndex == (int)KernelType.Gaussian
                    ? new GaussianKernel(radius, (double)(sigmaNud.Value ?? 0m))
                    : new MedianKernel(radius);
                bool thisFrameOnly = ThisFrameOnlyCheckBox?.IsChecked == true;
                bool syncSource = syncCheck.IsChecked == true;
                _result = new SpatialFilterParameters(kernel, thisFrameOnly, syncSource);
                Close(_result);
            }, okLabel: "Apply");
        }
    }
}
