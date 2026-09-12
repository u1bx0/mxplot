// MatrixPlotter.Initialization.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.ViewModels;
using System;
using System.Diagnostics;
using Avalonia.Diagnostics;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>
        /// Initializes a <see cref="MatrixPlotter"/> with no data loaded.
        /// <para>
        /// The constructor delegates all UI construction to focused <c>Initialize*</c>
        /// and <c>Wire*</c> methods defined in this file, keeping the call site concise
        /// and each initialization concern independently readable.
        /// </para>
        /// </summary>
        public MatrixPlotter()
        {
#if DEBUG
            this.AttachDevTools();
#endif


            Width = 450;
            Height = 520;

            InitializeOrthogonalPanel();
            InitializeTrackerPanel();
            InitializeLutSelector();
            BuildRootLayout();

            WireViewEvents();
            WireLutSelectorEvents();
            WireLutSettingsPanelEvents();
            WireRangeBarEvents();
            WireWindowResizeEvents();
        }

        // ── Initialization ────────────────────────────────────────────────────

        /// <summary>
        /// Creates the <see cref="OrthogonalPanel"/>, obtains the main <see cref="MxView"/>,
        /// and constructs the <see cref="OrthogonalViewController"/>.
        /// Must be called first — other initializers depend on <c>_view</c> and <c>_orthoPanel</c>.
        /// </summary>
        private void InitializeOrthogonalPanel()
        {
            _orthoPanel = new OrthogonalPanel();
            _view = _orthoPanel.MainView;
            _orthoController = new OrthogonalViewController(_orthoPanel);
            _orthoController.XYProjectionChanged += OnXYProjectionChanged;
            _orthoController.ColorCodedComputeFailed += () => _xyProjectionWindow?.SetColorCodedBusy(false);
            _orthoPanel.ProjectionSelector.CreateProjectedDataRequested +=
                (_, e) => _ = InvokeCreateProjectedDataAsync(e.Plane, e.Mode);

            _view.EnableBuiltInContextMenu = true;
            _orthoPanel.BottomView.EnableBuiltInContextMenu = true;
            _orthoPanel.RightView.EnableBuiltInContextMenu = true;

            // Export format providers
            _view.ExportFormatsProvider = () => GetExportFormatsForView(_view);
            _orthoPanel.BottomView.ExportFormatsProvider = () => GetExportFormatsForView(_orthoPanel.BottomView);
            _orthoPanel.RightView.ExportFormatsProvider = () => GetExportFormatsForView(_orthoPanel.RightView);

            // Side-view context menu providers.
            // BottomView shows XZ (sliced/projected along Y) -- its natural Restack direction is Y.
            // RightView shows YZ (sliced/projected along X) -- its natural Restack direction is X.
            _orthoPanel.BottomView.SideViewMenuItemsProvider = () => BuildSideViewContextMenuItems(ViewFrom.Y);
            _orthoPanel.RightView.SideViewMenuItemsProvider = () => BuildSideViewContextMenuItems(ViewFrom.X);
        }

        /// <summary>
        /// Creates the axis-tracker host panel that is embedded in
        /// <see cref="OrthogonalPanel"/> column 0 (above the main view).
        /// </summary>
        private void InitializeTrackerPanel()
        {
            _trackerPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                IsVisible = false,
                Margin = new Thickness(0, 1, 0, 1),
            };
        }

        /// <summary>
        /// Creates the <see cref="LutSelector"/> control in compact mode.
        /// Event wiring is deferred to <see cref="WireLutSelectorEvents"/>.
        /// </summary>
        private void InitializeLutSelector()
        {
            _lutSelector = new LutSelector
            {
                CompactMode = true,
                ComboWidth = 97,
            };
        }

        /// <summary>
        /// Creates all status-bar controls: dirty badge, info text, virtual badge,
        /// zoom text, notice text, progress separator, progress bar, and progress label.
        /// Assembles them into a horizontal <see cref="StackPanel"/> wrapped in a
        /// bottom-bordered <see cref="Border"/>, stored in a local variable that is
        /// consumed by <see cref="BuildRootLayout"/>.
        /// </summary>
        private Border BuildStatusBar()
        {
            _dirtyBadge = new TextBlock
            {
                Text = "*",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 160, 60)),
                Margin = new Thickness(4, 0, 0, 0),
                IsVisible = false,
            };

            _infoText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
            };

            _virtualBadge = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DodgerBlue,
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(2, 0),
                IsVisible = false,
            };
            _virtualBadge.PointerPressed += (_, _) => OpenOrActivateCacheMonitor();

            _zoomText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                [ToolTip.TipProperty] = "Click to set zoom / output size",
            };
            _zoomText.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (_zoomFlyout == null)
                    _zoomFlyout = BuildZoomFlyout();
                if (_zoomFlyout.IsVisible) HideZoomFlyout();
                else ShowZoomFlyout();
            };

            _progressSep = new TextBlock
            {
                Text = "|",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                Margin = new Thickness(2, 0),
                IsVisible = false,
            };

            _noticeText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 2, 0),
                Opacity = 0.7,
                IsVisible = false,
            };

            _progressText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                IsVisible = false,
            };

            _progressBar = new ProgressBar
            {
                Width = 100,
                Height = 4,
                MinHeight = 0,
                Minimum = 0,
                Maximum = 100,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                IsVisible = false,
            };

            _progressCancelBtn = new Button
            {
                Content = "\u2715",
                FontSize = 9,
                Width = 16,
                Height = 16,
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0),
                IsVisible = false,
                [ToolTip.TipProperty] = "Cancel the running operation",
            };
            _progressCancelBtn.Click += (_, _) => OnProgressCancelClick();

            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal };
            statusPanel.Children.Add(_dirtyBadge);
            statusPanel.Children.Add(_infoText);
            statusPanel.Children.Add(_virtualBadge);
            statusPanel.Children.Add(_zoomText);
            statusPanel.Children.Add(_noticeText);
            statusPanel.Children.Add(_progressSep);
            statusPanel.Children.Add(_progressBar);
            statusPanel.Children.Add(_progressText);
            statusPanel.Children.Add(_progressCancelBtn);

            _statusBarBorder = new Border
            {
                Child = statusPanel,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 2),
            };
            return _statusBarBorder;
        }

        private Border BuildToastPanel()
        {
            _toastText = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.Parse("#F2F2F2")),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var icon = new TextBlock
            {
                Text = "✓",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#72D060")),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { icon, _toastText },
            };

            return _toastPanel = new Border
            {
                IsVisible = false,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 5, 30),
                Opacity = 0,
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.Parse("#D8181818")),
                BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF")),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 9),
                Child = row,
                RenderTransform = new TranslateTransform(0, 12), // slight vertical offset for a "floating" effect
            };
        }

        /// <summary>
        /// Creates the top header row: hamburger button, LUT/VR revert button, and
        /// settings toggle button for LUT mode. 
        /// Assembles them into a <see cref="DockPanel"/>
        /// together with the already-constructed <see cref="_lutSelector"/> and
        /// <see cref="_rangeBar"/>, stored in a local variable consumed by
        /// <see cref="BuildRootLayout"/>.
        /// Event wiring for buttons is deferred to <see cref="WireLutSettingsPanelEvents"/>.
        /// </summary>
        private DockPanel BuildLutModeHeader()
        {
            _hamburgerBtn = new Button
            {
                Content = "☰",
                Width = 26,
                Height = 20,
                FontSize = 14,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };

            // Window-level light-dismiss: closes the hamburger menu when the user
            // clicks anywhere in the window outside the overlay panel.
            AddHandler(InputElement.PointerPressedEvent,
                       OnMenuLightDismiss, RoutingStrategies.Bubble, handledEventsToo: true);

            _hamburgerBtn.Click += (_, _) =>
            {
                if (_menuPanel == null)
                    _menuPanel = BuildMenuPanel();
                if (_menuPanel.IsVisible) HideMenuPanel();
                else ShowMenuPanel();
            };

            _lutVrRevertBtn = new Button
            {
                Content = new PathIcon
                {
                    Data = MenuIcons.Undo,
                    Width = 12,
                    Height = 12,
                    Foreground = MenuIcons.DefaultBrush(MenuIcons.Undo),
                },
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1, 0),
                IsVisible = false,
            };
            ToolTip.SetTip(_lutVrRevertBtn, "Revert display settings (LUT / Composite / Value Range) to initial state");
            _lutVrRevertBtn.Click += (_, _) => RevertRenderState();

            _settingsBtn = new Button
            {
                Content = "▾",
                Width = 22,
                Height = 20,
                MinHeight = 20,
                FontSize = 18,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Margin = new Thickness(4, 0),
            };

            _rangeBar = new ValueRangeBar();

            var topRow = new DockPanel
            {
                Margin = new Thickness(2, 0),
                LastChildFill = true,
            };
            
            ToolTip.SetTip(_settingsBtn, "Show / hide detailed display settings");
            DockPanel.SetDock(_settingsBtn, Dock.Right);
            DockPanel.SetDock(_lutVrRevertBtn, Dock.Right);
            DockPanel.SetDock(_hamburgerBtn, Dock.Left);
            DockPanel.SetDock(_lutSelector, Dock.Left);
            topRow.Children.Add(_settingsBtn);
            topRow.Children.Add(_lutVrRevertBtn);
            topRow.Children.Add(_hamburgerBtn);
            topRow.Children.Add(_lutSelector);
            topRow.Children.Add(_rangeBar);

            // Shrink LUT ComboBox first; only after it reaches 0 does ValueRangeBar compress.
            // fixedW   = hamburger(26) + settingsBtn(22) + lutVrRevertBtn(22) + buffer(4)
            // labelW   = "LUT:" label + spacing + panel margins ≈ 42 px
            // rangeMin = ValueRangeBar minimum (both min/max boxes at MinBoxWidth=36)
            topRow.SizeChanged += (_, e) =>
            {
                const double fixedW = 26 + 22 + 22 + 4;
                const double labelW = 42;
                const double rangeMin = 240;
                double comboW = Math.Clamp(e.NewSize.Width - fixedW - labelW - rangeMin, 0, 97);
                _lutSelector.ComboWidth = comboW;
            };

            return topRow;
        }

        /// <summary>
        /// Creates the collapsible inline settings panel containing the LUT level
        /// spinner, invert toggle, and histogram
        /// Event wiring is deferred to <see cref="WireLutSettingsPanelEvents"/>.
        /// </summary>
        private Border BuildLutModeDetails()
        {
            _levelNud = new NumericUpDown
            {
                Minimum = 2,
                Maximum = 4096,
                Value = 256,
                Increment = 1,
                Width = 60,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(4, 0),
            };
            _levelNud.Classes.Add("compact");

            _invertLutChk = new ToggleButton
            {
                // ◑ half-filled circle: circle outline (Stroke) + right semicircle (Fill).
                // PathIcon supports only Fill, so two overlaid Path elements are used.
                Content = new PathIcon
                {
                    Data = Geometry.Parse(
                        "F1 " +
                        "M 8,1 A 7,7 0 0,1 8,15 A 7,7 0 0,1 8,1 Z " +
                        "M 8,2 A 6,6 0 0,0 8,14 L 8,2 Z"),
                    Width = 14,
                    Height = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(5, 0),
            };
            ToolTip.SetTip(_invertLutChk, "Invert LUT");

            // Histogram visualization (shown only when settings panel is expanded)
            _histogramPlot = new HistogramPlotControl
            {
                Width = 280,  // Initial width matching MaxHistogramWidth; resized dynamically via _rangeBar.SizeChanged
                Height = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0,2,0,4),
            };

            var settingsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(30, 2, 10, 2),
            };
            settingsRow.Children.Add(new TextBlock
            {
                Text = "Level:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            settingsRow.Children.Add(_levelNud);
            settingsRow.Children.Add(_invertLutChk);
            settingsRow.Children.Add(new Border
            {
                Width = 1,
                Background = Brushes.Gray,
                Margin = new Thickness(5, 3),
            });
            settingsRow.Children.Add(_histogramPlot);

            // Dynamically adjust histogram width based on available space in settingsRow.
            // Fixed elements: "Level:" label (~35px) + LevelNud (60px) + InvertChk (~35px)
            //                 + separator (1px + 10px margin) + spacing (4px × 3) = ~157px
            // Margins: left (30px) + right (10px) = 40px
            // Total fixed width: ~197px
            settingsRow.SizeChanged += (_, e) =>
            {
                if (_histogramPlot == null) return;

                const double FixedElementsWidth = 197.0;  // Sum of all non-histogram elements
                const double MaxHistogramWidth = 280.0;
                const double MinHistogramWidth = 50.0;
                
                double availableWidth = e.NewSize.Width - FixedElementsWidth;
                _histogramPlot.Width = Math.Clamp(availableWidth, MinHistogramWidth, MaxHistogramWidth);
            };

            return new Border
            {
                Child = settingsRow,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = false,
            };
        }

        /// <summary>
        /// Stores the <see cref="Border"/> instances returned by
        /// <see cref="BuildStatusBar"/>, <see cref="BuildLutModeHeader"/>, and
        /// <see cref="BuildLutModeDetails"/> into their backing fields, then
        /// assembles the root <see cref="DockPanel"/> and sets it as
        /// <see cref="Window.Content"/> wrapped in <see cref="_contentBorder"/>.
        /// </summary>
        private void BuildRootLayout()
        {
            var statusBar = BuildStatusBar();
            _lutHeaderRow = BuildLutModeHeader();
            _lutModeDetails = BuildLutModeDetails();
            var toast = BuildToastPanel();

            // Wrap the LUT header/panel in swappable containers so RenderingMode.Composite
            // can swap in its own header/panel (see MatrixPlotter.Composite.cs) without
            // touching BuildLutModeHeader()/BuildLutModeDetails() or any of their event wiring.
            _headerContainer = new ContentControl { Content = _lutHeaderRow };
            _detailsContainer = new ContentControl { Content = _lutModeDetails };

            _headerContainer.Height = 30;

            var dock = new DockPanel();
            DockPanel.SetDock(_headerContainer, Dock.Top);
            DockPanel.SetDock(_detailsContainer, Dock.Top);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            dock.Children.Add(_headerContainer);
            dock.Children.Add(_detailsContainer);
            dock.Children.Add(statusBar);
            dock.Children.Add(_orthoPanel);

            _orthoPanel.SetTopPanel(_trackerPanel);
            //_contentBorder = new Border { Child = dock };

            var root = new Grid();
            root.Children.Add(dock);
            root.Children.Add(toast); // overlay for toast messages

            _contentBorder = new Border { Child = root };


            Content = _contentBorder;
        }

        // ── Event wiring ──────────────────────────────────────────────────────

        /// <summary>
        /// Wires all events from <see cref="_view"/>, <see cref="OrthogonalPanel.BottomView"/>,
        /// and <see cref="OrthogonalPanel.RightView"/>: overlay manager callbacks,
        /// clipboard toast, crop/extract requests, and complex-value-mode sync.
        /// </summary>
        private void WireViewEvents()
        {
            // ── Main view ────────────────────────────────────────────────────
            _view.MatrixDataChanged += (_, data) =>
            {
                if (_reentrancy.IsActive(GuardContext.Initializing)) return;
                SetMatrixData(data);
            };
            // MxView.Refresh() is a public entry point in its own right (MxView supports standalone
            // use), so a caller holding MainView directly can call it without going through
            // MatrixPlotter.Refresh(). Routing that back into our own Refresh() keeps derived state
            // (histogram, overlay analysis, the Refreshed event for linked windows/sync-source
            // consumers) consistent regardless of which entry point was used. Refresh()'s own
            // _isRefreshing guard absorbs the reentrant case where this fires because we ourselves
            // just called _view.Refresh() from DoRefresh().
            _view.RefreshRequested += (_, _) => Refresh();
            // Upsync for external-control pattern 3 (plotter.MainView.Xxx = value set directly):
            // push the change into the ViewModel so Facade properties and UI chrome stay in sync
            // the same way plotter.ViewModel.Xxx = value already does.
            _view.RenderingPropertyChanged += (_, change) =>
            {
                // Deliberately NOT excluding GuardContext.SyncApply here: this handler must still
                // update vm.Xxx when this plotter is the RECEIVING end of a cross-plotter Linked
                // Sync application (SyncApplyLut/SyncApplyRangeMode/etc. wrap SyncApply while
                // pushing an inbound value into _view/chrome) — otherwise the VM (and hence the
                // external-control Facade) would silently go stale the moment a sync group applies
                // anything. Loop prevention against re-entrantly re-applying back down is already
                // handled at the OnDataContextChanged switch-case level (which does check SyncApply),
                // not here.
                //
                // GuardContext.UiSync IS excluded: it marks a multi-statement _view mutation (e.g.
                // WireRangeBarEvents' ModeChanged handler) still in progress via
                // ApplyViewMutationAtomically — reacting mid-mutation would read/write half-updated
                // VM state. ApplyViewMutationAtomically calls SyncViewModelFromView() once the
                // mutation completes, so the VM still ends up correct afterward.
                if (_reentrancy.IsActive(GuardContext.UiSync | GuardContext.Initializing)) return;
                if (DataContext is not MatrixPlotterViewModel vm) return;
                if (change.Property == MxView.LutProperty) { if (_view.Lut != null) vm.Lut = _view.Lut; }
                else if (change.Property == MxView.IsFixedRangeProperty) vm.IsFixedRange = _view.IsFixedRange;
                else if (change.Property == MxView.FixedMinProperty) vm.FixedMin = _view.FixedMin;
                else if (change.Property == MxView.FixedMaxProperty) vm.FixedMax = _view.FixedMax;
                else if (change.Property == MxView.IsInvertedColorProperty) vm.IsInvertedColor = _view.IsInvertedColor;
                else if (change.Property == MxView.LutDepthProperty) vm.LutDepth = _view.LutDepth;
                else if (change.Property == MxView.FrameIndexProperty)
                {
                    // Push into MatrixData.ActiveIndex (the single source of truth) rather than
                    // vm.ActiveIndex directly, so this goes through the same _activeIndexHandler
                    // cascade (ortho views, Composite frame indices, histogram) that an AxisTracker-
                    // driven frame change already does — not just a narrower View/VM-only update.
                    if (_currentData != null)
                    {
                        int clampedIndex = Math.Clamp(_view.FrameIndex, 0, _currentData.FrameCount - 1);
                        _currentData.ActiveIndex = clampedIndex;
                        // Same stranded-value hazard as the vm.ActiveIndex case: if the clamped value
                        // already equals MatrixData.ActiveIndex's current value, the line above is a
                        // no-op and never cascades back to correct _view.FrameIndex itself.
                        if (_view.FrameIndex != clampedIndex)
                            _view.FrameIndex = clampedIndex;
                    }
                }
            };
            _view.OverlayManager.ObjectAdded += OnOverlayObjectAdded;
            _view.OverlayManager.ObjectRemoved += OnOverlayObjectRemoved;
            _view.OverlayManager.GhostUpdated += (_, g) =>
                _view.OverlayInfoText = g.GetInfo(_view.MatrixData);
            _view.OverlayManager.GhostCancelled += (_, _) =>
                _view.OverlayInfoText = null;
            _view.CopiedToClipboard += (_, msg) => ShowToast(msg);
            _view.CropRequested += (_, _) => InvokeCropAction();
            _view.ExtractFrameRequested += (_, _) => InvokeExtractFrame(_view);
            _view.ExtractDimensionRequested += (_, _) => _ = InvokeExtractDimensionAsync();
            _view.ScrollStateChanged += (_, _) => UpdateStatusBar();
            _view.SyncComplexValueModeRequested += (_, mode) =>
            {
                _view.ComplexValueMode = mode;
                SyncComplexValueModeChanged();
            };

            // ── Bottom orthogonal view ────────────────────────────────────────
            _orthoPanel.BottomView.OverlayManager.ObjectAdded += OnOverlayObjectAdded;
            _orthoPanel.BottomView.OverlayManager.ObjectRemoved += OnOverlayObjectRemoved;
            _orthoPanel.BottomView.OverlayManager.GhostUpdated += (_, g) =>
                _orthoPanel.BottomView.OverlayInfoText = g.GetInfo(_orthoPanel.BottomView.MatrixData);
            _orthoPanel.BottomView.OverlayManager.GhostCancelled += (_, _) =>
                _orthoPanel.BottomView.OverlayInfoText = null;
            _orthoPanel.BottomView.CopiedToClipboard += (_, msg) => ShowToast(msg);
            _orthoPanel.BottomView.CropRequested += (_, _) => InvokeCropAction();
            _orthoPanel.BottomView.ExtractFrameRequested += (_, _) => InvokeExtractFrame(_orthoPanel.BottomView);
            _orthoPanel.BottomView.SyncComplexValueModeRequested += (_, mode) =>
            {
                _view.ComplexValueMode = mode;
                SyncComplexValueModeChanged();
            };
            _orthoPanel.BottomView.MatrixDataChanged += (_, _) => OnSideViewMatrixDataChanged(_orthoPanel.BottomView);

            // ── Right orthogonal view ─────────────────────────────────────────
            _orthoPanel.RightView.OverlayManager.ObjectAdded += OnOverlayObjectAdded;
            _orthoPanel.RightView.OverlayManager.ObjectRemoved += OnOverlayObjectRemoved;
            _orthoPanel.RightView.OverlayManager.GhostUpdated += (_, g) =>
                _orthoPanel.RightView.OverlayInfoText = g.GetInfo(_orthoPanel.RightView.MatrixData);
            _orthoPanel.RightView.OverlayManager.GhostCancelled += (_, _) =>
                _orthoPanel.RightView.OverlayInfoText = null;
            _orthoPanel.RightView.CopiedToClipboard += (_, msg) => ShowToast(msg);
            _orthoPanel.RightView.CropRequested += (_, _) => InvokeCropAction();
            _orthoPanel.RightView.ExtractFrameRequested += (_, _) => InvokeExtractFrame(_orthoPanel.RightView);
            _orthoPanel.RightView.SyncComplexValueModeRequested += (_, mode) =>
            {
                _view.ComplexValueMode = mode;
                SyncComplexValueModeChanged();
            };
            _orthoPanel.RightView.MatrixDataChanged += (_, _) => OnSideViewMatrixDataChanged(_orthoPanel.RightView);

            // ── Dirty badge ───────────────────────────────────────────────────
            IsModifiedChanged += (_, _) => UpdateDirtyBadge();

#if DEBUG
            SizeChanged += (_, _) =>
                Debug.WriteLine($"Window size: W:{Bounds.Width:F0} H:{Bounds.Height:F0}");
#endif
        }

        /// <summary>
        /// Wires the <see cref="LutSelector.SelectedLutChanged"/> event: propagates the
        /// new LUT to the view, orthogonal controller, view-model, and window icon;
        /// updates the dirty state; and raises the sync event when not in a sync-apply scope.
        /// </summary>
        private void WireLutSelectorEvents()
        {
            _lutSelector.SelectedLutChanged += (_, lut) =>
            {
                if (lut == null) return;
                // ColorCoded projection child: this selects the depth palette, not a value→colour
                // LUT -- TrueColorBitmapWriter ignores _view.Lut entirely, so report the change
                // upward instead of applying it here (see MatrixPlotter.ColorCoded.cs).
                if (IsColorCodedProjectionChild)
                {
                    RaiseColorCodedParamsChanged();
                    // Same window-icon tracking as ordinary LUT mode below (UpdateWindowIcon() ->
                    // _lutSelector.SelectedIcon) -- here that's the depth palette's gradient.
                    UpdateWindowIcon();
                    return;
                }
                _view.Lut = lut;
                _orthoController.SyncRenderSettings();
                if (DataContext is MatrixPlotterViewModel vm)
                    vm.Lut = lut;
                UpdateWindowIcon();
                SaveViewSettings();

                // If the selected LUT matches the snapshot the state is clean — not dirty.
                // This also suppresses Avalonia's deferred SelectionChanged re-fire on
                // TemplateApplied, which would otherwise produce a false-positive revert
                // button for non-default LUTs like BSMod.
                bool lutMatchesSnapshot = _renderSnapshot != null
                    && string.Equals(lut.Name, _renderSnapshot.LutName, StringComparison.OrdinalIgnoreCase)
                    && _view.LutDepth == _renderSnapshot.LutLevel
                    && _view.IsInvertedColor == _renderSnapshot.Inverted;
                SetLutDirty(!lutMatchesSnapshot);

                // Recolor the histogram if settings panel is expanded. A palette swap changes only
                // which colors the bars are tinted with -- bin count and data/view range are
                // unaffected -- so this stays on the cheap path (no pixel rescan).
                if (_lutModeDetails?.IsVisible == true)
                    UpdateHistogramColors();

                if (!_reentrancy.IsActive(GuardContext.SyncApply))
                    SyncLutChanged?.Invoke(this, lut);
            };
        }

        /// <summary>
        /// Wires events for the settings-panel toggle button (<see cref="_settingsBtn"/>),
        /// LUT level spinner (<see cref="_levelNud"/>), invert toggle (<see cref="_invertLutChk"/>),
        /// and value-range mode radio buttons.
        /// These handlers propagate user intent to the view and dirty-state tracker.
        /// </summary>
        private void WireLutSettingsPanelEvents()
        {
            // ── Settings panel open/close toggle ─────────────────────────────
            // Reads/animates ActiveDetailsPanel (MatrixPlotter.ColorCoded.cs), not a hardcoded
            // _lutModeDetails, so this keeps working after a ColorCoded details-panel swap --
            // always _lutModeDetails for an ordinary window, so behaviour there is unchanged.
            _settingsBtn.Click += (_, _) =>
            {
                var panel = ActiveDetailsPanel;
                if (panel == null) return;
                bool opening = !panel.IsVisible;
                double panelH = panel.Bounds.Height;

                panel.IsVisible = opening;
                _settingsBtn.Content = opening ? "▴" : "▾";
                _settingsBtn.Background = opening ? Brushes.LightGray : Brushes.Transparent;

                // Refresh histogram when opening settings panel (meaningless for the ColorCoded
                // details panel, which has no histogram control at all).
                if (opening && _histogramPlot != null && !IsColorCodedProjectionChild)
                    UpdateHistogram();

                if (WindowState == WindowState.Maximized) return;

                if (!opening && panelH > 0)
                {
                    Height -= panelH;
                }
                else if (opening)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        double h = panel.Bounds.Height;
                        if (h > 0) Height += h;
                    }, DispatcherPriority.Background);
                }
            };

            // ── LUT level ─────────────────────────────────────────────────────
            _levelNud.ValueChanged += (_, _) =>
            {
                _view.LutDepth = (int)(_levelNud.Value ?? 256);
                _orthoController.SyncRenderSettings();
                SaveViewSettings();
                bool lutMatchesSnapshot = _renderSnapshot != null
                    && _view.LutDepth == _renderSnapshot.LutLevel
                    && string.Equals(_view.Lut?.Name, _renderSnapshot.LutName, StringComparison.OrdinalIgnoreCase)
                    && _view.IsInvertedColor == _renderSnapshot.Inverted;
                SetLutDirty(!lutMatchesSnapshot);

                // Update histogram LUT if settings panel is expanded
                if (_lutModeDetails?.IsVisible == true && _histogramPlot != null)
                    UpdateHistogram();

                if (!_reentrancy.IsActive(GuardContext.SyncApply))
                    SyncLutDepthChanged?.Invoke(this, _view.LutDepth);
            };

            // ── Invert LUT ────────────────────────────────────────────────────
            _invertLutChk.IsCheckedChanged += (_, _) =>
            {
                _view.IsInvertedColor = _invertLutChk.IsChecked == true;
                _orthoController.SyncRenderSettings();
                SaveViewSettings();
                bool lutMatchesSnapshot = _renderSnapshot != null
                    && _view.IsInvertedColor == _renderSnapshot.Inverted
                    && string.Equals(_view.Lut?.Name, _renderSnapshot.LutName, StringComparison.OrdinalIgnoreCase)
                    && _view.LutDepth == _renderSnapshot.LutLevel;
                SetLutDirty(!lutMatchesSnapshot);

                // Recolor the histogram when invert is toggled (if settings panel is expanded).
                // Inversion only reverses the color array -- no pixel rescan needed.
                if (_lutModeDetails?.IsVisible == true)
                    UpdateHistogramColors();

                if (!_reentrancy.IsActive(GuardContext.SyncApply))
                    SyncInvertedChanged?.Invoke(this, _view.IsInvertedColor);
            };


            // ── HistogramPlot → ValueRangeBar (reverse sync) ──────────────────
            // When user drags red boundaries in the histogram, force Fixed mode
            // and update the range bar, which then propagates to MxView.
            _histogramPlot.ViewRangeChanged += (min, max) =>
            {
                Debug.WriteLine($"[HistogramPlot.ViewRangeChanged] min={min:F3}, max={max:F3}, UiSync={_reentrancy.IsActive(GuardContext.UiSync)}");
                if (_reentrancy.IsActive(GuardContext.UiSync)) return;

                using var _ = _reentrancy.Begin(GuardContext.UiSync);

                // User interaction with histogram always implies explicit Fixed range
                if(_rangeBar.Mode != ValueRangeMode.Fixed)
                    _rangeBar.SetMode(ValueRangeMode.Fixed);
                _rangeBar.SetRange(min, max);

                // ModeChanged and RangeChanged events in WireRangeBarEvents() will handle:
                // - MxView property updates (IsFixedRange, FixedMin, FixedMax)
                // - OrthogonalController sync
                // - Dirty state tracking
                // - Histogram refresh via UpdateHistogram()
            };

        }

        /// <summary>
        /// Wires all <see cref="ValueRangeBar"/> events to the main view and orthogonal
        /// controller: mode changes, range edits, and min/max search requests.
        /// Also wires <see cref="MxView.AutoRangeComputed"/> so that the bar tracks the
        /// current-frame range while in <see cref="ValueRangeMode.Current"/> mode.
        /// </summary>
        private void WireRangeBarEvents()
        {
            _view.AutoRangeComputed += (_, args) =>
            {
                if (_rangeBar.Mode == ValueRangeMode.Current)
                    _rangeBar.SetRange(args.Min, args.Max);
            };

            _rangeBar.ModeChanged += (_, mode) =>
            {
                // ColorCoded projection child: Fixed/Auto here means "pin the intensity range" vs
                // "re-derive it from the winner values every recompute", both handled entirely on
                // the parent's OrthogonalViewController -- report upward instead of touching
                // _view.IsFixedRange/FixedMin/FixedMax, which TrueColorBitmapWriter ignores.
                if (IsColorCodedProjectionChild)
                {
                    RaiseColorCodedParamsChanged();
                    return;
                }
                // Wrapped: this block sets IsFixedRange/FixedMin/FixedMax across several separate
                // statements. Without ApplyViewMutationAtomically, the very first assignment could
                // reentrantly cascade (View -> VM upsync -> OnDataContextChanged switch -> back into
                // View/RangeBar) using the ViewModel's stale prior values, corrupting a property this
                // block hasn't gotten to setting yet — this is exactly how the first Fixed-mode
                // transition after opening a plotter used to reset FixedMax to its unset default.
                ApplyViewMutationAtomically(() =>
                {
                    _view.IsFixedRange = mode != ValueRangeMode.Current;

                    if (mode == ValueRangeMode.Fixed)
                    {
                        if (!double.IsNaN(_rangeBar.DisplayedMinValue))
                        {
                            _view.FixedMin = _rangeBar.DisplayedMinValue;
                            _view.FixedMax = _rangeBar.DisplayedMaxValue;
                        }
                        else
                        {
                            var (min, max) = _view.ScanCurrentFrameRange();
                            _rangeBar.SetRange(min, max);
                            _view.FixedMin = min;
                            _view.FixedMax = max;
                        }
                    }
                    else if (mode == ValueRangeMode.All)
                    {
                        ApplyAllModeRange();
                        Debug.WriteLine($"[ModeChanged] All mode: _rangeBar.DisplayedMin={_rangeBar.DisplayedMinValue:F3}, Max={_rangeBar.DisplayedMaxValue:F3}");
                    }
                    else if (mode == ValueRangeMode.Roi)
                    {
                        _view.IsFixedRange = true;
                        RefreshRoiValueRange();
                    }
                    else // Current
                    {
                        var (min, max) = _view.ScanCurrentFrameRange();
                        _rangeBar.SetRange(min, max);
                    }
                });

                _orthoController.SyncRenderSettings();
                SaveViewSettings();

                bool vrMatchesSnapshot = _renderSnapshot != null
                    && mode == _renderSnapshot.VrMode
                    && (mode != ValueRangeMode.Fixed
                        || (_view.FixedMin == _renderSnapshot.VrMin
                            && _view.FixedMax == _renderSnapshot.VrMax));
                SetVrDirty(!vrMatchesSnapshot);

                // Update histogram when mode changes (if settings panel is open)
                if (_lutModeDetails?.IsVisible == true && _histogramPlot != null)
                {
                    // Rebuild histogram with new mode-specific ranges
                    // UpdateHistogram() will read the correct viewRange from _rangeBar
                    // and set both viewRange and plotRange appropriately via SetHistogram()
                    Debug.WriteLine($"[ModeChanged] Updating histogram for mode={mode}");
                    UpdateHistogram();
                }

                if (!_reentrancy.IsActive(GuardContext.SyncApply))
                {
                    SyncRangeModeChanged?.Invoke(this, mode);
                    // When switching to Fixed the min/max are resolved inside the Fixed branch
                    // above but RangeChanged is never fired (SetRange suppresses it via _updating).
                    // Explicitly propagate the resolved values so sync targets receive them.
                    if (mode == ValueRangeMode.Fixed)
                        SyncFixedRangeChanged?.Invoke(this, (_view.FixedMin, _view.FixedMax));
                }
            };

            _rangeBar.RangeChanged += (_, args) =>
            {
                if (IsColorCodedProjectionChild)
                {
                    RaiseColorCodedParamsChanged();
                    return;
                }
                _view.FixedMin = args.Min;
                _view.FixedMax = args.Max;
                _orthoController.SyncRenderSettings();
                SaveViewSettings();
                bool vrMatchesSnapshot = _renderSnapshot != null
                    && _renderSnapshot.VrMode == ValueRangeMode.Fixed
                    && _view.FixedMin == _renderSnapshot.VrMin
                    && _view.FixedMax == _renderSnapshot.VrMax;
                SetVrDirty(!vrMatchesSnapshot);

                if (_lutModeDetails?.IsVisible == true && _histogramPlot != null)
                {
                    if (!_reentrancy.IsActive(GuardContext.UiSync))
                    {
                        // External range change: rebuild histogram bins with new LUT range
                        UpdateHistogram();
                    }
                    else
                    {
                        // During histogram drag (UiSync active): only update red line positions
                        // without rebuilding bins (avoids resetting _plotMin/_plotMax)
                        _histogramPlot.SetViewValueRange(args.Min, args.Max);
                    }
                }

                if (!_reentrancy.IsActive(GuardContext.SyncApply))
                    SyncFixedRangeChanged?.Invoke(this, (args.Min, args.Max));
            };

            _rangeBar.SearchMinRequested += (_, _) =>
            {
                // ColorCoded projection child: this window's "current frame" is the packed-ARGB
                // image itself -- scanning it as a value range would be meaningless. Search instead
                // reuses the natural (winnerValue) range the parent already computed every recompute.
                if (IsColorCodedProjectionChild) { SearchColorCodedMin(); return; }
                // Wrapped: this reads back _view.FixedMax after setting FixedMin, so it's vulnerable
                // to the same reentrant-corruption hazard as ModeChanged above (see its comment).
                ApplyViewMutationAtomically(() =>
                {
                    var (min, _) = _view.ScanCurrentFrameRange();
                    _view.FixedMin = min;
                    _rangeBar.SetRange(min, _view.FixedMax);
                });
                _orthoController.SyncRenderSettings();
            };

            _rangeBar.SearchMaxRequested += (_, _) =>
            {
                if (IsColorCodedProjectionChild) { SearchColorCodedMax(); return; }
                ApplyViewMutationAtomically(() =>
                {
                    var (_, max) = _view.ScanCurrentFrameRange();
                    _view.FixedMax = max;
                    _rangeBar.SetRange(_view.FixedMin, max);
                });
                _orthoController.SyncRenderSettings();
            };
        }

        /// <summary>
        /// Wires <see cref="OrthogonalPanel"/> splitter-drag and auto-resize events
        /// so that dragging a splitter or requesting an auto-fit adjusts the window
        /// dimensions to accommodate the updated panel sizes.
        /// </summary>
        private void WireWindowResizeEvents()
        {
            _orthoPanel.VerticalSplitterDragged += delta =>
            {
                if (WindowState == WindowState.Normal) Width += delta;
            };
            _orthoPanel.HorizontalSplitterDragged += delta =>
            {
                if (WindowState == WindowState.Normal) Height += delta;
            };

            _orthoPanel.AutoResizeBottomRequested += () =>
            {
                if (WindowState != WindowState.Normal) return;
                var (_, bmpH) = _orthoPanel.BottomView.GetEffectiveBmpDims();
                int zCount = _orthoPanel.BottomView.MatrixData?.YCount ?? 1;
                double oneDip = zCount > 0 ? bmpH / zCount : 50.0;
                double margin = Math.Max(oneDip, 50.0);
                double delta = bmpH + margin - _orthoPanel.BottomView.Bounds.Height;
                if (delta <= 0.5) return;
                double newH = Height + delta;
                var screen = Screens?.ScreenFromWindow(this);
                if (screen != null)
                    newH = Math.Min(newH, (screen.WorkingArea.Bottom - Position.Y) / screen.Scaling);
                Height = newH;
            };

            _orthoPanel.AutoResizeRightRequested += () =>
            {
                if (WindowState != WindowState.Normal) return;
                var (bmpW, _) = _orthoPanel.RightView.GetEffectiveBmpDims();
                int zCount = _orthoPanel.RightView.MatrixData?.YCount ?? 1;
                double oneDip = zCount > 0 ? bmpW / zCount : 50.0;
                double margin = Math.Max(oneDip, 50.0);
                double delta = bmpW + margin - _orthoPanel.RightView.Bounds.Width;
                if (delta <= 0.5) return;
                double newW = Width + delta;
                var screen = Screens?.ScreenFromWindow(this);
                if (screen != null)
                    newW = Math.Min(newW, (screen.WorkingArea.Right - Position.X) / screen.Scaling);
                Width = newW;
            };

            _orthoPanel.AutoResizeMainRequested += () =>
            {
                if (WindowState != WindowState.Normal) return;
                if (_orthoPanel.MainView.MatrixData == null) return;

                var (bmpW, bmpH) = _orthoPanel.MainView.GetEffectiveBmpDims();
                double pad = _orthoPanel.MainView.BitmapPadding * 2;
                double targetW = Math.Ceiling(bmpW) + pad + 1.0;
                double targetH = Math.Ceiling(bmpH) + pad + 1.0;

                var screen = Screens?.ScreenFromWindow(this);
                if (screen != null)
                {
                    double nonW = Width - _orthoPanel.MainView.Bounds.Width;
                    double nonH = Height - _orthoPanel.MainView.Bounds.Height;
                    targetW = Math.Min(targetW, (screen.WorkingArea.Right - Position.X) / screen.Scaling - nonW);
                    targetH = Math.Min(targetH, (screen.WorkingArea.Bottom - Position.Y) / screen.Scaling - nonH);
                }

                bool isOrtho = _orthoPanel.ShowRight || _orthoPanel.ShowBottom;
                if (isOrtho)
                {
                    _orthoPanel.SetMainViewPixelSize(targetW, targetH);
                    Dispatcher.UIThread.Post(
                        () => _orthoPanel.MainView.FitToView(),
                        DispatcherPriority.Background);
                }
                else
                {
                    double dW = targetW - _orthoPanel.MainView.Bounds.Width;
                    double dH = targetH - _orthoPanel.MainView.Bounds.Height;
                    if (Math.Abs(dW) > 0.5) Width += dW;
                    if (Math.Abs(dH) > 0.5) Height += dH;
                    Dispatcher.UIThread.Post(
                        () => _orthoPanel.MainView.FitToView(),
                        DispatcherPriority.Background);
                }
            };
        }
    }
}