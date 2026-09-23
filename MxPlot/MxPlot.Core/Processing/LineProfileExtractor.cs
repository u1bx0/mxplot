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
        /// <param name="src">The data to sample.</param>
        /// <param name="start">Start of the segment, in physical coordinates (those of <c>XMin</c>…<c>YMax</c>).</param>
        /// <param name="end">End of the segment, in physical coordinates.</param>
        /// <param name="frameIndex">The frame to sample. A negative value (the default) selects <c>ActiveIndex</c>.</param>
        /// <param name="option">How a value between pixels is obtained: the nearest pixel, or bilinear interpolation.</param>
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
            int n = ComputeAdaptiveSampleCount(src, dx, dy, clippedLen);

            return SampleLine(start, end, clippedLen, n,
                (x, y) => Sample(src, x, y, frameIndex, option));
        }

        /// <summary>
        /// Extracts a line profile resampled to exactly <paramref name="numPoints"/> equally spaced points.
        /// If the segment extends outside the data bounds, it is clipped to the valid region.
        /// </summary>
        /// <param name="src">The data to sample.</param>
        /// <param name="start">Start of the segment, in physical coordinates (those of <c>XMin</c>…<c>YMax</c>).</param>
        /// <param name="end">End of the segment, in physical coordinates.</param>
        /// <param name="numPoints">The number of sample points along the clipped segment. Must be ≥ 1.</param>
        /// <param name="frameIndex">The frame to sample. A negative value (the default) selects <c>ActiveIndex</c>.</param>
        /// <param name="option">How a value between pixels is obtained: the nearest pixel, or bilinear interpolation.</param>
        /// <returns>
        /// <c>Pos</c>: distance from the clipped start along the line direction (<c>Pos[0] = 0</c>).
        /// <c>Values</c>: sampled data values at each position.
        /// Both arrays are empty if the segment is entirely outside the data bounds.
        /// </returns>
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

            return SampleLine(start, end, clippedLen, numPoints,
                (x, y) => Sample(src, x, y, frameIndex, option));
        }

        /// <summary>
        /// Extracts a line profile along the segment from <paramref name="start"/> to <paramref name="end"/>,
        /// reading samples directly from a strongly-typed <see cref="MatrixData{T}"/>.
        /// The sampling interval adapts to the line direction so that no pixel is skipped (see the
        /// <see cref="IMatrixData"/> overload for the exact step formula). If the segment extends
        /// outside the data bounds, it is clipped to the valid region.
        /// </summary>
        /// <typeparam name="T">The element type of <paramref name="src"/>.</typeparam>
        /// <param name="src">The data to sample.</param>
        /// <param name="start">Start of the segment, in physical coordinates (those of <c>XMin</c>…<c>YMax</c>).</param>
        /// <param name="end">End of the segment, in physical coordinates.</param>
        /// <param name="frameIndex">The frame to sample. A negative value (the default) selects <c>ActiveIndex</c>.</param>
        /// <param name="option">How a value between pixels is obtained: the nearest pixel, or bilinear interpolation.</param>
        /// <param name="valueConverter">
        /// Reduces a sampled <typeparamref name="T"/> value to <c>double</c>.
        /// Unnecessary (and ignored) when <typeparamref name="T"/> is one of
        /// <see cref="MatrixData.SupportedPrimitiveTypes"/>. Required for any other <typeparamref name="T"/>
        /// (e.g. <c>Complex</c>) — throws <see cref="ArgumentNullException"/> if omitted.
        /// For <see cref="LineProfileOption.Bilinear"/>, interpolation happens in <typeparamref name="T"/>-space
        /// first (via <see cref="MatrixData{T}.GetValue(double, double, int, bool)"/>), and <paramref name="valueConverter"/>
        /// is applied to the interpolated result — not to the four corner values independently.
        /// </param>
        /// <returns>
        /// <c>Pos</c>: distance from the clipped start along the line direction (<c>Pos[0] = 0</c>).
        /// <c>Values</c>: sampled data values at each position.
        /// Both arrays are empty if the segment is entirely outside the data bounds.
        /// </returns>
        public static (double[] Pos, double[] Values) GetLineProfile<T>(
            this MatrixData<T> src,
            (double X, double Y) start,
            (double X, double Y) end,
            int frameIndex = -1,
            LineProfileOption option = LineProfileOption.NearestNeighbor,
            Func<T, double>? valueConverter = null)
            where T : unmanaged
        {
            ValidateConverter<T>(valueConverter);
            if (frameIndex < 0) frameIndex = src.ActiveIndex;
            if (!ClipSegment(src, ref start, ref end, out _, out _))
                return Empty();

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double clippedLen = Math.Sqrt(dx * dx + dy * dy);
            int n = ComputeAdaptiveSampleCount(src, dx, dy, clippedLen);

            return SampleLine(start, end, clippedLen, n,
                (x, y) => SampleTyped(src, x, y, frameIndex, option, valueConverter));
        }

        /// <summary>
        /// Extracts a line profile resampled to exactly <paramref name="numPoints"/> equally spaced points,
        /// reading samples directly from a strongly-typed <see cref="MatrixData{T}"/>.
        /// If the segment extends outside the data bounds, it is clipped to the valid region.
        /// </summary>
        /// <typeparam name="T">The element type of <paramref name="src"/>.</typeparam>
        /// <param name="src">The data to sample.</param>
        /// <param name="start">Start of the segment, in physical coordinates (those of <c>XMin</c>…<c>YMax</c>).</param>
        /// <param name="end">End of the segment, in physical coordinates.</param>
        /// <param name="numPoints">The number of sample points along the clipped segment. Must be ≥ 1.</param>
        /// <param name="frameIndex">The frame to sample. A negative value (the default) selects <c>ActiveIndex</c>.</param>
        /// <param name="option">How a value between pixels is obtained: the nearest pixel, or bilinear interpolation.</param>
        /// <param name="valueConverter">See the adaptive-step overload for the exact contract.</param>
        /// <returns>
        /// <c>Pos</c>: distance from the clipped start along the line direction (<c>Pos[0] = 0</c>).
        /// <c>Values</c>: sampled data values at each position.
        /// Both arrays are empty if the segment is entirely outside the data bounds.
        /// </returns>
        public static (double[] Pos, double[] Values) GetLineProfile<T>(
            this MatrixData<T> src,
            (double X, double Y) start,
            (double X, double Y) end,
            int numPoints,
            int frameIndex = -1,
            LineProfileOption option = LineProfileOption.NearestNeighbor,
            Func<T, double>? valueConverter = null)
            where T : unmanaged
        {
            if (numPoints < 1)
                throw new ArgumentOutOfRangeException(nameof(numPoints), "numPoints must be >= 1.");
            ValidateConverter<T>(valueConverter);
            if (frameIndex < 0) frameIndex = src.ActiveIndex;
            if (!ClipSegment(src, ref start, ref end, out _, out _))
                return Empty();

            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double clippedLen = Math.Sqrt(dx * dx + dy * dy);

            return SampleLine(start, end, clippedLen, numPoints,
                (x, y) => SampleTyped(src, x, y, frameIndex, option, valueConverter));
        }

        // =============================================================
        // Internal helpers
        // =============================================================

        /// <summary>
        /// Computes the adaptive sample count for the clipped segment: the sampling interval adapts to
        /// the line direction so that no pixel is skipped along either axis
        /// (<c>step = min(|XStep / ux|, |YStep / uy|)</c> where (ux, uy) is the unit direction vector).
        /// </summary>
        private static int ComputeAdaptiveSampleCount(IMatrixData src, double dx, double dy, double clippedLen)
        {
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

            return clippedLen < step * 0.5 ? 1 : (int)(clippedLen / step) + 1;
        }

        /// <summary>
        /// Validates that a converter was supplied whenever <typeparamref name="T"/> is not one of
        /// <see cref="MatrixData.SupportedPrimitiveTypes"/>.
        /// </summary>
        private static void ValidateConverter<T>(Func<T, double>? valueConverter) where T : unmanaged
        {
            if (valueConverter is null && !MatrixData.SupportedPrimitiveTypes.Contains(typeof(T)))
                throw new ArgumentNullException(nameof(valueConverter),
                    $"valueConverter is required for non-primitive type {typeof(T).Name}.");
        }

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
            (double X, double Y) clippedStart,
            (double X, double Y) clippedEnd,
            double clippedLen,
            int n,
            Func<double, double, double> sampleAt)
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
                double x = clippedStart.X + ux * d;
                double y = clippedStart.Y + uy * d;
                pos[i]    = d;
                values[i] = sampleAt(x, y);
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

        /// <summary>
        /// Typed counterpart of <see cref="Sample(IMatrixData, double, double, int, LineProfileOption)"/>.
        /// For primitive <typeparamref name="T"/>, delegates to the same <see cref="IMatrixData"/>-level
        /// accessors used by the type-erased overloads. For non-primitive <typeparamref name="T"/>
        /// (e.g. <c>Complex</c>), sampling/interpolation happens in <typeparamref name="T"/>-space via
        /// <see cref="MatrixData{T}.GetValue(double, double, int, bool)"/> / <see cref="MatrixData{T}.GetValueAtTyped"/>,
        /// and <paramref name="valueConverter"/> reduces the result to <c>double</c>.
        /// </summary>
        private static double SampleTyped<T>(MatrixData<T> src, double x, double y,
            int frameIndex, LineProfileOption option, Func<T, double>? valueConverter)
            where T : unmanaged
        {
            bool isPrimitive = MatrixData.SupportedPrimitiveTypes.Contains(typeof(T));

            if (option == LineProfileOption.Bilinear)
            {
                if (isPrimitive)
                    return src.GetValueAsDouble(x, y, frameIndex, interpolate: true);

                // Interpolate structurally in T-space first (GetValue falls back to nearest-neighbor
                // internally when no IBilinearInterpolator<T> is registered for T), then reduce to double.
                T interpolated = src.GetValue(x, y, frameIndex, interpolate: true);
                return valueConverter!(interpolated);
            }

            // NearestNeighbor
            int ix = (int)Math.Round((x - src.XMin) / src.XStep);
            int iy = (int)Math.Round((y - src.YMin) / src.YStep);
            ix = Math.Clamp(ix, 0, src.XCount - 1);
            iy = Math.Clamp(iy, 0, src.YCount - 1);

            if (isPrimitive)
                return src.GetValueAt(ix, iy, frameIndex);

            return valueConverter!(src.GetValueAtTyped(ix, iy, frameIndex));
        }
    }
}
