using SharpAvi.Output;
using System;

namespace MxPlot.UI.Avalonia.Video
{
    /// <summary>
    /// Writes frames into an uncompressed AVI (BGR24 DIB) container via SharpAvi.
    /// </summary>
    public sealed class AviFrameWriter : IVideoFrameWriter
    {
        private readonly AviWriter _writer;
        private readonly IAviVideoStream _stream;
        private bool _closed;

        public AviFrameWriter(string path, int width, int height, decimal fps)
        {
            _writer = new AviWriter(path) { FramesPerSecond = fps, EmitIndex1 = true };
            _stream = _writer.AddVideoStream();
            _stream.Width = width;
            _stream.Height = height;
            _stream.BitsPerPixel = SharpAvi.BitsPerPixel.Bpp24;
        }

        public void WriteFrame(byte[] bgra, int width, int height)
        {
            byte[] bgr = ConvertBgraToBgr24Padded(bgra, width, height);
            _stream.WriteFrame(true, bgr, 0, bgr.Length);
        }

        public void Finish()
        {
            _writer.Close();
            _closed = true;
        }

        public void Dispose()
        {
            // AviWriter.Close() is idempotent (guarded by its own isClosed flag), so calling it again
            // here on the error/cancel path (where Finish() never ran) is safe and ensures the file
            // handle is released either way.
            if (!_closed) _writer.Close();
        }

        private static byte[] ConvertBgraToBgr24Padded(byte[] bgra, int width, int height)
        {
            // AVI DIB rows must be padded to a 4-byte boundary (BMP/DIB spec).
            // SharpAVI does not handle this; widths where (width * 3) % 4 != 0
            // produce 0xC00D36B1 (MF_E_UNSUPPORTED_FORMAT) in Windows Media Foundation.
            int rowStride = (width * 3 + 3) & ~3;
            var bgr = new byte[rowStride * height];
            int srcRowStride = bgra.Length / height;
            // AVI DIB frames are stored bottom-up, unlike RenderFrameAsync's top-down source.
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
