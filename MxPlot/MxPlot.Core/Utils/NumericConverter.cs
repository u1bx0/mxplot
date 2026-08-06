using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace MxPlot.Core.Utils
{
    /// <summary>
    /// High-performance conversion utilities between T and double.
    /// JIT eliminates all branches and boxing at call sites when T is a concrete type.
    /// </summary>
    internal static class NumericConverter
    {
        //
        // Used for conversion from (u)long to double to avoid overflow issues.
        //
        private const double LongMaxAsDouble = 9.2233720368547758E+18;  // long.MaxValue  rounded down to nearest representable double
        private const double LongMinAsDouble = -9.2233720368547758E+18; // long.MinValue  rounded up   to nearest representable double
        private const double ULongMaxAsDouble = 1.8446744073709550E+19;  // ulong.MaxValue rounded down to nearest representable double

        /// <summary>
        /// Converts a value of type T to double.
        /// </summary>
        /// <typeparam name="T">The type of the value to convert.</typeparam>
        /// <param name="value">The value to convert.</param>
        /// <returns>The converted double value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static double ToDouble<T>(T value) where T : unmanaged
        {
            if (typeof(T) == typeof(double)) return (double)(object)value!;
            if (typeof(T) == typeof(float)) return (float)(object)value!;
            if (typeof(T) == typeof(int)) return (int)(object)value!;
            if (typeof(T) == typeof(uint)) return (uint)(object)value!;
            if (typeof(T) == typeof(long)) return (long)(object)value!;
            if (typeof(T) == typeof(ulong)) return (ulong)(object)value!;
            if (typeof(T) == typeof(short)) return (short)(object)value!;
            if (typeof(T) == typeof(ushort)) return (ushort)(object)value!;
            if (typeof(T) == typeof(byte)) return (byte)(object)value!;
            if (typeof(T) == typeof(sbyte)) return (sbyte)(object)value!;
            if (typeof(T) == typeof(decimal)) return (double)(decimal)(object)value!;
            if (typeof(T) == typeof(Complex)) return ((Complex)(object)value!).Magnitude;

            return Convert.ToDouble(value);
        }

        /// <summary>
        /// Converts a double value to type T.
        /// </summary>
        /// <typeparam name="T">The type to convert to.</typeparam>
        /// <param name="value">The double value to convert.</param>
        /// <returns>The converted value of type T.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T FromDouble<T>(double value) where T : unmanaged
        {
            if (typeof(T) == typeof(double)) return (T)(object)value;
            if (typeof(T) == typeof(float)) return (T)(object)(float)value;

            if (typeof(T) == typeof(byte)) return (T)(object)(byte)Math.Clamp(Math.Round(value), byte.MinValue, byte.MaxValue);
            if (typeof(T) == typeof(sbyte)) return (T)(object)(sbyte)Math.Clamp(Math.Round(value), sbyte.MinValue, sbyte.MaxValue);
            if (typeof(T) == typeof(ushort)) return (T)(object)(ushort)Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue);
            if (typeof(T) == typeof(short)) return (T)(object)(short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);
            if (typeof(T) == typeof(uint)) return (T)(object)(uint)Math.Clamp(Math.Round(value), uint.MinValue, uint.MaxValue);
            if (typeof(T) == typeof(int)) return (T)(object)(int)Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue);
            if (typeof(T) == typeof(ulong)) return (T)(object)(ulong)Math.Clamp(Math.Round(value), 0.0, ULongMaxAsDouble);
            if (typeof(T) == typeof(long)) return (T)(object)(long)Math.Clamp(Math.Round(value), LongMinAsDouble, LongMaxAsDouble);

            if (typeof(T) == typeof(decimal)) return (T)(object)(decimal)value;
            if (typeof(T) == typeof(Complex)) return (T)(object)new Complex(value, 0);

            return (T)(object)Convert.ChangeType(value, typeof(T));
        }
    }
}
