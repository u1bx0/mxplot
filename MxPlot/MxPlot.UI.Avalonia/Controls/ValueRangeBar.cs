using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Diagnostics;
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

        private const double BaseFontSize = 11; // base font size for labels and mode button; 

        // Everything in the row except the two value boxes: mode button + full-scan button (+ the
        // small gap between them and the group's own right margin), the Min/Max labels, the two
        // search buttons, the column spacing and the grid margin.
        private const double FixedPartsWidth = 52 + BtnSize + 2 + 6 + 26 + 26 + BtnSize * 2 + 6 * 2 + 4 * 2;

        /// <summary>
        /// Narrowest width at which the bar is still fully usable, i.e. both value boxes at
        /// <see cref="MinBoxWidth"/>.
        /// </summary>
        public static double MinUsefulWidth => FixedPartsWidth + MinBoxWidth * 2;

        /// <summary>
        /// Width at which both value boxes reach <see cref="BoxWidth"/>; anything beyond this is
        /// wasted space, because the boxes stop growing.
        /// <para>
        /// A host that places the bar in a proportional slot should clamp that slot to
        /// [<see cref="MinUsefulWidth"/>, <see cref="PreferredWidth"/>]. An auto-sized slot must be
        /// avoided: it measures the bar with unbounded width, which makes the star-sized value
        /// boxes fall back to sizing on their content and visibly jump as the digit count changes.
        /// </para>
        /// </summary>
        public static double PreferredWidth => FixedPartsWidth + BoxWidth * 2;
        // ── Controls ──────────────────────────────────────────────────────────
        private readonly Button _modeBtn;     // shows mode-picker flyout on click
        private readonly TextBox _minBox;
        private readonly TextBox _maxBox;
        private readonly Button _searchMinBtn;
        private readonly Button _searchMaxBtn;
        private readonly TextBlock _imperfectBadge; // * indicator shown in All mode when imperfect
        private readonly Button _fullScanBtn;   // 🔄 manual full min/max scan, shown next to the mode button

        // ── State ─────────────────────────────────────────────────────────────
        private ValueRangeMode _mode;       // current display mode
        private bool _isMultiFrame;         // true when FrameCount > 1
        private bool _isImperfect;          // true when All range is only partially scanned
        private int _invalidCount;          // number of frames not yet scanned
        private bool _fullScanAvailable;    // true while the owner's backend supports a manual full scan (see SetFullScanAvailable)
        private bool _roiAvailable;         // true when an ROI overlay is designated
        private bool _forceReadOnly;        // Composite Channel-wise header: read-only even in Fixed
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

        /// <summary>
        /// Fired when the user clicks the 🔄 full-scan button (see <see cref="SetFullScanAvailable"/>).
        /// The owner decides what "full scan" means and whether to confirm with the user first --
        /// this control only reports the click.
        /// </summary>
        public event EventHandler? FullScanRequested;

        // ── Public properties ─────────────────────────────────────────────────

        /// <summary>True when <see cref="Mode"/> is <see cref="ValueRangeMode.Fixed"/>.</summary>
        public bool IsFixedRange => _mode == ValueRangeMode.Fixed;
        /// <summary>The current display/control mode.</summary>
        public ValueRangeMode Mode => _mode;
        /// <summary>True when the All-mode range is flagged as only partially scanned (see <see cref="SetImperfect"/>).</summary>
        internal bool IsImperfect => _isImperfect;

        /// <summary>The min value currently displayed in the bar (valid for all modes).</summary>
        public double DisplayedMinValue => _lastMin;
        /// <summary>The max value currently displayed in the bar (valid for all modes).</summary>
        public double DisplayedMaxValue => _lastMax;

        /// <summary>
        /// The actual component width to the right edge of SerachMaxButton
        /// </summary>
        public double EffectiveUIComponentWidth => _searchMaxBtn is not null ? _searchMaxBtn.Bounds.Right : double.NaN;

        // ── Constructor ───────────────────────────────────────────────────────

        public ValueRangeBar()
        {
            // Warning indicator for imperfect All mode (displayed inside mode button)
            _imperfectBadge = new TextBlock
            {
                Text = "*",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 0)),
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, -2, 0, 0),  // slight upward shift for visual balance
            };

            // Mode button content: [TextBlock] [⚠ icon]
            var modeBtnContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Auto",
                        FontSize = BaseFontSize,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    _imperfectBadge,
                },
            };

            _modeBtn = new Button
            {
                Content = modeBtnContent,
                Height = BtnSize,
                Padding = new Thickness(4, 0),
                MinWidth = 34,
                Width = 52,  // Fixed width to prevent content-driven resizing
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(_modeBtn, "Click to select value range mode");
            _modeBtn.ContextMenu = new ContextMenu();

            _minBox = MakeTextBox();
            _maxBox = MakeTextBox();
            _minBox.Classes.Add("grayed"); // Auto (readonly) by default
            _maxBox.Classes.Add("grayed");

            _searchMinBtn = MakeSearchBtn(isMin: true,  "Find min value in current frame");
            _searchMaxBtn = MakeSearchBtn(isMin: false, "Find max value in current frame");

            // Same footprint as _searchMinBtn/_searchMaxBtn; spacing to _modeBtn comes from the
            // wrapping StackPanel below (see modeAndScanPanel), not from its own margin, so a
            // hidden button leaves no leftover gap.
            _fullScanBtn = new Button
            {
                // Always enabled while visible (no grayed-out state like the search buttons have),
                // so it uses their "enabled" color outright rather than SearchDisabledBrush.
                Content = new PathIcon { Data = MenuIcons.Refresh, Width = 11, Height = 11, Foreground = SearchEnabledBrush },
                Width = BtnSize,
                Height = BtnSize,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                IsVisible = false, // shown only via SetFullScanAvailable, alongside the "*" indicator
            };
            ToolTip.SetTip(_fullScanBtn, "Scan every frame for the exact min/max");

            // ── Wire events ───────────────────────────────────────────────────
            _modeBtn.Click += (_, _) => OpenModePopup();
            _searchMinBtn.Click += (_, _) => SearchMinRequested?.Invoke(this, EventArgs.Empty);
            _searchMaxBtn.Click += (_, _) => SearchMaxRequested?.Invoke(this, EventArgs.Empty);
            _fullScanBtn.Click += (_, _) => FullScanRequested?.Invoke(this, EventArgs.Empty);

            RegisterBoxEvents(_minBox, isMin: true,  nextFocus: _maxBox);
            RegisterBoxEvents(_maxBox, isMin: false, nextFocus: _modeBtn);

            // ── Layout ────────────────────────────────────────────────────────
            // Grid with Star columns for Min/Max TextBoxes so they shrink with the window.
            // ColumnDefinition.MaxWidth = BoxWidth keeps the current look as the upper bound;
            // MinWidth prevents collapse below a readable size.
            var minLabel = MakeLabel("Min");
            var maxLabel = MakeLabel("Max");

            // ModeBtn + FullScanBtn share one grid column via a StackPanel: its Spacing only
            // applies between children that are actually visible, so a hidden FullScanBtn leaves
            // no leftover gap -- unlike giving it its own grid column, whose ColumnSpacing gap
            // would persist even while empty.
            var modeAndScanPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _modeBtn, _fullScanBtn },
            };

            var grid = new Grid
            {
                Margin = new Thickness(4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                ColumnSpacing = 2,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 0: ModeBtn + FullScanBtn
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 1: "Min"
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinBoxWidth, MaxWidth = BoxWidth });   // 2: MinBox
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 3: SearchMin
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 4: "Max"
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = MinBoxWidth, MaxWidth = BoxWidth });   // 5: MaxBox
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                                                       // 6: SearchMax

            Grid.SetColumn(modeAndScanPanel, 0);
            Grid.SetColumn(minLabel, 1);
            Grid.SetColumn(_minBox, 2);
            Grid.SetColumn(_searchMinBtn, 3);
            Grid.SetColumn(maxLabel, 4);
            Grid.SetColumn(_maxBox, 5);
            Grid.SetColumn(_searchMaxBtn, 6);

            grid.Children.Add(modeAndScanPanel);
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
            ApplyEditableState();
            UpdateModeBtnLabel();
            ModeChanged?.Invoke(this, mode);
        }

        /// <summary>
        /// Applies the readonly / grayed / search-enabled visual state implied by the current
        /// mode and <see cref="_forceReadOnly"/>. Split out of <see cref="SetMode"/> so that
        /// <see cref="SetRangeEditable"/> can refresh it without firing <see cref="ModeChanged"/>.
        /// </summary>
        private void ApplyEditableState()
        {
            bool editable = _mode == ValueRangeMode.Fixed && !_forceReadOnly;
            _minBox.IsReadOnly = !editable;
            _maxBox.IsReadOnly = !editable;
            _minBox.Classes.Remove("grayed");
            _maxBox.Classes.Remove("grayed");
            if (!editable) { _minBox.Classes.Add("grayed"); _maxBox.Classes.Add("grayed"); }
            _searchMinBtn.IsEnabled = editable;
            _searchMaxBtn.IsEnabled = editable;
            // The mode menu is suppressed only by the explicit read-only override. In normal
            // (LUT) use it must stay reachable whatever the current mode is.
            _modeBtn.IsEnabled = !_forceReadOnly;
        }

        /// <summary>Convenience overload: <c>false</c> → Current, <c>true</c> → Fixed.</summary>
        public void SetMode(bool isFixed)
            => SetMode(isFixed ? ValueRangeMode.Fixed : ValueRangeMode.Current);

        /// <summary>Update the displayed min/max without firing <see cref="RangeChanged"/>.</summary>
        /// <remarks>
        /// <c>_updating</c> alone cannot deliver that guarantee: Avalonia raises
        /// <see cref="TextBox.TextChanged"/> through the dispatcher, so the callback arrives after
        /// this method has already cleared the flag. <see cref="_lastMin"/>/<see cref="_lastMax"/>
        /// are therefore written *before* the boxes, and the handler drops any event whose parsed
        /// value already matches them — see <see cref="RegisterBoxEvents"/>.
        /// </remarks>
        public void SetRange(double min, double max)
        {
            _updating = true;
            _lastMin = min;
            _lastMax = max;
            _minBox.Text = FormatValue(min);
            _maxBox.Text = FormatValue(max);
            _updating = false;
        }

        // ── Box event wiring ──────────────────────────────────────────────────

        /// <summary>
        /// Attaches all keyboard/focus/text events for a Min or Max TextBox.
        /// <paramref name="isMin"/> selects which backing field to read/write.
        /// <paramref name="nextFocus"/> is the control that receives focus on Enter.
        /// </summary>
        private void RegisterBoxEvents(TextBox box, bool isMin, Control nextFocus)
        {
            box.GotFocus += (_, _) =>
            {
                if (!IsFixedRange) return;
                double val = isMin ? _lastMin : _lastMax;
                if (!double.IsNaN(val))
                {
                    _updating = true;
                    box.Text = val.ToString("G10");
                    _updating = false;
                }
                Dispatcher.UIThread.Post(() => box.SelectAll());
            };

            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)  { nextFocus.Focus(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Revert(); nextFocus.Focus(); e.Handled = true; }
                else if ((e.Key == Key.Up || e.Key == Key.Down) && IsFixedRange)
                {
                    if (NudgeDigitAtCaret(box, e.Key == Key.Up ? +1 : -1))
                    {
                        if (double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                        {
                            if (isMin) _lastMin = v; else _lastMax = v;
                            RangeChanged?.Invoke(this, (_lastMin, _lastMax));
                        }
                        e.Handled = true;
                    }
                }
            };

            box.TextChanged += (_, _) =>
            {
                if (!IsFixedRange || _updating) return;
                if (!double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) return;
                // TextChanged is dispatched, not raised inline, so a programmatic SetRange lands
                // here with _updating already cleared. The value it stored is the authority: an
                // event that only restates it is an echo of our own write, not a user edit.
                // (double.Equals treats NaN as equal to NaN, which is what we want here.)
                if (v.Equals(isMin ? _lastMin : _lastMax)) return;
                if (isMin) _lastMin = v; else _lastMax = v;
                RangeChanged?.Invoke(this, (_lastMin, _lastMax));
            };

            box.LostFocus += (_, _) =>
            {
                if (!IsFixedRange || _updating) return;
                if (double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                {
                    double val = isMin ? _lastMin : _lastMax;
                    _updating = true;
                    box.Text = FormatValue(val);
                    _updating = false;
                }
                else Revert();
            };
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
        /// Updates the button icon and tooltip with detailed frame count information.
        /// </summary>
        /// <param name="imperfect">True if the All-mode range is incomplete.</param>
        /// <param name="invalidCount">The number of frames not yet scanned (used in tooltip).</param>
        public void SetImperfect(bool imperfect, int invalidCount = 0)
        {
            if (_isImperfect == imperfect && _invalidCount == invalidCount) return;
            _isImperfect = imperfect;
            _invalidCount = invalidCount;
            UpdateModeBtnLabel();
        }

        /// <summary>
        /// Tells the bar whether the owner's current backend supports a manual full min/max scan
        /// (e.g. MMF, or a still-filling Lazy-decode backend) -- unrelated to whether one is
        /// currently needed. The 🔄 button is shown only when this is <see langword="true"/> *and*
        /// the "*" indicator is showing -- that is, <see cref="IsImperfect"/> is true and either
        /// <see cref="Mode"/> is <see cref="ValueRangeMode.All"/> or the bar is the read-only
        /// summary of other bars (<see cref="SetRangeEditable"/>).
        /// The owner is expected to call this every time it also calls <see cref="SetImperfect"/>,
        /// passing <see langword="false"/> once the backend stops needing it (e.g. a Lazy-decode
        /// backend that just finished its background fill) so the button quietly disappears instead
        /// of lingering for an operation that no longer applies.
        /// </summary>
        public void SetFullScanAvailable(bool available)
        {
            if (_fullScanAvailable == available) return;
            _fullScanAvailable = available;
            UpdateModeBtnLabel();
        }

        /// <summary>
        /// Makes the whole range control non-interactive: the mode menu, the Min/Max boxes and the
        /// search buttons.
        /// Used by Composite mode's header bar in Channel-wise scope, where the displayed range is a
        /// read-only union of the per-channel ranges and is therefore never edited directly.
        /// LUT mode never calls this, so its behaviour is unchanged: the flag defaults to
        /// <c>false</c> and <see cref="SetMode"/> is otherwise untouched.
        /// </summary>
        public void SetRangeEditable(bool editable)
        {
            if (_forceReadOnly == !editable) return;
            _forceReadOnly = !editable;
            ApplyEditableState();
            UpdateModeBtnLabel(); // the read-only summary shows "*" / the scan button on its own terms
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
            // Get the TextBlock inside the mode button's StackPanel
            if (_modeBtn.Content is not StackPanel panel || panel.Children[0] is not TextBlock textBlock)
                return;

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
                _                      => ("All",
                                           _isImperfect && _invalidCount > 0
                                               ? $"Global min/max — {_invalidCount} frame{(_invalidCount == 1 ? "" : "s")} not yet scanned"
                                               : _isImperfect
                                                   ? "Global min/max — some frames not yet scanned"
                                                   : "Global min/max across all frames"),
            };

            textBlock.Text = label;
            ToolTip.SetTip(_modeBtn, tip);

            // Show the * indicator while imperfect, in All mode -- or in any mode while read-only.
            // A read-only bar is Composite's header: a summary of the per-channel rows, whose own
            // mode chip is a leftover from Global scope and says nothing about what the rows are
            // doing. There the summary's own "some frames are not scanned" is what matters.
            bool showImperfect = _isImperfect && (_forceReadOnly || _mode == ValueRangeMode.All);
            _imperfectBadge.IsVisible = showImperfect;
            if (showImperfect)
            {
                // In All mode the mode tooltip already says it; otherwise (read-only summary) the
                // mode tooltip describes a different mode entirely, so word it here.
                ToolTip.SetTip(_imperfectBadge, _mode == ValueRangeMode.All
                    ? tip
                    : _invalidCount > 0
                        ? $"{_invalidCount} frame{(_invalidCount == 1 ? "" : "s")} not yet scanned"
                        : "Some frames not yet scanned");
            }

            // Same gating as the * indicator, plus the owner's own availability signal.
            _fullScanBtn.IsVisible = showImperfect && _fullScanAvailable;
        }

        // ── Mode picker flyout ────────────────────────────────────────────────

        private void OpenModePopup()
        {
            var menu = _modeBtn.ContextMenu;
            if (menu == null) return;

            menu.Items.Clear();

            void AddItem(ValueRangeMode mode, string label, string description, bool showWarning = false)
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                // Label with optional warning icon
                var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                labelPanel.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = BaseFontSize,
                    MinWidth = 52,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                if (showWarning)
                {
                    var menuFillPath = new Path
                    {
                        Data = Geometry.Parse(
                            "M 7,0 L 14,12 L 0,12 Z " +
                            "M 6.3,3.5 L 6.3,8 L 7.7,8 L 7.7,3.5 Z " +
                            "M 6.3,9.5 L 6.3,11 L 7.7,11 L 7.7,9.5 Z"),
                        Fill = new SolidColorBrush(Color.FromRgb(255, 190, 0)),
                    };
                    var menuStrokePath = new Path
                    {
                        Data = Geometry.Parse("M 7,0 L 14,12 L 0,12 Z"),
                        Stroke = new SolidColorBrush(Color.FromRgb(80, 60, 0)),
                        StrokeThickness = 0.8,
                    };
                    var warningIcon = new Panel
                    {
                        Width = 10,
                        Height = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { menuFillPath, menuStrokePath },
                    };
                    labelPanel.Children.Add(warningIcon);
                }

                header.Children.Add(labelPanel);
                header.Children.Add(new TextBlock
                {
                    Text = description,
                    FontSize = BaseFontSize - 1,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var item = new MenuItem { Header = header };
                item.Click += (_, _) => SetMode(mode);
                menu.Items.Add(item);
            }

            AddItem(ValueRangeMode.Fixed, "Fixed", "User-specified min/max");
            AddItem(ValueRangeMode.Current,
                _isMultiFrame ? "Current" : "Auto",
                _isMultiFrame ? "Current frame min/max" : "Automatic min/max");

            if (_isMultiFrame)
            {
                string allDescription = _isImperfect && _invalidCount > 0
                    ? $"{_invalidCount} frame{(_invalidCount == 1 ? "" : "s")} not yet scanned"
                    : _isImperfect
                        ? "Some frames not yet scanned"
                        : "Global min/max across all frames";
                AddItem(ValueRangeMode.All, "All", allDescription, showWarning: _isImperfect);
            }

            if (_roiAvailable)
                AddItem(ValueRangeMode.Roi, "ROI", "Value range from designated ROI overlay");

            menu.PlacementTarget = _modeBtn;
            menu.Placement = PlacementMode.AnchorAndGravity;
            menu.PlacementAnchor = PopupAnchor.BottomLeft;
            menu.PlacementGravity = PopupGravity.BottomRight;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.Open(_modeBtn);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Increments or decrements the decimal digit under the caret in <paramref name="box"/>
        /// by <paramref name="delta"/> (+1 or -1), with no carry/borrow across digits.
        /// Returns <c>true</c> if a digit was found and modified.
        /// </summary>
        private static bool NudgeDigitAtCaret(TextBox box, int delta)
        {
            var text = box.Text ?? string.Empty;
            int caret = box.CaretIndex;

            // Clamp caret to valid range; treat position after last char as last char.
            if (text.Length == 0) return false;
            int pos = Math.Clamp(caret > 0 ? caret - 1 : 0, 0, text.Length - 1);

            // Walk left from caret to find the nearest digit.
            while (pos >= 0 && !char.IsDigit(text[pos])) pos--;
            if (pos < 0) return false;

            char original = text[pos];
            int digit = original - '0';
            // Clamp without carry: 9+1 stays 9, 0-1 stays 0.
            digit = Math.Clamp(digit + delta, 0, 9);
            if (digit == original - '0') return false; // no change (already at boundary)

            var newText = string.Concat(text.AsSpan(0, pos), ((char)('0' + digit)).ToString(), text.AsSpan(pos + 1));
            box.Text = newText;
            box.CaretIndex = pos + 1;
            return true;
        }
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
            FontSize = BaseFontSize,
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
