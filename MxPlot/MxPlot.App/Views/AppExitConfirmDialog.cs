using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Modal dialog shown when the user exits MxPlot with one or more windows having unsaved changes.
    /// Returns <c>true</c> if the user chose "Don't Save" (proceed with exit),
    /// or <c>false</c> / <c>null</c> if the user cancelled.
    /// </summary>
    internal sealed class AppExitConfirmDialog : Window
    {
        private AppExitConfirmDialog(IReadOnlyList<string> titles)
        {
            Title = "Unsaved Changes";
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 280;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            string countText = titles.Count == 1
                ? "1 window has unsaved changes."
                : $"{titles.Count} windows have unsaved changes.";

            var msgBlock = new TextBlock
            {
                Text = countText,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8),
            };

            var listPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 14) };
            foreach (var t in titles)
                listPanel.Children.Add(new TextBlock
                {
                    Text = "\u25cf  " + t,
                    FontSize = 11,
                    Opacity = 0.75,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 320,
                });

            var dontSaveBtn = new Button
            {
                Content = "Don't Save",
                Width = 88,
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
                IsDefault = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            dontSaveBtn.Click += (_, _) => Close(true);
            cancelBtn.Click += (_, _) => Close(false);

            Content = new StackPanel
            {
                Margin = new Thickness(16, 12, 16, 12),
                Children =
                {
                    msgBlock,
                    listPanel,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { dontSaveBtn, cancelBtn },
                    },
                },
            };
        }

        /// <summary>
        /// Shows the dialog modally.
        /// Returns <c>true</c> if the user chose "Don't Save", <c>false</c> or <c>null</c> if cancelled.
        /// </summary>
        public static async Task<bool> ShowAsync(Window owner, IReadOnlyList<string> unsavedTitles)
        {
            var dlg = new AppExitConfirmDialog(unsavedTitles);
            return await dlg.ShowDialog<bool>(owner);
        }
    }
}
