using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MxPlot.UI.Avalonia.Analysis
{
    /// <summary>
    /// Generic N-parameter Levenberg–Marquardt solver that works with any
    /// <see cref="IProfileFitter"/> implementation.
    /// Pure C# — no external dependencies.
    /// </summary>
    public static class LevenbergMarquardtSolver
    {
        // ── Result ────────────────────────────────────────────────────────────

        public sealed class FitResult
        {
            /// <summary>The fitter that produced this result.</summary>
            public IProfileFitter Fitter { get; init; } = null!;
            /// <summary>Fitted parameter values.</summary>
            public double[] Parameters { get; init; } = [];
            /// <summary>Reduced chi-squared χ²/(N−P).</summary>
            public double ChiSquared { get; init; }
            /// <summary>Coefficient of determination R².</summary>
            public double R2 { get; init; }
            public bool Converged { get; init; }
            /// <summary>Non-null when fitting failed before the LM loop.</summary>
            public string? Error { get; init; }

            /// <summary>Evaluates the fitted curve at <paramref name="n"/> uniformly-spaced points.</summary>
            public IReadOnlyList<(double X, double Y)> GenerateCurve(double xMin, double xMax, int n = 400)
            {
                var pts = new List<(double, double)>(n);
                for (int i = 0; i < n; i++)
                {
                    double x = xMin + (xMax - xMin) * i / (n - 1);
                    pts.Add((x, Fitter.Evaluate(x, Parameters)));
                }
                return pts;
            }

            /// <summary>Builds a full info-panel string.</summary>
            public string FormatInfo(string unit)
            {
                static string F(double v) => v.ToString("G5", CultureInfo.InvariantCulture);
                string u = string.IsNullOrEmpty(unit) ? "" : $" [{unit}]";

                var sb = new StringBuilder();
                sb.AppendLine($"── {Fitter.Name} fit ──────────────");
                sb.AppendLine("Function:");
                sb.AppendLine($"  {Fitter.FormulaDescription}");
                sb.AppendLine("Result:");

                var names = Fitter.ParameterNames;
                int maxLen = names.Max(n => n.Length);
                for (int i = 0; i < Parameters.Length; i++)
                    sb.AppendLine($"  {names[i].PadRight(maxLen)} = {F(Parameters[i])}{u}");

                foreach (var line in Fitter.FormatDerivedResults(Parameters, unit))
                    sb.AppendLine($"  {line}");

                sb.AppendLine($"  R²{new string(' ', Math.Max(0, maxLen - 2))} = {R2:F4}");
                sb.Append($"  χ²ᵣ{new string(' ', Math.Max(0, maxLen - 3))} = {ChiSquared:G4}");
                if (!Converged) sb.Append(" (not converged)");
                return sb.ToString();
            }
        }

        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>Fits the model defined by <paramref name="fitter"/> to the supplied XY data.</summary>
        public static FitResult Fit(IProfileFitter fitter, IReadOnlyList<(double X, double Y)> points)
        {
            if (points == null || points.Count < fitter.MinimumPoints)
                return Fail(fitter, $"Too few data points (minimum {fitter.MinimumPoints} required).");

            var pts = points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
            if (pts.Count < fitter.MinimumPoints)
                return Fail(fitter, "Too few finite data points.");

            double[] p0 = fitter.EstimateInitialParams(pts);
            return RunLM(fitter, pts, p0);
        }

        // ── Levenberg-Marquardt ───────────────────────────────────────────────

        private static FitResult RunLM(IProfileFitter fitter, List<(double X, double Y)> pts, double[] p)
        {
            int nPar = fitter.ParameterCount;
            int n = pts.Count;
            double xRange = Math.Abs(pts[n - 1].X - pts[0].X);
            double lambda = 1e-2;
            bool converged = false;

            for (int iter = 0; iter < 500; iter++)
            {
                var (JtJ, Jtr, chiSq) = Accumulate(fitter, pts, p);

                // Damped normal equations: (J'J + λ·diag(J'J)) Δp = J'r
                var JtJd = (double[,])JtJ.Clone();
                for (int i = 0; i < nPar; i++)
                    JtJd[i, i] += lambda * Math.Max(JtJ[i, i], 1e-14);

                var dp = SolveN(JtJd, Jtr, nPar);
                if (dp == null) break;

                // Trial step
                var pNew = new double[nPar];
                for (int i = 0; i < nPar; i++) pNew[i] = p[i] + dp[i];
                fitter.ClampParameters(pNew, xRange);

                double newChiSq = ChiSq(fitter, pts, pNew);

                if (newChiSq < chiSq)
                {
                    double relChi = (chiSq - newChiSq) / Math.Max(chiSq, 1e-15);
                    double stepMax = 0, pMax = 0;
                    for (int i = 0; i < nPar; i++)
                    {
                        stepMax = Math.Max(stepMax, Math.Abs(dp[i]));
                        pMax = Math.Max(pMax, Math.Abs(p[i]));
                    }

                    p = pNew;
                    lambda = Math.Max(lambda * 0.1, 1e-15);

                    if (relChi < 1e-10 || stepMax / Math.Max(pMax, 1e-10) < 1e-9)
                    { converged = true; break; }
                }
                else
                {
                    lambda = Math.Min(lambda * 10.0, 1e12);
                    if (lambda > 1e11) { converged = true; break; }   // stalled → accept current
                }
            }

            double finalChi = ChiSq(fitter, pts, p);
            double yMean = pts.Average(pt => pt.Y);
            double ssTot = pts.Sum(pt => (pt.Y - yMean) * (pt.Y - yMean));
            double r2 = ssTot > 1e-30 ? Math.Max(0.0, 1.0 - finalChi / ssTot) : 0.0;

            return new FitResult
            {
                Fitter = fitter,
                Parameters = p,
                ChiSquared = finalChi / Math.Max(n - nPar, 1),
                R2 = r2,
                Converged = converged,
            };
        }

        // ── Math helpers ──────────────────────────────────────────────────────

        private static (double[,] JtJ, double[] Jtr, double chiSq)
            Accumulate(IProfileFitter fitter, List<(double X, double Y)> pts, double[] p)
        {
            int nPar = fitter.ParameterCount;
            double[,] JtJ = new double[nPar, nPar];
            double[] Jtr = new double[nPar];
            double chiSq = 0;
            double[] g = new double[nPar];

            foreach (var (x, y) in pts)
            {
                double r = y - fitter.Evaluate(x, p);
                fitter.Jacobian(x, p, g);

                chiSq += r * r;
                for (int i = 0; i < nPar; i++)
                {
                    Jtr[i] += g[i] * r;
                    for (int j = 0; j < nPar; j++)
                        JtJ[i, j] += g[i] * g[j];
                }
            }
            return (JtJ, Jtr, chiSq);
        }

        private static double ChiSq(IProfileFitter fitter, List<(double X, double Y)> pts, double[] p)
        {
            double s = 0;
            foreach (var (x, y) in pts)
            {
                double r = y - fitter.Evaluate(x, p);
                s += r * r;
            }
            return s;
        }

        /// <summary>Solves N×N linear system A·x = b via Gauss-Jordan with partial pivoting.</summary>
        private static double[]? SolveN(double[,] A, double[] b, int N)
        {
            double[,] m = new double[N, N + 1];
            for (int i = 0; i < N; i++)
            {
                for (int j = 0; j < N; j++) m[i, j] = A[i, j];
                m[i, N] = b[i];
            }

            for (int col = 0; col < N; col++)
            {
                int pivot = col;
                double best = Math.Abs(m[col, col]);
                for (int row = col + 1; row < N; row++)
                    if (Math.Abs(m[row, col]) > best) { best = Math.Abs(m[row, col]); pivot = row; }

                if (best < 1e-14) return null;   // singular

                if (pivot != col)
                    for (int j = 0; j <= N; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);

                double inv = 1.0 / m[col, col];
                for (int row = 0; row < N; row++)
                {
                    if (row == col) continue;
                    double f = m[row, col] * inv;
                    for (int j = col; j <= N; j++) m[row, j] -= f * m[col, j];
                }
            }

            double[] x = new double[N];
            for (int i = 0; i < N; i++) x[i] = m[i, N] / m[i, i];
            return x;
        }

        private static FitResult Fail(IProfileFitter fitter, string error)
            => new() { Fitter = fitter, Converged = false, Error = error };
    }
}
