using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog warning that switching an axis to Composite mode will discard its physical
    /// scale (Min/Max/Unit/Step) -- Composite promotes the axis to a <see cref="ColorAxis"/>,
    /// which is always index-based (see Tests.Documents/Working/ColorCoded/
    /// ColorCoded_View_InitialDesign.md section 3.3.7). Shown only for an axis that is not already
    /// index-based; a plain Channel axis (already index-based) has nothing to lose and never
    /// triggers this.
    /// </summary>
    internal sealed class ScaleLossConfirmDialog : Window
    {
        private ScaleLossConfirmDialog(string axisName)
        {
            Title = "Switch to Composite Mode";
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var msg = new TextBlock
            {
                Text = $"\"{axisName}\" has its own scale (Min/Max/Unit). Switching it to Composite " +
                       "mode replaces it with a plain 0..N channel index, and the original scale is " +
                       "lost once you save (it can still be undone by reverting within this session).\n\n" +
                       "Continue?",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
                Margin = new Thickness(0, 0, 0, 12),
            };

            var continueBtn = new Button
            {
                Content = "Continue",
                Width = 90,
                Height = 26,
                FontSize = 11,
                IsDefault = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 72,
                Height = 26,
                FontSize = 11,
                IsCancel = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            continueBtn.Click += (_, _) => Close(true);
            cancelBtn.Click += (_, _) => Close(false);

            Content = new StackPanel
            {
                Margin = new Thickness(16, 12, 16, 12),
                Spacing = 0,
                Children =
                {
                    msg,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { continueBtn, cancelBtn },
                    },
                },
            };
        }

        /// <summary>Shows the dialog modally. Returns <c>true</c> only if the user chose Continue.</summary>
        public static async Task<bool> ShowAsync(Window owner, string axisName)
        {
            var dlg = new ScaleLossConfirmDialog(axisName);
            return await dlg.ShowDialog<bool>(owner);
        }
    }
}
