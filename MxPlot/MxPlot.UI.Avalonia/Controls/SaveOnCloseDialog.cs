using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Controls
{
    internal enum SaveOnCloseResult { Save, Discard }

    /// <summary>
    /// Modal dialog shown when the user closes a <c>MatrixPlotter</c> with unsaved changes.
    /// Returns <see cref="SaveOnCloseResult.Save"/>, <see cref="SaveOnCloseResult.Discard"/>,
    /// or <c>null</c> (Cancel / window closed without choosing).
    /// </summary>
    internal sealed class SaveOnCloseDialog : Window
    {
        private SaveOnCloseDialog(string? title, bool isReadOnly)
        {
            Title = "Unsaved Changes";
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            string subject = string.IsNullOrEmpty(title) ? "this window" : $"\"{title}\"";
            string msgText = isReadOnly
                ? $"The source file of {subject} is read-only and cannot be overwritten.\nSave a copy to a new file, or discard changes?"
                : $"Save changes to {subject}?";
            var msg = new TextBlock
            {
                Text = msgText,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 300,
                Margin = new Thickness(0, 0, 0, 12),
            };

            var saveBtn = new Button
            {
                Content = isReadOnly ? "Save a Copy\u2026" : "Save",
                Width = isReadOnly ? 100 : 72,
                Height = 26,
                FontSize = 11,
                IsDefault = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var discardBtn = new Button
            {
                Content = "Discard",
                Width = 72,
                Height = 26,
                FontSize = 11,
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

            saveBtn.Click += (_, _) => Close(SaveOnCloseResult.Save);
            discardBtn.Click += (_, _) => Close(SaveOnCloseResult.Discard);
            cancelBtn.Click += (_, _) => Close();

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
                        Children = { saveBtn, discardBtn, cancelBtn },
                    },
                },
            };
        }

        /// <summary>
        /// Shows the dialog modally. Returns <c>null</c> if the user cancelled or closed the window.
        /// </summary>
        /// <param name="isReadOnly">
        /// When <c>true</c> the source file is read-only; the Save button becomes "Save a Copy…"
        /// and the message is adjusted to indicate a copy will be saved instead of overwriting.
        /// </param>
        public static async Task<SaveOnCloseResult?> ShowAsync(Window owner, string? windowTitle, bool isReadOnly = false)
        {
            var dlg = new SaveOnCloseDialog(windowTitle, isReadOnly);
            return await dlg.ShowDialog<SaveOnCloseResult?>(owner);
        }
    }
}
