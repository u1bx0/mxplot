using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    /// <summary>
    /// System-managed region-of-interest rectangle.
    /// <list type="bullet">
    ///   <item>Cannot be deleted by the user.</item>
    ///   <item>Default snap mode is <see cref="PixelSnapMode.Corner"/> (pixel-edge snap).</item>
    ///   <item>Resize handles are invisible but functional; the cursor changes near corners to indicate resize.</item>
    ///   <item>Resize and Move are clamped to <see cref="DataBounds"/> when set.</item>
    ///   <item>Drawn with a black 3 px / <see cref="AccentColor"/> 1 px double border (ImageJ-style ROI).</item>
    ///   <item>When <see cref="IsMoveOnly"/> is <c>true</c>, resize handles are suppressed; only drag is permitted.</item>
    /// </list>
    /// </summary>
    public sealed class RoiObject : BoundingBoxBase, ISystemOverlay
    {
        /// <summary>Raised after the ROI position or size changes due to a Move or Resize operation.</summary>
        public event EventHandler? BoundsChanged;

        public RoiObject()
        {
            IsDeletable = false;
            SnapMode    = PixelSnapMode.Corner;
        }

        /// <summary>
        /// World-coordinate data extent. Resize and Move operations are clamped to this rectangle.
        /// Also forwarded to <see cref="OverlayObjectBase.MoveBounds"/> so that
        /// <see cref="OverlayManager"/> clamps the resize handle during drag.
        /// </summary>
        public Rect? DataBounds
        {
            get => MoveBounds;
            set => MoveBounds = value;
        }

        /// <summary>
        /// When <c>true</c>, draws a "ROI (px)=[W, H]" label near the top-left corner of the ROI.
        /// Default is <c>false</c>.
        /// </summary>
        public bool ShowLabel { get; set; } = false;

        /// <summary>
        /// When <c>true</c>, resize handles that would change the ROI height are remapped to
        /// horizontal-only handles (or suppressed), effectively locking the height.
        /// Direct property assignment of <see cref="BoundingBoxBase.Y"/> and
        /// <see cref="BoundingBoxBase.Height"/> by sync code is still allowed.
        /// </summary>
        public bool IsHeightLocked { get; set; } = false;

        /// <summary>Accent color for the 1 px inner border. Default is <see cref="Colors.Yellow"/> (ImageJ-style).</summary>
        public Color AccentColor { get; set; } = Colors.Yellow;

        /// <summary>
        /// When <c>true</c>, resize handle hit-tests are suppressed — only drag (body hit) is permitted.
        /// <see cref="Resize"/> becomes a no-op; <see cref="GetCursor"/> always returns <see cref="StandardCursorType.SizeAll"/>.
        /// </summary>
        public bool IsMoveOnly { get; set; } = false;

        // ── Drawing ───────────────────────────────────────────────────────────

        public override void Draw(AvaloniaOverlayGraphics g)
        {
            if (Width <= 0 || Height <= 0) return;

            // Black 3 px outer border + accent 1 px inner border (ImageJ-style)
            g.DrawRectangle(Colors.Black, 3.0, OverlayDashStyle.Solid, X, Y, Width, Height);
            g.DrawRectangle(AccentColor, 1.0, OverlayDashStyle.Solid, X, Y, Width, Height);
            // Corner L-shapes and edge-midpoint bars (always visible, zoom-independent)
            DrawRoiMarkers(g);
            // Size label near the top-left corner
            if (ShowLabel) DrawLabel(g);
        }

        private void DrawLabel(AvaloniaOverlayGraphics g)
        {
            var r    = GetNormalizedRect();
            var text = $"ROI (px)=[{r.Width:F0}, {r.Height:F0}]";
            var sTL  = g.WorldToScreen(X, Y);
            g.DrawStringAtScreen(text,
                foreground: Colors.White,
                background: Color.FromArgb(180, 40, 40, 40),
                screenPos:  new Point(sTL.X + 4, sTL.Y + 4));
        }

        /// <summary>
        /// Draws fixed-pixel L-shaped marks at the four corners only.
        /// Edge midpoint bars are omitted; edges remain draggable for directional resize.
        /// <code>
        /// ┏          ┓
        /// 
        /// ┃              ┃
        /// 
        /// ┗          ┛
        /// </code>
        /// </summary>
        private void DrawRoiMarkers(AvaloniaOverlayGraphics g)
        {
            const double L = 12.0;

            var sTL = g.WorldToScreen(X,          Y);
            var sTR = g.WorldToScreen(X + Width,  Y);
            var sBL = g.WorldToScreen(X,          Y + Height);
            var sBR = g.WorldToScreen(X + Width,  Y + Height);

            var h = Norm(sTR - sTL);
            var v = Norm(sBL - sTL);

            (Point pivot, double sh, double sv)[] corners =
            [
                (sTL,  1,  1),
                (sTR, -1,  1),
                (sBL,  1, -1),
                (sBR, -1, -1),
            ];
            foreach (var (p, sh, sv) in corners)
            {
                var ph = p + h * (sh * L);
                var pv = p + v * (sv * L);
                g.DrawLineAtScreen(Colors.Black, 5.0, p, ph);
                g.DrawLineAtScreen(Colors.Black, 5.0, p, pv);
                g.DrawLineAtScreen(Colors.White, 3.0, p, ph);
                g.DrawLineAtScreen(Colors.White, 3.0, p, pv);
            }
        }

        private static Vector Norm(Vector v)
        {
            double len = Math.Sqrt(v.X * v.X + v.Y * v.Y);
            return len > 0.5 ? new Vector(v.X / len, v.Y / len) : new Vector(1, 0);
        }

        // ── Hit testing ───────────────────────────────────────────────────────

        public override HandleType HitTest(Point location, AvaloniaViewport vp)
        {
            if (IsMoveOnly)
            {
                var h = HitTestBoundingBox(location, vp, testEdges: true, alwaysTestHandles: true, testInterior: true);
                return h != HandleType.None ? HandleType.Body : HandleType.None;
            }

            // 1. Corner handles (always active regardless of selection state)
            var corner = HitTestBoundingBox(location, vp, testEdges: false, alwaysTestHandles: true, testInterior: false);
            if (corner is HandleType.TopLeft or HandleType.TopRight
                       or HandleType.BottomLeft or HandleType.BottomRight)
                return IsHeightLocked ? RemapHeightLocked(corner) : corner;

            // 2. Full-edge proximity → directional 1-axis resize (no visual handle needed)
            var edge = HitTestEdgeDirectional(location, vp);
            if (edge != HandleType.None) return IsHeightLocked ? RemapHeightLocked(edge) : edge;

            // 3. Interior → move
            if (HitTestBoundingBox(location, vp, testEdges: false, alwaysTestHandles: false, testInterior: true)
                == HandleType.Body)
                return HandleType.Body;

            return HandleType.None;
        }

        /// <summary>
        /// Tests proximity to each of the four edges and returns the corresponding
        /// directional handle type, so that dragging any part of an edge triggers
        /// a single-axis resize rather than a move.
        /// </summary>
        private HandleType HitTestEdgeDirectional(Point location, AvaloniaViewport vp)
        {
            var sTL = vp.WorldToScreen(new Point(X,          Y));
            var sTR = vp.WorldToScreen(new Point(X + Width,  Y));
            var sBL = vp.WorldToScreen(new Point(X,          Y + Height));
            var sBR = vp.WorldToScreen(new Point(X + Width,  Y + Height));
            const double Thresh = 5.0;
            if (SegDist(location, sTL, sTR) <= Thresh) return HandleType.TopCenter;
            if (SegDist(location, sTR, sBR) <= Thresh) return HandleType.MiddleRight;
            if (SegDist(location, sBR, sBL) <= Thresh) return HandleType.BottomCenter;
            if (SegDist(location, sBL, sTL) <= Thresh) return HandleType.MiddleLeft;
            return HandleType.None;
        }

        /// <summary>
        /// Remaps height-affecting handles to horizontal-only equivalents when
        /// <see cref="IsHeightLocked"/> is <c>true</c>.
        /// TopCenter/BottomCenter → None; corner handles → the corresponding MiddleLeft/MiddleRight.
        /// </summary>
        private static HandleType RemapHeightLocked(HandleType h) => h switch
        {
            HandleType.TopCenter or HandleType.BottomCenter => HandleType.None,
            HandleType.TopLeft or HandleType.BottomLeft => HandleType.MiddleLeft,
            HandleType.TopRight or HandleType.BottomRight => HandleType.MiddleRight,
            _ => h,
        };

        private static double SegDist(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-6)
                return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
            double px = a.X + t * dx, py = a.Y + t * dy;
            return Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
        }

        public override Cursor GetCursor(HandleType handle, AvaloniaViewport vp) =>
            IsMoveOnly ? new Cursor(StandardCursorType.SizeAll) : GetResizeCursor(handle, vp);

        // ── Move (clamped to DataBounds) ──────────────────────────────────────

        public override void Move(double dx, double dy)
        {
            double nx = X + dx, ny = Y + dy;
            if (MoveBounds.HasValue)
            {
                var b = MoveBounds.Value;
                nx = Math.Clamp(nx, b.Left, b.Right  - Width);
                ny = Math.Clamp(ny, b.Top,  b.Bottom - Height);
            }
            X = nx;
            Y = ny;
            BoundsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Resize ────────────────────────────────────────────────────────────

        public override void Resize(HandleType handle, Point worldNewPos)
        {
            if (IsMoveOnly) return;
            ResizeBoundingBox(handle, worldNewPos);
            BoundsChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Creation ──────────────────────────────────────────────────────────

        public override void SetCreationBounds(Point start, Point end) =>
            SetCreationBoundsInternal(start, end);

        // ── Info ──────────────────────────────────────────────────────────────

        /// <summary>
        /// When <c>true</c>, <see cref="GetInfo"/> appends a double-click hint to the status text while the ROI is selected.
        /// Set by <c>CropAction</c> on the leader ROI that has a size-edit dialog.
        /// </summary>
        public bool ShowSizeEditHint { get; set; } = false;

        public override string? GetInfo(IMatrixData? data)
        {
            if (IsSelected && ShowSizeEditHint)
                return "ROI: Double-click to configure size";
            return null;
        }

        // ── No context menu, no pen editor ────────────────────────────────────

        public override IEnumerable<OverlayMenuEntry>? GetContextMenuItems() => null;

        /// <summary>
        /// Raised when the user double-clicks the ROI to request a direct size-edit dialog.
        /// If no subscriber is attached, the double-click is silently ignored.
        /// </summary>
        internal event EventHandler? SizeEditRequested;

        /// <summary>ROI style is fixed; suppresses the default pen-editor. Fires <see cref="SizeEditRequested"/> instead.</summary>
        public override void OnDoubleClicked() => SizeEditRequested?.Invoke(this, EventArgs.Empty);

        /// <summary>
        /// Applies a new width and height (in world/pixel-edge units) coming from a size-edit dialog,
        /// clamps the position so the ROI stays within <see cref="DataBounds"/>, then fires
        /// <see cref="BoundsChanged"/> so that the hosting <c>CropAction</c> can propagate
        /// the change through the sync group.
        /// </summary>
        internal void ApplySizeFromDialog(double newWidth, double newHeight)
        {
            Width = Math.Max(1.0, newWidth);
            Height = Math.Max(1.0, newHeight);
            if (MoveBounds.HasValue)
            {
                var b = MoveBounds.Value;
                // サイズが境界の幅・高さを超えないようにクランプ
                Width = Math.Min(Width, b.Right - b.Left);
                Height = Math.Min(Height, b.Bottom - b.Top);
                X = Math.Clamp(X, b.Left, b.Right - Width);
                Y = Math.Clamp(Y, b.Top, b.Bottom - Height);
            }
            BoundsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
