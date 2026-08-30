using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// A compact horizontal editor for a single <see cref="BlendRecipe"/> (one composite channel).
    /// Layout: <c>[chk] [Name] [Color] [== ValueRangeBar ==] [== histogram ==] Gain▾ [──] [1.0x]</c>
    /// <para>
    /// The "Gain" slot is a mode button (same idiom as <see cref="ValueRangeBar"/>'s own mode
    /// button): it and the slider/box pair to its right are shared between <see cref="BlendRecipe.Gain"/>
    /// and <see cref="BlendRecipe.Gamma"/>, switched via a small menu. Both parameters live in the
    /// row at once, but there is no room to show both editors side by side, so only one is ever
    /// visible; a <c>*</c> badge plus a combined ToolTip on the mode button keeps the hidden one
    /// from being silently forgotten.
    /// </para>
    /// <para>
    /// Plays the same role for a composite channel that <see cref="AxisTracker"/> plays for an
    /// <see cref="MxPlot.Core.Axis"/>: it owns its controls and their interactions and reports user
    /// edits through events, while the host (<c>MatrixPlotter.Composite</c>) owns the recipe list
    /// and all data access.
    /// </para>
    /// <para>
    /// The Min/Max half of the row is a real <see cref="ValueRangeBar"/> — the same control the LUT
    /// toolbar uses — so a channel's range UI is identical to LUT mode's, including the
    /// Fixed/Current/All mode menu and the amber <c>*</c> badge for a partially scanned All range.
    /// In Global scope the host disables it via <see cref="SetRangeEnabled"/> and pushes the shared
    /// range in with <see cref="SetRange"/>.
    /// </para>
    /// <para>
    /// This control never reads <see cref="MxPlot.Core.IMatrixData"/>. Pixel-derived values arrive
    /// through <see cref="SetHistogram"/> and <see cref="SetRange"/>; it only ever *requests* work
    /// via <see cref="RangeModeChanged"/> / <see cref="SearchMinRequested"/> /
    /// <see cref="SearchMaxRequested"/>.
    /// </para>
    /// </summary>
    public class BlendRecipeBar : UserControl
    {
        // ── Dimensions ────────────────────────────────────────────────────────
        private const double RowHeight = 26;
        private const double ControlHeight = 20;
        private const double BaseFontSize = 11;
        private const double FontSizeSmall = 10;
        private const double GainBoxWidth = 46;
        private const double SwatchWidth = 20;
        private const double ItemSpacing = 4;
        private const double RowPaddingH = 5;
        private const double LabelWidth = 46;
        private const double GainGammaBtnWidth = 44;
        private const double DefaultGain = 1.0;
        private const double GainSliderDefaultMax = 5.0;
        // Sanity cap on typed-in gain: guards against a stray extra digit, not a real usage limit.
        private const double GainInputGuardMax = 1000.0;
        private const double DefaultGamma = 1.0;
        // Gamma's useful range is narrow around 1.0 - values far outside it barely change the
        // image further, unlike Gain which has real headroom.
        private const double GammaSliderMin = 0.1;
        private const double GammaSliderDefaultMax = 2.0;
        private const double GammaInputGuardMax = 10.0;

        private const string VisibilityTipNormal = "Click to toggle this channel on / off";
        private const string GainGammaBoxTip = "Click to type a value directly";
        private const string VisibilityTipLocked = "Cannot be hidden: at least one channel must stay visible";

        /// <summary>Which of the two shared-widget parameters is currently shown/editable.</summary>
        private enum GainGammaParam { Gain, Gamma }

        // ── Model ─────────────────────────────────────────────────────────────
        private BlendRecipe _recipe;

        /// <summary>Which parameter the shared Gain/Gamma slider+box currently edits.</summary>
        private GainGammaParam _activeParam = GainGammaParam.Gain;

        /// <summary>
        /// The slider's own Maximum for each parameter, tracked independently so that switching
        /// which one is shown does not lose an extension made to the other (see <see cref="ApplyTypedValue"/>).
        /// </summary>
        private double _gainMax;
        private double _gammaMax;

        /// <summary>
        /// Set while pushing external state into the child controls, so their change handlers do
        /// not echo the value back out as a user edit. Required because
        /// <see cref="ValueRangeBar.SetMode"/> raises <c>ModeChanged</c> even for programmatic calls.
        /// </summary>
        private bool _isUpdating;

        /// <summary>
        /// Whether this row owns its range (Channel-wise scope). Tracked separately from the
        /// hidden-channel greying so that toggling a channel back on does not silently re-enable
        /// the range bar while Global scope still owns the range.
        /// </summary>
        private bool _rangeEnabled = true;

        // ── Controls ──────────────────────────────────────────────────────────
        private readonly CheckBox _visibleToggle;
        private readonly TextBlock _nameLabel;
        private readonly Button _swatch;
        private readonly ValueRangeBar _rangeBar;
        private readonly HistogramPlotControl _histogram;
        private readonly Button _gainGammaBtn;
        private readonly TextBlock _gainGammaLabel;
        private readonly TextBlock _gainGammaBadge;
        private readonly Slider _gainSlider;
        private readonly TextBox _gainBox;

        /// <summary>
        /// Every control except <see cref="_visibleToggle"/>. Greyed out and disabled while the
        /// channel is hidden — the visibility toggle itself must stay live, otherwise a channel
        /// could be switched off but never back on.
        /// </summary>
        private readonly Control[] _contentControls;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>The channel name shown in the row label (e.g. a ColorAxis tag).</summary>
        public string ChannelName { get; private set; }

        /// <summary>The current recipe as edited by this bar.</summary>
        public BlendRecipe Recipe => _recipe;

        /// <summary>This channel's value-range mode (Fixed / Current / All).</summary>
        public ValueRangeMode RangeMode => _rangeBar.Mode;

        /// <summary>
        /// Fired after any user edit (visibility, colour, range, gain), carrying the updated recipe.
        /// Never fired for programmatic updates made through the <c>Set*</c> methods.
        /// </summary>
        public event EventHandler<BlendRecipe>? RecipeChanged;

        /// <summary>
        /// Fired when the user picks a different range mode for this channel. The host evaluates the
        /// new range and pushes it back via <see cref="SetRange"/> (and <see cref="SetImperfect"/>).
        /// </summary>
        public event EventHandler<ValueRangeMode>? RangeModeChanged;

        /// <summary>
        /// Fired when the user clicks the channel name. The host renames the underlying
        /// <see cref="MxPlot.Core.TaggedAxis"/> tag and echoes the result back through
        /// <see cref="SetChannelName"/>; the bar never touches the axis itself.
        /// </summary>
        public event EventHandler? RenameRequested;

        /// <summary>Fired when the user clicks the ↓ button: host should rescan this channel's Min.</summary>
        public event EventHandler? SearchMinRequested;

        /// <summary>Fired when the user clicks the ↑ button: host should rescan this channel's Max.</summary>
        public event EventHandler? SearchMaxRequested;

        // ── Constructor ───────────────────────────────────────────────────────

        public BlendRecipeBar(string channelName, BlendRecipe recipe)
        {
            ChannelName = channelName;
            _recipe = recipe;
            var channelColor = ToColor(recipe.ColorArgb);

            // ── Visibility toggle (same compact checkbox as AxisRenameDialog's "Index-based") ──
            _visibleToggle = ControlFactory.MakeCheckBox(string.Empty, BaseFontSize);
            _visibleToggle.IsChecked = recipe.IsVisible;
            _visibleToggle.VerticalAlignment = VerticalAlignment.Center;
            _visibleToggle.Margin = new Thickness(0);
            ToolTip.SetTip(_visibleToggle, VisibilityTipNormal);
            _visibleToggle.IsCheckedChanged += (_, _) =>
            {
                bool visible = _visibleToggle.IsChecked == true;
                ApplyContentEnabledState(visible);
                Commit(_recipe with { IsVisible = visible });
            };

            // ── Name ──
            _nameLabel = new TextBlock
            {
                Text = channelName,
                Width = LabelWidth,
                FontSize = BaseFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Left,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 2, 0),
            };
            ToolTip.SetTip(_nameLabel, "Click to rename");
            _nameLabel.Cursor = new Cursor(StandardCursorType.Hand);
            _nameLabel.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                RenameRequested?.Invoke(this, EventArgs.Empty);
            };

            // ── Color swatch ──
            _swatch = ControlFactory.MakeColorSwatch(channelColor, newColor =>
            {
                _histogram!.SetLut(BuildChannelGradientLut(newColor));
                Commit(_recipe with { ColorArgb = ToArgb(newColor) });
            }, showAlpha: false);
            _swatch.Width = SwatchWidth;
            _swatch.Height = ControlHeight;

            // ── Range (the LUT toolbar's own control, reused verbatim) ──
            _rangeBar = new ValueRangeBar();
            _rangeBar.SetRoiAvailable(false);    // ROI ranges are not supported per channel
            _rangeBar.SetMode(ValueRangeMode.Current);
            _rangeBar.SetRange(recipe.ValueMin, recipe.ValueMax);
            _rangeBar.ModeChanged += (_, mode) =>
            {
                if (_isUpdating) return;
                RangeModeChanged?.Invoke(this, mode);
            };
            _rangeBar.RangeChanged += (_, r) =>
            {
                if (_isUpdating) return;
                _histogram.SetViewValueRange(r.Min, r.Max);
                Commit(_recipe with { ValueMin = r.Min, ValueMax = r.Max });
            };
            _rangeBar.SearchMinRequested += (_, _) => { if (!_isUpdating) SearchMinRequested?.Invoke(this, EventArgs.Empty); };
            _rangeBar.SearchMaxRequested += (_, _) => { if (!_isUpdating) SearchMaxRequested?.Invoke(this, EventArgs.Empty); };

            // ── Histogram (drag the red bounds to set Min/Max) ──
            _histogram = new HistogramPlotControl
            {
                MinWidth = 0,     // may be squeezed to nothing; the range bar has priority
                Height = ControlHeight,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _histogram.ViewRangeChanged += (min, max) =>
            {
                if (_isUpdating) return;
                // A drag is an explicit range choice, so pin the channel to Fixed — otherwise the
                // next frame change would overwrite it (mirrors the LUT panel's histogram behaviour).
                // Only when this row actually owns its range: in Global scope the header bar owns
                // the mode, and flipping this row to Fixed would wrongly enable its search buttons.
                if (_rangeEnabled && _rangeBar.Mode != ValueRangeMode.Fixed)
                {
                    _isUpdating = true;
                    try { _rangeBar.SetMode(ValueRangeMode.Fixed); }
                    finally { _isUpdating = false; }
                    RangeModeChanged?.Invoke(this, ValueRangeMode.Fixed);
                }
                _isUpdating = true;
                try { _rangeBar.SetRange(min, max); }
                finally { _isUpdating = false; }
                Commit(_recipe with { ValueMin = min, ValueMax = max });
            };

            // ── Gain / Gamma (shared slider+box; mode button switches which one is live) ──
            _gainMax = Math.Max(GainSliderDefaultMax, recipe.Gain);
            _gammaMax = Math.Max(GammaSliderDefaultMax, recipe.Gamma);

            _gainGammaLabel = new TextBlock { Text = "Gain", FontSize = BaseFontSize, VerticalAlignment = VerticalAlignment.Center };
            _gainGammaBadge = new TextBlock
            {
                Text = "*",
                FontSize = BaseFontSize,
                FontWeight = FontWeight.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 0)),
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(1, -2, 0, 0),
            };
            var gainGammaBtnContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1 };
            gainGammaBtnContent.Children.Add(_gainGammaLabel);
            gainGammaBtnContent.Children.Add(_gainGammaBadge);
            _gainGammaBtn = new Button
            {
                Content = gainGammaBtnContent,
                // Fixed width (not Auto) so the column doesn't resize when the label text or badge
                // changes - other rows in the composite channel list must stay column-aligned.
                Width = GainGammaBtnWidth,
                Height = ControlHeight,
                MinHeight = 0,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
            };
            _gainGammaBtn.ContextMenu = new ContextMenu();
            _gainGammaBtn.Click += (_, _) => OpenGainGammaMenu();

            _gainSlider = new Slider
            {
                Minimum = ActiveMinimum,
                Maximum = ActiveMax,
                Value = ActiveValue,
                MinWidth = 0,
                Height = ControlHeight,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ConfigureCompactSlider(_gainSlider);
            _gainSlider.DoubleTapped += (_, e) =>
            {
                e.Handled = true;
                double def = ActiveDefault;
                double defMax = ActiveSliderDefaultMax;
                bool valueAtDefault = Math.Abs(_gainSlider.Value - def) < double.Epsilon;
                bool maxAtDefault = _gainSlider.Maximum.Equals(defMax);
                if (valueAtDefault && maxAtDefault) return;
                // A double-click reset also drops the extended range back to default, even though
                // an ordinary drag never touches Maximum - this is an explicit "reset everything" action.
                ActiveMax = defMax;
                _gainSlider.Maximum = defMax;
                _gainSlider.Value = def;   // raises ValueChanged, which commits the edit
                UpdateGainSliderToolTip();
            };
            _gainBox = new TextBox
            {
                Width = GainBoxWidth,
                Height = ControlHeight,
                MinHeight = 0,
                MinWidth = 0,
                Text = FormatActive(ActiveValue),
                FontSize = FontSizeSmall,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0),
                IsReadOnly = true,
            };
            _gainBox.Classes.Add("grayed");
            ToolTip.SetTip(_gainBox, GainGammaBoxTip);
            UpdateGainSliderToolTip();
            UpdateGainGammaLabel();
            _gainSlider.ValueChanged += (_, _) =>
            {
                if (_isUpdating) return;
                // Dragging the slider never touches Maximum - only a typed value can extend or
                // reset it (see ApplyTypedValue), so the thumb never jumps under the user's hand.
                _gainBox.Text = FormatActive(_gainSlider.Value);
                Commit(WithActiveValue(_gainSlider.Value));
            };

            // ── Box edit mode: focus switches from "5.0x"/"0.70" display to a plain, editable
            // number that can exceed the slider's default range. Enter or focus-leave commits
            // the value; only focus-leave reformats back to display form (mirrors ValueRangeBar's boxes).
            _gainBox.GotFocus += (_, _) =>
            {
                _isUpdating = true;
                try
                {
                    _gainBox.IsReadOnly = false;
                    _gainBox.Classes.Remove("grayed");
                    _gainBox.Text = ActiveValue.ToString("G10", CultureInfo.InvariantCulture);
                }
                finally { _isUpdating = false; }
                Dispatcher.UIThread.Post(() => _gainBox.SelectAll());
            };
            _gainBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (TryApplyTypedValue(out double applied))
                {
                    _isUpdating = true;
                    try { _gainBox.Text = applied.ToString("G10", CultureInfo.InvariantCulture); }
                    finally { _isUpdating = false; }
                }
                else
                {
                    _isUpdating = true;
                    try { _gainBox.Text = ActiveValue.ToString("G10", CultureInfo.InvariantCulture); }
                    finally { _isUpdating = false; }
                }
            };
            _gainBox.LostFocus += (_, _) =>
            {
                if (!_isUpdating) TryApplyTypedValue(out _);
                _isUpdating = true;
                try
                {
                    _gainBox.Text = FormatActive(ActiveValue);
                    _gainBox.IsReadOnly = true;
                    _gainBox.Classes.Add("grayed");
                }
                finally { _isUpdating = false; }
            };

            // ── Layout ──
            var grid = new Grid
            {
                Margin = new Thickness(RowPaddingH, 0),
                Height = RowHeight,
                ColumnSpacing = ItemSpacing,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));      // 0 visibleToggle
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));      // 1 nameLabel
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));      // 2 swatch
            // Bounded star rather than Auto: a definite width lets the bar's own value boxes track
            // the window exactly as they do in the LUT toolbar, while the bounds keep the bar from
            // being squeezed below usability or padded past the point where the boxes stop growing.
            // The travel is inherently small - the value boxes only span 36..66 px, so the whole bar
            // moves within a 60 px band - hence the heavier weight: at ordinary window sizes the bar
            // settles at its preferred width (full-size boxes) and only gives ground once the window
            // is genuinely narrow, at which point the histogram and gain slider yield first.
            grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Star)  // 3 rangeBar ★ (bounded)
            {
                MinWidth = ValueRangeBar.MinUsefulWidth,
                MaxWidth = ValueRangeBar.PreferredWidth,
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Star)); // 4 histogram ★ (shrinks to 0)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));      // 5 Gain/Gamma mode button
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star)); // 6 gainSlider ★ (shrinks to 0)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));      // 7 gainBox

            Control[] cells =
            {
                _visibleToggle, _nameLabel, _swatch, _rangeBar,
                _histogram, _gainGammaBtn, _gainSlider, _gainBox,
            };
            for (int i = 0; i < cells.Length; i++)
            {
                Grid.SetColumn(cells[i], i);
                grid.Children.Add(cells[i]);
            }

            _contentControls = new Control[cells.Length - 1];
            Array.Copy(cells, 1, _contentControls, 0, _contentControls.Length);

            Content = new Border { Child = grid, Padding = new Thickness(0, 1) };

            ApplyContentEnabledState(recipe.IsVisible);
        }

        // ── External (programmatic) updates — never raise RecipeChanged ────────

        /// <summary>
        /// Pushes a recipe in from the host (e.g. after a settings restore) without echoing a
        /// <see cref="RecipeChanged"/> event back.
        /// </summary>
        public void SetRecipe(BlendRecipe recipe)
        {
            _isUpdating = true;
            try
            {
                _recipe = recipe;
                var color = ToColor(recipe.ColorArgb);
                _visibleToggle.IsChecked = recipe.IsVisible;
                _swatch.Background = new SolidColorBrush(color);
                _rangeBar.SetRange(recipe.ValueMin, recipe.ValueMax);
                _gainMax = Math.Max(GainSliderDefaultMax, recipe.Gain);
                _gammaMax = Math.Max(GammaSliderDefaultMax, recipe.Gamma);
                _gainSlider.Minimum = ActiveMinimum;
                _gainSlider.Maximum = ActiveMax;
                _gainSlider.Value = ActiveValue;
                _gainBox.Text = FormatActive(ActiveValue);
                _histogram.SetLut(BuildChannelGradientLut(color));
                _histogram.SetViewValueRange(recipe.ValueMin, recipe.ValueMax);
            }
            finally { _isUpdating = false; }
            UpdateGainSliderToolTip();
            UpdateGainGammaLabel();
            ApplyContentEnabledState(recipe.IsVisible);
        }

        /// <summary>
        /// Mirrors a visibility change made elsewhere into this bar, without echoing an event back.
        /// </summary>
        public void SetVisibleState(bool visible)
        {
            if (_recipe.IsVisible == visible && _visibleToggle.IsChecked == visible) return;
            _isUpdating = true;
            try
            {
                _recipe = _recipe with { IsVisible = visible };
                _visibleToggle.IsChecked = visible;
            }
            finally { _isUpdating = false; }
            ApplyContentEnabledState(visible);
        }

        /// <summary>
        /// Pushes an evaluated (or shared, in Global scope) range in without echoing an event back.
        /// Updates the recipe, the range boxes, and the histogram's red bounds together.
        /// </summary>
        public void SetRange(double min, double max)
        {
            _isUpdating = true;
            try
            {
                _recipe = _recipe with { ValueMin = min, ValueMax = max };
                _rangeBar.SetRange(min, max);
                _histogram.SetViewValueRange(min, max);
            }
            finally { _isUpdating = false; }
        }

        /// <summary>Updates the displayed channel name after the host renamed the axis tag.</summary>
        public void SetChannelName(string name)
        {
            ChannelName = name;
            _nameLabel.Text = name;
        }

        /// <summary>Sets the range mode without raising <see cref="RangeModeChanged"/>.</summary>
        public void SetRangeMode(ValueRangeMode mode)
        {
            _isUpdating = true;
            try { _rangeBar.SetMode(mode); }
            finally { _isUpdating = false; }
        }

        /// <summary>
        /// Enables or disables this channel's range control. Disabled in Global scope, where the
        /// header bar owns the single shared range and each row merely mirrors it.
        /// </summary>
        public void SetRangeEnabled(bool enabled)
        {
            _rangeEnabled = enabled;
            _rangeBar.IsEnabled = enabled && _recipe.IsVisible;
        }

        /// <summary>
        /// Prevents this channel from being hidden. The host locks whichever channel is the last
        /// visible one, so a composite can never end up showing nothing. Purely a UI affordance:
        /// the toggle is disabled and explains why, rather than snapping back after the click.
        /// </summary>
        public void SetVisibilityLocked(bool locked)
        {
            _visibleToggle.IsEnabled = !locked;
            ToolTip.SetTip(_visibleToggle, locked ? VisibilityTipLocked : VisibilityTipNormal);
        }

        /// <summary>Shows or hides the All entry in this channel's mode menu.</summary>
        public void SetMultiFrame(bool isMultiFrame) => _rangeBar.SetMultiFrame(isMultiFrame);

        /// <summary>Marks this channel's All range as only partially scanned (amber <c>*</c> badge).</summary>
        public void SetImperfect(bool imperfect, int invalidCount = 0)
            => _rangeBar.SetImperfect(imperfect, invalidCount);

        /// <summary>
        /// Supplies freshly computed histogram bins for this channel's current frame.
        /// The red view bounds stay at the recipe's Min/Max.
        /// </summary>
        public void SetHistogram(int[] bins, double dataMin, double dataMax)
        {
            _isUpdating = true;
            try
            {
                // Fixed mode's Min/Max are user-set and must not move on a frame change - preserve
                // the plot window (zoom) the same way LUT mode's ValueRangeBar.Mode == Fixed does,
                // otherwise the red view lines snap out to the widget edges on every ActiveIndex step.
                bool preservePlot = _rangeBar.Mode == ValueRangeMode.Fixed;
                _histogram.SetHistogram(
                    bins, dataMin, dataMax,
                    _recipe.ValueMin, _recipe.ValueMax,
                    BuildChannelGradientLut(ToColor(_recipe.ColorArgb)),
                    preservePlot);
            }
            finally { _isUpdating = false; }
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private void Commit(BlendRecipe updated)
        {
            if (_isUpdating) return;
            _recipe = updated;
            UpdateGainGammaLabel();
            RecipeChanged?.Invoke(this, _recipe);
        }

        // ── Gain / Gamma shared-widget plumbing ─────────────────────────────────

        private double ActiveValue => _activeParam == GainGammaParam.Gain ? _recipe.Gain : _recipe.Gamma;
        private double ActiveDefault => _activeParam == GainGammaParam.Gain ? DefaultGain : DefaultGamma;
        private double ActiveMinimum => _activeParam == GainGammaParam.Gain ? 0.0 : GammaSliderMin;
        private double ActiveSliderDefaultMax => _activeParam == GainGammaParam.Gain ? GainSliderDefaultMax : GammaSliderDefaultMax;
        private double ActiveInputGuardMax => _activeParam == GainGammaParam.Gain ? GainInputGuardMax : GammaInputGuardMax;
        // Gain may be typed down to exactly 0 (mutes the channel). Gamma=0 is degenerate (t^0 is a
        // constant regardless of t), so its floor is the same fixed value as the slider's Minimum -
        // unlike Maximum, Minimum never extends further down than that via typed input.
        private double ActiveInputGuardMin => _activeParam == GainGammaParam.Gain ? 0.0 : GammaSliderMin;

        private double ActiveMax
        {
            get => _activeParam == GainGammaParam.Gain ? _gainMax : _gammaMax;
            set { if (_activeParam == GainGammaParam.Gain) _gainMax = value; else _gammaMax = value; }
        }

        private BlendRecipe WithActiveValue(double value)
            => _activeParam == GainGammaParam.Gain ? _recipe with { Gain = value } : _recipe with { Gamma = value };

        private static string FormatGamma(double g) => $"{g:F2}";
        private string FormatActive(double v) => _activeParam == GainGammaParam.Gain ? FormatGain(v) : FormatGamma(v);
        // Short form used inside slider ToolTips ("5x" / "2"), as opposed to FormatActive's box
        // display form ("5.0x" / "2.00").
        private string FormatActiveShort(double v) => _activeParam == GainGammaParam.Gain ? $"{v:0.#}x" : $"{v:0.#}";

        /// <summary>
        /// Parses <see cref="_gainBox"/>'s current text and, if it is a valid value for the active
        /// parameter (finite, within its guard range), applies it via <see cref="ApplyTypedValue"/>.
        /// Returns false (leaving <paramref name="applied"/> at the last committed value) for
        /// unparsable or out-of-range text, which the caller reverts to.
        /// </summary>
        private bool TryApplyTypedValue(out double applied)
        {
            applied = ActiveValue;
            if (!double.TryParse(_gainBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                return false;
            if (!double.IsFinite(v) || v < ActiveInputGuardMin || v > ActiveInputGuardMax)
                return false;
            applied = v;
            ApplyTypedValue(v);
            return true;
        }

        /// <summary>
        /// Applies a value that came from the text box, for whichever parameter is currently active.
        /// This is the only place the slider's Maximum ever changes: extending past the active
        /// parameter's default max for a value that exceeds it, and resetting back down once a typed
        /// value no longer needs the extension. Dragging the slider itself never calls this, so the
        /// thumb never jumps mid-drag. Each parameter tracks its own Maximum (<see cref="_gainMax"/> /
        /// <see cref="_gammaMax"/>) independently, so switching which one is shown never discards it.
        /// </summary>
        private void ApplyTypedValue(double value)
        {
            // Compare against the slider's Maximum too, not just the recipe's value: if the user
            // drags the slider down while Maximum is still extended (never reset by a drag, per
            // this method's own rule) and then retypes that same value, the Max-reset must still
            // happen even though the value itself did not change.
            double newMax = Math.Max(ActiveSliderDefaultMax, value);
            if (value.Equals(ActiveValue) && newMax.Equals(_gainSlider.Maximum)) return;
            ActiveMax = newMax;
            _isUpdating = true;
            try
            {
                _gainSlider.Maximum = newMax;
                _gainSlider.Value = value;
            }
            finally { _isUpdating = false; }
            UpdateGainSliderToolTip();
            Commit(WithActiveValue(value));
        }

        /// <summary>
        /// The "exceed default" hint is only useful while the slider is still at its default range -
        /// once it is already extended, the hint about how to extend it is stale.
        /// </summary>
        private void UpdateGainSliderToolTip()
        {
            string resetTip = $"Double-click to reset ({FormatActiveShort(ActiveDefault)})";
            double defaultMax = ActiveSliderDefaultMax;
            if (_gainSlider.Maximum > defaultMax)
            {
                ToolTip.SetTip(_gainSlider, resetTip);
                return;
            }
            string paramLabel = _activeParam == GainGammaParam.Gain ? "Gain" : "Gamma";
            ToolTip.SetTip(_gainSlider, $"To exceed {FormatActiveShort(defaultMax)}, type a value in the {paramLabel} box\n{resetTip}");
        }

        /// <summary>Updates the mode button's label, <c>*</c> badge, and combined-value ToolTip.</summary>
        private void UpdateGainGammaLabel()
        {
            _gainGammaLabel.Text = _activeParam == GainGammaParam.Gain ? "Gain" : "Gamma";
            // The badge flags the *other* parameter, i.e. the one currently hidden behind the shared
            // widget, so a non-default value there is never silently forgotten.
            bool otherNonDefault = _activeParam == GainGammaParam.Gain
                ? !_recipe.Gamma.Equals(DefaultGamma)
                : !_recipe.Gain.Equals(DefaultGain);
            _gainGammaBadge.IsVisible = otherNonDefault;
            string tip = $"Gain={FormatGain(_recipe.Gain)}\nGamma={FormatGamma(_recipe.Gamma)}";
            ToolTip.SetTip(_gainGammaBtn, tip);
        }

        /// <summary>Switches which parameter the shared slider+box edits, and refreshes them to match.</summary>
        private void SwitchActiveParam(GainGammaParam param)
        {
            if (_activeParam == param) return;
            _activeParam = param;
            _isUpdating = true;
            try
            {
                _gainSlider.Minimum = ActiveMinimum;
                _gainSlider.Maximum = ActiveMax;
                _gainSlider.Value = ActiveValue;
                _gainBox.Text = FormatActive(ActiveValue);
            }
            finally { _isUpdating = false; }
            UpdateGainGammaLabel();
            UpdateGainSliderToolTip();
        }

        private void OpenGainGammaMenu()
        {
            var menu = _gainGammaBtn.ContextMenu;
            if (menu == null) return;
            menu.Items.Clear();

            void AddItem(GainGammaParam param, string label, string valueText)
            {
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new TextBlock
                {
                    Text = label,
                    FontSize = BaseFontSize,
                    MinWidth = 44,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                header.Children.Add(new TextBlock
                {
                    Text = valueText,
                    FontSize = BaseFontSize - 1,
                    Foreground = new SolidColorBrush(Color.FromRgb(140, 140, 140)),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => SwitchActiveParam(param);
                menu.Items.Add(item);
            }

            AddItem(GainGammaParam.Gain, "Gain", FormatGain(_recipe.Gain));
            AddItem(GainGammaParam.Gamma, "Gamma", FormatGamma(_recipe.Gamma));

            menu.PlacementTarget = _gainGammaBtn;
            menu.Placement = PlacementMode.AnchorAndGravity;
            menu.PlacementAnchor = PopupAnchor.BottomLeft;
            menu.PlacementGravity = PopupGravity.BottomRight;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.Open(_gainGammaBtn);
        }

        /// <summary>
        /// Greys out and disables everything except the visibility toggle, so a hidden channel can
        /// always be switched back on from its own row.
        /// </summary>
        private void ApplyContentEnabledState(bool visible)
        {
            foreach (var c in _contentControls)
            {
                // The range bar additionally obeys the Global/Channel-wise scope.
                c.IsEnabled = visible && (!ReferenceEquals(c, _rangeBar) || _rangeEnabled);
                c.Opacity = visible ? 1.0 : 0.45;
            }
        }

        private static void ConfigureCompactSlider(Slider slider)
        {
            slider.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find("thumb") is Thumb thumb)
                {
                    thumb.MinWidth = 0;
                    thumb.MinHeight = 0;
                    thumb.Width = 12;
                    thumb.Height = 12;
                }
                if (e.NameScope.Find("PART_Track") is Track track)
                {
                    track.MinHeight = 2;
                    track.Height = 2;
                    if (track.IncreaseButton is Control inc) inc.Height = 1;
                    if (track.DecreaseButton is Control dec) dec.Height = 1;
                }
            };
        }

        /// <summary>
        /// Builds a 256-entry black→channel-colour gradient LUT so the histogram is tinted with the
        /// channel's own colour, matching how that channel appears in the composite.
        /// </summary>
        internal static int[] BuildChannelGradientLut(Color baseColor)
        {
            var lut = new int[256];
            for (int i = 0; i < 256; i++)
            {
                var c = Color.FromArgb(255,
                    (byte)(baseColor.R * i / 255),
                    (byte)(baseColor.G * i / 255),
                    (byte)(baseColor.B * i / 255));
                lut[i] = unchecked((int)c.ToUInt32());
            }
            return lut;
        }

        internal static Color ToColor(int argb) => Color.FromUInt32(unchecked((uint)argb));
        internal static int ToArgb(Color color) => unchecked((int)color.ToUInt32());

        private static string FormatGain(double g) => $"{g:F1}x";
    }
}
