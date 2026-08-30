using System;
using System.Collections.Generic;

namespace MxPlot.Core
{
    /// <summary>
    /// Extension methods for locating an <see cref="Axis"/> within a collection.
    /// </summary>
    public static class AxisExtensions
    {
        /// <summary>
        /// Finds the first axis whose <see cref="Axis.Name"/> matches <paramref name="name"/>,
        /// case-insensitively — the same axis-name semantics as <see cref="DimensionStructure"/>'s
        /// own lookups (<see cref="DimensionStructure.Contains(string)"/>, indexer), which this
        /// method backs.
        /// </summary>
        /// <param name="axes">The axes to search.</param>
        /// <param name="name">The axis name to look for. The comparison is case-insensitive.</param>
        /// <returns>The matching axis, or <c>null</c> if <paramref name="name"/> is null or no match is found.</returns>
        public static Axis? FindAxis(this IEnumerable<Axis> axes, string? name)
        {
            if (name == null) return null;
            foreach (var a in axes)
            {
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                    return a;
            }
            return null;
        }
    }
}
