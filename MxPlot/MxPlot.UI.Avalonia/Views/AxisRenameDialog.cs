using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace MxPlot.UI.Avalonia.Views
{
    internal sealed class AxisRenameDialog : Window
    {
        private readonly TextBox _inputBox;
        public string? Result { get; private set; }

        public AxisRenameDialog(string currentName)
        {
            Title = "Rename Axis";
            Width = 290;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 11;

            _inputBox = new TextBox
            {
                Text = currentName,
                Width = 150,
                Height = 24,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };
            _inputBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Return) { Commit(); e.Handled = true; }
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
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 14, 0, 10),
            };
            inputRow.Children.Add(new TextBlock
            {
                Text = "New name:",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            });
            inputRow.Children.Add(_inputBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(16, 0, 16, 14),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);

            Content = new StackPanel { Children = { inputRow, btnRow } };
            Opened += (_, _) => { _inputBox.Focus(); _inputBox.SelectAll(); };
        }

        private void Commit()
        {
            var text = _inputBox.Text?.Trim();
            if (!string.IsNullOrEmpty(text)) Result = text;
            Close();
        }
    }
}
