using Avalonia.Controls;
using MxPlot.UI.Avalonia.Plugins;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Video
{
    /// <summary>
    /// Base <see cref="IRenderExportPlugin"/> for frame-sequence video export: shows
    /// <see cref="VideoExportDialog"/> to collect the animation axis, interval/fps, output size, and
    /// overlay setting, then drives the shared render-and-write loop against whatever
    /// <see cref="IVideoFrameWriter"/> <see cref="CreateWriter"/> returns.
    /// </summary>
    /// <remarks>
    /// Implement a new video export format by subclassing this and providing
    /// <see cref="CreateWriter"/> plus the plain <see cref="IRenderExportPlugin"/> identity
    /// properties (<see cref="Label"/>, <see cref="Hint"/>, <see cref="FileTypeName"/>,
    /// <see cref="FilePattern"/>) -- the dialog and frame loop, including progress reporting and
    /// cancellation, come for free. See <see cref="AviExporter"/> and its <see cref="AviFrameWriter"/>
    /// for a minimal reference implementation.
    /// </remarks>
    public abstract class VideoExporterBase : IRenderExportPlugin
    {
        public abstract string Label { get; }
        public abstract string Hint { get; }
        public abstract string FileTypeName { get; }
        public abstract string FilePattern { get; }
        public virtual bool RequiresStack => true;

        /// <summary>
        /// Short format name shown in the settings dialog's title, e.g. <c>"AVI"</c> or <c>"MP4"</c>.
        /// </summary>
        protected abstract string FormatLabel { get; }

        /// <summary>
        /// Whether the dialog's live size estimate (computed from raw uncompressed frame size) is
        /// meaningful for this format -- see <see cref="VideoExportDialog"/>'s constructor doc.
        /// Default <see langword="true"/> (uncompressed); override to <see langword="false"/> for an
        /// encoder-compressed format.
        /// </summary>
        protected virtual bool SizeEstimateIsExact => true;

        /// <summary>
        /// Creates the writer that will receive every frame for this export, already opened against
        /// <paramref name="path"/>. Called once, after the user confirms the settings dialog, before
        /// the frame loop starts.
        /// </summary>
        protected abstract IVideoFrameWriter CreateWriter(string path, int width, int height, decimal fps);

        public async Task ExportAsync(string path, Window parent, IRenderHost host, IProgress<int> progress, CancellationToken cancellationToken)
        {
            var dialog = new VideoExportDialog(host, path, FormatLabel, SizeEstimateIsExact);
            dialog.ShowInTaskbar = false;
            await dialog.ShowDialog(parent);
            if (!dialog.Confirmed) return;

            cancellationToken.ThrowIfCancellationRequested();

            var axis = dialog.SelectedAxis;
            double intervalMs = dialog.IntervalMs;
            int width = dialog.OutputWidth;
            int height = dialog.OutputHeight;
            bool withOverlay = dialog.WithOverlay;
            var size = new global::Avalonia.Size(width, height);

            decimal fps = (decimal)Math.Round(1000.0 / intervalMs, 4);
            if (fps <= 0) fps = 10;

            int frameCount = axis.Count;
            var dims = host.Data.Dimensions;

            var writer = CreateWriter(path, width, height, fps);

            try
            {
                // The write loop runs inside Task.Run so that a writer whose WriteFrame blocks
                // synchronously (SharpAvi's AviWriter, or a Process.StandardInput.Write to ffmpeg)
                // never blocks the UI thread. host.RenderFrameAsync() is thread-safe and handles its
                // own UI-thread marshalling.
                await Task.Run(async () =>
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int globalFrame = dims.GetFrameIndexFor(axis, i);
                        byte[] bgra = await host.RenderFrameAsync(globalFrame, size, withOverlay);

                        if (bgra.Length == 0)
                            throw new InvalidOperationException($"Frame {i + 1}/{frameCount}: render returned no data.");

                        writer.WriteFrame(bgra, width, height);
                        progress?.Report((i + 1) * 100 / frameCount);
                    }

                    writer.Finish();
                }, cancellationToken);
            }
            catch
            {
                TryDeleteFile(path);
                throw;
            }
            finally
            {
                // Dispose is safe to call whether Finish() ran (success) or not (error/cancel) -- an
                // IVideoFrameWriter must treat it as "release resources regardless of outcome", the
                // same contract AviWriter.Close()'s existing idempotency already relied on.
                writer.Dispose();
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { /* best-effort cleanup */ }
        }
    }
}
