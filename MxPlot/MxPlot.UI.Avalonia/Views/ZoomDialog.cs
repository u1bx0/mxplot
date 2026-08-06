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
    /// Dialog for entering a custom zoom level.
    /// The three fields (Zoom %, Width px, Height px) are linked and update each other.
    /// Show with <c>await dlg.ShowDialog(ownerWindow)</c> then read <see cref="Result"/>.
    /// </summary>
    internal sealed class ZoomDialog : Window
    {
        /// <summary>Zoom factor (e.g. 2.0 = 200 %). <c>null</c> when the user cancelled.</summary>
        public double? Result { get; private set; }

        public ZoomDialog(double currentZoom, double naturalW, double naturalH)
        {
            Title = "Set Zoom";
            Width = 260;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 11;

            const double FS = 11;
            const double LabelW = 80;
            const double NudW = 110;
            double aspect = naturalW > 0 && naturalH > 0 ? naturalW / naturalH : 1.0;

            TextBox MakeBox(string text) => new()
            {
                Text = text,
                Width = NudW,
                Height = 24,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = FS,
            };

            var zoomBox = MakeBox($"{currentZoom * 100:0.##}");
            var wBox = MakeBox($"{Math.Max(1, (int)Math.Round(naturalW * currentZoom))}");
            var hBox = MakeBox($"{Math.Max(1, (int)Math.Round(naturalH * currentZoom))}");

            bool syncing = false;

            zoomBox.LostFocus += (_, _) => SyncFromZoom();
            zoomBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SyncFromZoom(); e.Handled = true; } };
            wBox.LostFocus += (_, _) => SyncFromW();
            wBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SyncFromW(); e.Handled = true; } };
            hBox.LostFocus += (_, _) => SyncFromH();
            hBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SyncFromH(); e.Handled = true; } };

            void SyncFromZoom()
            {
                if (syncing) return;
                if (!TryParsePositive(zoomBox.Text, out double pct)) return;
                syncing = true;
                double z = Math.Clamp(pct / 100.0, 0.01, 64.0);
                zoomBox.Text = $"{z * 100:0.##}";
                wBox.Text = $"{Math.Max(1, (int)Math.Round(naturalW * z))}";
                hBox.Text = $"{Math.Max(1, (int)Math.Round(naturalH * z))}";
                syncing = false;
            }

            void SyncFromW()
            {
                if (syncing) return;
                if (!TryParsePositive(wBox.Text, out double w)) return;
                syncing = true;
                double z = Math.Clamp(w / Math.Max(1, naturalW), 0.01, 64.0);
                zoomBox.Text = $"{z * 100:0.##}";
                hBox.Text = $"{Math.Max(1, (int)Math.Round(w / aspect))}";
                syncing = false;
            }

            void SyncFromH()
            {
                if (syncing) return;
                if (!TryParsePositive(hBox.Text, out double h)) return;
                syncing = true;
                double z = Math.Clamp(h / Math.Max(1, naturalH), 0.01, 64.0);
                zoomBox.Text = $"{z * 100:0.##}";
                wBox.Text = $"{Math.Max(1, (int)Math.Round(h * aspect))}";
                syncing = false;
            }

            StackPanel Row(string label, TextBox box, string unit) => new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
                Children =
                {
                    new TextBlock { Text = label, Width = LabelW, TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center, FontSize = FS },
                    box,
                    new TextBlock { Text = unit,
                        VerticalAlignment = VerticalAlignment.Center, FontSize = FS, Opacity = 0.6 },
                }
            };

            var okBtn = new Button
            {
                Content = "OK",
                Width = 72,
                Height = 28,
                IsDefault = true,
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

            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 10, 0, 14),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);

            var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            root.Children.Add(Row("Zoom (%):", zoomBox, ""));
            root.Children.Add(Row("Width (px):", wBox, ""));
            root.Children.Add(Row("Height (px):", hBox, ""));
            root.Children.Add(btnRow);
            Content = root;

            Opened += (_, _) => { zoomBox.SelectAll(); zoomBox.Focus(); };

            void Commit()
            {
                SyncFromZoom();
                if (TryParsePositive(zoomBox.Text, out double pct) && pct > 0)
                {
                    Result = Math.Clamp(pct / 100.0, 0.01, 64.0);
                    Close();
                }
            }
        }

        private static bool TryParsePositive(string? text, out double value)
        {
            value = 0;
            return !string.IsNullOrWhiteSpace(text)
                && double.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value)
                && value > 0;
        }
    }
}
