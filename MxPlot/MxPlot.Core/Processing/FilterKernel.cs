using System;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Defines a spatial filter kernel applied per-pixel.
    /// Implementations receive the neighborhood values and return the filtered result.
    /// </summary>
    public interface IFilterKernel
    {
        /// <summary>Kernel half-size. Radius=1 → 3×3, Radius=2 → 5×5.</summary>
        int Radius { get; }

        /// <summary>
        /// Computes the output value from the neighborhood.
        /// </summary>
        /// <param name="values">
        /// Scratch buffer containing neighborhood values.
        /// Only the first <paramref name="count"/> elements are valid.
        /// The implementation may freely mutate the buffer (e.g. for sorting).
        /// </param>
        /// <param name="count">Number of valid elements (may be less than the full kernel size at edges).</param>
        double Apply(Span<double> values, int count);
    }

    /// <summary>
    /// Median filter kernel. Sorts the neighborhood and returns the middle value.
    /// </summary>
    public sealed class MedianKernel : IFilterKernel
    {
        public int Radius { get; }

        /// <param name="radius">Kernel half-size. 1 → 3×3, 2 → 5×5, etc.</param>
        public MedianKernel(int radius = 1)
        {
            if (radius < 1) throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be >= 1.");
            Radius = radius;
        }

        public double Apply(Span<double> values, int count)
        {
            var slice = values[..count];
            slice.Sort();
            return slice[count / 2];
        }
    }

    /// <summary>
    /// Gaussian filter kernel. Applies a weighted average using a precomputed Gaussian weight table.
    /// </summary>
    public sealed class GaussianKernel : IFilterKernel
    {
        public int Radius { get; }
        public double Sigma { get; }
        private readonly double[] _weights;

        /// <param name="radius">Kernel half-size. 1 → 3×3, 2 → 5×5, etc.</param>
        /// <param name="sigma">Standard deviation. If ≤ 0, defaults to <c>radius / 2.0</c>.</param>
        public GaussianKernel(int radius = 1, double sigma = 0)
        {
            if (radius < 1) throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be >= 1.");
            Radius = radius;
            Sigma = sigma > 0 ? sigma : radius / 2.0;
            _weights = BuildWeights();
        }

        public double Apply(Span<double> values, int count)
        {
            // When count equals the full kernel size, use the precomputed weights directly.
            // At edges (count < full size), fall back to uniform average for simplicity.
            int fullSize = (2 * Radius + 1) * (2 * Radius + 1);
            if (count == fullSize)
            {
                double sum = 0, wsum = 0;
                for (int i = 0; i < count; i++)
                {
                    sum += values[i] * _weights[i];
                    wsum += _weights[i];
                }
                return sum / wsum;
            }
            else
            {
                // Edge case: simple average (weights don't align with partial neighborhood)
                double sum = 0;
                for (int i = 0; i < count; i++)
                    sum += values[i];
                return sum / count;
            }
        }

        private double[] BuildWeights()
        {
            int side = 2 * Radius + 1;
            var w = new double[side * side];
            double s2 = 2.0 * Sigma * Sigma;
            int idx = 0;
            for (int dy = -Radius; dy <= Radius; dy++)
            {
                for (int dx = -Radius; dx <= Radius; dx++)
                {
                    w[idx++] = Math.Exp(-(dx * dx + dy * dy) / s2);
                }
            }
            return w;
        }
    }
}
