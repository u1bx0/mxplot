namespace MxPlot.UI.Avalonia.Metadata
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
}
