using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
            var hamburgerBtn = ActiveHamburgerButton;
            if (_menuPanel == null || hamburgerBtn == null)
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
            var pt = hamburgerBtn.TranslatePoint(new Point(0, hamburgerBtn.Bounds.Height), overlay);
            if (pt.HasValue)
            {
                Canvas.SetLeft(_menuPanel, pt.Value.X);
                Canvas.SetTop(_menuPanel, pt.Value.Y);
            }

            // Refresh Scale tab to reflect any changes from sync operations
            // (RefreshScaleTab is lighter than RefreshInfoTab as it skips metadata tab refresh)
            RefreshScaleTab();

            _menuPanel.IsVisible = true;
            hamburgerBtn.Background = Brushes.LightGray;
        }

        /// <summary>
        /// Drops the cached hamburger panel so it is rebuilt on next open.
        /// The panel is built once and reused, so anything that changes which items belong in it -
        /// data replacement, plugin registration, an axis rename that creates or removes a
        /// "Channel" axis - has to call this or the menu keeps showing the stale item set.
        /// </summary>
        private void InvalidateMenuPanel()
        {
            if (_menuPanel?.Parent is Panel menuParent)
                menuParent.Children.Remove(_menuPanel);
            _menuPanel = null;
        }

        private void HideMenuPanel()
        {
            if (_menuPanel != null) _menuPanel.IsVisible = false;
            if (ActiveHamburgerButton != null) ActiveHamburgerButton.Background = Brushes.Transparent;
        }


        /// <summary>
        /// Closes the menu panel when the pointer is pressed outside it (and outside the
        /// hamburger button, which has its own click-toggle handler).
        /// </summary>
        private void OnMenuLightDismiss(object? s, PointerPressedEventArgs e)
        {
            bool anyVisible = (_menuPanel?.IsVisible == true) || (_zoomFlyout?.IsVisible == true);
            if (!anyVisible) return;

            if (_menuPanel?.IsVisible == true)
            {
                var pp = e.GetPosition(_menuPanel);
                if (pp.X >= 0 && pp.Y >= 0 && pp.X <= _menuPanel.Bounds.Width && pp.Y <= _menuPanel.Bounds.Height)
                    return;

                var hamburgerBtn = ActiveHamburgerButton;
                if (hamburgerBtn != null)
                {
                    var hp = e.GetPosition(hamburgerBtn);
                    if (hp.X >= 0 && hp.Y >= 0 && hp.X <= hamburgerBtn.Bounds.Width && hp.Y <= hamburgerBtn.Bounds.Height)
                        return;
                }
                HideMenuPanel();
            }

            if (_zoomFlyout?.IsVisible == true)
            {
                var zp = e.GetPosition(_zoomFlyout);
                if (zp.X >= 0 && zp.Y >= 0 && zp.X <= _zoomFlyout.Bounds.Width && zp.Y <= _zoomFlyout.Bounds.Height)
                    return;

                var zt = e.GetPosition(_zoomText);
                if (zt.X >= 0 && zt.Y >= 0 && zt.X <= _zoomText.Bounds.Width && zt.Y <= _zoomText.Bounds.Height)
                    return;

                HideZoomFlyout(commit: true);
            }
        }

        // ── Zoom flyout ────────────────────────────────────────────────────────

        private double _zoomFlyoutOriginalZoom;
        private EventHandler<SizeChangedEventArgs>? _zoomFlyoutSizeChangedHandler;

        private void ShowZoomFlyout()
        {
            if (_zoomFlyout == null) return;
            var overlay = OverlayLayer.GetOverlayLayer(this);
            if (overlay == null) return;

            _zoomFlyoutOriginalZoom = _view.Zoom;
            RefreshZoomFlyout();

            if (_zoomFlyout.Parent == null)
                overlay.Children.Add(_zoomFlyout);
            else
            {
                int idx = overlay.Children.IndexOf(_zoomFlyout);
                if (idx >= 0 && idx < overlay.Children.Count - 1)
                    overlay.Children.Move(idx, overlay.Children.Count - 1);
            }

            _zoomFlyout.IsVisible = true;
            RepositionZoomFlyout(overlay);

            // Reposition again after layout pass (flyout height becomes known)
            void OnLayout(object? s, EventArgs _)
            {
                _zoomFlyout.LayoutUpdated -= OnLayout;
                RepositionZoomFlyout(overlay);
            }
            _zoomFlyout.LayoutUpdated += OnLayout;

            // Reposition when window is resized
            if (_zoomFlyoutSizeChangedHandler == null)
            {
                _zoomFlyoutSizeChangedHandler = (_, _) =>
                {
                    var ov = OverlayLayer.GetOverlayLayer(this);
                    if (ov != null) RepositionZoomFlyout(ov);
                };
                SizeChanged += _zoomFlyoutSizeChangedHandler;
            }
        }

        private void RepositionZoomFlyout(Panel overlay)
        {
            if (_zoomFlyout == null) return;
            var topPt = _zoomText.TranslatePoint(new Point(0, 0), overlay);
            if (!topPt.HasValue) return;

            double flyoutH = _zoomFlyout.Bounds.Height > 0 ? _zoomFlyout.Bounds.Height : _zoomFlyout.DesiredSize.Height;
            double top = topPt.Value.Y - flyoutH;

            double left = topPt.Value.X;
            double overlayW = overlay.Bounds.Width;
            if (overlayW > 0 && left + _zoomFlyout.Width > overlayW)
                left = Math.Max(0, overlayW - _zoomFlyout.Width);

            Canvas.SetLeft(_zoomFlyout, left);
            Canvas.SetTop(_zoomFlyout, top);
        }

        private void HideZoomFlyout(bool commit = true)
        {
            if (_zoomFlyout == null) return;
            if (!commit)
                _view.SetZoom(_zoomFlyoutOriginalZoom);
            _zoomFlyout.IsVisible = false;
            if (_zoomFlyoutSizeChangedHandler != null)
            {
                SizeChanged -= _zoomFlyoutSizeChangedHandler;
                _zoomFlyoutSizeChangedHandler = null;
            }
        }

        /// <summary>Refreshes the zoom flyout's text boxes from the current view zoom.</summary>
        internal void RefreshZoomFlyout()
        {
            if (_zoomFlyout?.Tag is not (TextBox zb, TextBox wb, TextBox hb)) return;
            if (_zoomFlyout.IsVisible) return; // only sync before opening
            double z = _view.Zoom;
            var (nw, nh) = _view.GetNaturalDims();
            zb.Text = $"{z * 100:0.##}";
            wb.Text = $"{Math.Max(1, (int)Math.Round(nw * z))}";
            hb.Text = $"{Math.Max(1, (int)Math.Round(nh * z))}";
        }

        private Border BuildZoomFlyout()
        {
            const double FS = 11;
            const double LabelW = 74;
            const double BoxW = 90;

            var (nw, nh) = _view.GetNaturalDims();
            double aspect = nw > 0 && nh > 0 ? nw / nh : 1.0;
            double z0 = _view.Zoom;

            TextBox MakeBox(string text) => new()
            {
                Text = text,
                Width = BoxW,
                MinHeight = 0,
                Height = 26,
                TextAlignment = TextAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = FS,
            };

            var zoomBox = MakeBox($"{z0 * 100:0.##}");
            var wBox = MakeBox($"{Math.Max(1, (int)Math.Round(nw * z0))}");
            var hBox = MakeBox($"{Math.Max(1, (int)Math.Round(nh * z0))}");

            bool syncing = false;

            void ApplyZoom(double z)
            {
                z = Math.Clamp(z, 0.01, 64.0);
                _view.SetZoom(z);
            }

            void SyncFromZoom()
            {
                if (syncing || !double.TryParse(zoomBox.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out double pct) || pct <= 0) return;
                syncing = true;
                double z = Math.Clamp(pct / 100.0, 0.01, 64.0);
                wBox.Text = $"{Math.Max(1, (int)Math.Round(nw * z))}";
                hBox.Text = $"{Math.Max(1, (int)Math.Round(nh * z))}";
                ApplyZoom(z);
                syncing = false;
            }

            void SyncFromW()
            {
                if (syncing || !double.TryParse(wBox.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out double w) || w <= 0) return;
                syncing = true;
                double z = Math.Clamp(w / Math.Max(1, nw), 0.01, 64.0);
                zoomBox.Text = $"{z * 100:0.##}";
                hBox.Text = $"{Math.Max(1, (int)Math.Round(w / aspect))}";
                ApplyZoom(z);
                syncing = false;
            }

            void SyncFromH()
            {
                if (syncing || !double.TryParse(hBox.Text, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.CurrentCulture, out double h) || h <= 0) return;
                syncing = true;
                double z = Math.Clamp(h / Math.Max(1, nh), 0.01, 64.0);
                zoomBox.Text = $"{z * 100:0.##}";
                wBox.Text = $"{Math.Max(1, (int)Math.Round(h * aspect))}";
                ApplyZoom(z);
                syncing = false;
            }

            // Debounce: apply zoom 300 ms after the last keystroke so typing "100" doesn't
            // snap the view on each character.
            CancellationTokenSource? zoomCts = null, wCts = null, hCts = null;

            zoomBox.TextChanged += async (_, _) =>
            {
                zoomCts?.Cancel();
                zoomCts = new CancellationTokenSource();
                var token = zoomCts.Token;
                try { await System.Threading.Tasks.Task.Delay(250, token); }
                catch (OperationCanceledException) { return; }
                if (!token.IsCancellationRequested) SyncFromZoom();
            };
            wBox.TextChanged += async (_, _) =>
            {
                wCts?.Cancel();
                wCts = new CancellationTokenSource();
                var token = wCts.Token;
                try { await System.Threading.Tasks.Task.Delay(250, token); }
                catch (OperationCanceledException) { return; }
                if (!token.IsCancellationRequested) SyncFromW();
            };
            hBox.TextChanged += async (_, _) =>
            {
                hCts?.Cancel();
                hCts = new CancellationTokenSource();
                var token = hCts.Token;
                try { await System.Threading.Tasks.Task.Delay(150, token); }
                catch (OperationCanceledException) { return; }
                if (!token.IsCancellationRequested) SyncFromH();
            };

            // Sync flyout fields when user scrolls / zooms with mouse wheel
            _view.ScrollStateChanged += (_, _) =>
            {
                if (_zoomFlyout?.IsVisible != true) return;
                if (syncing) return;
                syncing = true;
                double z = _view.Zoom;
                var (cnw, cnh) = _view.GetNaturalDims();
                zoomBox.Text = $"{z * 100:0.##}";
                wBox.Text = $"{Math.Max(1, (int)Math.Round(cnw * z))}";
                hBox.Text = $"{Math.Max(1, (int)Math.Round(cnh * z))}";
                syncing = false;
            };

            StackPanel Row(string label, TextBox box) => new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(0, 3, 0, 0),
                Children =
                {
                    new TextBlock { 
                        Text = label, Width = LabelW, TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center, FontSize = FS ,
                    },
                    box,
                }
            };

            var closeBtn = new Button
            {
                Content = "✕",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                FontSize = 10,
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            closeBtn.Click += (_, _) => HideZoomFlyout(commit: true);

            var body = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };
            body.Children.Add(new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(0, 2, 0, 4),
                Children =
                {
                    new TextBlock { Text = "Zoom / View Size", FontSize = FS, FontWeight = FontWeight.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Left },
                    closeBtn,
                }
            });
            DockPanel.SetDock(closeBtn, Dock.Right);
            body.Children.Add(Row("Zoom (%):", zoomBox));
            body.Children.Add(Row("Width (px):", wBox));
            body.Children.Add(Row("Height (px):", hBox));

            static IBrush? GetMenuBg() =>
                Application.Current?.TryGetResource("MenuPopupBg",
                    Application.Current.ActualThemeVariant, out var r) == true
                    ? r as IBrush
                    : new SolidColorBrush(Color.FromArgb(220, 36, 36, 36));

            var flyout = new Border
            {
                Child = body,
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 110, 110, 110)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Width = LabelW + BoxW + 6 + 8 * 2,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = GetMenuBg(),
                Tag = (zoomBox, wBox, hBox),
            };

            if (Application.Current is { } app)
            {
                void UpdateBg(object? _, EventArgs __) => flyout.Background = GetMenuBg();
                app.ActualThemeVariantChanged += UpdateBg;
                Closed += (_, _) => app.ActualThemeVariantChanged -= UpdateBg;
            }

            return flyout;
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

            // ── Data tab
            bool isSaveCopy = _currentData != null && !_currentData.IsWritable;
            string saveLabel = isSaveCopy ? "Save a Copy (S)\u2026" : "Save As (S)\u2026";
            string saveHint = isSaveCopy
                ? "Exports data to a new file. The current view remains backed by the original."
                : "Saves data to a new file and updates the window title.";

            var dataItems = new StackPanel { Spacing = 3, Margin = new Thickness(4, 6, 4, 4) };

            var fileMenuItems = new List<Control>
            {
                ControlFactory.MakeChildMenuItem(saveLabel, ActAsync(SaveDataAsync), saveHint, icon: MenuIcons.Save),
            };

            // ── Export as… submenu (built-in PNG + injected formats) ──────────
            var pngDesc = MakePngExportDescriptor();
            var exportItems = new List<Control>
            {
                ControlFactory.MakeChildMenuItem(pngDesc.Label, ActAsync(() => InvokeExportAsync(pngDesc)), pngDesc.Hint, icon: MenuIcons.Image),
            };
            // Same rule as the right-click export menu: a frame-stepping exporter needs an axis
            // that is actually free to step (see HasAnimatableAxis).
            bool canAnimate = HasAnimatableAxis(BuildExcludedAxisNames());
            foreach (var ef in ExportFormats)
            {
                if (ef.RequiresStack && !canAnimate) continue;
                var captured = ef;
                exportItems.Add(ControlFactory.MakeChildMenuItem(
                    captured.Label, ActAsync(() => InvokeExportAsync(captured)), captured.Hint, icon: MenuIcons.Image));
            }
            foreach (var ep in MatrixPlotterPluginRegistry.ExportPlugins)
            {
                if (ep.RequiresStack && !canAnimate) continue;
                var captured = ToDescriptor(ep);
                exportItems.Add(ControlFactory.MakeChildMenuItem(
                    captured.Label, ActAsync(() => InvokeExportAsync(captured)), captured.Hint, icon: MenuIcons.Image));
            }
            fileMenuItems.Add(ControlFactory.MakeMenuGroup("Export as\u2026", [.. exportItems],
                icon: MenuIcons.Image, initiallyExpanded: false, indent: 10,
                headerFontWeight: FontWeight.Regular));
            dataItems.Children.Add(ControlFactory.MakeMenuGroup("File", [.. fileMenuItems], icon: MenuIcons.Folder));

            // Convert menu item: label and tooltip change for Complex data
            bool isComplex = _currentData?.ValueType == typeof(System.Numerics.Complex);
            string convertLabel = isComplex ? "Convert Complex To\u2026" : "Convert Value Type\u2026";
            string convertHint = isComplex
                ? "Converts complex data to double by extracting a component (Magnitude, Real, Imaginary, Phase, or Power)."
                : "Converts the matrix data to a different numerical type (e.g., float to ushort).";

            var copyItems = new List<Control>
            {
                ControlFactory.MakeChildMenuItem("Copy to Clipboard", ActAsync(CopyFrameToClipboardAsync), "Copies the current frame to the clipboard as an image or tab-separated text.", icon: MenuIcons.Copy),
                ControlFactory.MakeChildMenuItem("Duplicate Window",  ActAsync(DuplicateWindowAsync),      "Opens a new window with an independent deep copy of the data.", icon: MenuIcons.Duplicate),
            };
            dataItems.Children.Add(ControlFactory.MakeMenuGroup("Copy", [.. copyItems], icon: MenuIcons.Briefcase));

            var conversionItems = new List<Control>
            {
                ControlFactory.MakeChildMenuItem(convertLabel, ActAsync(ConvertValueTypeAsync), convertHint, icon: MenuIcons.ConvertType),
            };
            conversionItems.AddRange(CommandItems("Conversion"));
            dataItems.Children.Add(ControlFactory.MakeMenuGroup("Conversion", [.. conversionItems], icon: MenuIcons.ViewGrid));

            var processingItems = new List<Control>
            {
                ControlFactory.MakeChildMenuItem("Crop", Act(InvokeCropTool), "Crop the image to ROI selection", icon: MenuIcons.Crop),
            };
            if (_cropUndoData != null)
            {
                var revertItem = ControlFactory.MakeChildMenuItem("Revert Crop", Act(RevertCrop), "Undo the last Replace crop and restore the original data", icon: MenuIcons.Undo);
                revertItem.Margin = new Thickness(22, revertItem.Margin.Top, revertItem.Margin.Right, revertItem.Margin.Bottom);
                processingItems.Add(revertItem);
            }
            processingItems.AddRange(CommandItems("Geometry & Dimensions"));
            /*
            //NOTE: These are placeholders for potential future features, currently disabled until implemented
            if (_currentData?.FrameCount > 1)
            {
                processingItems.Add(ControlFactory.MakeChildMenuItem("Reorder", Act(() => { }), "(Not yet implemented)", icon: MenuIcons.AutoFix, enabled:false));
                processingItems.Add(ControlFactory.MakeChildMenuItem("Extract", Act(() => { }), "(Not yet implemented)", icon: MenuIcons.AutoFix, enabled:false));
                processingItems.Add(ControlFactory.MakeChildMenuItem("Select", Act(() => { }), "(Not yet implemented)", icon: MenuIcons.AutoFix, enabled:false));
            }
            */

            // ── Info tab ──────────────────────────────────────────────────────
            _scaleTabBody = new StackPanel { Margin = new Thickness(2, 5, 2, 2) };

            // ── TabControl ────────────────────────────────────────────────────
            Control TabHdr(string t, Geometry? icon = null)
            {
                if (icon == null)
                    return new TextBlock { Text = t, FontSize = TabFontSize, FontWeight = FontWeight.Bold };
                var pathIcon = new PathIcon { Data = icon, Width = 11, Height = 11 };
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

            
            var tabControl = new TabControl {
                FontSize = MenuFontSize,
                Padding = new Thickness(0, 5, 0, 0),
                Classes = { "menu-tabs" }
            };
            tabControl.Items.Add(new TabItem
            {
                Header = TabHdr("Data", MenuIcons.Database),
                Padding = new Thickness(0),
                Content = new ScrollViewer
                {
                    Content = dataItems,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            });
            tabControl.Items.Add(new TabItem
            {
                Header = TabHdr("Scale", MenuIcons.Ruler),
                Padding = new Thickness(0),
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

            // Groups of commands from the catalog: the menu only wires an item to its command.
            Control[] CommandItems(string group) => CommandCatalog.InGroup(group, this)
                .Select(c => (Control)ControlFactory.MakeChildMenuItem(
                    c.Label, ActAsync(() => c.Command.RunAsync(this)), c.Hint, icon: c.Icon))
                .ToArray();

            processingTabBody.Children.Add(
                ControlFactory.MakeMenuGroup("Filters", CommandItems("Filters"), icon: MenuIcons.Processing));

            processingTabBody.Children.Add(
                ControlFactory.MakeMenuGroup("Intensity", CommandItems("Intensity"), icon: MenuIcons.Processing));

            processingTabBody.Children.Add(
                ControlFactory.MakeMenuGroup("Frequency", CommandItems("Frequency"), icon: MenuIcons.Processing));

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
                Padding = new Thickness(0),
                Content = new ScrollViewer
                {
                    Content = processingTabBody,
                    VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                },
            });
            void onPluginsChanged() => RebuildPluginsGroup();
            MatrixPlotterPluginRegistry.PluginsChanged += onPluginsChanged;
            void onExportPluginsChanged() { InvalidateMenuPanel(); }
            MatrixPlotterPluginRegistry.ExportPluginsChanged += onExportPluginsChanged;
            Closed += (_, _) =>
            {
                MatrixPlotterPluginRegistry.PluginsChanged -= onPluginsChanged;
                MatrixPlotterPluginRegistry.ExportPluginsChanged -= onExportPluginsChanged;
            };

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

            // About sits bottom-left, opposite the resize grip.
            var aboutIcon = new PathIcon { Data = MenuIcons.Info, Width = 14, Height = 14 };
            if (MenuIcons.DefaultBrush(MenuIcons.Info) is { } aboutBrush) aboutIcon.Foreground = aboutBrush;
            var aboutBtn = new Button
            {
                Content = aboutIcon,
                Width = 22,
                Height = 18,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 2, 0, 2),
            };
            ToolTip.SetTip(aboutBtn, "About");
            aboutBtn.Click += (_, _) => ActAsync(ShowAboutAsync)();

            // A rule above the footer row, in the color of the one under the tab strip, that stops
            // short of the resize grip.
            var footerLine = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, grip.Width + grip.Margin.Right, 0),
            };
            footerLine.Bind(Border.BackgroundProperty, footerLine.GetResourceObservable("MenuTabSelectedBorder"));

            var outerGrid = new Grid();
            outerGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            outerGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(tabControl, 0);
            Grid.SetRow(footerLine, 1);
            Grid.SetRow(aboutBtn, 1);
            Grid.SetRow(grip, 1);
            outerGrid.Children.Add(tabControl);
            outerGrid.Children.Add(footerLine);
            outerGrid.Children.Add(aboutBtn);
            outerGrid.Children.Add(grip);

            var panel = new Border
            {
                Child = outerGrid,
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 110, 110, 110)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(3, 0, 0, 0),
                Width = 380,
                Height = 360,
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
            double startW = 0, startH = 0;

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
