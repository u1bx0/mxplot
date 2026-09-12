using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for reversing the frame order along one axis or all frames.
    /// Returns a <see cref="ReverseStackParameters"/> on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class ReverseStackDialog : ProcessingDialogBase
    {
        internal sealed record ReverseStackParameters(
            string? AxisName,
            bool ReplaceData);

        internal static Task<ReverseStackParameters?> ShowAsync(Window owner, IReadOnlyList<Axis> axes, bool isLinkWindow = false, IMatrixData? src = null)
        {
            var dlg = new ReverseStackDialog(axes, isLinkWindow, src);
            return dlg.ShowDialog<ReverseStackParameters?>(owner);
        }

        private ReverseStackDialog(IReadOnlyList<Axis> axes, bool isLinkWindow, IMatrixData? src)
            : base("Reverse Stack", isLinkWindow: isLinkWindow, src: src)
        {
            const double LW = 80;

            // ── Axis selector ─────────────────────────────────────────────────
            var axisCombo = new ComboBox
            {
                Width = 120,
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

            // ── Reverse all frames ────────────────────────────────────────────
            // With a single axis, reversing "all frames" (raw frame order) and reversing along
            // that one axis are the same operation (see ReverseStackOperation.Execute) -- the
            // checkbox would just be a confusing second way to say the same thing, so it is
            // hidden entirely rather than merely disabled, mirroring how CreateProjectionDialog
            // hides "This position only" when there are no surviving axes to distinguish.
            bool hasMultipleAxes = axes.Count > 1;
            var allAxesChk = ControlFactory.MakeCheckBox(
                "Reverse all frames",
                hint: "Ignore the axis selection and reverse the entire frame sequence");
            allAxesChk.Margin = new Thickness(0, 2, 0, -7);
            allAxesChk.IsVisible = hasMultipleAxes;
            allAxesChk.IsCheckedChanged += (_, _) =>
                axisRow.IsEnabled = allAxesChk.IsChecked != true;

            // ── Assemble and finalize ─────────────────────────────────────────
            var mainContent = new StackPanel { Spacing = 4 };
            mainContent.Children.Add(axisRow);
            mainContent.Children.Add(allAxesChk);

            FinalizeContent(mainContent, onOk: () =>
            {
                bool allAxes = allAxesChk.IsChecked == true;
                string? axisName = allAxes ? null : axisCombo.SelectedItem as string;
                bool replace = ReplaceDataCheckBox.IsChecked == true;
                Close(new ReverseStackParameters(axisName, replace));
            });
        }
    }
}
