using System;
using System.Diagnostics;

namespace MxPlot.UI.Avalonia.Video
{
    /// <summary>
    /// Exports the current stack as an H.264 MP4 by shelling out to an external <c>ffmpeg</c>
    /// installation (see <see cref="FfmpegFrameWriter"/>) -- unlike <see cref="AviExporter"/>'s
    /// uncompressed AVI, this plays natively on both Windows and macOS (QuickTime included).
    /// </summary>
    /// <remarks>
    /// Only register this with <c>MatrixPlotterPluginRegistry.AddExportPlugin</c> when
    /// <see cref="IsFfmpegAvailable"/> returns <see langword="true"/> -- there is no bundled
    /// fallback encoder, so offering it unconditionally would just fail every export on a machine
    /// without ffmpeg on PATH.
    /// </remarks>
    public sealed class Mp4Exporter : VideoExporterBase
    {
        public override string Label => "MP4 (H.264)…";
        public override string Hint => "Exports the frames as an MP4 video (requires ffmpeg)";
        public override string FileTypeName => "MP4 Video";
        public override string FilePattern => "*.mp4";

        protected override string FormatLabel => "MP4";
        protected override bool SizeEstimateIsExact => false; // H.264-compressed: real size depends on content

        protected override IVideoFrameWriter CreateWriter(string path, int width, int height, decimal fps)
            => new FfmpegFrameWriter(path, width, height, fps);

        /// <summary>
        /// Whether <c>ffmpeg</c> is installed and resolvable on PATH, by actually attempting to
        /// launch <c>ffmpeg -version</c> rather than searching PATH by hand (simpler, and works the
        /// same way <see cref="Process.Start(ProcessStartInfo)"/> itself resolves the executable on
        /// every platform). Call once at startup to decide whether to register this exporter at all.
        /// </summary>
        public static bool IsFfmpegAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var process = Process.Start(psi);
                if (process == null) return false;
                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    return false;
                }
                return process.ExitCode == 0;
            }
            catch
            {
                // FileName not found, no permission to launch, etc. -- treat all of these as "not available".
                return false;
            }
        }
    }
}
