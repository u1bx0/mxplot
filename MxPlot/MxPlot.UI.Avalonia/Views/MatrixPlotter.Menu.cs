using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Hamburger menu ─ OverlayLayer implementation ──────────────────────
        // OverlayLayer renders inside the window's own Skia surface, so background
        // alpha genuinely composites against the underlying MxView content.

        private void ShowMenuPanel()
        {
            if (_menuPanel == null || _hamburgerBtn == null) 
                return;
            var overlay = OverlayLayer.GetOverlayLayer(this);
            if (overlay == null) 
                return;

            if (_menuPanel.Parent == null)
                overlay.Children.Add(_menuPanel);
            else
            {
                int idx = overlay.Children.IndexOf(_menuPanel);
                if (idx >= 0 && idx < overlay.Children.Count - 1)
                    overlay.Children.Move(idx, overlay.Children.Count - 1);
            }

            // If the retained panel size exceeds the window, shrink to ~90% of the window
            double overlayW = overlay.Bounds.Width;
            double overlayH = overlay.Bounds.Height;
            if (overlayW > 0 && _menuPanel.Width > overlayW)
                _menuPanel.Width = Math.Max(_menuPanel.MinWidth, overlayW * 0.9);
            if (overlayH > 0 && _menuPanel.Height > overlayH)
                _menuPanel.Height = Math.Max(_menuPanel.MinHeight, overlayH * 0.8);

            // Position immediately below the hamburger button
            var pt = _hamburgerBtn.TranslatePoint(new Point(0, _hamburgerBtn.Bounds.Height), overlay);
            if (pt.HasValue)
            {
                Canvas.SetLeft(_menuPanel, pt.Value.X);
                Canvas.SetTop(_menuPanel, pt.Value.Y);
            }
            _menuPanel.IsVisible = true;
            _hamburgerBtn.Background = Brushes.LightGray;
        }

        private void HideMenuPanel()
        {
            if (_menuPanel != null) _menuPanel.IsVisible = false;
            if (_hamburgerBtn != null) _hamburgerBtn.Background = Brushes.Transparent;
        }

        /// <summary>
        /// Closes the menu panel when the pointer is pressed outside it (and outside the
        /// hamburger button, which has its own click-toggle handler).
        /// </summary>
        private void OnMenuLightDismiss(object? s, PointerPressedEventArgs e)
        {
            if (_menuPanel == null || !_menuPanel.IsVisible) return;

            var pp = e.GetPosition(_menuPanel);
            if (pp.X >= 0 && pp.Y >= 0 && pp.X <= _menuPanel.Bounds.Width && pp.Y <= _menuPanel.Bounds.Height)
                return; // inside panel → keep open

            if (_hamburgerBtn != null)
            {
                var hp = e.GetPosition(_hamburgerBtn);
                if (hp.X >= 0 && hp.Y >= 0 && hp.X <= _hamburgerBtn.Bounds.Width && hp.Y <= _hamburgerBtn.Bounds.Height)
                    return; // inside hamburger button → let Click handler toggle
            }

            HideMenuPanel();
        }

        /// <summary>Builds the floating panel (created once, reused). Contains Actions and Matrix Info tabs.</summary>
        private Border BuildMenuPanel()
        {
            // ── Menu panel font sizes ─────────────────────────────────────────
            const double MenuFontSize = 12;   // base font size (inherited by panel)
            const double TabFontSize = 11;   // tab header labels
            const double GripFontSize = 11;   // resize grip glyph

            // ── Hides the menu panel then runs the given action ───────────────
            Action Act(Action a) => () => { HideMenuPanel(); a(); };
            Action ActAsync(Func<Task> a) => async () => { HideMenuPanel(); await a(); };

            // ── Actions tab
            bool isSaveCopy = _currentData != null && !_currentData.IsWritable;
            string saveLabel = isSaveCopy ? "Save a Copy (S)\u2026" : "Save As (S)\u2026";
            string saveHint = isSaveCopy
                ? "Exports data to a new file. The current view remains backed by the original."
                : "Saves data to a new file and updates the window title.";

            var actionsItems = new StackPanel { Spacing = 3, Margin = new Thickness(4, 6, 4, 4) };
            actionsItems.Children.Add(ControlFactory.MakeMenuGroup("File", [
                ControlFactory.MakeChildMenuItem(saveLabel, ActAsync(SaveDataAsync), saveHint, icon: MenuIcons.Save),
                ControlFactory.MakeChildMenuItem("Export as PNG\u2026", ActAsync(ExportFrameAsPngAsync), "Exports the current frame as a PNG image.",                          icon: MenuIcons.Image),
            ], icon: MenuIcons.Folder));
            actionsItems.Children.Add(ControlFactory.MakeMenuGroup("Edit", [
                ControlFactory.MakeChildMenuItem("Copy to Clipboard", ActAsync(CopyFrameToClipboardAsync), "Copies the current frame to the clipboard as an image or tab-separated text.", icon: MenuIcons.Copy),
                ControlFactory.MakeChildMenuItem("Duplicate Window",  ActAsync(DuplicateWindowAsync),      "Opens a new window with an independent deep copy of the data.", icon: MenuIcons.Duplicate),
                ControlFactory.MakeChildMenuItem("Convert Value Type\u2026", Act(ConvertValueTypeAsync), "Converts the matrix data to a different numerical type (e.g., float to ushort).", icon: MenuIcons.ConvertType),
            ], icon: MenuIcons.Edit));
            var processingItems = new List<Control>
            {
                ControlFactory.MakeChildMenuItem("Crop", Act(InvokeCropAction), "Crop the image to ROI selection", icon: MenuIcons.AutoFix),
            };
            if (_cropUndoData != null)
            {
                var revertItem = ControlFactory.MakeChildMenuItem("Revert Crop", Act(RevertCrop), "Undo the last Replace crop and restore the original data", icon: MenuIcons.Undo);
                revertItem.Margin = new Thickness(22, revertItem.Margin.Top, revertItem.Margin.Right, revertItem.Margin.Bottom);
                processingItems.Add(revertItem);
            }
            if (_currentData?.FrameCount > 1)
            {
                processingItems.Add(ControlFactory.MakeChildMenuItem("Reverse Stack\u2026", ActAsync(InvokeReverseStackAsync), "Reverse the frame order along a selected axis", icon: MenuIcons.Layers));
            }
            /*
            //NOTE: These are placeholders for potential future features, currently disabled until implemented
            processingItems.Add(ControlFactory.MakeChildMenuItem("Transpose", Act(() => { }), "(Not yet implemented)", icon: MenuIcons.AutoFix, enabled:false));
            if (_currentData?.FrameCount > 1)
            {
                processingItems.Add(ControlFactory.MakeChildMenuItem("Reorder", Act(() => { }), "(Not yet implemented)", icon: MenuIcons.AutoFix, enabled:false));
                processingItems.Add(ControlFactory.MakeChildMenuItem("Extract", Act(() => { }), "(Not yet implemented)", icon: MenuIcons.AutoFix, enabled:false));
                processingItems.Add(ControlFactory.MakeChildMenuItem("Select", Act(() => { }), "(Not yet implemented)", icon: MenuIcons.AutoFix, enabled:false));
            }
            */
            // example
            //actionsItems.Children.Add(ControlFactory.MakeSep(new Thickness(6, 3)));
            //actionsItems.Children.Add(ControlFactory.MakeMenuItem("Dummy Process", ActAsync(DummyProcessAsync), icon: MenuIcons.Refresh));

            actionsItems.Children.Add(ControlFactory.MakeSep(new Thickness(6, 3)));
            actionsItems.Children.Add(ControlFactory.MakeMenuItem("About", ActAsync(ShowAboutAsync), icon: MenuIcons.Info));
            actionsItems.Children.Add(ControlFactory.MakeMenuItem("Close", Act(Close), icon: MenuIcons.Close));

            // ── Info tab ──────────────────────────────────────────────────────
            _scaleTabBody = new StackPanel { Margin = new Thickness(2) };

            // ── TabControl ────────────────────────────────────────────────────
            Control TabHdr(string t, Geometry? icon = null)
            {
                if (icon == null)
                    return new TextBlock { Text = t, FontSize = TabFontSize, FontWeight = FontWeight.Bold };
                var pathIcon = new PathIcon { Data = icon, Width = 12, Height = 12 };
                var brush = MenuIcons.DefaultBrush(icon);
                if (brush != null) pathIcon.Foreground = brush;
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        pathIcon,
                        new TextBlock { Text = t, FontSize = TabFontSize, FontWeight = FontWeight.Bold },
                    }
                };
            }

            var tabControl = new TabControl { FontSize = MenuFontSize, Padding = new Thickness(0, 2, 0, 0) };
            tabControl.Items.Add(new TabItem
            {
                Header = TabHdr("Actions", MenuIcons.Lightning),
                Padding = new Thickness(8, 3),
                Content = new ScrollViewer
                {
                    Content = actionsItems,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            });
            tabControl.Items.Add(new TabItem
            {
                Header = TabHdr("Scale", MenuIcons.Ruler),
                Padding = new Thickness(8, 3),
                Content = new ScrollViewer
                {
                    Content = _scaleTabBody,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            });


            // ── Processing tab ───────────────────────────────────────────────────
            var processingTabBody = new StackPanel { Spacing = 1, Margin = new Thickness(4, 6, 4, 4) };
            processingTabBody.Children.Add(
                ControlFactory.MakeMenuGroup("Geometry & Dimensions", [.. processingItems], icon: MenuIcons.Processing));

            var filterItems = new Control[]
            {
                ControlFactory.MakeChildMenuItem("Median\u2026",   ActAsync(InvokeMedianFilterAsync),   "Apply a median (hot-pixel removal) filter",  icon: MenuIcons.AutoFix),
                ControlFactory.MakeChildMenuItem("Gaussian\u2026", ActAsync(InvokeGaussianFilterAsync), "Apply a Gaussian (smoothing) filter",        icon: MenuIcons.AutoFix),
            };
            processingTabBody.Children.Add(
                ControlFactory.MakeMenuGroup("Filters", filterItems, icon: MenuIcons.Processing));

            var intensityItems = new Control[]
            {
                ControlFactory.MakeChildMenuItem("Normalize\u2026",     ActAsync(InvokeNormalizeAsync),     "Scale pixel values so the maximum equals a target value", icon: MenuIcons.AutoFix),
                ControlFactory.MakeChildMenuItem("Log Transform\u2026", ActAsync(InvokeLogTransformAsync), "Apply a logarithm transform (ln / log\u2081\u2080 / log\u2082); outputs double", icon: MenuIcons.AutoFix),
            };
            processingTabBody.Children.Add(
                ControlFactory.MakeMenuGroup("Intensity", intensityItems, icon: MenuIcons.Processing));

            var pluginsContainer = new StackPanel();
            void RebuildPluginsGroup()
            {
                pluginsContainer.Children.Clear();
                var plugs = MatrixPlotterPluginRegistry.Plugins;
                if (plugs.Count == 0) return;

                Action PlugRun(IMatrixPlotterPlugin p) => () =>
                {
                    HideMenuPanel();
                    try   { p.Run(CreatePluginContext()); }
                    catch { /* silently absorb plugin errors */ }
                };

                var items = new List<Control>();
                foreach (var plugin in plugs.Where(p => string.IsNullOrEmpty(p.GroupName)))
                {
                    var mi = ControlFactory.MakeChildMenuItem(plugin.CommandName, PlugRun(plugin),
                        plugin.Description, icon: MenuIcons.AutoFix);
                    items.Add(mi);
                }
                foreach (var grp in plugs.Where(p => !string.IsNullOrEmpty(p.GroupName)).GroupBy(p => p.GroupName!))
                {
                    var gi = grp.Select(p => (Control)ControlFactory.MakeChildMenuItem(
                        p.CommandName, PlugRun(p), p.Description, icon: MenuIcons.AutoFix)).ToArray();
                    items.Add(ControlFactory.MakeMenuGroup(grp.Key, gi));
                }
                pluginsContainer.Children.Add(
                    ControlFactory.MakeMenuGroup("Plugins", [.. items], icon: MenuIcons.Plugin));
            }
            RebuildPluginsGroup();
            processingTabBody.Children.Add(pluginsContainer);
            tabControl.Items.Add(new TabItem
            {
                Header  = TabHdr("Processing", MenuIcons.Processing),
                Padding = new Thickness(8, 3),
                Content = new ScrollViewer
                {
                    Content = processingTabBody,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            });
            void onPluginsChanged() => RebuildPluginsGroup();
            MatrixPlotterPluginRegistry.PluginsChanged += onPluginsChanged;
            Closed += (_, _) => MatrixPlotterPluginRegistry.PluginsChanged -= onPluginsChanged;

            // ── Metadata tab ──────────────────────────────────────────────────
            BuildMetadataTab(tabControl);


            // ── Resize grip
            var grip = new Border
            {
                Width = 16,
                Height = 16,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 2, 2, 2),
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                Child = new TextBlock
                {
                    Text = "▟",
                    FontSize = GripFontSize,
                    Foreground = new SolidColorBrush(Color.FromArgb(160, 170, 170, 170)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            outerGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(tabControl, 0);
            Grid.SetRow(grip, 1);
            outerGrid.Children.Add(tabControl);
            outerGrid.Children.Add(grip);

            var panel = new Border
            {
                Child = outerGrid,
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 110, 110, 110)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3, 0, 0, 0),
                Width = 400,
                Height = 400,
                MinWidth = 220,
                MinHeight = 120,
                ClipToBounds = true,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            static IBrush? GetMenuBg() =>
                Application.Current?.TryGetResource("MenuPopupBg",
                    Application.Current.ActualThemeVariant, out var r) == true
                    ? r as IBrush
                    : new SolidColorBrush(Color.FromArgb(220, 36, 36, 36));

            panel.Background = GetMenuBg();
            if (Application.Current is { } app)
            {
                void UpdateBg(object? _, EventArgs __) => panel.Background = GetMenuBg();
                app.ActualThemeVariantChanged += UpdateBg;
                Closed += (_, _) => app.ActualThemeVariantChanged -= UpdateBg;
            }

            // ── Resize state ──────────────────────────────────────────────────
            bool resizing = false;
            Point resizeOrigin = default;
            double startW = 350, startH = 300;

            grip.PointerPressed += (_, e) =>
            {
                resizing = true;
                resizeOrigin = e.GetPosition(null);
                startW = panel.Width;
                startH = double.IsNaN(panel.Height) ? panel.Bounds.Height : panel.Height;
                e.Pointer.Capture(grip);
                e.Handled = true;
            };
            grip.PointerMoved += (_, e) =>
            {
                if (!resizing) return;
                var p = e.GetPosition(null);
                panel.Width = Math.Max(220, startW + p.X - resizeOrigin.X);
                panel.Height = Math.Max(120, startH + p.Y - resizeOrigin.Y);
                e.Handled = true;
            };
            grip.PointerReleased += (_, e) =>
            {
                resizing = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };

            RefreshInfoTab();
            return panel;
        }
    }
}
