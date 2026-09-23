using Avalonia.Controls;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Plugins
{
    /// <summary>
    /// Adds an entry to the "Export as…" submenu of every <see cref="Views.MatrixPlotter"/> window.
    /// The exporter receives rendered frame bitmaps (LUT and overlays applied),
    /// not the raw <see cref="MxPlot.Core.IMatrixData"/> pixel values.
    /// </summary>
    /// <remarks>
    /// To export raw data instead of the rendered view, implement
    /// <c>IMatrixDataWriter</c> and register via <c>FormatRegistry</c>.
    /// <para>
    /// Typical implementation flow inside <see cref="ExportAsync"/>:
    /// <list type="number">
    ///   <item>Read <see cref="IRenderHost.Data"/>, <see cref="IRenderHost.CurrentRenderSize"/>,
    ///         and <see cref="IRenderHost.IsOverlayVisible"/> to populate a settings dialog.</item>
    ///   <item>Show the settings dialog using the <c>parent</c> window. Return early if cancelled.</item>
    ///   <item>Loop over frames, calling <see cref="IRenderHost.RenderFrameAsync"/> from
    ///         a background task, reporting progress and honouring the cancellation token.</item>
    /// </list>
    /// The host guarantees that UI input is blocked and the frame index is restored after
    /// <see cref="ExportAsync"/> returns, so the plugin does not need to manage those concerns.
    /// </para>
    /// </remarks>
    public interface IRenderExportPlugin
    {
        /// <summary>Label shown in the "Export as…" submenu, e.g. "MP4 (H.264)…"</summary>
        string Label { get; }

        /// <summary>Tooltip shown next to the menu item.</summary>
        string Hint { get; }

        /// <summary>File type name for the Save File dialog, e.g. "MP4 Video"</summary>
        string FileTypeName { get; }

        /// <summary>File glob for the Save File dialog, e.g. "*.mp4"</summary>
        string FilePattern { get; }

        /// <summary>
        /// When <c>true</c>, the menu item is hidden unless
        /// <see cref="MxPlot.Core.IMatrixData.FrameCount"/> &gt; 1.
        /// Default: <c>false</c> (shown for both single-frame and stack data).
        /// </summary>
        bool RequiresStack => false;

        /// <summary>
        /// Performs the export.
        /// </summary>
        /// <param name="path">Destination file path chosen by the user.</param>
        /// <param name="parent">
        /// The parent window. Use this to show a plugin-specific settings dialog
        /// at the start of the export. Return without writing if the user cancels the dialog.
        /// </param>
        /// <param name="host">
        /// Provides access to the matrix data and rendering capabilities.
        /// Use <see cref="IRenderHost.RenderFrameAsync"/> to obtain BGRA32 frame bytes.
        /// <see cref="IRenderHost.RenderFrameAsync"/> is thread-safe and may be called
        /// directly from a background <see cref="System.Threading.Tasks.Task"/>.
        /// </param>
        /// <param name="progress">
        /// Reports the number of frames completed (0 to <see cref="MxPlot.Core.IMatrixData.FrameCount"/>).
        /// The host uses this to drive the progress bar overlay.
        /// </param>
        /// <param name="cancellationToken">
        /// Token that the host raises when the user cancels the export.
        /// Check it between frames and throw <see cref="OperationCanceledException"/> promptly.
        /// </param>
        Task ExportAsync(
            string path,
            Window parent,
            IRenderHost host,
            IProgress<int> progress,
            CancellationToken cancellationToken);
    }
}
