using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog warning that Composite settings (recipes: colors, ranges, gains) saved for one
    /// axis will be discarded when Composite is switched to a different axis -- the
    /// <c>mxplot.composite.*</c> metadata keys are not namespaced per axis, so
    /// <c>RemoveCompositeKeys</c> unconditionally clears the prior
    /// axis's entries before the new axis's recipes are written. Shown only when saved metadata
    /// names an axis different from the one about to be composited; a fresh axis (no saved
    /// Composite metadata yet) or re-entering the same saved axis never triggers this.
    /// </summary>
    internal sealed class CompositeAxisOverwriteConfirmDialog : Window
    {
        private CompositeAxisOverwriteConfirmDialog(string oldAxisName, string newAxisName)
        {
            Title = "Switch Composite Axis";
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var msg = new TextBlock
            {
                Text = $"\"{oldAxisName}\" already has saved Composite settings (colors, ranges, " +
                       $"gains). Switching Composite to \"{newAxisName}\" will discard them.\n\n" +
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
        public static async Task<bool> ShowAsync(Window owner, string oldAxisName, string newAxisName)
        {
            var dlg = new CompositeAxisOverwriteConfirmDialog(oldAxisName, newAxisName);
            return await dlg.ShowDialog<bool>(owner);
        }
    }
}
