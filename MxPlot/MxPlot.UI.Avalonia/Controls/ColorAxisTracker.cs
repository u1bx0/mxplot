using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// A compact horizontal tracker for a <see cref="ColorChannel"/> axis.
    /// Used instead of <see cref="AxisTracker"/> when the axis supports per-channel color assignment.
    /// <para>
    /// Layout: <c>[Name] [C Mode] [🎨 Config] [  Tag  |  Tag  |  Tag  ]</c>
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Name:</b> axis name label (same width as <see cref="AxisTracker"/>).</item>
    ///   <item><b>Mode (C):</b> toggles between composite (all channels overlaid) and
    ///         single-channel display mode. Located in the FuncSlot column shared with
    ///         <see cref="AxisTracker"/> for consistent alignment.</item>
    ///   <item><b>Config (🎨):</b> opens composite color settings (wired externally).</item>
    ///   <item><b>Channel Indicator:</b> horizontal chip strip — each chip shows the tag text
    ///         with its background set to the channel's assigned ARGB color.</item>
    /// </list>
    /// </summary>
    public class ColorAxisTracker : UserControl
    {
        // ── Dimensions (match AxisTracker) ────────────────────────────────────
        private const double LabelWidth = 50;
        private const double ButtonSize = 22;
        private const double ComponentHeight = 20;
        private const double ChipFontSize = 10;

        // ── Default palette (used when ColorChannel.HasAssignedColors is false) ─
        private static readonly Color[] DefaultPalette =
        [
            Color.FromRgb(0x44, 0x88, 0xFF),   // Blue   (DAPI)
            Color.FromRgb(0x44, 0xFF, 0x44),   // Green  (GFP / FITC)
            Color.FromRgb(0xFF, 0x44, 0x44),   // Red    (Cy3 / TRITC)
            Color.FromRgb(0x00, 0xDD, 0xDD),   // Cyan
            Color.FromRgb(0xFF, 0x44, 0xFF),   // Magenta
            Color.FromRgb(0xFF, 0xDD, 0x00),   // Yellow
            Color.FromRgb(0xFF, 0x88, 0x00),   // Orange
            Color.FromRgb(0xAA, 0xAA, 0xAA),   // Gray (fallback)
        ];

        // ── Model ─────────────────────────────────────────────────────────────
        private readonly ColorChannel _channel;

        // ── Controls ──────────────────────────────────────────────────────────
        private readonly TextBlock _nameLabel;
        private readonly ToggleButton _modeToggle;
        private readonly Button _configButton;
        private readonly StackPanel _chipPanel;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>The underlying <see cref="ColorChannel"/> axis.</summary>
        public ColorChannel Channel => _channel;

        /// <summary>Whether the tracker is in composite display mode (all channels overlaid).</summary>
        public bool IsComposite => _modeToggle.IsChecked == true;

        /// <summary>
        /// Raised when the 🎨 config button is clicked.
        /// The host (e.g., MatrixPlotter) should open a composite color settings dialog.
        /// </summary>
        public event EventHandler? ConfigRequested;

        /// <summary>
        /// Raised when the composite/single-channel mode toggle changes.
        /// <c>true</c> = composite mode (all channels overlaid),
        /// <c>false</c> = single-channel mode.
        /// </summary>
        public event EventHandler<bool>? ModeChanged;

        // ── Constructor ───────────────────────────────────────────────────────

        public ColorAxisTracker(ColorChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));

            // ── Name label ────────────────────────────────────────────────────
            _nameLabel = new TextBlock
            {
                Text = channel.Name,
                Width = LabelWidth,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                FontSize = 12,
                Margin = new Thickness(0, 0, 6, 0),
                MinHeight = 0,
            };

            // ── Mode toggle (FuncSlot: composite ↔ single-channel) ──────────
            _modeToggle = new ToggleButton
            {
                Content = "C",
                Width = ButtonSize,
                Height = ButtonSize,
                FontSize = 12,
                FontWeight = FontWeight.Bold,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsChecked = true,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(_modeToggle, "Composite mode (all channels overlaid)");
            _modeToggle.IsCheckedChanged += OnModeToggleChanged;

            // ── Config button ─────────────────────────────────────────────────
            _configButton = new Button
            {
                Content = "🎨",
                Width = ButtonSize,
                Height = ButtonSize,
                FontSize = 11,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(_configButton, "Composite color settings");
            _configButton.Click += (_, _) => ConfigRequested?.Invoke(this, EventArgs.Empty);

            // ── Channel indicator (chip strip) ────────────────────────────────
            _chipPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
            };

            // ── Wire model events ─────────────────────────────────────────────
            _channel.ColorAssignChanged += OnChannelChanged;
            _channel.TagNameChanged += OnChannelChanged;
            _channel.NameChanged += OnChannelNameChanged;

            // ── Layout ────────────────────────────────────────────────────────
            var grid = new Grid { Margin = new Thickness(5, 0, 5, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // Name
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // Mode toggle (FuncSlot)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));   // Config 🎨
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));   // Chip strip

            Grid.SetColumn(_nameLabel, 0);
            Grid.SetColumn(_modeToggle, 1);
            Grid.SetColumn(_configButton, 2);
            Grid.SetColumn(_chipPanel, 3);

            grid.Children.Add(_nameLabel);
            grid.Children.Add(_modeToggle);
            grid.Children.Add(_configButton);
            grid.Children.Add(_chipPanel);

            Content = grid;
            RebuildChips();
        }

        // ── Chip construction ─────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the tag chip strip from <see cref="ColorChannel.Tags"/>
        /// and <see cref="ColorChannel.AssignedColors"/>.
        /// </summary>
        private void RebuildChips()
        {
            _chipPanel.Children.Clear();

            IReadOnlyList<string> tags = _channel.Tags;
            bool hasColors = _channel.HasAssignedColors;

            for (int i = 0; i < tags.Count; i++)
            {
                Color bg = hasColors
                    ? ArgbToColor(_channel.GetColor(i))
                    : DefaultPalette[i % DefaultPalette.Length];

                var chip = new Border
                {
                    Background = new SolidColorBrush(bg),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 1),
                    MinHeight = ComponentHeight,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = tags[i],
                        FontSize = ChipFontSize,
                        Foreground = IsLightColor(bg) ? Brushes.Black : Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                    },
                };
                ToolTip.SetTip(chip, $"#{i}: {tags[i]}  (R={bg.R} G={bg.G} B={bg.B})");
                _chipPanel.Children.Add(chip);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Converts a packed ARGB <see cref="int"/> to an Avalonia <see cref="Color"/>.</summary>
        private static Color ArgbToColor(int argb)
        {
            byte a = (byte)((argb >> 24) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte b = (byte)(argb & 0xFF);
            return a == 0 ? Color.FromRgb(r, g, b) : Color.FromArgb(a, r, g, b);
        }

        /// <summary>
        /// Returns <c>true</c> when the perceived luminance is high enough for black text.
        /// Uses the W3C relative-luminance formula (sRGB coefficients).
        /// </summary>
        private static bool IsLightColor(Color c)
            => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 160;

        // ── Cleanup ───────────────────────────────────────────────────────────

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _channel.ColorAssignChanged -= OnChannelChanged;
            _channel.TagNameChanged -= OnChannelChanged;
            _channel.NameChanged -= OnChannelNameChanged;
        }

        private void OnChannelChanged(object? sender, EventArgs e) => RebuildChips();
        private void OnChannelNameChanged(object? sender, EventArgs e) => _nameLabel.Text = _channel.Name;

        private void OnModeToggleChanged(object? sender, RoutedEventArgs e)
        {
            bool composite = _modeToggle.IsChecked == true;
            ToolTip.SetTip(_modeToggle, composite
                ? "Composite mode (all channels overlaid)"
                : "Single-channel mode");
            ModeChanged?.Invoke(this, composite);
        }
    }
}
