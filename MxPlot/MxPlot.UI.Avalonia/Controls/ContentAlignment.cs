namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Controls where the bitmap is pinned when it is smaller than the viewport
    /// (i.e. during <see cref="MxRenderSurface.FitToView"/> or at low zoom).
    /// Has no visual effect when the bitmap fills the viewport in both dimensions.
    /// Alignment is expressed in screen space and is independent of
    /// <see cref="ViewTransform"/> (e.g. <see cref="Top"/> always pins the image
    /// to the top of the screen, regardless of rotation).
    /// </summary>
    public enum ContentAlignment
    {
        TopLeft,
        Top,
        TopRight,
        Left,
        /// <summary>Default: image centred both horizontally and vertically.</summary>
        Center,
        Right,
        BottomLeft,
        Bottom,
        BottomRight,
    }
}
