using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace MxPlot.Core.Arithmetic
{
    /// <summary>
    /// Provides element-wise arithmetic operations for MatrixData objects using Generic Math.
    /// Uses INumberBase&lt;T&gt; for maximum type compatibility.
    /// Optimized with Span&lt;T&gt; for better performance.
    /// </summary>
    /// <remarks>
    /// <para><b>Complex Number Support:</b></para>
    /// <para>
    /// While Complex type implements INumberBase&lt;Complex&gt; and can be used with matrix-to-matrix operations,
    /// scalar operations (e.g., Multiply(double), Add(double)) are <b>NOT fully supported</b> for Complex matrices.
    /// Scalar values are converted to Complex(scalar, 0), which means you cannot multiply by imaginary numbers.
    /// </para>
    /// <para>
    /// Dedicated Complex-specific overloads (e.g., Multiply(MatrixData&lt;Complex&gt;, Complex)) may be added in future releases.
    /// </para>
    /// <para><b>Coordinate and Metadata Inheritance:</b></para>
    /// <para>
    /// For binary operations (matrix-to-matrix), physical coordinates (XMin, XMax, YMin, YMax), units, metadata, 
    /// and dimension structures are inherited from the <b>first matrix argument</b>. The second matrix's metadata is ignored.
    /// </para>
    /// </remarks>
    public static class MatrixArithmetic
    {
        #region Matrix-Matrix Operations

        /// <summary>
        /// Performs element-wise addition of two matrices.
        /// Physical coordinates are inherited from the first matrix.
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumberBase&lt;T&gt;).</typeparam>
        /// <param name="a">The first matrix.</param>
        /// <param name="b">The second matrix.</param>
        /// <returns>A new MatrixData containing the element-wise sum.</returns>
        /// <exception cref="ArgumentException">Thrown if matrices have different dimensions.</exception>
        public static MatrixData<T> Add<T>(this MatrixData<T> a, MatrixData<T> b) 
            where T : unmanaged, INumberBase<T>
        {
            ValidateDimensions(a, b, nameof(Add));
            return ApplyBinaryOperation(a, b, (x, y) => x + y);
        }

        /// <summary>
        /// Performs element-wise subtraction of two matrices (A - B).
        /// Useful for background subtraction in measurement data.
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumberBase&lt;T&gt;).</typeparam>
        /// <param name="signal">The signal matrix.</param>
        /// <param name="background">The background matrix to subtract.</param>
        /// <returns>A new MatrixData containing the difference.</returns>
        /// <exception cref="ArgumentException">Thrown if matrices have different dimensions.</exception>
        public static MatrixData<T> Subtract<T>(this MatrixData<T> signal, MatrixData<T> background) 
            where T : unmanaged, INumberBase<T>
        {
            ValidateDimensions(signal, background, nameof(Subtract));
            return ApplyBinaryOperation(signal, background, (x, y) => x - y);
        }

        /// <summary>
        /// Performs element-wise multiplication of two matrices. If b has only one frame, it is treated as a background to be applied to all frames of a.
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumberBase&lt;T&gt;).</typeparam>
        /// <param name="a">The first matrix.</param>
        /// <param name="b">The second matrix.</param>
        /// <returns>A new MatrixData containing the element-wise product.</returns>
        /// <exception cref="ArgumentException">Thrown if matrices have different dimensions.</exception>
        public static MatrixData<T> Multiply<T>(this MatrixData<T> a, MatrixData<T> b) 
            where T : unmanaged, INumberBase<T>
        {
            ValidateDimensions(a, b, nameof(Multiply));
            return ApplyBinaryOperation(a, b, (x, y) => x * y);
        }

        /// <summary>
        /// Performs element-wise division of two matrices (A / B).
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumberBase&lt;T&gt;).</typeparam>
        /// <param name="a">The numerator matrix.</param>
        /// <param name="b">The denominator matrix.</param>
        /// <returns>A new MatrixData containing the element-wise quotient.</returns>
        /// <exception cref="ArgumentException">Thrown if matrices have different dimensions.</exception>
        /// <exception cref="DivideByZeroException">Thrown if any element in b is zero.</exception>
        public static MatrixData<T> Divide<T>(this MatrixData<T> a, MatrixData<T> b) 
            where T : unmanaged, INumberBase<T>
        {
            ValidateDimensions(a, b, nameof(Divide));
            return ApplyBinaryOperation(a, b, (x, y) =>
            {
                if (T.IsZero(y))
                    throw new DivideByZeroException("Division by zero");
                return x / y;
            });
        }
        
        #endregion

        #region Scalar Operations

        /// <summary>
        /// Multiplies all elements by a scalar value.
        /// Useful for gain correction and unit conversion.
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumberBase&lt;T&gt;).</typeparam>
        /// <param name="source">The source matrix.</param>
        /// <param name="scalar">The scalar multiplier.</param>
        /// <returns>A new MatrixData with all elements multiplied by the scalar.</returns>
        public static MatrixData<T> Multiply<T>(this MatrixData<T> data, double scaleFactor) 
            where T : unmanaged, INumberBase<T>
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            
            T scalarT = T.CreateChecked(scaleFactor);
            return ApplyUnaryOperation(data, value => value * scalarT);
        }

        /// <summary>
        /// Adds a scalar value to all elements.
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumberBase&lt;T&gt;).</typeparam>
        /// <param name="data">The source matrix.</param>
        /// <param name="scalar">The scalar value to add.</param>
        /// <returns>A new MatrixData with the scalar added to all elements.</returns>
        public static MatrixData<T> Add<T>(this MatrixData<T> data, double scalar) 
            where T : unmanaged, INumberBase<T>
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            
            T scalarT = T.CreateChecked(scalar);
            return ApplyUnaryOperation(data, value => value + scalarT);
        }

        /// <summary>
        /// Subtracts a scalar value from all elements.
        /// </summary>
        /// <typeparam name="T">The numeric type (must implement INumberBase&lt;T&gt;).</typeparam>
        /// <param name="data">The source matrix.</param>
        /// <param name="scalar">The scalar value to subtract.</param>
        /// <returns>A new MatrixData with the scalar subtracted from all elements.</returns>
        public static MatrixData<T> Subtract<T>(this MatrixData<T> data, double scalar) 
            where T : unmanaged, INumberBase<T>
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            
            T scalarT = T.CreateChecked(scalar);
            return ApplyUnaryOperation(data, value => value - scalarT);
        }

        #endregion


        #region Core Implementation with Generic Math and Span Optimization

        private static void ValidateDimensions<T>(MatrixData<T> a, MatrixData<T> b, string operation) 
            where T : unmanaged
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (b == null) throw new ArgumentNullException(nameof(b));

            // Check XY dimensions
            if (a.XCount != b.XCount || a.YCount != b.YCount)
                throw new ArgumentException(
                    $"{operation}: Matrix dimensions must match. " +
                    $"A: {a.XCount}x{a.YCount}, B: {b.XCount}x{b.YCount}");

            // Check frame counts
            if (a.FrameCount != b.FrameCount && b.FrameCount != 1)
                throw new ArgumentException(
                    $"{operation}: Frame counts must match or second matrix must have 1 frame. " +
                    $"A: {a.FrameCount}, B: {b.FrameCount}");

            // Check dimension structure compatibility (if both have multi-axis structure)
            if (b.FrameCount > 1 && a.Dimensions?.Axes?.Any() == true && b.Dimensions?.Axes?.Any() == true)
            {
                var axesA = a.Dimensions.Axes.ToArray();
                var axesB = b.Dimensions.Axes.ToArray();

                if (axesA.Length != axesB.Length)
                    throw new ArgumentException(
                        $"{operation}: Dimension structures must match. " +
                        $"A has {axesA.Length} axes, B has {axesB.Length} axes.");

                // Check each axis compatibility
                for (int i = 0; i < axesA.Length; i++)
                {
                    if (axesA[i].Count != axesB[i].Count)
                        throw new ArgumentException(
                            $"{operation}: Axis {i} count mismatch. " +
                            $"A[{i}] ({axesA[i].Name}): {axesA[i].Count}, " +
                            $"B[{i}] ({axesB[i].Name}): {axesB[i].Count}");

                    // Optional: Check axis names for better error messages
                    if (axesA[i].Name != axesB[i].Name)
                    {
                        // Warning: Different axis names but same structure
                        System.Diagnostics.Debug.WriteLine(
                            $"Warning: {operation} - Axis {i} name mismatch: " +
                            $"A='{axesA[i].Name}', B='{axesB[i].Name}'");
                    }
                }
            }
        }

        private static MatrixData<T> ApplyBinaryOperation<T>(
            MatrixData<T> a, 
            MatrixData<T> b, 
            Func<T, T, T> operation) 
            where T : unmanaged, INumberBase<T>
        {
            var resultArrays = new List<T[]>(a.FrameCount);
            bool useSingleBackground = b.FrameCount == 1;

            for (int frame = 0; frame < a.FrameCount; frame++)
            {
                var arrayA = a.GetArray(frame);
                var arrayB = b.GetArray(useSingleBackground ? 0 : frame);
                var resultArray = new T[arrayA.Length];

                // Use Span<T> with Generic Math operators for optimal performance
                var spanA = arrayA.AsSpan();
                var spanB = arrayB.AsSpan();
                var spanResult = resultArray.AsSpan();

                for (int i = 0; i < spanA.Length; i++)
                {
                    spanResult[i] = operation(spanA[i], spanB[i]);
                }

                resultArrays.Add(resultArray);
            }

            return CreateResult(a, resultArrays);
        }

        private static MatrixData<T> ApplyUnaryOperation<T>(
            MatrixData<T> source, 
            Func<T, T> operation) 
            where T : unmanaged, INumberBase<T>
        {
            var resultArrays = new List<T[]>(source.FrameCount);

            for (int frame = 0; frame < source.FrameCount; frame++)
            {
                var srcArray = source.GetArray(frame);
                var resultArray = new T[srcArray.Length];

                // Use Span<T> with Generic Math for performance
                var spanSrc = srcArray.AsSpan();
                var spanResult = resultArray.AsSpan();

                for (int i = 0; i < spanSrc.Length; i++)
                {
                    spanResult[i] = operation(spanSrc[i]);
                }

                resultArrays.Add(resultArray);
            }

            return CreateResult(source, resultArrays);
        }

        private static MatrixData<T> CreateResult<T>(MatrixData<T> source, List<T[]> resultArrays)
            where T : unmanaged
        {   
            var result = new MatrixData<T>(source.XCount, source.YCount, resultArrays);

            result.SetXYScale(source.XMin, source.XMax, source.YMin, source.YMax);
            result.XUnit = source.XUnit;
            result.YUnit = source.YUnit;

            if (source.Metadata != null)
            {
                foreach (var kvp in source.Metadata)
                    result.Metadata[kvp.Key] = kvp.Value;
            }

            if (source.Dimensions?.Axes?.Any() == true)
            {
                var axes = Axis.CreateFrom(source.Dimensions.Axes.ToArray());
                result.DefineDimensions(axes);
            }

            return result;
        }

        #endregion
    }
}
