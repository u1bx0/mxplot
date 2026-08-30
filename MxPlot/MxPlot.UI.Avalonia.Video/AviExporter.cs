namespace MxPlot.UI.Avalonia.Video
{
    /// <summary>Exports the current stack as an uncompressed AVI (BGR24 DIB) video via SharpAvi.</summary>
    /// <remarks>
    /// Plays natively on Windows (Video for Windows / Media Foundation both handle uncompressed
    /// DIB AVI). macOS's QuickTime does not reliably support this legacy Windows-native format --
    /// use <see cref="Mp4Exporter"/> there, or transcode the AVI with e.g. ffmpeg.
    /// </remarks>
    public sealed class AviExporter : VideoExporterBase
    {
        public override string Label => "AVI…";
        public override string Hint => "Exports the frames as an AVI video";
        public override string FileTypeName => "AVI Video";
        public override string FilePattern => "*.avi";

        protected override string FormatLabel => "AVI";
        protected override bool SizeEstimateIsExact => true; // uncompressed BGR24: exact

        protected override IVideoFrameWriter CreateWriter(string path, int width, int height, decimal fps)
            => new AviFrameWriter(path, width, height, fps);
    }
}
