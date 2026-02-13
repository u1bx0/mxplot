using System;
using System.Runtime.CompilerServices;


namespace MxPlot.Core
{
    /// <summary>
    ///  Two-dimensional scale structure
    /// </summary>
    public readonly struct Scale2D : IEquatable<Scale2D>
    {
        public int XCount { get; }
        public double XMin { get; }
        public double XMax { get; }

        public int YCount { get; }
        public double YMin { get; }
        public double YMax { get; }

        public string XUnit { get; }
        public string YUnit { get; }

        // --- Pre-calculated Fields (Cached) ---
        // これらは計算プロパティ(=>)ではなく、フィールドとして保持するため、
        // ループ内で何度アクセスしてもコストはゼロです。
        public double XRange { get; }
        public double YRange { get; }
        public double XStep { get; }
        public double YStep { get; }
        public double XLength { get; }
        public double YLength { get; }

        // --- Constructor ---
        // Each property is initialized only once here.
        public Scale2D(
            int xCount, double xMin, double xMax,
            int yCount, double yMin, double yMax,
            string xUnit = "", string yUnit = "")
        {
            XCount = xCount;
            XMin = xMin;
            XMax = xMax;
            YCount = yCount;
            YMin = yMin;
            YMax = yMax;
            XUnit = xUnit;
            YUnit = yUnit;

            // Calculate once, read forever
            XRange = xMax - xMin;
            YRange = yMax - yMin;

            XStep = xCount > 1 ? (xMax - xMin) / (xCount - 1) : 0;
            YStep = yCount > 1 ? (yMax - yMin) / (yCount - 1) : 0;

            XLength = (xMax - xMin) + XStep;
            YLength = (yMax - yMin) + YStep;
        }

        //Utility methods to get the value at the index
        /// <summary>
        /// = (XStep * ix + XMin); Calculates the X-axis value corresponding to the specified index (ix). 
        /// This method will be inlined for performance.
        /// </summary>
        /// <param name="ix">The zero-based index for which to compute the X-axis value.</param>
        /// <returns>The X-axis value at the specified index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double XValue(int ix) => XStep * ix + XMin;

        /// <summary>
        /// = (YStep * iy + YMin);
        /// Calculates the Y-axis value corresponding to the specified index (iy).
        /// This method will be inlined for performance. 
        /// </summary>
        /// <param name="iy">The zero-based index along the Y-axis for which to compute the value.</param>
        /// <returns>The Y-axis value at the specified index.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double YValue(int iy) => YStep * iy + YMin;


        public override bool Equals(object? obj) => obj is Scale2D other && Equals(other);

        public bool Equals(Scale2D other)
        {
            return XCount == other.XCount && XMin.Equals(other.XMin) && XMax.Equals(other.XMax) &&
                   YCount == other.YCount && YMin.Equals(other.YMin) && YMax.Equals(other.YMax);
        }

        public override int GetHashCode() => HashCode.Combine(XCount, XMin, XMax, YCount, YMin, YMax);

        public static bool operator ==(Scale2D left, Scale2D right) => left.Equals(right);

        public static bool operator !=(Scale2D left, Scale2D right) => !(left == right);

        /// <summary>
        /// Simple factory method to create a Scale2D instance with specified number of points in X and Y dimensions,
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <returns></returns>
        public static Scale2D Pixels(int xnum, int ynum) => new Scale2D(xnum, 0, xnum - 1, ynum, 0, ynum - 1);
        /// <summary>
        /// Simple factory method to create a Scale2D instance with specified number of points in X and Y dimensions and size,
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <returns></returns>
        public static Scale2D Centered(int xnum, int ynum, double width, double height) => new Scale2D(xnum, -width * 0.5, width * 0.5, ynum, -height * 0.5, height * 0.5);


    }

    public static class Scale2DExtension
    {
        /// <summary>
        /// Invokes the specified action for each grid point defined by the two-dimensional scale, providing the
        /// zero-based indices and corresponding coordinate values.
        /// </summary>
        /// <remarks>The method iterates over all points in the grid defined by the scale's XCount and
        /// YCount properties. The action is invoked in row-major order, with the x index varying fastest within each
        /// row.</remarks>
        /// <param name="scale">The two-dimensional scale that defines the grid over which to iterate. Cannot be null.</param>
        /// <param name="action">The action to perform on each grid point. Receives the zero-based x and y indices, followed by the
        /// corresponding x and y coordinate values. Cannot be null.</param>
        public static void ForEach(this Scale2D scale, Action<int, int, double, double> action)
        {
            int xnum = scale.XCount;
            int ynum = scale.YCount;
            double xstep = scale.XStep;
            double ystep = scale.YStep;
            double xmin = scale.XMin;
            double ymin = scale.YMin;
            for (int iy = 0; iy < ynum; iy++)
            {
                double y = iy * ystep + ymin;
                for (int ix = 0; ix < xnum; ix++)
                {
                    double x = ix * xstep + xmin;
                    action(ix, iy, x, y);
                }
            }
        }

        /// <summary>
        /// Executes the specified action for each (x, y) grid point defined by the Scale2D instance, using parallel
        /// processing to improve performance.
        /// </summary>
        /// <remarks>The method iterates over all grid points in the Scale2D instance and invokes the
        /// specified action for each point. The action is executed in parallel across multiple threads along y axis, which can
        /// improve performance for compute-intensive operations. The order in which the action is invoked for each grid
        /// point is not guaranteed. If the action modifies shared state, ensure that appropriate synchronization is
        /// used.</remarks>
        /// <param name="scale">The Scale2D instance that defines the grid dimensions and coordinate ranges to iterate over.</param>
        /// <param name="action">The action to perform for each grid point. The parameters are the zero-based x and y indices, followed by
        /// the corresponding x and y coordinate values.</param>
        /// <param name="parallelism">The maximum number of concurrent tasks to use for parallel execution. Specify -1 to use the default degree
        /// of parallelism, or a positive integer to limit the number of concurrent tasks.</param>
        public static void ParallelForEach(this Scale2D scale, Action<int, int, double, double> action, int parallelism = -1)
        {
            
            int xnum = scale.XCount;
            int ynum = scale.YCount;
            double xstep = scale.XStep;
            double ystep = scale.YStep;
            double xmin = scale.XMin;
            double ymin = scale.YMin;
            ParallelOptions op = new ParallelOptions();
            op.MaxDegreeOfParallelism = parallelism;
            System.Threading.Tasks.Parallel.For(0, ynum, op, iy =>
            {
                double y = iy * ystep + ymin;
                for (int ix = 0; ix < xnum; ix++)
                {
                    double x = ix * xstep + xmin;
                    action(ix, iy, x, y);
                }
            });
        }

       
    }
}
