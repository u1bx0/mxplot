using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Converts the element values of matrix data: reading a structured element type in one of its value modes,
    /// reducing any element to double with a function, and converting between primitive element types.
    /// </summary>
    /// <remarks>
    /// <para>The methods are grouped by what the caller has in hand:</para>
    /// <list type="bullet">
    /// <item><b>A value mode</b>: <see cref="ToScalars{T}"/> for any registered element type. For Complex,
    /// <c>ToDoubleAllFrames(ComplexValueMode, …)</c> and its single-frame form <c>ToDouble(ComplexValueMode, …)</c>
    /// can also apply log₁₀ in the same pass.</item>
    /// <item><b>A function from the element to double</b>: <see cref="ToDoubleAllFrames{T}"/> and
    /// <see cref="ToDouble{T}"/>, for a reduction that is not one of the element type's value modes.</item>
    /// <item><b>Another element type</b>: <see cref="ConvertTo{TSrc,TDst}(MatrixData{TSrc},Func{TSrc,TDst})"/>, or
    /// <see cref="ConvertToType"/> when only an <see cref="IMatrixData"/> and a <see cref="Type"/> are known.</item>
    /// </list>
    /// <para>
    /// Within each group, the all-frame method comes first, then its single-frame form. All-frame methods
    /// process frames in parallel; a single frame is split into parallel rows once it reaches
    /// <see cref="ParallelThreshold"/> elements.
    /// </para>
    /// </remarks>
    public static class MatrixDataValueConverter
    {
        /// <summary>
        /// Minimum number of elements (XCount × YCount) required to trigger parallel processing.
        /// Defaults to 256 × 256 = 65,536.
        /// </summary>
        public static readonly int ParallelThreshold = 256 * 256;

        #region Value mode → double

        /// <summary>
        /// Reads every element of <paramref name="src"/> in value mode <paramref name="valueMode"/>, through the
        /// element type's registered reading (<see cref="ElementTypeRegistration{T}.ToScalar"/>, or the built-in one
        /// for a primitive type and Complex). The result has the same frames, scale and
        /// dimensions; for a primitive element type it is simply a copy as double.
        /// </summary>
        /// <typeparam name="T">The source element type.</typeparam>
        /// <param name="src">The data to read.</param>
        /// <param name="valueMode">An index into <see cref="ElementType.ValueModes"/>; clamped like <see cref="ElementType.ClampMode"/>.</param>
        /// <param name="progress">Receives <c>-FrameCount</c> first, then <c>0..FrameCount-1</c> as frames complete.</param>
        /// <param name="ct">Cancels the operation.</param>
        /// <returns>A <see cref="MatrixData{T}">MatrixData&lt;double&gt;</see> with every frame read.</returns>
        public static MatrixData<double> ToScalars<T>(
            this MatrixData<T> src,
            int valueMode = 0,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
            where T : unmanaged
        {
            ArgumentNullException.ThrowIfNull(src);

            // Non-null for any instance: an element type can only be constructed once registered.
            var provider = MatrixData<T>.RegisteredValueProvider!;
            int mode = src.ElementType.ClampMode(valueMode);
            int xnum = src.XCount;
            int ynum = src.YCount;
            int frameCount = src.FrameCount;
            var options = new ParallelOptions { CancellationToken = ct };
            progress?.Report(-frameCount);

            var frames = new double[frameCount][];
            int completed = 0;

            // The provider reads a whole row (or frame) per call, so its bulk path is used either way.
            void ReadFrame(int frameIndex, bool byRows)
            {
                ct.ThrowIfCancellationRequested();
                ReadOnlyMemory<T> srcMem = src.AsMemory(frameIndex);
                var dst = new double[xnum * ynum];
                if (byRows)
                    Parallel.For(0, ynum, options, iy =>
                        provider.ToScalars(srcMem.Span.Slice(iy * xnum, xnum), dst.AsSpan(iy * xnum, xnum), mode));
                else
                    provider.ToScalars(srcMem.Span, dst, mode);
                frames[frameIndex] = dst;
                progress?.Report(Interlocked.Increment(ref completed) - 1);
            }

            if (frameCount == 1)
                ReadFrame(0, byRows: Environment.ProcessorCount > 1 && (long)xnum * ynum >= ParallelThreshold);
            else
                Parallel.For(0, frameCount, options, i => ReadFrame(i, byRows: false));

            var result = new MatrixData<double>(xnum, ynum, frames.ToList());
            result.CopyPropertiesFrom(src, copyScale: true, copyDimensions: true);
            return result;
        }

        /// <summary>
        /// Converts all frames of a <see cref="MatrixData{T}">MatrixData&lt;Complex&gt;</see>
        /// to <c>double</c> by extracting the specified complex component.
        /// Frames are processed in parallel.
        /// </summary>
        /// <param name="src">Source complex matrix data.</param>
        /// <param name="mode">Specifies which component to extract (magnitude, real, imaginary, phase, or power).</param>
        /// <param name="applyLog10">
        /// When <c>true</c>, applies <c>log₁₀</c> to each output value, flooring it at the same small value as
        /// <see cref="ElementWiseOperator.LogTransform{T}"/> with <see cref="NegativeHandling.Clamp"/>, so the two give
        /// the same result. Meant for the non-negative modes, Magnitude and Power: a negative value is floored too.
        /// </param>
        /// <param name="dst">Optional pre-allocated destination matching source dimensions; otherwise a new instance is created.</param>
        /// <returns>A <see cref="MatrixData{T}">MatrixData&lt;double&gt;</see> with all frames converted.</returns>
        public static MatrixData<double> ToDoubleAllFrames(
            this MatrixData<Complex> src,
            ComplexValueMode mode,
            bool applyLog10 = false,
            MatrixData<double>? dst = null)
        {
            var (ret, xnum, ynum) = PrepareDstForToDouble<Complex>(src, dst, src.FrameCount);
            dst = ret;
            Parallel.For(0, src.FrameCount, frameIndex =>
            {
                ReadOnlyMemory<Complex> srcMem = src.AsMemory(frameIndex);
                double[] dstArray = dst.GetArray(frameIndex);
                ToDoubleFromComplexProc(srcMem, dstArray, mode, applyLog10, xnum, ynum, useParallel: false);
                dst.Invalidate(frameIndex);
            });
            return dst;
        }

        /// <summary>
        /// Converts a single frame of a <see cref="MatrixData{T}">MatrixData&lt;Complex&gt;</see>
        /// to <c>double</c> by extracting the specified complex component.
        /// </summary>
        /// <param name="src">Source complex matrix data.</param>
        /// <param name="mode">Specifies which component to extract (magnitude, real, imaginary, phase, or power).</param>
        /// <param name="applyLog10">
        /// When <c>true</c>, applies <c>log₁₀</c> to each output value, flooring it at the same small value as
        /// <see cref="ElementWiseOperator.LogTransform{T}"/> with <see cref="NegativeHandling.Clamp"/>, so the two give
        /// the same result. Meant for the non-negative modes, Magnitude and Power: a negative value is floored too.
        /// </param>
        /// <param name="frameIndex">The frame to convert. If negative, the active frame index is used.</param>
        /// <param name="dst">Optional pre-allocated single-frame destination; otherwise a new instance is created.</param>
        /// <returns>A single-frame <see cref="MatrixData{T}">MatrixData&lt;double&gt;</see> with the extracted values.</returns>
        public static MatrixData<double> ToDouble(
            this MatrixData<Complex> src,
            ComplexValueMode mode,
            bool applyLog10 = false,
            int frameIndex = -1,
            MatrixData<double>? dst = null)
        {
            var (ret, xnum, ynum) = PrepareDstForToDouble<Complex>(src, dst, 1);
            dst = ret;

            ReadOnlyMemory<Complex> srcMem = src.AsMemory(frameIndex);
            double[] dstArray = dst.GetArray();

            ToDoubleFromComplexProc(srcMem, dstArray, mode, applyLog10, xnum, ynum, useParallel: true);

            dst.Invalidate();
            return dst;
        }

        private static void ToDoubleFromComplexProc(ReadOnlyMemory<Complex> srcMem, double[] dstArray,
            ComplexValueMode mode, bool applyLog10, int xnum, int ynum, bool useParallel)
        {
            // Row-level processing (optimized path)
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;

                // Obtain base references for the current row
                ReadOnlySpan<Complex> srcRow = srcMem.Span.Slice(offset, xnum);
                ref double dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);
                ref double rowDstBase = ref Unsafe.Add(ref dstRefBase, offset);

                // Inner loop using base+ix addressing (JIT-friendly pattern)
                for (int ix = 0; ix < xnum; ix++)
                {
                    ref readonly Complex currentSrc = ref srcRow[ix];

                    double val = currentSrc.ToScalar(mode);

                    if (applyLog10)
                        val = Math.Log10(Math.Max(val, ElementWiseOperator.LogEpsilon));

                    Unsafe.Add(ref rowDstBase, ix) = val;
                }
            }

            // Parallel execution decision
            if (useParallel && Environment.ProcessorCount > 1 && (long)xnum * ynum >= ParallelThreshold)
            {
                Parallel.For(0, ynum, ProcessRow);
            }
            else
            {
                for (int y = 0; y < ynum; y++) ProcessRow(y);
            }
        }

        #endregion

        #region Element function → double

        /// <summary>
        /// Converts all frames of <paramref name="src"/> to <c>double</c> using the specified element converter.
        /// Frames are processed in parallel.
        /// </summary>
        /// <typeparam name="T">The source element type.</typeparam>
        /// <param name="src">Source matrix data.</param>
        /// <param name="converter">Per-element conversion function.</param>
        /// <param name="dst">
        /// Optional pre-allocated destination. When provided it must match the source dimensions exactly;
        /// otherwise a new instance is created.
        /// </param>
        /// <returns>A <see cref="MatrixData{T}">MatrixData&lt;double&gt;</see> with all frames converted.</returns>
        public static MatrixData<double> ToDoubleAllFrames<T>(this MatrixData<T> src,
            Func<T, double> converter,
            MatrixData<double>? dst = null)
            where T : unmanaged
        {
            var (ret, xnum, ynum) = PrepareDstForToDouble(src, dst, src.FrameCount);
            dst = ret;

            Parallel.For(0, src.FrameCount, frameIndex =>
            {
                T[] srcArray = src.GetArray(frameIndex);
                double[] dstArray = dst.GetArray(frameIndex);
                ToDoubleProc(srcArray, dstArray, converter, xnum, ynum, useParallel: false);
                dst.Invalidate(frameIndex);
            });
            return dst;
        }

        /// <summary>
        /// Converts a single frame of <paramref name="src"/> to <c>double</c> using the specified element converter.
        /// </summary>
        /// <typeparam name="T">The source element type.</typeparam>
        /// <param name="src">Source matrix data.</param>
        /// <param name="converter">Per-element conversion function.</param>
        /// <param name="frameIndex">The frame to convert. If negative, the active frame index is used.</param>
        /// <param name="dst">
        /// Optional pre-allocated destination (single-frame, matching XCount/YCount);
        /// otherwise a new instance is created.
        /// </param>
        /// <returns>A single-frame <see cref="MatrixData{T}">MatrixData&lt;double&gt;</see> with the converted values.</returns>
        public static MatrixData<double> ToDouble<T>(this MatrixData<T> src,
            Func<T, double> converter,
            int frameIndex = -1,
            MatrixData<double>? dst = null)
            where T : unmanaged
        {
            var (dstRet, xnum, ynum) = PrepareDstForToDouble(src, dst, 1);
            dst = dstRet;

            ReadOnlyMemory<T> srcMem = src.AsMemory(frameIndex);
            double[] dstArray = dst.GetArray();

            ToDoubleProc(srcMem, dstArray, converter, xnum, ynum, useParallel: true);

            dst.Invalidate();
            return dst;
        }

        private static void ToDoubleProc<T>(ReadOnlyMemory<T> srcMem, double[] dstArray,
            Func<T, double> converter, int xnum, int ynum, bool useParallel)
            where T : unmanaged
        {
            int totalLength = xnum * ynum;

            // Row-level processing
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;
                ReadOnlySpan<T> srcRow = srcMem.Span.Slice(offset, xnum);
                ref double dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);
                ref double rowDstBase = ref Unsafe.Add(ref dstRefBase, offset);

                // Inner loop — no bounds checking
                for (int ix = 0; ix < xnum; ix++)
                {
                    ref readonly T currentSrc = ref srcRow[ix];
                    Unsafe.Add(ref rowDstBase, ix) = converter(currentSrc);
                }
            }

            // Parallel execution decision
            if (useParallel && Environment.ProcessorCount > 1 && totalLength >= ParallelThreshold)
                Parallel.For(0, ynum, ProcessRow);
            else
                for (int y = 0; y < ynum; y++)
                    ProcessRow(y);
        }

        #endregion

        #region Element type → element type

        /// <summary>
        /// Converts all frames of a <see cref="MatrixData{TSrc}"/> to <see cref="MatrixData{TDst}"/>
        /// using the specified element converter.
        /// Scale, units, metadata, and dimensions are copied from the source.
        /// </summary>
        /// <remarks>
        /// <para>This uses the same optimized Unsafe row-processing path as <see cref="ToDouble{T}"/>,
        /// providing better throughput than <see cref="DimensionalOperator.Map{TSrc,TDst}"/> for
        /// pure value-only conversions (no coordinate dependency).</para>
        /// <para>Since the converter receives only the source value (not coordinates), use
        /// <see cref="DimensionalOperator.Map{TSrc,TDst}"/> instead when the conversion depends
        /// on pixel position or frame index.</para>
        /// </remarks>
        /// <example>
        /// <code>
        /// // double → ushort with clamping
        /// var ushortData = doubleData.ConvertTo&lt;double, ushort&gt;(
        ///     v => (ushort)Math.Clamp(v, 0, 65535));
        ///
        /// // ushort → double (widening, trivial)
        /// var doubleData = ushortData.ConvertTo&lt;ushort, double&gt;(v => v);
        ///
        /// // float → byte with normalization
        /// var byteData = floatData.ConvertTo&lt;float, byte&gt;(
        ///     v => (byte)Math.Clamp(v / maxVal * 255, 0, 255));
        /// </code>
        /// </example>
        /// <typeparam name="TSrc">The source element type.</typeparam>
        /// <typeparam name="TDst">The destination element type.</typeparam>
        /// <param name="src">Source matrix data.</param>
        /// <param name="converter">
        /// Element conversion function. The caller is responsible for clamping/rounding
        /// as needed for narrowing conversions (e.g. double → byte).
        /// </param>
        /// <returns>A new <see cref="MatrixData{TDst}"/> with all frames converted.</returns>
        public static MatrixData<TDst> ConvertTo<TSrc, TDst>(
            this MatrixData<TSrc> src,
            Func<TSrc, TDst> converter)
            where TSrc : unmanaged
            where TDst : unmanaged
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            int frameCount = src.FrameCount;
            var dstArrays = new TDst[frameCount][];

            if (frameCount >= 2)
            {
                Parallel.For(0, frameCount, frame =>
                {
                    dstArrays[frame] = ConvertArray(src.GetArray(frame), converter, xnum, ynum);
                });
            }
            else
            {
                dstArrays[0] = ConvertArray(src.GetArray(0), converter, xnum, ynum);
            }

            var result = new MatrixData<TDst>(xnum, ynum, new List<TDst[]>(dstArrays));
            result.CopyPropertiesFrom(src);
            return result;
        }

        /// <summary>
        /// Converts a single frame of a <see cref="MatrixData{TSrc}"/> to a single-frame
        /// <see cref="MatrixData{TDst}"/> using the specified element converter.
        /// </summary>
        /// <typeparam name="TSrc">The source element type.</typeparam>
        /// <typeparam name="TDst">The destination element type.</typeparam>
        /// <param name="src">Source matrix data.</param>
        /// <param name="converter">Element conversion function.</param>
        /// <param name="frameIndex">
        /// The frame to convert. If negative, the active frame index is used.
        /// </param>
        /// <returns>A new single-frame <see cref="MatrixData{TDst}"/>.</returns>
        public static MatrixData<TDst> ConvertTo<TSrc, TDst>(
            this MatrixData<TSrc> src,
            Func<TSrc, TDst> converter,
            int frameIndex)
            where TSrc : unmanaged
            where TDst : unmanaged
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            var dstArray = ConvertArray(src.AsMemory(frameIndex), converter, xnum, ynum);

            var result = new MatrixData<TDst>(xnum, ynum, dstArray);
            result.CopyPropertiesFrom(src);
            return result;
        }

        /// <summary>
        /// Converts all frames of <paramref name="src"/> to the specified <paramref name="targetType"/>
        /// with optional linear scaling, dispatching through the typed
        /// <see cref="ConvertTo{TSrc,TDst}(MatrixData{TSrc},Func{TSrc,TDst})"/> path.
        /// </summary>
        /// <remarks>
        /// Both <paramref name="src"/> value type and <paramref name="targetType"/> must be in
        /// <see cref="MatrixData.SupportedPrimitiveTypes"/> (excludes <c>Complex</c>).
        /// Integral target types receive rounded, clamped values. Floating-point targets are
        /// clamped to their representable range only.
        /// </remarks>
        /// <param name="src">Source data (type-erased).</param>
        /// <param name="targetType">CLR type to convert elements to.</param>
        /// <param name="doScale">
        /// When <c>true</c>, applies a linear mapping:
        /// <c>dst = tgtMin + (src − srcMin) / (srcMax − srcMin) × (tgtMax − tgtMin)</c>.
        /// When <c>false</c>, direct-casts each element, clamping to the target range.
        /// </param>
        /// <param name="srcMin">Source range minimum (used only when <paramref name="doScale"/> is <c>true</c>).</param>
        /// <param name="srcMax">Source range maximum (used only when <paramref name="doScale"/> is <c>true</c>).</param>
        /// <param name="tgtMin">Target range minimum (used only when <paramref name="doScale"/> is <c>true</c>).</param>
        /// <param name="tgtMax">Target range maximum (used only when <paramref name="doScale"/> is <c>true</c>).</param>
        /// <returns>A new <see cref="IMatrixData"/> with the element type set to <paramref name="targetType"/>.</returns>
        public static IMatrixData ConvertToType(this IMatrixData src, Type targetType,
            bool doScale = false,
            double srcMin = 0.0, double srcMax = 1.0,
            double tgtMin = 0.0, double tgtMax = 1.0)
        {
            if (!MatrixData.SupportedPrimitiveTypes.Contains(src.ElementType.Type))
                throw new ArgumentException(
                    $"Source type '{src.ElementType.Type.Name}' is not a supported primitive type. " +
                    $"For complex or custom struct types, provide an explicit element converter via ConvertTo<TSrc,TDst>.",
                    nameof(src));
            if (!MatrixData.SupportedPrimitiveTypes.Contains(targetType))
                throw new ArgumentException(
                    $"Target type '{targetType.Name}' is not a supported primitive type. " +
                    $"For complex or custom struct types, provide an explicit element converter via ConvertTo<TSrc,TDst>.",
                    nameof(targetType));

            // All SupportedPrimitiveTypes implement INumber<T>; verify at the dispatch boundary
            // in case this method is ever called with a type that was added to SupportedPrimitiveTypes
            // but does not implement INumber<T> (e.g. a future Complex addition).
            static bool implementsINumber(Type t) =>
                t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INumber<>));
            if (!implementsINumber(src.ElementType.Type))
                throw new NotSupportedException(
                    $"Source type '{src.ElementType.Type.Name}' does not implement INumber<T>. " +
                    $"Use ConvertTo<TSrc,TDst> with a custom converter lambda instead.");
            if (!implementsINumber(targetType))
                throw new NotSupportedException(
                    $"Target type '{targetType.Name}' does not implement INumber<T>. " +
                    $"Use ConvertTo<TSrc,TDst> with a custom converter lambda instead.");

            var method = typeof(MatrixDataValueConverter)
                .GetMethod(nameof(ConvertToTypeDispatch),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(src.ElementType.Type, targetType);

            return (IMatrixData)method.Invoke(null, [src, doScale, srcMin, srcMax, tgtMin, tgtMax])!;
        }

        // Concrete generic dispatch; called via reflection above.
        // INumber<T> constraint is satisfied by all MatrixData.SupportedPrimitiveTypes.
        private static IMatrixData ConvertToTypeDispatch<TSrc, TDst>(
            MatrixData<TSrc> src, bool doScale,
            double srcMin, double srcMax, double tgtMin, double tgtMax)
            where TSrc : unmanaged, INumber<TSrc>
            where TDst : unmanaged, INumber<TDst>
        {
            Func<TSrc, TDst> converter;
            if (!doScale)
            {
                // TSrc → double → TDst with saturation (clamp to TDst range, no throw).
                converter = v => TDst.CreateSaturating(double.CreateSaturating(v));
            }
            else
            {
                double srcRange = srcMax - srcMin;
                // Precompute scale once: replaces per-pixel division with multiply.
                // isZeroRange → scale=0 collapses the formula to tgtMin with no branch in the loop.
                double scale = Math.Abs(srcRange) < double.Epsilon ? 0.0 : (tgtMax - tgtMin) / srcRange;
                converter = v =>
                {
                    double d = double.CreateSaturating(v);
                    return TDst.CreateSaturating(tgtMin + (d - srcMin) * scale);
                };
            }
            return src.ConvertTo(converter);
        }

        /// <summary>
        /// Core array conversion using Unsafe row-processing.
        /// Uses row-level parallelism for large arrays, matching the ToDouble path.
        /// </summary>
        private static TDst[] ConvertArray<TSrc, TDst>(ReadOnlyMemory<TSrc> srcMem,
            Func<TSrc, TDst> converter, int xnum, int ynum)
            where TSrc : unmanaged
            where TDst : unmanaged
        {
            var dstArray = new TDst[srcMem.Length];
            int totalLength = xnum * ynum;

            // Row-level processing
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;
                ReadOnlySpan<TSrc> srcRow = srcMem.Span.Slice(offset, xnum);
                Span<TDst> dstRow = new Span<TDst>(dstArray, offset, xnum);

                for (int ix = 0; ix < xnum; ix++)
                {
                    dstRow[ix] = converter(srcRow[ix]);
                }
            }

            // Row-parallel for large single-frame arrays
            if (Environment.ProcessorCount > 1 && totalLength >= ParallelThreshold)
                Parallel.For(0, ynum, ProcessRow);
            else
                for (int y = 0; y < ynum; y++)
                    ProcessRow(y);

            return dstArray;
        }

        #endregion

        #region Shared helpers

        /// <summary>
        /// The destination of a conversion to double: <paramref name="dst"/> when given (it must match the
        /// source's size and <paramref name="frameCount"/>), or else a new instance. Either way the source's
        /// properties are copied onto it.
        /// </summary>
        private static (MatrixData<double> dst, int xnum, int ynum)
            PrepareDstForToDouble<T>(MatrixData<T> src, MatrixData<double>? dst, int frameCount)
            where T : unmanaged
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            if (dst == null)
            {
                dst = new MatrixData<double>(xnum, ynum, frameCount);
            }
            else if (dst.XCount != xnum || dst.YCount != ynum || dst.FrameCount != frameCount)
            {
                throw new ArgumentException("Size mismatch.");
            }

            dst.CopyPropertiesFrom(src);
            return (dst, xnum, ynum);
        }

        #endregion
    }
}
