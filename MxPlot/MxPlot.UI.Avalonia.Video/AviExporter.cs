using Avalonia;
using Avalonia.Controls;
using MxPlot.UI.Avalonia.Plugins;
using SharpAvi.Output;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Video
{
    public class AviExporter : IRenderExportPlugin
    {
        public string Label => "AVI\u2026";

        public string Hint => "Exports the frames as an AVI video";

        public string FileTypeName => "AVI Video";

        public string FilePattern => "*.avi";

        public bool RequiresStack => true;

        public async Task ExportAsync(string path, Window parent, IRenderHost host, IProgress<int> progress, CancellationToken cancellationToken)
        {
            var dialog = new AviExportDialog(host, path);
            dialog.ShowInTaskbar = false;
            await dialog.ShowDialog(parent);
            if (!dialog.Confirmed) return;

            cancellationToken.ThrowIfCancellationRequested();

            var axis = dialog.SelectedAxis;
            double intervalMs = dialog.IntervalMs;
            int width = dialog.OutputWidth;
            int height = dialog.OutputHeight;
            bool withOverlay = dialog.WithOverlay;
            var size = new Size(width, height);

            decimal fps = (decimal)Math.Round(1000.0 / intervalMs, 4);
            if (fps <= 0) fps = 10;

            int frameCount = axis.Count;
            var dims = host.Data.Dimensions;

            // The write loop runs inside Task.Run so that SharpAVI's synchronous
            // WriteFrame() (which calls task.Wait() internally) never blocks the UI thread.
            // host.RenderFrameAsync() is thread-safe and handles its own UI-thread marshalling.
            //
            // Note: SharpAVI does NOT handle AVI DIB row padding.  The caller is
            // responsible for padding each BGR24 row to a 4-byte boundary, which
            // ConvertBgraToBgr24Padded() does.  Without this, widths not divisible
            // by 4 produce 0xC00D36B1 (MF_E_UNSUPPORTED_FORMAT) in Windows Media
            // Foundation (Media Player, PowerPoint, etc.).
            var writer = new AviWriter(path) { FramesPerSecond = fps, EmitIndex1 = true };
            var stream = writer.AddVideoStream();
            stream.Width = width;
            stream.Height = height;
            stream.BitsPerPixel = SharpAvi.BitsPerPixel.Bpp24;

            try
            {
                await Task.Run(async () =>
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int globalFrame = dims.GetFrameIndexFor(axis, i);
                        byte[] bgra = await host.RenderFrameAsync(globalFrame, size, withOverlay);

                        if (bgra.Length == 0)
                            throw new InvalidOperationException($"Frame {i + 1}/{frameCount}: render returned no data.");

                        byte[] bgr = ConvertBgraToBgr24Padded(bgra, width, height);
                        stream.WriteFrame(true, bgr, 0, bgr.Length);
                        progress?.Report((i + 1) * 100 / frameCount);
                    }

                    writer.Close();
                }, cancellationToken);
            }
            catch
            {
                // writer.Close() may have already been called inside Task.Run on success path;
                // AviWriter.Close() is idempotent (guarded by isClosed flag) so calling it
                // again on the error/cancel path is safe and ensures the file handle is released.
                try { writer.Close(); } catch { /* ignore secondary close errors */ }
                TryDeleteFile(path);
                throw;
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

        private static byte[] ConvertBgraToBgr24Padded(byte[] bgra, int width, int height)
        {
            // AVI DIB rows must be padded to a 4-byte boundary (BMP/DIB spec).
            // SharpAVI does not handle this; widths where (width * 3) % 4 != 0
            // produce 0xC00D36B1 (MF_E_UNSUPPORTED_FORMAT) in Windows Media Foundation.
            int rowStride = (width * 3 + 3) & ~3;
            var bgr = new byte[rowStride * height];
            int srcRowStride = bgra.Length / height;
            // AVI DIB frames are stored bottom-up.
            for (int row = 0; row < height; row++)
            {
                int srcRow = height - 1 - row;
                for (int col = 0; col < width; col++)
                {
                    int src = srcRow * srcRowStride + col * 4;
                    int dst = row * rowStride + col * 3;
                    bgr[dst] = bgra[src];
                    bgr[dst + 1] = bgra[src + 1];
                    bgr[dst + 2] = bgra[src + 2];
                }
            }
            return bgr;
        }
    }
}
