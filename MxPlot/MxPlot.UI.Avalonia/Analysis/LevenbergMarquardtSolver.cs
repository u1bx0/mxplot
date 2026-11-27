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
            /// <summary>For each parameter, whether it was held at a given value instead of being fitted.</summary>
            public IReadOnlyList<bool> IsFixed { get; init; } = [];
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
                sb.AppendLine($"── {Fitter.Name} fit ──────");
                sb.AppendLine("Function:");
                sb.AppendLine($"  {Fitter.FormulaDescription}");
                sb.AppendLine("Result:");

                var names = Fitter.ParameterNames;
                int maxLen = names.Max(n => n.Length);
                for (int i = 0; i < Parameters.Length; i++)
                {
                    string note = i < IsFixed.Count && IsFixed[i] ? " (fixed)" : "";
                    sb.AppendLine($"  {names[i].PadRight(maxLen)} = {F(Parameters[i])}{u}{note}");
                }

                foreach (var line in Fitter.FormatDerivedResults(Parameters, unit))
                    sb.AppendLine($"  {line}");

                sb.AppendLine($"  R²{new string(' ', Math.Max(0, maxLen - 2))} = {R2:F4}");
                sb.Append($"  χ²ᵣ{new string(' ', Math.Max(0, maxLen - 3))} = {ChiSquared:G4}");
                if (!Converged) sb.Append(" (not converged)");
                return sb.ToString();
            }
        }

        // ── Public entry point ────────────────────────────────────────────────

        /// <summary>
        /// The fewest points a fit needs: the fitter's own minimum, less one for each parameter held at a
        /// fixed value (a fixed parameter takes no data to determine). At least 1.
        /// </summary>
        public static int MinimumPoints(IProfileFitter fitter, IReadOnlyList<double?>? fixedValues = null)
        {
            int fixedCount = fixedValues?.Count(v => v != null) ?? 0;
            return Math.Max(1, fitter.MinimumPoints - fixedCount);
        }

        /// <summary>
        /// Fits the model defined by <paramref name="fitter"/> to the supplied XY data.
        /// </summary>
        /// <param name="fixedValues">
        /// One entry per parameter: a value holds that parameter there, <c>null</c> leaves it free.
        /// <c>null</c> (the default) fits every parameter.
        /// </param>
        public static FitResult Fit(IProfileFitter fitter, IReadOnlyList<(double X, double Y)> points,
            IReadOnlyList<double?>? fixedValues = null)
        {
            if (fixedValues != null && fixedValues.Count != fitter.ParameterCount)
                return Fail(fitter, "The fixed values do not match the fitter's parameters.");

            int minPoints = MinimumPoints(fitter, fixedValues);
            if (points == null || points.Count < minPoints)
                return Fail(fitter, $"Too few data points (minimum {minPoints} required).");

            var pts = points.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
            if (pts.Count < minPoints)
                return Fail(fitter, "Too few finite data points.");

            double[] p0 = fitter.EstimateInitialParams(pts);
            if (fixedValues != null)
                for (int i = 0; i < p0.Length; i++)
                    if (fixedValues[i] is { } v) p0[i] = v;
            return RunLM(fitter, pts, p0, fixedValues);
        }

        // ── Levenberg-Marquardt ───────────────────────────────────────────────

        private static FitResult RunLM(IProfileFitter fitter, List<(double X, double Y)> pts, double[] p,
            IReadOnlyList<double?>? fixedValues)
        {
            int nPar = fitter.ParameterCount;
            int n = pts.Count;
            double xRange = Math.Abs(pts[n - 1].X - pts[0].X);
            double lambda = 1e-2;
            bool converged = false;

            // Only the free parameters take part in the steps; a fixed one keeps its value throughout.
            int[] free = Enumerable.Range(0, nPar).Where(i => fixedValues?[i] == null).ToArray();
            int nFree = free.Length;
            if (nFree == 0) converged = true;

            for (int iter = 0; nFree > 0 && iter < 500; iter++)
            {
                var (JtJ, Jtr, chiSq) = Accumulate(fitter, pts, p);

                // Damped normal equations over the free parameters: (J'J + λ·diag(J'J)) Δp = J'r
                var A = new double[nFree, nFree];
                var b = new double[nFree];
                for (int a = 0; a < nFree; a++)
                {
                    b[a] = Jtr[free[a]];
                    for (int c = 0; c < nFree; c++) A[a, c] = JtJ[free[a], free[c]];
                    A[a, a] += lambda * Math.Max(JtJ[free[a], free[a]], 1e-14);
                }

                var sol = SolveN(A, b, nFree);
                if (sol == null) break;
                var dp = new double[nPar];   // zero for the fixed parameters
                for (int a = 0; a < nFree; a++) dp[free[a]] = sol[a];

                // Trial step
                var pNew = new double[nPar];
                for (int i = 0; i < nPar; i++) pNew[i] = p[i] + dp[i];
                fitter.ClampParameters(pNew, xRange);
                // A constraint may have moved a fixed parameter (σ is kept positive, for one): put it back.
                if (fixedValues != null)
                    for (int i = 0; i < nPar; i++)
                        if (fixedValues[i] is { } fv) pNew[i] = fv;

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
                ChiSquared = finalChi / Math.Max(n - nFree, 1),
                R2 = r2,
                Converged = converged,
                IsFixed = Enumerable.Range(0, nPar).Select(i => fixedValues?[i] != null).ToArray(),
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
