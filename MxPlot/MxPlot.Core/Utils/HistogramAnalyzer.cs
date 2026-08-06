using System;
using System.Numerics;

namespace MxPlot.Core.Utils
{
    /// <summary>
    /// Provides histogram analysis functionality for <see cref="IMatrixData"/>.
    /// </summary>
    public static class HistogramAnalyzer
    {
        /// <summary>
        /// Creates a histogram of the pixel values for a specified frame.
        /// </summary>
        /// <param name="src">The source matrix data.</param>
        /// <param name="frameIndex">The index of the frame to analyze. If negative, the active frame is used.</param>
        /// <param name="bins">The number of histogram bins.</param>
        /// <param name="minValue">
        /// Optional minimum value for histogram binning range. 
        /// When <c>null</c>, uses the frame's actual minimum value from <see cref="IMatrixData.GetValueRange"/>.
        /// </param>
        /// <param name="maxValue">
        /// Optional maximum value for histogram binning range. 
        /// When <c>null</c>, uses the frame's actual maximum value from <see cref="IMatrixData.GetValueRange"/>.
        /// </param>
        /// <returns>An array of integers representing the histogram bin counts.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="src"/> is null.</exception>
        /// <exception cref="NotSupportedException">Thrown when the underlying data type of <paramref name="src"/> is not supported.</exception>
        public static int[] CreateHistogram(this IMatrixData src, int frameIndex = -1, int bins = 256,
            double? minValue = null, double? maxValue = null)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (frameIndex < 0) frameIndex = src.ActiveIndex;

            // Use provided range or retrieve the value range for the specified frame
            double min, max;
            if (minValue.HasValue && maxValue.HasValue)
            {
                min = minValue.Value;
                max = maxValue.Value;
            }
            else
            {
                (min, max) = src.GetValueRange(frameIndex);
            }

            // Resolve the concrete type of MatrixData and pass its Span to the optimized calculation engine.
            return src switch
            {
                MatrixData<double> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<float> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<ushort> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<byte> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<int> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<uint> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<short> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<long> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<ulong> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<sbyte> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                MatrixData<decimal> md => Compute(md.AsSpan(frameIndex), min, max, bins),
                // Complex: defaults to Magnitude. Use CreateHistogram<T> overload with a converter for other modes.
                MatrixData<Complex> md => Compute(md.AsSpan(frameIndex), min, max, bins),

                _ => throw new NotSupportedException($"Histogram creation for {src.GetType()} is not supported.")
            };
        }

        /// <summary>
        /// Creates a histogram of the pixel values for a specified frame.
        /// </summary>
        /// <param name="src">The source matrix data.</param>
        /// <param name="frameIndex">The index of the frame to analyze. If negative, the active frame is used.</param>
        /// <param name="bins">The number of histogram bins.</param>
        /// <param name="valueMode">
        /// The index into the value range list that selects which scalar projection of <typeparamref name="T"/>
        /// to use for binning. For primitive types, this is always 0. For structured types (e.g.
        /// <see cref="Complex"/> or custom structs), each index corresponds to a type-specific
        /// scalar component (e.g. 0 = Magnitude, 1 = Real, 2 = Imaginary, 3 = Phase, and 4 = Power for <see cref="Complex"/>).
        /// The meaning of each index is defined by the struct's ValueRangeList convention.
        /// IMPORTANT: an appropriate <paramref name="converter"/> must be provided for structured types with the corresponding valueMode.
        /// </param>
        /// <param name="converter">
        /// An optional scalar projection function. When provided, overrides the default
        /// <see cref="NumericConverter.ToDouble{T}"/> conversion. Intended for structured types
        /// where the desired projection cannot be inferred from <paramref name="valueMode"/> alone,
        /// or when a custom mapping is required.
        /// </param>
        /// <param name="minValue">
        /// Optional minimum value for histogram binning range. 
        /// When <c>null</c>, uses the frame's actual minimum value from <see cref="MatrixData{T}.GetValueRange"/>.
        /// </param>
        /// <param name="maxValue">
        /// Optional maximum value for histogram binning range. 
        /// When <c>null</c>, uses the frame's actual maximum value from <see cref="MatrixData{T}.GetValueRange"/>.
        /// </param>
        /// <returns>An array of integers representing the histogram bin counts.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="src"/> is null.</exception>
        public static int[] CreateHistogram<T>(this MatrixData<T> src, int frameIndex = -1, int bins = 256,
            int valueMode = 0, Func<T, double>? converter = null, double? minValue = null, double? maxValue = null)
            where T : unmanaged
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (frameIndex < 0) frameIndex = src.ActiveIndex;

            // Use provided range or retrieve the value range for the specified frame
            double min, max;
            if (minValue.HasValue && maxValue.HasValue)
            {
                min = minValue.Value;
                max = maxValue.Value;
            }
            else
            {
                (min, max) = src.GetValueRange(frameIndex, valueMode);
            }

            //fallback to default converter if not provided
            if (converter == null)
                return Compute(src.AsSpan(frameIndex), min, max, bins);

            return Compute(src.AsSpan(frameIndex), converter, min, max, bins);
        }


        /// <summary>
        /// The highly optimized internal computation core that processes the data span.
        /// </summary>
        internal static int[] Compute<T>(ReadOnlySpan<T> srcSpan, double min, double max, int bins) 
            where T : unmanaged
        {
            if (bins <= 0) throw new ArgumentException("Bin count must be positive.", nameof(bins));

            int[] histogram = new int[bins];
            if (srcSpan.Length == 0 || max <= min) return histogram;

            // Pre-calculate the scaling factor to replace expensive floating-point divisions 
            // inside the loop with fast multiplications.
            // Subtracting 1e-9 prevents the bin index from reaching exactly equal to 'bins' when v == max.
            double scale = (bins - 1e-9) / (max - min);

            // Iterating over the ReadOnlySpan automatically eliminates array bound checks by the JIT compiler,
            // allowing for optimal hardware pipeline utilization.
            //for (int i = 0; i < srcSpan.Length; i++)
            foreach (var pixel in srcSpan)  // Using foreach to avoid bounds checks and improve readability
            {
                // JIT compiler removes boxing and branches, expanding this into a raw native conversion instruction.
                double v = NumericConverter.ToDouble(pixel);

                // Calculate the target bin index. Assuming most values fall within [min, max],
                // we handle rounding or edge cases cleanly using conditional expressions.
                int binIndex = (int)((v - min) * scale);

                // Clamp the index to prevent out-of-bounds access due to rare floating-point precision issues.
                // This pattern translates to conditional move (CMOV) instructions, avoiding branch mispredictions.
                binIndex = binIndex < 0 ? 0 : (binIndex >= bins ? bins - 1 : binIndex);

                histogram[binIndex]++;
            }

            return histogram;
        }

        internal static int[] Compute<T>(ReadOnlySpan<T> srcSpan, 
            Func<T, double> converter, double min, double max, int bins)
            where T : unmanaged
        {
            if (bins <= 0) throw new ArgumentException("Bin count must be positive.", nameof(bins));

            int[] histogram = new int[bins];
            if (srcSpan.Length == 0 || max <= min) return histogram;
            double scale = (bins - 1e-9) / (max - min);

            foreach (var pixel in srcSpan)
            {
                double v = converter(pixel);
                int binIndex = (int)((v - min) * scale);
                binIndex = binIndex < 0 ? 0 : (binIndex >= bins ? bins - 1 : binIndex);
                histogram[binIndex]++;
            }
            return histogram;
        }
    }
}