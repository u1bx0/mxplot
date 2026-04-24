using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Overlays
{
    /// <summary>
    /// A lightweight context-menu action returned by overlay objects.
    /// The host control builds an Avalonia <c>ContextMenu</c> from these items.
    /// Supports nested submenus via <see cref="Children"/> and radio-style
    /// checked state via <see cref="IsChecked"/>.
    /// </summary>
    public sealed class OverlayMenuItem
    {
        public string Header { get; }
        /// <summary>Optional vector icon geometry displayed in the menu icon slot.</summary>
        public Geometry? Icon { get; }
        public Action Click { get; }
        public bool IsSeparator { get; }
        /// <summary>When true the item shows a radio/check indicator.</summary>
        public bool IsChecked { get; }
        /// <summary>Optional tooltip text shown on hover.</summary>
        public string? Tooltip { get; }
        /// <summary>Non-null → this item is a submenu header; Click is ignored.</summary>
        public IReadOnlyList<OverlayMenuItem>? Children { get; }

        /// <summary>Regular clickable item.</summary>
        public OverlayMenuItem(string header, Action click, bool isChecked = false, Geometry? icon = null, string? tooltip = null)
        {
            Header = header;
            Icon = icon;
            Click = click;
            IsChecked = isChecked;
            Tooltip = tooltip;
            IsSeparator = false;
        }

        /// <summary>Submenu header with child items.</summary>
        public OverlayMenuItem(string header, IReadOnlyList<OverlayMenuItem> children, Geometry? icon = null, string? tooltip = null)
        {
            Header = header;
            Icon = icon;
            Click = () => { };
            Children = children;
            Tooltip = tooltip;
            IsSeparator = false;
        }

        private OverlayMenuItem()
        {
            Header = "-";
            Click = () => { };
            IsSeparator = true;
        }

        public static OverlayMenuItem Separator() => new();
    }
}
