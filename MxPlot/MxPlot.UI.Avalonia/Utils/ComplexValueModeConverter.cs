using MxPlot.Core;
using System;
using System.Numerics;

namespace MxPlot.UI.Avalonia.Utils
{
    /// <summary>
    /// Central source of the Complex → double projection used across MxPlot.UI.Avalonia
    /// (bitmap rendering, histograms, line profiles, etc.).
    /// </summary>
    /// <remarks>
    /// This lives in the UI layer rather than MxPlot.Core because how Complex data is reduced
    /// to a displayable scalar is a MatrixPlotter/rendering policy decision, not a data-engine
    /// concern — <see cref="ComplexValueMode"/> itself is defined in MxPlot.Core, but the
    /// projection function it maps to belongs here.
    /// </remarks>
    internal static class ComplexValueModeConverter
    {
        /// <summary>
        /// Returns the projection function from <see cref="Complex"/> to <c>double</c>
        /// corresponding to <paramref name="mode"/>.
        /// </summary>
        internal static Func<Complex, double> GetConverter(ComplexValueMode mode) => mode switch
        {
            ComplexValueMode.Magnitude => c => c.Magnitude,
            ComplexValueMode.Real => c => c.Real,
            ComplexValueMode.Imaginary => c => c.Imaginary,
            ComplexValueMode.Phase => c => c.Phase,
            ComplexValueMode.Power => c => c.Real * c.Real + c.Imaginary * c.Imaginary,
            _ => c => c.Magnitude,
        };
    }
}
