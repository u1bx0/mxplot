using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Turns an <see cref="ExtremumIndexOperation"/> result (winner index + winner value, no
    /// colour) into a packed-ARGB image. Deliberately separate from Core: it needs a depth-colour
    /// palette (<c>int[]</c>, ARGB) and intensity range, which are UI/rendering concerns, not data
    /// ones. See Tests.Documents/Working/ColorCoded/ColorCoded_View_InitialDesign.md section 3.3.3.
    /// <para>
    /// The live ColorCoded projection window no longer calls this: its own MatrixData is the
    /// winner *value* matrix directly, and <see cref="ColorCodedBitmapWriter"/> colorizes at
    /// render time instead (same per-pixel formula, a separate implementation -- see that class's
    /// doc comment for why). This eager, one-shot form remains the right shape for Phase 3's
    /// "Create Data" bake handler (not yet implemented), which genuinely wants a materialized
    /// packed-ARGB <c>MatrixData&lt;int&gt;</c> to RGB-decompose and save, not a live render.
    /// </para>
    /// </summary>
    public static class ColorCodedColorizer
    {
        /// <summary>
        /// Colorizes a winner-take-all result.
        /// </summary>
        /// <param name="winnerIndex">
        /// The winning axis index per pixel (<c>MatrixData&lt;int&gt;</c>), as produced by
        /// <see cref="Core.Processing.ExtremumIndexOperation"/>.
        /// </param>
        /// <param name="winnerValue">
        /// The value found at that index per pixel, same numeric type as the source data.
        /// </param>
        /// <param name="start">
        /// The <c>Start</c> that was passed to the <see cref="Core.Processing.ExtremumIndexOperation"/>
        /// that produced <paramref name="winnerIndex"/> -- needed to map an absolute axis index back
        /// to a position in <paramref name="depthColors"/>.
        /// </param>
        /// <param name="valueMin">Intensity normalization lower bound (maps to fully dark).</param>
        /// <param name="valueMax">Intensity normalization upper bound (maps to full brightness).</param>
        /// <param name="depthColors">
        /// ARGB colour per swept slice, length <c>End - Start + 1</c> (indexed by
        /// <c>winnerIndex - start</c>), matching <see cref="Imaging.LookupTable.AsSpan"/>'s format.
        /// There is no separate <c>invert</c> flag here: Invert means "reverse which end of the
        /// palette maps to which end of the axis" -- the same semantics the live ColorCoded view
        /// uses (<c>OrthogonalViewController.ComputeXYProjectionAsync</c>) -- so a caller that wants
        /// an inverted bake passes an already-<see cref="Array.Reverse(Array)"/>d array here, not a
        /// flag consumed internally.
        /// </param>
        /// <returns>A packed-ARGB <c>MatrixData&lt;int&gt;</c>, same width/height as the inputs.</returns>
        /// <exception cref="NotSupportedException">
        /// Thrown if <paramref name="winnerValue"/>'s value type is not one this handles (must be
        /// numeric and orderable -- the same requirement <see cref="Core.Processing.ExtremumIndexOperation"/>
        /// itself has; <c>Complex</c> in particular is excluded there too).
        /// </exception>
        public static MatrixData<int> Colorize(
            IMatrixData winnerIndex, IMatrixData winnerValue, int start,
            double valueMin, double valueMax, IReadOnlyList<int> depthColors)
        {
            var indexData = (MatrixData<int>)winnerIndex;

            return winnerValue.ValueType switch
            {
                Type t when t == typeof(byte) => ColorizeTyped((MatrixData<byte>)winnerValue, indexData, start, valueMin, valueMax, depthColors),
                Type t when t == typeof(ushort) => ColorizeTyped((MatrixData<ushort>)winnerValue, indexData, start, valueMin, valueMax, depthColors),
                Type t when t == typeof(short) => ColorizeTyped((MatrixData<short>)winnerValue, indexData, start, valueMin, valueMax, depthColors),
                Type t when t == typeof(int) => ColorizeTyped((MatrixData<int>)winnerValue, indexData, start, valueMin, valueMax, depthColors),
                Type t when t == typeof(float) => ColorizeTyped((MatrixData<float>)winnerValue, indexData, start, valueMin, valueMax, depthColors),
                Type t when t == typeof(double) => ColorizeTyped((MatrixData<double>)winnerValue, indexData, start, valueMin, valueMax, depthColors),
                _ => throw new NotSupportedException(
                    $"ColorCodedColorizer does not support value type '{winnerValue.ValueType}'."),
            };
        }

        private static MatrixData<int> ColorizeTyped<T>(
            MatrixData<T> winnerValue, MatrixData<int> winnerIndex, int start,
            double valueMin, double valueMax, IReadOnlyList<int> depthColors)
            where T : unmanaged, INumber<T>
        {
            int width = winnerValue.XCount;
            int height = winnerValue.YCount;
            var idxArray = winnerIndex.GetArray(0);
            var valArray = winnerValue.GetArray(0);
            var result = new int[width * height];

            double range = valueMax - valueMin;
            int colorCount = depthColors.Count;

            Parallel.For(0, height, y =>
            {
                int rowStart = y * width;
                for (int x = 0; x < width; x++)
                {
                    int pixel = rowStart + x;
                    int localIndex = Math.Clamp(idxArray[pixel] - start, 0, colorCount - 1);
                    int baseColor = depthColors[localIndex];

                    double t = range > 0
                        ? Math.Clamp((double.CreateChecked(valArray[pixel]) - valueMin) / range, 0.0, 1.0)
                        : 1.0;

                    byte r = (byte)((byte)(baseColor >> 16) * t);
                    byte g = (byte)((byte)(baseColor >> 8) * t);
                    byte b = (byte)((byte)baseColor * t);
                    result[pixel] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
                }
            });

            var md = new MatrixData<int>(width, height, result);
            md.SetXYScale(winnerValue.XMin, winnerValue.XMax, winnerValue.YMin, winnerValue.YMax);
            md.XUnit = winnerValue.XUnit;
            md.YUnit = winnerValue.YUnit;
            return md;
        }
    }
}
