using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.OpenGL;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Non-modal property editor for <see cref="TextObject"/>.
    /// All changes are applied live to the target object; close the window to dismiss.
    /// </summary>
    internal sealed class TextEditDialog : Window
    {
        private static readonly string[] _fontFamilies =
        [
            "Arial", "Calibri", "Consolas", "Courier New", "Georgia",
            "Segoe UI", "Tahoma", "Times New Roman", "Verdana",
        ];

        private readonly TextObject _target;
        private readonly Action _redraw;

        public TextEditDialog(TextObject target, Action redraw)
        {
            _target = target;
            _redraw = redraw;

            Title = "Text Properties";
            Width = 310;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            Content = BuildContent();
        }

        private Control BuildContent()
        {
            // ── Text input ────────────────────────────────────────────────────
            var textBox = new TextBox
            {
                Text = _target.Text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 72,
                FontSize = 11,
                Padding = new Thickness(4),
            };
            textBox.TextChanged += (_, _) => { _target.Text = textBox.Text ?? ""; _redraw(); };

            // ── Font family ───────────────────────────────────────────────────
            var fontCombo = new ComboBox
            {
                Width = 140,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var f in _fontFamilies) fontCombo.Items.Add(f);
            int fi = Array.IndexOf(_fontFamilies, _target.FontFamily);
            fontCombo.SelectedIndex = fi >= 0 ? fi : 0;
            fontCombo.SelectionChanged += (_, _) =>
            {
                if (fontCombo.SelectedItem is string f) { _target.FontFamily = f; _redraw(); }
            };

            // ── Font size ─────────────────────────────────────────────────────
            var sizeNud = ControlFactory.MakeNumericUpDown(
                (decimal)_target.FontSize, 1m, 120m, 1m, width: 62);
            sizeNud.ValueChanged += (_, _) =>
            {
                if (sizeNud.Value is { } v) { _target.FontSize = (double)v; _redraw(); }
            };

            var fontRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 4, 0, 0),
            };
            fontRow.Children.Add(fontCombo);
            fontRow.Children.Add(new TextBlock
            {
                Text = "Size:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            fontRow.Children.Add(sizeNud);

            // ── Color row ─────────────────────────────────────────────────────
            var textColorSwatch = ControlFactory.MakeColorSwatch(_target.PenColor,
                c => { _target.PenColor = c; _redraw(); });
            textColorSwatch.BorderBrush = Brushes.Gray;
            textColorSwatch.BorderThickness = new Thickness(1);

            var bgColorSwatch = ControlFactory.MakeColorSwatch(_target.BackgroundColor,
                c => { _target.BackgroundColor = c; _redraw(); });
            bgColorSwatch.IsEnabled = _target.ShowBackground;
            bgColorSwatch.BorderBrush = Brushes.Gray;
            bgColorSwatch.BorderThickness = new Thickness(1);

            var showBgCheck = ControlFactory.MakeCheckBox("Background:");
            showBgCheck.IsChecked = _target.ShowBackground;
            showBgCheck.IsCheckedChanged += (_, _) =>
                {
                    _target.ShowBackground = showBgCheck.IsChecked == true;
                    _redraw();
                    bgColorSwatch.IsEnabled = showBgCheck.IsChecked == true;
                };

            var s = showBgCheck.Margin;
            showBgCheck.Margin = new Thickness(s.Left + 23, s.Top, s.Right, s.Bottom);
            var t = bgColorSwatch.Margin;
            bgColorSwatch.Margin = new Thickness(-20, t.Top, t.Right, t.Bottom);
            

            var colorRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
            };
            colorRow.Children.Add(new TextBlock
            {
                Text = "Text:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            colorRow.Children.Add(textColorSwatch);
            colorRow.Children.Add(showBgCheck);
            colorRow.Children.Add(bgColorSwatch);

            // ── Options row (border / zoom sync) ──────────────────────────────
            var showBorderCheck = ControlFactory.MakeCheckBox("Border");
            showBorderCheck.IsChecked = _target.ShowBorder;
            showBorderCheck.IsCheckedChanged += (_, _) =>
                { _target.ShowBorder = showBorderCheck.IsChecked == true; _redraw(); };

            var scaleFontCheck = ControlFactory.MakeCheckBox("Scale font");
            scaleFontCheck.IsChecked = _target.ScaleFontWithZoom;
            scaleFontCheck.IsCheckedChanged += (_, _) =>
                { _target.ScaleFontWithZoom = scaleFontCheck.IsChecked == true; _redraw(); };

            var optionsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 4, 0, 0),
            };
            optionsRow.Children.Add(showBorderCheck);
            optionsRow.Children.Add(scaleFontCheck);

            // ── Close button ──────────────────────────────────────────
            var closeBtn = new Button
            {
                Content = "Close",
                FontSize = 11,
                Height = 24,
                MinHeight = 0,
                Padding = new Thickness(16, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            };
            closeBtn.Click += (_, _) => Close();

            // ── Root ──────────────────────────────────────────────────────────
            var root = new StackPanel { Margin = new Thickness(10), Spacing = 0 };
            var infoIcon = new PathIcon
            {
                Data = MenuIcons.Info,
                Width = 13,
                Height = 13,
                Foreground = Brushes.SteelBlue,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(infoIcon,
                "Format syntax:\n" +
                "{i}     = index of axis 0\n" +
                "{p}     = position of axis 0\n" +
                "{N:i}   = index of axis N\n" +
                "{N:p}   = position of axis N\n" +
                "{N:pFn} = position of axis N (n decimal places)");
            var textLabelRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 0, 0, 2),
                Children = { new TextBlock { Text = "Text:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center }, infoIcon },
            };
            root.Children.Add(textLabelRow);
            root.Children.Add(textBox);
            root.Children.Add(new TextBlock
            {
                Text = "Font:",
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
            });
            root.Children.Add(fontRow);
            root.Children.Add(colorRow);
            root.Children.Add(optionsRow);
            root.Children.Add(closeBtn);
            return root;
        }

    }
}
