using MxPlot.Core;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Plugins
{
    /// <summary>
    /// Provides rendering capabilities to an <see cref="IRenderExportPlugin"/>.
    /// Implemented by <c>MatrixPlotter</c> and passed to
    /// <see cref="IRenderExportPlugin.ExportAsync"/> at export time.
    /// </summary>
    public interface IRenderHost
    {
        /// <summary>The matrix data currently displayed in the plotter.</summary>
        IMatrixData Data { get; }

        /// <summary>
        /// A short human-readable label for the view being exported (e.g. <c>"X-Z View"</c>).
        /// Used by export dialogs to indicate which view is being exported.
        /// Returns <c>null</c> for the main view.
        /// </summary>
        string? ViewLabel => null;

        /// <summary>
        /// Names of axes that must not be offered as the animation axis in export dialogs,
        /// because this view already consumes them.
        /// </summary>
        /// <remarks>
        /// Two things land here: a side view's ortho-depth axis (the Y axis when exporting the
        /// X-Z view, since it is the slice dimension), and the Channel axis while Composite
        /// rendering is active (every channel is blended into each frame, so stepping along it
        /// would produce identical frames). A view can consume both at once, which is why this
        /// is a list rather than a single name.
        /// </remarks>
        IReadOnlyList<string>? ExcludedAxisNames => null;

        /// <summary>
        /// The natural (1:1 zoom) render size of the current view in pixels.
        /// Suitable as the default output size in an export settings dialog.
        /// </summary>
        global::Avalonia.Size CurrentRenderSize { get; }

        /// <summary>
        /// Whether overlays are currently visible in the plotter.
        /// Suitable as the default overlay setting in an export settings dialog.
        /// </summary>
        bool IsOverlayVisible { get; }

        /// <summary>
        /// Renders the specified frame and returns it as a top-down BGRA32 byte array
        /// (4 bytes per pixel, row-major). The array length is always
        /// <c><paramref name="size"/>.Width × <paramref name="size"/>.Height × 4</c>.
        /// <para>
        /// This method is <b>thread-safe</b>: it may be called from any thread, including
        /// a background <see cref="System.Threading.Tasks.Task"/>. All required UI-thread
        /// marshalling is handled internally by the host implementation.
        /// </para>
        /// </summary>
        /// <param name="frameIndex">Zero-based frame index (0 to <see cref="IMatrixData.FrameCount"/> − 1).</param>
        /// <param name="size">Output pixel size.</param>
        /// <param name="withOverlay">Whether to render overlays onto the frame.</param>
        Task<byte[]> RenderFrameAsync(int frameIndex, global::Avalonia.Size size, bool withOverlay);
    }
}
