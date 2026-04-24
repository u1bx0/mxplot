using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MxPlot.UI.Avalonia.Analysis
{
    /// <summary>
    /// Lorentzian (Cauchy) model  y = y₀ + A·(Γ/2)² / [(x−x₀)² + (Γ/2)²]  with four parameters.
    /// Provides an analytical Jacobian for fast convergence.
    /// </summary>
    public sealed class LorentzianProfileFitter : IProfileFitter
    {
        /// <summary>Singleton instance.</summary>
        public static LorentzianProfileFitter Instance { get; } = new();
        private LorentzianProfileFitter() { }

        public string Name => "Lorentzian";
        public int ParameterCount => 4;
        public IReadOnlyList<string> ParameterNames { get; } = ["y₀", "A", "x₀", "Γ"];
        public string FormulaDescription => "y = y₀ + A·(Γ/2)² / [(x-x₀)² + (Γ/2)²]";

        public double Evaluate(double x, double[] p)
        {
            double dx = x - p[2];
            double half = p[3] * 0.5;
            double half2 = Math.Max(half * half, 1e-30);
            return p[0] + p[1] * half2 / (dx * dx + half2);
        }

        /// <summary>Analytical Jacobian — four partial derivatives.</summary>
        public void Jacobian(double x, double[] p, double[] g)
        {
            double dx = x - p[2];
            double half = p[3] * 0.5;
            double half2 = Math.Max(half * half, 1e-30);
            double denom = dx * dx + half2;
            double denom2 = denom * denom;
            double ratio = half2 / denom;

            g[0] = 1.0;
            g[1] = ratio;
            // ∂/∂x₀ : A · half² · 2(x-x₀) / denom²
            g[2] = p[1] * half2 * 2.0 * dx / denom2;
            // ∂/∂Γ : A · half · denom / denom² = A · (x-x₀)² · Γ / denom² / 2
            //         derived as: ∂(half²/denom)/∂Γ = half·denom - half²·half / denom² = half·(x-x₀)²/denom²
            g[3] = p[1] * half * dx * dx / denom2;
        }

        public double[] EstimateInitialParams(IReadOnlyList<(double X, double Y)> data)
        {
            int n = data.Count;

            // y₀: mean of the bottom 10% (robust background)
            int bgN = Math.Max(1, n / 10);
            double y0 = data.Select(pt => pt.Y).Order().Take(bgN).Average();

            // A and x₀ from peak
            double maxY = double.NegativeInfinity, x0 = data[n / 2].X;
            foreach (var (x, y) in data)
                if (y > maxY) { maxY = y; x0 = x; }

            double A = maxY - y0;
            if (Math.Abs(A) < 1e-15) A = 1.0;

            // Γ from FWHM crossing (half-max positions)
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
            double gamma = (foundL && foundR)
                ? Math.Abs(xRight - xLeft)
                : xRange * 0.25;

            if (gamma < xRange * 1e-6) gamma = xRange * 0.1;

            return [y0, A, x0, gamma];
        }

        public void ClampParameters(double[] p, double xRange)
        {
            double gammaMin = Math.Max(xRange * 1e-8, 1e-15);
            p[3] = Math.Max(Math.Abs(p[3]), gammaMin);
        }

        public IEnumerable<string> FormatDerivedResults(double[] parameters, string unit)
        {
            static string F(double v) => v.ToString("G5", CultureInfo.InvariantCulture);
            string u = string.IsNullOrEmpty(unit) ? "" : $" [{unit}]";
            double fwhm = Math.Abs(parameters[3]);
            yield return $"FWHM = {F(fwhm)}{u}";
        }
    }
}
