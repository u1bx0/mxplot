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
    /// Modal dialog for configuring a Log Transform operation.
    /// Returns a <see cref="LogTransformParameters"/> record on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class LogTransformDialog : Window
    {
        internal sealed record LogTransformParameters(
            LogBase Base,
            NegativeHandling Handling,
            bool ThisFrameOnly,
            bool SyncSource,
            bool ReplaceData);

        private LogTransformParameters? _result;

        // ── Factory ───────────────────────────────────────────────────────────

        /// <param name="isMultiFrame">Whether the source data has more than one frame.</param>
        /// <param name="hasNegOrZero">Whether the current frame contains non-positive values.</param>
        internal static Task<LogTransformParameters?> ShowAsync(
            Window owner, bool isMultiFrame, bool hasNegOrZero)
        {
            var dlg = new LogTransformDialog(isMultiFrame, hasNegOrZero);
            return dlg.ShowDialog<LogTransformParameters?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private LogTransformDialog(bool isMultiFrame, bool hasNegOrZero)
        {
            Title = "Log Transform";
            Width = 290;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            // ── Base selector ─────────────────────────────────────────────────
            var baseCombo = new ComboBox
            {
                Width = 120,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
            };
            baseCombo.Items.Add("Natural (ln)");
            baseCombo.Items.Add("Log\u2081\u2080");
            baseCombo.Items.Add("Log\u2082");
            baseCombo.SelectedIndex = 1;
            var baseRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            baseRow.Children.Add(new TextBlock
            {
                Text = "Base:",
                FontSize = 11,
                Width = 44,
                VerticalAlignment = VerticalAlignment.Center,
            });
            baseRow.Children.Add(baseCombo);

            // ── Negative value warning + handling ─────────────────────────────
            var warnBorder = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse("#E57373")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 6, 0, 0),
                IsVisible = hasNegOrZero,
            };
            var warnInner = new StackPanel { Spacing = 4 };

            var warnHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            warnHeader.Children.Add(new TextBlock
            {
                Text = "\u26a0",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#E57373")),
                VerticalAlignment = VerticalAlignment.Center,
            });
            warnHeader.Children.Add(new TextBlock
            {
                Text = "Data contains non-positive values.",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            warnInner.Children.Add(warnHeader);

            const string negGroup = "LogNegHandling";
            var shiftRadio = new RadioButton
            {
                Content = "Shift (+|min|+\u03b5 per frame)",
                GroupName = negGroup,
                FontSize = 11,
                IsChecked = true,
                MinHeight = 0,
                Height = 20,
            };
            shiftRadio.Classes.Add("compact");
            ToolTip.SetTip(shiftRadio,
                "Add |frameMin| + \u03b5 to each value before log. Minimum maps to log(\u03b5).");

            var clampRadio = new RadioButton
            {
                Content = "Clamp to \u03b5",
                GroupName = negGroup,
                FontSize = 11,
                MinHeight = 0,
                Height = 20,
            };
            clampRadio.Classes.Add("compact");
            ToolTip.SetTip(clampRadio,
                "Clamp values \u2264 0 to \u03b5 (1e-10) before log. Negative values map to log(\u03b5).");

            var handlingLabel = new TextBlock
            {
                Text = "Negative handling:",
                FontSize = 11,
                Opacity = 0.7,
            };
            warnInner.Children.Add(handlingLabel);
            warnInner.Children.Add(shiftRadio);
            warnInner.Children.Add(clampRadio);
            warnBorder.Child = warnInner;

            // ── This frame only / Sync source ────────────────────────────────
            var thisFrameCheck = ControlFactory.MakeCheckBox(
                "This frame only",
                hint: "Apply log transform only to the currently active frame");
            thisFrameCheck.Margin = new Thickness(0, 6, 0, -7);
            thisFrameCheck.IsVisible = isMultiFrame;

            var syncCheck = ControlFactory.MakeCheckBox(
                "Sync source data",
                hint: "Automatically re-apply the transform when the source frame changes");
            syncCheck.Margin = new Thickness(0, 4, 0, -7);
            syncCheck.IsVisible = !isMultiFrame; // single-frame: always show; multi: show when ThisFrameOnly

            if (isMultiFrame)
            {
                thisFrameCheck.IsCheckedChanged += (_, _) =>
                {
                    bool single = thisFrameCheck.IsChecked == true;
                    syncCheck.IsVisible = single;
                    if (!single) syncCheck.IsChecked = false;
                };
            }

            // ── Replace data ──────────────────────────────────────────────────
            var replaceCheck = ControlFactory.MakeCheckBox(
                "Replace data",
                hint: "Overwrite the current window instead of opening a new one");
            replaceCheck.Margin = new Thickness(0, 4, 0, -7);

            // sync and replace are mutually exclusive
            syncCheck.IsCheckedChanged += (_, _) =>
            {
                if (syncCheck.IsChecked == true) replaceCheck.IsChecked = false;
                replaceCheck.IsEnabled = syncCheck.IsChecked != true;
            };

            // ── Buttons ───────────────────────────────────────────────────────
            var applyBtn = new Button
            {
                Content = "Apply",
                Width = 80,
                MinHeight = 26,
                Padding = new Thickness(8, 4),
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            applyBtn.Classes.Add("accent");

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 80,
                MinHeight = 26,
                Padding = new Thickness(8, 4),
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 10, 0, 0),
            };
            btnRow.Children.Add(applyBtn);
            btnRow.Children.Add(cancelBtn);

            applyBtn.Click += (_, _) =>
            {
                LogBase lb = baseCombo.SelectedIndex switch
                {
                    1 => LogBase.Log10,
                    2 => LogBase.Log2,
                    _ => LogBase.Natural,
                };
                NegativeHandling nh = clampRadio.IsChecked == true
                    ? NegativeHandling.Clamp
                    : NegativeHandling.Shift;

                _result = new LogTransformParameters(
                    Base: lb,
                    Handling: nh,
                    ThisFrameOnly: thisFrameCheck.IsChecked == true,
                    SyncSource: syncCheck.IsChecked == true,
                    ReplaceData: replaceCheck.IsChecked == true);
                Close(_result);
            };
            cancelBtn.Click += (_, _) => Close(null);

            // ── Layout ────────────────────────────────────────────────────────
            var sep = ControlFactory.MakeSep(new Thickness(0, 6));

            var panel = new StackPanel { Spacing = 2, Margin = new Thickness(16, 14, 16, 14) };
            panel.Children.Add(baseRow);
            panel.Children.Add(warnBorder);
            panel.Children.Add(thisFrameCheck);
            panel.Children.Add(syncCheck);
            panel.Children.Add(sep);
            panel.Children.Add(replaceCheck);
            panel.Children.Add(btnRow);
            Content = panel;
        }
    }
}
