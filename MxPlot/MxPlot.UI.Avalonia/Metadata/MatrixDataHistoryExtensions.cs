using MxPlot.Core;
using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxPlot.UI.Avalonia.Metadata
{
    /// <summary>
    /// Records a processing history on an <see cref="IMatrixData"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The history is a JSON array stored in <see cref="IMatrixData.Metadata"/> under the system key
    /// <c>mxplot.data.history</c>. Each element is an object with at least <c>op</c> and <c>at</c>:
    /// </para>
    /// <code>
    /// { "op": "Crop", "at": "2026-04-12T15:30:00+09:00", "from": "Sample", "detail": "X=10 Y=20 W=100 H=100" }
    /// </code>
    /// <para>
    /// The key lives in the <c>mxplot.</c> namespace, so MatrixPlotter lists it read-only as "History"
    /// in the Metadata tab and the user cannot overwrite it there. The array travels with the
    /// metadata of the data it is written to, including
    /// <see cref="MatrixData.CopyPropertiesFrom"/> and saving to a file.
    /// </para>
    /// </remarks>
    public static class MatrixDataHistoryExtensions
    {
        internal const string HistoryMetaKey = "mxplot.data.history";

        /// <summary>
        /// Appends one entry to the processing history of <paramref name="data"/>.
        /// An existing history (for example one inherited from the source through
        /// <see cref="MatrixData.CopyPropertiesFrom"/>) is kept and the entry is added at
        /// its end; an unreadable history is replaced by a new one.
        /// </summary>
        /// <param name="data">The data to annotate, typically the result of a processing step.</param>
        /// <param name="operation">Short operation name, e.g. "Crop" or "Median 3x3".</param>
        /// <param name="from">
        /// Where the data came from: the source window title or file name. <c>null</c> for
        /// newly created data.
        /// </param>
        /// <param name="detail">Human-readable parameter summary; <c>null</c> when there are no parameters.</param>
        public static void AppendHistory(this IMatrixData data, string operation, string? from, string? detail = null)
        {
            ArgumentNullException.ThrowIfNull(data);
            ArgumentNullException.ThrowIfNull(operation);

            JsonArray history;
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

            data.Metadata[HistoryMetaKey] = history.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
    }
}
