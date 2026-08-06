using MxPlot.Core;
using System;
using System.Linq;
using System.Threading;

namespace MxPlot.App.Services
{
    /// <summary>
    /// Static utility class for generating sample/test datasets for demonstration purposes.
    /// </summary>
    internal static class TestDataGenerator
    {
        /// <summary>
        /// Generates 8 Julia set frames (1024×1024 float), each with a visually distinct
        /// complex parameter c chosen to showcase different fractal morphologies.
        /// Smooth escape-time with gamma=0.5 compression: brighter than log but still
        /// distributes values toward boundary detail, keeping the interior dark and the
        /// full LUT gradient visible across thin boundary zones.
        /// Value 0 = inside; >0 = escaped (higher = slower escape / nearer to boundary).
        /// </summary>
        public static MatrixData<float> GenerateJuliaSet()
        {
            const int w = 1024, h = 1024, maxIter = 1024;
            const double r = 1.65; // view half-extent

            // Carefully chosen c values covering spirals, dendrites, rabbits, discs, flowers
            (double cRe, double cIm, string name)[] specs =
            [
                (-0.7269,   0.1889,  "Simonini spirals"),    // 0: fine spiral arms
                (-0.70176, -0.3842,  "Snowflake dendrite"),  // 1: spiky crystalline
                ( 0.285,    0.010,   "Disk packing"),        // 2: nested discs
                (-0.4,      0.600,   "Classic spirals"),     // 3: large open spirals
                (-0.835,   -0.232,   "Crystal dendrite"),    // 4: dendritic branches
                (-0.7,      0.27,    "Douady rabbit"),       // 5: three-lobe rabbit
                ( 0.000,    0.640,   "Siegel disc"),         // 6: smooth rotation disc
                (-0.1,      0.651,   "Flower / petals"),     // 7: petal-like lobes
            ];

            var md = new MatrixData<float>(w, h, specs.Length);
            md.SetXYScale(-r, r, -r, r);
            md.Axes[0].Step = 0.1;

            // Gamma-compressed smooth escape: val = (smoothed / maxIter)^gamma
            // gamma=0.5 is a good middle ground — brighter than log, more contrast than linear
            const double gamma = 0.5;

            System.Threading.Tasks.Parallel.For(0, specs.Length, frame =>
            {
                var (cRe, cIm, _) = specs[frame];
                var arr = md.GetArray(frame);
                for (int py = 0; py < h; py++)
                {
                    double zy0 = -r + py * (2.0 * r) / (h - 1.0);
                    for (int px = 0; px < w; px++)
                    {
                        double zx = -r + px * (2.0 * r) / (w - 1.0);
                        double zy = zy0;
                        int iter = 0;
                        while (zx * zx + zy * zy <= 4.0 && iter < maxIter)
                        {
                            double tmp = zx * zx - zy * zy + cRe;
                            zy = 2.0 * zx * zy + cIm;
                            zx = tmp;
                            iter++;
                        }
                        if (iter >= maxIter)
                        {
                            arr[py * w + px] = 0f;
                        }
                        else
                        {
                            double mod = Math.Sqrt(zx * zx + zy * zy);
                            if (mod < 1.0 + 1e-15) mod = 1.0 + 1e-15;
                            double smooth = iter + 1.0 - Math.Log(Math.Log(mod)) / Math.Log(2.0);
                            smooth = Math.Clamp(smooth, 0.0, maxIter - 1.0);
                            arr[py * w + px] = (float)Math.Pow(smooth / (maxIter - 1.0), gamma);
                        }
                    }
                }
            });
            return md;
        }

