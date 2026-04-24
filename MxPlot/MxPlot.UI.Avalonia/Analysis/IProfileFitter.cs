using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Analysis
{
    /// <summary>
    /// Defines a parametric model that can be fitted to 1-D (X,Y) data.
    /// Implementations supply the model function, Jacobian, initial-guess
    /// estimator, and display helpers.
    /// </summary>
    public interface IProfileFitter
    {
        /// <summary>Display name shown in the UI combo box (e.g. "Gaussian").</summary>
        string Name { get; }

        /// <summary>Number of free parameters.</summary>
        int ParameterCount { get; }

        /// <summary>Short names for each parameter (e.g. ["y₀","A","x₀","σ"]).</summary>
        IReadOnlyList<string> ParameterNames { get; }

        /// <summary>One-line formula string shown in the info panel.</summary>
        string FormulaDescription { get; }

        /// <summary>Evaluates the model at <paramref name="x"/> given parameters <paramref name="p"/>.</summary>
        double Evaluate(double x, double[] p);

        /// <summary>
        /// Writes the partial derivatives ∂f/∂pᵢ into <paramref name="gradient"/>.
        /// The default implementation uses central finite differences;
        /// override for speed or accuracy.
        /// </summary>
        void Jacobian(double x, double[] p, double[] gradient)
        {
            const double h = 1e-7;
            for (int i = 0; i < ParameterCount; i++)
            {
                double save = p[i];
                p[i] = save + h;
                double fp = Evaluate(x, p);
                p[i] = save - h;
                double fm = Evaluate(x, p);
                p[i] = save;
                gradient[i] = (fp - fm) / (2.0 * h);
            }
        }

        /// <summary>Estimates initial parameter values from the raw data.</summary>
        double[] EstimateInitialParams(IReadOnlyList<(double X, double Y)> data);

        /// <summary>
        /// Called after each LM step to enforce parameter constraints (e.g. σ &gt; 0).
        /// Default implementation does nothing.
        /// </summary>
        void ClampParameters(double[] p, double xRange) { }

        /// <summary>Minimum number of data points required for fitting.</summary>
        int MinimumPoints => ParameterCount;

        /// <summary>
        /// Returns additional derived-result lines for the info panel
        /// (e.g. "FWHM = …"). Empty enumerable if none.
        /// </summary>
        IEnumerable<string> FormatDerivedResults(double[] parameters, string unit);
    }
}
