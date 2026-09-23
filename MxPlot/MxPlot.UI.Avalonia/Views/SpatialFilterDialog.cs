using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for configuring a spatial filter (Median or Gaussian).
    /// Returns the <see cref="SpatialFilterParameters"/> and the dialog's checkboxes on OK, or <c>null</c>
    /// on cancel. It offers Sync source data instead of Replace data.
    /// </summary>
    internal sealed class SpatialFilterDialog : ProcessingDialogBase
    {
        /// <summary>Parameters collected from the dialog.</summary>
        internal sealed record SpatialFilterParameters(IFilterKernel Kernel);

        // ── Kernel type ───────────────────────────────────────────────────────

        internal enum KernelType { Median, Gaussian }

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
        internal static Task<DialogAnswer<SpatialFilterParameters>?> ShowAsync(
            Window owner, bool isMultiFrame, KernelType defaultKernel = KernelType.Median, IMatrixData? src = null)
        {
            var dlg = new SpatialFilterDialog(isMultiFrame, defaultKernel, src);
            return dlg.ShowDialog<DialogAnswer<SpatialFilterParameters>?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private SpatialFilterDialog(bool isMultiFrame, KernelType defaultKernel, IMatrixData? src)
            : base("Spatial Filter", width: 280, canResize: false, src: src,
                   thisFrameOnlyDefault: isMultiFrame ? false : (bool?)null, showReplaceData: false,
                   showSyncSource: true)
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

            // ── Layout ────────────────────────────────────────────────────────
            var kernelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            kernelRow.Children.Add(new TextBlock { Text = "Kernel:", FontSize = 11, Width = LW, VerticalAlignment = VerticalAlignment.Center });
            kernelRow.Children.Add(kernelCombo);

            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(kernelRow);
            content.Children.Add(ControlFactory.MakeNudRow("Radius:", radiusNud, "px", labelWidth: LW));
            content.Children.Add(sigmaRow);
            content.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));
            var frameOptions = BuildFrameOptions(indent: 26);
            if (frameOptions != null)
                content.Children.Add(frameOptions);

            FinalizeContent(content, onOk: () =>
            {
                int radius = (int)(radiusNud.Value ?? 1m);
                IFilterKernel kernel = kernelCombo.SelectedIndex == (int)KernelType.Gaussian
                    ? new GaussianKernel(radius, (double)(sigmaNud.Value ?? 0m))
                    : new MedianKernel(radius);
                Close(Answer(new SpatialFilterParameters(kernel)));
            }, okLabel: "Apply");
        }
    }
}
