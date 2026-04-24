using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// A lightweight spinning-arc indicator displayed while background work is in progress.
    /// Set <see cref="IsActive"/> to <c>true</c> to start the animation.
    /// The indicator only becomes visible after <see cref="Delay"/> has elapsed,
    /// so fast operations never show a spinner.
    /// </summary>
    public sealed class BusyIndicator : Control
    {
        private static readonly IBrush s_bgBrush =
            new SolidColorBrush(Color.FromArgb(128, 0, 0, 0));
        private static readonly Pen s_arcPen =
            new(Brushes.White, 2, lineCap: PenLineCap.Round);

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
            IsHitTestVisible = false;
            IsVisible = false;
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
        }

        private void OnDelayElapsed(object? sender, EventArgs e)
        {
            _delayTimer!.Stop();          // one-shot
            if (!IsActive) return;        // cancelled in the meantime

            IsVisible = true;
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
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            double size = Math.Min(Bounds.Width, Bounds.Height);
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

            // Semi-transparent dark background circle
            context.DrawEllipse(s_bgBrush, null, center, size / 2, size / 2);

            // Spinning 270° arc
            double r = size / 2 - 4;
            if (r <= 0) return;

            double startRad = _angle * Math.PI / 180;
            double endRad   = (_angle + 270) * Math.PI / 180;

            var p0 = new Point(center.X + r * Math.Cos(startRad),
                               center.Y + r * Math.Sin(startRad));
            var p1 = new Point(center.X + r * Math.Cos(endRad),
                               center.Y + r * Math.Sin(endRad));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(p0, false);
                ctx.ArcTo(p1, new Size(r, r), 0, true, SweepDirection.Clockwise);
            }

            context.DrawGeometry(null, s_arcPen, geo);
        }
    }
}
