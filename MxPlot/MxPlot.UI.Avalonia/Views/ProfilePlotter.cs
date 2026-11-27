using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MxPlot.UI.Avalonia.Analysis;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Standalone window for displaying profile/scatter plots.
    /// Includes a hamburger menu (File / Edit / Property / Analyze) that expands as a
    /// collapsible left panel, and a collapsible, resizable <see cref="InfoPanel"/> on the right side.
    /// </summary>
    public class ProfilePlotter : Window
    {
        // ── Layout constants ──────────────────────────────────────────────────
        private const double SplitterThickness = 4.0;
        private const double InitialInfoWidth = 220.0;
        private const double MinInfoWidth = 80.0;
        private const double MinPlotWidth = 200.0;
        private const double MenuPanelWidth = 180.0;
        private const double MinMenuWidth = 80.0;

        // ── Public surface ────────────────────────────────────────────────────

        /// <summary>The hosted plot control.</summary>
        public ProfilePlotControl Plot { get; }

        // ── Info panel ────────────────────────────────────────────────────────

        private Grid _contentGrid = null!;
        private Border _infoSplitter = null!;
        private Border _infoPanel = null!;
        private TextBox _infoTextBox = null!;
        private Button _infoPanelBtn = null!;
        private bool _showInfo;
        private double _savedInfoWidth = InitialInfoWidth;

        // ── Fit state ─────────────────────────────────────────────────────────
        private IProfileFitter? _activeFitter;
        private CheckBox? _fitCurveToggle;

        /// <summary>User-adjustable fit options, edited in the gear flyout next to "Fit curve".</summary>
        private sealed class FitSettings
        {
            /// <summary>Index of the series to fit; clamped to the available series at fit time.</summary>
            public int SeriesIndex;
            /// <summary>When true the curve gets max(300, 2 x data points) samples.</summary>
            public bool AutoCurvePoints = true;
            /// <summary>Number of samples of the fit curve when <see cref="AutoCurvePoints"/> is off.</summary>
            public int CurvePoints = 300;

            private readonly Dictionary<string, double?[]> _fixedByFitter = [];

            /// <summary>
            /// The values the parameters of <paramref name="fitter"/> are held at, one entry per parameter
            /// (<c>null</c> = free). Kept per fitter, so switching fitters and back finds them again.
            /// The array is edited in place.
            /// </summary>
            public double?[] FixedFor(IProfileFitter fitter)
            {
                if (!_fixedByFitter.TryGetValue(fitter.Name, out var values) || values.Length != fitter.ParameterCount)
                    _fixedByFitter[fitter.Name] = values = new double?[fitter.ParameterCount];
                return values;
            }
        }
        private readonly FitSettings _fitSettings = new();

        private LevenbergMarquardtSolver.FitResult? _lastFit;
        // Refreshes the fitted values shown in the parameters flyout; set only while that flyout is open.
        private Action? _refreshFitParameterValues;

        // Plot size mode
        private bool _plotSizeFixed = false;
        private double _fixedPlotWidth = 500.0;
        private double _fixedPlotHeight = 420.0;
        private ScrollViewer _plotScrollViewer = null!;
        private ToggleButton? _sizeFitToggle;
        private ToggleButton? _sizeFixedToggle;
        private NumericUpDown? _plotWidthNud;
        private NumericUpDown? _plotHeightNud;

        private WindowNotificationManager? _notificationManager;

        /// <summary>Gets or sets the text displayed in the info panel.</summary>
        public string InfoText
        {
            get => _infoTextBox.Text ?? "";
            set => _infoTextBox.Text = value;
        }

        /// <summary>
        /// Appends <paramref name="line"/> to the info panel.
        /// If the panel is hidden, it is shown automatically.
        /// </summary>
        public void AppendInfoLine(string line)
        {
            _infoTextBox.Text = string.IsNullOrEmpty(_infoTextBox.Text)
                ? line
                : _infoTextBox.Text + "\n" + line;
            if (!_showInfo) ToggleInfoPanel();
        }

        /// <summary>Clears the info panel text.</summary>
        public void ClearInfo() => _infoTextBox.Text = "";

        /// <summary>Gets or sets whether the plot uses a fixed pixel size.
        /// When <c>false</c> (default) the plot stretches to fill the available area.</summary>
        public bool PlotSizeFixed
        {
            get => _plotSizeFixed;
            set
            {
                if (_plotSizeFixed == value) return;
                if (value) { if (_sizeFixedToggle != null) _sizeFixedToggle.IsChecked = true; }
                else        { if (_sizeFitToggle  != null) _sizeFitToggle.IsChecked  = true; }
            }
        }

        /// <summary>Gets or sets the fixed plot width in device-independent pixels.
        /// Takes effect when <see cref="PlotSizeFixed"/> is <c>true</c>.</summary>
        public double FixedPlotWidth
        {
            get => _fixedPlotWidth;
            set
            {
                if (_plotWidthNud != null) _plotWidthNud.Value = (decimal)value;
                else { _fixedPlotWidth = value; if (_plotSizeFixed) ApplyPlotSizeMode(); }
            }
        }

        /// <summary>Gets or sets the fixed plot height in device-independent pixels.
        /// Takes effect when <see cref="PlotSizeFixed"/> is <c>true</c>.</summary>
        public double FixedPlotHeight
        {
            get => _fixedPlotHeight;
            set
            {
                if (_plotHeightNud != null) _plotHeightNud.Value = (decimal)value;
                else { _fixedPlotHeight = value; if (_plotSizeFixed) ApplyPlotSizeMode(); }
            }
        }

        // ── Menu side panel state ─────────────────────────────────────────────

        private Border _menuSidePanel = null!;
        private Border _menuSplitter = null!;
        private Button _hamburgerBtn = null!;
        private bool _showMenu;
        private double _savedMenuWidth = MenuPanelWidth;
        // Opens/closes the "Property" group, and shows the Axes flyout with focus on the given part.
        private Action<bool>? _setPropertyExpanded;
        private Action<PlotAxisRegion>? _showAxesFlyout;
        private StackPanel _plotStyleContainer = null!;
        private StackPanel _fitStyleSection    = null!;
        private StackPanel _dataVisibilityContainer = null!;

        // ── Color palette for series color picker ─────────────────────────────
        private static readonly Color[] ColorPalette =
        [
            Color.FromRgb( 31, 119, 180), Color.FromRgb(255, 127,  14),
            Color.FromRgb( 44, 160,  44), Color.FromRgb(214,  39,  40),
            Color.FromRgb(148, 103, 189), Color.FromRgb(140,  86,  75),
            Color.FromRgb(227, 119, 194), Color.FromRgb( 23, 190, 207),
            Color.FromRgb(188, 189,  34), Color.FromRgb(127, 127, 127),
            Colors.Black,                 
            Colors.White,
            Color.FromRgb(  0, 120, 255), Color.FromRgb(255,  50,  50),
            Color.FromRgb(  0, 190, 100), Color.FromRgb(255, 200,   0),
        ];

        // ── Constructors ──────────────────────────────────────────────────────

        /// <summary>Shows one or more pre-built <see cref="PlotSeries"/>.</summary>
        public ProfilePlotter(
            IReadOnlyList<PlotSeries> series,
            string xAxisLabel = "",
            string yAxisLabel = "",
            string title = "Profile")
        {
            Title = title;
            Width =520;
            Height = 450;

            Plot = new ProfilePlotControl();
            Plot.SetData(series, xAxisLabel, yAxisLabel, title);

            BuildLayout();
            Plot.AxisRegionDoubleClicked += (_, region) => OpenAxesEditor(region);
            Plot.SeriesChanged += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                RebuildDataVisibilityControls();
                RebuildStyleControls();
            });
            RebuildDataVisibilityControls();
            RebuildStyleControls();

            this.Styles.Add(new Style(x => x.OfType<NotificationCard>()) //Toast
            {
                Setters =
                {
                    new Setter(MinHeightProperty, 0.0),
                    //new Setter(HeightProperty, 60.0), 
                    new Setter(VerticalContentAlignmentProperty, VerticalAlignment.Top),
                    new Setter(CornerRadiusProperty, new CornerRadius(4)),
                    new Setter(OpacityProperty, 0.95),
                    new Setter(WidthProperty, 250.0)
                }
            });

            _notificationManager = new WindowNotificationManager(this)
            {
                Position = NotificationPosition.BottomCenter,
                MaxItems = 2,
                Margin = new Thickness(0, 0, 10, 5)
            };

            PlotWindowNotifier.NotifyCreated(this);
        }

        /// <summary>Convenience: single data series.</summary>
        public ProfilePlotter(
            IReadOnlyList<(double X, double Y)> points,
            string name = "",
            string xAxisLabel = "",
            string yAxisLabel = "",
            PlotStyle style = PlotStyle.Line,
            string title = "Profile")
            : this([new PlotSeries(points, name, style)], xAxisLabel, yAxisLabel, title) { }

        /// <summary>Convenience: multiple series from parallel lists.</summary>
        public ProfilePlotter(
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> pointSets,
            IReadOnlyList<string>? names = null,
            string xAxisLabel = "",
            string yAxisLabel = "",
            PlotStyle style = PlotStyle.Line,
            string title = "Profile")
            : this(BuildSeries(pointSets, names, style), xAxisLabel, yAxisLabel, title) { }

        // ── Layout ────────────────────────────────────────────────────────────

        private void BuildLayout()
        {
            // ── Hamburger button ─────────────────────────────────────────────
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
            _hamburgerBtn.Click += (_, _) => ToggleMenuPanel();

            // ── Info panel toggle button (▶ / ◀) ────────────────────────────
            _infoPanelBtn = new Button
            {
                Content = "▶",
                Width = 22,
                Height = 20,
                FontSize = 11,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
            };
            ToolTip.SetTip(_infoPanelBtn, "Show / hide info panel");
            _infoPanelBtn.Click += (_, _) => ToggleInfoPanel();

            // ── Top bar ──────────────────────────────────────────────────────
            var topBar = new DockPanel { LastChildFill = false, Height = 24 };
            DockPanel.SetDock(_hamburgerBtn, Dock.Left);
            DockPanel.SetDock(_infoPanelBtn, Dock.Right);
            topBar.Children.Add(_hamburgerBtn);
            topBar.Children.Add(_infoPanelBtn);

            // ── Info panel content ───────────────────────────────────────────
            _infoTextBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(6),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                LineHeight = 14,
            };

            static IBrush? GetInfoBg() =>
                Application.Current?.ActualThemeVariant.ToString() == "Dark"
                    ? new SolidColorBrush(Color.FromRgb(30, 30, 30))
                    : new SolidColorBrush(Color.FromRgb(248, 248, 248));

            _infoPanel = new Border
            {
                IsVisible = false,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                BorderThickness = new Thickness(1, 0, 0, 0),
                Background = GetInfoBg(),
                Child = new DockPanel
                {
                    Children =
                    {
                        // Header strip
                        new Border
                        {
                            [DockPanel.DockProperty] = Dock.Top,
                            Padding         = new Thickness(6, 3),
                            BorderBrush     = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                            BorderThickness = new Thickness(0, 0, 0, 1),
                            Child = new TextBlock
                            {
                                Text       = "Info",
                                FontSize   = 11,
                                FontWeight = FontWeight.SemiBold,
                                Opacity    = 0.7,
                            },
                        },
                        // Scrollable text area
                        new ScrollViewer
                        {
                            Content = _infoTextBox,
                            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                        },
                    },
                },
            };

            // ── Vertical splitter ────────────────────────────────────────────
            _infoSplitter = new Border
            {
                Width = SplitterThickness,
                IsVisible = false,
                Background = Brushes.DarkGray,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            WireInfoSplitter();

            // ── Build menu side panel ────────────────────────────────────────
            _menuSidePanel = BuildMenuSidePanel();

            // ── Menu splitter ────────────────────────────────────────────────
            _menuSplitter = new Border
            {
                Width = SplitterThickness,
                IsVisible = false,
                Background = Brushes.DarkGray,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            WireMenuSplitter();

            // ── Content grid: [MenuPanel | MenuSplitter | Plot | InfoSplitter | InfoPanel] ─
            _contentGrid = new Grid();
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));  // Col 0: Menu
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));  // Col 1: MenuSplitter
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));    // Col 2: Plot
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));  // Col 3: InfoSplitter
            _contentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));  // Col 4: InfoPanel

            _plotScrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = Plot,
            };

            Grid.SetColumn(_menuSidePanel, 0);
            Grid.SetColumn(_menuSplitter, 1);
            Grid.SetColumn(_plotScrollViewer, 2);
            Grid.SetColumn(_infoSplitter, 3);
            Grid.SetColumn(_infoPanel, 4);

            _contentGrid.Children.Add(_menuSidePanel);
            _contentGrid.Children.Add(_menuSplitter);
            _contentGrid.Children.Add(_plotScrollViewer);
            _contentGrid.Children.Add(_infoSplitter);
            _contentGrid.Children.Add(_infoPanel);

            // ── Top-level layout ─────────────────────────────────────────────
            var dock = new DockPanel();
            DockPanel.SetDock(topBar, Dock.Top);
            dock.Children.Add(topBar);
            dock.Children.Add(_contentGrid);

            AttachContextMenu();
            Content = dock;

            // Update info panel background on theme change
            if (Application.Current is { } app)
            {
                void UpdateInfoBg(object? _, EventArgs __) => _infoPanel.Background = GetInfoBg();
                app.ActualThemeVariantChanged += UpdateInfoBg;
                Closed += (_, _) => app.ActualThemeVariantChanged -= UpdateInfoBg;
            }
        }

        // ── Info panel toggle ─────────────────────────────────────────────────

        private void ToggleInfoPanel()
        {
            bool opening = !_showInfo;
            _showInfo = opening;

            if (opening)
            {
                _contentGrid.ColumnDefinitions[3].Width = new GridLength(SplitterThickness);
                _contentGrid.ColumnDefinitions[4].Width = new GridLength(_savedInfoWidth);
                _contentGrid.ColumnDefinitions[4].MinWidth = MinInfoWidth;
                _infoSplitter.IsVisible = true;
                _infoPanel.IsVisible = true;
                _infoPanelBtn.Content = "◀";
                _infoPanelBtn.Background = Brushes.LightGray;

                if (WindowState != WindowState.Maximized)
                    Width += _savedInfoWidth + SplitterThickness;
            }
            else
            {
                // Capture current fixed width before zeroing the column
                double cur = _contentGrid.ColumnDefinitions[4].Width.Value;
                if (cur >= MinInfoWidth) _savedInfoWidth = cur;

                _contentGrid.ColumnDefinitions[3].Width = new GridLength(0);
                _contentGrid.ColumnDefinitions[4].Width = new GridLength(0);
                _contentGrid.ColumnDefinitions[4].MinWidth = 0;
                _infoSplitter.IsVisible = false;
                _infoPanel.IsVisible = false;
                _infoPanelBtn.Content = "▶";
                _infoPanelBtn.Background = Brushes.Transparent;

                if (WindowState != WindowState.Maximized)
                    Width -= cur + SplitterThickness;
            }
        }

        // ── Info splitter drag ────────────────────────────────────────────────

        private void WireInfoSplitter()
        {
            bool dragging = false;
            double dragOrigin = 0;

            _infoSplitter.PointerPressed += (_, e) =>
            {
                dragging = true;
                dragOrigin = e.GetPosition(this).X;
                e.Pointer.Capture(_infoSplitter);
                e.Handled = true;
            };
            _infoSplitter.PointerMoved += (_, e) =>
            {
                if (!dragging) return;
                double x = e.GetPosition(this).X;
                double delta = x - dragOrigin;
                if (Math.Abs(delta) < 0.5) return;
                dragOrigin = x;

                // drag right → splitter moves right → info panel shrinks (delta > 0 means ?)
                //double maxInfo = Bounds.Width - SplitterThickness - MinPlotWidth;
                //double newInfo = Math.Clamp(_savedInfoWidth - delta, MinInfoWidth, maxInfo);
                double maxInfo = Math.Max(MinInfoWidth, Bounds.Width - SplitterThickness - MinPlotWidth);
                double newInfo = Math.Clamp(_savedInfoWidth - delta, MinInfoWidth, maxInfo);
                _savedInfoWidth = newInfo;
                _contentGrid.ColumnDefinitions[4].Width = new GridLength(_savedInfoWidth);
                e.Handled = true;
            };
            _infoSplitter.PointerReleased += (_, e) =>
            {
                dragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
        }

        // ── Menu splitter drag ────────────────────────────────────────────────

        private void WireMenuSplitter()
        {
            bool dragging = false;
            double dragOrigin = 0;

            _menuSplitter.PointerPressed += (_, e) =>
            {
                dragging = true;
                dragOrigin = e.GetPosition(this).X;
                e.Pointer.Capture(_menuSplitter);
                e.Handled = true;
            };
            _menuSplitter.PointerMoved += (_, e) =>
            {
                if (!dragging) return;
                double x = e.GetPosition(this).X;
                double delta = x - dragOrigin;
                if (Math.Abs(delta) < 0.5) return;
                dragOrigin = x;

                // drag right → menu grows, drag left → menu shrinks
                //double maxMenu = Bounds.Width - SplitterThickness - MinPlotWidth;
                //double newMenu = Math.Clamp(_savedMenuWidth + delta, MinMenuWidth, maxMenu);
                double maxMenu = Math.Max(MinMenuWidth, Bounds.Width - SplitterThickness - MinPlotWidth);
                double newMenu = Math.Clamp(_savedMenuWidth + delta, MinMenuWidth, maxMenu);
                _savedMenuWidth = newMenu;
                _contentGrid.ColumnDefinitions[0].Width = new GridLength(_savedMenuWidth);
                e.Handled = true;
            };
            _menuSplitter.PointerReleased += (_, e) =>
            {
                dragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
        }

        // ── Plot size mode ────────────────────────────────────────────────────

        private void ApplyPlotSizeMode()
        {
            if (_plotSizeFixed)
            {
                Plot.Width = _fixedPlotWidth;
                Plot.Height = _fixedPlotHeight;
                Plot.HorizontalAlignment = HorizontalAlignment.Center;
                Plot.VerticalAlignment = VerticalAlignment.Center;
                _plotScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                _plotScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                Plot.Width = double.NaN;
                Plot.Height = double.NaN;
                Plot.HorizontalAlignment = HorizontalAlignment.Stretch;
                Plot.VerticalAlignment = VerticalAlignment.Stretch;
                _plotScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                _plotScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
        }

        // ── Menu panel toggle ─────────────────────────────────────────────────

        private void ToggleMenuPanel()
        {
            bool opening = !_showMenu;
            _showMenu = opening;

            if (opening)
            {
                _contentGrid.ColumnDefinitions[0].Width = new GridLength(_savedMenuWidth);
                _contentGrid.ColumnDefinitions[0].MinWidth = MinMenuWidth;
                _contentGrid.ColumnDefinitions[1].Width = new GridLength(SplitterThickness);
                _menuSidePanel.IsVisible = true;
                _menuSplitter.IsVisible = true;
                _hamburgerBtn.Background = Brushes.LightGray;

                if (WindowState != WindowState.Maximized)
                    Width += _savedMenuWidth + SplitterThickness;
            }
            else
            {
                double cur = _contentGrid.ColumnDefinitions[0].Width.Value;
                if (cur >= MinMenuWidth) _savedMenuWidth = cur;

                _contentGrid.ColumnDefinitions[0].Width = new GridLength(0);
                _contentGrid.ColumnDefinitions[0].MinWidth = 0;
                _contentGrid.ColumnDefinitions[1].Width = new GridLength(0);
                _menuSidePanel.IsVisible = false;
                _menuSplitter.IsVisible = false;
                _hamburgerBtn.Background = Brushes.Transparent;

                if (WindowState != WindowState.Maximized)
                    Width -= cur + SplitterThickness;
            }
        }

        /// <summary>
        /// Shows the Axes flyout from the menu: opens the side panel and the Property group if they
        /// are closed, so that settings are always edited in the same place.
        /// </summary>
        private void OpenAxesEditor(PlotAxisRegion region)
        {
            if (!_showMenu) ToggleMenuPanel();
            _setPropertyExpanded?.Invoke(true);
            // The panel and the group have only just become visible: anchor the flyout once they are laid out.
            Dispatcher.UIThread.Post(() => _showAxesFlyout?.Invoke(region), DispatcherPriority.Loaded);
        }

        // ── Menu side panel builder ───────────────────────────────────────────

        private Border BuildMenuSidePanel()
        {
            const double BaseFontSize = 12;
            var menuItems = new StackPanel { Spacing = 2, Margin = new Thickness(4, 6, 4, 4) };

            menuItems.Children.Add(ControlFactory.MakeMenuGroup("File", [
                ControlFactory.MakeChildMenuItem("Save as\u2026",
                    async () => { await OnSaveAsAsync(); },
                    "Save plot as PNG or SVG."),
            ], icon: MenuIcons.Folder, initiallyExpanded: false));
            menuItems.Children.Add(ControlFactory.MakeMenuGroup("Edit", [
                ControlFactory.MakeChildMenuItem("Copy as image",    async () => { await CopyImageToClipboardAsync(); },    "Copy plot as PNG image."),
                ControlFactory.MakeChildMenuItem("Copy as CSV", async () => { await CopyRowDataToClipboardAsync(); }, "Copy XY series data as CSV."),
            ], icon: MenuIcons.Edit, initiallyExpanded: false));

            // ── Data visibility controls (per-series checkboxes, rebuilt on SeriesChanged) ──
            _dataVisibilityContainer = new StackPanel { Spacing = 1 };
            menuItems.Children.Add(ControlFactory.MakeMenuGroup("Data", [
                _dataVisibilityContainer,
            ], icon: MenuIcons.Data, initiallyExpanded: false));

            // ── Plot style controls (per-series, rebuilt on SeriesChanged) ──
            _plotStyleContainer = new StackPanel { Spacing = 1 };
            _fitStyleSection    = new StackPanel { Spacing = 1, IsVisible = false };

            // ── Legend position controls ─────────────────────────────────────────
            var insetToggle = new ToggleButton { Content = "Inset", IsChecked = true, Height = 20, MinHeight = 0, FontSize = 11, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var belowToggle = new ToggleButton { Content = "Below", Height = 20, MinHeight = 0, FontSize = 11, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var noneToggle = new ToggleButton { Content = "None", Height = 20, MinHeight = 0, FontSize = 11, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            
            var legendSection = new StackPanel { Margin = new Thickness(10, 2, 0, 2), Spacing = 1 };
            var legendRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 3, 6, 3) };
            //legendRow.Children.Add(new TextBlock { Text = "Legend", FontSize = BaseFontSize, VerticalAlignment = VerticalAlignment.Center });
            legendRow.Children.Add(insetToggle);
            legendRow.Children.Add(belowToggle);
            legendRow.Children.Add(noneToggle);
            legendSection.Children.Add(ControlFactory.MakeSep());
            legendSection.Children.Add(
                new TextBlock { Text = "Legend", FontSize = BaseFontSize, VerticalAlignment = VerticalAlignment.Center });
            legendSection.Children.Add(legendRow);
            bool suppressLegend = false;
            insetToggle.IsCheckedChanged += (_, _) =>
            {
                if (suppressLegend) return;
                if (insetToggle.IsChecked == true)
                {
                    suppressLegend = true; belowToggle.IsChecked = false; noneToggle.IsChecked = false; suppressLegend = false;
                    Plot.LegendPosition = LegendPosition.InsetTopRight;
                }
                else if (belowToggle.IsChecked != true && noneToggle.IsChecked != true)
                    insetToggle.IsChecked = true;
            };
            belowToggle.IsCheckedChanged += (_, _) =>
            {
                if (suppressLegend) return;
                if (belowToggle.IsChecked == true)
                {
                    suppressLegend = true; insetToggle.IsChecked = false; noneToggle.IsChecked = false; suppressLegend = false;
                    Plot.LegendPosition = LegendPosition.BelowPlot;
                }
                else if (insetToggle.IsChecked != true && noneToggle.IsChecked != true)
                    belowToggle.IsChecked = true;
            };
            noneToggle.IsCheckedChanged += (_, _) =>
            {
                if (suppressLegend) return;
                if (noneToggle.IsChecked == true)
                {
                    suppressLegend = true; insetToggle.IsChecked = false; belowToggle.IsChecked = false; suppressLegend = false;
                    Plot.LegendPosition = LegendPosition.None;
                }
                else if (insetToggle.IsChecked != true && belowToggle.IsChecked != true)
                    noneToggle.IsChecked = true;
            };
            //var legendSection = new StackPanel { Margin = new Thickness(0, 2, 0, 2), Spacing = 1 };
            
            //legendSection.Children.Add(legendRow);

            // ── Size mode controls (built here; closures capture instance fields) ───────────────
            _sizeFitToggle = new ToggleButton { Content = "Stretch", IsChecked = !_plotSizeFixed, Height = 20, MinHeight = 0, FontSize = 11, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            _sizeFixedToggle = new ToggleButton { Content = "Fixed", IsChecked = _plotSizeFixed, Height = 20, MinHeight = 0, FontSize = 11, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center };
            var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 3, 6, 3) };
            toggleRow.Children.Add(
                new TextBlock
                {
                    Text = "Size",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = BaseFontSize,
                });
            toggleRow.Children.Add(_sizeFixedToggle);
            toggleRow.Children.Add(_sizeFitToggle);
            _plotWidthNud = ControlFactory.MakeNumericUpDown((decimal)_fixedPlotWidth, 100, 4000, 10);
            _plotHeightNud = ControlFactory.MakeNumericUpDown((decimal)_fixedPlotHeight, 80, 4000, 10);
            var fixedInputs = new Border
            {
                IsVisible = _plotSizeFixed,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(10, 2, 6, 2),
                Padding = new Thickness(6, 2),
            };
            var fixedInputsStack = new StackPanel { Spacing = 3 };
            fixedInputsStack.Children.Add(ControlFactory.MakeNudRow("Width:", _plotWidthNud, "dip"));
            fixedInputsStack.Children.Add(ControlFactory.MakeNudRow("Height:", _plotHeightNud, "dip"));
            fixedInputs.Child = fixedInputsStack;
            bool suppressSizeModeSync = false;
            _sizeFitToggle.IsCheckedChanged += (_, _) =>
            {
                if (suppressSizeModeSync) return;
                if (_sizeFitToggle.IsChecked == true)
                {
                    suppressSizeModeSync = true; _sizeFixedToggle.IsChecked = false; suppressSizeModeSync = false;
                    _plotSizeFixed = false;
                    fixedInputs.IsVisible = false;
                    ApplyPlotSizeMode();
                }
                else if (_sizeFixedToggle.IsChecked != true)
                    _sizeFitToggle.IsChecked = true; // prevent both unchecked
            };
            _sizeFixedToggle.IsCheckedChanged += (_, _) =>
            {
                if (suppressSizeModeSync) return;
                if (_sizeFixedToggle.IsChecked == true)
                {
                    suppressSizeModeSync = true; _sizeFitToggle.IsChecked = false; suppressSizeModeSync = false;
                    _plotSizeFixed = true;
                    fixedInputs.IsVisible = true;
                    ApplyPlotSizeMode();
                }
                else if (_sizeFitToggle.IsChecked != true)
                    _sizeFixedToggle.IsChecked = true; // prevent both unchecked
            };
            _plotWidthNud.ValueChanged  += (_, _) => { _fixedPlotWidth  = (double)(_plotWidthNud.Value  ?? 600m); if (_plotSizeFixed) ApplyPlotSizeMode(); };
            _plotHeightNud.ValueChanged += (_, _) => { _fixedPlotHeight = (double)(_plotHeightNud.Value ?? 400m); if (_plotSizeFixed) ApplyPlotSizeMode(); };
            var sizeModeSection = new StackPanel { Margin = new Thickness(0, 4, 0, 2), Spacing = 1 };
            sizeModeSection.Children.Add(ControlFactory.MakeSep());
            //sizeModeSection.Children.Add();
            sizeModeSection.Children.Add(toggleRow);
            sizeModeSection.Children.Add(fixedInputs);

            // ── Font / appearance controls ─────────────────────────────────────────
            var fontNud     = ControlFactory.MakeNumericUpDown((decimal)Plot.TickFontSize,   6m, 36m, 1m);
            var axisNud     = ControlFactory.MakeNumericUpDown((decimal)Plot.AxisThickness, 0.5m, 8m, 0.5m);
            fontNud.ValueChanged  += (_, _) => { if (fontNud.Value  is { } v) { Plot.TickFontSize = (double)v; Plot.LabelFontSize = (double)v; Plot.InvalidateVisual(); } };
            axisNud.ValueChanged  += (_, _) => { if (axisNud.Value  is { } v) Plot.AxisThickness = (double)v; };
            var fontRowCtrl = ControlFactory.MakeNudRow("Font size:",  fontNud,  "pt", labelWidth: 62);
            var axisRowCtrl = ControlFactory.MakeNudRow("Axis width:", axisNud,  "px", labelWidth: 62);
            fontRowCtrl.Margin = new Thickness(10, 2, 6, 1);
            axisRowCtrl.Margin = new Thickness(10, 2, 6, 2);
            var fontSection = new StackPanel { Margin = new Thickness(0, 4, 0, 2), Spacing = 1 };
            fontSection.Children.Add(ControlFactory.MakeSep());
            fontSection.Children.Add(fontRowCtrl);
            fontSection.Children.Add(axisRowCtrl);

            menuItems.Children.Add(ControlFactory.MakeMenuGroup("Style", [
                _plotStyleContainer,
                _fitStyleSection,
            ], icon: MenuIcons.Palette, initiallyExpanded: false));
            menuItems.Children.Add(ControlFactory.MakeMenuGroup("Property", [
                BuildAxesFlyoutButton(),
                legendSection,
                sizeModeSection,
                fontSection,
            ], out var setPropertyExpanded, icon: MenuIcons.Ruler, initiallyExpanded: false));
            _setPropertyExpanded = setPropertyExpanded;

            var fitCurveChk = ControlFactory.MakeCheckBox("Fit curve", hint: "Fit curve with selected function");
            fitCurveChk.Margin = new Thickness(26, 1, 6, -7);
            // The gear lives inside the checkbox content so it scales and aligns with the label
            // (see the rename button in RebuildDataVisibilityControls).
            var fitContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            fitContent.Children.Add(new TextBlock { Text = "Fit curve", VerticalAlignment = VerticalAlignment.Center });
            fitContent.Children.Add(BuildFitSettingsButton());
            fitCurveChk.Content = fitContent;
            _fitCurveToggle = fitCurveChk;

            var fitterCombo = new ComboBox
            {
                ItemsSource = ProfileFitterRegistry.Fitters,
                SelectedIndex = ProfileFitterRegistry.Fitters.Count > 0 ? 0 : -1,
                // Takes the width the row leaves, so a long fitter name shows when the menu panel is widened.
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 20,
                MinHeight = 0,
                MinWidth = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            fitterCombo.ItemTemplate = new FuncDataTemplate<IProfileFitter>((f, _) =>
                new TextBlock { Text = f?.Name ?? "", FontSize = 11 });

            // [combo][parameters button]: the button sticks to the panel's right edge and the combo fills the rest.
            var fitParamsBtn = BuildFitParametersButton();
            DockPanel.SetDock(fitParamsBtn, Dock.Right);
            var fitterRow = new DockPanel { Margin = new Thickness(26, 0, 6, 2), LastChildFill = true };
            fitterRow.Children.Add(fitParamsBtn);
            fitterRow.Children.Add(fitterCombo);
            _activeFitter = ProfileFitterRegistry.Fitters.Count > 0 ? ProfileFitterRegistry.Fitters[0] : null;
            fitterCombo.SelectionChanged += (_, _) =>
            {
                _activeFitter = fitterCombo.SelectedItem as IProfileFitter;
                if (_fitCurveToggle?.IsChecked == true)
                {
                    RunFit();
                    // The Style section is titled after the fitter, so it follows the selection.
                    BuildFitStyleSection();
                }
            };
            fitterRow.IsEnabled = (fitCurveChk.IsChecked == true);
            // Dragging a range marker refits as it moves.
            Plot.FitRangeChanged += (_, _) =>
            {
                if (fitCurveChk.IsChecked == true) RunFit();
            };
            fitCurveChk.IsCheckedChanged += (_, _) =>
            {
                fitterRow.IsEnabled = (fitCurveChk.IsChecked == true);
                Plot.ShowFitRangeMarkers = fitCurveChk.IsChecked == true;
                if (fitCurveChk.IsChecked == true)
                {
                    RunFit();
                    BuildFitStyleSection();
                }
                else
                {
                    
                    Plot.SetFitOverlay(null);
                    ClearInfo();
                    _fitStyleSection.IsVisible = false;
                    if (_showInfo) ToggleInfoPanel();
                }
            };
            // DataChanged fires on SetData / UpdatePoints / UpdatePointsAndFit — covers all data update paths
            Plot.DataChanged += (_, _) =>
            {
                if (fitCurveChk.IsChecked == true)
                    Dispatcher.UIThread.Post(RunFit);
            };
            menuItems.Children.Add(ControlFactory.MakeMenuGroup("Analyze", [
                fitCurveChk,
                fitterRow,
            ],
            icon: MenuIcons.Magnify, initiallyExpanded: false
            ));

            var crosshairChk = ControlFactory.MakeCheckBox("Crosshair", hint: "Show crosshair and value readout at pointer position");
            crosshairChk.IsChecked = Plot.ShowCrosshair;
            crosshairChk.IsCheckedChanged += (_, _) => Plot.ShowCrosshair = crosshairChk.IsChecked == true;
            crosshairChk.Margin = new Thickness(10, 1, 0, 0);
            menuItems.Children.Add(ControlFactory.MakeSep());
            menuItems.Children.Add(crosshairChk);

            var sv = new ScrollViewer
            {
                Content = menuItems,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var panel = new Border
            {
                Child = sv,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                BorderThickness = new Thickness(0, 0, 1, 0),
                IsVisible = false,
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

            return panel;
        }

        // ── Action implementations ────────────────────────────────────────────

        private void RebuildDataVisibilityControls()
        {
            _dataVisibilityContainer.Children.Clear();
            var series = Plot.Series;
            for (int i = 0; i < series.Count; i++)
            {
                int idx = i;
                var s = series[i];
                string label = s.Name.Length > 0
                    ? (s.Name.Length > 20 ? s.Name[..18] + "\u2026" : s.Name)
                    : $"Series {i + 1}";
                var chk = ControlFactory.MakeCheckBox(label, fontSize: 11);
                chk.IsChecked = Plot.GetSeriesVisible(idx);
                chk.Margin = new Thickness(10, 0, 6, -10);
                chk.IsCheckedChanged += (_, _) =>
                {
                    Plot.SetSeriesVisible(idx, chk.IsChecked == true);
                };
                // The button lives inside the checkbox content: MakeCheckBox shrinks the whole control
                // with a RenderTransform, which leaves its layout size unscaled, so a sibling element
                // would sit far from the label and off its baseline. Sizes below are therefore in
                // pre-transform units (x0.75 on screen).
                var renameBtn = new Button
                {
                    Content = new PathIcon { Data = MenuIcons.Edit, Width = 13, Height = 13 },
                    Padding = new Thickness(1),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Width = 20,
                    Height = 20,
                    MinWidth = 0,
                    MinHeight = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                ToolTip.SetTip(renameBtn, "Rename series");
                renameBtn.Click += async (_, _) => await RenameSeriesAsync(idx);
                var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(renameBtn);
                chk.Content = content;
                _dataVisibilityContainer.Children.Add(chk);
            }
        }

        private async Task RenameSeriesAsync(int index)
        {
            var series = Plot.Series;
            if ((uint)index >= (uint)series.Count) return;
            var dlg = new AxisRenameDialog(series[index].Name, title: "Rename Series");
            await dlg.ShowDialog(this);
            if (dlg.Result is { } name)
                Plot.SetSeriesName(index, name);
        }

        private void RebuildStyleControls()
        {
            _plotStyleContainer.Children.Clear();
            var series = Plot.Series;
            for (int i = 0; i < series.Count; i++)
            {
                int idx = i;
                var s = series[i];
                string label = s.Name.Length > 0
                    ? (s.Name.Length > 16 ? s.Name[..14] + "\u2026" : s.Name)
                    : $"Series {i + 1}";
                if (i > 0) _plotStyleContainer.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 3, 6, 0), Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)) });
                _plotStyleContainer.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Margin = new Thickness(10, 4, 6, 0),
                    Opacity = 0.75,
                });
                var colorSwatch = MakeColorSwatch(idx, Plot.GetSeriesColor(idx));
                var widthNud = ControlFactory.MakeNumericUpDown((decimal)s.LineWidth, 0.5m, 10m, 0.5m);
                widthNud.ValueChanged += (_, _) => { if (widthNud.Value is { } v) Plot.SetSeriesLineWidth(idx, (double)v); };
                var attrRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 2, 6, 1) };
                attrRow.Children.Add(colorSwatch);
                attrRow.Children.Add(widthNud);
                attrRow.Children.Add(new TextBlock { Text = "px", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
                _plotStyleContainer.Children.Add(attrRow);
                var styleCombo = new ComboBox
                {
                    Width = 120,
                    Height = 20,
                    MinHeight = 0,
                    FontSize = 11,
                    Padding = new Thickness(4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                styleCombo.Items.Add("Line");
                styleCombo.Items.Add("Marker");
                styleCombo.Items.Add("Marker+Line");
                styleCombo.SelectedIndex = s.Style switch
                {
                    PlotStyle.Marker => 1,
                    PlotStyle.MarkedLine => 2,
                    _ => 0,
                };
                styleCombo.SelectionChanged += (_, _) =>
                {
                    Plot.SetSeriesStyle(idx, styleCombo.SelectedIndex switch
                    {
                        1 => PlotStyle.Marker,
                        2 => PlotStyle.MarkedLine,
                        _ => PlotStyle.Line,
                    });
                };
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 1, 6, 3) };
                btnRow.Children.Add(styleCombo);
                _plotStyleContainer.Children.Add(btnRow);
            }
        }

        private void BuildFitStyleSection()
        {
            _fitStyleSection.Children.Clear();
            _fitStyleSection.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(6, 3, 6, 0),
                Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            });
            _fitStyleSection.Children.Add(new TextBlock
            {
                Text = (_activeFitter?.Name ?? "Fit") + " curve",
                FontSize = 11,
                Margin = new Thickness(10, 4, 6, 0),
                Opacity = 0.75,
            });
            var colorSwatch = MakeFitColorSwatch(Plot.FitOverlayColor);
            var widthNud = ControlFactory.MakeNumericUpDown((decimal)Plot.FitOverlayLineWidth, 0.5m, 5m, 0.5m);
            widthNud.ValueChanged += (_, _) =>
            {
                if (widthNud.Value is { } v) Plot.SetFitOverlayStyle(Plot.FitOverlayColor, (double)v);
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(10, 2, 6, 3) };
            row.Children.Add(colorSwatch);
            row.Children.Add(widthNud);
            row.Children.Add(new TextBlock { Text = "px", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            _fitStyleSection.Children.Add(row);
            _fitStyleSection.IsVisible = true;
        }

        private Button MakeFitColorSwatch(Color initialColor)
        {
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            var swatch = new Button
            {
                Width = 22, Height = 22, MinHeight = 0,
                Background = new SolidColorBrush(initialColor),
                Padding = new Thickness(0), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.Classes.Add("colorswatch");
            void Apply(Color c)
            {
                swatch.Background = new SolidColorBrush(c);
                Plot.SetFitOverlayStyle(c, Plot.FitOverlayLineWidth);
                flyout.Hide();
            }
            var grid = new UniformGrid { Columns = 8 };
            foreach (var pc in ColorPalette)
            {
                var cap = pc;
                var btn = new Button { Width = 22, Height = 22, MinHeight = 0, Background = new SolidColorBrush(pc), Padding = new Thickness(0), BorderThickness = new Thickness(0.5) };
                btn.Click += (_, _) => Apply(cap);
                grid.Children.Add(btn);
            }
            flyout.Content = new Border { Child = grid, Padding = new Thickness(1) };
            FlyoutBase.SetAttachedFlyout(swatch, flyout);
            swatch.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(swatch);
            return swatch;
        }

        /// <summary>Builds the gear button whose flyout edits <see cref="_fitSettings"/>.</summary>
        private Button BuildFitSettingsButton()
        {
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(2) };
            var flyout = new Flyout { Content = panel };
            // Rebuilt on each opening so the series list is always current.
            flyout.Opening += (_, _) => RebuildFitSettingsPanel(panel);

            var btn = new Button
            {
                Content = new PathIcon { Data = MenuIcons.Gear, Width = 13, Height = 13 },
                Padding = new Thickness(1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 20,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Flyout = flyout,
            };
            ToolTip.SetTip(btn, "Fit settings");
            return btn;
        }

        private void RebuildFitSettingsPanel(StackPanel panel)
        {
            panel.Children.Clear();
            var series = Plot.Series;

            var seriesCombo = new ComboBox
            {
                Width = 140,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
            };
            for (int i = 0; i < series.Count; i++)
                seriesCombo.Items.Add(series[i].Name.Length > 0 ? series[i].Name : $"Series {i + 1}");
            seriesCombo.SelectedIndex = series.Count == 0 ? -1 : Math.Min(_fitSettings.SeriesIndex, series.Count - 1);
            seriesCombo.SelectionChanged += (_, _) =>
            {
                if (seriesCombo.SelectedIndex < 0) return;
                _fitSettings.SeriesIndex = seriesCombo.SelectedIndex;
                if (_fitCurveToggle?.IsChecked == true) RunFit();
            };

            var autoChk = ControlFactory.MakeCheckBox("Auto", hint: "Use max(300, 2 x data points) samples");
            autoChk.IsChecked = _fitSettings.AutoCurvePoints;
            var pointsNud = ControlFactory.MakeNumericUpDown(_fitSettings.CurvePoints, 10m, 100000m, 10m);
            pointsNud.IsEnabled = !_fitSettings.AutoCurvePoints;
            autoChk.IsCheckedChanged += (_, _) =>
            {
                _fitSettings.AutoCurvePoints = autoChk.IsChecked == true;
                pointsNud.IsEnabled = !_fitSettings.AutoCurvePoints;
                if (_fitCurveToggle?.IsChecked == true) RunFit();
            };
            pointsNud.ValueChanged += (_, _) =>
            {
                if (pointsNud.Value is not { } v) return;
                _fitSettings.CurvePoints = (int)v;
                if (_fitCurveToggle?.IsChecked == true && !_fitSettings.AutoCurvePoints) RunFit();
            };

            panel.Children.Add(new TextBlock { Text = "Target series", FontSize = 11, Opacity = 0.75 });
            panel.Children.Add(seriesCombo);
            panel.Children.Add(new TextBlock { Text = "Curve points", FontSize = 11, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
            var pointsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            pointsRow.Children.Add(pointsNud);
            pointsRow.Children.Add(autoChk);
            panel.Children.Add(pointsRow);

            // ── Fit range (X). Empty = from the first / up to the last point. The same range is
            //    set by dragging the diamond markers in the plot.
            TextBox MakeRangeBox(double? value, string watermark, string tip)
            {
                var box = new TextBox
                {
                    Width = 72, Height = 20, MinHeight = 0, FontSize = 11,
                    Padding = new Thickness(4, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Watermark = watermark,
                    Text = value?.ToString("G6", CultureInfo.InvariantCulture) ?? "",
                };
                ToolTip.SetTip(box, tip);
                return box;
            }
            static bool TryParseRangeEnd(string? text, out double? value)
            {
                value = null;
                if (string.IsNullOrWhiteSpace(text)) return true;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
                value = d;
                return true;
            }
            void RefitIfOn()
            {
                if (_fitCurveToggle?.IsChecked == true) RunFit();
            }

            var startBox = MakeRangeBox(Plot.FitRangeStart, "first", "Start of the fitted X range. Empty: from the first point.");
            var endBox = MakeRangeBox(Plot.FitRangeEnd, "last", "End of the fitted X range. Empty: up to the last point.");
            void ApplyStart()
            {
                if (!TryParseRangeEnd(startBox.Text, out var v)) return;
                Plot.FitRangeStart = v;
                RefitIfOn();
            }
            void ApplyEnd()
            {
                if (!TryParseRangeEnd(endBox.Text, out var v)) return;
                Plot.FitRangeEnd = v;
                RefitIfOn();
            }
            startBox.LostFocus += (_, _) => ApplyStart();
            startBox.KeyDown += (_, e) => { if (e.Key == Key.Return) ApplyStart(); };
            endBox.LostFocus += (_, _) => ApplyEnd();
            endBox.KeyDown += (_, e) => { if (e.Key == Key.Return) ApplyEnd(); };

            var resetBtn = new Button
            {
                Content = "Whole data", FontSize = 11, Height = 20, MinHeight = 0,
                Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            };
            resetBtn.Click += (_, _) =>
            {
                Plot.FitRangeStart = null;
                Plot.FitRangeEnd = null;
                startBox.Text = "";
                endBox.Text = "";
                RefitIfOn();
            };

            panel.Children.Add(new TextBlock { Text = "Fit range (X)", FontSize = 11, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) });
            var rangeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            rangeRow.Children.Add(startBox);
            rangeRow.Children.Add(new TextBlock { Text = "–", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            rangeRow.Children.Add(endBox);
            panel.Children.Add(rangeRow);
            panel.Children.Add(resetBtn);
        }

        /// <summary>
        /// Builds the wrench button next to the fitter combo box. Its flyout lists the parameters of the
        /// selected fitter, each of which can be held at a fixed value.
        /// </summary>
        private Button BuildFitParametersButton()
        {
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(2) };
            var flyout = new Flyout { Content = panel };
            // Rebuilt on each opening: the parameters belong to whichever fitter is selected then.
            flyout.Opening += (_, _) => RebuildFitParametersPanel(panel);
            flyout.Closed += (_, _) => _refreshFitParameterValues = null;

            var btn = new Button
            {
                Content = new PathIcon { Data = MenuIcons.Wrench, Width = 13, Height = 13 },
                Padding = new Thickness(1),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = 20,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Flyout = flyout,
            };
            ToolTip.SetTip(btn, "Fit parameters");
            return btn;
        }

        /// <summary>
        /// One row per parameter of the selected fitter: a check box that holds the parameter at the value in
        /// the box beside it. A free parameter's box shows the value of the latest fit, so ticking it fixes
        /// the parameter where it is now. At least one parameter stays free.
        /// </summary>
        private void RebuildFitParametersPanel(StackPanel panel)
        {
            panel.Children.Clear();
            _refreshFitParameterValues = null;
            var fitter = _activeFitter;
            if (fitter == null) return;

            var fixedValues = _fitSettings.FixedFor(fitter);
            var names = fitter.ParameterNames;
            int count = fitter.ParameterCount;
            var checks = new CheckBox[count];
            var boxes = new TextBox[count];
            bool updating = false;

            static string Format(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
            void RefitIfOn()
            {
                if (_fitCurveToggle?.IsChecked == true) RunFit();
            }
            double? FittedValue(int i) =>
                _lastFit is { } f && ReferenceEquals(f.Fitter, fitter) && i < f.Parameters.Length ? f.Parameters[i] : null;

            // A fixed parameter shows its fixed value; a free one the value of the latest fit.
            void ShowValues()
            {
                updating = true;
                for (int i = 0; i < count; i++)
                {
                    bool isFixed = fixedValues[i] != null;
                    checks[i].IsChecked = isFixed;
                    boxes[i].IsEnabled = isFixed;
                    boxes[i].Text = fixedValues[i] is { } v ? Format(v)
                        : FittedValue(i) is { } fv ? Format(fv) : "";
                }
                // The last free parameter cannot be fixed.
                int fixedCount = fixedValues.Count(v => v != null);
                for (int i = 0; i < count; i++)
                    checks[i].IsEnabled = fixedValues[i] != null || fixedCount < count - 1;
                updating = false;
            }

            for (int i = 0; i < count; i++)
            {
                int index = i;
                var check = ControlFactory.MakeCheckBox(names[i], hint: $"Hold {names[i]} at the value on the right");
                check.Width = 64;
                check.VerticalAlignment = VerticalAlignment.Center;
                var box = new TextBox
                {
                    Width = 84, Height = 20, MinHeight = 0, FontSize = 11,
                    Padding = new Thickness(4, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                checks[i] = check;
                boxes[i] = box;

                check.IsCheckedChanged += (_, _) =>
                {
                    if (updating) return;
                    if (check.IsChecked == true)
                    {
                        // Fix at the value shown now: the latest fitted value, or one that was typed.
                        fixedValues[index] = double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                            ? v : 0.0;
                    }
                    else
                    {
                        fixedValues[index] = null;
                    }
                    ShowValues();
                    RefitIfOn();
                };

                void Commit()
                {
                    if (updating || fixedValues[index] == null) return;
                    if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    {
                        fixedValues[index] = v;
                        RefitIfOn();
                    }
                    else
                    {
                        box.Text = Format(fixedValues[index]!.Value);   // not a number: keep the old value
                    }
                }
                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, e) => { if (e.Key == Key.Return) Commit(); };

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                row.Children.Add(check);
                row.Children.Add(box);
                panel.Children.Add(row);
            }

            var freeAllBtn = new Button
            {
                Content = "All free", FontSize = 11, Height = 20, MinHeight = 0,
                Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            };
            freeAllBtn.Click += (_, _) =>
            {
                for (int i = 0; i < count; i++) fixedValues[i] = null;
                ShowValues();
                RefitIfOn();
            };

            panel.Children.Insert(0, new TextBlock
            {
                Text = $"{fitter.Name} parameters", FontSize = 11, Opacity = 0.75,
            });
            panel.Children.Add(freeAllBtn);

            ShowValues();
            _refreshFitParameterValues = ShowValues;
        }

        private void RunFit()
        {
            if (_activeFitter == null) return;

            var series = Plot.Series;
            int seriesIndex = Math.Min(_fitSettings.SeriesIndex, series.Count - 1);
            var fixedValues = _fitSettings.FixedFor(_activeFitter);
            int minPoints = LevenbergMarquardtSolver.MinimumPoints(_activeFitter, fixedValues);
            // The range markers sit on the fitted series and leave it as many points as the fit needs.
            Plot.FitSeriesIndex = Math.Max(seriesIndex, 0);
            Plot.FitMinimumPoints = minPoints;
            if (seriesIndex < 0)
            {
                _lastFit = null;
                Plot.SetFitOverlay(null);
                return;
            }

            // Only the points inside the fit range are fitted; an end left unset follows the data.
            IReadOnlyList<(double X, double Y)> pts = series[seriesIndex].Points;
            bool ranged = Plot.FitRangeStart != null || Plot.FitRangeEnd != null;
            if (ranged)
            {
                double lo = Plot.FitRangeStart ?? double.NegativeInfinity;
                double hi = Plot.FitRangeEnd ?? double.PositiveInfinity;
                pts = pts.Where(p => p.X >= lo && p.X <= hi).ToList();
            }

            if (pts.Count < minPoints)
            {
                _lastFit = null;
                Plot.SetFitOverlay(null);
                if (ranged)
                {
                    InfoText = $"The fit range holds {pts.Count} point(s); {_activeFitter.Name} needs at least {minPoints}.";
                    if (!_showInfo) ToggleInfoPanel();
                }
                return;
            }

            var result = LevenbergMarquardtSolver.Fit(_activeFitter, pts, fixedValues);

            if (result.Error != null)
            {
                _lastFit = null;
                Plot.SetFitOverlay(null);
                InfoText = $"{_activeFitter.Name} fit failed:\n{result.Error}";
                if (!_showInfo) ToggleInfoPanel();
                return;
            }

            _lastFit = result;
            _refreshFitParameterValues?.Invoke();

            double xMin = pts.Min(p => p.X), xMax = pts.Max(p => p.X);
            int curveN = _fitSettings.AutoCurvePoints ? Math.Max(300, pts.Count * 2) : _fitSettings.CurvePoints;
            Plot.SetFitOverlay(result.GenerateCurve(xMin, xMax, curveN), $"{_activeFitter.Name} fit");

            //string unit = Plot.XLabel ?? "";
            string unit = ""; //Currently, we don't have a way to get the unit from the plot, so we'll leave it empty for now.
            InfoText = result.FormatInfo(unit);
            if (!_showInfo) ToggleInfoPanel();
        }

        private async Task OnSaveAsAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is not { } sp) return;

            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save Plot",
                SuggestedFileName = string.IsNullOrWhiteSpace(Plot.PlotTitle) ? "plot" : Plot.PlotTitle,
                FileTypeChoices =
                [
                    new FilePickerFileType("PNG Image")  { Patterns = ["*.png"] },
                    new FilePickerFileType("SVG Image")  { Patterns = ["*.svg"] },
                    new FilePickerFileType("CSV Data")   { Patterns = ["*.csv"] },
                ],
            });
            if (file == null) return;

            bool isSvg = file.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
            bool isCsv = file.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            try
            {
                if (isCsv)
                {
                    string? csv = BuildRowDataCsv("R");
                    if (csv == null) return;
                    await using var csvStream = await file.OpenWriteAsync();
                    await using var writer = new StreamWriter(csvStream, new UTF8Encoding(false));
                    await writer.WriteAsync(csv);
                    return;
                }

                var logical = Plot.Bounds.Size;
                if (logical.Width <= 0 || logical.Height <= 0) return;

                await using var stream = await file.OpenWriteAsync();
                if (isSvg)
                {
                    Plot.RenderToSvg(stream, logical.Width, logical.Height);
                }
                else
                {
                    const double ExportScale = 2.0;
                    bool prevCrosshair = Plot.ShowCrosshair;
                    bool prevMarkers = Plot.ShowFitRangeMarkers;
                    Plot.ShowCrosshair = false;
                    Plot.ShowFitRangeMarkers = false;
                    try
                    {
                        var pixelSize = new PixelSize(
                            (int)Math.Ceiling(logical.Width  * ExportScale),
                            (int)Math.Ceiling(logical.Height * ExportScale));
                        var bmp = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                        using (var ctx = bmp.CreateDrawingContext())
                        {
                            RenderOptions.SetBitmapInterpolationMode(Plot, BitmapInterpolationMode.HighQuality);
                            RenderOptions.SetTextRenderingMode(Plot, TextRenderingMode.Antialias);
                            RenderOptions.SetEdgeMode(Plot, EdgeMode.Antialias);
                            ctx.FillRectangle(Brushes.White, new Rect(logical));
                            using (ctx.PushTransform(Matrix.CreateScale(ExportScale, ExportScale)))
                                Plot.Render(ctx);
                        }
                        bmp.Save(stream);
                    }
                    finally { Plot.ShowCrosshair = prevCrosshair; Plot.ShowFitRangeMarkers = prevMarkers; }
                }
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        // ── Axes flyout ────────────────────────────────────────────────────────────────
        private Button BuildAxesFlyoutButton()
        {
            const double FS     = 11;
            const double LabelW = 48;
            const double BoxW   = 130;
            const double NumW   = 96;

            static TextBox MakeBox(double width) => new TextBox
            {
                Width = width, Height = 20, MinHeight = 0,
                FontSize = FS, Padding = new Thickness(4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            static StackPanel MakeRow(string lbl, Control ctrl) => new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                Children =
                {
                    new TextBlock { Text = lbl, Width = LabelW, FontSize = FS, VerticalAlignment = VerticalAlignment.Center },
                    ctrl,
                },
            };
            static ToggleButton MakeToggle(string text) => new ToggleButton
            {
                Content = text, Height = 20, MinHeight = 0, FontSize = FS,
                Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center,
            };
            static Border MakeRangeInputs(TextBox minBox, TextBox maxBox) => new Border
            {
                IsVisible = false,
                BorderBrush = new SolidColorBrush(Color.FromArgb(80, 128, 128, 128)),
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(8, 2, 0, 0), Padding = new Thickness(6, 2),
                Child = new StackPanel { Spacing = 3, Children = { MakeRow("Min:", minBox), MakeRow("Max:", maxBox) } },
            };

            // ── Controls
            var titleBox  = MakeBox(BoxW);
            var xLabelBox = MakeBox(BoxW);  var xFixedBtn = MakeToggle("Fixed");  var xAutoBtn = MakeToggle("Auto");
            var xMinBox   = MakeBox(NumW);  var xMaxBox   = MakeBox(NumW);
            var yLabelBox = MakeBox(BoxW);  var yFixedBtn = MakeToggle("Fixed");  var yAutoBtn = MakeToggle("Auto");
            var yMinBox   = MakeBox(NumW);  var yMaxBox   = MakeBox(NumW);
            var xRangeInputs = MakeRangeInputs(xMinBox, xMaxBox);
            var yRangeInputs = MakeRangeInputs(yMinBox, yMaxBox);

            // ── Layout
            static TextBlock MakeSectionLabel(string t) =>
                new TextBlock { Text = t, FontSize = FS, FontWeight = FontWeight.SemiBold, Opacity = 0.65 };
            static StackPanel MakeToggleRow(ToggleButton f, ToggleButton a) =>
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { f, a } };

            var flyoutPanel = new StackPanel { Spacing = 5, Margin = new Thickness(10) };
            flyoutPanel.Children.Add(MakeRow("Title:", titleBox));
            flyoutPanel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));
            flyoutPanel.Children.Add(MakeSectionLabel("X axis"));
            flyoutPanel.Children.Add(MakeRow("Label:", xLabelBox));
            flyoutPanel.Children.Add(MakeToggleRow(xFixedBtn, xAutoBtn));
            flyoutPanel.Children.Add(xRangeInputs);
            flyoutPanel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));
            flyoutPanel.Children.Add(MakeSectionLabel("Y axis"));
            flyoutPanel.Children.Add(MakeRow("Label:", yLabelBox));
            flyoutPanel.Children.Add(MakeToggleRow(yFixedBtn, yAutoBtn));
            flyoutPanel.Children.Add(yRangeInputs);

            // ── Wiring
            bool suppress = false;
            void WireTextBox(TextBox tb, Action<string> apply)
            {
                tb.LostFocus += (_, _) => apply(tb.Text ?? "");
                tb.KeyDown   += (_, e) => { if (e.Key == Key.Return) apply(tb.Text ?? ""); };
            }
            void WireDoubleBox(TextBox tb, Action<double> apply)
            {
                void Try() { if (double.TryParse(tb.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) apply(v); }
                tb.LostFocus += (_, _) => Try();
                tb.KeyDown   += (_, e) => { if (e.Key == Key.Return) Try(); };
            }
            void WireAxisToggles(
                ToggleButton fixedBtn, ToggleButton autoBtn, Border rangePanel,
                TextBox minBox, TextBox maxBox,
                Action<bool> setFixed, Func<double> getMin, Func<double> getMax)
            {
                fixedBtn.IsCheckedChanged += (_, _) =>
                {
                    if (suppress) return;
                    if (fixedBtn.IsChecked == true)
                    {
                        suppress = true; autoBtn.IsChecked = false; suppress = false;
                        setFixed(true);
                        minBox.Text = getMin().ToString("G6", CultureInfo.InvariantCulture);
                        maxBox.Text = getMax().ToString("G6", CultureInfo.InvariantCulture);
                        rangePanel.IsVisible = true;
                    }
                    else if (autoBtn.IsChecked != true) fixedBtn.IsChecked = true;
                };
                autoBtn.IsCheckedChanged += (_, _) =>
                {
                    if (suppress) return;
                    if (autoBtn.IsChecked == true)
                    {
                        suppress = true; fixedBtn.IsChecked = false; suppress = false;
                        setFixed(false);
                        rangePanel.IsVisible = false;
                    }
                    else if (fixedBtn.IsChecked != true) autoBtn.IsChecked = true;
                };
            }

            WireTextBox(titleBox,  s => Plot.PlotTitle = s);
            WireTextBox(xLabelBox, s => Plot.XLabel    = s);
            WireTextBox(yLabelBox, s => Plot.YLabel    = s);
            WireDoubleBox(xMinBox, v => Plot.XFixedMin = v);
            WireDoubleBox(xMaxBox, v => Plot.XFixedMax = v);
            WireDoubleBox(yMinBox, v => Plot.YFixedMin = v);
            WireDoubleBox(yMaxBox, v => Plot.YFixedMax = v);
            WireAxisToggles(xFixedBtn, xAutoBtn, xRangeInputs, xMinBox, xMaxBox,
                v => Plot.XAxisFixed = v, () => Plot.XFixedMin, () => Plot.XFixedMax);
            WireAxisToggles(yFixedBtn, yAutoBtn, yRangeInputs, yMinBox, yMaxBox,
                v => Plot.YAxisFixed = v, () => Plot.YFixedMin, () => Plot.YFixedMax);

            // ── Flyout
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            flyout.Content = flyoutPanel;
            flyout.Opening += (_, _) =>
            {
                suppress = true;
                titleBox.Text  = Plot.PlotTitle;
                xLabelBox.Text = Plot.XLabel;
                yLabelBox.Text = Plot.YLabel;
                bool xf = Plot.XAxisFixed, yf = Plot.YAxisFixed;
                xFixedBtn.IsChecked = xf;  xAutoBtn.IsChecked = !xf;
                yFixedBtn.IsChecked = yf;  yAutoBtn.IsChecked = !yf;
                if (xf) { xMinBox.Text = Plot.XFixedMin.ToString("G6", CultureInfo.InvariantCulture); xMaxBox.Text = Plot.XFixedMax.ToString("G6", CultureInfo.InvariantCulture); }
                if (yf) { yMinBox.Text = Plot.YFixedMin.ToString("G6", CultureInfo.InvariantCulture); yMaxBox.Text = Plot.YFixedMax.ToString("G6", CultureInfo.InvariantCulture); }
                xRangeInputs.IsVisible = xf;  yRangeInputs.IsVisible = yf;
                suppress = false;
            };

            var axesBtn = ControlFactory.MakeChildMenuItem("Axes\u2026", () => { }, "Edit title, axis labels and ranges.");
            FlyoutBase.SetAttachedFlyout(axesBtn, flyout);
            axesBtn.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(axesBtn);

            // The part of the plot that was double-clicked decides which field takes the focus.
            _showAxesFlyout = region =>
            {
                FlyoutBase.ShowAttachedFlyout(axesBtn);
                TextBox box = region switch
                {
                    PlotAxisRegion.XAxis => xLabelBox,
                    PlotAxisRegion.YAxis => yLabelBox,
                    _ => titleBox,
                };
                // The flyout fills its boxes while opening; focus them afterwards.
                Dispatcher.UIThread.Post(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Background);
            };
            return axesBtn;
        }

        private Button MakeColorSwatch(int seriesIndex, Color initialColor)
        {
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                MinHeight = 0,
                Background = new SolidColorBrush(initialColor),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.Classes.Add("colorswatch");

            // ── Status bar ────────────────────────────────────────────────────
            var statusBar = new TextBlock
            {
                Text = "\u2014",
                FontSize = 10,
                Opacity = 0.65,
                Margin = new Thickness(4, 1, 4, 1),
            };

            var flyout = new Flyout {
                Placement = PlacementMode.BottomEdgeAlignedLeft 
            };

            void ApplyColor(Color c)
            {
                swatch.Background = new SolidColorBrush(c);
                flyout.Hide();
                Plot.SetSeriesColor(seriesIndex, c);
            }

            // ── Palette grid ──────────────────────────────────────────────────
            var paletteGrid = new UniformGrid { Columns = 8 };
            foreach (var pc in ColorPalette)
            {
                var captured = pc;
                var btn = new Button
                {
                    Width = 22,
                    Height = 22,
                    MinHeight = 0,
                    Background = new SolidColorBrush(pc),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0.5),
                };
                btn.Classes.Add("colorswatch");
                btn.Click += (_, _) => ApplyColor(captured);
                btn.PointerEntered += (_, _) => statusBar.Text = $"R={captured.R}  G={captured.G}  B={captured.B}";
                btn.PointerExited += (_, _) => statusBar.Text = "\u2014";
                paletteGrid.Children.Add(btn);
            }

            // ── Custom RGB picker ─────────────────────────────────────────────
            Color customColor = initialColor;

            var rSlider = new Slider { Minimum = 0, Maximum = 255, Value = customColor.R, Width = 100 };
            var gSlider = new Slider { Minimum = 0, Maximum = 255, Value = customColor.G, Width = 100 };
            var bSlider = new Slider { Minimum = 0, Maximum = 255, Value = customColor.B, Width = 100 };
            var rNud = ControlFactory.MakeNumericUpDown(customColor.R, 0, 255, 1); rNud.Width = 58;
            var gNud = ControlFactory.MakeNumericUpDown(customColor.G, 0, 255, 1); gNud.Width = 58;
            var bNud = ControlFactory.MakeNumericUpDown(customColor.B, 0, 255, 1); bNud.Width = 58;

            var rgbPreview = new Border
            {
                Width = 32,
                Height = 20,
                Background = new SolidColorBrush(customColor),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(2),
            };

            bool syncing = false;
            void UpdateRgbPreview()
            {
                var c = Color.FromRgb((byte)Math.Round(rSlider.Value),
                                      (byte)Math.Round(gSlider.Value),
                                      (byte)Math.Round(bSlider.Value));
                rgbPreview.Background = new SolidColorBrush(c);
                statusBar.Text = $"R={(byte)Math.Round(rSlider.Value)}  G={(byte)Math.Round(gSlider.Value)}  B={(byte)Math.Round(bSlider.Value)}";
            }
            void SyncFromSliders()
            {
                if (syncing) return;
                syncing = true;
                rNud.Value = (decimal)Math.Round(rSlider.Value);
                gNud.Value = (decimal)Math.Round(gSlider.Value);
                bNud.Value = (decimal)Math.Round(bSlider.Value);
                syncing = false;
                UpdateRgbPreview();
            }
            void SyncFromNuds()
            {
                if (syncing) return;
                syncing = true;
                rSlider.Value = (double)(rNud.Value ?? 0);
                gSlider.Value = (double)(gNud.Value ?? 0);
                bSlider.Value = (double)(bNud.Value ?? 0);
                syncing = false;
                UpdateRgbPreview();
            }

            rSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncFromSliders(); };
            gSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncFromSliders(); };
            bSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncFromSliders(); };
            rNud.ValueChanged += (_, _) => SyncFromNuds();
            gNud.ValueChanged += (_, _) => SyncFromNuds();
            bNud.ValueChanged += (_, _) => SyncFromNuds();

            var applyBtn = new Button
            {
                Content = "Apply",
                FontSize = 11,
                Height = 20,
                MinHeight = 0,
                Padding = new Thickness(10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            applyBtn.Click += (_, _) =>
            {
                customColor = Color.FromRgb((byte)Math.Round(rSlider.Value),
                                            (byte)Math.Round(gSlider.Value),
                                            (byte)Math.Round(bSlider.Value));
                ApplyColor(customColor);
            };

            var rgbPickerPanel = new StackPanel { Spacing = 1, Margin = new Thickness(3) };
            rgbPickerPanel.Children.Add(ControlFactory.MakeSliderRow("R", rSlider, rNud));
            rgbPickerPanel.Children.Add(ControlFactory.MakeSliderRow("G", gSlider, gNud));
            rgbPickerPanel.Children.Add(ControlFactory.MakeSliderRow("B", bSlider, bNud));
            var rgbBottomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
            rgbBottomRow.Children.Add(rgbPreview);
            rgbBottomRow.Children.Add(applyBtn);
            rgbPickerPanel.Children.Add(rgbBottomRow);

            var rgbFlyout = new Flyout { Placement = PlacementMode.RightEdgeAlignedTop };
            rgbFlyout.Content = rgbPickerPanel;

            // ── Custom row ────────────────────────────────────────────────────
            var customPickerBtn = new Button
            {
                Width = 22,
                Height = 22,
                MinHeight = 0,
                Background = new SolidColorBrush(customColor),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            FlyoutBase.SetAttachedFlyout(customPickerBtn, rgbFlyout);
            customPickerBtn.PointerEntered += (_, _) => statusBar.Text = $"R={customColor.R}  G={customColor.G}  B={customColor.B}";
            customPickerBtn.PointerExited += (_, _) => statusBar.Text = "\u2014";
            customPickerBtn.Click += (_, _) =>
            {
                syncing = true;
                rSlider.Value = customColor.R; gSlider.Value = customColor.G; bSlider.Value = customColor.B;
                rNud.Value = customColor.R; gNud.Value = customColor.G; bNud.Value = customColor.B;
                syncing = false;
                UpdateRgbPreview();
                FlyoutBase.ShowAttachedFlyout(customPickerBtn);
            };

            // After Apply, also update customPickerBtn's background
            applyBtn.Click += (_, _) => customPickerBtn.Background = new SolidColorBrush(customColor);

            var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(4, 2, 4, 1) };
            customRow.Children.Add(new TextBlock { Text = "Custom", FontSize = 11, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center });
            customRow.Children.Add(customPickerBtn);

            // ── Assemble flyout ───────────────────────────────────────────────
            var flyoutPanel = new StackPanel { Spacing = 0 };
            flyoutPanel.Children.Add(new Border { Child = paletteGrid, Padding = new Thickness(1) });
            flyoutPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(2, 1, 2, 0), Background = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)) });
            flyoutPanel.Children.Add(customRow);
            flyoutPanel.Children.Add(new Border { Height = 1, Margin = new Thickness(2, 1, 2, 0), Background = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)) });
            flyoutPanel.Children.Add(statusBar);

            flyout.Content = flyoutPanel;
            FlyoutBase.SetAttachedFlyout(swatch, flyout);
            swatch.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(swatch);
            return swatch;
        }

        private void ShowToast(string title, string message)
        {
            _notificationManager?.Show(new Notification(
                title,
                message,
                NotificationType.Success, 
                TimeSpan.FromSeconds(2.5) 
            ));
        }

        private async Task CopyImageToClipboardAsync()
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is not { } clipboard) return;

            double scaling = topLevel.RenderScaling;
            var logicalSize = Plot.Bounds.Size;
            var pixelSize = new PixelSize(
                (int)Math.Ceiling(logicalSize.Width * scaling),
                (int)Math.Ceiling(logicalSize.Height * scaling));

            bool prevCrosshair = Plot.ShowCrosshair;
            bool prevMarkers = Plot.ShowFitRangeMarkers;
            Plot.ShowCrosshair = false;
            Plot.ShowFitRangeMarkers = false;
            var bmp = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
            try
            {
                RenderOptions.SetBitmapInterpolationMode(Plot, BitmapInterpolationMode.HighQuality);
                RenderOptions.SetTextRenderingMode(Plot, TextRenderingMode.Antialias);
                RenderOptions.SetEdgeMode(Plot, EdgeMode.Antialias);
                using (var ctx = bmp.CreateDrawingContext())
                {
                    ctx.FillRectangle(Brushes.White, new Rect(logicalSize));
                    using (ctx.PushTransform(Matrix.CreateScale(scaling, scaling)))
                        Plot.Render(ctx);
                }
                await clipboard.SetBitmapAsync(bmp);

                ShowToast("Copied", "Image copied to clipboard.");
            }
            catch (Exception ex) { bmp.Dispose(); Debug.WriteLine(ex); }
            finally { Plot.ShowCrosshair = prevCrosshair; Plot.ShowFitRangeMarkers = prevMarkers; }
        }

        /// <summary>
        /// Quotes a CSV field when it contains a comma, a double quote or a line break;
        /// embedded double quotes are doubled.
        /// </summary>
        private static string CsvField(string value)
            => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;

        /// <summary>
        /// Builds the row-oriented CSV of all series (and the fit overlay): one X,Y column pair per
        /// series. Returns null when there is nothing to export.
        /// </summary>
        /// <param name="numberFormat">Format string for the numeric cells ("R" keeps every digit).</param>
        private string? BuildRowDataCsv(string numberFormat)
        {
            var series    = Plot.Series;
            var fitPts    = Plot.FitOverlayPoints;
            string fitLabel = Plot.FitOverlayName;
            bool includeFit = fitPts is { Count: > 0 } && fitLabel.Length > 0;
            if (series.Count == 0 && !includeFit) return null;

            string xl = CsvField(Plot.XLabel);
            var sb = new StringBuilder();

            // Header: xLabel,seriesName, ...  [, xLabel, fitLabel]
            for (int i = 0; i < series.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(xl).Append(',').Append(CsvField(series[i].Name));
            }
            if (includeFit)
            {
                if (series.Count > 0) sb.Append(',');
                sb.Append(xl).Append(',').Append(CsvField(fitLabel));
            }
            sb.AppendLine();

            // Rows: x0,y0, ...
            int maxLen = series.Count > 0 ? series.Max(s => s.Points.Count) : 0;
            if (includeFit) maxLen = Math.Max(maxLen, fitPts!.Count);
            for (int row = 0; row < maxLen; row++)
            {
                for (int i = 0; i < series.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var pts = series[i].Points;
                    if (row < pts.Count)
                    {
                        sb.Append(pts[row].X.ToString(numberFormat, CultureInfo.InvariantCulture));
                        sb.Append(',');
                        sb.Append(pts[row].Y.ToString(numberFormat, CultureInfo.InvariantCulture));
                    }
                    else sb.Append(',');
                }
                if (includeFit)
                {
                    if (series.Count > 0) sb.Append(',');
                    if (row < fitPts!.Count)
                    {
                        sb.Append(fitPts[row].X.ToString(numberFormat, CultureInfo.InvariantCulture));
                        sb.Append(',');
                        sb.Append(fitPts[row].Y.ToString(numberFormat, CultureInfo.InvariantCulture));
                    }
                    else sb.Append(',');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private async Task CopyRowDataToClipboardAsync()
        {
            string? csv = BuildRowDataCsv("G6");
            if (csv == null) return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            try
            {
                await clipboard.SetTextAsync(csv);
                ShowToast("Copied", "Row data copied to clipboard.");
            }
            catch { }
        }

        // ── Plot right-click context menu ─────────────────────────────────────

        private void AttachContextMenu()
        {
            const double FS = 11;

            var copyImageItem = new MenuItem { Header = "Image", FontSize = FS };
            copyImageItem.Click += async (_, _) => await CopyImageToClipboardAsync();

            var copyCsvItem = new MenuItem { Header = "CSV", FontSize = FS };
            copyCsvItem.Click += async (_, _) => await CopyRowDataToClipboardAsync();

            var copyItem = new MenuItem { Header = "Copy", FontSize = FS };
            copyItem.Items.Add(copyImageItem);
            copyItem.Items.Add(copyCsvItem);

            var yFixedIcon = new TextBlock { Text = "✓", FontSize = FS };
            var yAutoIcon  = new TextBlock { Text = "✓", FontSize = FS };
            var xFixedIcon = new TextBlock { Text = "✓", FontSize = FS };
            var xAutoIcon  = new TextBlock { Text = "✓", FontSize = FS };

            var yFixedItem = new MenuItem { Header = "Fixed", FontSize = FS, Icon = yFixedIcon };
            yFixedItem.Click += (_, _) => Plot.YAxisFixed = true;

            var yAutoItem = new MenuItem { Header = "Auto", FontSize = FS, Icon = yAutoIcon };
            yAutoItem.Click += (_, _) => Plot.YAxisFixed = false;

            var yAxisItem = new MenuItem { Header = "Y axis range", FontSize = FS };
            yAxisItem.Items.Add(yFixedItem);
            yAxisItem.Items.Add(yAutoItem);

            var xFixedItem = new MenuItem { Header = "Fixed", FontSize = FS, Icon = xFixedIcon };
            xFixedItem.Click += (_, _) => Plot.XAxisFixed = true;

            var xAutoItem = new MenuItem { Header = "Auto", FontSize = FS, Icon = xAutoIcon };
            xAutoItem.Click += (_, _) => Plot.XAxisFixed = false;

            var xAxisItem = new MenuItem { Header = "X axis range", FontSize = FS };
            xAxisItem.Items.Add(xFixedItem);
            xAxisItem.Items.Add(xAutoItem);

            var menu = new ContextMenu { FontSize = FS };
            menu.Items.Add(copyItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(yAxisItem);
            menu.Items.Add(xAxisItem);

            menu.Opening += (_, _) =>
            {
                yFixedIcon.IsVisible = Plot.YAxisFixed;
                yAutoIcon.IsVisible  = !Plot.YAxisFixed;
                xFixedIcon.IsVisible = Plot.XAxisFixed;
                xAutoIcon.IsVisible  = !Plot.XAxisFixed;
            };

            Plot.ContextMenu = menu;
        }

        // ── Static factory helpers ────────────────────────────────────────────

        private static List<PlotSeries> BuildSeries(
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> pointSets,
            IReadOnlyList<string>? names,
            PlotStyle style)
        {
            var list = new List<PlotSeries>(pointSets.Count);
            for (int i = 0; i < pointSets.Count; i++)
            {
                string n = names != null && i < names.Count ? names[i] : "";
                list.Add(new PlotSeries(pointSets[i], n, style));
            }
            return list;
        }
    }
}
