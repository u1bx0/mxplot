using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MxPlot.App.Plugins;
using MxPlot.App.ViewModels;
using MxPlot.Core;
using MxPlot.UI.Avalonia.AddOns;
using MxPlot.UI.Avalonia.Plugins;
using MxPlot.UI.Avalonia.Views;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── Plugin menu management ────────────────────────────────────────

        /// <summary>
        /// Rebuilds the Tools submenu from the current plugin registry contents, followed by the
        /// fixed "Open Add-ons Folder" command, so the submenu is never empty and always leads to
        /// the place where add-ons go.
        /// </summary>
        private void RebuildPluginMenuItems(MenuItem toolsItem)
        {
            toolsItem.Items.Clear();

            var plugins = MxPlotAppPluginRegistry.Plugins;
            if (plugins.Count > 0)
            {
                MenuItem MakeItem(IMxPlotAppPlugin plugin)
                {
                    var captured = plugin;
                    var item = new MenuItem { Header = plugin.CommandName };
                    ToolTip.SetTip(item, plugin.Description);
                    item.Click += (_, _) =>
                    {
                        try { captured.Run(CreateMxPlotAppContext()); }
                        catch { /* silently absorb plugin errors */ }
                    };
                    return item;
                }

                foreach (var plugin in plugins.Where(p => string.IsNullOrEmpty(p.GroupName)))
                    toolsItem.Items.Add(MakeItem(plugin));

                foreach (var grp in plugins.Where(p => !string.IsNullOrEmpty(p.GroupName)).GroupBy(p => p.GroupName!))
                {
                    var groupItem = new MenuItem { Header = grp.Key };
                    foreach (var plugin in grp)
                        groupItem.Items.Add(MakeItem(plugin));
                    toolsItem.Items.Add(groupItem);
                }

                toolsItem.Items.Add(new Separator());
            }

            var openFolder = new MenuItem { Header = "Open Add-ons Folder" };
            ToolTip.SetTip(openFolder,
                $"{AddOnFolders.UserFolder}\n\n" +
                $"Put plugin and file-format DLLs in a \"{AddOnFolders.PluginsFolderName}\" folder and " +
                $"custom LUTs in a \"{AddOnFolders.LutsFolderName}\" folder inside it.\n" +
                "Restart MxPlot to load them.");
            openFolder.Click += async (_, _) => await OpenAddOnsFolderAsync();
            toolsItem.Items.Add(openFolder);
        }

        /// <summary>
        /// Shows <see cref="AddOnFolders.UserFolder"/> in the system file browser. Only that folder
        /// is created when it is missing (the per-user folder on macOS does not exist until used);
        /// nothing inside it is.
        /// </summary>
        private async Task OpenAddOnsFolderAsync()
        {
            try
            {
                string folder = AddOnFolders.UserFolder;
                Directory.CreateDirectory(folder);

                bool opened;
                if (OperatingSystem.IsMacOS())
                {
                    // Avalonia's launcher hands the path to `open` unquoted, so the space in
                    // "Application Support" splits it into two arguments and nothing opens.
                    // ArgumentList passes the path as one argument.
                    var start = new ProcessStartInfo("open") { UseShellExecute = false };
                    start.ArgumentList.Add(folder);
                    using var process = Process.Start(start);
                    opened = process != null;
                }
                else
                {
                    opened = await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder));
                }

                if (!opened)
                    await ShowErrorAsync($"Could not open the add-ons folder:\n{folder}");
            }
            catch (Exception ex)
            {
                await ShowErrorAsync($"Could not open the add-ons folder:\n{ex.Message}");
            }
        }

        private IMxPlotAppContext CreateMxPlotAppContext() => new MxPlotAppContextImpl(this, ViewModel, _windowList);

        /// <summary>
        /// Implementation of <see cref="IMxPlotAppContext"/> that provides plugin access
        /// to the dashboard's open and selected datasets, owner window, and file operations.
        /// </summary>
        private sealed class MxPlotAppContextImpl : IMxPlotAppContext
        {
            private readonly MxPlotAppWindow _window;
            private readonly MxPlotAppViewModel _vm;
            private readonly ListBox _list;

            internal MxPlotAppContextImpl(MxPlotAppWindow window, MxPlotAppViewModel vm, ListBox list)
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

        /// <summary>
        /// Opens the Create Data dialog. Two or more selected windows become its references (m1, m2, ...);
        /// a single selection is only the active window, so it is not taken as a request to reference it.
        /// </summary>
        private void HamburgerCreateData_Click(object? sender, RoutedEventArgs e)
        {
            var selected = SelectedPlotters();
            // A dialog that has made nothing yet is only brought forward, with what the user has entered left as it is.
            OpenCreateDataDialog(_createDataDialog is { HasResult: false } || selected.Count < 2 ? null : selected);
        }

        /// <summary>The Create Data dialog while it is open. There is only one.</summary>
        private CreateDataDialog? _createDataDialog;

        /// <summary>The windows the Create Data dialog may reference: every MatrixPlotter the dashboard lists.</summary>
        private IReadOnlyList<MatrixPlotter> CreateDataCandidates()
            => ViewModel.ManagedWindows.Select(m => m.Window).OfType<MatrixPlotter>().ToList();

        /// <summary>The results that are kept up to date from other windows, by the window that shows each.</summary>
        private readonly Dictionary<Window, LiveCalculation> _liveCalculations = [];

        private void OnLiveCalculationStarted(LiveCalculation calc)
        {
            _liveCalculations[calc.Result] = calc;
            calc.Ended += (_, _) =>
            {
                if (_liveCalculations.TryGetValue(calc.Result, out var known) && ReferenceEquals(known, calc))
                    _liveCalculations.Remove(calc.Result);
                ShowLiveState(calc);
            };
            ShowLiveState(calc);
        }

        /// <summary>
        /// Marks the list row of a live result, or clears the mark when the link has ended, and tints the inputs of
        /// the selected results. The row is added to the list a moment after its window is created, so a new result
        /// is looked for again until it is there.
        /// </summary>
        private void ShowLiveState(LiveCalculation calc, int attempts = 20)
        {
            var item = ViewModel.ManagedWindows.FirstOrDefault(m => m.Window == calc.Result);
            if (item == null)
            {
                if (calc.IsActive && attempts > 0)
                    Dispatcher.UIThread.Post(() => ShowLiveState(calc, attempts - 1), DispatcherPriority.Background);
                return;
            }
            item.IsLiveResult = calc.IsActive;
            item.LiveSourceText = calc.IsActive ? LiveSourceText(calc) : string.Empty;
            UpdateLiveInputHighlights();
        }

        /// <summary>The tooltip text naming the windows <paramref name="calc"/> is calculated from.</summary>
        private string LiveSourceText(LiveCalculation calc)
        {
            var names = calc.Inputs.Select(input =>
                ViewModel.ManagedWindows.FirstOrDefault(m => m.Window == input)?.FileName ?? input.Title ?? "");
            return "from\n" + string.Join("\n", names.Select(n => "    " + n));
        }

        /// <summary>
        /// Tints the inputs of the selected live results, one level only: a result of results shows its own inputs,
        /// not theirs. A window that is itself selected shows its selection instead.
        /// </summary>
        private void UpdateLiveInputHighlights()
        {
            var inputs = new HashSet<Window>();
            foreach (var item in ViewModel.ManagedWindows)
            {
                if (item.IsSelected && item.Window is MatrixPlotter result && _liveCalculations.TryGetValue(result, out var calc))
                    foreach (var input in calc.Inputs) inputs.Add(input);
            }
            foreach (var item in ViewModel.ManagedWindows)
            {
                item.IsLiveInput = !item.IsSelected && inputs.Contains(item.Window);

                // The inputs may have been renamed since the tooltip text was made.
                if (item.IsLiveResult && item.Window is MatrixPlotter live && _liveCalculations.TryGetValue(live, out var known))
                    item.LiveSourceText = LiveSourceText(known);
            }
        }

        private List<MatrixPlotter> SelectedPlotters()
            => _windowList.SelectedItems?.OfType<WindowListItemViewModel>()
                .Select(m => m.Window).OfType<MatrixPlotter>().ToList() ?? [];

        /// <summary>
        /// Opens the Create Data dialog, which may reference any MatrixPlotter the dashboard lists. There is one
        /// dialog. While it has made nothing it is only brought forward, and <paramref name="references"/>, when
        /// given, replace its references. Once it has made a result it belongs to that result, so a new dialog takes
        /// its place, where it was; the result stays as it is. <paramref name="edit"/> opens a new dialog on a result that
        /// is kept up to date, the same way.
        /// </summary>
        private void OpenCreateDataDialog(IReadOnlyList<MatrixPlotter>? references, LiveCalculation? edit = null)
        {
            var open = _createDataDialog;
            if (open != null && !open.HasResult && edit == null)
            {
                if (references != null) open.SetReferences(references);
                if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
                open.Activate();
                return;
            }

            PixelPoint? position = null;
            double width = 0, height = 0;
            if (open != null)
            {
                position = open.Position;
                width = open.Width;
                height = open.Height;
                open.Close();
            }

            // No owner: the dialog must not stay in front of the dashboard, whose list the user reads
            // while choosing references. It is closed with the dashboard instead.
            var dialog = CreateDataDialog.Show(null, CreateDataCandidates, references);
            if (position is { } at)
            {
                dialog.Position = at;
                dialog.Width = width;
                dialog.Height = height;
            }
            _createDataDialog = dialog;
            dialog.LiveCalculationStarted += OnLiveCalculationStarted;
            EventHandler closeWithDashboard = (_, _) => dialog.Close();
            Closed += closeWithDashboard;
            dialog.Closed += (_, _) =>
            {
                Closed -= closeWithDashboard;
                if (ReferenceEquals(_createDataDialog, dialog)) _createDataDialog = null;
            };
            if (edit != null) dialog.EditLive(edit);
        }

        private void ToolsGenerate2DTestData_Click(object? sender, RoutedEventArgs e)
        {
            var md = Services.TestDataGenerator.Generate2DTestData();
            MatrixPlotter.Create(md, title: "Test 2D Data").Show();
        }

        private void ToolsGenerateJulia_Click(object? sender, RoutedEventArgs e)
        {
            var md = Services.TestDataGenerator.GenerateJuliaSet();
            MatrixPlotter.Create(md, title: "Julia Set  1024×1024  ×8 frames (float)", lut: ColorThemes.Profiler).Show();
        }
    }
}
