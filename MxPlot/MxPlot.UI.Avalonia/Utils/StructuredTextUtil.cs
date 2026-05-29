using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;

namespace MxPlot.UI.Avalonia.Utils
{
    /// <summary>
    /// Format-agnostic pretty-print / minify helpers for structured text (XML, JSON).
    /// Each method auto-detects the format from the first non-whitespace character
    /// and returns the original string unchanged if the format is unrecognised or malformed.
    /// </summary>
    internal static class StructuredTextUtil
    {
        // ── public entry points ──────────────────────────────────────────────

        /// <summary>Pretty-prints XML or JSON; returns <paramref name="text"/> unchanged otherwise.</summary>
        internal static string TryFormat(string text)
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0) return text;
            return trimmed[0] switch
            {
                '<' when LooksLikeXml(trimmed) => FormatXml(trimmed, text),
                '{' or '[' => FormatJson(trimmed, text),
                _ => text,
            };
        }

        /// <summary>Minifies XML or JSON; returns <paramref name="text"/> unchanged otherwise.</summary>
        internal static string TryMinify(string text)
        {
            var trimmed = text.TrimStart();
            if (trimmed.Length == 0) return text;
            return trimmed[0] switch
            {
                '<' when LooksLikeXml(trimmed) => MinifyXml(trimmed, text),
                '{' or '[' => MinifyJson(trimmed, text),
                _ => text,
            };
        }

        // ── XML ──────────────────────────────────────────────────────────────

        private static bool LooksLikeXml(string trimmed)
            => trimmed.Length >= 2
            && trimmed[1] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '!' or '?';

        private static string FormatXml(string trimmed, string original)
        {
            try
            {
                Debug.WriteLine($"[StructuredTextUtil.FormatXml] Try to format XML...: {trimmed.Substring(0, Math.Min(128, trimmed.Length))}");
                var sb = new StringBuilder(trimmed.Length);
                var settings = new XmlWriterSettings { Indent = true, IndentChars = "  " };
                using var reader = XmlReader.Create(new StringReader(trimmed));
                using var writer = XmlWriter.Create(sb, settings);
                writer.WriteNode(reader, defattr: true);
                writer.Flush();
                Debug.WriteLine($"[StructuredTextUtil.FormatXml] Successfully formatted XML.");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StructuredTextUtil.FormatXml] FormatXml failed: {ex}");
                return original;
            }
        }

        private static string MinifyXml(string trimmed, string original)
        {
            try
            {
                var sb = new StringBuilder(trimmed.Length);
                var settings = new XmlWriterSettings { Indent = false, NewLineHandling = NewLineHandling.None };
                using var reader = XmlReader.Create(new StringReader(trimmed));
                using var writer = XmlWriter.Create(sb, settings);
                writer.WriteNode(reader, defattr: true);
                writer.Flush();
                return sb.ToString();
            }
            catch { return original; }
        }

        // ── JSON ─────────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions _jsonIndented  = new() { WriteIndented = true,  Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private static readonly JsonSerializerOptions _jsonCompact   = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        private static readonly JsonDocumentOptions   _jsonDocOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

        private static string FormatJson(string trimmed, string original)
        {
            try
            {
                // Upgrade legacy INF representations to standard Infinity strings to support System.Text.Json.
                var safeJson = trimmed
                    .Replace(":INF", ":\"Infinity\"")
                    .Replace(":-INF", ":\"-Infinity\"")
                    .Replace(":\"INF\"", ":\"Infinity\"")    // Catch already quoted legacy INF
                    .Replace(":\"-INF\"", ":\"-Infinity\"")  // Catch already quoted legacy -INF
                    .Replace(":NaN", ":\"NaN\"");
                trimmed = safeJson;
                Debug.WriteLine($"[StructuredTextUtil.FormatJson] Try to format JSON...: {trimmed.Substring(0, Math.Min(128, trimmed.Length))}");
                using var doc = JsonDocument.Parse(trimmed, _jsonDocOptions);
                Debug.WriteLine($"[StructuredTextUtil.FormatJson] Successfully formatted JSON.");
                return JsonSerializer.Serialize(doc.RootElement, _jsonIndented);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StructuredTextUtil.FormatJson] FormatJson failed: {ex}");
                return original;
            }
        }

        private static string MinifyJson(string trimmed, string original)
        {
            try
            {
                // Upgrade legacy INF representations to standard Infinity strings to support System.Text.Json.
                var safeJson = trimmed
                    .Replace(":INF", ":\"Infinity\"")
                    .Replace(":-INF", ":\"-Infinity\"")
                    .Replace(":\"INF\"", ":\"Infinity\"")    // Catch already quoted legacy INF
                    .Replace(":\"-INF\"", ":\"-Infinity\"")  // Catch already quoted legacy -INF
                    .Replace(":NaN", ":\"NaN\"");
                trimmed = safeJson;
                using var doc = JsonDocument.Parse(trimmed, _jsonDocOptions);
                return JsonSerializer.Serialize(doc.RootElement, _jsonCompact);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[StructuredText.MinifyJson] MinifyJson failed: {ex}");
                return original;
            }
        }
    }
}
