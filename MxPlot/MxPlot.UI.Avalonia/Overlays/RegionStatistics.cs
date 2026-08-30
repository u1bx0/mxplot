using System.Globalization;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Immutable statistics computed for the pixel region enclosed by an
    /// <see cref="IAnalyzableOverlay"/> overlay object.
    /// </summary>
    public readonly struct RegionStatistics
    {
        public double Min { get; }
        public double Max { get; }
        public double Average { get; }
        public double Sum { get; }
        public int NumPoints { get; }

        public RegionStatistics(double min, double max, double average, double sum, int numPoints)
        {
            Min = min;
            Max = max;
            Average = average;
            Sum = sum;
            NumPoints = numPoints;
        }

        private static string Fmt(double v) =>
            v.ToString("G4", CultureInfo.InvariantCulture);

        /// <summary>Returns the two-line label displayed inside the overlay region.</summary>
        public string ToLabel() =>
            $"Min {Fmt(Min)}, Max {Fmt(Max)}, Avg {Fmt(Average)}\n(n={NumPoints})";

        /// <summary>
        /// Returns a single-line label prefixed with <paramref name="prefix"/> (e.g. a channel tag
        /// like "Ch0"), for composite per-channel statistics breakdowns. Omits "(n=...)" -- the same
        /// ROI/point count applies to every channel, so the caller appends it once instead of
        /// repeating it per line.
        /// </summary>
        public string ToCompactLabel(string prefix) =>
            $"{prefix}: Min {Fmt(Min)}, Max {Fmt(Max)}, Avg {Fmt(Average)}";
    }
}
