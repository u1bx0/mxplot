using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Lets the rows of a <see cref="ListBox"/> be dragged to a new position: the dragged card follows
    /// the cursor, the list parts at the drop position, and a line marks the seam the card will drop
    /// into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list may be a flat projection of a tree, as long as each item's children follow it
    /// immediately. Reordering is then confined to one sibling group and an item moves together with
    /// its whole subtree, so a child can never be dropped among another parent's children. Items are
    /// handled as plain objects; the hierarchy is read through the <c>getParent</c> delegate and the
    /// move itself is left to <c>applyDrop</c>, which receives the dragged item and the gap index it
    /// was dropped into.
    /// </para>
    /// <para>
    /// Built on raw pointer events rather than Avalonia's <c>DragDrop</c>, for three reasons:
    /// DragDrop carries no drag image, so the dragged card could not be shown following the cursor,
    /// which is the whole point of this interaction; a host that already uses DragDrop as a file-drop
    /// target would have to tell internal drags apart in every one of those handlers; and DragDrop's
    /// nested OS drag loop does not mix with a tunnelled PointerPressed handler on the same list.
    /// </para>
    /// </remarks>
    internal sealed class ListDragReorder
    {
        /// <summary>Pointer travel, in DIPs, before a press turns into a drag.</summary>
        private const double DragStartThreshold = 5.0;

        /// <summary>Height of the top/bottom band that auto-scrolls during a drag.</summary>
        private const double AutoScrollBand = 28.0;

        /// <summary>Maximum auto-scroll speed in DIPs per tick, reached at the very edge of the band.</summary>
        private const double AutoScrollMaxStep = 14.0;

        /// <summary>
        /// Transparent gutter around the dragged card, wide enough to contain its drop shadow and
        /// its scale-up so that nothing is painted outside the ghost's own bounds.
        /// </summary>
        private const double GhostShadowPad = 16.0;

        /// <summary>How far the list parts at the drop position, in DIPs. Not used when wrapped.</summary>
        private const double DropGapSize = 10.0;

        /// <summary>Thickness of the drop indicator line, in DIPs.</summary>
        private const double IndicatorThickness = 2.0;

        /// <summary>Opacity the source card is dimmed to while its ghost is being dragged.</summary>
        private const double SourceDimOpacity = 0.35;

        private readonly ListBox _list;
        private readonly Canvas _overlay;
        private readonly Func<bool> _canStart;
        private readonly Func<object, object?> _getParent;
        private readonly Func<bool> _isWrapped;
        private readonly Action<object, int> _applyDrop;

        /// <summary>The item being dragged, or <see langword="null"/> when no drag is armed or running.</summary>
        private object? _dragItem;

        /// <summary>
        /// The container the drag started on. Read once at pickup, for the ghost's size, padding and
        /// grab offset; never consulted afterwards, because the virtualizing panel may recycle it
        /// onto a different item while the drag is in progress.
        /// </summary>
        private ListBoxItem? _dragContainer;

        private Control? _dragGhost;
        private Rectangle? _dropIndicator;

        /// <summary>Size of the dragged card itself, without the ghost's shadow gutter.</summary>
        private Size _dragCardSize;

        /// <summary>
        /// The sibling whose container currently carries the drop gap, or <see langword="null"/> when
        /// no gap is applied. The gap sits above it, unless <see cref="_gapBelowTarget"/> is set.
        /// </summary>
        private object? _gapTarget;

        /// <summary>Whether <see cref="_gapTarget"/> carries the gap below it (drop at the very end).</summary>
        private bool _gapBelowTarget;

        /// <summary>Press position in overlay coordinates, used for the movement threshold.</summary>
        private Point _dragPressPoint;

        /// <summary>
        /// Offset from the pointer to the dragged card's top-left corner, captured at pickup and held
        /// for the whole drag so the card stays grabbed at the point it was picked up by.
        /// </summary>
        private Point _dragGrabOffset;

        private bool _dragArmed;

        /// <summary>
        /// Last pointer position in overlay coordinates. Auto-scroll re-evaluates the drop target
        /// from it, since the list keeps moving under a pointer that is being held still.
        /// </summary>
        private Point _lastDragPointer;

        /// <summary>Gap index within the dragged item's sibling group that the pointer addresses.</summary>
        private int _dropSlot = -1;

        private DispatcherTimer? _autoScrollTimer;
        private double _autoScrollStep;

        /// <summary>The captured pointer, watched so the drag can bail out if the capture is taken away.</summary>
        private IPointer? _dragPointer;

        /// <param name="list">The list whose rows can be reordered.</param>
        /// <param name="overlay">
        /// A non-hit-testable <see cref="Canvas"/> covering the list, which the dragged card's ghost
        /// and the drop indicator are placed on. All positions in this class are in its coordinates.
        /// </param>
        /// <param name="canStart">
        /// Asked on every press: whether a drag may begin at all. Lets the host freeze reordering
        /// while it is in a mode of its own.
        /// </param>
        /// <param name="getParent">
        /// The parent of an item in the list's hierarchy, or <see langword="null"/> for a root-level
        /// item. A flat list returns <see langword="null"/> throughout.
        /// </param>
        /// <param name="isWrapped">
        /// Whether the list currently lays its items out in wrapped rows rather than a single column.
        /// The drop position is then decided across each row, and no gap is opened, since a margin
        /// would change where the rows wrap.
        /// </param>
        /// <param name="applyDrop">
        /// Applies the drop: moves the given item to the given gap index within its own sibling group,
        /// counted with the item still in place (<c>0</c> before the first sibling, <c>Count</c> after
        /// the last). Gaps either side of the item itself are not reported, so this is never called
        /// for a move that would change nothing.
        /// </param>
        public ListDragReorder(
            ListBox list,
            Canvas overlay,
            Func<bool> canStart,
            Func<object, object?> getParent,
            Func<bool> isWrapped,
            Action<object, int> applyDrop)
        {
            _list = list;
            _overlay = overlay;
            _canStart = canStart;
            _getParent = getParent;
            _isWrapped = isWrapped;
            _applyDrop = applyDrop;

            // Handled on the tunnelling route so they are seen before ListBoxItem consumes them
            // for selection.
            _list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            _list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
            _list.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);

            // PointerCaptureLost is deliberately not subscribed. Whether it arrives before or after
            // PointerReleased is not guaranteed, and a cancel running first would clear the dragged
            // item and the drop slot before the release could commit them. A lost capture is picked
            // up by the auto-scroll tick instead, which already runs for the whole drag.
        }

        /// <summary>Whether a card is currently being dragged.</summary>
        public bool IsDragging { get; private set; }

        /// <summary>
        /// Abandons the drag in progress, leaving the order unchanged. For the host to call on Escape
        /// or on anything else that should interrupt it.
        /// </summary>
        public void Cancel()
        {
            if (!IsDragging && !_dragArmed) return;
            _dragArmed = false;
            if (IsDragging) End(null, animateGapClose: true);
        }

        // ── Gesture ───────────────────────────────────────────────────────

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            _dragArmed = false;
            if (!_canStart()) return;
            if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;

            var container = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (container?.DataContext is not { } item) return;

            // An only child has nowhere to go: reordering is confined to one sibling group.
            if (Siblings(item).Count < 2) return;

            _dragItem = item;
            _dragContainer = container;
            _dragPressPoint = e.GetPosition(_overlay);
            _dragArmed = true;
        }

        private void OnMoved(object? sender, PointerEventArgs e)
        {
            if (!_dragArmed && !IsDragging) return;

            var p = e.GetPosition(_overlay);

            if (!IsDragging)
            {
                if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
                {
                    _dragArmed = false;
                    return;
                }

                // The threshold keeps plain clicks intact: selection, whatever the host does with a
                // click on an already selected row, and double-taps all still work.
                if (Math.Abs(p.X - _dragPressPoint.X) < DragStartThreshold &&
                    Math.Abs(p.Y - _dragPressPoint.Y) < DragStartThreshold)
                    return;

                if (!Begin(e)) { _dragArmed = false; return; }
            }

            Update(p);
            e.Handled = true;
        }

        private void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            _dragArmed = false;
            if (!IsDragging) return;

            // Commit before releasing the capture, never after: Capture(null) raises
            // PointerCaptureLost synchronously, and anything cancelling on it would clear the dragged
            // item and the drop slot out from under the commit below.
            Commit();
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        // ── Drag lifecycle ────────────────────────────────────────────────

        private bool Begin(PointerEventArgs e)
        {
            if (_dragItem is null || _dragContainer is null) return false;

            var origin = _dragContainer.TranslatePoint(default, _overlay);
            if (origin is null) return false;

            var bounds = new Rect(origin.Value, _dragContainer.Bounds.Size);
            _dragGrabOffset = _dragPressPoint - bounds.Position;
            _dragCardSize = bounds.Size;

            _dragGhost = CreateGhost(_dragItem, bounds.Size);
            _overlay.Children.Add(_dragGhost);

            _dropIndicator = CreateIndicator();
            _overlay.Children.Add(_dropIndicator);

            // The source card stays in place, only dimmed: removing it would reflow the list the
            // moment the drag starts, so the card would appear to jump out of a collapsing gap.
            ApplyDecorations();

            // Captured on the list, not on the container: the virtualizing panel is free to recycle
            // the container while the list auto-scrolls under the pointer.
            e.Pointer.Capture(_list);
            _dragPointer = e.Pointer;

            IsDragging = true;
            StartAutoScroll();
            return true;
        }

        private void Update(Point pointer)
        {
            if (_dragGhost is null) return;

            _lastDragPointer = pointer;

            // The ghost's own origin is the shadow gutter's corner, one pad up and left of the card.
            var topLeft = pointer - _dragGrabOffset;
            Canvas.SetLeft(_dragGhost, topLeft.X - GhostShadowPad);
            Canvas.SetTop(_dragGhost, topLeft.Y - GhostShadowPad);

            RefreshDropTarget();
            UpdateAutoScrollSpeed(pointer);
        }

        private void RefreshDropTarget()
        {
            var (slot, gap) = ResolveDropSlot(_lastDragPointer);
            _dropSlot = slot;
            UpdateGapTarget(slot);
            UpdateIndicator(gap);
            ApplyDecorations();
        }

        private void Commit()
        {
            var item = _dragItem;
            int slot = _dropSlot;
            var indicator = CurrentIndicatorRect();
            Rect? target = indicator.Width > 0 || indicator.Height > 0 ? indicator : null;

            End(target, animateGapClose: false);

            if (item is not null && slot >= 0)
                _applyDrop(item, slot);
        }

        /// <summary>
        /// Tears the drag down. When <paramref name="landing"/> is given, the ghost first glides to
        /// that rectangle while fading out, so the card reads as being set down where the indicator
        /// stood rather than vanishing from under the cursor.
        /// </summary>
        private void End(Rect? landing, bool animateGapClose)
        {
            IsDragging = false;
            StopAutoScroll();
            ClearDecorations(animateGapClose);

            if (_dropIndicator is not null)
            {
                _overlay.Children.Remove(_dropIndicator);
                _dropIndicator = null;
            }

            if (_dragGhost is { } ghost)
            {
                if (landing is { } rect)
                {
                    var duration = Motion.Base;
                    ghost.Transitions = new Transitions
                    {
                        new DoubleTransition { Property = Canvas.LeftProperty, Duration = duration, Easing = Motion.Land },
                        new DoubleTransition { Property = Canvas.TopProperty, Duration = duration, Easing = Motion.Land },
                        new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = Motion.Travel },
                    };

                    // Centre the card on the indicator line: across it for a horizontal line, along
                    // it for a vertical one. Positions are the ghost's own origin, so the shadow
                    // gutter is taken off both axes.
                    bool wrapped = _isWrapped();
                    double cardX = wrapped ? rect.X - _dragCardSize.Width / 2 : rect.X;
                    double cardY = wrapped ? rect.Y : rect.Y - _dragCardSize.Height / 2;
                    Canvas.SetLeft(ghost, cardX - GhostShadowPad);
                    Canvas.SetTop(ghost, cardY - GhostShadowPad);
                    ghost.Opacity = 0;

                    DispatcherTimer.RunOnce(
                        () => _overlay.Children.Remove(ghost),
                        duration + TimeSpan.FromMilliseconds(20));
                }
                else
                {
                    _overlay.Children.Remove(ghost);
                }
                _dragGhost = null;
            }

            _dragItem = null;
            _dragContainer = null;
            _dragPointer = null;
            _dropSlot = -1;
        }

        // ── Row decorations ───────────────────────────────────────────────

        /// <summary>
        /// Picks the sibling whose container should carry the drop gap for <paramref name="slot"/>:
        /// the one the drop would land in front of, or the last one (with the gap below it) when the
        /// drop lands at the end. A wrapped layout gets no gap.
        /// </summary>
        private void UpdateGapTarget(int slot)
        {
            _gapTarget = null;
            _gapBelowTarget = false;
            if (_isWrapped() || _dragItem is null || slot < 0) return;

            var siblings = Siblings(_dragItem);
            if (siblings.Count == 0) return;

            if (slot < siblings.Count)
            {
                _gapTarget = siblings[slot];
            }
            else
            {
                _gapTarget = siblings[^1];
                _gapBelowTarget = true;
            }
        }

        /// <summary>
        /// The index in the list of the row that carries the drop gap, or <c>-1</c>. A gap below a
        /// sibling goes under its last descendant, so a parent's children stay inside the block the
        /// gap is opening next to.
        /// </summary>
        private int ResolveGapRowIndex()
        {
            if (_gapTarget is null) return -1;

            int start = _list.Items.IndexOf(_gapTarget);
            if (start < 0 || !_gapBelowTarget) return start;

            int last = start;
            for (int i = start + 1; i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is not { } row) break;
                if (ReferenceEquals(SiblingAncestorOf(row, _getParent(_gapTarget)), _gapTarget)) last = i;
                else break;
            }
            return last;
        }

        /// <summary>
        /// Dims whichever container currently shows the dragged item and opens the drop gap on the
        /// container at the drop position, leaving every other realized container untouched.
        /// </summary>
        /// <remarks>
        /// Re-applied on every drag update rather than set once on the container the drag started on:
        /// the virtualizing panel recycles containers as the list auto-scrolls, so a one-shot
        /// decoration would travel to whatever item inherits that container. Values are compared
        /// before being assigned so an unchanged frame does not trigger a layout pass.
        /// </remarks>
        private void ApplyDecorations()
        {
            int gapRow = ResolveGapRowIndex();

            foreach (var container in _list.GetRealizedContainers())
            {
                double opacity = ReferenceEquals(container.DataContext, _dragItem) ? SourceDimOpacity : 1.0;
                if (container.Opacity != opacity) container.Opacity = opacity;

                var margin = gapRow >= 0 && _list.IndexFromContainer(container) == gapRow
                    ? (_gapBelowTarget
                        ? new Thickness(0, 0, 0, DropGapSize)
                        : new Thickness(0, DropGapSize, 0, 0))
                    : default;
                if (container.Margin != margin) container.Margin = margin;
            }
        }

        /// <summary>
        /// Removes the drag decorations. <paramref name="animate"/> closes the drop gap over the
        /// host's usual margin transition, which suits a cancelled drag.
        /// </summary>
        /// <remarks>
        /// A committed drop closes it instantly instead, because the host reorders the rows straight
        /// afterwards: an animating margin keeps laying the rows out for the whole of its duration,
        /// so anything measuring them to animate the reorder would catch them in mid-flight.
        /// </remarks>
        private void ClearDecorations(bool animate)
        {
            foreach (var container in _list.GetRealizedContainers())
            {
                container.Opacity = 1.0;

                if (animate)
                {
                    container.Margin = default;
                }
                else
                {
                    // A local null suppresses the style's transitions for this assignment only.
                    container.Transitions = null;
                    container.Margin = default;
                    container.ClearValue(Animatable.TransitionsProperty);
                }
            }
            _gapTarget = null;
            _gapBelowTarget = false;
        }

        // ── Ghost and indicator visuals ───────────────────────────────────

        /// <summary>
        /// Builds the card that follows the cursor. It is a fresh <see cref="ContentControl"/> bound
        /// to the same item template the list is showing, not a snapshot of the live container: the
        /// virtualizing panel may hand that container to another item mid-drag, which would make a
        /// live copy display the wrong row.
        /// </summary>
        /// <remarks>
        /// The card is wrapped in a transparent gutter of <see cref="GhostShadowPad"/> so that both
        /// the drop shadow and the slight scale-up stay inside the returned control's own bounds.
        /// Anything a moving control paints outside its bounds is not covered by the invalidated
        /// region, and is left behind on screen as a smear.
        /// </remarks>
        private Control CreateGhost(object item, Size size)
        {
            var card = new ContentControl
            {
                Content = item,
                ContentTemplate = _list.ItemTemplate,
                Padding = _dragContainer?.Padding ?? default,
                Width = size.Width,
                Height = size.Height,
                // Lifted slightly off the surface so it reads as held above the list.
                RenderTransform = new ScaleTransform(1.02, 1.02),
            };

            return new Border
            {
                Padding = new Thickness(GhostShadowPad),
                Background = Brushes.Transparent,
                Opacity = 0.9,
                IsHitTestVisible = false,
                Child = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    BoxShadow = BoxShadows.Parse("0 3 12 0 #59000000"),
                    Child = card,
                },
            };
        }

        private Rectangle CreateIndicator() => new()
        {
            Fill = AccentBrush(),
            RadiusX = 1,
            RadiusY = 1,
            IsHitTestVisible = false,
        };

        private IBrush AccentBrush() =>
            _list.TryFindResource("WindowListSelectedBorder", out var value) && value is IBrush brush
                ? brush
                : Brushes.DodgerBlue;

        private void UpdateIndicator(Rect gap)
        {
            if (_dropIndicator is null) return;

            bool empty = gap.Width <= 0 && gap.Height <= 0;
            _dropIndicator.IsVisible = !empty;
            if (empty) return;

            _dropIndicator.Width = gap.Width;
            _dropIndicator.Height = gap.Height;
            Canvas.SetLeft(_dropIndicator, gap.X);
            Canvas.SetTop(_dropIndicator, gap.Y);
        }

        private Rect CurrentIndicatorRect()
        {
            if (_dropIndicator is null || !_dropIndicator.IsVisible) return default;
            return new Rect(
                Canvas.GetLeft(_dropIndicator),
                Canvas.GetTop(_dropIndicator),
                _dropIndicator.Width,
                _dropIndicator.Height);
        }

        // ── Drop position ─────────────────────────────────────────────────

        /// <summary>
        /// The sibling group <paramref name="item"/> belongs to, in the order the list displays it:
        /// every item sharing its parent.
        /// </summary>
        private List<object> Siblings(object item)
        {
            var parent = _getParent(item);
            var siblings = new List<object>();
            for (int i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is not { } row) continue;
                if (ReferenceEquals(_getParent(row), parent)) siblings.Add(row);
            }
            return siblings;
        }

        /// <summary>
        /// Works out which gap of the dragged item's sibling group the pointer currently addresses,
        /// and the rectangle the drop indicator should occupy to mark it.
        /// </summary>
        /// <remarks>
        /// Each sibling is measured as the union of the realized containers of itself and its
        /// descendants, so a parent with children is treated as the single block it moves as.
        /// Siblings scrolled out of view have no realized container; they are placed relative to the
        /// pointer by comparing their position in the flat list against the realized range instead.
        /// <para>
        /// Measurement takes the drop gap back out of the rows it has pushed down, so the geometry
        /// the slot is decided from is the same whether or not a gap is currently open. Reading the
        /// gapped positions directly would let a pointer resting on a block boundary open a gap that
        /// moves the boundary past the pointer, which closes the gap again — an endless flicker.
        /// </para>
        /// </remarks>
        private (int Slot, Rect Gap) ResolveDropSlot(Point pointer)
        {
            if (_dragItem is null) return (-1, default);

            var siblings = Siblings(_dragItem);
            if (siblings.Count == 0) return (-1, default);

            // Rows from this index down are currently displaced by the open gap. A gap below its
            // target displaces only rows outside the sibling group, which are never measured here.
            int displacedFrom = _gapTarget is not null && !_gapBelowTarget
                ? _list.Items.IndexOf(_gapTarget)
                : -1;

            var parent = _getParent(_dragItem);
            var blocks = new Dictionary<object, Rect>();
            int firstRealized = int.MaxValue;

            foreach (var container in _list.GetRealizedContainers())
            {
                int index = _list.IndexFromContainer(container);
                if (index < 0 || index >= _list.Items.Count) continue;
                if (_list.Items[index] is not { } row) continue;

                firstRealized = Math.Min(firstRealized, index);

                if (SiblingAncestorOf(row, parent) is not { } owner) continue;
                if (container.TranslatePoint(default, _overlay) is not { } origin) continue;

                double undoGap = displacedFrom >= 0 && index >= displacedFrom ? DropGapSize : 0;
                var rect = new Rect(origin.X, origin.Y - undoGap,
                                    container.Bounds.Width, container.Bounds.Height);
                blocks[owner] = blocks.TryGetValue(owner, out var existing) ? existing.Union(rect) : rect;
            }

            if (blocks.Count == 0) return (-1, default);

            bool wrapped = _isWrapped();
            int slot = 0;
            foreach (var sibling in siblings)
            {
                if (blocks.TryGetValue(sibling, out var rect))
                {
                    if (IsPointerPastBlock(pointer, rect, wrapped)) slot++;
                }
                else
                {
                    // Off-screen: above the viewport counts as passed, below it does not.
                    int index = _list.Items.IndexOf(sibling);
                    if (index >= 0 && index < firstRealized) slot++;
                }
            }

            var line = GapRectFor(siblings, blocks, slot, wrapped);

            // The line is placed in un-gapped coordinates; centre it inside the gap that is about
            // to open there, so it reads as the seam the card drops into.
            if (!wrapped && line.Height > 0)
                line = line.WithY(line.Y + DropGapSize / 2);

            return (slot, line);
        }

        /// <summary>
        /// Walks up from <paramref name="item"/> to the ancestor that sits directly under
        /// <paramref name="parent"/> — i.e. the sibling whose block <paramref name="item"/> belongs to.
        /// Returns <see langword="null"/> when the item is in another branch entirely.
        /// </summary>
        private object? SiblingAncestorOf(object item, object? parent)
        {
            var current = item;
            while (current is not null)
            {
                if (ReferenceEquals(_getParent(current), parent)) return current;
                current = _getParent(current);
            }
            return null;
        }

        private static bool IsPointerPastBlock(Point pointer, Rect block, bool wrapped)
        {
            if (!wrapped) return pointer.Y > block.Center.Y;

            // Wrapped rows: anything below the row is past it; within the row, compare across.
            if (pointer.Y > block.Bottom) return true;
            if (pointer.Y < block.Top) return false;
            return pointer.X > block.Center.X;
        }

        /// <summary>
        /// The indicator rectangle for <paramref name="slot"/>: a thin line on the leading edge of
        /// the block that would follow the drop, or on the trailing edge of the last block when the
        /// drop lands at the end.
        /// </summary>
        private static Rect GapRectFor(
            List<object> siblings,
            Dictionary<object, Rect> blocks,
            int slot,
            bool wrapped)
        {
            for (int i = slot; i < siblings.Count; i++)
            {
                if (!blocks.TryGetValue(siblings[i], out var rect)) continue;
                return wrapped
                    ? new Rect(rect.X - IndicatorThickness / 2, rect.Y, IndicatorThickness, rect.Height)
                    : new Rect(rect.X, rect.Y - IndicatorThickness / 2, rect.Width, IndicatorThickness);
            }

            for (int i = Math.Min(slot, siblings.Count) - 1; i >= 0; i--)
            {
                if (!blocks.TryGetValue(siblings[i], out var rect)) continue;
                return wrapped
                    ? new Rect(rect.Right - IndicatorThickness / 2, rect.Y, IndicatorThickness, rect.Height)
                    : new Rect(rect.X, rect.Bottom - IndicatorThickness / 2, rect.Width, IndicatorThickness);
            }

            return default;
        }

        // ── Auto-scroll ───────────────────────────────────────────────────

        private void StartAutoScroll()
        {
            _autoScrollStep = 0;
            _autoScrollTimer ??= new DispatcherTimer(
                TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, (_, _) => AutoScrollTick());
            _autoScrollTimer.Start();
        }

        private void StopAutoScroll()
        {
            _autoScrollTimer?.Stop();
            _autoScrollStep = 0;
        }

        /// <summary>
        /// Sets the auto-scroll speed from how deeply the pointer has entered the band at the top or
        /// bottom edge of the list, so scrolling eases in rather than switching on at full speed.
        /// </summary>
        /// <remarks>
        /// Always the vertical axis, in both layouts: a wrapped list wraps into rows and therefore
        /// still scrolls downwards. Only the drop-position test differs between the two.
        /// </remarks>
        private void UpdateAutoScrollSpeed(Point pointer)
        {
            double position = pointer.Y;
            double extent = _overlay.Bounds.Height;

            if (position < AutoScrollBand)
                _autoScrollStep = -AutoScrollMaxStep * (1.0 - Math.Max(position, 0) / AutoScrollBand);
            else if (position > extent - AutoScrollBand)
                _autoScrollStep = AutoScrollMaxStep * (1.0 - Math.Max(extent - position, 0) / AutoScrollBand);
            else
                _autoScrollStep = 0;
        }

        private void AutoScrollTick()
        {
            if (!IsDragging) return;

            // Something else took the pointer (another window, a popup): drop the drag rather than
            // keep painting a ghost that no longer receives moves.
            if (!ReferenceEquals(_dragPointer?.Captured, _list))
            {
                Cancel();
                return;
            }

            // The drop target is re-evaluated every tick, not only on pointer moves: while
            // auto-scrolling, the list slides under a pointer that may be perfectly still.
            RefreshDropTarget();

            if (_autoScrollStep == 0) return;
            if (_list.Scroll is not { } scroll) return;

            var offset = scroll.Offset;
            double maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            var moved = new Vector(offset.X, Math.Clamp(offset.Y + _autoScrollStep, 0, maxY));

            if (moved == offset) return;
            scroll.Offset = moved;
        }
    }
}
