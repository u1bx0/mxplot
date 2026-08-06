using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.App.ViewModels;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── Sync feature ──────────────────────────────────────────────────

        /// <summary>
        /// Activates or deactivates the window synchronization feature.
        /// When active, multiple MatrixPlotter windows share their view state (pan, zoom, colormap).
        /// </summary>
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

                // Deselect hidden items and non-MatrixPlotter items before building the sync snapshot.
                DeselectHiddenItems();

                // Save selection so it can be restored on click during Sync.
                // Only visible MatrixPlotter windows participate in sync.
                _syncSelectionSnapshot = _windowList.SelectedItems?
                    .OfType<WindowListItemViewModel>()
                    .Where(m => m.Window is MatrixPlotter && m.IsWindowVisible)
                    .ToList();

                // Deselect any remaining non-MatrixPlotter items.
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
    }
}
