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
    /// Modal dialog for configuring a Normalize operation.
    /// Returns the <see cref="NormalizeParameters"/> and the dialog's checkboxes on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class NormalizeDialog : ProcessingDialogBase
    {
        /// <summary>Parameters collected from the dialog.</summary>
        internal sealed record NormalizeParameters(double Target, NormalizeScope Scope);

        // ── Factory ───────────────────────────────────────────────────────────

        internal static Task<DialogAnswer<NormalizeParameters>?> ShowAsync(
            Window owner, bool isMultiFrame, IMatrixData? src, bool isLinkWindow = false)
        {
            var dlg = new NormalizeDialog(isMultiFrame, src, isLinkWindow);
            return dlg.ShowDialog<DialogAnswer<NormalizeParameters>?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private NormalizeDialog(bool isMultiFrame, IMatrixData? src, bool isLinkWindow)
            : base("Normalize", isLinkWindow: isLinkWindow, src: src, thisFrameOnlyDefault: isMultiFrame ? false : (bool?)null,
                   showSyncSource: true)
        {
            bool isVirtual = src?.IsVirtual == true;
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

            // disable scope radios when "This frame only" is checked
            if (ThisFrameOnlyCheckBox != null)
            {
                ThisFrameOnlyCheckBox.IsCheckedChanged += (_, _) =>
                {
                    bool single = ThisFrameOnlyCheckBox.IsChecked == true;
                    perFrameRadio.IsEnabled = !single;
                    globalRadio.IsEnabled = !single;
                    if (single) warnRow.IsVisible = false;
                };
            }

            // ── Assemble main content ─────────────────────────────────────────
            var main = new StackPanel { Spacing = 2 };
            main.Children.Add(targetRow);
            main.Children.Add(scopePanel);
            var frameOptions = BuildFrameOptions();
            if (frameOptions != null)
                main.Children.Add(frameOptions);

            FinalizeContent(main, onOk: () =>
            {
                Close(Answer(new NormalizeParameters(
                    Target: (double)(targetNud.Value ?? 100m),
                    Scope: globalRadio.IsChecked == true ? NormalizeScope.Global : NormalizeScope.PerFrame)));
            }, okLabel: "Apply");
        }
    }
}
