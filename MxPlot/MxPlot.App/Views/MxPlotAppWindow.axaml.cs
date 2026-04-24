using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MxPlot.App.Plugins;
using MxPlot.App.ViewModels;
using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.UI.Avalonia;
using MxPlot.UI.Avalonia.Plugins;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow : Window
    {
        private enum ViewMode { Details, Icons }
        private ViewMode _viewMode = ViewMode.Details;
        private ListBox _windowList = null!;
        private Button _viewDetailsBtn = null!;
        private Button _viewIconsBtn = null!;
        private Slider _cardSizeSlider = null!;
        private int _cardSizeStep = 2;
        private bool _applyingCardSize = false;
        private bool _topmostEnabled = false;
        private bool _processingSelectionChange = false;
        private Button _syncBtn = null!;
        private Button? _revertBtn;
        private bool _isSyncActive = false;
        private MatrixPlotterSyncGroup? _syncGroup;
        private List<WindowListItemViewModel>? _syncSelectionSnapshot;
        private List<MatrixPlotter> _syncBorderedPlotters = [];

        private Border _toastPanel = null!;
        private TextBlock _toastText = null!;
        private CancellationTokenSource? _toastCts;

        private static readonly (double Outer, double Thumb, double Icon)[] CardSizeSteps =
        [
            (54,  42, 22),
            (66,  52, 28),
            (76,  62, 34),
            (96,  80, 44),
            (116, 96, 54),
            (140, 120, 66),
            (160, 146, 80),
        ];

        /// <summary>Threshold above which the loading-mode dialog is shown.</summary>
        private const long LargeFileThresholdBytes = 500 * 1024 * 1024;

        private MxPlotAppViewModel ViewModel => (MxPlotAppViewModel)DataContext!;

        public MxPlotAppWindow()
        {
            InitializeComponent();

            // ── Top bar: drag to move + minimize + close ────────────────
            var topBarDrag = this.FindControl<Border>("TopBarDrag")!;
            topBarDrag.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    BeginMoveDrag(e);
            };
            var minimizeBtn = this.FindControl<Button>("MinimizeBtn")!;
            minimizeBtn.Click += (_, _) => WindowState = WindowState.Minimized;
            var closeBtn = this.FindControl<Button>("CloseBtn")!;
            closeBtn.Click += (_, _) => Close();

            // ── MxPlotAppWindow minimize/restore: hide and restore managed windows ──────
            PropertyChanged += (_, e) =>
            {
                if (e.Property != WindowStateProperty) return;
                if (WindowState == WindowState.Minimized)
                {
                    // Hide all user-visible managed windows (without changing their IsWindowVisible flag).
                    foreach (var item in ViewModel.ManagedWindows)
                        if (item.IsWindowVisible) item.Window.Hide();
                }
                else if (WindowState == WindowState.Normal)
                {
                    // Restore only the windows the user had visible before minimization.
                    foreach (var item in ViewModel.ManagedWindows)
                        if (item.IsWindowVisible) item.Window.Show();
                }
            };

            // ── Close all managed windows when MxPlotAppWindow exits ──────────
            Closed += (_, _) =>
            {
                foreach (var item in ViewModel.ManagedWindows.ToList())
                    item.Window.Close();
            };

            // ── Window List: drag-drop (on the Panel so empty-state area accepts drops too) ──
            // DataContext is set after the constructor via object initializer in App.axaml.cs,
            // so wire PositionWindowAction once DataContext becomes available.
            DataContextChanged += (_, _) =>
            {
                if (DataContext is not MxPlotAppViewModel vm) return;
                vm.PositionWindowAction = (w, i) => PositionNewWindow(w, i);
                vm.DashboardWindow = this;
                vm.WindowFocusedAction = OnManagedWindowFocused;
            };

            var listPanel = this.FindControl<Panel>("ListPanel")!;
            DragDrop.SetAllowDrop(listPanel, true);
            listPanel.AddHandler(DragDrop.DropEvent, async (_, e) =>
            {
                if (e.Data.GetFiles() is { } files)
                {
                    foreach (var item in files)
                    {
                        var path = item.TryGetLocalPath();
                        if (path != null)
                            await ViewModel.LoadAndOpenFileAsync(path, this);
                    }
                }
            });
            listPanel.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = e.Data.GetFiles() != null
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            });

            // ── Window List: selection → ViewModel sync + activate ───────
            _windowList = this.FindControl<ListBox>("WindowList")!;
            _windowList.SelectionChanged += (_, _) =>
            {
                if (_processingSelectionChange) return;
                _processingSelectionChange = true;
                try
                {
                    // Sync active: freeze selection, just activate the clicked window.
                    if (_isSyncActive)
                    {
                        var clicked = _windowList.SelectedItems?
                            .OfType<WindowListItemViewModel>()
                            .FirstOrDefault(vm => _syncSelectionSnapshot == null
                                                  || !_syncSelectionSnapshot.Contains(vm));
                        RestoreSyncSelection();
                        if (clicked?.IsWindowVisible == true)
                            clicked.Window.Activate();
                        return;
                    }

                    // Remove hidden items from the ListBox selection synchronously.
                    var toDeselect = _windowList.SelectedItems?
                        .OfType<WindowListItemViewModel>()
                        .Where(vm => !vm.IsWindowVisible)
                        .ToList();
                    if (toDeselect is { Count: > 0 })
                        foreach (var h in toDeselect)
                            _windowList.SelectedItems!.Remove(h);

                    var selected = _windowList.SelectedItems;
                    foreach (var item in ViewModel.ManagedWindows)
                        item.IsSelected = false;
                    if (selected != null)
                    {
                        foreach (var s in selected.OfType<WindowListItemViewModel>())
                            s.IsSelected = true;
                    }
                    ViewModel.RefreshSelectionState();

                    // Single-selection: bring the window to front only if it is currently visible.
                    // Skip activation while any item is being renamed — the managed window
                    // would steal keyboard focus from the rename TextBox.
                    if (_windowList.SelectedItems?.Count == 1
                        && _windowList.SelectedItem is WindowListItemViewModel single
                        && single.IsWindowVisible
                        && !ViewModel.ManagedWindows.Any(m => m.IsRenaming))
                    {
                        single.Window.Activate();
                    }
                }
                finally
                {
                    _processingSelectionChange = false;
                }
            };

            // Double-click: toggle show/hide for the selected window.
            _windowList.DoubleTapped += (_, _) =>
            {
                if (_windowList.SelectedItem is WindowListItemViewModel item)
                    item.ToggleVisibility();
            };

            // ── Re-activate already-selected window when clicked again ─────────
            // SelectionChanged only fires when selection *changes*, so clicking an
            // already-selected item in front of another app won't raise it.
            // Tunneling lets us read vm.IsSelected before ListBoxItem mutates the selection.
            _windowList.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (_isSyncActive) return;
                if (!e.GetCurrentPoint(_windowList).Properties.IsLeftButtonPressed) return;
                if (ViewModel.ManagedWindows.Any(m => m.IsRenaming)) return;

                var listBoxItem = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
                if (listBoxItem?.DataContext is WindowListItemViewModel vm
                    && vm.IsSelected
                    && vm.IsWindowVisible)
                {
                    vm.Window.Activate();
                }
            }, RoutingStrategies.Tunnel);

            // ── View mode toggle buttons ──────────────────────────────────
            _viewDetailsBtn = this.FindControl<Button>("ViewDetailsBtn")!;
            _viewIconsBtn = this.FindControl<Button>("ViewIconsBtn")!;
            _viewDetailsBtn.Click += (_, _) => ApplyViewMode(ViewMode.Details);
            _viewIconsBtn.Click += (_, _) => ApplyViewMode(ViewMode.Icons);

            _cardSizeSlider = this.FindControl<Slider>("CardSizeSlider")!;
            _cardSizeSlider.ValueChanged += (_, e) => ApplyCardSize((int)e.NewValue);

            _windowList.PointerWheelChanged += (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _viewMode == ViewMode.Icons)
                {
                    e.Handled = true;
                    var delta = e.Delta.Y > 0 ? 1 : -1;
                    ApplyCardSize(Math.Clamp(_cardSizeStep + delta, 0, CardSizeSteps.Length - 1));
                }
            };

            _syncBtn = this.FindControl<Button>("SyncBtn")!;
            _syncBtn.Click += (_, _) =>
            {
                if ((_windowList.SelectedItems?.Count ?? 0) >= 2)
                    SetSyncActive(!_isSyncActive);
            };

            _toastPanel = this.FindControl<Border>("ToastPanel")!;
            _toastText = this.FindControl<TextBlock>("ToastText")!;

            // ── Plugin items wired into the Tools submenu ─────────────────────
            var hamburgerBtn = this.FindControl<Button>("HamburgerBtn")!;
            var flyout = (MenuFlyout)hamburgerBtn.Flyout!;
            var toolsItem = flyout.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "Tools");
            RebuildPluginMenuItems(toolsItem);
            Action onPluginsChanged = () => RebuildPluginMenuItems(toolsItem);
            MxPlotAppPluginRegistry.PluginsChanged += onPluginsChanged;
            Closed += (_, _) => MxPlotAppPluginRegistry.PluginsChanged -= onPluginsChanged;

            // ── "Open from Clipboard" item (not in AXAML: flyout items are outside the name scope) ──
            var clipboardItem = new MenuItem
            {
                Header = "Open from Clipboard\u2026",
                IsEnabled = false,
                Icon = new PathIcon
                {
                    Data = Geometry.Parse("M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z"),
                    Width = 14,
                    Height = 14,
                    Foreground = new SolidColorBrush(Color.Parse("#78909C")),
                },
            };
            clipboardItem.Click += HamburgerOpenFromClipboard_Click;
            flyout.Items.Insert(1, clipboardItem);

            var exportItem = new MenuItem { Header = "Export as PNG\u2026", IsEnabled = false };
            exportItem.Click += HamburgerExportAsPng_Click;
            flyout.Items.Insert(2, exportItem);

            // Enable/disable depending on clipboard/export availability when the flyout opens
            flyout.Opened += async (_, _) =>
            {
                clipboardItem.IsEnabled = await ClipboardHasImageAsync();
                exportItem.IsEnabled = ViewModel.HasExportableSelection;
                exportItem.Header = ViewModel.HasMultiSelection
                    ? "Export selected as PNG\u2026"
                    : "Export as PNG\u2026";
            };

            // ── ───────────────────────────────
            var topmostCheckIcon = new TextBlock
            {
                Text = "✓",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            topmostCheckIcon.IsVisible = _topmostEnabled;
            var topmostItem = new MenuItem
            {
                Header = "Always on Top",
                Icon = topmostCheckIcon,
            };
            topmostItem.Click += (_, _) =>
            {
                _topmostEnabled = !_topmostEnabled;
                topmostCheckIcon.IsVisible = _topmostEnabled;
                if (_topmostEnabled)
                    Topmost = true;
                else
                    Topmost = IsCurrentForegroundOurProcess();
            };

            var aboutMenuItem = flyout.Items.OfType<MenuItem>().First(m => m.Header?.ToString() == "About");
            int aboutIdx = ((System.Collections.IList)flyout.Items).IndexOf(aboutMenuItem);
            //flyout.Items.Insert(aboutIdx, new Separator());
            flyout.Items.Insert(aboutIdx, topmostItem);

            // Dashboard stays above own plot windows; drops behind other apps when they take focus.
            Topmost = true;
            Activated += OnAnyAppWindowActivated;
            Deactivated += OnAnyAppWindowDeactivated;
            Action<Window, IMatrixData?> plotWindowSetup = (w, _) =>
            {
                w.Activated += OnAnyAppWindowActivated;
                w.Deactivated += OnAnyAppWindowDeactivated;
                w.Closed += (_, _) =>
                {
                    w.Activated -= OnAnyAppWindowActivated;
                    w.Deactivated -= OnAnyAppWindowDeactivated;
                    if (_isSyncActive && _syncSelectionSnapshot != null
                        && _syncSelectionSnapshot.Any(vm => vm.Window == w))
                        SetSyncActive(false);
                };
            };
            PlotWindowNotifier.PlotWindowCreated += plotWindowSetup;
            Closed += (_, _) => PlotWindowNotifier.PlotWindowCreated -= plotWindowSetup;
        }

        // ── Hamburger menu handlers ──────────────────────────────────────

        private async void HamburgerOpenFile_Click(object? sender, RoutedEventArgs e)
            => await OpenFileViaDialogAsync();

        private async void HamburgerAbout_Click(object? sender, RoutedEventArgs e)
            => await ShowAboutAsync();

        private async void HamburgerExit_Click(object? sender, RoutedEventArgs e)
            => Close();

        /// <summary>Shows an error dialog above the always-on-top main window.</summary>
        internal async System.Threading.Tasks.Task ShowErrorAsync(string message)
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

        private async System.Threading.Tasks.Task ShowAboutAsync()
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

        // ── View mode switching ──────────────────────────────────────────

        private void ApplyViewMode(ViewMode mode)
        {
            _viewMode = mode;
            if (mode == ViewMode.Icons)
            {
                _windowList.ItemTemplate = (IDataTemplate)Resources["IconTemplate"]!;
                _windowList.ItemsPanel = (ITemplate<Panel>)Resources["IconsPanel"]!;
                if (!_windowList.Classes.Contains("IconView"))
                    _windowList.Classes.Add("IconView");
                _viewDetailsBtn.Opacity = 0.35;
                _viewIconsBtn.Opacity = 1.0;
                ViewModel.IsIconView = true;
            }
            else
            {
                _windowList.ItemTemplate = (IDataTemplate)Resources["DetailsTemplate"]!;
                _windowList.ItemsPanel = (ITemplate<Panel>)Resources["DetailsPanel"]!;
                _windowList.Classes.Remove("IconView");
                _viewDetailsBtn.Opacity = 1.0;
                _viewIconsBtn.Opacity = 0.35;
                ViewModel.IsIconView = false;
            }
        }

        private void ApplyCardSize(int step)
        {
            if (_applyingCardSize) return;
            _applyingCardSize = true;
            _cardSizeStep = step;
            var (outer, thumb, icon) = CardSizeSteps[step];
            Resources["GridCardOuter"] = outer;
            Resources["GridCardThumb"] = thumb;
            Resources["GridCardIcon"] = icon;
            if ((int)_cardSizeSlider.Value != step)
                _cardSizeSlider.Value = step;
            _applyingCardSize = false;
        }

        // ── File open helpers ────────────────────────────────────────────


        /// <summary>
        /// Temporarily suspends <see cref="Window.Topmost"/> while awaiting a dialog so that
        /// the dialog is not obscured by the always-on-top main window.
        /// </summary>
        private async System.Threading.Tasks.Task<T> WithTopmostSuspended<T>(
            System.Func<System.Threading.Tasks.Task<T>> action)
        {
            bool wasTopmost = Topmost;
            Topmost = false;
            try { return await action(); }
            finally { Topmost = wasTopmost; }
        }

        /// <summary>
        /// Places a newly created plot window in the wider side of the screen relative to
        /// this dashboard window, with a cascade offset so multiple windows don’t stack exactly.
        /// </summary>
        private void PositionNewWindow(Window window, int windowIndex)
        {
            const double Gap = 16.0;
            const double CascadeStep = 28.0;

            // Find the screen that contains this dashboard window.
            var screen = Screens.All.FirstOrDefault(s =>
                Position.X >= s.Bounds.X && Position.X < s.Bounds.X + s.Bounds.Width &&
                Position.Y >= s.Bounds.Y && Position.Y < s.Bounds.Y + s.Bounds.Height)
                ?? Screens.Primary;
            if (screen == null) return;

            double sc = screen.Scaling;
            var wb = screen.WorkingArea;

            // Convert everything to DIPs for easy arithmetic.
            var screenRect = new Rect(wb.X / sc, wb.Y / sc, wb.Width / sc, wb.Height / sc);
            var dashRect = new Rect(Position.X / sc, Position.Y / sc, Bounds.Width, Bounds.Height);

            // Determine the available side: use whichever side of the dashboard is wider.
            var available = screenRect;
            if (dashRect.Intersects(screenRect))
            {
                double rightSpace = screenRect.Right - dashRect.Right - Gap;
                double leftSpace = dashRect.Left - screenRect.X - Gap;
                if (rightSpace >= leftSpace)
                    available = new Rect(dashRect.Right + Gap, screenRect.Y, rightSpace, screenRect.Height);
                else
                    available = new Rect(screenRect.X, screenRect.Y, leftSpace, screenRect.Height);
            }

            // Fallback: use full screen if the computed side is too narrow.
            if (available.Width < 200 || available.Height < 200)
                available = screenRect;

            // Cascade: wrap within the available region so windows don’t drift off screen.
            double maxStep = Math.Max(0, Math.Min(available.Width, available.Height) - 200);
            double step = maxStep > 0 ? CascadeStep * windowIndex % maxStep : 0;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(
                (int)((available.X + Gap / 2 + step) * sc),
                (int)((available.Y + Gap / 2 + step) * sc));
        }

        /// <summary>Shows the file picker dialog and loads selected files.</summary>
        private async System.Threading.Tasks.Task OpenFileViaDialogAsync()
        {
            var descriptors = FormatRegistry.ReaderDescriptors;
            var perFormat = descriptors
                .Select(d => new FilePickerFileType(d.FormatName) { Patterns = d.DialogPatterns.ToList() })
                .ToList();

            var allPatterns = descriptors.SelectMany(d => d.DialogPatterns).Distinct().ToList();
            var allSupported = new FilePickerFileType("All Supported Files") { Patterns = allPatterns };

            var fileTypes = new List<FilePickerFileType> { allSupported };
            fileTypes.AddRange(perFormat);

            var files = await WithTopmostSuspended(() =>
                StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open File",
                    AllowMultiple = true,
                    FileTypeFilter = fileTypes,
                }));

            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path != null)
                    await ViewModel.LoadAndOpenFileAsync(path, this);
            }
        }

        /// <summary>
        /// For large files, asks the user to choose between InMemory and Virtual loading.
        /// Returns null if the user cancels.
        /// </summary>
        internal async System.Threading.Tasks.Task<LoadingMode?> ResolveLoadingModeAsync(string path)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length < LargeFileThresholdBytes)
                return LoadingMode.Auto;

            var reader = FormatRegistry.CreateReader(path);
            if (reader is not IVirtualLoadable)
                return LoadingMode.InMemory;

            return await WithTopmostSuspended(() =>
                LoadingModeDialog.ShowAsync(this, Path.GetFileName(path), fileInfo.Length));
        }

        // ── Tools menu handlers ───────────────────────────────────────────────

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

            var md = await Task.Run(() => GenerateHyperstackTestData(progress));
            dlg.Close();
            MatrixPlotter.Create(md, title: "Mandelbulb  n=2/4/6  Z=81 / T=5  192×192").Show();
        }

        private void ToolsGenerate2DTestData_Click(object? sender, RoutedEventArgs e)
        {
            var md = Generate2DTestData();
            MatrixPlotter.Create(md, title: "Test 2D Data").Show();
        }

        private void ToolsGenerateJulia_Click(object? sender, RoutedEventArgs e)
        {
            var md = GenerateJuliaData();
            MatrixPlotter.Create(md, title: "Julia Set  1024×1024  ×8 frames (float)", lut: Core.Imaging.ColorThemes.BSMod).Show();
        }

        /// <summary>
        /// Generates 8 Julia set frames (1024×1024 float), each with a visually distinct
        /// complex parameter c chosen to showcase different fractal morphologies.
        /// Smooth escape-time with gamma=0.5 compression: brighter than log but still
        /// distributes values toward boundary detail, keeping the interior dark and the
        /// full LUT gradient visible across thin boundary zones.
        /// Value 0 = inside; >0 = escaped (higher = slower escape / nearer to boundary).
        /// </summary>
        private static MatrixData<float> GenerateJuliaData()
        {
            const int w = 1024, h = 1024, maxIter = 1024;
            const double r = 1.65; // view half-extent

            // Carefully chosen c values covering spirals, dendrites, rabbits, discs, flowers
            (double cRe, double cIm, string name)[] specs =
            [
                (-0.7269,   0.1889,  "Simonini spirals"),    // 0: fine spiral arms
                (-0.70176, -0.3842,  "Snowflake dendrite"),  // 1: spiky crystalline
                ( 0.285,    0.010,   "Disk packing"),        // 2: nested discs
                (-0.4,      0.600,   "Classic spirals"),     // 3: large open spirals
                (-0.835,   -0.232,   "Crystal dendrite"),    // 4: dendritic branches
                (-0.7,      0.27,    "Douady rabbit"),       // 5: three-lobe rabbit
                ( 0.000,    0.640,   "Siegel disc"),         // 6: smooth rotation disc
                (-0.1,      0.651,   "Flower / petals"),     // 7: petal-like lobes
            ];

            var md = new MatrixData<float>(w, h, specs.Length);
            md.SetXYScale(-r, r, -r, r);
            md.Axes[0].Step = 0.1;

            // Gamma-compressed smooth escape: val = (smoothed / maxIter)^gamma
            // gamma=0.5 is a good middle ground — brighter than log, more contrast than linear
            const double gamma = 0.5;

            Parallel.For(0, specs.Length, frame =>
            {
                var (cRe, cIm, _) = specs[frame];
                var arr = md.GetArray(frame);
                for (int py = 0; py < h; py++)
                {
                    double zy0 = -r + py * (2.0 * r) / (h - 1.0);
                    for (int px = 0; px < w; px++)
                    {
                        double zx = -r + px * (2.0 * r) / (w - 1.0);
                        double zy = zy0;
                        int iter = 0;
                        while (zx * zx + zy * zy <= 4.0 && iter < maxIter)
                        {
                            double tmp = zx * zx - zy * zy + cRe;
                            zy = 2.0 * zx * zy + cIm;
                            zx = tmp;
                            iter++;
                        }
                        if (iter >= maxIter)
                        {
                            arr[py * w + px] = 0f;
                        }
                        else
                        {
                            double mod = Math.Sqrt(zx * zx + zy * zy);
                            if (mod < 1.0 + 1e-15) mod = 1.0 + 1e-15;
                            double smooth = iter + 1.0 - Math.Log(Math.Log(mod)) / Math.Log(2.0);
                            smooth = Math.Clamp(smooth, 0.0, maxIter - 1.0);
                            arr[py * w + px] = (float)Math.Pow(smooth / (maxIter - 1.0), gamma);
                        }
                    }
                }
            });
            return md;
        }




        private static MatrixData<ushort> Generate2DTestData()
        {
            const int xnum = 41;
            const int ynum = 41;
            var md = new MatrixData<ushort>(xnum, ynum);
            md.SetXYScale(-10, 10, -10, 10);
            md.Set((ix, iy, x, y) => (ushort)(iy * ix));
            return md;
        }

        /// <summary>
        /// Generates a 3ch × Z=81 × T=5 Mandelbulb hyperstack.
        /// <list type="bullet">
        ///   <item>C (channel) — Mandelbulb power n: 2, 4, 6</item>
        ///   <item>Z — evenly-spaced cross-section slices through the bulb</item>
        ///   <item>T — Y-axis rotation angle (0°–90°),</item>
        /// </list>
        /// Interior pixels = 0; escaped pixels = smooth escape value in (0, 1].
        /// </summary>
        private static MatrixData<float> GenerateHyperstackTestData(IProgress<double>? progress = null)
        {
            const int w = 192, h = 192, cNum = 3, zNum = 81, tNum = 5;
            const int maxIter = 30;
            const double extent = 1.5;
            const double escapeR = 2.0;
            const double gamma = 0.95;

            int[] powers = [2, 3, 5];
            int total = cNum * zNum * tNum;
            int completed = 0;

            var md = new MatrixData<float>(w, h, total);
            md.SetXYScale(-extent, extent, -extent, extent);
            md.XUnit = "";
            md.YUnit = "";
            md.DefineDimensions(
                new ColorChannel(["n=2", "n=4", "n=6"]),
                Axis.Z(zNum, -extent, extent, ""),
                Axis.Time(tNum, 0.0, 330.0, "°"));

            Parallel.For(0, total, frame =>
            {
                var (ic, iz, it) = md.Dimensions.GetAxisIndicesStruct(frame);
                int n = powers[ic];
                double logN = Math.Log(n);
                double logEsc = Math.Log(escapeR);
                double rotY = it * Math.PI * 0.5 / tNum;
                double cosR = Math.Cos(rotY), sinR = Math.Sin(rotY);
                // Z: evenly-spaced cross-section planes through the bulb
                double zPlane = -extent + iz * (2.0 * extent) / (zNum - 1.0);
                var arr = md.GetArray(frame);

                for (int py = 0; py < h; py++)
                {
                    double sy = -extent + py * (2.0 * extent) / (h - 1.0);
                    for (int px = 0; px < w; px++)
                    {
                        double sx = -extent + px * (2.0 * extent) / (w - 1.0);
                        // Rotate (sx, zPlane) around Y-axis to get the 3-D seed point c
                        double cx = sx * cosR - zPlane * sinR;
                        double cy = sy;
                        double cz = sx * sinR + zPlane * cosR;

                        double x = cx, y = cy, z = cz;
                        int iter = 0;
                        double minOrbitR = double.MaxValue;

                        while (iter < maxIter)
                        {
                            double r2 = x * x + y * y + z * z;
                            double r = Math.Sqrt(r2);
                            if (r < minOrbitR) minOrbitR = r;
                            if (r2 > escapeR * escapeR) break;
                            double theta = Math.Atan2(Math.Sqrt(x * x + y * y), z);
                            double phi = Math.Atan2(y, x);
                            double rn = Math.Pow(r, n);
                            double nt = n * theta;
                            double np = n * phi;
                            double sinT = Math.Sin(nt);
                            x = rn * sinT * Math.Cos(np) + cx;
                            y = rn * sinT * Math.Sin(np) + cy;
                            z = rn * Math.Cos(nt) + cz;
                            iter++;
                        }

                        if (iter >= maxIter)
                        {
                            // Interior: orbit trap → concentric spherical shell banding
                            double normTrap = minOrbitR / extent;
                            double band = 0.5 + 0.5 * Math.Sin(normTrap * Math.PI * 1.5);
                            arr[py * w + px] = (float)(0.08 + band * 0.20) * 0.1f;
                        }
                        else
                        {
                            // Exterior: smooth escape coloring with gamma compression
                            double rFinal = Math.Sqrt(x * x + y * y + z * z);
                            double logR = Math.Log(Math.Max(rFinal, 1.0 + 1e-15));
                            double smooth = iter - Math.Log(logR / logEsc) / logN;
                            smooth = Math.Clamp(smooth, 0.0, maxIter - 1.0);
                            arr[py * w + px] = (float)Math.Pow(smooth / (maxIter - 1.0), gamma);
                        }
                    }
                }

                int done = Interlocked.Increment(ref completed);
                if (done % 10 == 0 || done == total)
                    progress?.Report(done * 100.0 / total);
            });
            return md;
        }

        // ── Plugin menu helpers ───────────────────────────────────────────────

        /// <summary>Built-in item count inside the Tools submenu (before any plugin separator).</summary>
        private const int ToolsBuiltInCount = 3; // "Sample Mandelbrot…", "Sample Julia Set…", "Sample Hyperstack…"

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

        // ── Clipboard ────────────────────────────────────────────────────────

        // Platform image-format identifiers tried in order of preference
        private static readonly string[] ClipboardImageFormats =
            ["PNG", "image/png", "public.png", "public.tiff", "com.apple.tiff", "Bitmap"];

        private async Task<bool> ClipboardHasImageAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) return false;
                var formats = await clipboard.GetFormatsAsync();
                return formats?.Any(f =>
                    ClipboardImageFormats.Contains(f, StringComparer.OrdinalIgnoreCase)) == true;
            }
            catch { return false; }
        }

        private async Task<Bitmap?> GetClipboardBitmapAsync()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return null;

            var available = await clipboard.GetFormatsAsync() ?? [];
            Console.WriteLine(
                $"[Clipboard] available: {string.Join(", ", available)}");

            // On macOS, Avalonia's GetDataAsync returns null for all native NSPasteboard
            // image formats. As a workaround, MxView.CopyImageAsync caches the PNG bytes
            // of the last in-process copy. If the clipboard still carries the avaloniaui
            // in-process format (meaning no other app has overwritten it since), use the cache.
            bool hasInProc = available.Any(f =>
                f.Contains("avaloniaui", StringComparison.OrdinalIgnoreCase));
            if (hasInProc && MxPlot.UI.Avalonia.Controls.MxView.LastCopiedPng is { Length: > 4 } cached)
            {
                Console.WriteLine("[Clipboard] using in-process PNG cache");
                try
                {
                    using var cacheMem = new MemoryStream(cached);
                    return new Bitmap(cacheMem);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Clipboard] cache decode failed: {ex.Message}");
                }
            }

            var predefined = ClipboardImageFormats
                .Where(f => available.Any(a =>
                    string.Equals(a, f, StringComparison.OrdinalIgnoreCase)));
            var extra = available
                .Where(f => !ClipboardImageFormats.Any(cf =>
                    string.Equals(cf, f, StringComparison.OrdinalIgnoreCase))
                    && (f.Contains("image", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("tiff", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("png", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("bmp", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("pict", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("avaloniaui", StringComparison.OrdinalIgnoreCase)));

            foreach (var fmt in predefined.Concat(extra))
            {
                try
                {
                    var data = await clipboard.GetDataAsync(fmt);
                    Console.WriteLine(
                        $"[Clipboard] fmt={fmt}  type={data?.GetType().Name ?? "null"}" +
                        $"  len={(data is byte[] b0 ? b0.Length : -1)}");

                    if (data is Bitmap inProcBmp)
                        return inProcBmp;

                    byte[]? bytes = data switch
                    {
                        byte[] b => b,
                        MemoryStream ms => ms.ToArray(),
                        Stream s => ReadStreamToBytes(s),
                        _ => null,
                    };
                    if (bytes == null || bytes.Length < 4) continue;

                    using var skBmp = SkiaSharp.SKBitmap.Decode(bytes);
                    if (skBmp != null)
                    {
                        using var png = new MemoryStream();
                        skBmp.Encode(png, SkiaSharp.SKEncodedImageFormat.Png, 100);
                        png.Position = 0;
                        return new Bitmap(png);
                    }

                    using var ms2 = new MemoryStream(bytes);
                    return new Bitmap(ms2);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Clipboard] fmt={fmt} exception: {ex.Message}");
                }
            }
            return null;
        }

        private static byte[] ReadStreamToBytes(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private async void HamburgerOpenFromClipboard_Click(object? sender, RoutedEventArgs e)
        {
            var bmp = await GetClipboardBitmapAsync();
            if (bmp == null)
            {
                await ShowErrorAsync("No image found in clipboard.");
                return;
            }

            try
            {
                int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;

                var mode = await ShowClipboardLoadModeAsync(w, h);
                if (mode == null) return;

                // Extract raw RGBA pixels on UI thread (WriteableBitmap requires it)
                byte[] raw;
                int stride;
                using (var wb = new WriteableBitmap(bmp.PixelSize, bmp.Dpi,
                           PixelFormat.Rgba8888, AlphaFormat.Unpremul))
                using (var fb = wb.Lock())
                {
                    bmp.CopyPixels(new PixelRect(0, 0, w, h),
                        fb.Address, fb.RowBytes * h, fb.RowBytes);
                    stride = fb.RowBytes;
                    raw = new byte[stride * h];
                    Marshal.Copy(fb.Address, raw, 0, raw.Length);
                }

                bool grayscale = mode.Value;
                var md = await Task.Run(() => ClipboardPixelsToMatrixData(raw, stride, w, h, grayscale));

                var title = grayscale ? "Clipboard  (Grayscale)" : "Clipboard  (RGB)";
                MatrixPlotter.Create(md, title: title).Show();
            }
            finally
            {
                bmp.Dispose();
            }
        }

        /// <summary>Shows a dialog asking whether to load the clipboard image as Grayscale or Color Channels.</summary>
        /// <returns><c>true</c> = Grayscale, <c>false</c> = Color Channels, <c>null</c> = cancelled.</returns>
        private async Task<bool?> ShowClipboardLoadModeAsync(int w, int h)
        {
            bool? result = null;

            var grayBtn = new Button
            {
                Content = "Grayscale",
                Width = 126,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            var colorBtn = new Button
            {
                Content = "Color Channels",
                Width = 126,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 72,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            var dlg = new Window
            {
                Title = "Open from Clipboard",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontSize = 11,
            };

            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock
            {
                Text = $"Image: {w} \u00d7 {h}",
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 6),
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Load as:",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 10),
            });
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            btnRow.Children.Add(grayBtn);
            btnRow.Children.Add(colorBtn);
            stack.Children.Add(btnRow);
            stack.Children.Add(cancelBtn);
            dlg.Content = stack;

            grayBtn.Click += (_, _) => { result = true; dlg.Close(); };
            colorBtn.Click += (_, _) => { result = false; dlg.Close(); };
            cancelBtn.Click += (_, _) => dlg.Close();

            await WithTopmostSuspended(() => dlg.ShowDialog<object?>(this));
            return result;
        }

        private static MatrixData<byte> ClipboardPixelsToMatrixData(
            byte[] raw, int stride, int w, int h, bool grayscale)
        {
            if (grayscale)
            {
                var gray = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + x * 4;
                        gray[(h - 1 - y) * w + x] = (byte)(0.2126 * raw[i] + 0.7152 * raw[i + 1] + 0.0722 * raw[i + 2]);
                    }
                return new MatrixData<byte>(w, h, new List<byte[]> { gray });
            }
            else
            {
                var r = new byte[w * h];
                var g = new byte[w * h];
                var b = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + x * 4;
                        int idx = (h - 1 - y) * w + x;
                        r[idx] = raw[i];
                        g[idx] = raw[i + 1];
                        b[idx] = raw[i + 2];
                    }
                var cc = new ColorChannel(["R", "G", "B"]);
                // Pure primaries are required: additive composite R*(1,0,0) + G*(0,1,0) + B*(0,0,1) = (R,G,B).
                // Any desaturation introduces cross-channel bleeding and breaks composite reconstruction.
                cc.AssignColors([
                    unchecked((int)0xFFFF0000),   // R → pure red
                    unchecked((int)0xFF00FF00),   // G → pure green
                    unchecked((int)0xFF0000FF),   // B → pure blue
                ]);
                var md = new MatrixData<byte>(w, h, new List<byte[]> { r, g, b });
                md.DefineDimensions(cc);
                return md;
            }
        }

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

        // ── Export as PNG ────────────────────────────────────────────────

        private async void HamburgerExportAsPng_Click(object? sender, RoutedEventArgs e)
            => await ExportSelectedAsPngAsync();

        private async System.Threading.Tasks.Task ExportSelectedAsPngAsync()
        {
            var items = ViewModel.ManagedWindows
                .Where(m => m.IsSelected && m is IExportableAsImage)
                .ToList();
            if (items.Count == 0) return;

            var folders = await WithTopmostSuspended(() =>
                StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Export Folder",
                    AllowMultiple = false,
                }));
            if (folders.Count == 0) return;

            var dir = folders[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(dir)) return;

            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pathMap = items
                .Select(m => ((IExportableAsImage)m, GetExportPngPath(m.FileName, dir, usedPaths)))
                .ToList();

            var existing = pathMap
                .Where(p => File.Exists(p.Item2))
                .Select(p => Path.GetFileName(p.Item2))
                .ToList();
            if (existing.Count > 0 && !await ShowExportOverwriteConfirmAsync(existing))
                return;

            var exportedPaths = new List<string>();
            foreach (var (exportable, path) in pathMap)
                if (await exportable.ExportAsImageAsync(path)) exportedPaths.Add(path);

            if (exportedPaths.Count > 0)
            {
                var msg = exportedPaths.Count == 1
                    ? $"Exported  {Path.GetFileName(exportedPaths[0])}"
                    : $"Exported {exportedPaths.Count} PNGs";
                await ShowToastAsync(msg);
            }
        }

        private async System.Threading.Tasks.Task<bool> ShowExportOverwriteConfirmAsync(IList<string> fileNames)
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

        private static string GetExportPngPath(string title, string dir, ISet<string> usedPaths)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(title.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(safe)) safe = "export";

            // Strip any existing extension (e.g. "image.csv" → "image") before appending ".png".
            var baseName = Path.GetFileNameWithoutExtension(safe);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = safe;

            var candidate = Path.Combine(dir, baseName + ".png");
            if (usedPaths.Add(candidate)) return candidate;

            for (int i = 2; ; i++)
            {
                candidate = Path.Combine(dir, $"{baseName}_{i}.png");
                if (usedPaths.Add(candidate)) return candidate;
            }
        }

        private async System.Threading.Tasks.Task ShowToastAsync(string message)
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

        // ── Window focus / sync helpers ───────────────────────────────────

        private void OnManagedWindowFocused(Window window)
        {
            if (_isSyncActive) return;
            var item = ViewModel.ManagedWindows.FirstOrDefault(m => m.Window == window);
            if (item == null) return;

            // If the focused window is already part of a multi-selection, keep the
            // selection as-is (e.g. after Tile repositions and activates windows).
            if (_windowList.SelectedItems?.Count > 1 &&
                _windowList.SelectedItems.Contains(item))
                return;

            _windowList.SelectedItem = item;
        }

        private void SetSyncActive(bool active)
        {
            _isSyncActive = active;
            if (active)
            {
                _syncBtn.Content = "Unsync";
                _syncBtn.Classes.Add("sync-active");
                _syncBtn.ClearValue(Button.BackgroundProperty);
                _syncBtn.ClearValue(Button.ForegroundProperty);
                Resources["WindowListSelectedBorder"] = new SolidColorBrush(Color.Parse("#E57373"));

                // Save selection so it can be restored on click during Sync.
                // Only MatrixPlotter windows participate in sync; others are silently excluded.
                _syncSelectionSnapshot = _windowList.SelectedItems?
                    .OfType<WindowListItemViewModel>()
                    .Where(m => m.Window is MatrixPlotter)
                    .ToList();

                // Deselect any non-MatrixPlotter items so the list reflects the actual sync group.
                var toDeselect = _windowList.SelectedItems?
                    .OfType<WindowListItemViewModel>()
                    .Where(m => m.Window is not MatrixPlotter)
                    .ToList();
                if (toDeselect is { Count: > 0 })
                {
                    _processingSelectionChange = true;
                    try { foreach (var vm in toDeselect) _windowList.SelectedItems!.Remove(vm); }
                    finally { _processingSelectionChange = false; }
                }

                var plotters = _syncSelectionSnapshot?
                    .Select(m => m.Window as MatrixPlotter)
                    .Where(p => p != null)
                    .Cast<MatrixPlotter>()
                    .ToList() ?? [];
                _syncGroup = plotters.Count >= 2 ? new MatrixPlotterSyncGroup(plotters) : null;
                if (_syncGroup != null)
                    _syncGroup.DirtyChanged += OnSyncDirtyChanged;

                // Highlight each sync-group plotter window with a colored border.
                var syncBorderBrush = new SolidColorBrush(Color.Parse("#E57373"));
                foreach (var plotter in plotters)
                {
                    plotter.SetSyncBorder(syncBorderBrush);
                    _syncBorderedPlotters.Add(plotter);
                }
            }
            else
            {
                _syncBtn.Content = "Sync";
                _syncBtn.Classes.Remove("sync-active");
                _syncBtn.ClearValue(Button.BackgroundProperty);
                _syncBtn.ClearValue(Button.ForegroundProperty);
                Resources.Remove("WindowListSelectedBorder");
                if (_syncGroup != null)
                    _syncGroup.DirtyChanged -= OnSyncDirtyChanged;
                _syncGroup?.Dispose();
                _syncGroup = null;
                _syncSelectionSnapshot = null;

                // Remove the colored border highlight from each sync-group plotter.
                foreach (var plotter in _syncBorderedPlotters)
                    plotter.SetSyncBorder(null);
                _syncBorderedPlotters.Clear();

                HideRevertButton();
            }
        }

        /// <summary>Restores the ListBox selection to the sync-group snapshot.</summary>
        private void RestoreSyncSelection()
        {
            if (_syncSelectionSnapshot == null) return;
            _processingSelectionChange = true;
            try
            {
                _windowList.SelectedItems!.Clear();
                foreach (var item in _syncSelectionSnapshot)
                    _windowList.SelectedItems!.Add(item);
            }
            finally { _processingSelectionChange = false; }
        }

        // ── Revert button ─────────────────────────────────────────────────

        private void OnSyncDirtyChanged(object? sender, bool isDirty)
        {
            if (isDirty) ShowRevertButton();
            else HideRevertButton();
        }

        private void ShowRevertButton()
        {
            if (_revertBtn != null) return;

            _revertBtn = new Button
            {
                Content = "\u21a9 Revert",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#E57373")),
                Foreground = Brushes.White,
                FontSize = _syncBtn.FontSize - 1,
                Padding = new Thickness(4, 2),
            };
            _revertBtn.Click += (_, _) => _syncGroup?.Revert();

            // Place via OverlayLayer so it floats above normal content,
            // positioned just above the Sync button with a slide-down entrance.
            var overlay = OverlayLayer.GetOverlayLayer(_syncBtn);
            if (overlay == null) return;

            overlay.Children.Add(_revertBtn);
            PositionRevertButton(overlay);

            // Re-position when layout changes (window resize, etc.)
            EventHandler? layoutHandler = null;
            layoutHandler = (_, _) =>
            {
                if (_revertBtn == null) { _syncBtn.LayoutUpdated -= layoutHandler; return; }
                PositionRevertButton(overlay);
            };
            _syncBtn.LayoutUpdated += layoutHandler;
            _revertBtn.Tag = layoutHandler; // stash for cleanup

            // Slide-in animation: start offset above, animate to final position
            _revertBtn.Opacity = 0;
            _revertBtn.RenderTransform = new TranslateTransform(0, 10);
            Dispatcher.UIThread.Post(async () =>
            {
                if (_revertBtn == null) return;
                const int steps = 8;
                for (int i = 1; i <= steps; i++)
                {
                    if (_revertBtn == null) return;
                    double t = i / (double)steps;
                    _revertBtn.Opacity = t;
                    _revertBtn.RenderTransform = new TranslateTransform(0, 10 * (1 - t));
                    await Task.Delay(20);
                }
                if (_revertBtn != null)
                {
                    _revertBtn.Opacity = 1;
                    _revertBtn.RenderTransform = null;
                }
            }, DispatcherPriority.Background);
        }

        private void PositionRevertButton(OverlayLayer overlay)
        {
            if (_revertBtn == null) return;
            var pt = _syncBtn.TranslatePoint(new Point(0, 0), overlay);
            if (!pt.HasValue) return;
            // Slightly narrower than the Sync button, centered horizontally
            double btnW = _syncBtn.Bounds.Width;
            double revW = Math.Max(btnW * 0.8, 60);
            _revertBtn.Width = revW;
            Canvas.SetLeft(_revertBtn, pt.Value.X + (btnW - revW) / 2);
            double revH = _revertBtn.DesiredSize.Height > 0 ? _revertBtn.DesiredSize.Height : _syncBtn.Bounds.Height;
            Canvas.SetTop(_revertBtn, pt.Value.Y - revH - 6);
        }

        private void HideRevertButton()
        {
            if (_revertBtn == null) return;
            var btn = _revertBtn;
            _revertBtn = null;

            // Detach layout handler
            if (btn.Tag is EventHandler handler)
                _syncBtn.LayoutUpdated -= handler;

            // Remove from overlay
            var overlay = OverlayLayer.GetOverlayLayer(_syncBtn);
            overlay?.Children.Remove(btn);
        }

        private void OnAnyAppWindowActivated(object? sender, EventArgs e)
            => Topmost = true;

        private async void OnAnyAppWindowDeactivated(object? sender, EventArgs e)
        {
            await Task.Delay(80);
            if (_topmostEnabled) return;
            if (!IsCurrentForegroundOurProcess())
                Topmost = false;
        }

        private bool IsCurrentForegroundOurProcess()
        {
            if (!OperatingSystem.IsWindows())
                return false;
            nint fgHwnd = GetForegroundWindow();
            if (fgHwnd == 0) return false;
            GetWindowThreadProcessId(fgHwnd, out uint fgPid);
            return (int)fgPid == Environment.ProcessId;
        }

        [DllImport("user32.dll", SetLastError = false)]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = false)]
        private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    }
}