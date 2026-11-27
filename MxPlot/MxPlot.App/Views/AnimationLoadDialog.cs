using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Extensions.Images;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Modal dialog for an animated image whose expanded pixel data is large: load every frame, or the
    /// first frame only.
    /// </summary>
    internal class AnimationLoadDialog : Window
    {
        private AnimationLoadDialog(string fileName, AnimationInfo info, long availableBytes)
        {
            Title = "Large Animation";
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MinWidth = 400;

            var nameLabel = new TextBlock
            {
                Text = fileName,
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380
            };

            var infoLabel = new TextBlock
            {
                Text = $"{info.FrameCount} frames of {info.Width} × {info.Height}. " +
                       $"Loading every frame takes about {FormatBytes(info.EstimatedBytes)} of memory " +
                       $"(this computer has {FormatBytes(availableBytes)}).",
                FontSize = 11,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Margin = new Thickness(0, 2, 0, 4)
            };

            var warningLabel = new TextBlock
            {
                Text = "That is more than the memory available: loading all frames may take very long or fail.",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.Parse("#E57373")),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Margin = new Thickness(0, 0, 0, 4),
                IsVisible = info.EstimatedBytes > availableBytes
            };

            var allBtn = MakeButton("Load All Frames");
            ToolTip.SetTip(allBtn, "Reads every frame into memory.");
            var firstBtn = MakeButton("First Frame Only");
            ToolTip.SetTip(firstBtn, "Reads only the first frame, as a still image.");
            var cancelBtn = MakeButton("Cancel");

            allBtn.Click += (_, _) => Close(ImageLoader.AnimationReadMode.AllFrames);
            firstBtn.Click += (_, _) => Close(ImageLoader.AnimationReadMode.FirstFrameOnly);
            cancelBtn.Click += (_, _) => Close();

            Content = new StackPanel
            {
                Margin = new Thickness(24, 20, 24, 20),
                Spacing = 0,
                Children =
                {
                    nameLabel,
                    infoLabel,
                    warningLabel,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(0, 8, 0, 0),
                        Children = { allBtn, firstBtn, cancelBtn },
                        HorizontalAlignment = HorizontalAlignment.Center,
                    }
                }
            };
        }

        private static Button MakeButton(string text) => new()
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(12, 2),
            Height = 28,
            Width = 120,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };

        private static string FormatBytes(long bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            if (gb >= 1.0)
                return $"{gb:F1} GB";
            return $"{bytes / (1024.0 * 1024.0):F0} MB";
        }

        /// <summary>
        /// Shows the dialog as a modal child of <paramref name="owner"/>. Returns the chosen read mode
        /// (<c>AllFrames</c> or <c>FirstFrameOnly</c>), or <c>null</c> if cancelled.
        /// </summary>
        public static async Task<ImageLoader.AnimationReadMode?> ShowAsync(
            Window owner, string fileName, AnimationInfo info, long availableBytes)
        {
            var dlg = new AnimationLoadDialog(fileName, info, availableBytes);
            return await dlg.ShowDialog<ImageLoader.AnimationReadMode?>(owner);
        }
    }
}
