using System;
using System.Collections.Generic;

namespace MxPlot.Core
{
    /// <summary>
    /// A single scalar view a structured element type exposes -- e.g. "Magnitude" or "Phase" for
    /// <see cref="System.Numerics.Complex"/>. A built-in primitive element type exposes exactly one,
    /// named "Value"; a custom struct exposes the modes it was registered with.
    /// </summary>
    public sealed record ValueMode(string Name);

    /// <summary>
    /// Describes the element type T of a <see cref="MatrixData{T}"/>: its CLR type, display and
    /// persistent names, and the scalar value modes it exposes (<see cref="ValueModes"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ValueModes"/> always has at least one entry. Its index is the <c>valueMode</c>
    /// parameter accepted by <see cref="MatrixData{T}.GetValueRange(int, int)"/> and related
    /// members: a built-in primitive element type exposes a single mode;
    /// <see cref="System.Numerics.Complex"/> exposes five, in the order defined by
    /// <see cref="ComplexValueMode"/> (Magnitude, Real, Imaginary, Phase, Power); a custom struct
    /// exposes the modes given to <see cref="MatrixData{T}.Register"/>.
    /// </para>
    /// <para>
    /// Equality is defined by <see cref="Type"/> alone -- <c>a == b</c>, <c>a.Equals(b)</c> and
    /// <c>a.Type == b.Type</c> all mean the same thing, and two instances describing the same CLR
    /// type compare equal even if they are not the same object. <see cref="MatrixData{T}"/> keeps
    /// one instance per T, but that sharing is an implementation detail, not part of the equality
    /// contract.
    /// </para>
    /// </remarks>
    public class ElementType
    {
        /// <summary>
        /// The CLR type this instance describes -- the T of a <c>MatrixData&lt;T&gt;</c>.
        /// </summary>
        /// <remarks>
        /// When the calling code actually wants to know which concrete <c>MatrixData&lt;T&gt;</c> it
        /// has -- to cast, dispatch, or call a T-specific member -- prefer a type pattern on the
        /// <see cref="IMatrixData"/> reference itself, e.g. <c>data is MatrixData&lt;Complex&gt; typed</c>,
        /// over comparing this property to <c>typeof(T)</c>: the pattern also produces the typed
        /// reference, with no separate cast. Reach for <see cref="Type"/> only when the CLR
        /// <see cref="System.Type"/> value itself is what is needed -- reflection, a comparison
        /// against a <see cref="System.Type"/> chosen at runtime (not known at compile time), or
        /// passing it to a non-generic API.
        /// </remarks>
        public Type Type { get; }

        /// <summary>
        /// The display name (e.g. <c>"ushort"</c>, <c>"Complex"</c>), for UI labels and messages.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// The name written to, and matched against, a persisted <c>.mxd</c> file's header: the
        /// <see cref="ElementTypeRegistration{T}.TypeId"/> of a registered custom struct, or
        /// <see cref="System.Type.FullName"/> for a built-in type. Unlike <see cref="Name"/>, this is a
        /// file-format contract: changing it for a given T breaks compatibility with files written earlier.
        /// </summary>
        public string PersistentName { get; }

        /// <summary>The scalar value modes this element type exposes. Never empty.</summary>
        public IReadOnlyList<ValueMode> ValueModes { get; }

        /// <summary>Shorthand for <c>ValueModes.Count</c>.</summary>
        public int ValueModeCount => ValueModes.Count;

        internal ElementType(Type type, string name, IReadOnlyList<ValueMode> valueModes, string? persistentName = null)
        {
            if (valueModes is null || valueModes.Count < 1)
                throw new ArgumentException("At least one value mode is required.", nameof(valueModes));

            Type = type ?? throw new ArgumentNullException(nameof(type));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            PersistentName = persistentName ?? type.FullName ?? type.Name;
            ValueModes = valueModes;
        }

        /// <summary>
        /// Clamps <paramref name="mode"/> to a valid index into <see cref="ValueModes"/>, returning
        /// 0 (the primary mode) when it is out of range. For a single-mode element type this
        /// collapses any nonzero mode to 0, so a stored mode from a differently-typed instance
        /// (e.g. a UI's selected <see cref="ComplexValueMode"/>) can be applied unconditionally.
        /// </summary>
        public int ClampMode(int mode) => (uint)mode < (uint)ValueModes.Count ? mode : 0;

        /// <summary>Returns <see cref="Name"/>.</summary>
        public override string ToString() => Name;

        /// <summary>Two instances are equal when they describe the same <see cref="Type"/>.</summary>
        public override bool Equals(object? obj) => obj is ElementType other && Type == other.Type;

        /// <inheritdoc/>
        public override int GetHashCode() => Type.GetHashCode();

        /// <summary>
        /// True when both describe the same <see cref="Type"/> (or both are <see langword="null"/>);
        /// same as <c>left.Type == right.Type</c>. Compares by value, not by reference.
        /// </summary>
        public static bool operator ==(ElementType? left, ElementType? right)
            => left is null ? right is null : left.Equals(right);

        /// <summary>
        /// True when the two describe different <see cref="Type"/>s; the negation of
        /// <see cref="operator ==(ElementType, ElementType)"/>.
        /// </summary>
        public static bool operator !=(ElementType? left, ElementType? right) => !(left == right);
    }
}
