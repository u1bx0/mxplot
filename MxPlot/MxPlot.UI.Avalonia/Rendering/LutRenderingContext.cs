using System;
using System.Numerics;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Rendering context for LUT-based pseudo-color rendering.
    /// Carries all per-invocation parameters needed by <see cref="LutBitmapWriter"/>.
    /// </summary>
    /// <param name="FrameIndex">Zero-based frame index to render.</param>
    /// <param name="LookupTable">Lookup table for value-to-color mapping.</param>
    /// <param name="ValueMin">Minimum value of the display range.</param>
    /// <param name="ValueMax">Maximum value of the display range.</param>
    /// <param name="IsInvertedColor">
    /// When <c>true</c>, the color mapping is inverted (min maps to the top of the LUT,
    /// max maps to the bottom).
    /// </param>
    public record LutRenderingContext(
        int FrameIndex,
        LookupTable LookupTable,
        double ValueMin,
        double ValueMax,
        bool IsInvertedColor = false) : IRenderingContext
    {
        /// <summary>
        /// Returns the effective display range accounting for color inversion.
        /// </summary>
        internal (double ViewMin, double ViewMax) GetEffectiveRange()
            => IsInvertedColor ? (ValueMax, ValueMin) : (ValueMin, ValueMax);
    }
}