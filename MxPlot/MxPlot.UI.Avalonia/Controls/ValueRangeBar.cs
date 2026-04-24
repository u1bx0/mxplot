using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>Value range display and control mode.</summary>
    public enum ValueRangeMode
    {
        /// <summary>
        /// Fixed: user-specified numeric min/max.  TextBoxes are editable, ↑,↓ buttons enabled.
        /// </summary>
        Fixed,
        /// <summary>
        /// Current: automatically adjusted to the current frame.  TextBoxes are read-only, ↑,↓ buttons disabled.
        /// Note: in single-frame data, this is effectively the same as Auto mode, but in multi-frame data it reflects the current frame's range.
        /// </summary>
        Current,
        /// <summary>
        /// All: Find the value range from the entire frames.  TextBoxes are read-only, ↑,↓ buttons disabled.
        /// Note: If virtual frames are used, this may be an imperfect estimate until all frames are scanned.  The UI reflects this with an asterisk and tooltip.
        /// </summary>
        All,
        /// <summary>
        /// ROI: value range is computed from the pixels inside a designated overlay region.
        /// TextBoxes are read-only, ↑,↓ buttons disabled.
        /// Only available when an ROI overlay has been designated via the overlay context menu.
        /// </summary>
        Roi
    }

    /// <summary>
    /// <para>Compact top-bar control for value range configuration.</para>
    /// <para>Single-frame layout: [Fixed/Auto] [Min] [↓] [Max] [↑]</para>
    /// <para>Multi-frame layout:  [Fixed/Current/All] [Min] [↓] [Max] [↑]</para>
    /// </summary>
    public class ValueRangeBar : UserControl
    {
        // ── Dimensions ────────────────────────────────────────────────────────
        private const double BoxWidth = 66;
        private const double MinBoxWidth = 36;  // minimum TextBox width before overflow
        private const double BtnSize = 20;      // uniform height for all buttons
        private const double ItemH = 20;        // textbox height (MinHeight=0 required)

        // ── Controls ──────────────────────────────────────────────────────────
        private readonly Button _modeBtn;     // cycles Fixed → Auto/Current → All on click
        private readonly TextBox _minBox;
        private readonly TextBox _maxBox;
        private readonly Button _searchMinBtn;
        private readonly Button _searchMaxBtn;

        // ── State ─────────────────────────────────────────────────────────────
        private ValueRangeMode _mode;       // current display mode
        private bool _isMultiFrame;         // true when FrameCount > 1
        private bool _isImperfect;          // true when All range is only partially scanned
        private bool _roiAvailable;         // true when an ROI overlay is designated
        private bool _updating;
        private double _lastMin = double.NaN;
        private double _lastMax = double.NaN;

        // ── Events ────────────────────────────────────────────────────────────

        /// <summary>Fired when the user switches mode.</summary>
        public event EventHandler<ValueRangeMode>? ModeChanged;

        /// <summary>Fired when the user commits a new min/max pair.</summary>
        public event EventHandler<(double Min, double Max)>? RangeChanged;

        /// <summary>Fired when the user clicks the 🔍 button next to Min.</summary>
        public event EventHandler? SearchMinRequested;

        /// <summary>Fired when the user clicks the 🔍 button next to Max.</summary>
        public event EventHandler? SearchMaxRequested;

        // ── Public properties ─────────────────────────────────────────────────

        /// <summary>True when <see cref="Mode"/> is <see cref="ValueRangeMode.Fixed"/>.</summary>
        public bool IsFixedRange => _mode == ValueRangeMode.Fixed;
        /// <summary>The current display/control mode.</summary>
        public ValueRangeMode Mode => _mode;

        /// <summary>The min value currently displayed in the bar (valid for all modes).</summary>
        public double DisplayedMinValue => _lastMin;
        /// <summary>The max value currently displayed in the bar (valid for all modes).</summary>
        public double DisplayedMaxValue => _lastMax;

        // ── Constructor ───────────────────────────────────────────────────────

        public ValueRangeBar()
        {
            _modeBtn = new Button
            {
                Content = "Auto",
                Height = BtnSize,
                FontSize = 10,
                Padding = new Thickness(4, 0),
                MinWidth = 34,
                Width = 50,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(_modeBtn, "Automatic min/max from current frame");

            _minBox = MakeTextBox();
            _maxBox = MakeTextBox();
            _minBox.Classes.Add("grayed"); // Auto (readonly) by default
            _maxBox.Classes.Add("grayed");

            _searchMinBtn = MakeSearchBtn(isMin: true,  "Find min value in current frame");
            _searchMaxBtn = MakeSearchBtn(isMin: false, "Find max value in current frame");
            
            // ── Wire events ───────────────────────────────────────────────────
            _modeBtn.Click += (_, _) => SetMode(_mode switch
            {
                ValueRangeMode.Fixed   => ValueRangeMode.Current,
                ValueRangeMode.Current => _isMultiFrame ? ValueRangeMode.All : (_roiAvailable ? ValueRangeMode.Roi : ValueRangeMode.Fixed),
                ValueRangeMode.All     => _roiAvailable ? ValueRangeMode.Roi : ValueRangeMode.Fixed,
                _                      => ValueRangeMode.Fixed,   // Roi → Fixed
            });
            _searchMinBtn.Click += (_, _) => SearchMinRequested?.Invoke(this, EventArgs.Empty);
            _searchMaxBtn.Click += (_, _) => SearchMaxRequested?.Invoke(this, EventArgs.Empty);

            _minBox.GotFocus    += OnMinGotFocus;
            _minBox.LostFocus   += OnMinLostFocus;
            _minBox.KeyDown     += OnMinKeyDown;
            _minBox.TextChanged += OnMinTextChanged;

            _maxBox.GotFocus    += OnMaxGotFocus;
            _maxBox.LostFocus   += OnMaxLostFocus;
            _maxBox.KeyDown     += OnMaxKeyDown;
            _maxBox.TextChanged += OnMaxTextChanged;

            // ── Layout ────────────────────────────────────────────────────────
            // Grid with Star columns for Min/Max TextBoxes so they shrink with the window.
            // ColumnDefinition.MaxWidth = BoxWidth keeps the current look as the upper bound;
            // MinWidth prevents collapse below a readable size.
            var minLabel = MakeLabel("Min");
            var maxLabel = MakeLabel("Max");

            var grid = new Grid
            {
                Margin = new Thickness(4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                ColumnSpacing = 2,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 0: ModeBtn
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 1: "Min"
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinBoxWidth, MaxWidth = BoxWidth });   // 2: MinBox
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 3: SearchMin
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 4: "Max"
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinBoxWidth, MaxWidth = BoxWidth });   // 5: MaxBox
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 6: SearchMax

            Grid.SetColumn(_modeBtn, 0);
            Grid.SetColumn(minLabel, 1);
            Grid.SetColumn(_minBox, 2);
            Grid.SetColumn(_searchMinBtn, 3);
            Grid.SetColumn(maxLabel, 4);
            Grid.SetColumn(_maxBox, 5);
            Grid.SetColumn(_searchMaxBtn, 6);

            grid.Children.Add(_modeBtn);
            grid.Children.Add(minLabel);
            grid.Children.Add(_minBox);
            grid.Children.Add(_searchMinBtn);
            grid.Children.Add(maxLabel);
            grid.Children.Add(_maxBox);
            grid.Children.Add(_searchMaxBtn);

            Content = grid;
        }

        // ── Public helpers ────────────────────────────────────────────────────

        /// <summary>Sets the mode and updates all visual state.</summary>
        public void SetMode(ValueRangeMode mode)
        {
            _mode = mode;
            bool editable = mode == ValueRangeMode.Fixed;
            _minBox.IsReadOnly = !editable;
            _maxBox.IsReadOnly = !editable;
            _minBox.Classes.Remove("grayed");
            _maxBox.Classes.Remove("grayed");
            if (!editable) { _minBox.Classes.Add("grayed"); _maxBox.Classes.Add("grayed"); }
            _searchMinBtn.IsEnabled = editable;
            _searchMaxBtn.IsEnabled = editable;
            UpdateModeBtnLabel();
            ModeChanged?.Invoke(this, mode);
        }

        /// <summary>Convenience overload: <c>false</c> → Current, <c>true</c> → Fixed.</summary>
        public void SetMode(bool isFixed)
            => SetMode(isFixed ? ValueRangeMode.Fixed : ValueRangeMode.Current);

        /// <summary>Update the displayed min/max without firing <see cref="RangeChanged"/>.</summary>
        public void SetRange(double min, double max)
        {
            _updating = true;
            _lastMin = min;
            _lastMax = max;
            _minBox.Text = FormatValue(min);
            _maxBox.Text = FormatValue(max);
            _updating = false;
        }

        // ── GotFocus: switch to full-precision edit format ────────────────────

        private void OnMinGotFocus(object? s, GotFocusEventArgs e)
        {
            if (!IsFixedRange) return;
            if (!double.IsNaN(_lastMin))
            {
                _updating = true;
                _minBox.Text = _lastMin.ToString("G10");
                _updating = false;
            }
            Dispatcher.UIThread.Post(() => _minBox.SelectAll());
        }

        private void OnMaxGotFocus(object? s, GotFocusEventArgs e)
        {
            if (!IsFixedRange) return;
            if (!double.IsNaN(_lastMax))
            {
                _updating = true;
                _maxBox.Text = _lastMax.ToString("G10");
                _updating = false;
            }
            Dispatcher.UIThread.Post(() => _maxBox.SelectAll());
        }

        // ── KeyDown: Enter commits, Escape cancels ────────────────────────────

        private void OnMinKeyDown(object? s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _maxBox.Focus(); e.Handled = true; }      // LostFocus will reformat
            else if (e.Key == Key.Escape) { Revert(); _maxBox.Focus(); e.Handled = true; }
        }

        private void OnMaxKeyDown(object? s, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { _modeBtn.Focus(); e.Handled = true; }     // LostFocus will reformat
            else if (e.Key == Key.Escape) { Revert(); _modeBtn.Focus(); e.Handled = true; }
        }

        // ── TextChanged: live update while typing ─────────────────────────────────

        private void OnMinTextChanged(object? s, TextChangedEventArgs e)
        {
            if (!IsFixedRange || _updating) return;
            if (!double.TryParse(_minBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double vMin)) return;
            _lastMin = vMin;
            RangeChanged?.Invoke(this, (_lastMin, _lastMax));
        }

        private void OnMaxTextChanged(object? s, TextChangedEventArgs e)
        {
            if (!IsFixedRange || _updating) return;
            if (!double.TryParse(_maxBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double vMax)) return;
            _lastMax = vMax;
            RangeChanged?.Invoke(this, (_lastMin, _lastMax));
        }

        private void OnMinLostFocus(object? s, RoutedEventArgs e)
        {
            if (!IsFixedRange || _updating) return;
            if (double.TryParse(_minBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            {
                _updating = true;
                _minBox.Text = FormatValue(_lastMin);
                _updating = false;
            }
            else Revert();
        }

        private void OnMaxLostFocus(object? s, RoutedEventArgs e)
        {
            if (!IsFixedRange || _updating) return;
            if (double.TryParse(_maxBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
            {
                _updating = true;
                _maxBox.Text = FormatValue(_lastMax);
                _updating = false;
            }
            else Revert();
        }

        // ── Revert ───────────────────────────────────────────────────────────────

        private void Revert()
        {
            _updating = true;
            _minBox.Text = FormatValue(_lastMin);
            _maxBox.Text = FormatValue(_lastMax);
            _updating = false;
        }

        // ── Mode management ───────────────────────────────────────────────────

        /// <summary>
        /// Switches between 2-button (single frame: Fix/Auto) and 3-button (multi-frame: Fix/Curr/All) layouts.
        /// Falls back to Current mode if the current mode is All and switching to single-frame.
        /// </summary>
        public void SetMultiFrame(bool isMultiFrame)
        {
            _isMultiFrame = isMultiFrame;
            if (!isMultiFrame && _mode == ValueRangeMode.All)
                SetMode(ValueRangeMode.Current);
            else
                UpdateModeBtnLabel();
        }

        /// <summary>
        /// Marks the All-mode range as imperfect (some frames not yet scanned).
        /// Appends <c>*</c> to the All button and updates its tooltip.
        /// </summary>
        public void SetImperfect(bool imperfect)
        {
            if (_isImperfect == imperfect) return;
            _isImperfect = imperfect;
            if (_mode == ValueRangeMode.All) UpdateModeBtnLabel();
        }

        /// <summary>
        /// Notifies the bar whether an ROI overlay is currently available for use.
        /// When set to <c>false</c> while <see cref="Mode"/> is <see cref="ValueRangeMode.Roi"/>,
        /// silently falls back to <see cref="ValueRangeMode.Current"/>.
        /// </summary>
        public void SetRoiAvailable(bool available)
        {
            _roiAvailable = available;
            if (!available && _mode == ValueRangeMode.Roi)
                SetMode(ValueRangeMode.Current);
            else
                UpdateModeBtnLabel();
        }

        private void UpdateModeBtnLabel()
        {
            var (label, tip) = _mode switch
            {
                ValueRangeMode.Fixed   => ("Fixed",
                                           "Fixed: user-specified numeric min/max"),
                ValueRangeMode.Current => (_isMultiFrame ? "Current" : "Auto",
                                           _isMultiFrame
                                               ? "Current frame min/max (automatic)"
                                               : "Automatic min/max from current frame"),
                ValueRangeMode.Roi     => ("ROI",
                                           "Value range from designated ROI overlay"),
                _                      => (_isImperfect ? "All*" : "All",
                                           _isImperfect
                                               ? "Global min/max \u2014 some frames not yet scanned"
                                               : "Global min/max across all frames"),
            };
            _modeBtn.Content = label;
            ToolTip.SetTip(_modeBtn, tip);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static TextBox MakeTextBox() => new TextBox
        {
            // Width is controlled by the parent Grid's Star column (MinWidth..MaxWidth).
            // MinWidth = 0 overrides the Fluent theme's TextControlThemeMinWidth (64),
            // which would otherwise prevent the TextBox from shrinking below 64 px.
            MinWidth = 0,
            Height = ItemH,
            MinHeight = 0,
            IsReadOnly = true,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 10,
            Margin = new Thickness(0),
            Padding = new Thickness(4, 0),
        };

        private static readonly IBrush SearchEnabledBrush  = new SolidColorBrush(Color.FromRgb(180, 60, 60));
        private static readonly IBrush SearchDisabledBrush = Brushes.DimGray;

        /// <summary>
        /// Creates a vector arrow icon: ↑ with top bar (max) or ↓ with bottom bar (min).
        /// Pure <see cref="Path"/> geometry — no emoji, no font dependency.
        /// </summary>
        private static Path CreateArrowIcon(bool isMin)
        {
            // Min: bottom bar + down arrow  |  Max: top bar + up arrow
            string geo = isMin
                ? "M 2,14 L 14,14  M 8,12 L 8,2  M 8,12 L 4,8  M 8,12 L 12,8"
                : "M 2,2  L 14,2   M 8,4  L 8,14 M 8,4  L 4,8  M 8,4  L 12,8";

            return new Path
            {
                Data = StreamGeometry.Parse(geo),
                Stroke = SearchDisabledBrush,
                StrokeThickness = 1,
                StrokeLineCap = PenLineCap.Round,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
            };
        }

        private static Button MakeSearchBtn(bool isMin, string tooltip)
        {
            var icon = CreateArrowIcon(isMin);
            var btn = new Button
            {
                Content = icon,
                Width = BtnSize,
                Height = BtnSize,
                Padding = new Thickness(0),
                IsEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
            };
            btn.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsEnabledProperty)
                    icon.Stroke = btn.IsEnabled ? SearchEnabledBrush : SearchDisabledBrush;
            };
            ToolTip.SetTip(btn, tooltip);
            return btn;
        }

        private static TextBlock MakeLabel(string text) => new TextBlock
        {
            Text = text,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static string FormatValue(double v)
        {
            if (double.IsNaN(v)) return "—";
            return (Math.Abs(v) >= 1e6 || (v != 0 && Math.Abs(v) < 0.001))
                ? v.ToString("G4")
                : v.ToString("G6");
        }
    }
}
