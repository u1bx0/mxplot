using Avalonia.Media;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Serializes and deserializes overlay objects to/from JSON.
    /// System overlays (<see cref="ISystemOverlay"/>) are excluded from serialization.
    /// <para>
    /// All coordinates are stored in overlay world space (bitmap pixel-index, left-top origin,
    /// Y-down). <b>No FlipY is applied</b> during serialization or deserialization — the internal
    /// representation is preserved as-is. The Y-axis flip to data-index convention (left-bottom
    /// origin) is only applied at user-facing boundaries (dialogs, status bars, etc.).
    /// </para>
    /// </summary>
    internal static class OverlaySerializer
    {
        private static readonly JsonSerializerOptions _writeOptions = new() { WriteIndented = true };

        /// <summary>Serializes a collection of overlay objects to a JSON string.</summary>
        public static string Serialize(IEnumerable<OverlayObjectBase> objects)
        {
            var arr = new JsonArray();
            foreach (var obj in objects)
            {
                if (obj is ISystemOverlay) continue;
                var node = SerializeOne(obj);
                if (node != null) arr.Add(node);
            }
            return arr.ToJsonString(_writeOptions);
        }

        /// <summary>Deserializes a JSON string to a list of overlay objects.</summary>
        public static List<OverlayObjectBase> Deserialize(string json)
        {
            var result = new List<OverlayObjectBase>();
            try
            {
                var arr = JsonNode.Parse(json)?.AsArray();
                if (arr == null) return result;
                foreach (var node in arr)
                {
                    if (node is not JsonObject obj) continue;
                    var overlay = DeserializeOne(obj);
                    if (overlay != null) result.Add(overlay);
                }
            }
            catch (JsonException) { }
            return result;
        }

        // ── Single-object serialization ───────────────────────────────────

        private static JsonObject? SerializeOne(OverlayObjectBase obj)
        {
            string? type = obj switch
            {
                TextObject => "text",
                RectObject => "rect",
                OvalObject => "oval",
                TargetingObject => "target",
                LineObject => "line",
                _ => null,
            };
            if (type == null) return null;

            var json = new JsonObject { ["type"] = type };

            // Common pen/appearance
            json["penColor"] = FormatColor(obj.PenColor);
            json["penWidth"] = obj.PenWidth;
            json["penDash"] = obj.PenDash.ToString().ToLowerInvariant();
            json["snapMode"] = obj.SnapMode.ToString();
            if (obj.IsScaledPenWidth) json["isScaledPenWidth"] = true;

            // Type-specific geometry
            switch (obj)
            {
                case LineObject l:
                    json["p1x"] = l.P1.X;
                    json["p1y"] = l.P1.Y;
                    json["p2x"] = l.P2.X;
                    json["p2y"] = l.P2.Y;
                    break;

                case TextObject t:
                    WriteBBox(json, t);
                    json["text"] = t.Text;
                    json["fontSize"] = t.FontSize;
                    json["fontFamily"] = t.FontFamily;
                    json["bgColor"] = FormatColor(t.BackgroundColor);
                    json["showBg"] = t.ShowBackground;
                    json["showBorder"] = t.ShowBorder;
                    json["scaleFont"] = t.ScaleFontWithZoom;
                    break;

                case BoundingBoxBase bb:
                    WriteBBox(json, bb);
                    break;
            }

            if (obj is IAnalyzableOverlay ana && ana.IsValueRangeRoi)
                json["isValueRangeRoi"] = true;

            return json;
        }

        private static void WriteBBox(JsonObject json, BoundingBoxBase bb)
        {
            json["x"] = bb.X;
            json["y"] = bb.Y;
            json["w"] = bb.Width;
            json["h"] = bb.Height;
            if (bb.IsFilled)
            {
                json["isFilled"] = true;
                json["fillColor"] = FormatColor(bb.FillColor);
            }
        }

        // ── Single-object deserialization ─────────────────────────────────

        private static OverlayObjectBase? DeserializeOne(JsonObject json)
        {
            string? type = Str(json, "type");

            OverlayObjectBase? obj = type switch
            {
                "line" => new LineObject(
                    Dbl(json, "p1x"), Dbl(json, "p1y"),
                    Dbl(json, "p2x"), Dbl(json, "p2y")),
                "rect" => new RectObject(),
                "oval" => new OvalObject(),
                "target" => new TargetingObject(),
                "text" => new TextObject(),
                _ => null,
            };
            if (obj == null) return null;

            // Common pen/appearance
            if (Str(json, "penColor") is { } pc) obj.PenColor = ParseColor(pc);
            if (json["penWidth"] != null) obj.PenWidth = Dbl(json, "penWidth");
            if (Str(json, "penDash") is { } pd)
                obj.PenDash = Enum.TryParse<OverlayDashStyle>(pd, true, out var ds) ? ds : OverlayDashStyle.Solid;
            if (Str(json, "snapMode") is { } sm)
                obj.SnapMode = Enum.TryParse<PixelSnapMode>(sm, true, out var sn) ? sn : PixelSnapMode.None;
            if (json["isScaledPenWidth"] != null) obj.IsScaledPenWidth = Bool(json, "isScaledPenWidth");

            // BBox geometry
            if (obj is BoundingBoxBase bb)
            {
                bb.X = Dbl(json, "x");
                bb.Y = Dbl(json, "y");
                bb.Width = Dbl(json, "w");
                bb.Height = Dbl(json, "h");
                if (json["isFilled"] != null) bb.IsFilled = Bool(json, "isFilled");
                if (Str(json, "fillColor") is { } fc) bb.FillColor = ParseColor(fc);
            }

            // Analyzable flags
            if (obj is IAnalyzableOverlay ana)
            {
                if (json["isValueRangeRoi"] != null) ana.IsValueRangeRoi = Bool(json, "isValueRangeRoi");
            }

            // Text-specific
            if (obj is TextObject t)
            {
                if (Str(json, "text") is { } tx) t.Text = tx;
                if (json["fontSize"] != null) t.FontSize = Dbl(json, "fontSize");
                if (Str(json, "fontFamily") is { } ff) t.FontFamily = ff;
                if (Str(json, "bgColor") is { } bg) t.BackgroundColor = ParseColor(bg);
                if (json["showBg"] != null) t.ShowBackground = Bool(json, "showBg");
                if (json["showBorder"] != null) t.ShowBorder = Bool(json, "showBorder");
                if (json["scaleFont"] != null) t.ScaleFontWithZoom = Bool(json, "scaleFont");
            }

            return obj;
        }

        // ── JSON helpers ──────────────────────────────────────────────────

        private static double Dbl(JsonObject o, string key) => o[key]?.GetValue<double>() ?? 0;
        private static bool Bool(JsonObject o, string key) => o[key]?.GetValue<bool>() ?? false;
        private static string? Str(JsonObject o, string key) => o[key]?.GetValue<string>();

        // ── Color formatting ──────────────────────────────────────────────

        private static string FormatColor(Color c) =>
            c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        private static Color ParseColor(string s)
        {
            try
            {
                if (s.Length == 7 && s[0] == '#')
                    return Color.FromArgb(255,
                        Convert.ToByte(s[1..3], 16),
                        Convert.ToByte(s[3..5], 16),
                        Convert.ToByte(s[5..7], 16));
                if (s.Length == 9 && s[0] == '#')
                    return Color.FromArgb(
                        Convert.ToByte(s[1..3], 16),
                        Convert.ToByte(s[3..5], 16),
                        Convert.ToByte(s[5..7], 16),
                        Convert.ToByte(s[7..9], 16));
            }
            catch (FormatException) { }
            return Colors.Black;
        }
    }
}
