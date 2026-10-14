using MxPlot.Core.Utils;
using System;
using System.Numerics;

namespace MxPlot.Core
{
    /// <summary>
    /// Reads a scalar value out of an element of type <typeparamref name="T"/> for a given value mode --
    /// the index into <see cref="ElementType.ValueModes"/>. One instance per element type is shared by the
    /// whole process and called concurrently.
    /// </summary>
    /// <remarks>
    /// Internal: a custom struct supplies its reading as functions in <see cref="ElementTypeRegistration{T}"/>,
    /// which <see cref="MatrixData{T}.Register"/> wraps in a <see cref="DelegateValueProvider{T}"/>. The library
    /// only passes modes that are valid for the registered <see cref="ElementType"/>
    /// (see <see cref="ElementType.ClampMode"/>).
    /// </remarks>
    /// <typeparam name="T">The element type.</typeparam>
    internal abstract class ValueProvider<T> where T : unmanaged
    {
        /// <summary>
        /// Returns the scalar value of <paramref name="element"/> for <paramref name="mode"/>.
        /// </summary>
        public abstract double ToScalar(T element, int mode);

        /// <summary>
        /// Writes the scalar value of each element of <paramref name="source"/> for <paramref name="mode"/>
        /// into <paramref name="destination"/>. Scan-order consumers call this once per row or frame.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="destination"/> is shorter than <paramref name="source"/>.
        /// </exception>
        public virtual void ToScalars(ReadOnlySpan<T> source, Span<double> destination, int mode)
        {
            if (destination.Length < source.Length)
                throw new ArgumentException("Destination is shorter than source.", nameof(destination));

            for (int i = 0; i < source.Length; i++)
                destination[i] = ToScalar(source[i], mode);
        }
    }

    /// <summary>
    /// The value provider of a registered custom struct: the functions given in its
    /// <see cref="ElementTypeRegistration{T}"/>.
    /// </summary>
    internal sealed class DelegateValueProvider<T> : ValueProvider<T> where T : unmanaged
    {
        private readonly Func<T, int, double> _toScalar;
        private readonly ScalarsReader<T>? _toScalars;

        public DelegateValueProvider(Func<T, int, double> toScalar, ScalarsReader<T>? toScalars)
        {
            _toScalar = toScalar;
            _toScalars = toScalars;
        }

        public override double ToScalar(T element, int mode) => _toScalar(element, mode);

        public override void ToScalars(ReadOnlySpan<T> source, Span<double> destination, int mode)
        {
            if (destination.Length < source.Length)
                throw new ArgumentException("Destination is shorter than source.", nameof(destination));

            if (_toScalars is not null)
            {
                _toScalars(source, destination, mode);
                return;
            }

            // The base loop would call the virtual ToScalar, which then calls the delegate: two indirect calls
            // per element. Calling the delegate directly keeps it to one.
            var toScalar = _toScalar;
            for (int i = 0; i < source.Length; i++)
                destination[i] = toScalar(source[i], mode);
        }
    }

    /// <summary>
    /// The single "Value" mode of a built-in primitive numeric type.
    /// </summary>
    internal sealed class PrimitiveValueProvider<T> : ValueProvider<T> where T : unmanaged
    {
        public override double ToScalar(T element, int mode) => NumericConverter.ToDouble(element);
    }

    /// <summary>
    /// The five modes of <see cref="Complex"/>, in <see cref="ComplexValueMode"/> order.
    /// </summary>
    internal sealed class ComplexValueProvider : ValueProvider<Complex>
    {
        public override double ToScalar(Complex element, int mode) => element.ToScalar((ComplexValueMode)mode);
    }
}
