using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog confirming a manual full min/max scan requested via
    /// <see cref="MxPlot.UI.Avalonia.Controls.ValueRangeBar.FullScanRequested"/> -- offered for
    /// backends where <see cref="MxPlot.Core.IMatrixData.IsVirtual"/> is <see langword="true"/>
    /// (MMF, which never auto-scans, and a still-filling Lazy-decode backend). Unlike the automatic
    /// background scan that runs once a Lazy-decode backend finishes caching (cheap: the data is
    /// already resident), this can mean reading an entire large file from disk, so it is opt-in.
    /// </summary>
    internal sealed class FullValueRangeScanConfirmDialog : Window
    {
        private FullValueRangeScanConfirmDialog()
        {
            Title = "Scan Value Range";
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MinWidth = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var msg = new TextBlock
            {
                Text = "Scan every frame for the exact min/max? This reads the whole file and may take a while.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 260,
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
        public static async Task<bool> ShowAsync(Window owner)
        {
            var dlg = new FullValueRangeScanConfirmDialog();
            return await dlg.ShowDialog<bool>(owner);
        }
    }
}
