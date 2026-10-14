using System;
using System.Collections.Generic;

namespace MxPlot.Core
{
    /// <summary>
    /// Writes the value of each element of <paramref name="source"/> in value mode <paramref name="mode"/> into
    /// <paramref name="destination"/>, which is at least as long as <paramref name="source"/>. The bulk form of
    /// <see cref="ElementTypeRegistration{T}.ToScalar"/>.
    /// </summary>
    public delegate void ScalarsReader<T>(ReadOnlySpan<T> source, Span<double> destination, int mode)
        where T : unmanaged;

    /// <summary>
    /// Everything <see cref="MatrixData{T}.Register"/> needs to make a custom struct usable as the element
    /// type of a <see cref="MatrixData{T}"/>: its value modes, how to read each of them as a scalar, and how
    /// to find a frame's range for each of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ToScalar"/> and <see cref="MinMaxFinder"/> share the value-mode contract defined by
    /// <see cref="ValueModes"/>: mode <c>k</c> of <see cref="ToScalar"/> and entry <c>k</c> of the arrays the
    /// finder returns describe the same view of the element. <see cref="MatrixData{T}.Register"/> checks that the
    /// finder returns one entry per mode.
    /// </para>
    /// <para>
    /// <b>The functions must not keep state.</b> They are shared by the whole process for the lifetime of the
    /// type's registration and are called concurrently, from parallel loops and from rendering and processing
    /// threads. Each call must depend only on its arguments. Do not:
    /// </para>
    /// <list type="bullet">
    /// <item>store a "current mode" or any other setting that one call sets for the next;</item>
    /// <item>reuse a buffer or cache kept outside the call;</item>
    /// <item>capture a variable that can change after registration.</item>
    /// </list>
    /// <para>
    /// A <c>static</c> lambda (<c>static (v, mode) => …</c>) or a static method cannot capture anything, so it
    /// rules out the last point at compile time.
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The custom element type.</typeparam>
    public sealed class ElementTypeRegistration<T> where T : unmanaged
    {
        /// <summary>
        /// A stable identifier for the type, written to and matched against a persisted <c>.mxd</c> file's
        /// header (<see cref="ElementType.PersistentName"/>). Keep it unchanged when the type is moved or
        /// renamed, or files written earlier can no longer be matched to it.
        /// </summary>
        public required string TypeId { get; init; }

        /// <summary>The value modes the type exposes, in index order. Must not be empty.</summary>
        public required IReadOnlyList<ValueMode> ValueModes { get; init; }

        /// <summary>
        /// Reads an element in a value mode: returns the scalar value of the element for the mode, an index into
        /// <see cref="ValueModes"/>. Only valid modes are passed. Must not keep state (see the remarks on this type).
        /// </summary>
        public required Func<T, int, double> ToScalar { get; init; }

        /// <summary>
        /// Optional. A faster bulk form of <see cref="ToScalar"/>, called once per row or frame by scan-order
        /// consumers. It must produce the same values as <see cref="ToScalar"/>. Without one, the library loops
        /// over <see cref="ToScalar"/>. Must not keep state (see the remarks on this type).
        /// </summary>
        public ScalarsReader<T>? ToScalars { get; init; }

        /// <summary>
        /// Finds a frame's minimum and maximum for each value mode. Must return one entry per mode, and must not
        /// keep state (see the remarks on this type). Can be replaced later with
        /// <see cref="MatrixData{T}.RegisterDefaultMinMaxFinder"/> or, per instance, <see cref="MatrixData{T}.SetMinMaxFinder"/>.
        /// </summary>
        public required MatrixData<T>.MinMaxFinder MinMaxFinder { get; init; }

        /// <summary>
        /// Optional. Without one, interpolated reads fall back to nearest-neighbor. Like the functions above, it is
        /// shared and called concurrently, so it must not keep state either.
        /// </summary>
        public IBilinearInterpolator<T>? Interpolator { get; init; }
    }
}
