namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Sentinel source-path constants for <see cref="MatrixPlotter"/> windows
    /// that are not backed by a real file.
    /// <para/>
    /// The leading colon is intentionally chosen: it is an invalid character
    /// on Windows file paths, so any attempt to use these as a real path
    /// fails loudly.
    /// </summary>
    internal static class MatrixDataSource
    {
        /// <summary>Data was pasted from the clipboard.</summary>
        public const string Clipboard = ":Clipboard";

        /// <summary>Data was generated programmatically (projection, profile, etc.).</summary>
        public const string Generated = ":Generated";

        /// <summary>Data is an in-memory duplicate of another window.</summary>
        public const string Duplicate = ":Duplicate";
    }
}