        /// <summary>
        /// Generates a 3ch × Z=81 × T=5 Mandelbulb hyperstack.
        /// <list type="bullet">
        ///   <item>C (channel) — Mandelbulb power n: 2, 4, 6</item>
        ///   <item>Z — evenly-spaced cross-section slices through the bulb</item>
        ///   <item>T — Y-axis rotation angle (0°–90°),</item>
        /// </list>
        /// Interior pixels = 0; escaped pixels = smooth escape value in (0, 1].
        /// </summary>
        public static MatrixData<float> GenerateMandelbulb(IProgress<double>? progress = null)
        {
            const int w = 192, h = 192, cNum = 3, zNum = 81, tNum = 5;
            const int maxIter = 30;
            const double extent = 1.5;
            const double escapeR = 2.0;
            const double gamma = 0.95;

            int[] powers = [2, 3, 5];
            int total = cNum * zNum * tNum;
            int completed = 0;

            var md = new MatrixData<float>(w, h, total);
            md.SetXYScale(-extent, extent, -extent, extent);
            md.XUnit = "";
            md.YUnit = "";
            md.DefineDimensions(
                new ColorChannel(["n=2", "n=4", "n=6"]),
                Axis.Z(zNum, -extent, extent, ""),
                Axis.Time(tNum, 0.0, 330.0, "°"));

            System.Threading.Tasks.Parallel.For(0, total, frame =>
            {
                var (ic, iz, it) = md.Dimensions.GetAxisIndicesStruct(frame);
                int n = powers[ic];
                double logN = Math.Log(n);
                double logEsc = Math.Log(escapeR);
                double rotY = it * Math.PI * 0.5 / tNum;
                double cosR = Math.Cos(rotY), sinR = Math.Sin(rotY);
                // Z: evenly-spaced cross-section planes through the bulb
                double zPlane = -extent + iz * (2.0 * extent) / (zNum - 1.0);
                var arr = md.GetArray(frame);

                for (int py = 0; py < h; py++)
                {
                    double sy = -extent + py * (2.0 * extent) / (h - 1.0);
                    for (int px = 0; px < w; px++)
                    {
                        double sx = -extent + px * (2.0 * extent) / (w - 1.0);
                        // Rotate (sx, zPlane) around Y-axis to get the 3-D seed point c
                        double cx = sx * cosR - zPlane * sinR;
                        double cy = sy;
                        double cz = sx * sinR + zPlane * cosR;

                        double x = cx, y = cy, z = cz;
                        int iter = 0;
                        double minOrbitR = double.MaxValue;

                        while (iter < maxIter)
                        {
                            double r2 = x * x + y * y + z * z;
                            double r = Math.Sqrt(r2);
                            if (r < minOrbitR) minOrbitR = r;
                            if (r2 > escapeR * escapeR) break;
                            double theta = Math.Atan2(Math.Sqrt(x * x + y * y), z);
                            double phi = Math.Atan2(y, x);
                            double rn = Math.Pow(r, n);
                            double nt = n * theta;
                            double np = n * phi;
                            double sinT = Math.Sin(nt);
                            x = rn * sinT * Math.Cos(np) + cx;
                            y = rn * sinT * Math.Sin(np) + cy;
                            z = rn * Math.Cos(nt) + cz;
                            iter++;
                        }

                        if (iter >= maxIter)
                        {
                            // Interior: orbit trap → concentric spherical shell banding
                            double normTrap = minOrbitR / extent;
                            double band = 0.5 + 0.5 * Math.Sin(normTrap * Math.PI * 1.5);
                            arr[py * w + px] = (float)(0.08 + band * 0.20) * 0.1f;
                        }
                        else
                        {
                            // Exterior: smooth escape coloring with gamma compression
                            double rFinal = Math.Sqrt(x * x + y * y + z * z);
                            double logR = Math.Log(Math.Max(rFinal, 1.0 + 1e-15));
                            double smooth = iter - Math.Log(logR / logEsc) / logN;
                            smooth = Math.Clamp(smooth, 0.0, maxIter - 1.0);
                            arr[py * w + px] = (float)Math.Pow(smooth / (maxIter - 1.0), gamma);
                        }
                    }
                }

                int done = Interlocked.Increment(ref completed);
                if (done % 10 == 0 || done == total)
                    progress?.Report(done * 100.0 / total);
            });
            return md;
        }

        /// <summary>
        /// Generates a simple 41×41 2D test dataset where each pixel value is the product of its row and column indices.
        /// </summary>
        public static MatrixData<ushort> Generate2DTestData()
        {
            const int xnum = 41;
            const int ynum = 41;
            var md = new MatrixData<ushort>(xnum, ynum);
            md.SetXYScale(-10, 10, -10, 10);
            md.Set((ix, iy, x, y) => (ushort)(iy * ix));
            return md;
        }
    }
}
