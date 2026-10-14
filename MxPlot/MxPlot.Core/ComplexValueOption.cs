using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MxPlot.Core
{
    public enum ComplexValueMode
    {
        Magnitude = 0,
        Real = 1,
        Imaginary = 2,
        Phase = 3,
        Power = 4
    }

    /// <summary>Reads a <see cref="Complex"/> value in one of its <see cref="ComplexValueMode"/>s.</summary>
    public static class ComplexValueModeExtensions
    {
        /// <summary>
        /// Returns <paramref name="value"/> read in <paramref name="mode"/>.
        /// </summary>
        /// <remarks>
        /// This is the single definition of <see cref="Complex"/>'s value modes: value reads, processing and
        /// rendering all go through it. Each mode is computed the way <c>FastMinMaxFinder.FindComplex</c>
        /// computes its range, so a value always lies within the range reported for its mode. A value outside
        /// the enum reads Magnitude.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double ToScalar(this Complex value, ComplexValueMode mode)
        {
            double re = value.Real;
            double im = value.Imaginary;
            return mode switch
            {
                ComplexValueMode.Real => re,
                ComplexValueMode.Imaginary => im,
                ComplexValueMode.Phase => Math.Atan2(im, re),
                ComplexValueMode.Power => re * re + im * im,
                _ => Math.Sqrt(re * re + im * im),
            };
        }
    }
}
