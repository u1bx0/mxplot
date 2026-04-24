namespace MxPlot.Core.IO
{
    /// <summary>
    /// Provides FITS (Flexible Image Transport System) format reading and writing for MxPlot.
    /// Delegates all I/O to <see cref="FitsHandler"/>.
    /// </summary>
    /// <remarks>
    /// Supported element types: byte, short, ushort, int, long, float, double.
    /// Compression is not supported in FITS raw format; <see cref="CompressionInWrite"/>
    /// is present for interface consistency but has no effect.
    /// </remarks>
    public sealed class FitsFormat : IMatrixDataReader, IMatrixDataWriter, IProgressReportable, ICompressible
    {
        public string FormatName => "FITS";

        public IReadOnlyList<string> Extensions { get; } = [".fits", ".fit", ".fts"];

        public IProgress<int>? ProgressReporter { get; set; }

        private CancellationToken _ct;
        CancellationToken IMatrixDataReader.CancellationToken { get => _ct; set => _ct = value; }
        bool IMatrixDataReader.IsCancellable => true;

        /// <summary>
        /// FITS raw format does not support compression.
        /// This property is present for interface consistency; setting it to <c>true</c> has no effect.
        /// </summary>
        public bool CompressionInWrite { get; set; } = false;

        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            IMatrixData raw = Read(filePath);
            if (raw is MatrixData<T> typed) return typed;
            throw new InvalidOperationException(
                $"File contains '{raw.ValueTypeName}' data, but '{typeof(T).Name}' was requested.");
        }

        public IMatrixData Read(string path)
            => FitsHandler.Load(path, ProgressReporter, _ct);

        public void Write<T>(string filePath, MatrixData<T> data, IBackendAccessor accessor) where T : unmanaged
            => FitsHandler.Save(filePath, data, ProgressReporter, CancellationToken.None);
    }
}
