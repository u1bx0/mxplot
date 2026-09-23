using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Animates the rows of an <see cref="ItemsControl"/> from where they were to where a reordering
    /// has just put them, so items slide past each other instead of jumping to their new positions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Works by the standard "first, last, invert, play" approach: record each row's position, let the
    /// collection change, and once the panel has laid the rows out again, offset every row back to
    /// where it was and ease that offset away. Only rows with a realized container take part —
    /// anything scrolled out of view has nothing to animate and simply appears in its new place.
    /// </para>
    /// <para>
    /// The offsets are interpolated by a timer that assigns to each row's
    /// <see cref="TranslateTransform"/> directly, rather than through Avalonia's keyframe animations.
    /// A keyframe animation needs an animator registered for the property it drives — there is none
    /// for a transform-typed property such as <c>RenderTransform</c> — and retargeting it at the
    /// transform object instead leaves it with no clock to advance it, which strands every row at its
    /// starting offset. Assigning the interpolated value is the one form that cannot fail that way.
    /// </para>
    /// <para>
    /// The animation always ends by clearing the transform, so the worst a mistimed measurement can
    /// produce is an odd-looking slide: rows cannot be left displaced from their layout positions.
    /// </para>
    /// </remarks>
    internal sealed class ListReorderAnimator
    {
        private readonly ItemsControl _host;
        private readonly List<Row> _rows = [];
        private readonly Stopwatch _elapsed = new();
        private DispatcherTimer? _timer;

        /// <summary>
        /// Handler waiting for the layout pass that follows a reorder. Dropped if another reorder
        /// arrives first, so a snapshot can never be measured against a layout it does not describe.
        /// </summary>
        private EventHandler? _pendingLayout;

        private readonly record struct Row(Control Container, object Item, TranslateTransform Offset, double Dx, double Dy);

        public ListReorderAnimator(ItemsControl host) => _host = host;

        /// <summary>
        /// Runs <paramref name="reorder"/> and animates the rows it moved. The mutation is applied
        /// exactly once whether or not anything can be animated.
        /// </summary>
        public void Run(Action reorder)
        {
            // A reorder arriving mid-slide takes over: the rows in flight are put back where their
            // layout says they belong, so this pass measures settled positions.
            Finish();

            var before = CapturePositions();
            if (before.Count == 0)
            {
                reorder();
                return;
            }

            void OnLayoutUpdated(object? sender, EventArgs e)
            {
                _host.LayoutUpdated -= OnLayoutUpdated;
                _pendingLayout = null;
                PlayFrom(before);
            }

            // LayoutUpdated fires once the panel has arranged the rows in their new order, which is
            // the first moment their final positions can be read.
            _pendingLayout = OnLayoutUpdated;
            _host.LayoutUpdated += OnLayoutUpdated;
            reorder();
        }

        private Dictionary<object, Point> CapturePositions()
        {
            var positions = new Dictionary<object, Point>();
            foreach (var container in _host.GetRealizedContainers())
            {
                if (container.DataContext is not { } item) continue;
                if (container.TranslatePoint(default, _host) is not { } origin) continue;
                positions[item] = origin;
            }
            return positions;
        }

        private void PlayFrom(IReadOnlyDictionary<object, Point> before)
        {
            foreach (var container in _host.GetRealizedContainers())
            {
                if (container.DataContext is not { } item) continue;
                if (!before.TryGetValue(item, out var from)) continue;
                if (container.TranslatePoint(default, _host) is not { } to) continue;

                double dx = from.X - to.X;
                double dy = from.Y - to.Y;

                // Sub-pixel shifts are layout noise, not a reorder.
                if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

                var offset = new TranslateTransform(dx, dy);
                container.RenderTransform = offset;
                _rows.Add(new Row(container, item, offset, dx, dy));
            }

            if (_rows.Count == 0) return;

            _timer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
            _elapsed.Restart();
            _timer.Start();
        }

        private void Tick()
        {
            double progress = Math.Clamp(_elapsed.Elapsed.TotalMilliseconds / Motion.Base.TotalMilliseconds, 0, 1);

            // Cubic ease-out: fast at the start, settling into place at the end.
            double eased = 1 - Math.Pow(1 - progress, 3);
            double remaining = 1 - eased;

            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                var row = _rows[i];

                // A window closing mid-slide hands its container to another item. Displacing that
                // item would be wrong, so the row drops out and its transform is taken off at once.
                if (!ReferenceEquals(row.Container.DataContext, row.Item))
                {
                    if (ReferenceEquals(row.Container.RenderTransform, row.Offset))
                        row.Container.RenderTransform = null;
                    _rows.RemoveAt(i);
                    continue;
                }

                row.Offset.X = row.Dx * remaining;
                row.Offset.Y = row.Dy * remaining;
            }

            if (progress >= 1 || _rows.Count == 0) Finish();
        }

        /// <summary>Ends any slide in progress, leaving every row at its own layout position.</summary>
        private void Finish()
        {
            _timer?.Stop();
            _elapsed.Reset();

            if (_pendingLayout is not null)
            {
                _host.LayoutUpdated -= _pendingLayout;
                _pendingLayout = null;
            }

            foreach (var row in _rows)
            {
                // Only clear what this animation put there, in case something else has since
                // assigned a transform to the same container.
                if (ReferenceEquals(row.Container.RenderTransform, row.Offset))
                    row.Container.RenderTransform = null;
            }
            _rows.Clear();
        }
    }
}
