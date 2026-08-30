using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// A rotating conic-gradient ring ("comet tail") displayed while background work is in
    /// progress, grounded by a soft blurred shadow for legibility over any content. Set
    /// <see cref="IsActive"/> to <c>true</c> to start the animation. The indicator only becomes
    /// visible after <see cref="Delay"/> has elapsed, so fast operations never show a spinner.
    /// </summary>
    public sealed class BusyIndicator : Control
    {
        // No flat background disk (previous design). A crisp dark outline stroke behind the ring
        // was tried instead, but a hard-edged ring-within-a-ring read as an odd banded pattern --
        // a soft blurred shadow gives the same contrast against blown-out/white areas without the
        // hard edge, so it reads as grounding rather than a second ring.
        private static readonly DropShadowEffect s_shadow =
            new() { OffsetX = 0, OffsetY = 0, BlurRadius = 7, Color = Colors.Black, Opacity = 0.9 };

        // Same accent color as OverlayObjectBase.PenColor's default (cyan/turquoise) -- reuses
        // MxPlot's own overlay accent instead of picking an arbitrary hue, and avoids ImageJ's
        // yellow/magenta ROI convention (see RoiObject.AccentColor) so the two aren't confused.
        private static readonly Color s_accent = Color.FromRgb(0, 0xFF, 0xE0);

        private readonly ConicGradientBrush _ringBrush;
        private readonly Pen _ringPen;

        private double _angle;
        private DispatcherTimer? _spinTimer;
        private DispatcherTimer? _delayTimer;

        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<BusyIndicator, bool>(nameof(IsActive));

        /// <summary>
        /// Minimum elapsed time before the spinner becomes visible.
        /// Operations that complete within this window never show the indicator.
        /// </summary>
        public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(150);

        /// <summary>
        /// Tooltip text shown on hover while the indicator is visible. Follows the
        /// gerund + ellipsis convention used by <c>MatrixPlotter.BeginProgress</c>'s labels
        /// (e.g. "Processing…", "Exporting…") -- "Computing…" names what this indicator
        /// specifically covers (XZ/YZ side-view slice rebuilds), distinct from that generic default.
        /// </summary>
        public string Text
        {
            get => _text;
            set { _text = value; ToolTip.SetTip(this, value); }
        }
        private string _text = "Processing…";

        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public BusyIndicator()
        {
            Width  = 28;
            Height = 28;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment   = VerticalAlignment.Top;
            Margin = new Thickness(6);
            // Hit-testable only while actually shown (see StartSpin/StopSpin) so the tooltip can
            // appear on hover without this tiny corner control ever intercepting pointer input
            // (crosshair placement, etc.) while idle -- which is nearly all the time.
            IsHitTestVisible = false;
            IsVisible = false;
            Effect = s_shadow;
            ToolTip.SetTip(this, _text);

            // Bright head near the end of the sweep, fading back through a dim tail to fully
            // transparent at the seam (offset 0 and 1 both transparent, so the loop is seamless).
            _ringBrush = new ConicGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0, s_accent.R, s_accent.G, s_accent.B), 0.0),
                    new GradientStop(Color.FromArgb(70, s_accent.R, s_accent.G, s_accent.B), 0.75),
                    new GradientStop(Color.FromArgb(255, s_accent.R, s_accent.G, s_accent.B), 0.97),
                    new GradientStop(Color.FromArgb(0, s_accent.R, s_accent.G, s_accent.B), 1.0),
                },
            };
            _ringPen = new Pen(_ringBrush, 3, lineCap: PenLineCap.Round);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsActiveProperty)
            {
                if (IsActive) StartSpin();
                else          StopSpin();
            }
        }

        private void StartSpin()
        {
            // Don't show immediately — start a one-shot delay timer.
            // If StopSpin() is called before it fires, the spinner is never shown.
            if (_delayTimer == null)
            {
                _delayTimer = new DispatcherTimer();
                _delayTimer.Tick += OnDelayElapsed;
            }
            _delayTimer.Interval = Delay;
            _delayTimer.Start();
        }

        private void StopSpin()
        {
            _delayTimer?.Stop();
            _spinTimer?.Stop();
            IsVisible = false;
            IsHitTestVisible = false;
        }

        private void OnDelayElapsed(object? sender, EventArgs e)
        {
            _delayTimer!.Stop();          // one-shot
            if (!IsActive) return;        // cancelled in the meantime

            IsVisible = true;
            IsHitTestVisible = true;
            if (_spinTimer == null)
            {
                _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _spinTimer.Tick += OnTick;
            }
            _spinTimer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            _angle = (_angle + 6) % 360;
            _ringBrush.Angle = _angle;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            double size = Math.Min(Bounds.Width, Bounds.Height);
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
            double r = size / 2 - _ringPen.Thickness / 2 - 1;
            if (r <= 0) return;

            context.DrawEllipse(null, _ringPen, center, r, r);
        }
    }
}
