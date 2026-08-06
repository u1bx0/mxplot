using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── Dialog helpers ────────────────────────────────────────────────

        /// <summary>Shows an error dialog above the always-on-top main window.</summary>
        internal async Task ShowErrorAsync(string message)
        {
            var ok = new Button
            {
                Content = "OK",
                Width = 70,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                FontSize = 11,
            });
            stack.Children.Add(ok);
            var dlg = new Window
            {
                Title = "Error",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = stack,
                FontSize = 11,
            };
            ok.Click += (_, _) => dlg.Close();
            await WithTopmostSuspended(() => dlg.ShowDialog<object?>(this));
        }

        private async Task ShowAboutAsync()
        {
            var ok = new Button
            {
                Content = "OK",
                Width = 60,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            };

            var verFull = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? string.Empty;
            var plusIdx = verFull.IndexOf('+');
            var ver = plusIdx >= 0 ? verFull[..plusIdx] : verFull;

            var buildDate = Assembly.GetEntryAssembly()
               ?.GetCustomAttributes<AssemblyMetadataAttribute>()
               .FirstOrDefault(a => a.Key == "BuildDate")?.Value;

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = "MxPlot", FontSize = 16, FontWeight = FontWeight.Bold });
            textStack.Children.Add(new TextBlock { Text = "—Multi-Axis Matrix Visualization", FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.7 });
            if (!string.IsNullOrEmpty(ver))
                textStack.Children.Add(new TextBlock
                {
                    Text = $"Version {ver}",
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Opacity = 0.55,
                });
            if (!string.IsNullOrEmpty(buildDate))
                textStack.Children.Add(new TextBlock
                {
                    Text = $"Built {buildDate}",
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Opacity = 0.55,
                });

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            try
            {
                var uri = new Uri("avares://MxPlot/Assets/mxplot_logo_pre.png");
                var bmp = new Bitmap(AssetLoader.Open(uri));
                headerRow.Children.Add(new Image
                {
                    Source = bmp,
                    Height = 64,
                    Width = 64,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                });
            }
            catch { }
            headerRow.Children.Add(textStack);

            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(headerRow);
            stack.Children.Add(ok);

            var dlg = new Window
            {
                Title = "About MxPlot",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = stack,
            };
            ok.Click += (_, _) => dlg.Close();

            await WithTopmostSuspended(() => dlg.ShowDialog<object?>(this));
        }

        private async Task<bool> ShowExportOverwriteConfirmAsync(IList<string> fileNames)
        {
            const int MaxListed = 8;
            var list = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            int shown = Math.Min(fileNames.Count, MaxListed);
            for (int i = 0; i < shown; i++)
                list.Children.Add(new TextBlock { Text = fileNames[i], FontSize = 11, Opacity = 0.85 });
            if (fileNames.Count > MaxListed)
                list.Children.Add(new TextBlock
                {
                    Text = $"\u2026 and {fileNames.Count - MaxListed} more",
                    FontSize = 11,
                    Opacity = 0.55,
                    Margin = new Thickness(0, 2, 0, 0),
                });

            bool result = false;
            var cancel = new Button
            {
                Content = "Cancel",
                Width = 70,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var ok = new Button
            {
                Content = "OK",
                Width = 70,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 14, 0, 0),
            };
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(ok);

            var intro = fileNames.Count == 1
                ? "The following file will be overwritten:"
                : "The following files will be overwritten:";
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock { Text = intro, FontSize = 11 });
            stack.Children.Add(list);
            stack.Children.Add(btnRow);

            var dlg = new Window
            {
                Title = "Confirm Overwrite",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = stack,
                FontSize = 11,
                MinWidth = 260,
            };
            cancel.Click += (_, _) => dlg.Close();
            ok.Click += (_, _) => { result = true; dlg.Close(); };
            await WithTopmostSuspended(() => dlg.ShowDialog<object?>(this));
            return result;
        }

        private async Task ShowToastAsync(string message)
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var token = _toastCts.Token;

            _toastText.Text = message;

            // Fade in (180 ms)
            for (int i = 1; i <= 12; i++)
            {
                if (token.IsCancellationRequested) return;
                _toastPanel.Opacity = i / 12.0;
                await Task.Delay(15, CancellationToken.None);
            }

            // Hold (2.8 s)
            try { await Task.Delay(2800, token); }
            catch (OperationCanceledException) { return; }

            // Fade out (480 ms)
            for (int i = 16; i >= 0; i--)
            {
                if (token.IsCancellationRequested) return;
                _toastPanel.Opacity = i / 16.0;
                await Task.Delay(30, CancellationToken.None);
            }
        }
    }
}
