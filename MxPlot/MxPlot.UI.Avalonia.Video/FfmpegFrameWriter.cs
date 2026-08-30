using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MxPlot.UI.Avalonia.Video
{
    /// <summary>
    /// Writes frames into an H.264 MP4 by piping raw BGR24 into an external <c>ffmpeg</c>
    /// subprocess, which does the actual encoding and container muxing -- no video codec ships
    /// inside this library. Requires <c>ffmpeg</c> to be installed and resolvable on PATH; check
    /// <see cref="Mp4Exporter.IsFfmpegAvailable"/> before offering this format at all.
    /// </summary>
    public sealed class FfmpegFrameWriter : IVideoFrameWriter
    {
        private readonly Process _process;
        private readonly StringBuilder _stderr = new();
        private readonly object _stderrLock = new();
        private bool _disposed;

        public FfmpegFrameWriter(string path, int width, int height, decimal fps)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add("rawvideo");
            psi.ArgumentList.Add("-pixel_format");
            psi.ArgumentList.Add("bgr24");
            psi.ArgumentList.Add("-video_size");
            psi.ArgumentList.Add($"{width}x{height}");
            psi.ArgumentList.Add("-framerate");
            // Invariant culture: this is a command-line argument, not user-facing text -- a
            // comma-decimal locale (most of non-US Windows, and macOS under many regions) would
            // otherwise split "23,976" into two ffmpeg thinks are separate arguments.
            psi.ArgumentList.Add(fps.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-");
            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-pix_fmt");
            psi.ArgumentList.Add("yuv420p");
            psi.ArgumentList.Add(path);

            _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            // Drained on a background thread, not just at the end: ffmpeg writes progress to
            // stderr continuously, and the OS pipe buffer is small enough that leaving it unread
            // while we are still writing frames to stdin can deadlock both sides.
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lock (_stderrLock) _stderr.AppendLine(e.Data);
            };
            _process.BeginErrorReadLine();
        }

        public void WriteFrame(byte[] bgra, int width, int height)
        {
            byte[] bgr = ConvertBgraToBgr24(bgra, width, height);
            _process.StandardInput.BaseStream.Write(bgr, 0, bgr.Length);
        }

        public void Finish()
        {
            _process.StandardInput.BaseStream.Flush();
            _process.StandardInput.Close();
            _process.WaitForExit();

            if (_process.ExitCode != 0)
            {
                string stderr;
                lock (_stderrLock) stderr = _stderr.ToString();
                throw new InvalidOperationException($"ffmpeg exited with code {_process.ExitCode}.\n{stderr}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    // Finish() never ran (error/cancel path) -- ffmpeg may still be waiting on more
                    // stdin frames. Close it and give ffmpeg a moment to exit on its own (it will,
                    // once stdin closes) before forcing it, so the partial output file isn't left
                    // locked by a lingering process.
                    try { _process.StandardInput.Close(); } catch { /* already closed/broken pipe */ }
                    if (!_process.WaitForExit(2000))
                        _process.Kill(entireProcessTree: true);
                }
            }
            catch { /* best-effort cleanup */ }
            finally
            {
                _process.Dispose();
            }
        }

        private static byte[] ConvertBgraToBgr24(byte[] bgra, int width, int height)
        {
            // No row flip needed here (unlike AVI's DIB rows): both RenderFrameAsync's source and
            // ffmpeg's rawvideo demuxer use top-down row order by convention.
            int srcStride = bgra.Length / height;
            var bgr = new byte[width * height * 3];
            for (int row = 0; row < height; row++)
            {
                int srcRowStart = row * srcStride;
                int dstRowStart = row * width * 3;
                for (int col = 0; col < width; col++)
                {
                    int src = srcRowStart + col * 4;
                    int dst = dstRowStart + col * 3;
                    bgr[dst] = bgra[src];
                    bgr[dst + 1] = bgra[src + 1];
                    bgr[dst + 2] = bgra[src + 2];
                }
            }
            return bgr;
        }
    }
}
