using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Minimal modal dialog for entering a custom zoom percentage.
    /// Show with <c>await dlg.ShowDialog(ownerWindow)</c> then read <see cref="Result"/>.
    /// </summary>
    internal sealed class ZoomDialog : Window
    {
        private readonly TextBox _inputBox;

        /// <summary>Zoom factor (e.g. 2.0 = 200 %). <c>null</c> when the user cancelled.</summary>
        public double? Result { get; private set; }

        public ZoomDialog(double currentZoom)
        {
            Title = "Set Zoom";
            Width = 230;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 11;

            _inputBox = new TextBox
            {
                Text = $"{currentZoom * 100:0}",
                Width = 110,
                Height = 24,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };

            _inputBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };

            var okBtn = new Button
            {
                Content = "OK",
                Width = 72,
                Height = 28,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            okBtn.Click += (_, _) => Commit();

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 72,
                Height = 28,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            cancelBtn.Click += (_, _) => Close();

            var inputRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 10),
            };
            inputRow.Children.Add(new TextBlock
            {
                Text = "Zoom (%):",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            });
            inputRow.Children.Add(_inputBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 14),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);

            var root = new StackPanel();
            root.Children.Add(inputRow);
            root.Children.Add(btnRow);
            Content = root;

            // Select all + focus once the window is visible
            Opened += (_, _) =>
            {
                _inputBox.SelectAll();
                _inputBox.Focus();
            };
        }

        private void Commit()
        {
            if (double.TryParse(_inputBox.Text, NumberStyles.Any,
                                CultureInfo.CurrentCulture, out double pct) && pct > 0)
            {
                Result = Math.Clamp(pct / 100.0, 0.01, 64.0);
                Close();
            }
        }
    }
}
