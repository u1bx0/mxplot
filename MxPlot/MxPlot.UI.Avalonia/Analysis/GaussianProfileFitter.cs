using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MxPlot.UI.Avalonia.Analysis
{
    /// <summary>
    /// Gaussian model  y = y₀ + A·exp(−(x−x₀)²/(2σ²))  with four parameters.
    /// Provides an analytical Jacobian for fast convergence.
    /// </summary>
    public sealed class GaussianProfileFitter : IProfileFitter
    {
        /// <summary>Singleton instance.</summary>
        public static GaussianProfileFitter Instance { get; } = new();
        private GaussianProfileFitter() { }

        public string Name => "Gaussian";
        public int ParameterCount => 4;
        public IReadOnlyList<string> ParameterNames { get; } = ["y₀", "A", "x₀", "σ"];
        public string FormulaDescription => "y = y₀ + A·exp[ -(x-x₀)² / 2σ² ]";

        public double Evaluate(double x, double[] p)
        {
            double dx = x - p[2];
            double sig2 = Math.Max(p[3] * p[3], 1e-30);
            return p[0] + p[1] * Math.Exp(-0.5 * dx * dx / sig2);
        }

        /// <summary>Analytical Jacobian — four partial derivatives.</summary>
        public void Jacobian(double x, double[] p, double[] g)
        {
            double dx = x - p[2];
            double sig2 = Math.Max(p[3] * p[3], 1e-30);
            double absSig = Math.Max(Math.Abs(p[3]), 1e-15);
            double ex = Math.Exp(-0.5 * dx * dx / sig2);
            double ae = p[1] * ex;

            g[0] = 1.0;
            g[1] = ex;
            g[2] = ae * dx / sig2;
            g[3] = ae * dx * dx / (sig2 * absSig);
        }

        public double[] EstimateInitialParams(IReadOnlyList<(double X, double Y)> data)
        {
            int n = data.Count;

            // y₀: mean of the bottom 10% (robust background)
            int bgN = Math.Max(1, n / 10);
            double y0 = data.Select(p => p.Y).Order().Take(bgN).Average();

            // A and x₀ from peak
            double maxY = double.NegativeInfinity, x0 = data[n / 2].X;
            foreach (var (x, y) in data)
                if (y > maxY) { maxY = y; x0 = x; }

            double A = maxY - y0;
            if (Math.Abs(A) < 1e-15) A = 1.0;

            // σ from FWHM crossing (left and right half-max positions)
            double halfMax = y0 + A * 0.5;
            double xLeft = data[0].X, xRight = data[n - 1].X;
            bool foundL = false, foundR = false;

            for (int i = 0; i < n - 1; i++)
            {
                if (!foundL && data[i].Y <= halfMax && data[i + 1].Y > halfMax)
                {
                    double t = (halfMax - data[i].Y) / (data[i + 1].Y - data[i].Y);
                    xLeft = data[i].X + t * (data[i + 1].X - data[i].X);
                    foundL = true;
                }
                if (data[i].Y > halfMax && data[i + 1].Y <= halfMax)
                {
                    double t = (halfMax - data[i].Y) / (data[i + 1].Y - data[i].Y);
                    xRight = data[i].X + t * (data[i + 1].X - data[i].X);
                    foundR = true;
                }
            }

            double xRange = Math.Abs(data[n - 1].X - data[0].X);
            double sigma = (foundL && foundR)
                ? Math.Abs(xRight - xLeft) / (2.0 * Math.Sqrt(2.0 * Math.Log(2.0)))
                : xRange * 0.25;

            if (sigma < xRange * 1e-6) sigma = xRange * 0.1;

            return [y0, A, x0, sigma];
        }

        public void ClampParameters(double[] p, double xRange)
        {
            double sigmaMin = Math.Max(xRange * 1e-8, 1e-15);
            p[3] = Math.Max(Math.Abs(p[3]), sigmaMin);
        }

        public IEnumerable<string> FormatDerivedResults(double[] parameters, string unit)
        {
            static string F(double v) => v.ToString("G5", CultureInfo.InvariantCulture);
            string u = string.IsNullOrEmpty(unit) ? "" : $" [{unit}]";
            double sigma = Math.Abs(parameters[3]);
            double w = 2.0 * sigma;
            double fwhm = 2.0 * Math.Sqrt(2.0 * Math.Log(2.0)) * sigma;
            yield return $"w(1/e²) = {F(w)}{u}";
            yield return $"FWHM = {F(fwhm)}{u}";
        }
    }
}
