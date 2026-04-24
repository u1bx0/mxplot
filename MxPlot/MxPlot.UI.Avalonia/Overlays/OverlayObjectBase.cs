using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Abstract base class for all overlay objects.
    /// <para>
    /// <b>Coordinate system:</b> All positions are in <em>overlay world coordinates</em>,
    /// which correspond to the rendered bitmap's pixel-index space (left-top origin, Y increases
    /// downward). This is the native coordinate system of the Avalonia display surface.
    /// <c>BitmapWriter.FlipY = true</c> maps data row 0 (YMin) to the bitmap bottom, so
    /// overlay world Y = 0 corresponds to data row <c>YCount − 1</c> (YMax) and
    /// overlay world Y = <c>YCount − 1</c> corresponds to data row 0 (YMin).
    /// </para>
    /// <para>
    /// To convert between overlay world coordinates and data/physical coordinates,
    /// use <see cref="MxPlot.UI.Avalonia.Controls.RenderSurface.ScreenToData"/> /
    /// <c>DataToScreen</c>, or apply the FlipY transform manually:
    /// <c>dataIndexY = (YCount − 1) − worldY</c>.
    /// </para>
    /// </summary>
    public abstract class OverlayObjectBase
    {
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                SelectionChanged?.Invoke(this, value);
            }
        }

        /// <summary>Raised when <see cref="IsSelected"/> changes; argument is the new value.</summary>
        public event EventHandler<bool>? SelectionChanged;

        public bool Visible { get; set; } = true;
        public bool IsLocked { get; set; } = false;
        public bool IsDeletable { get; set; } = true;
        public bool IsSelectable { get; set; } = true;
        public PixelSnapMode SnapMode { get; set; } = PixelSnapMode.None;

        /// <summary>
        /// Set by <see cref="OverlayManager"/> immediately before calling
        /// <see cref="Resize"/> or <see cref="ResizeConstrained"/> so that
        /// overrides can inspect the current keyboard modifier state.
        /// </summary>
        public KeyModifiers CurrentModifiers { get; set; }

        /// <summary>
        /// Optional world-coordinate clamp applied to Resize operations.
        /// null = unconstrained.
        /// </summary>
        public Rect? MoveBounds { get; set; } = null;

        // ── Pen / appearance ──────────────────────────────────────────────────

        public Color PenColor { get; set; } = Color.FromRgb(0, 0xFF, 0xE0);
        public double PenWidth { get; set; } = 2.0;
        public OverlayDashStyle PenDash { get; set; } = OverlayDashStyle.Solid;
        public bool IsScaledPenWidth { get; set; } = false;

        protected const double HandleSize = 8.0;

        // ── Abstract interface ────────────────────────────────────────────────

        public abstract void Draw(AvaloniaOverlayGraphics g);
        public abstract HandleType HitTest(Point screenPoint, AvaloniaViewport vp);
        public abstract void Move(double dx, double dy);
        public abstract void Resize(HandleType handle, Point worldNewPos);
        public virtual void ResizeConstrained(HandleType handle, Point worldNewPos) => Resize(handle, worldNewPos);
        public virtual void BeginResize() { }
        public virtual void ResetResizeState() { }
        public abstract Cursor GetCursor(HandleType handle, AvaloniaViewport vp);
        public abstract void SetCreationBounds(Point startWorld, Point endWorld);
        public virtual void SetCreationBounds(Point startWorld, Point endWorld, KeyModifiers modifiers) => SetCreationBounds(startWorld, endWorld);

        /// <summary>
        /// Returns a one-line description of the object's current geometry in physical units,
        /// suitable for display in a status bar. Returns <c>null</c> when not applicable.
        /// </summary>
        /// <param name="data">The <see cref="IMatrixData"/> whose scale/units are used for conversion. May be <c>null</c>.</param>
        public virtual string? GetInfo(IMatrixData? data) => null;

        /// <summary>Formats a physical length value for display (4 significant digits, invariant culture).</summary>
        protected static string FmtLen(double v) =>
            v.ToString("G4", System.Globalization.CultureInfo.InvariantCulture);

        // ── Context menu ──────────────────────────────────────────────────────

        public virtual IEnumerable<OverlayMenuItem>? GetContextMenuItems()
        {
            yield return new OverlayMenuItem("Snap Mode", new List<OverlayMenuItem>
            {
                new("None", () => SnapMode = PixelSnapMode.None,   SnapMode == PixelSnapMode.None, tooltip: "Move freely without snapping"),
                new("Pixel Center", () => SnapMode = PixelSnapMode.Center, SnapMode == PixelSnapMode.Center, tooltip: "Snap to pixel centers"),
                new("Pixel Edge",  () => SnapMode = PixelSnapMode.Corner, SnapMode == PixelSnapMode.Corner, tooltip: "Snap to pixel edges"),
                new("Both", () => SnapMode = PixelSnapMode.Both,   SnapMode == PixelSnapMode.Both, tooltip: "Snap to both pixel centers and edges"),
            }.AsReadOnly(), icon: MenuIcons.PushPin);
            yield return new OverlayMenuItem("Copy", OnCopyRequested, icon: MenuIcons.Copy, tooltip: "Copy overlay to clipboard");
            yield return new OverlayMenuItem("Paste", OnPasteRequested, icon: MenuIcons.Paste, tooltip: "Paste overlay from clipboard");
            yield return new OverlayMenuItem("Properties\u2026", RequestPenEdit, icon: MenuIcons.Edit, tooltip: "Open overlay property dialog");
            if (!IsDeletable) yield break;
            yield return new OverlayMenuItem("Delete", OnDeleteRequested, icon: MenuIcons.TrashCan, tooltip: "Delete this overlay");
        }

        public event EventHandler? DeleteRequested;
        protected virtual void OnDeleteRequested() =>
            DeleteRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Raised when the user selects "Copy" from the context menu.</summary>
        public event EventHandler? CopyRequested;
        private void OnCopyRequested() =>
            CopyRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>Raised when the user selects "Paste" from the context menu.</summary>
        public event EventHandler? PasteRequested;
        private void OnPasteRequested() =>
            PasteRequested?.Invoke(this, EventArgs.Empty);

        // ── Helpers ───────────────────────────────────────────────────────────

        protected Rect GetHandleRect(Point screenPos) =>
            new(screenPos.X - HandleSize / 2, screenPos.Y - HandleSize / 2,
                HandleSize, HandleSize);

        protected static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        protected static double DistanceToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-10) return Distance(p, a);
            double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0, 1);
            return Distance(p, new Point(a.X + t * dx, a.Y + t * dy));
        }

        /// <summary>
        /// Returns the pen to use for drawing.
        /// When selected, delegates to <see cref="GetSelectedDrawingPen"/>; otherwise returns <see cref="PenColor"/> / <see cref="PenDash"/>.
        /// </summary>
        protected (Color color, OverlayDashStyle dash) GetDrawingPen() =>
            IsSelected ? GetSelectedDrawingPen() : (PenColor, PenDash);

        /// <summary>
        /// Returns the pen used when this object is selected.
        /// Override in subclasses to provide a custom selected appearance.
        /// The default implementation returns the same pen as the normal state (<see cref="PenColor"/> / <see cref="PenDash"/>).
        /// </summary>
        protected virtual (Color color, OverlayDashStyle dash) GetSelectedDrawingPen() =>
            (PenColor, PenDash);

        // ── Pen editor ────────────────────────────────────────────────────────

        /// <summary>Raised when the user requests the pen-property editor.</summary>
        public event EventHandler? PenEditRequested;

        /// <summary>Fires <see cref="PenEditRequested"/>.</summary>
        public void RequestPenEdit() => PenEditRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Called when the user double-clicks this object.
        /// The default implementation opens the pen editor (<see cref="RequestPenEdit"/>).
        /// Override to provide a different action (e.g. <see cref="Shapes.TextObject"/> opens the text editor).
        /// </summary>
        public virtual void OnDoubleClicked() => RequestPenEdit();

        /// <summary>
        /// Returns an approximate world-coordinate center used to position floating editor windows.
        /// Returns <c>null</c> when the position is indeterminate.
        /// </summary>
        public virtual Point? GetApproxWorldCenter() => null;
    }
}
