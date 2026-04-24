using System;

namespace MxPlot.Core.Processing
{

    public enum LineProfileOption
    {
        NearestNeighbor, 
        Bilinear         
    }

    public static class LineProfileExtractor
    {
        /// <summary>
        /// Extracts a line profile along the segment from <paramref name="start"/> to <paramref name="end"/>.
        /// The sampling interval adapts to the line direction so that no pixel is skipped:
        /// <c>step = min(|XStep / ux|, |YStep / uy|)</c> where (ux, uy) is the unit direction vector.
        /// If the segment extends outside the data bounds, it is clipped to the valid region.
        /// </summary>
        /// <returns>
        /// <c>Pos</c>: distance from the clipped start along the line direction (<c>Pos[0] = 0</c>).
        /// <c>Values</c>: sampled data values at each position.
        /// Both arrays are empty if the segment is entirely outside the data bounds.
        /// </returns>
        public static (double[] Pos, double[] Values) GetLineProfile(
            this IMatrixData src,
            (double X, double Y) start,
            (double X, double Y) end,
            int frameIndex = -1,
            LineProfileOption option = LineProfileOption.NearestNeighbor)
        {
            if (frameIndex < 0) frameIndex = src.ActiveIndex;
            if (!ClipSegment(src, ref start, ref end, out _, out _))
                return Empty();

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double clippedLen = Math.Sqrt(dx * dx + dy * dy);

            // Adaptive step: no pixel skipped along either axis
            double absXStep = src.XCount > 1 ? Math.Abs(src.XStep) : 0;
            double absYStep = src.YCount > 1 ? Math.Abs(src.YStep) : 0;
            double absUx = Math.Abs(dx / clippedLen);
            double absUy = Math.Abs(dy / clippedLen);
            const double dirEps = 1e-12;

            double step;
            bool hasUx = absUx > dirEps && absXStep > 0;
            bool hasUy = absUy > dirEps && absYStep > 0;
            if (hasUx && hasUy)
                step = Math.Min(absXStep / absUx, absYStep / absUy);
            else if (hasUx)
                step = absXStep / absUx;
            else if (hasUy)
                step = absYStep / absUy;
            else
                step = 1.0;

            if (step <= 0 || double.IsNaN(step) || double.IsInfinity(step))
                step = 1.0;

            int n = clippedLen < step * 0.5 ? 1 : (int)(clippedLen / step) + 1;
            return SampleLine(src, start, end, clippedLen, n, frameIndex, option);
        }

        /// <summary>
        /// Extracts a line profile resampled to exactly <paramref name="numPoints"/> equally spaced points.
        /// If the segment extends outside the data bounds, it is clipped to the valid region.
        /// </summary>
        /// <param name="numPoints">The number of sample points along the clipped segment. Must be ≥ 1.</param>
        public static (double[] Pos, double[] Values) GetLineProfile(
            this IMatrixData src,
            (double X, double Y) start,
            (double X, double Y) end,
            int numPoints,
            int frameIndex = -1,
            LineProfileOption option = LineProfileOption.NearestNeighbor)
        {
            if (numPoints < 1)
                throw new ArgumentOutOfRangeException(nameof(numPoints), "numPoints must be >= 1.");
            if (frameIndex < 0) frameIndex = src.ActiveIndex;
            if (!ClipSegment(src, ref start, ref end, out _, out _))
                return Empty();

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double clippedLen = Math.Sqrt(dx * dx + dy * dy);

            return SampleLine(src, start, end, clippedLen, numPoints, frameIndex, option);
        }

        // =============================================================
        // Internal helpers
        // =============================================================

        /// <summary>
        /// Clips the segment to the data bounding box using Liang-Barsky.
        /// On output, <paramref name="start"/> and <paramref name="end"/> are replaced
        /// by the clipped endpoints.
        /// </summary>
        private static bool ClipSegment(
            IMatrixData src,
            ref (double X, double Y) start,
            ref (double X, double Y) end,
            out double tMin, out double tMax)
        {
            double xLo = Math.Min(src.XMin, src.XMax);
            double xHi = Math.Max(src.XMin, src.XMax);
            double yLo = Math.Min(src.YMin, src.YMax);
            double yHi = Math.Max(src.YMin, src.YMax);

            double origDx = end.X - start.X;
            double origDy = end.Y - start.Y;
            tMin = 0.0; tMax = 1.0;

            if (!ClipEdge(-origDx, start.X - xLo, ref tMin, ref tMax) ||
                !ClipEdge( origDx, xHi - start.X, ref tMin, ref tMax) ||
                !ClipEdge(-origDy, start.Y - yLo, ref tMin, ref tMax) ||
                !ClipEdge( origDy, yHi - start.Y, ref tMin, ref tMax))
                return false;

            // Replace start/end with clipped endpoints
            var origStart = start;
            start = (origStart.X + tMin * origDx, origStart.Y + tMin * origDy);
            end   = (origStart.X + tMax * origDx, origStart.Y + tMax * origDy);
            return true;
        }

        /// <summary>
        /// Samples <paramref name="n"/> equally spaced points along the clipped segment.
        /// <c>Pos[0]</c> is always 0 (clipped start); <c>Pos[n-1]</c> equals the clipped segment length.
        /// </summary>
        private static (double[] Pos, double[] Values) SampleLine(
            IMatrixData src,
            (double X, double Y) clippedStart,
            (double X, double Y) clippedEnd,
            double clippedLen,
            int n, int frameIndex, LineProfileOption option)
        {
            double cdx = clippedEnd.X - clippedStart.X;
            double cdy = clippedEnd.Y - clippedStart.Y;

            double step = n > 1 ? clippedLen / (n - 1) : 0;
            double ux = clippedLen > 0 ? cdx / clippedLen : 0;
            double uy = clippedLen > 0 ? cdy / clippedLen : 0;

            double[] pos    = new double[n];
            double[] values = new double[n];

            for (int i = 0; i < n; i++)
            {
                double d = i * step;
                pos[i]    = d;
                values[i] = Sample(src,
                    clippedStart.X + ux * d,
                    clippedStart.Y + uy * d,
                    frameIndex, option);
            }

            return (pos, values);
        }

        private static (double[], double[]) Empty()
            => (Array.Empty<double>(), Array.Empty<double>());

        /// <summary>
        /// Liang-Barsky edge clip helper. Returns false if the segment is entirely outside.
        /// </summary>
        private static bool ClipEdge(double p, double q, ref double tMin, ref double tMax)
        {
            const double eps = 1e-15;
            if (Math.Abs(p) < eps)
                return q >= -eps; // parallel: inside if q >= 0

            double t = q / p;
            if (p < 0) { if (t > tMin) tMin = t; }
            else       { if (t < tMax) tMax = t; }

            return tMin <= tMax + eps;
        }

        private static double Sample(IMatrixData src, double x, double y,
            int frameIndex, LineProfileOption option)
        {
            if (option == LineProfileOption.Bilinear)
                return src.GetValueAsDouble(x, y, frameIndex, interpolate: true);

            // NearestNeighbor
            int ix = (int)Math.Round((x - src.XMin) / src.XStep);
            int iy = (int)Math.Round((y - src.YMin) / src.YStep);
            ix = Math.Clamp(ix, 0, src.XCount - 1);
            iy = Math.Clamp(iy, 0, src.YCount - 1);
            return src.GetValueAt(ix, iy, frameIndex);
        }
    }
}
