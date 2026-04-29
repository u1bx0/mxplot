using Avalonia;
using Avalonia.Input;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    public class RectObject : BoundingBoxBase, IAnalyzableOverlay
    {
        public RectObject()
        {
            SnapMode = PixelSnapMode.Corner;
        }

        // ── IAnalyzableOverlay ────────────────────────────────────

        public event EventHandler? FindMinMaxRequested;
        public event EventHandler? ToggleShowStatisticsRequested;
        public event EventHandler? UseRoiForValueRangeRequested;
        public bool ShowStatistics { get; set; } = false;
        public RegionStatistics? CachedStatistics { get; set; }
        public bool IsValueRangeRoi { get; set; } = false;

        public void RaiseFindMinMaxRequested() => FindMinMaxRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseToggleShowStatisticsRequested() => ToggleShowStatisticsRequested?.Invoke(this, EventArgs.Empty);
        public void RaiseUseRoiForValueRangeRequested() => UseRoiForValueRangeRequested?.Invoke(this, EventArgs.Empty);

        public bool ContainsWorldPoint(Point worldPoint) =>
            worldPoint.X >= X && worldPoint.X < X + Width &&
            worldPoint.Y >= Y && worldPoint.Y < Y + Height;

        public override string? GetInfo(IMatrixData? data)
        {
            if (data == null || (data.XStep == 0 && data.YStep == 0))
                return $"Rect: W={FmtLen(Math.Abs(Width))}, H={FmtLen(Math.Abs(Height))}";

            double physW = Math.Abs(Width * data.XStep);
            double physH = Math.Abs(Height * data.YStep);
            string xu = data.XUnit ?? "";
            string yu = data.YUnit ?? "";
            string xuStr = xu.Length > 0 ? $" {xu}" : "";
            string yuStr = yu.Length > 0 ? $" {yu}" : "";
            return $"Rect: W={FmtLen(physW)}{xuStr} [{FmtLen(Width)}], H={FmtLen(physH)}{yuStr} [{FmtLen(Height)}]";
        }

        public override void SetCreationBounds(Point start, Point end)
            => SetCreationBoundsInternal(start, end, forceSquare: false);

        public override void SetCreationBounds(Point start, Point end, KeyModifiers modifiers)
        {
            bool isShift = modifiers.HasFlag(KeyModifiers.Shift);
            bool isCtrl  = modifiers.HasFlag(KeyModifiers.Control);
            if (isCtrl)
            {
                // start = center; rect extends symmetrically to end
                double halfW = Math.Abs(end.X - start.X);
                double halfH = Math.Abs(end.Y - start.Y);
                if (isShift) { double s = Math.Max(halfW, halfH); halfW = halfH = s; }
                X      = start.X - halfW;
                Y      = start.Y - halfH;
                Width  = 2 * halfW;
                Height = 2 * halfH;
            }
            else
            {
                SetCreationBoundsInternal(start, end, forceSquare: isShift);
            }
        }

        public override void Draw(AvaloniaOverlayGraphics g)
        {
            if (IsFilled)
                g.FillRectangle(FillColor, X, Y, Width, Height);
            var (color, dash) = GetDrawingPen();
            g.DrawRectangle(color, PenWidth, dash, X, Y, Width, Height, IsScaledPenWidth);
            DrawHandles(g);
            if (ShowStatistics && CachedStatistics.HasValue)
                DrawStatisticsLabel(g, CachedStatistics.Value);
            if (IsValueRangeRoi)
                DrawRoiLabel(g);
        }

        public override HandleType HitTest(Point location, AvaloniaViewport vp) =>
            HitTestBoundingBox(location, vp, testEdges: true, testInterior: IsFilled);

        public override Cursor GetCursor(HandleType handle, AvaloniaViewport vp) =>
            GetResizeCursor(handle, vp);

        public override IEnumerable<OverlayMenuItem>? GetContextMenuItems()
        {
            foreach (var item in base.GetContextMenuItems() ?? [])
                yield return item;
        }
    }
}
