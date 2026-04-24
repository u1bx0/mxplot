using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core.IO;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Modal dialog that asks the user to choose between InMemory and Virtual loading for large files.
    /// </summary>
    internal class LoadingModeDialog : Window
    {
        private LoadingModeDialog(string fileName, long fileBytes)
        {
            Title = "Select Loading Mode";
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MinWidth = 400;

            double sizeMb = fileBytes / (1024.0 * 1024.0);

            var sizeLabel = new TextBlock
            {
                Text = fileName,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380
            };

            var infoLabel = new TextBlock
            {
                Text = $"File size: {sizeMb:F0} MB \u2014 select a loading mode:",
                FontSize = 11,
                Opacity = 0.75,
                Margin = new Thickness(0, 2, 0, 8)
            };

            var inMemoryBtn = new Button
            {
                Content = "Load in Memory",
                Padding = new Thickness(12, 2),
                FontSize = 11,
                Height = 42,
                Width = 120,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            ToolTip.SetTip(inMemoryBtn, "Loads all frames into RAM. Fast random access; high memory usage.");

            var virtualBtn = new Button
            {
                Content = "Virtual Mode",
                FontSize = 11,
                Padding = new Thickness(12, 2),
                Height = 42,
                Width = 120,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(virtualBtn, "Reads frames from disk on demand. Low memory usage.");

            var cancelBtn = new Button
            {
                Content = "Cancel",
                FontSize = 11,
                Padding = new Thickness(12, 2),
                Height = 42,
                Width = 120,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };

            inMemoryBtn.Click += (_, _) => Close(LoadingMode.InMemory);
            virtualBtn.Click += (_, _) => Close(LoadingMode.Virtual);
            cancelBtn.Click += (_, _) => Close();

            Content = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 20),
                Spacing = 0,
                Children =
                {
                    sizeLabel,
                    infoLabel,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { inMemoryBtn, virtualBtn, cancelBtn },
                        HorizontalAlignment = HorizontalAlignment.Center,
                    }
                }
            };
        }

        /// <summary>
        /// Shows the mode-selection dialog as a modal child of <paramref name="owner"/>.
        /// Returns the chosen <see cref="LoadingMode"/>, or <c>null</c> if cancelled.
        /// </summary>
        public static async Task<LoadingMode?> ShowAsync(Window owner, string fileName, long fileBytes)
        {
            var dlg = new LoadingModeDialog(fileName, fileBytes);
            return await dlg.ShowDialog<LoadingMode?>(owner);
        }
    }
}
