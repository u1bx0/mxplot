using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MxPlot.UI.Avalonia.Overlays.Shapes
{
    /// <summary>
    /// A text label overlay with a resizable bounding-box border and clipped text rendering.
    /// </summary>
    public sealed class TextObject : BoundingBoxBase
    {
        public string Text            { get; set; } = "Text";
        public double FontSize        { get; set; } = 12.0;
        public string FontFamily      { get; set; } = "Arial";
        public Color  BackgroundColor  { get; set; } = Color.FromArgb(180, 255, 255, 255);
        public bool   ShowBackground   { get; set; } = true;
        public bool   ShowBorder        { get; set; } = true;
        /// <summary>
        /// When <c>true</c>, font size scales proportionally with the viewport zoom
        /// so that 100% zoom corresponds to the raw <see cref="FontSize"/> value.
        /// </summary>
        public bool   ScaleFontWithZoom { get; set; } = true;

        /// <summary>
        /// The data source used to resolve format tokens such as <c>{i}</c> and <c>{p}</c>.
        /// Set by <see cref="MatrixPlotter"/> when this object is added to an overlay manager.
        /// </summary>
        public IMatrixData? DataContext { get; set; }

        /// <summary>Raised when the user selects "Edit…" from the context menu.</summary>
        public event EventHandler? EditRequested;

        /// <summary>Double-click opens the text editor rather than the pen editor.</summary>
        public override void OnDoubleClicked() => EditRequested?.Invoke(this, EventArgs.Empty);

        public TextObject() { PenColor = Colors.Black; }

        public TextObject(double x, double y, string text) : this()
        {
            X = x; Y = y; Text = text;
            Width  = 80;
            Height = 28;
        }

        public override void SetCreationBounds(Point start, Point end)
        {
            SetCreationBoundsInternal(start, end);
            //if (Width  < 30) Width  = 80;
            //if (Height < 16) Height = 28;
        }

        public override void Draw(AvaloniaOverlayGraphics g)
        {
            if (Width <= 0 || Height <= 0) return;

            if (ShowBackground)
                g.FillRectangle(BackgroundColor, X, Y, Width, Height);

            var (color, dash) = GetDrawingPen();
            if (ShowBorder)
                g.DrawRectangle(color, PenWidth, dash, X, Y, Width, Height, IsScaledPenWidth);

            g.DrawStringInRect(GetFormattedText(), PenColor, X, Y, Width, Height,
                screenPad: 4.0, fontSize: FontSize, fontFamily: FontFamily,
                scaleWithZoom: ScaleFontWithZoom);

            DrawHandles(g);
        }

        public override HandleType HitTest(Point location, AvaloniaViewport vp)
        {
            var result = HitTestBoundingBox(location, vp, testEdges: true);
            if (result != HandleType.None) return result;

            // Interior fill: also selectable when clicking inside the box
            var tl = vp.WorldToScreen(new Point(X,         Y));
            var br = vp.WorldToScreen(new Point(X + Width, Y + Height));
            var rect = new Rect(Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y),
                                Math.Abs(br.X - tl.X), Math.Abs(br.Y - tl.Y));
            return rect.Contains(location) ? HandleType.Body : HandleType.None;
        }

        /// <summary>
        /// Resolves format tokens in <see cref="Text"/> against the current axis state of <see cref="DataContext"/>.
        /// </summary>
        /// <remarks>
        /// Supported tokens:
        /// <c>{i}</c> = index of axis 0,
        /// <c>{p}</c> = value of axis 0,
        /// <c>{N:i}</c> = index of axis N,
        /// <c>{N:p}</c> = value of axis N,
        /// <c>{N:pFn}</c> = value of axis N formatted to n decimal places.
        /// </remarks>
        private string GetFormattedText()
        {
            if (DataContext == null || DataContext.Axes.Count == 0 || !Text.Contains('{'))
                return Text;
            try
            {
                return Regex.Replace(Text, @"\{((\d*:|)[ip](F\d)*)\}", m =>
                {
                    string inner = m.Groups[1].Value;
                    int axisOrder;
                    string v;
                    if (inner.Contains(':'))
                    {
                        var parts = inner.Split(':', 2);
                        axisOrder = int.Parse(parts[0]);
                        v = parts[1];
                    }
                    else
                    {
                        axisOrder = 0;
                        v = inner;
                    }
                    if (axisOrder >= DataContext.Axes.Count)
                        return "NaN";
                    if (v.StartsWith("i", StringComparison.OrdinalIgnoreCase))
                        return DataContext.Axes[axisOrder].Index.ToString();
                    double pos = DataContext.Axes[axisOrder].Value;
                    var dp = v.Split('F', 2);
                    if (dp.Length == 2 && int.TryParse(dp[1], out int decimals))
                        return pos.ToString($"F{decimals}");
                    return pos.ToString();
                });
            }
            catch
            {
                return Text;
            }
        }

        public override void Move(double dx, double dy) { X += dx; Y += dy; }

        public override void Resize(HandleType handle, Point worldNewPos) =>
            ResizeBoundingBox(handle, worldNewPos);

        public override Cursor GetCursor(HandleType handle, AvaloniaViewport vp) =>
            GetResizeCursor(handle, vp);

        public override IEnumerable<OverlayMenuItem>? GetContextMenuItems()
        {
            yield return new OverlayMenuItem("Edit\u2026",
                () => EditRequested?.Invoke(this, EventArgs.Empty));
            yield return OverlayMenuItem.Separator();
            foreach (var item in base.GetContextMenuItems() ?? Enumerable.Empty<OverlayMenuItem>())
                yield return item;
        }
    }
}
