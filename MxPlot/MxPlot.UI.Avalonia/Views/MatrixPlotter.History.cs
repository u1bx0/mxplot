using MxPlot.Core;
using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Processing History ────────────────────────────────────────────────
        //
        // Each IMatrixData carries an optional processing history stored under
        // the system key "mxplot.data.history".  The value is a JSON array of
        // objects, each with at minimum:
        //
        //   { "op": "Crop", "detail": "X=10 Y=20 W=100 H=100", "at": "..." }
        //
        // Because the key lives in the "mxplot." namespace it is system-managed
        // and cannot be overwritten by the user.  PlotterConfigKeys.VisibleSystemKeys
        // maps it to the display name "History" so it appears in the Metadata tab
        // as a read-only entry.
        //
        // Call sites:  ApplyCropResult, Filter completion, Duplicate, etc.
        // Each call site appends one entry via AppendHistory().

        /// <summary>
        /// The internal metadata key for the processing history JSON array.
        /// Registered in <see cref="PlotterConfigKeys.VisibleSystemKeys"/> so it
        /// appears as "History" (read-only) in the Metadata tab.
        /// </summary>
        internal const string HistoryMetaKey = "mxplot.data.history";

        /// <summary>
        /// Appends a processing-history entry to the specified <paramref name="data"/>.
        /// If the data already contains a History array (e.g. inherited from a source
        /// via <c>CopyPropertiesFrom</c>), the new entry is appended to the end.
        /// </summary>
        /// <param name="data">The matrix data to annotate.</param>
        /// <param name="operation">Short operation name (e.g. "Crop", "Median 3×3", "Duplicate").</param>
        /// <param name="from">
        /// The source identity — typically the MatrixPlotter window title or file name
        /// that the data originated from. Pass <c>null</c> for newly-created data.
        /// </param>
        /// <param name="detail">
        /// Human-readable parameter summary. May be <c>null</c> for parameter-less operations.
        /// </param>
        internal static void AppendHistory(IMatrixData data, string operation, string? from, string? detail = null)
        {
            JsonArray history;

            // Parse existing history or start fresh
            if (data.Metadata.TryGetValue(HistoryMetaKey, out var existing)
                && !string.IsNullOrEmpty(existing))
            {
                try
                {
                    history = JsonNode.Parse(existing)?.AsArray() ?? [];
                }
                catch
                {
                    history = [];
                }
            }
            else
            {
                history = [];
            }

            // Build the new entry
            var entry = new JsonObject
            {
                ["op"] = operation,
                ["at"] = DateTime.Now.ToString("o"), // ISO 8601 round-trip
            };
            if (!string.IsNullOrEmpty(from))
                entry["from"] = from;
            if (!string.IsNullOrEmpty(detail))
                entry["detail"] = detail;

            history.Add(entry);

            var jsonStr = history.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            data.Metadata[HistoryMetaKey] = jsonStr;
        }
    }
}
