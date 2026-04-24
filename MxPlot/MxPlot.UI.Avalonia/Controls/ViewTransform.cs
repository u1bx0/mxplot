namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Visual transform applied to the rendered bitmap in <see cref="MxRenderSurface"/>.
    /// For 90° rotations and <see cref="Transpose"/>, the effective display dimensions
    /// (width ↔ height) are swapped so pan, zoom, and FitToView operate in the rotated space.
    /// </summary>
    public enum ViewTransform
    {
        /// <summary>No transform (default).</summary>
        None,

        /// <summary>Rotate 90° clockwise.</summary>
        Rotate90CW,

        /// <summary>Rotate 90° counter-clockwise (left 90°).</summary>
        Rotate90CCW,

        /// <summary>Rotate 180°.</summary>
        Rotate180,

        /// <summary>Flip horizontally (mirror left ↔ right).</summary>
        FlipH,

        /// <summary>Flip vertically (mirror top ↔ bottom).</summary>
        FlipV,

        /// <summary>
        /// Transpose – swap X and Y axes (equivalent to Rotate90CW + FlipV).
        /// Converts a bitmap with rows=Y, cols=Z to displayed as cols=Y, rows=Z,
        /// which is the natural orientation for an orthogonal YZ side view.
        /// </summary>
        Transpose,
    }
}
