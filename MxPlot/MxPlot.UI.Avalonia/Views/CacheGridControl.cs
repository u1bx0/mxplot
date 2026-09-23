using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Draws the cache state of every frame as a grid of cells: one cell per frame, indexed
    /// <c>x + y * columns</c>, with a legend above, a column ruler along the top and a row-index
    /// column down the left, so a specific frame can be located without counting cells.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cells are filled rectangles, not block characters in a text run. Glyph cells sit on fractional
    /// pen positions and line pitches, so adjacent glyphs leave seams that pick up the text
    /// renderer's colour fringing, and rows drift apart by a pixel every few lines; both depend on the
    /// display scale and on the theme. Here every edge is snapped to a device pixel and the gap
    /// between cells is a whole number of device pixels, so the grid renders identically at any scale
    /// and in either theme.
    /// </para>
    /// <para>
    /// Every <see cref="BlockSize"/> columns and rows an extra <see cref="BlockGap"/> separates the
    /// cells into blocks, which turns counting along a row into counting blocks of ten.
    /// </para>
    /// <para>
    /// Colours follow the theme: cached cells use the accent colour, uncached cells a faint tint of
    /// the foreground, and the labels the foreground itself.
    /// </para>
    /// </remarks>
    internal sealed class CacheGridControl : Control
    {
        /// <summary>Distance between the origins of adjacent cells, in DIPs.</summary>
        private const double CellPitch = 10;

        /// <summary>Number of columns and rows per block.</summary>
        private const int BlockSize = 10;

        /// <summary>Extra space between blocks, in DIPs.</summary>
        private const double BlockGap = 4;

        private const double LabelFontSize = 10;
        private const double LegendFontSize = 12;
        private const double LegendHeight = 16;
        private const double LegendSwatch = 11;
        private const double LegendItemSpacing = 16;
        private const double LegendToRuler = 8;
        private const double RulerHeight = 16;
        private const double LabelPad = 6;

        /// <summary>
        /// Width, in characters of the legend font, that the legend's own heading is padded to. The
        /// header lines above the grid are laid out in columns of that width, so the legend lines up
        /// with their values.
        /// </summary>
        private const int LegendHeadingChars = 15;

        private static readonly Typeface Mono = new(new FontFamily("Consolas,Courier New,monospace"));

        private int _total;
        private int _columns = 1;
        private HashSet<int> _cached = [];

        public CacheGridControl()
        {
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        }

        private int Rows => _total <= 0 ? 0 : (_total + _columns - 1) / _columns;

        /// <summary>
        /// Sets what is drawn. <paramref name="cached"/> is kept by reference and must not be changed
        /// afterwards. Nothing is invalidated when neither the layout nor the cached set differs from
        /// what is already shown.
        /// </summary>
        /// <param name="total">Number of frames, i.e. of cells.</param>
        /// <param name="columns">Cells per row.</param>
        /// <param name="cached">Indices of the frames currently held in memory.</param>
        public void SetState(int total, int columns, HashSet<int> cached)
        {
            columns = Math.Max(1, columns);
            bool layoutChanged = total != _total || columns != _columns;
            bool cacheChanged = !_cached.SetEquals(cached);
            if (!layoutChanged && !cacheChanged) return;

            _total = total;
            _columns = columns;
            _cached = cached;

            if (layoutChanged) InvalidateMeasure();
            InvalidateVisual();
        }

        /// <summary>
        /// The column count that makes the grid roughly square, rounded up to a whole number of
        /// blocks so that no block is left short and a frame's index reads off as
        /// <c>row * columns + column</c> with a round <c>columns</c>. Fewer than one block's worth of
        /// frames get one column each.
        /// </summary>
        public static int PreferredColumns(int total)
        {
            int square = (int)Math.Ceiling(Math.Sqrt(Math.Max(1, total)));
            int wholeBlocks = (square + BlockSize - 1) / BlockSize * BlockSize;
            return Math.Clamp(wholeBlocks, 1, Math.Max(1, total));
        }

        /// <summary>
        /// The largest number of whole blocks of columns whose grid, with its row labels, fits within
        /// <paramref name="availableWidth"/>, as the exact number of columns that fit when not even
        /// one block does. At least one, and never more than <paramref name="total"/>.
        /// </summary>
        public int ColumnsFitting(double availableWidth, int total)
        {
            double labelWidth = LabelWidth(DigitsOf(Math.Max(0, total - 1)));
            double room = availableWidth - Margin.Left - Margin.Right - labelWidth;

            int columns = (int)Math.Floor(room / CellPitch);
            while (columns > 1 && Extent(columns) > room) columns--;

            // Rounding down only shrinks the grid, so it still fits.
            if (columns >= BlockSize) columns -= columns % BlockSize;

            return Math.Clamp(columns, 1, Math.Max(1, total));
        }

        // ── Layout ────────────────────────────────────────────────────────

        /// <summary>Length of <paramref name="count"/> cells along one axis, block gaps included.</summary>
        private static double Extent(int count) =>
            count <= 0 ? 0 : count * CellPitch + ((count - 1) / BlockSize) * BlockGap;

        /// <summary>Offset of the origin of the cell at <paramref name="index"/> along one axis.</summary>
        private static double Offset(int index) => index * CellPitch + (index / BlockSize) * BlockGap;

        private static int DigitsOf(int value) => value.ToString(CultureInfo.InvariantCulture).Length;

        private static FormattedText Text(string text, double size, IBrush brush) =>
            new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, size, brush);

        private static double LabelWidth(int digits) =>
            Text(new string('0', Math.Max(1, digits)), LabelFontSize, Brushes.Black).Width + LabelPad;

        private double GridTop => LegendHeight + LegendToRuler + RulerHeight;

        protected override Size MeasureOverride(Size availableSize)
        {
            if (_total <= 0) return default;

            int rows = Rows;
            double gridWidth = LabelWidth(DigitsOf(rows - 1)) + Extent(_columns);
            double legendWidth = DrawLegend(null, Brushes.Black, Brushes.Black, Brushes.Black);

            return new Size(Math.Max(gridWidth, legendWidth), GridTop + Extent(rows));
        }

        // ── Rendering ─────────────────────────────────────────────────────

        public override void Render(DrawingContext context)
        {
            if (_total <= 0) return;

            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            var fg = TextElement.GetForeground(this) is ISolidColorBrush solid ? solid.Color : Colors.Gray;
            var accent = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var found) && found is Color c
                ? c
                : Colors.DodgerBlue;

            var cachedBrush = new SolidColorBrush(accent);
            var uncachedBrush = new SolidColorBrush(fg, 0.12);
            var labelBrush = new SolidColorBrush(fg, 0.75);
            var tickBrush = new SolidColorBrush(fg, 0.4);
            var textBrush = new SolidColorBrush(fg);

            DrawLegend(context, cachedBrush, uncachedBrush, textBrush);

            int rows = Rows;
            double originX = LabelWidth(DigitsOf(rows - 1));
            double originY = GridTop;

            // The gap between cells is a whole number of device pixels, so every seam is the same
            // width whatever the scale; every edge is placed on a device pixel for the same reason.
            double gap = Math.Max(1, Math.Round(scale)) / scale;
            double Snap(double v) => Math.Round(v * scale) / scale;

            Rect CellRect(int col, int row)
            {
                double left = Snap(originX + Offset(col));
                double top = Snap(originY + Offset(row));
                double right = Snap(originX + Offset(col) + CellPitch) - gap;
                double bottom = Snap(originY + Offset(row) + CellPitch) - gap;
                return new Rect(left, top, right - left, bottom - top);
            }

            // Ruler: a number at the start of each block, a tick over every column.
            for (int x = 0; x < _columns; x++)
            {
                var cell = CellRect(x, 0);
                double tickHeight = x % BlockSize == 0 ? 7 : x % 5 == 0 ? 5 : 3;
                double tickX = Snap(cell.X + (cell.Width - gap) / 2);
                context.FillRectangle(tickBrush, new Rect(tickX, originY - LegendToRuler / 2 - tickHeight + 4, gap, tickHeight));

                if (x % BlockSize != 0) continue;
                var label = Text(x.ToString(CultureInfo.InvariantCulture), LabelFontSize, labelBrush);
                context.DrawText(label, new Point(cell.X, LegendHeight + LegendToRuler - 2));
            }

            // Row labels and cells.
            int frame = 0;
            for (int y = 0; y < rows; y++)
            {
                var first = CellRect(0, y);
                var rowLabel = Text(y.ToString(CultureInfo.InvariantCulture), LabelFontSize, labelBrush);
                context.DrawText(rowLabel, new Point(originX - LabelPad - rowLabel.Width, first.Y + (first.Height - rowLabel.Height) / 2));

                for (int x = 0; x < _columns && frame < _total; x++, frame++)
                    context.FillRectangle(_cached.Contains(frame) ? cachedBrush : uncachedBrush, CellRect(x, y));
            }
        }

        /// <summary>
        /// Draws the legend row, or only measures it when <paramref name="context"/> is
        /// <see langword="null"/>. Returns its width.
        /// </summary>
        private double DrawLegend(DrawingContext? context, IBrush cachedBrush, IBrush uncachedBrush, IBrush textBrush)
        {
            var heading = Text("[Legend]", LegendFontSize, textBrush);
            double x = Text(new string('0', LegendHeadingChars), LegendFontSize, textBrush).Width;
            double textTop = (LegendHeight - heading.Height) / 2;
            double swatchTop = (LegendHeight - LegendSwatch) / 2;

            context?.DrawText(heading, new Point(0, textTop));

            var cachedText = Text(" = cached in RAM", LegendFontSize, textBrush);
            context?.FillRectangle(cachedBrush, new Rect(x, swatchTop, LegendSwatch, LegendSwatch));
            x += LegendSwatch;
            context?.DrawText(cachedText, new Point(x, textTop));
            x += cachedText.Width + LegendItemSpacing;

            var uncachedText = Text(" = not cached (reads from disk on next access)", LegendFontSize, textBrush);
            context?.FillRectangle(uncachedBrush, new Rect(x, swatchTop, LegendSwatch, LegendSwatch));
            x += LegendSwatch;
            context?.DrawText(uncachedText, new Point(x, textTop));
            return x + uncachedText.Width;
        }
    }
}
