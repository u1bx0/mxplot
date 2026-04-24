using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Modal dialog for configuring the animation timer interval (ms).
    /// Show with <c>await ShowAsync(owner, currentIntervalMs)</c>.
    /// Returns the new interval in ms, or <c>null</c> when the user cancels.
    /// </summary>
    internal sealed class AnimationIntervalDialog : Window
    {
        private readonly NumericUpDown _nud;
        private readonly TextBlock _fpsLabel;
        private readonly int _fallback;

        private AnimationIntervalDialog(int currentInterval)
        {
            _fallback = currentInterval;

            Title                  = "Animation Settings";
            CanResize              = true;
            SizeToContent          = SizeToContent.Height;
            Width                  = 220;
            MinWidth               = 200;
            MinHeight              = 100;
            WindowStartupLocation  = WindowStartupLocation.CenterOwner;

            var messageLabel = new TextBlock
            {
                Text       = "Input animation interval [ms]",
                FontSize   = 11,
                Margin     = new Thickness(0, 0, 0, 6),
            };

            _nud = new NumericUpDown
            {
                Minimum                    = 1,
                Maximum                    = 10000,
                Value                      = currentInterval,
                Increment                  = 10,
                Width                      = 120,
                Height                     = 20,
                MinHeight                  = 0,
                FontSize                   = 11,
                VerticalAlignment          = VerticalAlignment.Center,
                VerticalContentAlignment   = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment        = HorizontalAlignment.Left,
                Padding                    = new Thickness(4, 0),
            };
            _nud.Classes.Add("compact");

            _fpsLabel = new TextBlock
            {
                Text     = FormatFps(currentInterval),
                FontSize = 11,
                Opacity  = 0.75,
                Margin   = new Thickness(0, 4, 0, 0),
            };

            _nud.ValueChanged += (_, _) =>
                _fpsLabel.Text = FormatFps((int)(_nud.Value ?? _fallback));

            var okBtn = new Button
            {
                Content                    = "OK",
                Width                      = 70,
                Height                     = 26,
                FontSize                   = 11,
                IsDefault                  = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            var cancelBtn = new Button
            {
                Content                    = "Cancel",
                Width                      = 70,
                Height                     = 26,
                FontSize                   = 11,
                IsCancel                   = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };

            okBtn.Click     += (_, _) => Close((int)(_nud.Value ?? _fallback));
            cancelBtn.Click += (_, _) => Close();

            Content = new StackPanel
            {
                Margin   = new Thickness(16, 12, 16, 12),
                Spacing  = 0,
                Children =
                {
                    messageLabel,
                    _nud,
                    _fpsLabel,
                    new StackPanel
                    {
                        Orientation         = Orientation.Horizontal,
                        Spacing             = 8,
                        Margin              = new Thickness(0, 8, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children            = { okBtn, cancelBtn },
                    }
                }
            };
        }

        private static string FormatFps(int intervalMs)
        {
            double fps = intervalMs > 0 ? 1000.0 / intervalMs : 0;
            return $"(Frame rate = {fps:F1} fps)";
        }

        /// <summary>
        /// Shows the interval-setting dialog as a modal child of <paramref name="owner"/>.
        /// Returns the chosen interval in ms, or <c>null</c> if cancelled.
        /// </summary>
        public static async Task<int?> ShowAsync(Window owner, int currentInterval)
        {
            var dlg = new AnimationIntervalDialog(currentInterval);
            return await dlg.ShowDialog<int?>(owner);
        }
    }
}
