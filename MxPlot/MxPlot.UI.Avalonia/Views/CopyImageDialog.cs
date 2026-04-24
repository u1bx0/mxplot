using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MxPlot.UI.Avalonia.Helpers;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    internal enum CopyMode { ActualPixelSize, CustomSize, Text }

    internal sealed class CopyResult
    {
        public CopyMode Mode         { get; init; }
        public int      Width        { get; init; }
        public int      Height       { get; init; }
        public bool     WithOverlays { get; init; }
        public string   Separator    { get; init; } = "\t";
    }

    /// <summary>
    /// Modal dialog for selecting clipboard copy options:
    /// image at natural / custom size, or tab-separated text.
    /// </summary>
    internal sealed class CopyImageDialog : Window
    {
        /// <summary>The user's choice. <c>null</c> means the dialog was cancelled.</summary>
        public CopyResult? Result { get; private set; }

        private static CopyMode _lastMode = CopyMode.CustomSize;
        private static bool _lastOverlays = true;

        internal CopyImageDialog(Bitmap? preview, int naturalW, int naturalH, int displayW, int displayH,
                    Func<bool, Bitmap?>? refreshPreview = null)
                {
            Title = "Copy to Clipboard";
            SizeToContent = SizeToContent.Height;
            Width = 400;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            const double FS = 11;
            double aspect = (naturalW > 0 && naturalH > 0) ? (double)naturalW / naturalH : 1.0;

            // ── Preview thumbnail ───────────────────────────────────────────
            const double maxThumbW = 78, maxThumbH = 58;
            double ta   = aspect;
            double imgW = ta >= maxThumbW / maxThumbH ? maxThumbW : maxThumbH * ta;
            double imgH = ta >= maxThumbW / maxThumbH ? maxThumbW / ta : maxThumbH;
            var thumbImg = new Image
            {
                Source = preview,
                Width  = imgW,
                Height = imgH,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Stretch = Stretch.Fill,
            };
            var previewImg = new Border
            {
                Width  = 80,
                Height = 60,
                Margin = new Thickness(0, 0, 14, 0),
                Background = new SolidColorBrush(Colors.Black),
                VerticalAlignment = VerticalAlignment.Top,
                Child = thumbImg,
            };

            // ── Radio: Natural data size ────────────────────────────────────
            var radioNatural = new RadioButton
            {
                GroupName = "CopyMode",
                Content = new TextBlock
                {
                    Text = $"Actual size (scaled):  {naturalW} \u00d7 {naturalH}",
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            radioNatural.Classes.Add("compact");

            // ── Radio: Custom size ──────────────────────────────────────────
            int initW = displayW > 0 ? displayW : (naturalW > 0 ? naturalW : 512);
            int initH = displayH > 0 ? displayH : (naturalH > 0 ? naturalH : 512);

            var wNud = ControlFactory.MakeNumericUpDown((decimal)initW, 1, 16384, 1, 76);
            var hNud = ControlFactory.MakeNumericUpDown((decimal)initH, 1, 16384, 1, 76);
            wNud.IsEnabled = hNud.IsEnabled = false;

            bool syncNud = false;
            wNud.ValueChanged += (_, _) =>
            {
                if (syncNud || wNud.IsEnabled == false) return;
                syncNud = true;
                hNud.Value = (decimal)Math.Max(1, Math.Round((double)(wNud.Value ?? 1) / aspect));
                syncNud = false;
            };
            hNud.ValueChanged += (_, _) =>
            {
                if (syncNud || hNud.IsEnabled == false) return;
                syncNud = true;
                wNud.Value = (decimal)Math.Max(1, Math.Round((double)(hNud.Value ?? 1) * aspect));
                syncNud = false;
            };

            var customContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            customContent.Children.Add(new TextBlock { Text = "Custom size:", FontSize = FS, VerticalAlignment = VerticalAlignment.Center });
            customContent.Children.Add(wNud);
            customContent.Children.Add(new TextBlock { Text = "\u00d7", FontSize = FS, VerticalAlignment = VerticalAlignment.Center });
            customContent.Children.Add(hNud);

            var radioCustom = new RadioButton { GroupName = "CopyMode", Content = customContent };
            radioCustom.Classes.Add("compact");
            radioCustom.IsCheckedChanged += (_, _) =>
            {
                bool on = radioCustom.IsChecked == true;
                wNud.IsEnabled = hNud.IsEnabled = on;
            };

            // ── Radio: Text ─────────────────────────────────────────────────
            var sepLabels = new[] { "Tab", "Comma", "Semicolon", "Space" };
            var sepChars  = new[] { "\t",  ",",     ";",         " "    };
            var sepCombo  = new ComboBox
            {
                ItemsSource   = sepLabels,
                SelectedIndex = 0,
                Width         = 90,
                Height        = 20,
                IsEnabled     = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            sepCombo.Classes.Add("compact");

            var textContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            textContent.Children.Add(new TextBlock { Text = "Text:",     FontSize = FS, VerticalAlignment = VerticalAlignment.Center });
            textContent.Children.Add(new TextBlock { Text = "Separator", FontSize = FS, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center });
            textContent.Children.Add(sepCombo);

            var radioText = new RadioButton { GroupName = "CopyMode", Content = textContent };
            radioText.Classes.Add("compact");
            radioText.IsCheckedChanged += (_, _) => sepCombo.IsEnabled = radioText.IsChecked == true;

            // ── With overlays checkbox ──────────────────────────────────────
            var overlayChk = ControlFactory.MakeCheckBox("With overlays", fontSize: FS);
            overlayChk.Margin = new Thickness(0, 0, 0, 0);
            overlayChk.IsChecked = _lastOverlays;
            void UpdateOverlay() => overlayChk.IsEnabled = radioText.IsChecked != true;
            radioNatural.IsCheckedChanged += (_, _) => UpdateOverlay();
            radioCustom.IsCheckedChanged += (_, _) => UpdateOverlay();
            radioText.IsCheckedChanged += (_, _) => UpdateOverlay();
            if (refreshPreview != null)
            {
                overlayChk.IsCheckedChanged += (_, _) =>
                {
                    if (overlayChk.IsEnabled)
                        thumbImg.Source = refreshPreview(overlayChk.IsChecked == true) ?? thumbImg.Source;
                };
            }

            switch (_lastMode)
            {
                case CopyMode.ActualPixelSize: radioNatural.IsChecked = true; break;
                case CopyMode.Text:            radioText.IsChecked    = true; break;
                default:                       radioCustom.IsChecked  = true; break;
            }
            UpdateOverlay();
            if (refreshPreview != null && overlayChk.IsEnabled)
                thumbImg.Source = refreshPreview(overlayChk.IsChecked == true) ?? thumbImg.Source;

            // ── OK button ───────────────────────────────────────────────────
            var okBtn = new Button
            {
                Content = "OK",
                FontSize = FS,
                Width = 72,
                IsDefault = true,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            okBtn.Click += (_, _) =>
            {
                bool ov = overlayChk.IsChecked == true;
                _lastOverlays = ov;
                if (radioText.IsChecked == true)
                {
                    _lastMode = CopyMode.Text;
                    Result = new CopyResult { Mode = CopyMode.Text, Separator = sepChars[Math.Max(0, sepCombo.SelectedIndex)] };
                }
                else if (radioCustom.IsChecked == true)
                {
                    _lastMode = CopyMode.CustomSize;
                    Result = new CopyResult { Mode = CopyMode.CustomSize, Width = (int)(wNud.Value ?? 1), Height = (int)(hNud.Value ?? 1), WithOverlays = ov };
                }
                else
                {
                    _lastMode = CopyMode.ActualPixelSize;
                    Result = new CopyResult { Mode = CopyMode.ActualPixelSize, Width = naturalW, Height = naturalH, WithOverlays = ov };
                }
                Close();
            };

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Result = null; Close(); e.Handled = true; }
            };

            // ── Layout ──────────────────────────────────────────────────────
            var optPanel = new StackPanel { Spacing = 8 };
            optPanel.Children.Add(new TextBlock
            {
                Text = "Copy to clipboard as:",
                FontSize = FS,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            });
            optPanel.Children.Add(radioNatural);
            optPanel.Children.Add(radioCustom);
            optPanel.Children.Add(radioText);

            var bottomRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 12,
                Margin = new Thickness(0, 10, 0, 0),
            };
            bottomRow.Children.Add(overlayChk);
            bottomRow.Children.Add(okBtn);
            optPanel.Children.Add(bottomRow);

            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(14),
                Children = { previewImg, optPanel },
            };
        }

    }
}
