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
    /// A compact horizontal tracker for a <see cref="ColorAxis"/> axis.
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
    /// <para>
    /// <b>Status: not wired up.</b> This control was written while the multi-channel display
    /// design was still unsettled and was never finished: it raises <see cref="ConfigRequested"/>,
    /// <see cref="ModeChanged"/> and <see cref="TagToggled"/> but implements no behaviour behind
    /// them, and it cannot rename a tag despite subscribing to
    /// <see cref="TaggedAxis.TagNameChanged"/>.
    /// </para>
    /// <para>
    /// Composite mode superseded every one of its features: the composite/single toggle became the
    /// Composite entry button on <see cref="AxisTracker"/>, the config button became the composite
    /// settings panel, and the coloured tag chips became <see cref="BlendRecipeBar"/> rows, which
    /// additionally carry range, gain and click-to-rename. <c>MatrixPlotter</c> therefore no longer
    /// references this type at all. It is kept only as a record of the earlier design; anything
    /// built on top of it should extend <see cref="BlendRecipeBar"/> instead.
    /// </para>
    /// </summary>
    public class ColorAxisTracker : UserControl
    {
        // ── Dimensions (match AxisTracker) ────────────────────────────────────
        private const double LabelWidth = 50;
        private const double ButtonSize = 22;
        private const double IndicatorWidth = 32;
        private const double ComponentHeight = 20;
        private const double ChipFontSize = 10;

        // ── Default palette (used when ColorAxis.HasAssignedColors is false) ─
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
        private readonly ColorAxis _channel;

        // ── Controls ──────────────────────────────────────────────────────────
        private readonly TextBlock _nameLabel;
        private readonly ToggleButton _modeToggle;
        private readonly Button _configButton;
        private readonly UniformGrid _chipPanel;

        // ── State ─────────────────────────────────────────────────────────────
        private bool[] _tagEnabled = [];

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>The underlying <see cref="ColorAxis"/> axis.</summary>
        public ColorAxis Channel => _channel;

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

        /// <summary>
        /// Raised when a channel tag chip is toggled on or off.
        /// The tuple carries the tag index and the new enabled state.
        /// </summary>
        public event EventHandler<(int Index, bool Enabled)>? TagToggled;

        /// <summary>Returns the current enabled state of each tag chip (index-aligned with <see cref="ColorAxis.Tags"/>).</summary>
        public IReadOnlyList<bool> TagEnabled => _tagEnabled;

        // ── Constructor ───────────────────────────────────────────────────────

        public ColorAxisTracker(ColorAxis channel)
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
            _chipPanel = new UniformGrid
            {
                Rows = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0),
            };

            // ── Wire model events ─────────────────────────────────────────────
            _channel.ColorAssignChanged += OnChannelChanged;
            _channel.TagNameChanged += OnChannelChanged;
            _channel.NameChanged += OnChannelNameChanged;

            // ── Layout ────────────────────────────────────────────────────────
            var grid = new Grid { Margin = new Thickness(5, 0, 5, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                    // Name
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                    // Mode toggle (FuncSlot)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));                    // Config 🎨
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));                    // Chip strip
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(IndicatorWidth)));     // spacer ≈ Indicator
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(ButtonSize)));         // spacer ≈ FreezeButton

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
        /// Rebuilds the tag chip strip from <see cref="ColorAxis.Tags"/>
        /// and <see cref="ColorAxis.AssignedColors"/>.
        /// Existing toggle states are preserved across rebuilds where possible.
        /// </summary>
        private void RebuildChips()
        {
            _chipPanel.Children.Clear();

            IReadOnlyList<string> tags = _channel.Tags;
            bool hasColors = _channel.HasAssignedColors;

            // Preserve existing toggle states; new tags default to enabled.
            var prevEnabled = _tagEnabled;
            _tagEnabled = new bool[tags.Count];
            for (int i = 0; i < _tagEnabled.Length; i++)
                _tagEnabled[i] = i < prevEnabled.Length ? prevEnabled[i] : true;

            _chipPanel.Columns = tags.Count;

            for (int i = 0; i < tags.Count; i++)
            {
                Color bg = hasColors
                    ? ArgbToColor(_channel.GetColor(i))
                    : DefaultPalette[i % DefaultPalette.Length];

                bool enabled = _tagEnabled[i];
                int capturedIndex = i;

                var chip = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    MinHeight = ComponentHeight,
                    Padding = new Thickness(4, 1),
                    Background = new SolidColorBrush(bg),
                    BorderThickness = new Thickness(0),
                    Opacity = enabled ? 1.0 : 0.35,
                    Content = new TextBlock
                    {
                        Text = tags[i],
                        FontSize = ChipFontSize,
                        Foreground = IsLightColor(bg) ? Brushes.Black : Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                    },
                };
                ToolTip.SetTip(chip, $"#{i}: {tags[i]}  (R={bg.R} G={bg.G} B={bg.B})");
                chip.Click += (_, _) =>
                {
                    bool on = !_tagEnabled[capturedIndex];
                    _tagEnabled[capturedIndex] = on;
                    chip.Opacity = on ? 1.0 : 0.35;
                    TagToggled?.Invoke(this, (capturedIndex, on));
                };
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
