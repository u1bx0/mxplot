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
    /// Modal dialog for configuring a Normalize operation.
    /// Returns a <see cref="NormalizeParameters"/> record on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class NormalizeDialog : ProcessingDialogBase
    {
        /// <summary>Parameters collected from the dialog.</summary>
        internal sealed record NormalizeParameters(
            double Target,
            NormalizeScope Scope,
            bool ThisFrameOnly,
            bool ReplaceData);

        private NormalizeParameters? _result;

        // ── Factory ───────────────────────────────────────────────────────────

        internal static Task<NormalizeParameters?> ShowAsync(
            Window owner, bool isMultiFrame, bool isVirtual)
        {
            var dlg = new NormalizeDialog(isMultiFrame, isVirtual);
            return dlg.ShowDialog<NormalizeParameters?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private NormalizeDialog(bool isMultiFrame, bool isVirtual) : base("Normalize")
        {
            // ── "Normalize to:" row ───────────────────────────────────────────
            var targetNud = ControlFactory.MakeNumericUpDown(100m, 0.001m, 1_000_000m, 1m, width: 80);
            targetNud.FormatString = "G6";
            var targetRow = ControlFactory.MakeNudRow("Normalize to:", targetNud, labelWidth: 84);

            // ── Scope radio buttons ───────────────────────────────────────────
            const string group = "NormScope";
            var perFrameRadio = new RadioButton
            {
                Content = "Per frame",
                GroupName = group,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                IsChecked = true,
            };
            perFrameRadio.Classes.Add("compact");
            ToolTip.SetTip(perFrameRadio, "Each frame is normalized independently using its own maximum");
            
            var globalRadio = new RadioButton
            {
                Content = "Global (entire dataset)",
                GroupName = group,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
            };
            globalRadio.Classes.Add("compact");
            ToolTip.SetTip(globalRadio, "All frames are normalized using the single maximum found across the entire dataset");

            // ── Virtual data warning (Global scope only) ──────────────────────
            var warnIcon = new TextBlock
            {
                Text = "\u26a0",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#E57373")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            var warnText = new TextBlock
            {
                Text = "Virtual data: scanning all frames may take time.",
                FontSize = 10,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var warnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(18, 2, 0, 0),
                IsVisible = false,
            };
            warnRow.Children.Add(warnIcon);
            warnRow.Children.Add(warnText);

            if (isVirtual)
            {
                globalRadio.IsCheckedChanged += (_, _) =>
                    warnRow.IsVisible = globalRadio.IsChecked == true;
            }

            var scopePanel = new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 0) };
            scopePanel.Children.Add(perFrameRadio);
            scopePanel.Children.Add(globalRadio);
            scopePanel.Children.Add(warnRow);

            // ── "This frame only" checkbox (multi-frame only) ─────────────────
            var thisFrameCheck = ControlFactory.MakeCheckBox(
                "This frame only",
                hint: "Normalize only the currently active frame");
            thisFrameCheck.Margin = new Thickness(0, 4, 0, -7);
            thisFrameCheck.IsVisible = isMultiFrame;

            // disable scope radios when "This frame only" is checked
            thisFrameCheck.IsCheckedChanged += (_, _) =>
            {
                bool single = thisFrameCheck.IsChecked == true;
                perFrameRadio.IsEnabled = !single;
                globalRadio.IsEnabled = !single;
                if (single) warnRow.IsVisible = false;
            };

            // ── Assemble main content ─────────────────────────────────────────
            var main = new StackPanel { Spacing = 2 };
            main.Children.Add(targetRow);
            main.Children.Add(scopePanel);
            main.Children.Add(thisFrameCheck);

            FinalizeContent(main, onOk: () =>
            {
                _result = new NormalizeParameters(
                    Target: (double)(targetNud.Value ?? 100m),
                    Scope: globalRadio.IsChecked == true ? NormalizeScope.Global : NormalizeScope.PerFrame,
                    ThisFrameOnly: thisFrameCheck.IsChecked == true,
                    ReplaceData: ReplaceDataCheckBox.IsChecked == true);
                Close(_result);
            }, okLabel: "Apply");
        }
    }
}
