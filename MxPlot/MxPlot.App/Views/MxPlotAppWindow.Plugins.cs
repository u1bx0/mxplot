using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using MxPlot.App.Plugins;
using MxPlot.App.ViewModels;
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Plugins;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── Plugin menu management ────────────────────────────────────────

        /// <summary>Built-in item count inside the Tools submenu (before any plugin separator).</summary>
        private const int ToolsBuiltInCount = 5; // "Sample Mandelbrot…", "Sample Julia Set…", "Sample Hyperstack…"

        /// <summary>
        /// Rebuilds the plugin portion of the Tools submenu.
        /// Static built-in items (indices 0 … ToolsBuiltInCount-1) are preserved;
        /// everything beyond is replaced with the current registry contents.
        /// </summary>
        private void RebuildPluginMenuItems(MenuItem toolsItem)
        {
            while (toolsItem.Items.Count > ToolsBuiltInCount)
                toolsItem.Items.RemoveAt(toolsItem.Items.Count - 1);

            var plugins = MxPlotAppPluginRegistry.Plugins;
            if (plugins.Count == 0) return;

            toolsItem.Items.Add(new Separator());
            foreach (var plugin in plugins)
            {
                var captured = plugin;
                var item = new MenuItem { Header = plugin.CommandName };
                ToolTip.SetTip(item, plugin.Description);
                item.Click += (_, _) =>
                {
                    try { captured.Run(CreateMxPlotContext()); }
                    catch { /* silently absorb plugin errors */ }
                };
                toolsItem.Items.Add(item);
            }
        }

        private IMxPlotContext CreateMxPlotContext() => new MxPlotContextImpl(this, ViewModel, _windowList);

        /// <summary>
        /// Implementation of <see cref="IMxPlotContext"/> that provides plugin access
        /// to the dashboard's open and selected datasets, owner window, and file operations.
        /// </summary>
        private sealed class MxPlotContextImpl : IMxPlotContext
        {
            private readonly MxPlotAppWindow _window;
            private readonly MxPlotAppViewModel _vm;
            private readonly ListBox _list;

            internal MxPlotContextImpl(MxPlotAppWindow window, MxPlotAppViewModel vm, ListBox list)
            { _window = window; _vm = vm; _list = list; }

            public IReadOnlyList<IMatrixData> OpenDatasets
                => _vm.ManagedWindows
                       .OfType<MatrixPlotterListItemViewModel>()
                       .Select(w => w.MatrixData)
                       .OfType<IMatrixData>()
                       .ToList();

            public IReadOnlyList<IMatrixData> SelectedDatasets
                => (_list.SelectedItems?
                         .OfType<MatrixPlotterListItemViewModel>()
                         .Select(w => w.MatrixData)
                         .OfType<IMatrixData>()
                         .ToList()
                   ) ?? (IReadOnlyList<IMatrixData>)[];

            public IMatrixData? PrimarySelection
                => (_list.SelectedItem as MatrixPlotterListItemViewModel)?.MatrixData;

            public TopLevel? Owner => _window;

            public IPlotWindowService WindowService => MxPlotAppPluginRegistry.WindowService;

            public Task OpenFileAsync(string path)
                => _vm.LoadAndOpenFileAsync(path, _window);
        }

        // ── Test data generation handlers ─────────────────────────────────

        private async void ToolsGenerateLinearScale_Click(object? sender, RoutedEventArgs e)
        {
            var md = new MatrixData<float>(64, 256);
            md.SetXYScale(0, 63, 0, 255);
            md.Set((ix, iy, x, y) => (float)(y));
            MatrixPlotter.Create(md, title: "Linear Scale").Show();
        }

        private async void ToolsGenerateColorCodedScaleData_Click(object? sender, RoutedEventArgs e)
        {
            int xnum = 64;
            var md = new MatrixData<byte>(64, 256, 256);
            md.SetXYScale(0, 63, 0, 255);
            md.DefineDimensions(Axis.Z(256, 0, 255));
            md.ForEach((iz, array) =>
            {
                for(int i = 0; i < xnum; i++)
                {
                    array[iz * xnum + i] = (byte)i;
                }
            });
            MatrixPlotter.Create(md, title: "Color-Coded Scale Data").Show();
        }

        private async void ToolsGenerateHyperstack_Click(object? sender, RoutedEventArgs e)
        {
            var progressBar = new ProgressBar { Minimum = 0, Maximum = 100, Value = 0, Width = 260, Height = 14 };
            var progressText = new TextBlock
            {
                Text = "0%",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
                Opacity = 0.7,
            };
            var pStack = new StackPanel { Margin = new Thickness(20) };
            pStack.Children.Add(new TextBlock { Text = "Generating Mandelbulb…", FontSize = 11, Margin = new Thickness(0, 0, 0, 10) });
            pStack.Children.Add(progressBar);
            pStack.Children.Add(progressText);
            var dlg = new Window
            {
                Title = "Please Wait",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = pStack,
                FontSize = 11,
            };
            dlg.Show(this);

            var progress = new Progress<double>(pct =>
            {
                progressBar.Value = pct;
                progressText.Text = $"{pct:F0}%";
            });

            var md = await Task.Run(() => Services.TestDataGenerator.GenerateMandelbulb(progress));
            dlg.Close();
            MatrixPlotter.Create(md, title: "Mandelbulb  n=2/4/6  Z=81 / T=5  192×192").Show();
        }

        private void ToolsGenerate2DTestData_Click(object? sender, RoutedEventArgs e)
        {
            var md = Services.TestDataGenerator.Generate2DTestData();
            MatrixPlotter.Create(md, title: "Test 2D Data").Show();
        }

        private void ToolsGenerateJulia_Click(object? sender, RoutedEventArgs e)
        {
            var md = Services.TestDataGenerator.GenerateJuliaSet();
            MatrixPlotter.Create(md, title: "Julia Set  1024×1024  ×8 frames (float)", lut: Core.Imaging.ColorThemes.BSMod).Show();
        }
    }
}
