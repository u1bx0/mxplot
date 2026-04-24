using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Controls
{
    internal enum MetaDirtyResult { Save, Discard }

    /// <summary>
    /// Modal dialog shown when the user switches metadata keys with unsaved edits.
    /// Returns <see cref="MetaDirtyResult.Save"/>, <see cref="MetaDirtyResult.Discard"/>,
    /// or <c>null</c> (Cancel / window closed).
    /// </summary>
    internal sealed class MetaDirtyPromptDialog : Window
    {
        private MetaDirtyPromptDialog(string keyName)
        {
            Title                 = "Unsaved Changes";
            CanResize             = false;
            SizeToContent         = SizeToContent.WidthAndHeight;
            MinWidth              = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var msg = new TextBlock
            {
                Text         = $"Save changes to \"{keyName}\"?",
                FontSize     = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 280,
                Margin       = new Thickness(0, 0, 0, 12),
            };

            var saveBtn = new Button
            {
                Content                    = "Save",
                Width                      = 72,
                Height                     = 26,
                FontSize                   = 11,
                IsDefault                  = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var discardBtn = new Button
            {
                Content                    = "Discard",
                Width                      = 72,
                Height                     = 26,
                FontSize                   = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var cancelBtn = new Button
            {
                Content                    = "Cancel",
                Width                      = 72,
                Height                     = 26,
                FontSize                   = 11,
                IsCancel                   = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            saveBtn.Click    += (_, _) => Close(MetaDirtyResult.Save);
            discardBtn.Click += (_, _) => Close(MetaDirtyResult.Discard);
            cancelBtn.Click  += (_, _) => Close();

            Content = new StackPanel
            {
                Margin   = new Thickness(16, 12, 16, 12),
                Spacing  = 0,
                Children =
                {
                    msg,
                    new StackPanel
                    {
                        Orientation         = Orientation.Horizontal,
                        Spacing             = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children            = { saveBtn, discardBtn, cancelBtn },
                    },
                },
            };
        }

        /// <summary>
        /// Shows the dialog modally. Returns <c>null</c> if the user cancelled or closed the window.
        /// </summary>
        public static async Task<MetaDirtyResult?> ShowAsync(Window owner, string keyName)
        {
            var dlg = new MetaDirtyPromptDialog(keyName);
            return await dlg.ShowDialog<MetaDirtyResult?>(owner);
        }
    }
}
