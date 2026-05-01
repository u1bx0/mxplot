using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.App.ViewModels
{
    /// <summary>
    /// ViewModel for the main dashboard window.
    /// Manages all plot windows (MatrixPlotter, ProfilePlotter, etc.) and provides
    /// collective operations (Tile, Sync, Close).
    /// </summary>
    public partial class MxPlotAppViewModel : ViewModelBase
    {
        public ObservableCollection<WindowListItemViewModel> ManagedWindows { get; } = [];

        [ObservableProperty]
        private bool _hasSelection;

        [ObservableProperty]
        private bool _hasMultiSelection;

        /// <summary>True when two or more <em>visible</em> windows are selected (required for Tile and Sync).</summary>
        [ObservableProperty]
        private bool _hasVisibleMultiSelection;

        [ObservableProperty]
        private bool _hasExportableSelection;

        [ObservableProperty]
        private bool _isIconView;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _loadingMessage = string.Empty;

        /// <summary>Total frame count reported by the reader. 0 means the total is unknown (indeterminate).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLoadingIndeterminate))]
        private int _loadingTotal;

        [ObservableProperty]
        private int _loadingProgress;

        [ObservableProperty]
        private bool _isCancelVisible;

        public bool IsLoadingIndeterminate => _loadingTotal <= 0;

        private CancellationTokenSource? _loadingCts;

        /// <summary>Callback set by the View to position a newly created plot window.</summary>
        internal Action<Window, int>? PositionWindowAction { get; set; }

        /// <summary>Set by the View so <see cref="TileWindows"/> can avoid the dashboard's area.</summary>
        internal Window? DashboardWindow { get; set; }

        /// <summary>Set by the View to sync list selection when a managed window is activated.</summary>
        internal Action<Window>? WindowFocusedAction { get; set; }

        [RelayCommand]
        private void CancelLoading() => _loadingCts?.Cancel();

        /// <summary>True when no windows are managed; used to show the empty-state hint in the list area.</summary>
        public bool HasNoWindows => ManagedWindows.Count == 0;

        /// <summary>Newline-separated list of file extensions supported by registered readers.</summary>
        public string SupportedFormatsText =>
            string.Join("  ", FormatRegistry.ReaderDescriptors
                .SelectMany(d => d.DialogPatterns)
                .Where(p => p.StartsWith("*."))
                .Select(p => p[1..])   // "*.tif" → ".tif"
                .Distinct()
                .OrderBy(e => e));

        public MxPlotAppViewModel()
        {
            // Subscribe to PlotWindowNotifier for automatic registration of new windows
            PlotWindowNotifier.PlotWindowCreated += OnPlotWindowCreated;

            ManagedWindows.CollectionChanged += (_, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
                {
                    // Unsubscribe closed-window handlers
                    foreach (WindowListItemViewModel item in e.OldItems)
                        item.Window.Closed -= OnManagedWindowClosed;
                }
                OnPropertyChanged(nameof(HasNoWindows));
            };
        }

        /// <summary>
        /// Called by <see cref="PlotWindowNotifier"/> whenever any plot window is created
        /// anywhere in the application (including grandchild windows).
        /// </summary>
        private void OnPlotWindowCreated(Window window, IMatrixData? data)
        {
            // Suppress taskbar entries — all window management goes through MxPlot.App.
            // Must be set before Show() is called (called synchronously before the caller's .Show()).
            window.ShowInTaskbar = false;
            // Position the window before Show() so it opens directly in the right place.
            PositionWindowAction?.Invoke(window, ManagedWindows.Count);
            // NOTE: ConsumeParentLink is called inside RegisterWindow (posted), NOT here.
            // Reason: NotifyCreated fires synchronously from inside the child window's constructor,
            // BEFORE the caller has had a chance to call SetParentLink.
            // By the time the posted RegisterWindow executes, SetParentLink has already been called.
            Dispatcher.UIThread.Post(() => RegisterWindow(window, data));
        }

        /// <summary>Registers a window into the managed list and wires up its Closed event.</summary>
        internal void RegisterWindow(Window window, IMatrixData? data, Window? parentWindow = null)
        {
            // Avoid duplicate registration
            if (ManagedWindows.Any(m => m.Window == window)) return;

            // Consume parent link here — SetParentLink is guaranteed to have been called
            // by the time this posted method runs (it's called after the child ctor returns).
            var resolvedParent = parentWindow ?? PlotWindowNotifier.ConsumeParentLink(window);

            var item = window switch
            {
                MatrixPlotter plotter => (WindowListItemViewModel)new MatrixPlotterListItemViewModel(plotter, data),
                ProfilePlotter => new WindowListItemViewModel(window,
                                                 new Uri("avares://MxPlot/Assets/profile_plot.png")),
                _ => new WindowListItemViewModel(window),
            };

            // Wire up parent-child relationship
            var parentItem = resolvedParent is not null
                ? ManagedWindows.FirstOrDefault(m => m.Window == resolvedParent)
                : null;
            if (parentItem is not null)
            {
                item.ParentItem = parentItem;
                parentItem.ChildItems.Add(item);
            }

            window.Closed += OnManagedWindowClosed;
            window.Activated += (_, _) => WindowFocusedAction?.Invoke(window);

            // Tab / Shift+Tab during inline rename: commit current name and move to next/prev item.
            EventHandler<bool> onNav = (sender, forward) =>
            {
                if (sender is not WindowListItemViewModel current) return;
                int idx = ManagedWindows.IndexOf(current);
                if (idx < 0) return;
                int count = ManagedWindows.Count;
                int next = forward
                    ? (idx + 1) % count
                    : (idx - 1 + count) % count;
                ManagedWindows[next].RenameCommand.Execute(null);
            };
            item.RenameNavigationRequested += onNav;
            window.Closed += (_, _) => item.RenameNavigationRequested -= onNav;

            // Insert after the parent's subtree, or append at root level.
            if (parentItem is not null)
                ManagedWindows.Insert(FindSubtreeEndIndex(parentItem) + 1, item);
            else
                ManagedWindows.Add(item);
        }

        /// <summary>
        /// Returns the index of the last item in <paramref name="root"/>'s subtree
        /// within <see cref="ManagedWindows"/>.
        /// </summary>
        private int FindSubtreeEndIndex(WindowListItemViewModel root)
        {
            int rootIdx = ManagedWindows.IndexOf(root);
            if (rootIdx < 0) return ManagedWindows.Count - 1;
            int last = rootIdx;
            for (int i = rootIdx + 1; i < ManagedWindows.Count; i++)
            {
                if (IsDescendant(ManagedWindows[i], root)) last = i;
                else break;
            }
            return last;
        }

        private static bool IsDescendant(WindowListItemViewModel item, WindowListItemViewModel ancestor)
        {
            var p = item.ParentItem;
            while (p is not null)
            {
                if (p == ancestor) return true;
                p = p.ParentItem;
            }
            return false;
        }

        private void OnManagedWindowClosed(object? sender, EventArgs e)
        {
            if (sender is not Window w) return;
            Dispatcher.UIThread.Post(() =>
            {
                var item = ManagedWindows.FirstOrDefault(m => m.Window == w);
                if (item == null) return; // already removed (e.g. by a parent cascade)
                RemoveSubtree(item);
                RefreshSelectionState();
            });
        }

        /// <summary>
        /// Recursively closes and removes <paramref name="item"/> and all its descendants
        /// from <see cref="ManagedWindows"/>.
        /// Children are closed depth-first; each child's <see cref="OnManagedWindowClosed"/>
        /// subscription is removed before <see cref="Window.Close"/> is called to prevent
        /// double-processing.
        /// </summary>
        private void RemoveSubtree(WindowListItemViewModel item)
        {
            foreach (var child in item.ChildItems.ToArray())
            {
                child.Window.Closed -= OnManagedWindowClosed;
                child.Window.Close();
                RemoveSubtree(child);
            }
            item.ChildItems.Clear();
            item.ParentItem?.ChildItems.Remove(item);
            ManagedWindows.Remove(item);
        }

        /// <summary>
        /// Loads a file via <see cref="FormatRegistry"/> and opens it in a new <see cref="MatrixPlotter"/>.
        /// Called from code-behind after file dialog or drag-and-drop.
        /// </summary>
        internal async Task LoadAndOpenFileAsync(string path, Views.MxPlotAppWindow owner)
        {
            var fileName = Path.GetFileName(path);
            try
            {
                var mode = await owner.ResolveLoadingModeAsync(path);
                if (mode == null) return; // user cancelled

                var loadMode = mode.Value;

                var reader = FormatRegistry.CreateReader(path)
                    ?? throw new NotSupportedException(
                        $"No reader registered for '{Path.GetExtension(path)}'.");

                // Show progress overlay while the file is being read.
                _loadingCts = new CancellationTokenSource();
                IsLoading = true;
                LoadingMessage = $"Loading  {fileName}…";

                // Progress<int> captures the UI SynchronizationContext at construction time,
                // so the callback is automatically marshalled back to the UI thread.
                int totalFrames = 0;
                var progress = new Progress<int>(value =>
                {
                    if (value < 0)
                    {
                        // Negative: reader is reporting the total frame count.
                        totalFrames = -value;
                        LoadingTotal = totalFrames;
                        LoadingProgress = 0;
                        LoadingMessage = $"Loading  {fileName}…  0 / {totalFrames}";
                    }
                    else if (value < totalFrames)
                    {
                        // 0-based index of the frame just completed.
                        LoadingProgress = value + 1;
                        LoadingMessage = $"Loading  {fileName}…  {LoadingProgress} / {totalFrames}";
                    }
                    // Final positive report (value == totalFrames) is swallowed here;
                    // the finally block clears LoadingMessage.
                });

                if (reader is IVirtualLoadable vl) vl.LoadingMode = loadMode;
                if (reader is IProgressReportable p) p.ProgressReporter = progress;

                var ct = _loadingCts.Token;
                IsCancelVisible = reader.IsCancellable;
                if (reader.IsCancellable) reader.CancellationToken = ct;

                var data = await Task.Run(() => reader.Read(path), ct);

                // MatrixPlotter.Create fires PlotWindowNotifier → auto-registered
                MatrixPlotter.Create(data, ColorThemes.Grayscale, fileName, path).Show();
            }
            catch (OperationCanceledException)
            {
                // User cancelled loading — no error dialog needed.
            }
            catch (Exception ex)
            {
                await owner.ShowErrorAsync($"Failed to open '{fileName}':\n{ex.Message}");
            }
            finally
            {
                IsLoading = false;
                IsCancelVisible = false;
                LoadingMessage = string.Empty;
                LoadingTotal = 0;
                LoadingProgress = 0;
                _loadingCts?.Dispose();
                _loadingCts = null;
            }
        }

        // ── Commands ─────────────────────────────────────────────────────

        [RelayCommand]
        private void TileWindows()
        {
            var selected = ManagedWindows.Where(m => m.IsSelected && m.IsWindowVisible).ToList();
            if (selected.Count == 0) return;

            var windows = selected.Select(m => m.Window).ToList();
            var screen = windows[0].Screens.ScreenFromWindow(windows[0])
                       ?? windows[0].Screens.Primary;
            if (screen == null) return;

            SmartTileWindows(windows, DashboardWindow, screen);
        }

        private static void SmartTileWindows(IList<Window> windows, Window? dashboardWindow, Avalonia.Platform.Screen screen)
        {
            int count = windows.Count;
            if (count == 0) return;

            double scaling = screen.Scaling;
            var wb = screen.WorkingArea;
            Rect screenRect = new(
                wb.X / scaling, wb.Y / scaling,
                wb.Width / scaling, wb.Height / scaling);

            const double WindowGap = 16.0;
            const double VerticalGap = 48.0;
            const double MinWidth = 280.0;
            const double MinHeight = 200.0;

            // --- 1. Compute AvailableRect by subtracting the dashboard area ---
            Rect availableRect = screenRect;

            if (dashboardWindow != null)
            {
                Rect dashRect = new(
                    dashboardWindow.Position.X / scaling,
                    dashboardWindow.Position.Y / scaling,
                    dashboardWindow.Bounds.Width,
                    dashboardWindow.Bounds.Height);

                if (dashRect.Intersects(screenRect))
                {
                    if (dashRect.Center.X < screenRect.Center.X)
                    {
                        // Dashboard on the left: use the right side
                        double newX = dashRect.Right + WindowGap;
                        availableRect = new Rect(newX, screenRect.Y,
                            screenRect.Right - newX, screenRect.Height);
                    }
                    else
                    {
                        // Dashboard on the right: use the left side
                        double newW = dashRect.Left - screenRect.X - WindowGap;
                        availableRect = new Rect(screenRect.X, screenRect.Y,
                            newW, screenRect.Height);
                    }

                    // Fallback: if the remaining area is too narrow, ignore avoidance
                    if (availableRect.Width < MinWidth || availableRect.Height < MinHeight)
                        availableRect = screenRect;
                }
            }

            // --- 2. Capture aspect ratios before touching WindowState ---
            const double FallbackAspect = 4.0 / 3.0;
            double[] aspects = new double[count];
            for (int i = 0; i < count; i++)
            {
                double w = windows[i].Bounds.Width;
                double h = windows[i].Bounds.Height;
                aspects[i] = (windows[i].WindowState == WindowState.Normal && w > 10 && h > 10)
                    ? w / h : FallbackAspect;
            }

            // --- 3. Restore all windows to Normal ---
            foreach (var win in windows)
            {
                if (win.WindowState != WindowState.Normal)
                    win.WindowState = WindowState.Normal;
            }

            // --- 4. Proportional row-packing (Mission Control style) ---
            int cols = (int)Math.Ceiling(Math.Sqrt(count));
            int rows = (int)Math.Ceiling((double)count / cols);

            double rowH = (availableRect.Height - VerticalGap * (rows + 1)) / rows;
            double currentY = availableRect.Y + VerticalGap;

            for (int r = 0; r < rows; r++)
            {
                int startIdx = r * cols;
                int rowCount = Math.Min(cols, count - startIdx);
                double aspectSum = 0;
                for (int j = 0; j < rowCount; j++) aspectSum += aspects[startIdx + j];

                double maxTotalW = availableRect.Width - WindowGap * (rowCount + 1);
                double scaledRowH = aspectSum > 0 && aspectSum * rowH > maxTotalW
                    ? maxTotalW / aspectSum
                    : rowH;
                scaledRowH = Math.Max(scaledRowH, MinHeight);

                double currentX = availableRect.X + WindowGap;
                for (int j = 0; j < rowCount; j++)
                {
                    int idx = startIdx + j;
                    var win = windows[idx];
                    double winW = Math.Max(aspects[idx] * scaledRowH, MinWidth);

                    win.Width = winW;
                    win.Height = scaledRowH;
                    win.Position = new PixelPoint(
                        (int)(currentX * scaling),
                        (int)(currentY * scaling));
                    currentX += winW + WindowGap;
                    win.Activate();
                }
                currentY += scaledRowH + VerticalGap;
            }
        }

        [RelayCommand]
        private void SyncWindows()
        {
            var selected = ManagedWindows.Where(m => m.IsSelected).ToList();
            if (selected.Count < 2) return;

            // Link MatrixPlotters via existing LinkRefresh mechanism
            var plotters = selected
                .Select(m => m.Window)
                .OfType<MatrixPlotter>()
                .ToList();
            if (plotters.Count < 2) return;

            var primary = plotters[0];
            for (int i = 1; i < plotters.Count; i++)
                primary.LinkRefresh(plotters[i]);
        }

        [RelayCommand]
        private void CloseSelectedWindows()
        {
            var selected = ManagedWindows.Where(m => m.IsSelected).ToList();
            foreach (var item in selected)
                item.Window.Close();
            // OnManagedWindowClosed handles removal from the collection
        }

        /// <summary>
        /// Called from code-behind after ListBox selection changes.
        /// Updates <see cref="HasSelection"/> for action bar binding.
        /// </summary>
        internal void RefreshSelectionState()
        {
            var count = ManagedWindows.Count(m => m.IsSelected);
            HasSelection = count > 0;
            HasMultiSelection = count >= 2;
            HasVisibleMultiSelection = ManagedWindows.Count(m => m.IsSelected && m.IsWindowVisible) >= 2;
            HasExportableSelection = ManagedWindows.Any(m => m.IsSelected && m is IExportableAsImage);
        }
    }
}
