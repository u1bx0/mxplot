using System;

namespace MxPlot.UI.Avalonia.Video
{
    /// <summary>
    /// Consumes one rendered frame at a time and writes it to a specific video file format.
    /// Implement this to add a new video export format that reuses <see cref="VideoExporterBase"/>'s
    /// shared settings dialog and frame-rendering loop instead of reimplementing
    /// <see cref="MxPlot.UI.Avalonia.Plugins.IRenderExportPlugin"/> from scratch.
    /// </summary>
    /// <remarks>
    /// Frames are always handed to <see cref="WriteFrame"/> as top-down BGRA32 (matching
    /// <see cref="MxPlot.UI.Avalonia.Plugins.IRenderHost.RenderFrameAsync"/>'s own output format) --
    /// any pixel-format conversion, row padding, or row order a particular container/codec needs
    /// (e.g. AVI's bottom-up, 4-byte-padded BGR24 DIB rows) is this writer's own concern, not the
    /// caller's.
    /// </remarks>
    public interface IVideoFrameWriter : IDisposable
    {
        /// <summary>
        /// Writes one frame. <paramref name="bgra"/> is top-down BGRA32, row-major, exactly
        /// <paramref name="width"/> × <paramref name="height"/> × 4 bytes long.
        /// </summary>
        void WriteFrame(byte[] bgra, int width, int height);

        /// <summary>
        /// Finalizes the output (closes the container, flushes an encoder subprocess, etc.) after
        /// every frame has been written. Called once, before <see cref="IDisposable.Dispose"/>, only
        /// on the success path -- an export that throws or is cancelled skips straight to Dispose so
        /// a writer can tell "finished normally" from "abandoned partway through" without a flag.
        /// </summary>
        void Finish();
    }
}
