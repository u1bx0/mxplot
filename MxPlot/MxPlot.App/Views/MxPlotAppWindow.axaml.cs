using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Diagnostics;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Main dashboard window for MxPlot application.
    /// Manages open plot windows, synchronization, drag-drop loading, clipboard operations, and view modes.
    /// </summary>
    /// <remarks>
    /// <para><strong>Partial class organization:</strong></para>
    /// <list type="bullet">
    ///   <item><term>MxPlotAppWindow.axaml.cs</term>
    ///         <description>Core UI initialization, lifecycle management (minimize/restore/close), hamburger menu handlers,
    ///                      window list selection/activation, overlap avoidance, export orchestration, and topmost behavior.</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.Clipboard.cs</term>
    ///         <description>Clipboard detection (image/CSV/TSV), format conversion, plotter-choice dialogs,
    ///                      and profile data parsing for the "Open from Clipboard" feature.</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.Dialogs.cs</term>
    ///         <description>Reusable dialog helpers: error messages, about dialog, export overwrite confirmation,
    ///                      and toast notifications with fade-in/fade-out animations.</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.FileOperations.cs</term>
    ///         <description>File picker integration, loading-mode resolution for large files,
    ///                      new window positioning with cascade, and topmost suspension during dialogs.</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.Plugins.cs</term>
    ///         <description>Plugin menu rebuilding, plugin context implementation (<see cref="IMxPlotAppContext"/>),
    ///                      and test data generation handlers (Mandelbrot, Julia, Hyperstack, 2D linear scale).</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.SyncFeature.cs</term>
    ///         <description>Synchronization mode activation/deactivation, snapshot management,
    ///                      and revert-button overlay for dirty sync state.</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.ViewMode.cs</term>
    ///         <description>List display mode (Details/Icons), card size step resource updates,
    ///                      and view-mode control initialization (toggle buttons, slider, wheel zoom).</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.WindowManagement.cs</term>
    ///         <description>Window focus synchronization, dynamic context menu generation for rename/show/hide/close,
    ///                      and selection cleanup before tile/sync operations.</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.ContextMenu.cs</term>
    ///         <description>Reserved for future menu-related consolidation (currently empty; context menu logic resides in WindowManagement).</description>
    ///   </item>
    ///   <item><term>MxPlotAppWindow.WelcomeAnimation.cs</term>
    ///         <description>One-time startup reveal: the empty-state hint fades in after the window is shown.</description>
    ///   </item>
    /// </list>
    /// </remarks>
    public partial class MxPlotAppWindow : Window
    {
        private ListBox _windowList = null!;
        private Button _viewDetailsBtn = null!;
        private Button _viewIconsBtn = null!;
        private Slider _cardSizeSlider = null!;
        private int _cardSizeStep = 2;
        private bool _applyingCardSize = false;
        private ViewMode _viewMode = ViewMode.Details;
        private bool _processingSelectionChange = false;
        private Button _syncBtn = null!;
        private Button? _revertBtn;
        private bool _isSyncActive = false;
        private MatrixPlotterSyncGroup? _syncGroup;
        private List<WindowListItemViewModel>? _syncSelectionSnapshot;
        private List<MatrixPlotter> _syncBorderedPlotters = [];

        /// <summary>Re-entry guard for overlap-avoidance repositioning.</summary>
        private bool _adjustingOverlap = false;

        /// <summary>
        /// Set to <see langword="true"/> just before activating a window from the dashboard list,
        /// so that <see cref="OnAnyAppWindowActivated"/> knows to skip the dashboard-dodge path
        /// (the list-click path handles repositioning via <see cref="TryAvoidDashboardOverlap"/> instead),
        /// and so <see cref="OnManagedWindowFocused"/> knows to ignore an <c>Activated</c> that fired
        /// as a side effect of a purely programmatic <c>Show()</c>/<c>Activate()</c> -- e.g. restoring
        /// every managed window when the dashboard itself un-minimizes -- rather than a real user click.
        /// Without this, each restored window's <c>Activated</c> (deferred via <c>Dispatcher.Post</c> in
        /// <see cref="MxPlotAppViewModel.RegisterWindow"/>) re-selects it in the list, which re-activates
        /// it, which can re-fire <c>Activated</c>; observed as the whole restored group cycling focus
        /// indefinitely on macOS with 3+ windows, where re-activating an already-active window is not
        /// the no-op it is on Windows.
        /// </summary>
        private bool _activatingFromList = false;

        private Border _toastPanel = null!;
        private TextBlock _toastText = null!;
        private CancellationTokenSource? _toastCts;

        /// <summary>
        /// Whether Shift was down on the last DragOver tick, so the "will prompt for loading
        /// mode" toast fires once per Shift press rather than on every tick (DragOver fires
        /// continuously on mouse move). Null while no file drag is in progress over the window list.
        /// </summary>
        private bool? _dragHintShiftState;

        /// <summary>Threshold above which the loading-mode dialog is shown.</summary>
        private const long LargeFileThresholdBytes = 500 * 1024 * 1024;

        private MxPlotAppViewModel ViewModel => (MxPlotAppViewModel)DataContext!;

        public MxPlotAppWindow()
        {
#if DEBUG
            this.AttachDevTools();
#endif

            InitializeComponent();
            SetupWelcomeAnimation();

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
                // Guard the whole batch (not just each individual Show/Activate call) against the
                // Activated-driven list-selection sync in MxPlotAppViewModel.RegisterWindow, which
                // is deferred via Dispatcher.Post -- so the reset below is *also* posted, landing
                // after every Show() call in this loop has already had its chance to enqueue its own
                // deferred continuation. See _activatingFromList's remarks for why this matters.
                _activatingFromList = true;
                try
                {
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
                }
                finally
                {
                    Dispatcher.UIThread.Post(() => _activatingFromList = false, DispatcherPriority.Background);
                }
            };

            // ── Close all managed windows when MxPlotAppWindow exits ──────────
            Closed += (_, _) =>
            {
                foreach (var item in ViewModel.ManagedWindows.ToList())
                    item.Window.Close();
            };

            // ── Exit guard: suppress per-window dialogs after the app-exit check ──

            // ── Window List: drag-drop (on the Panel so empty-state area accepts drops too) ──
            // DataContext is set after the constructor via object initializer in App.axaml.cs,
            // so wire PositionWindowAction once DataContext becomes available.
            DataContextChanged += (_, _) =>
            {
                if (DataContext is not MxPlotAppViewModel vm) return;
                vm.PositionWindowAction = (w, i) => PositionNewWindow(w, i);
                vm.DashboardWindow = this;
                vm.WindowFocusedAction = OnManagedWindowFocused;
                vm.WindowSelectionClickedAction = OnManagedWindowSelectionClicked;
            };

            var listPanel = this.FindControl<Panel>("ListPanel")!;
            DragDrop.SetAllowDrop(listPanel, true);
            listPanel.AddHandler(DragDrop.DropEvent, async (_, e) =>
            {
                _dragHintShiftState = null;
                if (e.Data.GetFiles() is { } files)
                {
                    // Shift held at drop time forces the InMemory/Virtual prompt even for
                    // small files, instead of silently resolving via LoadingMode.Auto.
                    bool forcePrompt = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    foreach (var item in files)
                    {
                        var path = item.TryGetLocalPath();
                        if (path != null)
                            await ViewModel.LoadAndOpenFileAsync(path, this, forcePrompt);
                    }
                }
            });
            listPanel.AddHandler(DragDrop.DragEnterEvent, (_, __) => _dragHintShiftState = null);
            listPanel.AddHandler(DragDrop.DragLeaveEvent, (_, __) => _dragHintShiftState = null);
            listPanel.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                bool hasFiles = e.Data.GetFiles() != null;
                e.DragEffects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
                if (!hasFiles) return;

                // No hint toast for the default (no-modifier) case — only announce the
                // Shift-forces-prompt behavior when the user actually presses Shift, and
                // only once per press (DragOver fires continuously on mouse move).
                bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                if (shift && _dragHintShiftState != true)
                {
                    _dragHintShiftState = true;
                    _ = ShowToastAsync("Will prompt for loading mode");
                }
                else if (!shift)
                {
                    _dragHintShiftState = false;
                }
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
                        _activatingFromList = true;
                        single.Window.Activate();
                        _activatingFromList = false;
                        var w1 = single.Window;
                        Dispatcher.UIThread.Post(() => TryAvoidDashboardOverlap(w1), DispatcherPriority.Background);
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
                if (_isSyncActive) return;
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
                    _activatingFromList = true;
                    vm.Window.Activate();
                    _activatingFromList = false;
                    var w2 = vm.Window;
                    Dispatcher.UIThread.Post(() => TryAvoidDashboardOverlap(w2), DispatcherPriority.Background);
                }
            }, RoutingStrategies.Tunnel);

            // ── View mode toggle buttons ──────────────────────────────────
            InitializeViewModeControls();

            // Tile: after the command runs, deselect hidden items so only visible windows remain selected.
            // Dispatcher.UIThread.Post ensures this runs after TileWindowsCommand.Execute() completes.
            var tileBtn = this.FindControl<Button>("TileBtn");
            if (tileBtn != null)
            {
                tileBtn.Click += (_, _) =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(DeselectHiddenItems);
            }

            _syncBtn = this.FindControl<Button>("SyncBtn")!;
            _syncBtn.Click += (_, _) =>
            {
                if (!_isSyncActive) DeselectHiddenItems();
                if ((_windowList.SelectedItems?.Count ?? 0) >= 2)
                    SetSyncActive(!_isSyncActive);
            };

            // ── Window list context menu (dynamic) ────────────────────────────
            _windowList.ContextRequested += OnWindowListContextRequested;

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

            // Refresh clipboard-usability state each time the hamburger menu opens
            // ("Open from Clipboard…" IsEnabled is bound to ViewModel.IsClipboardUsable in AXAML).
            flyout.Opened += async (_, _) =>
            {
                ViewModel.IsClipboardUsable = await ClipboardHasUsableDataAsync();
            };

            // Dashboard stays above own plot windows; drops behind other apps when they take focus.
            Topmost = true;
            Activated += OnAnyAppWindowActivated;
            Deactivated += OnAnyAppWindowDeactivated;
            Action<Window, IMatrixData?> plotWindowSetup = (w, _) =>
            {
                w.Activated += OnAnyAppWindowActivated;
                w.Deactivated += OnAnyAppWindowDeactivated;
                // Maximize case: dock the dashboard against the screen edge instead of
                // leaving it wherever it happened to be sitting under the now-fullscreen window.
                EventHandler<AvaloniaPropertyChangedEventArgs> onPropertyChanged = (_, e) =>
                {
                    if (e.Property == WindowStateProperty && w.WindowState == WindowState.Maximized)
                        Dispatcher.UIThread.Post(() => TryDockDashboardOnMaximize(w), DispatcherPriority.Background);
                };
                w.PropertyChanged += onPropertyChanged;
                w.Closed += (_, _) =>
                {
                    w.Activated -= OnAnyAppWindowActivated;
                    w.Deactivated -= OnAnyAppWindowDeactivated;
                    w.PropertyChanged -= onPropertyChanged;
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

        /// <summary>
        /// Toggles the "Always on Top" user override. When enabled, <see cref="OnAnyAppWindowDeactivated"/>
        /// skips dropping <see cref="Topmost"/> on focus loss. The immediate effect (pin now / re-evaluate
        /// against the current foreground window) happens here since it touches native window state that
        /// isn't expressed as a binding.
        /// </summary>
        private void HamburgerToggleAlwaysOnTop_Click(object? sender, RoutedEventArgs e)
        {
            ViewModel.IsAlwaysOnTop = !ViewModel.IsAlwaysOnTop;
            Topmost = ViewModel.IsAlwaysOnTop || IsCurrentForegroundOurProcess();
        }

        private bool _suppressExitConfirmation = false;
        private bool _isCheckingExit = false;

        /*
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (!_suppressExitConfirmation)
            {
                var unsaved = ViewModel.ManagedWindows
                    .OfType<MatrixPlotterListItemViewModel>()
                    .Where(vm => vm.HasUnsavedChanges)
                    .ToList();

                if (unsaved.Count > 0)
                {
                    e.Cancel = true;
                    var titles = unsaved
                        .Select(vm => vm.FileName)
                        .ToList();
                    bool discard = await UnsavedChangesConfirmDialog.ShowAsync(this, titles);
                    if (discard)
                    {
                        // Suppress both the app-level guard and each individual plotter's dialog.
                        _suppressExitConfirmation = true;
                        foreach (var item in ViewModel.ManagedWindows.ToList())
                            if (item.Window is MatrixPlotter p)
                                p.SuppressCloseConfirmation = true;
                        
                        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                        {
                            desktopLifetime.Shutdown();
                        }
                        else
                        {
                            Close(); 
                        }
                    }
                    return;
                }
            }
            base.OnClosing(e);
        }
        */

        /// <summary>
        /// Intercepts the window closing event to check for unsaved changes across all managed plotters.
        /// Implements re-entrancy protection, gracefully handles macOS lifecycle differences,
        /// and enforces application-wide modality by temporarily disabling background windows.
        /// </summary>
        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            // Pass through if the exit is already confirmed (e.g., the second pass from Shutdown)
            if (_suppressExitConfirmation)
            {
                base.OnClosing(e);
                return;
            }

            var unsaved = ViewModel.ManagedWindows
                .OfType<MatrixPlotterListItemViewModel>()
                .Where(vm => vm.HasUnsavedChanges)
                .ToList();

            // If there are no unsaved changes, proceed with normal shutdown
            if (unsaved.Count == 0)
            {
                base.OnClosing(e);
                return;
            }

            // Unsaved changes exist: cancel the OS-level close request to show the dialog
            e.Cancel = true;

            // Prevent re-entrancy (e.g., if the user spams Cmd+Q or the close button)
            if (_isCheckingExit) return;

            _isCheckingExit = true;
            try
            {
                // Lock all child plotter windows to prevent the user from editing or saving
                // data in the background while the exit confirmation dialog is open.
                foreach (var item in ViewModel.ManagedWindows)
                {
                    if (item.Window is MatrixPlotter p)
                    {
                        p.IsEnabled = false;
                    }
                }

                var titles = unsaved.Select(vm => vm.FileName).ToList();
                bool discard = await UnsavedChangesConfirmDialog.ShowAsync(this, titles);

                if (discard)
                {
                    // The user chose to discard changes. Suppress both the app-level guard 
                    // and each individual plotter's dialog to allow a clean exit.
                    _suppressExitConfirmation = true;
                    foreach (var item in ViewModel.ManagedWindows.ToList())
                    {
                        if (item.Window is MatrixPlotter p)
                        {
                            p.SuppressCloseConfirmation = true;
                        }
                    }

                    // Re-request the application shutdown. This triggers OnClosing again,
                    // but this time _suppressExitConfirmation is true, so it will pass through.
                    // This ensures macOS cleanly terminates the process instead of leaving it in the Dock.
                    if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                    {
                        desktopLifetime.Shutdown();
                    }
                    else
                    {
                        Close(); // Fallback for environments without classic desktop lifetime
                    }
                }
            }
            catch (Exception ex)
            {
                // Catch exceptions to prevent async void from crashing the entire process
                Console.WriteLine($"[Warning] Exception during exit confirmation: {ex.Message}");
            }
            finally
            {
                _isCheckingExit = false;

                // If the user cancelled the exit (discard == false), unlock the child windows
                // so normal application operation can resume.
                if (!_suppressExitConfirmation)
                {
                    foreach (var item in ViewModel.ManagedWindows)
                    {
                        if (item.Window is MatrixPlotter p)
                        {
                            p.IsEnabled = true;
                        }
                    }
                }
            }
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

        private void OnAnyAppWindowActivated(object? sender, EventArgs e)
        {
            Topmost = true;
            if (sender is Window activatedWindow && activatedWindow != this && !_activatingFromList)
            {
                var w3 = activatedWindow;
                Dispatcher.UIThread.Post(() => TryDodgeDashboardFromPlotWindow(w3), DispatcherPriority.Background);
            }
        }

        // ── Overlap avoidance helpers ──────────────────────────────────────────

        /// <summary>
        /// When activating a plot window from the dashboard list, moves the plot window
        /// to the right or left of the dashboard if they overlap (X-axis only).
        /// Skips if the window is maximised/fullscreen or overlap avoidance is disabled.
        /// </summary>
        private void TryAvoidDashboardOverlap(Window plotWindow)
        {
            if (!ViewModel.IsAvoidWindowOverlapEnabled) return;
            if (_adjustingOverlap) return;
            // Guards against a stray Activated/PositionChanged event that can fire while a
            // window is in the middle of closing, at which point its Position/Bounds may
            // already be stale (e.g. reset towards the screen origin) — without this check,
            // that phantom rect can look like it overlaps the dashboard and drag it sideways
            // for no visible reason when the user closes the last plot window.
            if (!plotWindow.IsVisible) return;
            if (plotWindow.WindowState != WindowState.Normal) return;
            if (WindowState != WindowState.Normal) return;

            var screen = Screens.All.FirstOrDefault(s =>
                plotWindow.Position.X >= s.Bounds.X && plotWindow.Position.X < s.Bounds.X + s.Bounds.Width &&
                plotWindow.Position.Y >= s.Bounds.Y && plotWindow.Position.Y < s.Bounds.Y + s.Bounds.Height)
                ?? Screens.Primary;
            if (screen == null) return;

            double sc = screen.Scaling;
            var wb = screen.WorkingArea;
            var screenRect = new Rect(wb.X / sc, wb.Y / sc, wb.Width / sc, wb.Height / sc);
            var dashRect = new Rect(Position.X / sc, Position.Y / sc, Bounds.Width, Bounds.Height);
            var plotRect = new Rect(plotWindow.Position.X / sc, plotWindow.Position.Y / sc,
                plotWindow.Bounds.Width, plotWindow.Bounds.Height);

            if (!dashRect.Intersects(plotRect)) return;

            const double Gap = 8.0;

            double rightX = dashRect.Right + Gap;
            double rightOverflow = Math.Max(0, rightX + plotRect.Width - screenRect.Right);

            double leftX = dashRect.Left - Gap - plotRect.Width;
            double leftOverflow = Math.Max(0, screenRect.Left - leftX);

            double newX = rightOverflow <= leftOverflow
                ? rightX
                : Math.Max(screenRect.Left, leftX);
            newX = Math.Max(screenRect.Left, Math.Min(newX, screenRect.Right - plotRect.Width));

            _adjustingOverlap = true;
            try { plotWindow.Position = new PixelPoint((int)(newX * sc), plotWindow.Position.Y); }
            finally { _adjustingOverlap = false; }
        }

        /// <summary>
        /// When a plot window gains focus directly, moves the dashboard to the left or right
        /// of that window if they overlap (X-axis only).
        /// Skips during sync, maximised/fullscreen states, or when avoidance is disabled.
        /// </summary>
        private void TryDodgeDashboardFromPlotWindow(Window plotWindow)
        {
            if (!ViewModel.IsAvoidWindowOverlapEnabled) return;
            if (_adjustingOverlap) return;
            if (_isSyncActive) return;
            // See the matching comment in TryAvoidDashboardOverlap: skip windows that are
            // already closing, whose Position/Bounds can no longer be trusted.
            if (!plotWindow.IsVisible) return;
            if (plotWindow.WindowState != WindowState.Normal) return;
            if (WindowState != WindowState.Normal) return;

            var screen = Screens.All.FirstOrDefault(s =>
                plotWindow.Position.X >= s.Bounds.X && plotWindow.Position.X < s.Bounds.X + s.Bounds.Width &&
                plotWindow.Position.Y >= s.Bounds.Y && plotWindow.Position.Y < s.Bounds.Y + s.Bounds.Height)
                ?? Screens.Primary;
            if (screen == null) return;

            double sc = screen.Scaling;
            var wb = screen.WorkingArea;
            var screenRect = new Rect(wb.X / sc, wb.Y / sc, wb.Width / sc, wb.Height / sc);
            var dashRect = new Rect(Position.X / sc, Position.Y / sc, Bounds.Width, Bounds.Height);
            var plotRect = new Rect(plotWindow.Position.X / sc, plotWindow.Position.Y / sc,
                plotWindow.Bounds.Width, plotWindow.Bounds.Height);

            if (!dashRect.Intersects(plotRect)) return;

            const double Gap = 8.0;

            double leftX = plotRect.Left - Gap - dashRect.Width;
            double leftOverflow = Math.Max(0, screenRect.Left - leftX);

            double rightX = plotRect.Right + Gap;
            double rightOverflow = Math.Max(0, rightX + dashRect.Width - screenRect.Right);

            double newX = leftOverflow <= rightOverflow
                ? Math.Max(screenRect.Left, leftX)
                : Math.Min(screenRect.Right - dashRect.Width, rightX);
            newX = Math.Max(screenRect.Left, Math.Min(newX, screenRect.Right - dashRect.Width));

            _adjustingOverlap = true;
            try { Position = new PixelPoint((int)(newX * sc), Position.Y); }
            finally { _adjustingOverlap = false; }
        }

        /// <summary>
        /// When a plot window is maximized, docks the dashboard against the left edge of that
        /// window's screen (staying Topmost so it remains reachable) instead of leaving it
        /// wherever it happened to be sitting, now buried under the fullscreen content.
        /// No-op if the dashboard is already at the edge, or sits on a different screen.
        /// </summary>
        private void TryDockDashboardOnMaximize(Window plotWindow)
        {
            if (!ViewModel.IsAvoidWindowOverlapEnabled) return;
            if (_adjustingOverlap) return;
            if (_isSyncActive) return;
            if (!plotWindow.IsVisible) return;
            if (plotWindow.WindowState != WindowState.Maximized) return;
            if (WindowState != WindowState.Normal) return;

            var screen = Screens.ScreenFromWindow(plotWindow);
            var dashScreen = Screens.ScreenFromWindow(this);
            if (screen == null || dashScreen == null || dashScreen.Bounds != screen.Bounds) return;

            double sc = screen.Scaling;
            var wb = screen.WorkingArea;
            var screenRect = new Rect(wb.X / sc, wb.Y / sc, wb.Width / sc, wb.Height / sc);
            var dashRect = new Rect(Position.X / sc, Position.Y / sc, Bounds.Width, Bounds.Height);

            const double Gap = 8.0;
            double targetX = screenRect.Left + Gap;
            if (Math.Abs(dashRect.Left - targetX) < 1.0) return;

            double newY = Math.Max(screenRect.Top, Math.Min(dashRect.Top, screenRect.Bottom - dashRect.Height));

            _adjustingOverlap = true;
            try { Position = new PixelPoint((int)(targetX * sc), (int)(newY * sc)); }
            finally { _adjustingOverlap = false; }
        }

        private async void OnAnyAppWindowDeactivated(object? sender, EventArgs e)
        {
            if (ViewModel.IsAlwaysOnTop)
                return;

            if (!OperatingSystem.IsWindows())
            {
                Topmost = false;
                return;
            }

            await Task.Delay(80);
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
