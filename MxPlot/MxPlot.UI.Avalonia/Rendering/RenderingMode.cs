/// <summary>
/// Specifies how matrix data is rendered into the bitmap.
/// </summary>
public enum RenderingMode
{
    /// <summary>Single-frame LUT-based pseudo-color rendering.</summary>
    Lut,

    /// <summary>Multi-channel composite rendering.</summary>
    Composite,

    /// <summary>Color-coded depth rendering.</summary>
    ColorCoded,
}
