using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    /// <summary>
    /// Base class for overlay objects with an axis-aligned bounding box and 8 resize handles.
    /// <para>
    /// <b>Coordinate convention:</b> <see cref="X"/>, <see cref="Y"/> define the
    /// <em>top-left</em> corner in overlay world space (bitmap pixel-index, left-top origin,
    /// Y-down). <see cref="Width"/> extends rightward, <see cref="Height"/> extends downward.
    /// This matches the Avalonia screen convention and is the coordinate system in which
    /// <see cref="OverlayManager"/> performs hit-testing and dragging.
    /// </para>
    /// <para>
    /// Because <c>BitmapWriter.FlipY = true</c> inverts the Y axis when rendering data,
    /// overlay world Y = 0 is the <em>screen top</em> (= data YMax), not data row 0.
    /// Any code that needs data-index or physical coordinates must apply the FlipY
    /// transform: <c>dataIndexY = (YCount − 1) − worldY</c>.
    /// </para>
    /// </summary>
    public abstract class BoundingBoxBase : OverlayObjectBase
    {
        /// <summary>Left edge in overlay world space (bitmap pixel-index, left-top origin).</summary>
        public double X { get; set; }

        /// <summary>
        /// Top edge in overlay world space (bitmap pixel-index, left-top origin, Y-down).
        /// <para>
        /// At user-facing boundaries this must be converted to the data-index convention
        /// (left-bottom origin, Y-up) via <c>dataIndexY = (YCount − 1) − worldY</c>.
        /// </para>
        /// </summary>
        public double Y { get; set; }

        /// <summary>Horizontal extent (rightward from <see cref="X"/>).</summary>
        public double Width { get; set; }

        /// <summary>
        /// Vertical extent (downward from <see cref="Y"/> in overlay world space).
        /// At user-facing boundaries this appears as upward growth from the bottom-left origin.
        /// </summary>
        public double Height { get; set; }

        /// <summary>
        /// Bottom-left corner of the bounding box in overlay world space:
        /// (<see cref="X"/>, <see cref="Y"/> + <see cref="Height"/>).
        /// <para>
        /// This is the screen-visual bottom-left. In the user-facing data-index convention
        /// (left-bottom origin, Y-up), apply FlipY to obtain the display origin:
        /// <c>dataOriginY = (YCount − 1) − Origin.Y</c>.
        /// </para>
        /// </summary>
        public Point Origin => new(X, Y + Height);  

        /// <summary>When true, the bounding box is drawn with a filled interior.</summary>
        public bool  IsFilled  { get; set; } = false;

        /// <summary>
        /// Fill colour including alpha channel.
        /// Defaults to a semi-transparent cyan matching the default pen colour.
        /// </summary>
        public Color FillColor { get; set; } = Color.FromArgb(80, 0, 255, 224);

        public override Point? GetApproxWorldCenter() => new Point(X + Width / 2, Y + Height / 2);


        private double? _originalAspect;
        private double _originalCx;
        private double _originalCy;
        private bool _hasResizeState;

        public override void BeginResize()
        {
            if (Math.Abs(Width) > 0.01 && Math.Abs(Height) > 0.01)
                _originalAspect = Width / Height;
            _originalCx = X + Width / 2;
            _originalCy = Y + Height / 2;
            _hasResizeState = true;
        }

        public override void ResetResizeState()
        {
            _originalAspect = null;
            _hasResizeState = false;
        }

        public override IEnumerable<OverlayMenuItem>? GetContextMenuItems()
        {
            if (this is IAnalyzableOverlay evaluable)
            {
                yield return new OverlayMenuItem("Find Min/Max",
                    evaluable.RaiseFindMinMaxRequested,
                    icon: MenuIcons.Search,
                    tooltip: "Apply the region min/max to the main view value range");
                yield return new OverlayMenuItem(
                    evaluable.ShowStatistics ? "Hide Statistics" : "Show Statistics",
                    evaluable.RaiseToggleShowStatisticsRequested,
                    icon: MenuIcons.LineChart,
                    tooltip: "Toggle statistics label inside the region");
                yield return new OverlayMenuItem(
                    evaluable.IsValueRangeRoi ? "Unset as Value Range ROI" : "Use ROI for Value Range",
                    evaluable.RaiseUseRoiForValueRangeRequested,
                    icon: MenuIcons.Roi,
                    tooltip: evaluable.IsValueRangeRoi
                        ? "Stop using this region as the value-range ROI"
                        : "Use this region to determine the min/max value range");
                yield return OverlayMenuItem.Separator();
            }
            foreach (var item in base.GetContextMenuItems() ?? [])
                yield return item;
        }

        // ── Statistics label drawing helper ───────────────────────────────────

        /// <summary>
        /// Draws the cached statistics label just outside the bottom-left corner of the
        /// bounding box. Call from <see cref="OverlayObjectBase.Draw"/> in concrete subclasses
        /// when <see cref="IAnalyzableOverlay.ShowStatistics"/> is true.
        /// </summary>
        protected void DrawStatisticsLabel(AvaloniaOverlayGraphics g, RegionStatistics stats)
        {
            // All 4 corners in screen space (accounts for FlipV / Rotate90CCW in ortho views)
            var sTL = g.WorldToScreen(X,         Y);
            var sTR = g.WorldToScreen(X + Width,  Y);
            var sBL = g.WorldToScreen(X,          Y + Height);
            var sBR = g.WorldToScreen(X + Width,  Y + Height);

            // Pick the corner with the largest screen Y (= visually lowest).
            // Break ties by smallest screen X (= visually leftmost).
            ReadOnlySpan<Point> corners = [sTL, sTR, sBL, sBR];
            var best = corners[0];
            foreach (var c in corners)
            {
                if (c.Y > best.Y || (c.Y == best.Y && c.X < best.X))
                    best = c;
            }

            g.DrawStringAtScreen(
                stats.ToLabel(),
                foreground: Colors.White,
                background: Color.FromArgb(200, 30, 30, 30),
                screenPos: new Point(best.X + 4, best.Y + 4));
        }


        // ── ROI label drawing helper ──────────────────────────────────────────

        /// <summary>
        /// Draws a small "ROI" tag just outside the top-left corner of the bounding box.
        /// The tag's bottom-left corner is placed 1 DIP above and to the right of the
        /// top-left resize handle's outer corner (handle half-size = 4 screen px).
        /// Call from <see cref="OverlayObjectBase.Draw"/> in concrete subclasses
        /// when <see cref="IAnalyzableOverlay.IsValueRangeRoi"/> is true.
        /// </summary>
        protected void DrawRoiLabel(AvaloniaOverlayGraphics g)
        {
            var sTL = g.WorldToScreen(X, Y);
            // Place the label so its bottom-left is 1 DIP above the handle's outer top-left corner.
            // Handle outer top-left = (sTL.X - HandleSize/2, sTL.Y - HandleSize/2)
            // Label bottom = handle outer top - 1  =>  screenY = sTL.Y - HandleSize/2 - 1
            // DrawStringAtScreen draws from the top-left of the text box, font is ~10px tall.
            const double handleHalf = HandleSize / 2;   // 4.0
            const double labelH = 13.0;                 // approx rendered height of the tag
            g.DrawStringAtScreen(
                "ROI",
                foreground: Colors.Black,
                background: Color.FromArgb(210, 255, 210, 60),
                screenPos: new Point(sTL.X - handleHalf + 1, sTL.Y - handleHalf - labelH - 1));
        }


        protected void SetCreationBoundsInternal(Point start, Point end, bool forceSquare = false)
        {
            double ex = end.X, ey = end.Y;
            if (forceSquare)
            {
                double dx = ex - start.X, dy = ey - start.Y;
                double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
                ex = start.X + Math.Sign(dx == 0 ? 1 : dx) * size;
                ey = start.Y + Math.Sign(dy == 0 ? 1 : dy) * size;
            }
            X = Math.Min(start.X, ex);
            Y = Math.Min(start.Y, ey);
            Width  = Math.Abs(ex - start.X);
            Height = Math.Abs(ey - start.Y);
        }

        // ── Handle drawing ────────────────────────────────────────────────────

        protected void DrawHandles(AvaloniaOverlayGraphics g)
        {
            if (!IsSelected || g.SuppressHandles) return;

            // Zero-size: all 8 handle positions collapse to the same screen point.
            // Draw a single handle at that point so the object remains visible and selectable.
            if (Width == 0 && Height == 0)
            {
                g.DrawHandleAtScreen(Colors.White, Colors.Black, g.WorldToScreen(X, Y), HandleSize);
                return;
            }

            // World corners (pixel-index space, Y-down in Avalonia viewport)
            var (sTL, sTR, sBL, sBR) = GetScreenCorners(g);
            var sTC = Mid(sTL, sTR);
            var sMR = Mid(sTR, sBR);
            var sBC = Mid(sBL, sBR);
            var sML = Mid(sTL, sBL);

            ReadOnlySpan<Point> pts = [sTL, sTC, sTR, sML, sMR, sBL, sBC, sBR];
            foreach (var p in pts)
                g.DrawHandleAtScreen(Colors.White, Colors.Black, p, HandleSize);

#if DEBUG
            g.DrawString("[Origin]", Colors.Red, Origin.X, Origin.Y+1, 10);
            g.DrawString("[X,Y]", Colors.Red, X, Y +1, 10);
#endif
        }

        private (Point TL, Point TR, Point BL, Point BR) GetScreenCorners(AvaloniaOverlayGraphics g)
        {
            // In world (pixel-index) space Y increases downward (screen convention),
            // so top-left is (X, Y) and bottom-right is (X+W, Y+H).
            var tl = g.WorldToScreen(X,         Y);
            var tr = g.WorldToScreen(X + Width,  Y);
            var bl = g.WorldToScreen(X,          Y + Height);
            var br = g.WorldToScreen(X + Width,  Y + Height);
            return (tl, tr, bl, br);
        }

        private static (Point TL, Point TR, Point BL, Point BR) GetScreenCorners(AvaloniaViewport vp,
            double x, double y, double w, double h)
        {
            var tl = vp.WorldToScreen(new Point(x,     y));
            var tr = vp.WorldToScreen(new Point(x + w, y));
            var bl = vp.WorldToScreen(new Point(x,     y + h));
            var br = vp.WorldToScreen(new Point(x + w, y + h));
            return (tl, tr, bl, br);
        }

        // ── Hit testing ───────────────────────────────────────────────────────

        protected HandleType HitTestBoundingBox(Point location, AvaloniaViewport vp,
            bool testEdges = true, bool alwaysTestHandles = false, bool testInterior = false)
        {
            var (sTL, sTR, sBL, sBR) = GetScreenCorners(vp, X, Y, Width, Height);
            var sTC = Mid(sTL, sTR);
            var sMR = Mid(sTR, sBR);
            var sBC = Mid(sBL, sBR);
            var sML = Mid(sTL, sBL);

            if (IsSelected || alwaysTestHandles)
            {
                if (GetHandleRect(sTL).Contains(location)) return HandleType.TopLeft;
                if (GetHandleRect(sTC).Contains(location)) return HandleType.TopCenter;
                if (GetHandleRect(sTR).Contains(location)) return HandleType.TopRight;
                if (GetHandleRect(sMR).Contains(location)) return HandleType.MiddleRight;
                if (GetHandleRect(sBR).Contains(location)) return HandleType.BottomRight;
                if (GetHandleRect(sBC).Contains(location)) return HandleType.BottomCenter;
                if (GetHandleRect(sBL).Contains(location)) return HandleType.BottomLeft;
                if (GetHandleRect(sML).Contains(location)) return HandleType.MiddleLeft;
            }

            if (testEdges)
            {
                const double thresh = 5.0;
                if (DistanceToSegment(location, sTL, sTR) <= thresh ||
                    DistanceToSegment(location, sTR, sBR) <= thresh ||
                    DistanceToSegment(location, sBR, sBL) <= thresh ||
                    DistanceToSegment(location, sBL, sTL) <= thresh)
                    return HandleType.Body;
            }

            if (testInterior)
            {
                var w = vp.ScreenToWorld(location);
                if (w.X >= X && w.X <= X + Width && w.Y >= Y && w.Y <= Y + Height)
                    return HandleType.Body;
            }

            return HandleType.None;
        }

        // ── Resize ────────────────────────────────────────────────────────────

        protected void ResizeBoundingBox(HandleType handle, Point worldNewPos)
        {
            double x2 = X + Width, y2 = Y + Height;
            switch (handle)
            {
                case HandleType.TopLeft:      X  = worldNewPos.X; Y  = worldNewPos.Y; break;
                case HandleType.TopCenter:                         Y  = worldNewPos.Y; break;
                case HandleType.TopRight:     x2 = worldNewPos.X; Y  = worldNewPos.Y; break;
                case HandleType.MiddleRight:  x2 = worldNewPos.X;                     break;
                case HandleType.BottomRight:  x2 = worldNewPos.X; y2 = worldNewPos.Y; break;
                case HandleType.BottomCenter:                      y2 = worldNewPos.Y; break;
                case HandleType.BottomLeft:   X  = worldNewPos.X; y2 = worldNewPos.Y; break;
                case HandleType.MiddleLeft:   X  = worldNewPos.X;                     break;
            }
            Width  = x2 - X;
            Height = y2 - Y;
        }

        /// <summary>
        /// Resizes the bounding box keeping the original center fixed (Ctrl behaviour).
        /// Corner handles resize both axes symmetrically; edge handles resize only the relevant axis.
        /// </summary>
        protected void ResizeBoundingBoxCtrl(HandleType handle, Point worldNewPos)
        {
            double cx = _hasResizeState ? _originalCx : X + Width / 2;
            double cy = _hasResizeState ? _originalCy : Y + Height / 2;
            double halfW, halfH;
            switch (handle)
            {
                case HandleType.TopLeft or HandleType.TopRight
                  or HandleType.BottomLeft or HandleType.BottomRight:
                    halfW  = Math.Abs(worldNewPos.X - cx);
                    halfH  = Math.Abs(worldNewPos.Y - cy);
                    X      = cx - halfW;
                    Y      = cy - halfH;
                    Width  = 2 * halfW;
                    Height = 2 * halfH;
                    break;
                case HandleType.TopCenter or HandleType.BottomCenter:
                    halfH  = Math.Abs(worldNewPos.Y - cy);
                    Y      = cy - halfH;
                    Height = 2 * halfH;
                    break;
                case HandleType.MiddleLeft or HandleType.MiddleRight:
                    halfW = Math.Abs(worldNewPos.X - cx);
                    X     = cx - halfW;
                    Width = 2 * halfW;
                    break;
            }
        }

        public override void ResizeConstrained(HandleType handle, Point worldNewPos)
        {
            const double minSize = 1.0, maxSize = 100000.0;

            if (CurrentModifiers.HasFlag(KeyModifiers.Control))
            {
                // Shift+Ctrl: aspect-ratio-locked, center stays at pre-drag position
                double cx = _hasResizeState ? _originalCx : X + Width / 2;
                double cy = _hasResizeState ? _originalCy : Y + Height / 2;
                double aspect = _originalAspect ??
                    (Math.Abs(Width) > 0.01 && Math.Abs(Height) > 0.01 ? Width / Height : 1.0);
                if (double.IsNaN(aspect) || double.IsInfinity(aspect) || aspect <= 0 || aspect > 1000)
                {
                    ResizeBoundingBoxCtrl(handle, worldNewPos);
                    return;
                }
                double halfW, halfH;
                if (handle is HandleType.TopLeft or HandleType.TopRight
                           or HandleType.BottomLeft or HandleType.BottomRight)
                {
                    halfW = Math.Abs(worldNewPos.X - cx);
                    halfH = Math.Abs(worldNewPos.Y - cy);
                    if (halfW > halfH * aspect) halfH = halfW / aspect;
                    else                        halfW = halfH * aspect;
                }
                else if (handle is HandleType.TopCenter or HandleType.BottomCenter)
                {
                    halfH = Math.Abs(worldNewPos.Y - cy);
                    halfW = halfH * aspect;
                }
                else if (handle is HandleType.MiddleLeft or HandleType.MiddleRight)
                {
                    halfW = Math.Abs(worldNewPos.X - cx);
                    halfH = halfW / aspect;
                }
                else
                {
                    ResizeBoundingBoxCtrl(handle, worldNewPos);
                    return;
                }
                if (halfW * 2 < minSize || halfH * 2 < minSize || halfW * 2 > maxSize || halfH * 2 > maxSize) return;
                X      = cx - halfW;
                Y      = cy - halfH;
                Width  = 2 * halfW;
                Height = 2 * halfH;
                return;
            }

            // Shift only: aspect-ratio-locked, anchor at the opposite corner
            if (Math.Abs(Width) < minSize || Math.Abs(Height) < minSize)
            {
                ResizeBoundingBox(handle, worldNewPos);
                return;
            }

            double aspectS = _originalAspect ?? (Width / Height);
            if (double.IsNaN(aspectS) || double.IsInfinity(aspectS) || aspectS <= 0 || aspectS > 1000)
            {
                ResizeBoundingBox(handle, worldNewPos);
                return;
            }

            double x2 = X + Width, y2 = Y + Height;

            if (handle is HandleType.TopLeft or HandleType.TopRight
                       or HandleType.BottomLeft or HandleType.BottomRight)
            {
                Point anchor = handle switch
                {
                    HandleType.TopLeft    => new(x2, y2),
                    HandleType.TopRight   => new(X,  y2),
                    HandleType.BottomLeft => new(x2, Y),
                    _                    => new(X,  Y),
                };
                double dx = worldNewPos.X - anchor.X;
                double dy = worldNewPos.Y - anchor.Y;
                if (Math.Abs(dx) < 0.1 && Math.Abs(dy) < 0.1) return;

                if (Math.Abs(dx) > Math.Abs(dy) * aspectS)
                {
                    double signDy = dy >= 0 ? 1 : -1;
                    dy = signDy * Math.Abs(dx) / aspectS;
                }
                else
                {
                    double signDx = dx >= 0 ? 1 : -1;
                    dx = signDx * Math.Abs(dy) * aspectS;
                }

                double newW = Math.Abs(dx), newH = Math.Abs(dy);
                if (newW < minSize || newH < minSize || newW > maxSize || newH > maxSize) return;

                X      = Math.Min(anchor.X, anchor.X + dx);
                Y      = Math.Min(anchor.Y, anchor.Y + dy);
                Width  = newW;
                Height = newH;
            }
            else
            {
                ResizeBoundingBox(handle, worldNewPos);
            }
        }

        // ── Cursor ────────────────────────────────────────────────────────────

        protected Cursor GetResizeCursor(HandleType handle, AvaloniaViewport vp)
        {
            if (handle == HandleType.Body) return new Cursor(StandardCursorType.SizeAll);

            var (sTL, sTR, sBL, sBR) = GetScreenCorners(vp, X, Y, Width, Height);
            var sTC = Mid(sTL, sTR);
            var sBC = Mid(sBL, sBR);
            var sML = Mid(sTL, sBL);
            var sMR = Mid(sTR, sBR);

            Point dir = handle switch
            {
                HandleType.TopLeft      => new(sBR.X - sTL.X, sBR.Y - sTL.Y),
                HandleType.TopRight     => new(sBL.X - sTR.X, sBL.Y - sTR.Y),
                HandleType.BottomLeft   => new(sTR.X - sBL.X, sTR.Y - sBL.Y),
                HandleType.BottomRight  => new(sTL.X - sBR.X, sTL.Y - sBR.Y),
                HandleType.TopCenter    => new(sBC.X - sTC.X, sBC.Y - sTC.Y),
                HandleType.BottomCenter => new(sTC.X - sBC.X, sTC.Y - sBC.Y),
                HandleType.MiddleLeft   => new(sMR.X - sML.X, sMR.Y - sML.Y),
                HandleType.MiddleRight  => new(sML.X - sMR.X, sML.Y - sMR.Y),
                _                      => new(0, 0),
            };

            double angle = (Math.Atan2(dir.Y, dir.X) * 180 / Math.PI + 360 + 22.5) % 360;
            return (int)(angle / 45) switch
            {
                0 or 4 => new Cursor(StandardCursorType.SizeWestEast),
                1 or 5 => new Cursor(StandardCursorType.TopLeftCorner),
                2 or 6 => new Cursor(StandardCursorType.SizeNorthSouth),
                3 or 7 => new Cursor(StandardCursorType.TopRightCorner),
                _      => new Cursor(StandardCursorType.Cross),
            };
        }

        public Rect GetNormalizedRect()
        {
            double x = Width  >= 0 ? X : X + Width;
            double y = Height >= 0 ? Y : Y + Height;
            return new Rect(x, y, Math.Abs(Width), Math.Abs(Height));
        }

        private static Point Mid(Point a, Point b) =>
            new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }
}
