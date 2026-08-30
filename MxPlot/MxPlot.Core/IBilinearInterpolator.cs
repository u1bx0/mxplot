using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// Defines a bilinear interpolator for values of type <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Implementations are responsible for combining the four corner values of a cell into
    /// a single interpolated value.
    /// <para>
    /// In <see cref="MatrixData{T}"/>, this abstraction is primarily used for non-primitive
    /// value types that cannot be converted to and from <c>double</c> safely.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">
    /// The value type to interpolate. This is typically a non-primitive struct type.
    /// </typeparam>
    public interface IBilinearInterpolator<T> where T: unmanaged
    {
        /// <summary>
        /// Computes the bilinear interpolation result from the four corner values of a cell.
        /// </summary>
        /// <param name="v00">The value at the lower-left corner.</param>
        /// <param name="v10">The value at the lower-right corner.</param>
        /// <param name="v01">The value at the upper-left corner.</param>
        /// <param name="v11">The value at the upper-right corner.</param>
        /// <param name="dx">
        /// The horizontal interpolation factor in the range <c>[0, 1]</c>.
        /// </param>
        /// <param name="dy">
        /// The vertical interpolation factor in the range <c>[0, 1]</c>.
        /// </param>
        /// <returns>The interpolated value.</returns>
        T Interpolate(T v00, T v10, T v01, T v11, double dx, double dy);
    }
}
