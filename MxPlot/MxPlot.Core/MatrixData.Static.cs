using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace MxPlot.Core
{
    public static class MatrixData
    {
        /// <summary>
        /// Contains the set of primitive numeric types supported for MatrixData&lt;T&gt;. 
        /// This collection is used to determine if certain operations, such as GetValueAsDouble, are supported for a given type parameter T.
        /// </summary>
        /// <remarks>This static, read-only collection can be used to validate or restrict types during
        /// processing. The set includes common integral and floating-point types recognized by the system.</remarks>
        public static readonly HashSet<Type> SupportedPrimitiveTypes = new()
        {
            typeof(byte), typeof(sbyte),
            typeof(short), typeof(ushort),
            typeof(int), typeof(uint),
            typeof(long), typeof(ulong),
            typeof(float), typeof(double)
         };

        /// <summary>
        /// Returns the display name for a numeric type (e.g. <c>typeof(ushort)</c> → <c>"ushort"</c>).
        /// This is the same friendly name returned by <see cref="IMatrixData.ValueTypeName"/> at runtime.
        /// Falls back to <see cref="Type.Name"/> for custom structs not in the built-in set.
        /// </summary>
        public static string GetValueTypeName(Type t)
        {
            if (t == typeof(byte))    return "byte";
            if (t == typeof(sbyte))   return "sbyte";
            if (t == typeof(short))   return "short";
            if (t == typeof(ushort))  return "ushort";
            if (t == typeof(int))     return "int";
            if (t == typeof(uint))    return "uint";
            if (t == typeof(long))    return "long";
            if (t == typeof(ulong))   return "ulong";
            if (t == typeof(float))   return "float";
            if (t == typeof(double))  return "double";
            if (t == typeof(Complex)) return "Complex";
            return t.Name;
        }

        /// <summary>
        /// Internally calls Clone() method
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="target"></param>
        /// <returns></returns>
        public static T Duplicate<T>(this T target)
            where T : IMatrixData
        {
            return (T)target.Clone();
        }

        /// <summary>
        /// Loads matrix data from a file using the specified reader and returns it as an IMatrixData.
        /// </summary>
        /// <remarks>
        /// Use this method when the underlying data type is unknown at compile time. 
        /// The reader will determine the appropriate numeric type (e.g., double, int).
        /// </remarks>
        /// <param name="filePath">The source file path.</param>
        /// <param name="reader">The format reader implementation.</param>
        public static IMatrixData Load(string filePath, IMatrixDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return reader.Read(filePath);
        }

        /// <summary>
        /// Extension method for MatrixData<Complex> to get the value range (min and max) based on the specified ComplexValueMode.
        /// </summary>
        /// <param name="src"></param>
        /// <param name="frameIndex"></param>
        /// <param name="mode"></param>
        /// <returns></returns>
        public static (double Min, double Max) GetValueRange(this MatrixData<Complex> src, int frameIndex, ComplexValueMode mode)
        {
            return src.GetValueRange(frameIndex, (int)mode);
        }

        /// <summary>
        /// Copies units, metadata, and (optionally) the XY scale and dimension structure
        /// from <paramref name="source"/> to <paramref name="destination"/>.
        /// </summary>
        /// <remarks>
        /// This is a convenience helper for the common post-processing pattern where
        /// a newly created result matrix must inherit the properties of its source.
        /// Operations that change the scale (e.g. Crop) or swap axes (e.g. Transpose)
        /// should pass <c>copyScale: false</c> and set the scale manually.
        /// </remarks>
        /// <param name="destination">The target matrix whose properties are set.</param>
        /// <param name="source">The source matrix to copy from.</param>
        /// <param name="copyScale">
        /// When <c>true</c> (default), copies the XY scale via <see cref="IMatrixData.SetXYScale"/>.
        /// </param>
        /// <param name="copyDimensions">
        /// When <c>true</c> (default), copies the dimension structure if present.
        /// </param>
        public static void CopyPropertiesFrom(this IMatrixData destination, IMatrixData source,
            bool copyScale = true, bool copyDimensions = true)
        {
            if (copyScale)
            {
                var scale = source.GetScale();
                destination.SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            }

            destination.XUnit = source.XUnit;
            destination.YUnit = source.YUnit;

            var formatHeaders = GetFormatHeaderKeys(source);
            foreach (var kvp in source.Metadata)
            {
                // Skip format-header entries — they describe the source file format
                // and are invalid after derivation (Crop, Filter, Slice, etc.).
                if (formatHeaders.Contains(kvp.Key))
                    continue;
                // Skip the tracking key itself so the destination starts clean.
                if (kvp.Key.Equals(FormatHeaderMetaKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                destination.Metadata[kvp.Key] = kvp.Value;
            }

            if (copyDimensions && source.Dimensions?.Axes?.Any() == true)
            {
                var axes = Axis.CreateFrom(source.Dimensions.Axes.ToArray());
                destination.DefineDimensions(axes);
            }
        }

        // ── Frame-as-double accessor ───────────────────────────────────────────

        /// <summary>
        /// Returns the entire frame as a <c>double[]</c>, converting element-by-element when
        /// the underlying element type is not <c>double</c>.  For <c>MatrixData&lt;double&gt;</c>
        /// the internal array is returned directly (zero allocation).
        /// <para>
        /// Layout: <c>span[dataY * XCount + dataX]</c> — raw data-index order (Y-up, data row 0
        /// at index 0).  FlipY is <b>not</b> applied; callers that work in world/overlay space
        /// must convert with <c>dataY = YCount - 1 - worldY</c>.
        /// </para>
        /// </summary>
        /// <param name="md">The matrix data instance.</param>
        /// <param name="frameIndex">Frame to read. Defaults to the active frame when negative.</param>
        /// <returns>
        /// A <see cref="ReadOnlySpan{T}"/> over <c>double</c> values for the requested frame.
        /// The span is valid only until the next mutating call on <paramref name="md"/>.
        /// </returns>
        public static ReadOnlySpan<double> GetFrameAsDoubleSpan(this IMatrixData md, int frameIndex = -1)
        {
            if (md is MatrixData<double> dd)
                return dd.AsSpan(frameIndex);

            if (md is MatrixData<float> df)
            {
                var src = df.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<int> di)
            {
                var src = di.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<short> ds)
            {
                var src = ds.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<ushort> dus)
            {
                var src = dus.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<byte> db)
            {
                var src = db.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<sbyte> dsb)
            {
                var src = dsb.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<uint> dui)
            {
                var src = dui.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<long> dl)
            {
                var src = dl.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }
            if (md is MatrixData<ulong> dul)
            {
                var src = dul.AsSpan(frameIndex);
                var dst = new double[src.Length];
                for (int i = 0; i < src.Length; i++) dst[i] = src[i];
                return dst;
            }

            // Fallback: per-pixel GetValueAt (unsupported primitive or virtual-only type)
            if (frameIndex < 0) frameIndex = md.ActiveIndex;
            int total = md.XCount * md.YCount;
            var fallback = new double[total];
            for (int iy = 0; iy < md.YCount; iy++)
                for (int ix = 0; ix < md.XCount; ix++)
                    fallback[iy * md.XCount + ix] = md.GetValueAt(ix, iy, frameIndex);
            return fallback;
        }

        #region Format Header Metadata

        // Format-header keys are metadata entries that store the raw header blob
        // of the originating file format (e.g. FITS header text, OME-XML).
        //
        // They are:
        //  - Automatically treated as read-only by UI consumers (the fact that a
        //    key is listed here implies it must not be edited by the user).
        //  - Excluded from CopyPropertiesFrom — they describe the source file and
        //    become invalid after derivation (Crop, Filter, Slice, etc.).
        //  - Still fully copied by Clone() (exact duplicate of the same data).
        //
        // IO handlers call MarkAsFormatHeader() at read time.  The tracking key
        // lives in the "mxplot.*" reserved namespace so it is hidden from the
        // Metadata UI and round-trips transparently through any format writer.

        internal const string FormatHeaderMetaKey = "mxplot.metadata.format_header";

        /// <summary>
        /// Records one or more metadata keys as format-header entries.
        /// <para>
        /// Format-header entries store the raw header blob of the originating file
        /// format (e.g. FITS header text, OME-XML).  Marking a key as a format
        /// header has two effects:
        /// </para>
        /// <list type="bullet">
        ///   <item>UI consumers treat the key as read-only (derived from
        ///         <see cref="IsFormatHeader"/>).</item>
        ///   <item><see cref="CopyPropertiesFrom"/> skips the key, because the
        ///         header describes the source file and is invalid after
        ///         derivation.</item>
        /// </list>
        /// <para>
        /// Intended to be called by IO handlers at read time.
        /// </para>
        /// </summary>
        /// <param name="data">The matrix data whose metadata keys are being marked.</param>
        /// <param name="keys">The metadata key(s) to mark as format headers.</param>
        public static void MarkAsFormatHeader(this IMatrixData data, params string[] keys)
        {
            if (keys == null || keys.Length == 0) return;
            var existing = GetFormatHeaderKeys(data);
            var merged = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
            {
                if (!string.IsNullOrEmpty(k))
                    merged.Add(k);
            }
            data.Metadata[FormatHeaderMetaKey] = string.Join(",", merged);
        }

        /// <summary>
        /// Returns whether the specified metadata key has been marked as a format header.
        /// Format-header keys are treated as read-only and excluded from
        /// <see cref="CopyPropertiesFrom"/>.
        /// </summary>
        public static bool IsFormatHeader(this IMatrixData data, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (!data.Metadata.TryGetValue(FormatHeaderMetaKey, out var csv)
                || string.IsNullOrEmpty(csv))
                return false;
            foreach (var part in csv.Split(','))
            {
                if (part.Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the set of metadata keys that have been marked as format headers.
        /// </summary>
        public static IReadOnlySet<string> GetFormatHeaderKeys(this IMatrixData data)
        {
            if (!data.Metadata.TryGetValue(FormatHeaderMetaKey, out var csv)
                || string.IsNullOrEmpty(csv))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in csv.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                    set.Add(trimmed);
            }
            return set;
        }

        #endregion
    }
}
