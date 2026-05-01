using Avalonia.Media;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// Describes a single entry in an overlay object's context menu.
    /// The host control (<c>MxView</c>) builds an Avalonia <c>MenuItem</c> from this descriptor.
    /// <para>
    /// Entries whose <see cref="Handler"/> is <c>null</c> are automatically hidden in the menu,
    /// allowing the host to control availability simply by assigning or clearing the handler.
    /// </para>
    /// Supports nested submenus via <see cref="Children"/> and radio-style checked state
    /// via <see cref="IsChecked"/>.
    /// </summary>
    public sealed class OverlayMenuEntry
    {
        /// <summary>Menu item label.</summary>
        public string Header { get; }

        /// <summary>Optional vector icon geometry displayed in the menu icon slot.</summary>
        public Geometry? Icon { get; }

        /// <summary>Optional tooltip text shown on hover.</summary>
        public string? Tooltip { get; }

        /// <summary>When true the item shows a radio/check indicator.</summary>
        public bool IsChecked { get; set; }

        /// <summary>
        /// The action executed when the user clicks this entry.
        /// <para>
        /// <c>null</c> means "not available": the entry is hidden in the built context menu.
        /// The host assigns a handler after the overlay object is added to the manager;
        /// clearing it on removal prevents dangling references.
        /// </para>
        /// </summary>
        public System.Action? Handler { get; set; }

        /// <summary>
        /// Returns <c>true</c> when this entry should appear in the context menu.
        /// For entries with <see cref="Children"/>, always visible (the submenu itself is the handler).
        /// For leaf entries, visible only when <see cref="Handler"/> is assigned.
        /// </summary>
        public bool IsVisible => Children != null || Handler != null;

        /// <summary>True for separator pseudo-entries.</summary>
        public bool IsSeparator { get; }

        /// <summary>Non-null → this item is a submenu header; <see cref="Handler"/> is ignored.</summary>
        public IReadOnlyList<OverlayMenuEntry>? Children { get; }

        /// <summary>
        /// Creates a regular clickable entry whose availability is controlled by <see cref="Handler"/>.
        /// </summary>
        public OverlayMenuEntry(string header, Geometry? icon = null, string? tooltip = null)
        {
            Header = header;
            Icon = icon;
            Tooltip = tooltip;
        }

        /// <summary>
        /// Creates an immediately-clickable entry with an inline action (e.g. internal operations
        /// whose handler is always available, such as Delete or Copy).
        /// </summary>
        public OverlayMenuEntry(string header, System.Action handler, bool isChecked = false, Geometry? icon = null, string? tooltip = null)
        {
            Header = header;
            Handler = handler;
            IsChecked = isChecked;
            Icon = icon;
            Tooltip = tooltip;
        }

        /// <summary>Submenu header with child entries.</summary>
        public OverlayMenuEntry(string header, IReadOnlyList<OverlayMenuEntry> children, Geometry? icon = null, string? tooltip = null)
        {
            Header = header;
            Children = children;
            Icon = icon;
            Tooltip = tooltip;
        }

        private OverlayMenuEntry()
        {
            Header = "-";
            IsSeparator = true;
        }

        public static OverlayMenuEntry Separator() => new();
    }
}
