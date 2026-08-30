using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace MxPlot.Core.Utils
{
    /// <summary>
    /// Provides the built-in bilinear interpolator for <see cref="Complex"/>.
    /// </summary>
    /// <remarks>
    /// This is the default built-in interpolator used by <c>MatrixData&lt;Complex&gt;</c>.
    /// It interpolates the real and imaginary parts independently using bilinear interpolation.
    /// </remarks>
    internal sealed class ComplexBilinearInterpolator : IBilinearInterpolator<Complex>
    {
        /// <inheritdoc/>
        /// <remarks>
        /// The implementation performs bilinear interpolation on the real and imaginary parts
        /// separately. Because bilinear interpolation is fully defined by the four corner values
        /// and the <paramref name="dx"/> / <paramref name="dy"/> factors, no additional coordinate
        /// information is required.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Complex Interpolate(Complex v00, Complex v10, Complex v01, Complex v11, double dx, double dy)
        {
            double real0 = v00.Real + dx * (v10.Real - v00.Real);
            double real1 = v01.Real + dx * (v11.Real - v01.Real);
            double imag0 = v00.Imaginary + dx * (v10.Imaginary - v00.Imaginary);
            double imag1 = v01.Imaginary + dx * (v11.Imaginary - v01.Imaginary);

            return new Complex(
                real0 + dy * (real1 - real0),
                imag0 + dy * (imag1 - imag0));
        }
    }
}
