using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for dimension-based extraction operations on hyperstack data.
    /// Supports two modes:
    /// <list type="bullet">
    ///   <item><b>Extract Along</b> — extracts a 1-D slice along the chosen axis at the current indices of all other axes (<see cref="MxPlot.Core.Processing.ExtractAlongOperation"/>).</item>
    ///   <item><b>Extract At</b> — fixes the chosen axis at its current index and returns the remaining hyperstack dimensions (<see cref="MxPlot.Core.Processing.SelectByOperation"/>).</item>
    /// </list>
    /// Returns the <see cref="ExtractDimensionParameters"/> and the dialog's checkboxes on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class ExtractDimensionDialog : ProcessingDialogBase
    {
        internal enum ExtractMode { Along, At }

        internal sealed record ExtractDimensionParameters(ExtractMode Mode, string AxisName);

        internal static Task<DialogAnswer<ExtractDimensionParameters>?> ShowAsync(Window owner, IReadOnlyList<Axis> axes, bool isLinkWindow = false)
        {
            var dlg = new ExtractDimensionDialog(axes, isLinkWindow);
            return dlg.ShowDialog<DialogAnswer<ExtractDimensionParameters>?>(owner);
        }

        private ExtractDimensionDialog(IReadOnlyList<Axis> axes, bool isLinkWindow) : base("Extract...", width: 300, isLinkWindow: isLinkWindow)
        {
            const double LW = 90;

            // ── Mode selector ─────────────────────────────────────────────────
            var modeCombo = new ComboBox
            {
                Width = 140,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
            };
            modeCombo.Items.Add("Extract Along");
            modeCombo.Items.Add("Extract At");
            modeCombo.SelectedIndex = 0;

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            modeRow.Children.Add(new TextBlock
            {
                Text = "Mode:",
                FontSize = 11,
                Width = LW,
                VerticalAlignment = VerticalAlignment.Center,
            });
            modeRow.Children.Add(modeCombo);

            // ── Axis selector ─────────────────────────────────────────────────
            var axisCombo = new ComboBox
            {
                Width = 140,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
            };
            foreach (var a in axes)
                axisCombo.Items.Add(a.Name);
            if (axisCombo.Items.Count > 0)
                axisCombo.SelectedIndex = 0;

            var axisRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            axisRow.Children.Add(new TextBlock
            {
                Text = "Axis:",
                FontSize = 11,
                Width = LW,
                VerticalAlignment = VerticalAlignment.Center,
            });
            axisRow.Children.Add(axisCombo);

            // ── Description label ─────────────────────────────────────────────
            var descLabel = new TextBlock
            {
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                MaxWidth = 260,
                Margin = new Thickness(0, 2, 0, 0),
            };

            void UpdateDesc()
            {
                string axisName = axisCombo.SelectedItem as string ?? "";
                var otherAxes = axes.Where(a => !string.Equals(a.Name, axisName, StringComparison.OrdinalIgnoreCase)).Select(a => $"{a.Name}={a.Index}").ToList();
                string others = otherAxes.Count > 0 ? string.Join(", ", otherAxes) : "—";

                if (modeCombo.SelectedIndex == 0) // Extract Along
                    descLabel.Text = $"Extracts a 1-D slice along {axisName} at the current position of all other axes ({others}).";
                else // Extract At
                    descLabel.Text = $"Fixes {axisName} at index {(axisCombo.SelectedItem != null ? axes.FindAxis(axisName)?.Index : "—")} and returns the remaining dimensions as a hyperstack.";
            }

            modeCombo.SelectionChanged += (_, _) => UpdateDesc();
            axisCombo.SelectionChanged += (_, _) => UpdateDesc();
            UpdateDesc();

            // ── Assemble ──────────────────────────────────────────────────────
            var mainContent = new StackPanel { Spacing = 6 };
            mainContent.Children.Add(modeRow);
            mainContent.Children.Add(axisRow);
            mainContent.Children.Add(descLabel);

            FinalizeContent(mainContent, onOk: () =>
            {
                var mode = modeCombo.SelectedIndex == 0 ? ExtractMode.Along : ExtractMode.At;
                string axisName = axisCombo.SelectedItem as string ?? axes[0].Name;
                Close(Answer(new ExtractDimensionParameters(mode, axisName)));
            });
        }
    }
}
