namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Defines the reserved key namespace used by MatrixPlotter for system-level
    /// view-settings stored inside <see cref="MxPlot.Core.IMatrixData.Metadata"/>.
    /// Keys with the <see cref="Prefix"/> are hidden from the Metadata UI and
    /// cannot be created or deleted by the user, unless explicitly listed in
    /// <see cref="VisibleSystemKeys"/>.
    /// </summary>
    internal static class PlotterConfigKeys
    {
        /// <summary>The namespace prefix shared by all MatrixPlotter system keys.</summary>
        public const string Prefix = "mxplot.";

        /// <summary>
        /// System-managed metadata keys that should be visible (read-only) in the Metadata tab.
        /// Key = internal metadata key, Value = display name shown in the UI.
        /// </summary>
        public static readonly System.Collections.Generic.Dictionary<string, string> VisibleSystemKeys = new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["mxplot.data.history"] = "History",
        };

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="key"/> belongs to
        /// the MatrixPlotter reserved namespace and must not be exposed in the UI.
        /// Visible system keys (listed in <see cref="VisibleSystemKeys"/>) are excluded.
        /// </summary>
        public static bool IsReserved(string key) =>
            key.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase)
            && !VisibleSystemKeys.ContainsKey(key);
    }

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
