using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Manages user overlay objects for an Avalonia <see cref="Controls.RenderSurface"/>.
    /// Event handling is delegated from <see cref="Controls.MxView"/> via
    /// <see cref="OnPointerPressed"/>, <see cref="OnPointerMoved"/>, <see cref="OnPointerReleased"/>.
    /// Drawing is invoked from <c>RenderSurface.Render()</c> via <see cref="Draw"/>.
    /// </summary>
    public sealed class OverlayManager
    {
        // ── Dependencies (set by MxView) ──────────────────────────────────────

        /// <summary>Returns the current viewport snapshot for coordinate conversion.</summary>
        public Func<AvaloniaViewport>? GetViewport { get; set; }

        /// <summary>Called to request a redraw of the host surface.</summary>
        public Action InvalidateVisual { get; set; } = () => { };

        /// <summary>Called to change the host surface cursor.</summary>
        public Action<Cursor> SetCursor { get; set; } = _ => { };

        /// <summary>Sets clipboard text content. Set by the host control.</summary>
        public Func<string, System.Threading.Tasks.Task>? SetClipboardText { get; set; }

        /// <summary>Gets clipboard text content. Set by the host control.</summary>
        public Func<System.Threading.Tasks.Task<string?>>? GetClipboardText { get; set; }

        // ── State ─────────────────────────────────────────────────────────────

        private readonly List<OverlayObjectBase> _objects = new();
        private readonly PixelSnapService _snap = new();
        private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

        private OverlayObjectBase? _activeObj;
        private HandleType _activeHandle = HandleType.None;
        private Point _lastMouseWorldPos;
        private bool _isDragging;
        private bool _overlaysVisible = true;

        private OverlayObjectBase? _ghostObject;
        private Point _creationStartPos;
        private bool _isShiftDown;
        private bool _isCtrlDown;
        private Point _lastDragSnappedPos;
        private Point _dragBodyStartWorldPos;
        private Point _appliedBodyDelta;

        /// <summary>Sticky Shift/Ctrl modifier state as tracked by OnKeyDown/OnKeyUp. Exposed for diagnostic display.</summary>
        public KeyModifiers TrackedModifiers =>
            (_isShiftDown ? KeyModifiers.Shift : KeyModifiers.None) |
            (_isCtrlDown  ? KeyModifiers.Control : KeyModifiers.None);

        /// <summary>Current interaction state as a short string for the debug overlay.</summary>
        internal string DragStateText =>
            _ghostObject != null ? "OVL:CRT" :
            _isDragging          ? "OVL:DRG" : "\u2014";

        // ── Public API ────────────────────────────────────────────────────────
        public bool OverlaysVisible
        {
            get => _overlaysVisible;
            set
            {
                if (_overlaysVisible == value) return;
                _overlaysVisible = value;
                InvalidateVisual();
            }
        }

        /// <summary>Read-only view of the current overlay objects.</summary>
        public IReadOnlyList<OverlayObjectBase> Objects => _objects;

        public void StartCreating(OverlayObjectBase template)
        {
            ClearSelection();
            _ghostObject = template;
            SetCursor(new Cursor(StandardCursorType.Cross));
            InvalidateVisual();
        }

        /// <summary>Raised after an overlay object has been added to <see cref="Objects"/>.</summary>
        public event EventHandler<OverlayObjectBase>? ObjectAdded;

        /// <summary>Raised after an overlay object has been removed from <see cref="Objects"/>.</summary>
        public event EventHandler<OverlayObjectBase>? ObjectRemoved;

        /// <summary>
        /// Raised during creation drag whenever the ghost object's bounds change.
        /// The argument is the ghost object; call <see cref="OverlayObjectBase.GetInfo"/> to obtain display text.
        /// </summary>
        public event EventHandler<OverlayObjectBase>? GhostUpdated;

        /// <summary>
        /// Raised when creation is cancelled or the ghost is discarded without becoming an object
        /// (Escape key, <see cref="Cancel"/> call, or rubber-band selection release).
        /// </summary>
        public event EventHandler? GhostCancelled;

        /// <summary>Raised after overlay objects are successfully copied to the clipboard.</summary>
        public event EventHandler? Copied;

        public void AddObject(OverlayObjectBase obj, bool invalidate = true)
        {
            _objects.Add(obj);
            obj.DeleteRequested += OnDeleteRequested;
            obj.CopyRequested += OnCopyOverlay;
            obj.PasteRequested += OnPasteOverlay;
            ObjectAdded?.Invoke(this, obj);
            if (invalidate) InvalidateVisual();
            Debug.WriteLine($"[OverlayManager] AddObject total={_objects.Count}");
        }

        public void RemoveObject(OverlayObjectBase obj)
        {
            if (!_objects.Remove(obj)) return;
            obj.DeleteRequested -= OnDeleteRequested;
            obj.CopyRequested -= OnCopyOverlay;
            obj.PasteRequested -= OnPasteOverlay;
            if (_activeObj == obj)
            {
                _activeObj = null;
                _activeHandle = HandleType.None;
                _isDragging = false;
            }
            ObjectRemoved?.Invoke(this, obj);
            InvalidateVisual();
        }

        public void ClearAll()
        {
            var snapshot = _objects.ToArray();
            _objects.Clear();
            _ghostObject = null;
            foreach (var obj in snapshot)
                ObjectRemoved?.Invoke(this, obj);
            InvalidateVisual();
        }

        public void ClearSelection()
        {
            foreach (var o in _objects) o.IsSelected = false;
            _activeObj = null;
            _activeHandle = HandleType.None;
            InvalidateVisual();
        }

        // ── Serialization ─────────────────────────────────────────────────────

        /// <summary>Serializes all user overlays (excluding system overlays) to a JSON string.</summary>
        public string SerializeOverlays() => OverlaySerializer.Serialize(_objects);

        /// <summary>Serializes only the currently selected user overlays to a JSON string.</summary>
        public string SerializeSelected() =>
            OverlaySerializer.Serialize(_objects.Where(o => o.IsSelected));

        /// <summary>
        /// Loads overlay objects from a JSON string and adds them to the object list.
        /// When <paramref name="clearExisting"/> is <c>true</c>, existing user overlays are removed first.
        /// </summary>
        public void LoadOverlays(string json, bool clearExisting = true)
        {
            if (clearExisting)
            {
                var userObjs = _objects.Where(o => o is not ISystemOverlay).ToArray();
                foreach (var obj in userObjs) RemoveObject(obj);
            }
            foreach (var obj in OverlaySerializer.Deserialize(json))
                AddObject(obj, invalidate: false);
            InvalidateVisual();
        }

        public void Cancel()
        {
            bool redraw = false;
            if (_ghostObject != null)
            {
                _ghostObject = null;
                _isDragging = false;
                SetCursor(Cursor.Default);
                GhostCancelled?.Invoke(this, EventArgs.Empty);
                redraw = true;
            }
            else if (_isDragging)
            {
                _isDragging = false;
                _activeObj = null;
                SetCursor(Cursor.Default);
                redraw = true;
            }
            else if (_objects.Any(o => o.IsSelected))
            {
                ClearSelection();
                return;
            }
            if (redraw) InvalidateVisual();
        }

        public void DeleteSelected()
        {
            var toRemove = _objects.Where(o => o.IsSelected && o.IsDeletable).ToArray();
            if (toRemove.Length == 0) return;

            foreach (var obj in toRemove)
                _objects.Remove(obj);

            _activeObj = null;
            _activeHandle = HandleType.None;
            _isDragging = false;

            foreach (var obj in toRemove)
                ObjectRemoved?.Invoke(this, obj);

            InvalidateVisual();
        }

        // ── Off-screen capture support ────────────────────────────────────────

        /// <summary>
        /// Overrides overlay visibility for a single <see cref="Draw"/> call (for clipboard capture).
        /// Null = use <see cref="OverlaysVisible"/>. Call <see cref="EndCapture"/> after rendering.
        /// Does NOT call <see cref="InvalidateVisual"/>; caller is responsible for redraw.
        /// </summary>
        internal void BeginCapture(bool withOverlays) => _captureOverride = withOverlays;
        internal void EndCapture() => _captureOverride = null;

        private bool? _captureOverride;

        // ── Drawing ───────────────────────────────────────────────────────────

        public void Draw(DrawingContext ctx)
        {
            bool visible = _captureOverride ?? _overlaysVisible;
            bool isCapturing = _captureOverride.HasValue;
            var vp = GetViewport?.Invoke() ?? default;
            var mxG = new AvaloniaOverlayGraphics(ctx, vp);

            foreach (var obj in _objects)
            {
                if (!obj.Visible) continue;
                if (isCapturing && obj is ISystemOverlay) continue;
                if (!visible && obj is not ISystemOverlay) continue;
                obj.Draw(mxG);
            }

            if (!isCapturing) _ghostObject?.Draw(mxG);
        }

        /// <summary>
        /// Draws overlays using an explicitly supplied
        /// live viewport.
        /// where zoom/translation differ from the interactive display.
        /// </summary>
        internal void DrawWithViewport(DrawingContext ctx, AvaloniaViewport viewport)
        {
            bool visible = _captureOverride ?? _overlaysVisible;
            bool isCapturing = _captureOverride.HasValue;
            var mxG = new AvaloniaOverlayGraphics(ctx, viewport) { SuppressHandles = true };

            foreach (var obj in _objects)
            {
                if (!obj.Visible) continue;
                if (isCapturing && obj is ISystemOverlay) continue;
                if (!visible && obj is not ISystemOverlay) continue;
                obj.Draw(mxG);
            }

            if (!isCapturing) _ghostObject?.Draw(mxG);
        }

        // ── Context menu

        public IEnumerable<OverlayMenuEntry> GetContextMenuItems(Point screenPos)
        {
            var vp = GetViewport?.Invoke() ?? default;
            var target = _objects.LastOrDefault(o =>
            {
                if (!o.IsSelectable || !o.Visible) return false;
                if (!_overlaysVisible && o is not ISystemOverlay) return false;
                return o.HitTest(screenPos, vp) != HandleType.None;
            });

            if (target == null) yield break;

            foreach (var item in target.GetContextMenuItems() ?? [])
                yield return item;
        }

        // ── Pointer events ────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if the event was consumed (do not pan).
        /// Call from <c>RenderSurface.OnPointerPressed</c> before AxisIndicator / Crosshair handling.
        /// </summary>
        public bool OnPointerPressed(Point screenPos, bool isLeftButton, KeyModifiers modifiers, int clickCount = 1)
        {
            if (!isLeftButton) return false;
            var vp = GetViewport?.Invoke() ?? default;
            var rawWorld = vp.ScreenToWorld(screenPos);

            // A. Double-click → open editor on hit object
            if (clickCount >= 2 && _ghostObject == null)
            {
                foreach (var obj in Enumerable.Reverse(_objects))
                {
                    if (!obj.IsSelectable || !obj.Visible) continue;
                    if (!_overlaysVisible && obj is not ISystemOverlay) continue;
                    if (obj.HitTest(screenPos, vp) != HandleType.None)
                    {
                        obj.OnDoubleClicked();
                        return true;
                    }
                }
                return false;
            }

            // B. Creation mode
            if (_ghostObject != null)
            {
                var snapped = _snap.Snap(rawWorld, _ghostObject.SnapMode);
                _creationStartPos = snapped;
                _lastDragSnappedPos = snapped;
                _isDragging = true;
                _ghostObject.IsSelected = true;
                _ghostObject.SetCreationBounds(snapped, snapped);
                GhostUpdated?.Invoke(this, _ghostObject);
                InvalidateVisual();
                return true;
            }

            // B. Hit test
            foreach (var obj in Enumerable.Reverse(_objects))
            {
                if (!obj.IsSelectable || !obj.Visible) continue;
                if (!_overlaysVisible && obj is not ISystemOverlay) continue;
                var handle = obj.HitTest(screenPos, vp);
                if (handle == HandleType.None) continue;

                // Ctrl held → toggle this object without clearing others
                if (modifiers.HasFlag(KeyModifiers.Control))
                {
                    obj.IsSelected = !obj.IsSelected;
                    if (obj.IsSelected)
                    {
                        _activeObj = obj;
                        _activeHandle = handle;
                        if (!obj.IsLocked)
                        {
                            _lastMouseWorldPos = _snap.Snap(rawWorld, obj.SnapMode);
                            _isDragging = true;
                            if (handle != HandleType.Body)
                                obj.BeginResize();
                        }
                    }
                    else
                    {
                        // Deselected the active object — clear active tracking
                        if (_activeObj == obj) { _activeObj = null; _activeHandle = HandleType.None; }
                    }
                    InvalidateVisual();
                    return true;
                }

                // Body hit on already-selected object → drag all selected together
                if (handle == HandleType.Body && obj.IsSelected)
                {
                    _activeObj = obj;
                    _activeHandle = handle;
                    if (!obj.IsLocked)
                    {
                        _lastMouseWorldPos = _snap.Snap(rawWorld, obj.SnapMode);
                        _dragBodyStartWorldPos = _lastMouseWorldPos;
                        _appliedBodyDelta = default;
                        _isDragging = true;
                    }
                    InvalidateVisual();
                    return true;
                }

                // New object or resize handle → switch to single selection
                ClearSelection();
                obj.IsSelected = true;
                _activeObj = obj;
                _activeHandle = handle;

                if (!obj.IsLocked)
                {
                    _lastMouseWorldPos = _snap.Snap(rawWorld, obj.SnapMode);
                    _lastDragSnappedPos = _lastMouseWorldPos;
                    if (handle == HandleType.Body)
                    {
                        _dragBodyStartWorldPos = _lastMouseWorldPos;
                        _appliedBodyDelta = default;
                    }
                    _isDragging = true;

                    if (handle != HandleType.Body)
                        obj.BeginResize();
                }
                InvalidateVisual();
                return true;
            }

            // C. Miss → start rubber-band selection with Ctrl, otherwise deselect
            if (modifiers.HasFlag(KeyModifiers.Control))
            {
                // Begin rubber-band selection without clearing existing selection
                var sel = new SelectionRect();
                var snapped = _snap.Snap(rawWorld, sel.SnapMode);
                _ghostObject = sel;
                _creationStartPos = snapped;
                _isDragging = true;
                sel.SetCreationBounds(snapped, snapped);
                SetCursor(new Cursor(StandardCursorType.Cross));
                InvalidateVisual();
                return true;
            }

            ClearSelection();
            return false;
        }

        /// <summary>Returns <c>true</c> if the event was consumed (do not pan).</summary>
        public bool OnPointerMoved(Point screenPos, KeyModifiers modifiers)
        {
            // During pointer-capture drag, OnKeyDown may not fire for modifier-only
            // key presses (Avalonia/Parallels). Sync flags from pointer event modifiers.
            if (modifiers.HasFlag(KeyModifiers.Shift)) _isShiftDown = true;
            if (modifiers.HasFlag(KeyModifiers.Control)) _isCtrlDown = true;
            var vp = GetViewport?.Invoke() ?? default;
            var rawWorld = vp.ScreenToWorld(screenPos);

            if (_isDragging)
            {
                if (_ghostObject != null)
                {
                    SetCursor(new Cursor(StandardCursorType.Cross));
                    var snapped = _snap.Snap(rawWorld, _ghostObject.SnapMode);
                    _lastDragSnappedPos = snapped;
                    _ghostObject.SetCreationBounds(_creationStartPos, snapped, TrackedModifiers);
                    GhostUpdated?.Invoke(this, _ghostObject);
                }
                else if (_activeObj != null)
                {
                    var current = _snap.Snap(rawWorld, _activeObj.SnapMode);
                    if (_activeHandle == HandleType.Body)
                    {
                        // Compute total displacement from drag start for absolute positioning.
                        // This prevents axis drift when Shift-constraining with incremental deltas.
                        double totalDx = current.X - _dragBodyStartWorldPos.X;
                        double totalDy = current.Y - _dragBodyStartWorldPos.Y;

                        double targetDx, targetDy;
                        if (_isShiftDown)
                        {
                            // Constrain to the dominant axis — object stays on a cross
                            // centered at the drag start position
                            if (Math.Abs(totalDx) >= Math.Abs(totalDy))
                            { targetDx = totalDx; targetDy = 0; }
                            else
                            { targetDx = 0; targetDy = totalDy; }
                        }
                        else
                        {
                            targetDx = totalDx;
                            targetDy = totalDy;
                        }

                        // Correction = target minus what has already been applied
                        double dx = targetDx - _appliedBodyDelta.X;
                        double dy = targetDy - _appliedBodyDelta.Y;
                        _appliedBodyDelta = new Point(targetDx, targetDy);

                        foreach (var o in _objects)
                        {
                            if (o.IsSelected && !o.IsLocked)
                                o.Move(dx, dy);
                        }
                    }
                    else
                    {
                        var clamped = current;
                        if (_activeObj.MoveBounds.HasValue)
                        {
                            var b = _activeObj.MoveBounds.Value;
                            clamped = new Point(
                                Math.Clamp(current.X, b.Left, b.Right),
                                Math.Clamp(current.Y, b.Top, b.Bottom));
                        }

                        bool isShift = _isShiftDown;
                        _activeObj.CurrentModifiers = TrackedModifiers;
                        _lastDragSnappedPos = clamped;
                        if (isShift) _activeObj.ResizeConstrained(_activeHandle, clamped);
                        else _activeObj.Resize(_activeHandle, clamped);
                    }
                }
                InvalidateVisual();
                return true;
            }

            // Hover cursor
            if (_ghostObject != null)
            {
                SetCursor(new Cursor(StandardCursorType.Cross));
                return true;
            }

            bool hit = false;
            foreach (var obj in Enumerable.Reverse(_objects))
            {
                if (!obj.IsSelectable || !obj.Visible) continue;
                if (!_overlaysVisible && obj is not ISystemOverlay) continue;
                var handle = obj.HitTest(screenPos, vp);
                if (handle == HandleType.None) continue;
                SetCursor(obj.GetCursor(handle, vp));
                hit = true;
                break;
            }
            if (!hit) SetCursor(Cursor.Default);
            return hit;
        }

        /// <summary>Returns <c>true</c> if the event was consumed.</summary>
        public bool OnPointerReleased(Point screenPos, KeyModifiers modifiers)
        {
            if (_ghostObject != null)
            {
                NormalizeObject(_ghostObject);

                if (_ghostObject is ISelection selector)
                {
                    if (!modifiers.HasFlag(KeyModifiers.Control))
                        foreach (var obj in _objects) obj.IsSelected = false;

                    foreach (var obj in _objects)
                    {
                        if (obj != _ghostObject && selector.Contains(obj))
                            obj.IsSelected = true;
                    }
                    _ghostObject = null;
                    _isDragging = false;
                    GhostCancelled?.Invoke(this, EventArgs.Empty);
                    InvalidateVisual();
                    return true;
                }
                else
                {
                    ApplyDefaultCreationSize(_ghostObject);
                    AddObject(_ghostObject, invalidate: false);
                    foreach (var obj in _objects) obj.IsSelected = false;
                    _ghostObject.IsSelected = true;
                    _activeObj = _ghostObject;
                }

                _ghostObject = null;
                _isDragging = false;
                InvalidateVisual();
                return true;
            }

            if (_isDragging)
            {
                if (_activeObj != null)
                {
                    NormalizeObject(_activeObj);
                    _activeObj.ResetResizeState();
                }
                _isDragging = false;
                _activeHandle = HandleType.None;
                InvalidateVisual();
                return true;
            }

            return false;
        }

        // ── Keyboard ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if the key was consumed.
        /// Call from the host control's <c>OnKeyDown</c>.
        /// </summary>
        public bool OnKeyDown(Key key, KeyModifiers modifiers)
        {
            if (key is Key.LeftShift or Key.RightShift ) _isShiftDown = true;
            else if (key is Key.LeftCtrl or Key.RightCtrl) _isCtrlDown = true;
            if (_isDragging) ReapplyDragConstraint();
            else InvalidateVisual();
            switch (key)
            {
                case Key.Delete:
                case Key.Back:
                    if (_objects.Any(o => o.IsSelected))
                    {
                        DeleteSelected();
                        return true;
                    }
                    return false;

                case Key.Escape:
                    Cancel();
                    return true;

                case Key.Left:
                case Key.Right:
                case Key.Up:
                case Key.Down:
                    return NudgeSelected(key, modifiers);

                case Key.C when modifiers.HasFlag(KeyModifiers.Control):
                    if (_objects.Any(o => o.IsSelected))
                    {
                        CopySelectedToClipboard();
                        return true;
                    }
                    return false;

                case Key.V when modifiers.HasFlag(KeyModifiers.Control):
                    PasteOverlays();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Updates the tracked modifier state. Call from the host control's <c>OnKeyUp</c>.</summary>
        public void OnKeyUp(Key key, KeyModifiers modifiers)
        {
            if (key is Key.LeftShift or Key.RightShift) _isShiftDown = false;
            else if (key is Key.LeftCtrl or Key.RightCtrl) _isCtrlDown = false;
            if (_isDragging) ReapplyDragConstraint();
            else InvalidateVisual();
        }

        /// <summary>
        /// Resets the sticky modifier flags to the actual OS state.
        /// Call on pointer enter to clear flags left over from missed <see cref="OnKeyUp"/> events
        /// (e.g. after Cmd+Tab or a system dialog interrupting a drag).
        /// No-op during an active drag to avoid disrupting in-progress constraint logic.
        /// </summary>
        public void SyncModifiers(KeyModifiers modifiers)
        {
            if (_isDragging) return;
            _isShiftDown = modifiers.HasFlag(KeyModifiers.Shift);
            _isCtrlDown  = modifiers.HasFlag(KeyModifiers.Control);
        }

        private void ReapplyDragConstraint()
        {
            if (_ghostObject != null)
            {
                _ghostObject.SetCreationBounds(_creationStartPos, _lastDragSnappedPos, TrackedModifiers);
                InvalidateVisual();
                return;
            }
            if (_activeObj == null || _activeHandle == HandleType.Body) return;
            _activeObj.CurrentModifiers = TrackedModifiers;
            bool isShift = _isShiftDown;
            if (isShift) _activeObj.ResizeConstrained(_activeHandle, _lastDragSnappedPos);
            else _activeObj.Resize(_activeHandle, _lastDragSnappedPos);
            InvalidateVisual();
        }

        private bool NudgeSelected(Key key, KeyModifiers modifiers)
        {
            if (!_objects.Any(o => o.IsSelected && !o.IsLocked)) return false;

            double step = _isShiftDown ? 10.0 : 1.0;
            double dx = key switch { Key.Left => -step, Key.Right => step, _ => 0.0 };
            double dy = key switch { Key.Up => -step, Key.Down => step, _ => 0.0 };

            foreach (var obj in _objects)
            {
                if (obj.IsSelected && !obj.IsLocked)
                    obj.Move(dx, dy);
            }
            InvalidateVisual();
            return true;
        }

        // ── Clipboard ─────────────────────────────────────────────────────────

        private const string ClipboardMarker = "mxplot:";

        private void CopyToClipboard(IEnumerable<OverlayObjectBase> objects)
        {
            var json = OverlaySerializer.Serialize(objects);
            if (string.IsNullOrEmpty(json) || json == "[]") return;
            _ = SetClipboardText?.Invoke($"{ClipboardMarker}{_instanceId}\n{json}");
            Copied?.Invoke(this, EventArgs.Empty);
        }

        private void CopySelectedToClipboard() =>
            CopyToClipboard(_objects.Where(o => o.IsSelected));

        /// <summary>Pastes overlay objects from the clipboard.</summary>
        public async void PasteOverlays()
        {
            if (GetClipboardText == null) return;
            var text = await GetClipboardText();
            if (string.IsNullOrWhiteSpace(text)) return;

            // Detect source instance from "mxplot:<id>\n" header
            bool sameInstance = false;
            string json = text;
            if (text.StartsWith(ClipboardMarker))
            {
                int nl = text.IndexOf('\n');
                if (nl > 0)
                {
                    sameInstance = text[ClipboardMarker.Length..nl] == _instanceId;
                    json = text[(nl + 1)..];
                }
            }

            var objects = OverlaySerializer.Deserialize(json);
            if (objects.Count == 0) return;

            // Offset only when pasting back into the same window instance
            if (sameInstance)
                foreach (var obj in objects)
                    obj.Move(10, 10);

            ClearSelection();
            foreach (var obj in objects)
            {
                obj.IsSelected = true;
                AddObject(obj, invalidate: false);
            }
            InvalidateVisual();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void OnDeleteRequested(object? sender, EventArgs e)
        {
            if (sender is OverlayObjectBase obj)
                RemoveObject(obj);
        }

        private void OnCopyOverlay(object? sender, EventArgs e)
        {
            if (sender is OverlayObjectBase obj)
                CopyToClipboard([obj]);
        }

        private void OnPasteOverlay(object? sender, EventArgs e) =>
            PasteOverlays();

        private static void NormalizeObject(OverlayObjectBase obj)
        {
            if (obj is not BoundingBoxBase bbox) return;
            if (bbox.Width < 0) { bbox.X += bbox.Width; bbox.Width = Math.Abs(bbox.Width); }
            if (bbox.Height < 0) { bbox.Y += bbox.Height; bbox.Height = Math.Abs(bbox.Height); }
        }

        private const double DefaultCreationScreenPx = 60.0;

        /// <summary>
        /// Ensures a newly created object has a visible minimum size.
        /// Called after <see cref="NormalizeObject"/> on pointer release.
        /// </summary>
        private void ApplyDefaultCreationSize(OverlayObjectBase obj)
        {
            var vp = GetViewport?.Invoke() ?? default;
            const double threshPx = 2.0;

            if (obj is BoundingBoxBase bbox)
            {
                if (bbox.Width < vp.ScreenToWorldDistX(threshPx))
                    bbox.Width = vp.ScreenToWorldDistX(DefaultCreationScreenPx);
                if (bbox.Height < vp.ScreenToWorldDistY(threshPx))
                    bbox.Height = vp.ScreenToWorldDistY(DefaultCreationScreenPx);
                return;
            }

            if (obj is LineObject line)
            {
                var sp1 = vp.WorldToScreen(line.P1);
                var sp2 = vp.WorldToScreen(line.P2);
                double dx = sp2.X - sp1.X, dy = sp2.Y - sp1.Y;
                if (dx * dx + dy * dy < threshPx * threshPx)
                    line.P2 = new Point(line.P1.X + vp.ScreenToWorldDistX(DefaultCreationScreenPx),
                                        line.P1.Y);
            }
        }
    }
}
