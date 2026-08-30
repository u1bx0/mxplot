namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Defines the rendering recipe (styling, contrast, and color) for a single layer/frame
    /// in a composite drawing.
    /// </summary>
    /// <param name="IsVisible">Indicates whether the layer/frame is visible.</param>
    /// <param name="ColorArgb">The ARGB color value for the layer/frame.</param>
    /// <param name="ValueMin">The minimum value for the layer/frame.</param>
    /// <param name="ValueMax">The maximum value for the layer/frame.</param>
    /// <param name="Gain">The gain applied to the layer/frame. Defaults to 1.0.</param>
    /// <param name="Gamma">The gamma correction applied to the layer/frame. Defaults to 1.0.</param>
    public record BlendRecipe(
        bool IsVisible,
        int ColorArgb,
        double ValueMin,
        double ValueMax,
        double Gain = 1.0,
        double Gamma = 1.0);

    /// <summary>
    /// Specifies how multiple layers are blended together during composite rendering.
    /// </summary>
    public enum BlendMode
    {
        /// <summary>Pixel values are added together. Best for multi-color fluorescence.</summary>
        Additive,

        /// <summary>The maximum pixel value among layers is kept. Best for depth/time color coding.</summary>
        Maximum
    }
}
