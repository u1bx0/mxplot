using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Rendering context for multi-channel composite rendering.
    /// Carries all per-invocation parameters needed by <see cref="CompositeBitmapWriter"/>.
    /// </summary>
    /// <param name="FrameIndices">
    /// Frame indices to composite. Must have the same length as <paramref name="Recipes"/>.
    /// </param>
    /// <param name="Recipes">
    /// Per-layer rendering recipes (color, value range, gain, gamma, optional value converter).
    /// Must have the same length as <paramref name="FrameIndices"/>.
    /// </param>
    /// <param name="BlendMode">How the layers are blended into the final pixel.</param>
    public record CompositeRenderingContext(
        int[] FrameIndices,
        IReadOnlyList<BlendRecipe> Recipes,
        BlendMode BlendMode = BlendMode.Additive) : IRenderingContext;
}