using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MxPlot.Core.Processing
{

    /// <summary>
    /// Provides static methods for converting matrix data between types, supporting both
    /// single-frame and multi-frame operations.
    /// </summary>
    /// <remarks>
    /// This class includes conversion methods optimized for performance, utilizing parallel
    /// processing and unsafe code where appropriate. It supports conversion from unmanaged types
    /// and complex numbers, with options for applying specific conversion modes and logarithmic scaling.
    /// Callers must ensure that destination matrices are correctly sized to avoid exceptions.
    /// The conversion methods are designed for use with large matrices and may leverage parallelism
    /// based on matrix size and processor count.
    /// </remarks>
    public static class MatrixDataValueConverter
    {
        public static readonly int ParallelThreshold = 256 * 256;

        #region ToDouble<T> (T → double)

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

        public static MatrixData<double> ToDouble<T>(this MatrixData<T> src,
         Func<T, double> converter,
         int frameIndex = -1,
         MatrixData<double>? dst = null)
         where T : unmanaged
        {
            var (dstRet, xnum, ynum) = PrepareDstForToDouble(src, dst, 1);
            dst = dstRet;

            T[] srcArray = src.GetArray(frameIndex);
            double[] dstArray = dst.GetArray();

            ToDoubleProc(srcArray, dstArray, converter, xnum, ynum, useParallel: true);

            dst.Invalidate();
            return dst;
        }

        private static (MatrixData<double> dst, int xnum, int ynum)
            PrepareDstForToDouble<T>(MatrixData<T> src, MatrixData<double>? dst, int frameCount)
            where T : unmanaged
        {
            int xnum = src.XCount;
            int ynum = src.YCount;
            var scale = src.GetScale();
            if (dst == null)
            {
                dst = new MatrixData<double>(xnum, ynum, frameCount);
            }
            else if (dst.XCount != xnum || dst.YCount != ynum || dst.FrameCount != frameCount)
            {
                throw new ArgumentException("Size mismatch.");
            }

            // Sync scale if different
            if (!dst.GetScale().Equals(scale))
            {
                dst.SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            }
            return (dst, xnum, ynum);
        }

        private static void ToDoubleProc<T>(T[] srcArray, double[] dstArray,
            Func<T, double> converter, int xnum, int ynum, bool useParallel)
            where T : unmanaged
        {
            int totalLength = xnum * ynum;

            // Row-level processing
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;
                // Obtain base references to bypass array bounds checking
                ref T srcRefBase = ref MemoryMarshal.GetArrayDataReference(srcArray);
                ref double dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);

                // Advance to the start of the current row (equivalent to ptr + offset)
                ref T currentSrc = ref Unsafe.Add(ref srcRefBase, offset);
                ref double currentDst = ref Unsafe.Add(ref dstRefBase, offset);

                // Inner loop — no bounds checking
                for (int ix = 0; ix < xnum; ix++)
                {
                    currentDst = converter(currentSrc);

                    // Advance pointers (equivalent to ptr++)
                    currentSrc = ref Unsafe.Add(ref currentSrc, 1);
                    currentDst = ref Unsafe.Add(ref currentDst, 1);
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

        #region ToDouble (Complex → double)

        public static MatrixData<double> ToDouble(
            this MatrixData<Complex> src,
            ComplexValueMode mode,
            bool applyLog10 = false,
            int frameIndex = -1,
            MatrixData<double>? dst = null)
        {
            var (ret, xnum, ynum) = PrepareDstForToDouble<Complex>(src, dst, 1);
            dst = ret;

            Complex[] srcArray = src.GetArray(frameIndex);
            double[] dstArray = dst.GetArray();

            ToDoubleFromComplexProc(srcArray, dstArray, mode, applyLog10, xnum, ynum, useParallel: true);

            dst.Invalidate();
            return dst;
        }

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
                Complex[] srcArray = src.GetArray(frameIndex);
                double[] dstArray = dst.GetArray(frameIndex);
                ToDoubleFromComplexProc(srcArray, dstArray, mode, applyLog10, xnum, ynum, useParallel: false);
                dst.Invalidate(frameIndex);
            });
            return dst;
        }

        private static void ToDoubleFromComplexProc(Complex[] srcArray, double[] dstArray,
            ComplexValueMode mode, bool applyLog10, int xnum, int ynum, bool useParallel)
        {
            // Row-level processing (optimized path)
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;

                // Obtain base references for the current row
                ref Complex srcRefBase = ref MemoryMarshal.GetArrayDataReference(srcArray);
                ref double dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);

                ref Complex rowSrcBase = ref Unsafe.Add(ref srcRefBase, offset);
                ref double rowDstBase = ref Unsafe.Add(ref dstRefBase, offset);

                // Inner loop using base+ix addressing (JIT-friendly pattern)
                for (int ix = 0; ix < xnum; ix++)
                {
                    ref Complex currentSrc = ref Unsafe.Add(ref rowSrcBase, ix);

                    double val;

                    switch (mode)
                    {
                        case ComplexValueMode.Magnitude:
                            val = currentSrc.Magnitude;
                            break;
                        case ComplexValueMode.Real:
                            val = currentSrc.Real;
                            break;
                        case ComplexValueMode.Imaginary:
                            val = currentSrc.Imaginary;
                            break;
                        case ComplexValueMode.Phase:
                            val = currentSrc.Phase;
                            break;
                        case ComplexValueMode.Power:
                            double r = currentSrc.Real;
                            double i = currentSrc.Imaginary;
                            val = r * r + i * i;
                            break;
                        default:
                            val = 0.0;
                            break;
                    }

                    if (applyLog10)
                    {
                        double eps = 1e-12;
                        val = Math.Log10(Math.Max(val, eps));
                    }

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

        #region ConvertTo<TSrc, TDst> (general-purpose type conversion)

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
            var dstArray = ConvertArray(src.GetArray(frameIndex), converter, xnum, ynum);

            var result = new MatrixData<TDst>(xnum, ynum, dstArray);
            result.CopyPropertiesFrom(src);
            return result;
        }

        /// <summary>
        /// Core array conversion using Unsafe row-processing.
        /// Uses row-level parallelism for large arrays, matching the ToDouble path.
        /// </summary>
        private static TDst[] ConvertArray<TSrc, TDst>(TSrc[] srcArray,
            Func<TSrc, TDst> converter, int xnum, int ynum)
            where TSrc : unmanaged
            where TDst : unmanaged
        {
            var dstArray = new TDst[srcArray.Length];
            int totalLength = xnum * ynum;

            // Row-level processing
            void ProcessRow(int iy)
            {
                int offset = iy * xnum;
                ref TSrc srcRefBase = ref MemoryMarshal.GetArrayDataReference(srcArray);
                ref TDst dstRefBase = ref MemoryMarshal.GetArrayDataReference(dstArray);

                ref TSrc currentSrc = ref Unsafe.Add(ref srcRefBase, offset);
                ref TDst currentDst = ref Unsafe.Add(ref dstRefBase, offset);

                for (int ix = 0; ix < xnum; ix++)
                {
                    currentDst = converter(currentSrc);
                    currentSrc = ref Unsafe.Add(ref currentSrc, 1);
                    currentDst = ref Unsafe.Add(ref currentDst, 1);
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

        #region ConvertToType (IMatrixData type-erased dispatcher)

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
            if (!MatrixData.SupportedPrimitiveTypes.Contains(src.ValueType))
                throw new ArgumentException(
                    $"Source type '{src.ValueType.Name}' is not a supported primitive type. " +
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
            if (!implementsINumber(src.ValueType))
                throw new NotSupportedException(
                    $"Source type '{src.ValueType.Name}' does not implement INumber<T>. " +
                    $"Use ConvertTo<TSrc,TDst> with a custom converter lambda instead.");
            if (!implementsINumber(targetType))
                throw new NotSupportedException(
                    $"Target type '{targetType.Name}' does not implement INumber<T>. " +
                    $"Use ConvertTo<TSrc,TDst> with a custom converter lambda instead.");

            var method = typeof(MatrixDataValueConverter)
                .GetMethod(nameof(ConvertToTypeDispatch),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(src.ValueType, targetType);

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

        #endregion
    }
}
