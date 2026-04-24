using Avalonia.Media;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Helpers
{
    /// <summary>
    /// Vector icon geometries for menus, replacing emoji characters that render
    /// as monochrome under Avalonia's Skia text pipeline.
    /// All paths use a 24×24 coordinate space (Material Design Icons, Apache 2.0)
    /// and scale automatically via <see cref="Avalonia.Controls.PathIcon"/>.
    /// </summary>
    internal static class MenuIcons
    {
        // ── File operations ───────────────────────────────────────────────────

        internal static readonly StreamGeometry Folder = StreamGeometry.Parse(
            "M10,4H4C2.89,4 2,4.89 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V8C22,6.89 21.1,6 20,6H12L10,4Z");

        internal static readonly StreamGeometry Save = StreamGeometry.Parse(
            "M15,9H5V5H15M12,19A3,3 0 0,1 9,16A3,3 0 0,1 12,13A3,3 0 0,1 15,16A3,3 0 0,1 12,19M17,3H5C3.89,3 3,3.9 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V7L17,3Z");

        internal static readonly StreamGeometry Image = StreamGeometry.Parse(
            "M8.5,13.5L11,16.5L14.5,12L19,18H5M21,19V5C21,3.89 20.1,3 19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19Z");

        // ── Edit operations ───────────────────────────────────────────────────

        internal static readonly StreamGeometry Edit = StreamGeometry.Parse(
            "M20.71,7.04C21.1,6.65 21.1,6 20.71,5.63L18.37,3.29C18,2.9 17.35,2.9 16.96,3.29L15.12,5.12L18.87,8.87M3,17.25V21H6.75L17.81,9.93L14.06,6.18L3,17.25Z");

        internal static readonly StreamGeometry Copy = StreamGeometry.Parse(
            "M19,21H8V7H19M19,5H8A2,2 0 0,0 6,7V21A2,2 0 0,0 8,23H19A2,2 0 0,0 21,21V7A2,2 0 0,0 19,5M16,1H4A2,2 0 0,0 2,3V17H4V3H16V1Z");

        internal static readonly StreamGeometry Paste = StreamGeometry.Parse(
            "M19,20H5V9H19M19,7H5A2,2 0 0,0 3,9V20A2,2 0 0,0 5,22H19A2,2 0 0,0 21,20V9A2,2 0 0,0 19,7M16,1H8V3H16V1M12,5A1,1 0 0,0 11,6A1,1 0 0,0 12,7A1,1 0 0,0 13,6A1,1 0 0,0 12,5Z");

        internal static readonly StreamGeometry Duplicate = StreamGeometry.Parse(
            "M4,8H2V20A2,2 0 0,0 4,22H16V20H4V8M20,2H8A2,2 0 0,0 6,4V16A2,2 0 0,0 8,18H20A2,2 0 0,0 22,16V4A2,2 0 0,0 20,2M20,16H8V4H20V16Z");

        internal static readonly StreamGeometry ConvertType = StreamGeometry.Parse(
            "M 3,15 H 9 V 9 H 15 V 4 L 21,10 L 15,16 V 11 H 11 V 17 H 3 Z");
        // ── Actions ───────────────────────────────────────────────────────────

        internal static readonly StreamGeometry Refresh = StreamGeometry.Parse(
            "M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z");

        internal static readonly StreamGeometry Info = StreamGeometry.Parse(
            "M13,9H11V7H13M13,17H11V11H13M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z");

        internal static readonly StreamGeometry Close = StreamGeometry.Parse(
            "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z");

        internal static readonly StreamGeometry Lightning = StreamGeometry.Parse(
            "M7,2V13H10V22L17,10H13L17,2H7Z");

        // ── Property / Analysis ───────────────────────────────────────────────

        internal static readonly StreamGeometry Ruler = StreamGeometry.Parse(
            "M1,7V9H3V7H1M1,11V13H3V11H1M1,15V17H3V15H1M3,3H1V5H3V3M7,3H5V5H7V3M11,3H9V5H11V3M13,3H15V5H13V3M1,19V21H3V19H1M5,21V19H7V21H5M9,21V19H11V21H9M13,21V19H15V21H13M21,3H17V5H21V7H23V5V3H21M21,11H23V9H21V11M21,15H23V13H21V15M17,21V19H19V21H17M21,21V19H23V21H21M21,7H23V5H21V7Z");

        internal static readonly StreamGeometry Palette = StreamGeometry.Parse(
            "M12,22A10,10 0 0,1 2,12A10,10 0 0,1 12,2C17.5,2 22,6 22,11A6,6 0 0,1 16,17H14.2C13.9,17 13.7,17.2 13.7,17.5C13.7,17.6 13.8,17.7 13.8,17.8C14.2,18.3 14.4,18.9 14.4,19.5C14.5,20.9 13.4,22 12,22M12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C12.3,20 12.5,19.8 12.5,19.5C12.5,19.3 12.4,19.2 12.4,19.1C12,18.6 11.8,18.1 11.8,17.5C11.8,16.1 12.9,15 14.3,15H16A4,4 0 0,0 20,11C20,7.1 16.4,4 12,4M6.5,10A1.5,1.5 0 0,1 8,11.5A1.5,1.5 0 0,1 6.5,13A1.5,1.5 0 0,1 5,11.5A1.5,1.5 0 0,1 6.5,10M9,6.5A1.5,1.5 0 0,1 10.5,8A1.5,1.5 0 0,1 9,9.5A1.5,1.5 0 0,1 7.5,8A1.5,1.5 0 0,1 9,6.5M15,6.5A1.5,1.5 0 0,1 16.5,8A1.5,1.5 0 0,1 15,9.5A1.5,1.5 0 0,1 13.5,8A1.5,1.5 0 0,1 15,6.5M18,10A1.5,1.5 0 0,1 19.5,11.5A1.5,1.5 0 0,1 18,13A1.5,1.5 0 0,1 16.5,11.5A1.5,1.5 0 0,1 18,10Z");

        internal static readonly StreamGeometry Magnify = StreamGeometry.Parse(
            "M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z");

        // ── Controls ──────────────────────────────────────────────────────────

        internal static readonly StreamGeometry Cube = StreamGeometry.Parse(
            "M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.79,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5M12,4.15L6.04,7.5L12,10.85L17.96,7.5L12,4.15M5,15.91L11,19.29V12.58L5,9.21V15.91M19,15.91V9.21L13,12.58V19.29L19,15.91Z");

        // ── Tabs ──────────────────────────────────────────────────────────────

        internal static readonly StreamGeometry Metadata = StreamGeometry.Parse(
            "M20,20H4A2,2 0 0,1 2,18V6A2,2 0 0,1 4,4H20A2,2 0 0,1 22,6V18A2,2 0 0,1 20,20M4,6V18H20V6H4M6,9H18V11H6V9M6,13H16V15H6V13Z");

        internal static readonly StreamGeometry Processing = StreamGeometry.Parse(
            "M12,8A4,4 0 0,1 16,12A4,4 0 0,1 12,16A4,4 0 0,1 8,12A4,4 0 0,1 12,8M12,10A2,2 0 0,0 10,12A2,2 0 0,0 12,14A2,2 0 0,0 14,12A2,2 0 0,0 12,10M10,22C9.75,22 9.54,21.82 9.5,21.58L9.13,18.93C8.5,18.68 7.96,18.34 7.44,17.94L4.95,18.95C4.73,19.03 4.46,18.95 4.34,18.73L2.34,15.27C2.21,15.05 2.27,14.78 2.46,14.63L4.57,12.97C4.53,12.65 4.5,12.33 4.5,12C4.5,11.67 4.53,11.34 4.57,11L2.46,9.37C2.27,9.22 2.21,8.95 2.34,8.73L4.34,5.27C4.46,5.05 4.73,4.96 4.95,5.05L7.44,6.05C7.96,5.66 8.5,5.32 9.13,5.07L9.5,2.42C9.54,2.18 9.75,2 10,2H14C14.25,2 14.46,2.18 14.5,2.42L14.87,5.07C15.5,5.32 16.04,5.66 16.56,6.05L19.05,5.05C19.27,4.96 19.54,5.05 19.66,5.27L21.66,8.73C21.78,8.95 21.73,9.22 21.54,9.37L19.43,11C19.47,11.34 19.5,11.67 19.5,12C19.5,12.33 19.47,12.65 19.43,12.97L21.54,14.63C21.73,14.78 21.78,15.05 21.66,15.27L19.66,18.73C19.54,18.95 19.27,19.03 19.05,18.95L16.56,17.94C16.04,18.34 15.5,18.68 14.87,18.93L14.5,21.58C14.46,21.82 14.25,22 14,22H10Z");

        internal static readonly StreamGeometry AutoFix = StreamGeometry.Parse(
            "M7.5,5.6L5,7L6.4,4.5L5,2L7.5,3.4L10,2L8.6,4.5L10,7L7.5,5.6M19.5,17.6L22,16L20.6,18.5L22,21L19.5,19.6L17,21L18.4,18.5L17,16L19.5,17.6M22,2L20.6,4.5L22,7L19.5,5.6L17,7L18.4,4.5L17,2L19.5,3.4L22,2M13.34,12.78L15.78,10.34L13.66,8.22L11.22,10.66L13.34,12.78M14.37,7.29L16.71,9.63C17.1,10 17.1,10.65 16.71,11.04L5.04,22.71C4.65,23.1 4,23.1 3.63,22.71L1.29,20.37C0.9,20 0.9,19.35 1.29,18.96L12.96,7.29C13.35,6.9 14,6.9 14.37,7.29Z");

        internal static readonly StreamGeometry Undo = StreamGeometry.Parse(
            "M12.5,8C9.85,8 7.45,9 5.6,10.6L2,7V16H11L7.38,12.38C8.69,11.17 10.5,10.5 12.5,10.5C16.04,10.5 19.05,12.84 20.1,16L22.47,15.22C21.08,11.03 17.15,8 12.5,8Z");

        // ── Context menu ──────────────────────────────────────────────────────

        internal static readonly StreamGeometry PushPin = StreamGeometry.Parse(
            "M16,12V4H17V2H7V4H8V12L6,14V16H11.2V22H12.8V16H18V14L16,12Z");

        internal static readonly StreamGeometry TrashCan = StreamGeometry.Parse(
            "M9,3V4H4V6H5V19A2,2 0 0,0 7,21H17A2,2 0 0,0 19,19V6H20V4H15V3H9M7,6H17V19H7V6M9,8V17H11V8H9M13,8V17H15V8H13Z");

        internal static readonly StreamGeometry Layers = StreamGeometry.Parse(
            "M12,16L19.36,10.27L21,9L12,2L3,9L4.63,10.27M12,18.54L4.62,12.81L3,14.07L12,21.07L21,14.07L19.37,12.8L12,18.54Z");

        internal static readonly StreamGeometry Plus = StreamGeometry.Parse(
            "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z");

        internal static readonly StreamGeometry SelectRect = StreamGeometry.Parse(
            "M3,7H5V5H7V3H5A2,2 0 0,0 3,5V7M21,5A2,2 0 0,0 19,3H17V5H19V7H21V5M7,21H5V19H3V21A2,2 0 0,0 5,23H7V21M19,21V19H21V21A2,2 0 0,0 19,23H17V21H19M9,5H11V3H9V5M11,21H9V23H11V21M15,5H17V3H15V5M15,21H17V23H15V21M3,11H5V9H3V11M21,9H19V11H21V9M3,15H5V13H3V15M21,13H19V15H21V13Z");

        internal static readonly StreamGeometry Eye = StreamGeometry.Parse(
            "M12,9A3,3 0 0,0 9,12A3,3 0 0,0 12,15A3,3 0 0,0 15,12A3,3 0 0,0 12,9M12,17A5,5 0 0,1 7,12A5,5 0 0,1 12,7A5,5 0 0,1 17,12A5,5 0 0,1 12,17M12,4.5C7,4.5 2.73,7.61 1,12C2.73,16.39 7,19.5 12,19.5C17,19.5 21.27,16.39 23,12C21.27,7.61 17,4.5 12,4.5Z");

        internal static readonly StreamGeometry EyeOff = StreamGeometry.Parse(
            "M11.83,9L15,12.16C15,12.11 15,12.05 15,12A3,3 0 0,0 12,9C11.94,9 11.89,9 11.83,9M7.53,9.8L9.08,11.35C9.03,11.56 9,11.77 9,12A3,3 0 0,0 12,15C12.22,15 12.44,14.97 12.65,14.92L14.2,16.47C13.53,16.8 12.79,17 12,17A5,5 0 0,1 7,12C7,11.21 7.2,10.47 7.53,9.8M2,4.27L4.28,6.55L4.73,7C3.08,8.3 1.78,10 1,12C2.73,16.39 7,19.5 12,19.5C13.55,19.5 15.03,19.13 16.38,18.5L16.81,18.92L19.73,21.85L21,20.54L3.27,2.82L2,4.27M12,7A5,5 0 0,1 17,12C17,12.64 16.87,13.26 16.64,13.82L19.57,16.75C21.07,15.5 22.27,13.86 23,12C21.27,7.61 17,4.5 12,4.5C10.6,4.5 9.26,4.75 8,5.2L10.17,7.35C10.74,7.13 11.35,7 12,7Z");

        // ── Plugin system ─────────────────────────────────────────────────────

        /// <summary>Puzzle-piece icon for the Plugins tab (Material Design "extension", Apache 2.0).</summary>
        internal static readonly StreamGeometry Plugin = StreamGeometry.Parse(
            "M20.5,11H19V7C19,5.9 18.1,5 17,5H13V3.5A2.5,2.5 0 0,0 10.5,1A2.5,2.5 0 0,0 8,3.5V5H4C2.9,5 2,5.9 2,7V10.8H3.5A2.7,2.7 0 0,1 6.2,13.5A2.7,2.7 0 0,1 3.5,16.2H2V20C2,21.1 2.9,22 4,22H7.8V20.5A2.7,2.7 0 0,1 10.5,17.8A2.7,2.7 0 0,1 13.2,20.5V22H17C18.1,22 19,21.1 19,20V16H20.5A2.5,2.5 0 0,0 23,13.5A2.5,2.5 0 0,0 20.5,11Z");

        /// <summary>4-pointed sparkle star for individual plugin menu entries.</summary>
        internal static readonly StreamGeometry Sparkle = StreamGeometry.Parse(
            "M12,1L14.2,9.8L23,12L14.2,14.2L12,23L9.8,14.2L1,12L9.8,9.8Z");

        /// <summary>Line-chart / show_chart icon for "Plot Profile" actions (Material Design, Apache 2.0).</summary>
        internal static readonly StreamGeometry LineChart = StreamGeometry.Parse(
            "M3.5,18.49L9.5,12.48L13.5,16.48L22,6.92L20.59,5.51L13.5,13.48L9.5,9.48L2,16.99L3.5,18.49Z");

        /// <summary>Lock icon for read-only metadata keys (Material Design "lock", Apache 2.0).</summary>
        internal static readonly StreamGeometry Lock = StreamGeometry.Parse(
            "M12,17A2,2 0 0,0 14,15C14,13.89 13.1,13 12,13A2,2 0 0,0 10,15A2,2 0 0,0 12,17M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V10C4,8.89 4.9,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z");

        /// <summary>Clock / history icon for processing history entries (Material Design "history", Apache 2.0).</summary>
        internal static readonly StreamGeometry History = StreamGeometry.Parse(
            "M13.5,8H12V13L16.28,15.54L17,14.33L13.5,12.25V8M13,3A9,9 0 0,0 4,12H1L4.96,16.03L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,20 10.5,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3Z");

        internal static readonly StreamGeometry Search = StreamGeometry.Parse(
             "M11.5,5A6.5,6.5 0 1,0 11.5,18A6.5,6.5 0 1,0 11.5,5ZM11.5,7A4.5,4.5 0 1,1 11.5,16A4.5,4.5 0 1,1 11.5,7ZM16.1,16.1L21.8,21.8A1,1 0 0,0 23.2,20.4L17.5,14.7Z");

        /// <summary>Dashed selection rectangle (ROI marquee) icon for "Use ROI for value range" action.
        /// Built from filled dash rectangles because PathIcon renders geometry as Fill, not Stroke.</summary>
        internal static readonly StreamGeometry Roi = StreamGeometry.Parse(
            // Top edge: two horizontal dashes
            "M2,2 L8,2 L8,4 L2,4 Z  M12,2 L20,2 L20,4 L12,4 Z " +
            // Right edge: two vertical dashes
            "M20,6 L22,6 L22,12 L20,12 Z  M20,16 L22,16 L22,22 L20,22 Z " +
            // Bottom edge: two horizontal dashes (right-to-left, same result)
            "M14,20 L20,20 L20,22 L14,22 Z  M2,20 L10,20 L10,22 L2,22 Z " +
            // Left edge: two vertical dashes
            "M2,14 L4,14 L4,20 L2,20 Z  M2,4 L4,4 L4,10 L2,10 Z");

        // ── Default icon colours (Material Design 300–400) ────────────────────

        private static readonly Dictionary<StreamGeometry, IBrush> _defaultBrushes = new()
        {
            [Folder]    = new SolidColorBrush(Color.Parse("#FFA726")),  // Orange 400
            [Save]      = new SolidColorBrush(Color.Parse("#42A5F5")),  // Blue 400
            [Image]     = new SolidColorBrush(Color.Parse("#66BB6A")),  // Green 400
            [Edit]      = new SolidColorBrush(Color.Parse("#FF7043")),  // Deep Orange 400
            [Copy]      = new SolidColorBrush(Color.Parse("#78909C")),  // Blue Grey 400
            [Paste]     = new SolidColorBrush(Color.Parse("#78909C")),  // Blue Grey 400
            [Duplicate] = new SolidColorBrush(Color.Parse("#4DB6AC")),  // Teal 300
            [ConvertType] = new SolidColorBrush(Color.Parse("#7E57C2")),  // Deep Purple 400
            [Refresh]   = new SolidColorBrush(Color.Parse("#9CCC65")),  // Light Green 400
            [Info]      = new SolidColorBrush(Color.Parse("#29B6F6")),  // Light Blue 400
            [Close]     = new SolidColorBrush(Color.Parse("#EF5350")),  // Red 400
            [Lightning] = new SolidColorBrush(Color.Parse("#FFCA28")),  // Amber 400
            [Ruler]     = new SolidColorBrush(Color.Parse("#AB47BC")),  // Purple 400
            [Palette]   = new SolidColorBrush(Color.Parse("#EC407A")),  // Pink 400
            [Magnify]   = new SolidColorBrush(Color.Parse("#5C6BC0")),  // Indigo 400
            [Cube]      = new SolidColorBrush(Color.Parse("#4FC3F7")),  // Light Blue 300
            [Metadata]  = new SolidColorBrush(Color.Parse("#26C6DA")),  // Cyan 400
            [Processing] = new SolidColorBrush(Color.Parse("#8D6E63")),  // Brown 400
            [AutoFix]    = new SolidColorBrush(Color.Parse("#CE93D8")),  // Purple 200
            [Undo]       = new SolidColorBrush(Color.Parse("#FF8A65")),  // Deep Orange 300
            [PushPin]    = new SolidColorBrush(Color.Parse("#FF7043")),  // Deep Orange 400
            [TrashCan]   = new SolidColorBrush(Color.Parse("#EF5350")),  // Red 400
            [Layers]     = new SolidColorBrush(Color.Parse("#42A5F5")),  // Blue 400
            [Plus]       = new SolidColorBrush(Color.Parse("#66BB6A")),  // Green 400
            [SelectRect] = new SolidColorBrush(Color.Parse("#AB47BC")),  // Purple 400
            [Eye]        = new SolidColorBrush(Color.Parse("#26C6DA")),  // Cyan 400
            [EyeOff]     = new SolidColorBrush(Color.Parse("#78909C")),  // Blue Grey 400
            [Plugin]     = new SolidColorBrush(Color.Parse("#26A69A")),  // Teal 400
            [Sparkle]    = new SolidColorBrush(Color.Parse("#FFCA28")),  // Amber 400
            [LineChart]  = new SolidColorBrush(Color.Parse("#42A5F5")),  // Blue 400
            [Lock]       = new SolidColorBrush(Color.Parse("#FFB74D")),  // Orange 300
            [History]    = new SolidColorBrush(Color.Parse("#7E57C2")),  // Deep Purple 400
            [Search]     = new SolidColorBrush(Color.Parse("#5C6BC0")),  // Indigo 400
        };

        /// <summary>Returns the default colour brush for the given icon geometry, or <c>null</c> if unknown.</summary>
        internal static IBrush? DefaultBrush(Geometry? icon) =>
            icon is StreamGeometry sg && _defaultBrushes.TryGetValue(sg, out var b) ? b : null;
    }
}
