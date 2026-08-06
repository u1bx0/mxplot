using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// A compact horizontal tracker for a single <see cref="Axis"/>.
    /// Layout: [Name (fixed)] [══════Slider══════] [1/10 (editable)] [▶/■]
    /// <para>
    /// Changing the slider or indicator directly sets <see cref="Axis.Index"/>, which the
    /// <see cref="DimensionStructure"/> picks up and propagates to <c>MatrixData.ActiveIndex</c>.
    /// External changes via <see cref="Axis.IndexChanged"/> are reflected back to the UI.
    /// </para>
    /// </summary>
    public class AxisTracker : UserControl
    {
        // ── Dimensions ────────────────────────────────────────────────────────
        private const double LabelWidth = 50;
        private const double IndicatorWidth = 32;
        private const double ButtonSize = 22;
        private const double ComponentHeight = 20;
        private const int DefaultInterval = 100;   // ms

        private const double BaseFontSize = 11;

        // ── Model ─────────────────────────────────────────────────────────────
        private readonly Axis _axis;

        // ── Controls ──────────────────────────────────────────────────────────
        private readonly TextBlock _nameLabel;
        private readonly Slider _slider;
        private readonly TextBox _indicator;
        private readonly Button _playButton;

        // ── Animation ─────────────────────────────────────────────────────────
        private readonly DispatcherTimer _timer;
        private readonly Stopwatch _stopwatch = new();
        private long _lastTick;
        private double _frameRate;

        private readonly ToggleButton _freezeButton;

        // ── Re-entrancy guard ─────────────────────────────────────────────────
        private bool _isUpdating;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>The <see cref="Core.Axis"/> this tracker controls.</summary>
        public Axis Axis => _axis;

        /// <summary>The 🧊 orthogonal-view toggle button (right of the indicator).</summary>
        public ToggleButton FreezeButton => _freezeButton;

        /// <summary>Fired after every index change (0-based).</summary>
        public event EventHandler<int>? IndexChanged;

        /// <summary>Fired when the user starts dragging the slider thumb.</summary>
        public event EventHandler? SliderDragStarted;

        /// <summary>Fired when the user releases the slider thumb after dragging.</summary>
        public event EventHandler? SliderDragEnded;

        public bool IsAnimating => _timer.IsEnabled;
        public double DisplayFrameRate => IsAnimating ? _frameRate : 0;

        /// <summary>Animation timer interval in milliseconds.</summary>
        public int AnimationInterval
        {
            get => (int)_timer.Interval.TotalMilliseconds;
            set { if (value > 0) _timer.Interval = TimeSpan.FromMilliseconds(value); }
        }

       
        // ── Constructor ───────────────────────────────────────────────────────

        public AxisTracker(Axis axis)
        {
            _axis = axis ?? throw new ArgumentNullException(nameof(axis));

            // ── Name label ────────────────────────────────────────────────────
            _nameLabel = new TextBlock
            {
                Text = axis.Name,
                Width = LabelWidth,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                FontSize = BaseFontSize,
                Margin = new Thickness(0, 0, 6, 0),
                MinHeight = 0,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(_nameLabel, axis.Name);

            // ── Slider ────────────────────────────────────────────────────────
            _slider = new Slider
            {
                Minimum = 0,
                Maximum = axis.Count - 1,
                Value = axis.Index,
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 5, 0),
                MinHeight = 0,
                Height = ComponentHeight,
            };

            _slider.TemplateApplied += (_, e) =>
            {
                if (e.NameScope.Find("thumb") is Thumb thumb)
                {
                    // デフォルトの制約を解除して小さくする
                    thumb.MinWidth = 0;
                    thumb.MinHeight = 0;
                    thumb.Width = 12;
                    thumb.Height = 12;
                }

                if (e.NameScope.Find("PART_Track") is Track track)
                {
                    track.MinHeight = 2;
                    track.Height = 2;
                    if (track.IncreaseButton is Control ctlInc)
                    {
                        ctlInc.Height = 1;
                    }
                    if (track.DecreaseButton is Control ctlDec)
                    {
                        ctlDec.Height = 1;
                    }
                }
            };

            // ── Indicator (GotFocus = edit mode: shows 1-based index only;
            //              LostFocus = display mode: shows "N/Count") ───────────
            _indicator = new TextBox
            {
                Width = IndicatorWidth,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = BaseFontSize,
                Margin = new Thickness(5, 0, 0, 0),
                Padding = new Thickness(0, 0),
                MinHeight = ComponentHeight,
                Height = ComponentHeight,
            };

            // ── Play / Stop button ────────────────────────────────────────────
            _playButton = new Button
            {
                Width = ButtonSize,
                Height = ButtonSize,
                Content = "▶",
                FontSize = 10,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            // ── Freeze (orthogonal) button ─────────────────────────────────────
            _freezeButton = new ToggleButton
            {
                Content = new PathIcon
                {
                    Data = MenuIcons.Cube,
                    Width = 14,
                    Height = 14,
                    Foreground = MenuIcons.DefaultBrush(MenuIcons.Cube),
                },
                Width = 26,
                Height = 22,
                Padding = new Thickness(2),
                Margin = new Thickness(2, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(_freezeButton, $"Volume: XY-{axis.Name}");

            // ── Animation timer ───────────────────────────────────────────────
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DefaultInterval) };
            _timer.Tick += OnTimerTick;

            // ── Wire events ───────────────────────────────────────────────────
            _slider.ValueChanged += OnSliderValueChanged;
            _slider.AddHandler(PointerPressedEvent, OnSliderPointerPressed, handledEventsToo: true);
            _slider.AddHandler(PointerReleasedEvent, OnSliderPointerReleased, handledEventsToo: true);
            _slider.AddHandler(PointerWheelChangedEvent, OnSliderPointerWheelChanged, handledEventsToo: true);
            _indicator.GotFocus += OnIndicatorGotFocus;
            _indicator.LostFocus += OnIndicatorLostFocus;
            _indicator.KeyDown += OnIndicatorKeyDown;
            _playButton.Click += OnPlayButtonClick;
            _playButton.ContextMenu = BuildPlayButtonContextMenu();
            UpdatePlayButtonToolTip();
            _axis.IndexChanged += OnAxisIndexChanged;
            _axis.NameChanged += (_, _) =>
            {
                _nameLabel.Text = _axis.Name;
                ToolTip.SetTip(_nameLabel, _axis.Name);
                ToolTip.SetTip(_freezeButton, $"Volume: XY-{_axis.Name}");
            };

            // ── Function slot spacer (reserves column for specialized buttons) ──
            var funcSpacer = new Border { Width = ButtonSize, Height = ButtonSize };

            // ── Layout ────────────────────────────────────────────────────────
            var grid = new Grid { Margin = new Thickness(5, 0, 5, 0) };
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // Name
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // FuncSlot (reserved)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // Play/Stop
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // Slider
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // Indicator
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // Freeze

            Grid.SetColumn(_nameLabel, 0);
            Grid.SetColumn(funcSpacer, 1);
            Grid.SetColumn(_playButton, 2);
            Grid.SetColumn(_slider, 3);
            Grid.SetColumn(_indicator, 4);
            Grid.SetColumn(_freezeButton, 5);

            grid.Children.Add(_nameLabel);
            grid.Children.Add(funcSpacer);
            grid.Children.Add(_slider);
            grid.Children.Add(_indicator);
            grid.Children.Add(_playButton);
            grid.Children.Add(_freezeButton);

            Content = grid;
            UpdateIndicator();
        }

        // ── Core logic ────────────────────────────────────────────────────────

        /// <summary>
        /// Apply a 0-based index: updates <see cref="Axis.Index"/>, slider, and indicator atomically.
        /// </summary>
        private void ApplyIndex(int index)
        {
            index = Math.Clamp(index, 0, _axis.Count - 1);
            _isUpdating = true;
            try
            {
                _axis.Index = index;

                if ((int)Math.Round(_slider.Value) != index)
                    _slider.Value = index;

                UpdateIndicator();
                IndexChanged?.Invoke(this, index);
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void UpdateIndicator()
        {
            if (!_indicator.IsFocused)
                _indicator.Text = $"{_axis.Index + 1}/{_axis.Count}";
            ToolTip.SetTip(_indicator, BuildPositionText());
        }

        /// <summary>
        /// Builds the axis position string shown in the indicator tooltip and, while dragging,
        /// in the <see cref="MxView"/> top-right overlay.
        /// Format: <c>"Z: 12.500 um (8/100)"</c> for scaled axes, <c>"Z: 8/100"</c> for index-based.
        /// </summary>
        internal string BuildPositionText()
        {
            if (_axis is TaggedAxis tagAx)
                return $"{_axis.Name}: {tagAx.CurrentTag} ({_axis.Index + 1}/{_axis.Count})";
            else if (_axis.IsIndexBased)
                return $"{_axis.Name}: {_axis.Index + 1}/{_axis.Count}";

            double val = _axis.ValueAt(_axis.Index);
            string unit = string.IsNullOrEmpty(_axis.Unit) ? "" : $" {_axis.Unit}";
            return $"{_axis.Name}: {val:F4}{unit} ({_axis.Index + 1}/{_axis.Count})";
        }

        private void StopAnimation()
        {
            _timer.Stop();
            _stopwatch.Reset();
            _frameRate = 0;
            _playButton.Content = "▶";
        }

        // ── Event handlers ────────────────────────────────────────────────────

        /// <summary>User dragged the slider.</summary>
        private void OnSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isUpdating) return;
            ApplyIndex((int)Math.Round(e.NewValue));
        }

        private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(_slider).Properties.IsLeftButtonPressed)
                SliderDragStarted?.Invoke(this, EventArgs.Empty);
        }

        private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            SliderDragEnded?.Invoke(this, EventArgs.Empty);
        }

        private void OnSliderPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (_isUpdating) return;

            int delta = e.Delta.Y > 0 ? 1 : -1;
            int newIndex = Math.Clamp(_axis.Index + delta, 0, _axis.Count - 1);

            ApplyIndex(newIndex);
            e.Handled = true;
        }

        /// <summary>Axis.Index was changed externally (e.g., by DimensionStructure sync).</summary>
        private void OnAxisIndexChanged(object? sender, EventArgs e)
        {
            if (_isUpdating) return;
            _isUpdating = true;
            try
            {
                if ((int)Math.Round(_slider.Value) != _axis.Index)
                    _slider.Value = _axis.Index;
                UpdateIndicator();
            }
            finally { _isUpdating = false; }
        }

        /// <summary>GotFocus → switch to edit mode: show 1-based index only.</summary>
        private void OnIndicatorGotFocus(object? sender, GotFocusEventArgs e)
        {
            _indicator.Text = (_axis.Index + 1).ToString();
            Dispatcher.UIThread.Post(() => _indicator.SelectAll());
        }

        private void OnIndicatorLostFocus(object? sender, RoutedEventArgs e) => CommitIndicator();

        private void OnIndicatorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitIndicator();
                _slider.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                _indicator.Text = $"{_axis.Index + 1}/{_axis.Count}";
                _slider.Focus();
                e.Handled = true;
            }
        }

        private void CommitIndicator()
        {
            if (int.TryParse(_indicator.Text, out int parsed))
                ApplyIndex(Math.Clamp(parsed - 1, 0, _axis.Count - 1));   // 1-based → 0-based
            else
                UpdateIndicator();
        }

        private void OnPlayButtonClick(object? sender, RoutedEventArgs e)
        {
            if (_timer.IsEnabled)
            {
                StopAnimation();
            }
            else
            {
                _lastTick = 0;
                _stopwatch.Restart();
                _playButton.Content = "■";
                _timer.Start();
            }
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            var elapsed = _stopwatch.ElapsedMilliseconds;
            if (_lastTick > 0)
                _frameRate = 1000.0 / Math.Max(1, elapsed - _lastTick);
            _lastTick = elapsed;

            ApplyIndex((_axis.Index + 1) % _axis.Count);
        }

        // ── Play button context menu ──────────────────────────────────────────

        private ContextMenu BuildPlayButtonContextMenu()
        {
            var item = new MenuItem { Header = "Setup frame rate", FontSize = BaseFontSize };
            item.Click += async (_, _) => await ShowFrameRateDialogAsync();
            return new ContextMenu { Items = { item } };
        }

        private async Task ShowFrameRateDialogAsync()
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var result = await AnimationIntervalDialog.ShowAsync(owner, AnimationInterval);
            if (result.HasValue)
            {
                AnimationInterval = result.Value;
                UpdatePlayButtonToolTip();
            }
        }

        private void UpdatePlayButtonToolTip()
        {
            int ms = AnimationInterval;
            double fps = ms > 0 ? 1000.0 / ms : 0;
            ToolTip.SetTip(_playButton,
                $"Right-click to configure animation interval\n(Current: {ms} ms / {fps:F1} fps)");
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            StopAnimation();
            _axis.IndexChanged -= OnAxisIndexChanged;
        }
    }
}
