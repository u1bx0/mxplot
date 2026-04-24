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
                var sb = new StringBuilder(trimmed.Length);
                var settings = new XmlWriterSettings { Indent = true, IndentChars = "  " };
                using var reader = XmlReader.Create(new StringReader(trimmed));
                using var writer = XmlWriter.Create(sb, settings);
                writer.WriteNode(reader, defattr: true);
                writer.Flush();
                return sb.ToString();
            }
            catch { return original; }
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
                using var doc = JsonDocument.Parse(trimmed, _jsonDocOptions);
                return JsonSerializer.Serialize(doc.RootElement, _jsonIndented);
            }
            catch { return original; }
        }

        private static string MinifyJson(string trimmed, string original)
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed, _jsonDocOptions);
                return JsonSerializer.Serialize(doc.RootElement, _jsonCompact);
            }
            catch { return original; }
        }
    }
}
