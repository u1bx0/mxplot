using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace MxPlot.UI.Avalonia.Helpers
{
    /// <summary>
    /// Factory methods for common Avalonia UI controls used throughout MxPlot.UI.Avalonia.
    /// All methods produce consistently styled widgets that match the application theme.
    /// </summary>
    internal static class ControlFactory
    {
        // ── Color palette ───────────────────────────────────────────────
        private static readonly Color[] _colorPalette =
        [
            Colors.Black,                  Colors.White,
            Color.FromRgb(220,  20,  60),  Color.FromRgb(255, 165,   0),
            Color.FromRgb(255, 215,   0),  Color.FromRgb( 50, 205,  50),
            Color.FromRgb(  0, 128, 255),  Color.FromRgb(138,  43, 226),
            Color.FromRgb(128, 128, 128),  Color.FromRgb(192, 192, 192),
            Color.FromRgb(  0,   0, 128),  Color.FromRgb(139,   0,   0),
            Color.FromRgb(  0, 100,   0),  Colors.Yellow,
            Color.FromRgb(255, 255, 200),  Color.FromRgb(200, 230, 255),
        ];

        // ── Separators ────────────────────────────────────────────────

        /// <summary>Creates a thin horizontal separator line (1 px, 100-alpha grey by default).</summary>
        internal static Border MakeSep(Thickness? margin = null, byte alpha = 100) =>
            new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(alpha, 128, 128, 128)),
                Margin = margin ?? new Thickness(6, 1),
            };

        // ── Menu controls ─────────────────────────────────────────────────────

        /// <summary>Builds [PathIcon + TextBlock] content when an icon is provided.</summary>
        private static object MakeContent(string text, Geometry? icon, double fontSize, double iconSize = 14)
        {
            if (icon == null) return text;
            var pathIcon = new PathIcon { Data = icon, Width = iconSize, Height = iconSize };
            var brush = MenuIcons.DefaultBrush(icon);
            if (brush != null) pathIcon.Foreground = brush;
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { pathIcon, new TextBlock { Text = text, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center } },
            };
        }

        /// <summary>
        /// Creates a flat, full-width menu item button with the <c>menuitem</c> style class.
        /// </summary>
        internal static Button MakeMenuItem(string text, Action onClick,
            Geometry? icon = null, Thickness? padding = null, double fontSize = 12)
        {
            var btn = new Button
            {
                Content = MakeContent(text, icon, fontSize),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = padding ?? new Thickness(10, 5),
            };
            btn.Classes.Add("menuitem");
            btn.Click += (_, _) => onClick();
            return btn;
        }

        /// <summary>
        /// Creates an indented child menu item button (extra left indent, optional tooltip).
        /// </summary>
        internal static Button MakeChildMenuItem(string text, Action onClick,
            string? hint = null, Geometry? icon = null, double fontSize = 12, bool enabled = true)
        {
            var btn = new Button
            {
                Content = MakeContent(text, icon, fontSize, iconSize: 13),
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(26, 4),
                Margin = new Thickness(10, 3),
                IsEnabled = enabled,
            };
            btn.Classes.Add("menuitem");
            btn.Click += (_, _) => onClick();
            if (hint != null) ToolTip.SetTip(btn, hint);
            return btn;
        }

        /// <summary>
        /// Creates an indented toggle button menu item with optional tooltip.
        /// </summary>
        internal static ToggleButton MakeToggleMenuItem(string text, string? hint = null, double fontSize = 12)
        {
            var btn = new ToggleButton
            {
                Content = text,
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(26, 4),
                Margin = new Thickness(10, 3),
            };
            btn.Classes.Add("menuitem");
            if (hint != null) ToolTip.SetTip(btn, hint);
            return btn;
        }

        /// <summary>
        /// Creates a pseudo-checkbox row: a small square <see cref="ToggleButton"/> indicator
        /// followed by a <see cref="TextBlock"/> label. Clicking the label also toggles the button.
        /// Returns the container row and the toggle so the caller can wire <c>IsCheckedChanged</c>.
        /// </summary>
        internal static (StackPanel Row, ToggleButton Toggle) MakeCheckMenuItem(
            string text, string? hint = null, double fontSize = 12)
        {
            var toggle = new ToggleButton
            {
                Width = 13,
                Height = 13,
                MinHeight = 0,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            toggle.Classes.Add("check");
            var label = new TextBlock
            {
                Text = text,
                FontSize = fontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            label.PointerPressed += (_, e) =>
            {
                toggle.IsChecked = !(toggle.IsChecked ?? false);
                e.Handled = true;
            };
            toggle.IsCheckedChanged += (_, _) =>
                label.FontWeight = toggle.IsChecked == true ? FontWeight.SemiBold : FontWeight.Normal;
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 7,
                Margin = new Thickness(26, 4, 6, 4),
            };
            row.Children.Add(toggle);
            row.Children.Add(label);
            if (hint != null) ToolTip.SetTip(row, hint);
            return (row, toggle);
        }

        /// <summary>
        /// Creates a collapsible menu group: bold header button with ∧/∨ toggle arrow
        /// and a child <see cref="StackPanel"/> that shows/hides on click.
        /// </summary>
        internal static Control MakeMenuGroup(string header, Control[] items,
            Geometry? icon = null, double headerFontSize = 12, double arrowFontSize = 10,
            bool initiallyExpanded = true)
        {
            var arrow = new TextBlock
            {
                Text = initiallyExpanded ? "∧" : "∨",
                FontSize = arrowFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            var hdrContent = new DockPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            DockPanel.SetDock(arrow, Dock.Right);
            hdrContent.Children.Add(arrow);
            var hdrIcon = icon != null ? new PathIcon { Data = icon, Width = 14, Height = 14 } : null;
            if (hdrIcon != null)
            {
                var hdrBrush = MenuIcons.DefaultBrush(icon);
                if (hdrBrush != null) hdrIcon.Foreground = hdrBrush;
            }
            Control headerContent = hdrIcon != null
                ? (Control)new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        hdrIcon,
                        new TextBlock
                        {
                            Text = header,
                            FontSize = headerFontSize,
                            FontWeight = FontWeight.SemiBold,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    }
                }
                : new TextBlock
                {
                    Text = header,
                    FontSize = headerFontSize,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            hdrContent.Children.Add(headerContent);
            var hdrBtn = new Button
            {
                Content = hdrContent,
                FontSize = headerFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 5),
            };
            hdrBtn.Classes.Add("menuitem");
            var childStack = new StackPanel { Spacing = 1, IsVisible = initiallyExpanded };
            foreach (var item in items) childStack.Children.Add(item);
            hdrBtn.Click += (_, _) =>
            {
                childStack.IsVisible = !childStack.IsVisible;
                arrow.Text = childStack.IsVisible ? "∧" : "∨";
            };
            var group = new StackPanel();
            group.Children.Add(hdrBtn);
            group.Children.Add(childStack);
            group.Margin = new Thickness(0, 2);
            return group;
        }

        // ── Compact CheckBox ──────────────────────────────────────────────────

        /// <summary>
        /// Creates a compact <see cref="CheckBox"/> rendered at 75% scale via
        /// <see cref="ScaleTransform"/>, with <paramref name="fontSize"/> specifying the
        /// desired <em>visual</em> font size (internally upscaled to compensate for the transform).
        /// A negative bottom margin is applied to reclaim the layout space that
        /// <see cref="RenderTransform"/> leaves unused, making vertical stacking tight.
        /// <see cref="RenderTransformOrigin"/> is set to the top-left corner so the left
        /// edge stays aligned regardless of content width.
        /// </summary>
        internal static CheckBox MakeCheckBox(string label, double fontSize = 11, string? hint = null)
        {
            const double scale = 0.75;
            var chk = new CheckBox
            {
                Content = label,
                FontSize = fontSize / scale,
                Padding = new Thickness(4, 0, 0, 0),
                MinHeight = 0,
                Margin = new Thickness(0, 0, 0, -7),
                RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
                RenderTransform = new ScaleTransform(scale, scale),
            };
            if (hint != null) ToolTip.SetTip(chk, hint);
            return chk;
        }

        // ── Numeric controls ──────────────────────────────────────────────────

        /// <summary>
        /// Creates a compact <see cref="NumericUpDown"/> with consistent styling
        /// and the <c>compact</c> style class applied.
        /// </summary>
        internal static NumericUpDown MakeNumericUpDown(
            decimal value, decimal min, decimal max, decimal inc, double width = 76)
        {
            var nud = new NumericUpDown
            {
                Value = value,
                Minimum = min,
                Maximum = max,
                Increment = inc,
                Width = width,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            nud.Classes.Add("compact");
            return nud;
        }

        /// <summary>
        /// Creates a labeled horizontal row: [label] [NUD] [optional unit suffix].
        /// <paramref name="labelWidth"/> defaults to 50 to align multiple stacked rows.
        /// </summary>
        internal static Control MakeNudRow(
            string label, NumericUpDown nud, string unit = "", double labelWidth = 50)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Width = labelWidth,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(nud);
            if (unit.Length > 0)
                row.Children.Add(new TextBlock
                {
                    Text = unit,
                    FontSize = 11,
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center,
                });
            return row;
        }

        /// <summary>
        /// Creates a compact channel row: [short label] [Slider] [NUD].
        /// Designed for RGB-style or per-channel property pickers.
        /// </summary>
        internal static StackPanel MakeSliderRow(string label, Slider slider, NumericUpDown nud) =>
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = label,
                        Width = 10,
                        FontSize = 11,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    slider,
                    nud,
                }
            };

        // ── Color swatch ──────────────────────────────────────────────

        /// <summary>
        /// Creates a 22×22 colour swatch button that opens a flyout with a
        /// 16-colour palette and a custom ARGB/RGB channel picker.
        /// Changes are applied immediately via <paramref name="onApply"/>.
        /// </summary>
        /// <param name="showAlpha">
        /// When <c>true</c> (default), an alpha slider and NUD are included.
        /// When <c>false</c>, alpha is always 255 and the A row is hidden.
        /// </param>
        internal static Button MakeColorSwatch(Color initial, Action<Color> onApply, bool showAlpha = true, Color[]? colorPalette = null)
        {
            var swatch = new Button
            {
                Width = 22, Height = 22, MinHeight = 0,
                Background = new SolidColorBrush(initial),
                Padding = new Thickness(0), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

            Color custom = initial;
            var aSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.A, Width = 100 };
            var rSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.R, Width = 100 };
            var gSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.G, Width = 100 };
            var bSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.B, Width = 100 };
            var aNud = MakeNumericUpDown(custom.A, 0, 255, 1); aNud.Width = 58;
            var rNud = MakeNumericUpDown(custom.R, 0, 255, 1); rNud.Width = 58;
            var gNud = MakeNumericUpDown(custom.G, 0, 255, 1); gNud.Width = 58;
            var bNud = MakeNumericUpDown(custom.B, 0, 255, 1); bNud.Width = 58;

            var preview = new Border
            {
                Width = 32, Height = 20,
                Background = new SolidColorBrush(custom),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = new CornerRadius(2),
            };

            bool syncing = false;
            byte AlphaValue() => showAlpha ? (byte)Math.Round(aSlider.Value) : (byte)255;
            void UpdatePreview() =>
                preview.Background = new SolidColorBrush(
                    Color.FromArgb(AlphaValue(),
                                   (byte)Math.Round(rSlider.Value),
                                   (byte)Math.Round(gSlider.Value),
                                   (byte)Math.Round(bSlider.Value)));
            void SyncFromSliders()
            {
                if (syncing) return; syncing = true;
                if (showAlpha) aNud.Value = (decimal)Math.Round(aSlider.Value);
                rNud.Value = (decimal)Math.Round(rSlider.Value);
                gNud.Value = (decimal)Math.Round(gSlider.Value);
                bNud.Value = (decimal)Math.Round(bSlider.Value);
                syncing = false; UpdatePreview();
            }
            void SyncFromNuds()
            {
                if (syncing) return; syncing = true;
                if (showAlpha) aSlider.Value = (double)(aNud.Value ?? 0);
                rSlider.Value = (double)(rNud.Value ?? 0);
                gSlider.Value = (double)(gNud.Value ?? 0);
                bSlider.Value = (double)(bNud.Value ?? 0);
                syncing = false; UpdatePreview();
            }

            void Apply(Color c)
            {
                swatch.Background = new SolidColorBrush(c);
                onApply(c);
                flyout.Hide();
            }

            if (colorPalette == null || colorPalette.Length == 0)
            {
                colorPalette = _colorPalette;
            }
            var paletteGrid = new UniformGrid { Columns = 8 };
            foreach (var pc in colorPalette)
            {
                var cap = pc;
                var btn = new Button
                {
                    Width = 22, Height = 22, MinHeight = 0,
                    Background = new SolidColorBrush(pc),
                    Padding = new Thickness(0), BorderThickness = new Thickness(0.5),
                };
                string tip = showAlpha
                    ? $"R={cap.R}, G={cap.G}, B={cap.B} [A={cap.A}]"
                    : $"R={cap.R}, G={cap.G}, B={cap.B}";
                ToolTip.SetTip(btn, tip);
                // Palette loads R/G/B into sliders & NUDs (preserving current A); Apply commits.
                btn.Click += (_, _) =>
                {
                    if (syncing) return; syncing = true;
                    rSlider.Value = cap.R;
                    gSlider.Value = cap.G;
                    bSlider.Value = cap.B;
                    rNud.Value = cap.R;
                    gNud.Value = cap.G;
                    bNud.Value = cap.B;
                    syncing = false;
                    UpdatePreview();
                };
                paletteGrid.Children.Add(btn);
            }

            if (showAlpha) aSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncFromSliders(); };
            rSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncFromSliders(); };
            gSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncFromSliders(); };
            bSlider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SyncFromSliders(); };
            if (showAlpha) aNud.ValueChanged += (_, _) => SyncFromNuds();
            rNud.ValueChanged += (_, _) => SyncFromNuds();
            gNud.ValueChanged += (_, _) => SyncFromNuds();
            bNud.ValueChanged += (_, _) => SyncFromNuds();

            var applyBtn = new Button
            {
                Content = "Apply", FontSize = 11, Height = 20, MinHeight = 0,
                Padding = new Thickness(10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            applyBtn.Click += (_, _) =>
            {
                custom = Color.FromArgb(AlphaValue(),
                                        (byte)Math.Round(rSlider.Value),
                                        (byte)Math.Round(gSlider.Value),
                                        (byte)Math.Round(bSlider.Value));
                Apply(custom);
            };

            var channelPanel = new StackPanel { Spacing = 1, Margin = new Thickness(3) };
            if (showAlpha) channelPanel.Children.Add(MakeSliderRow("A", aSlider, aNud));
            channelPanel.Children.Add(MakeSliderRow("R", rSlider, rNud));
            channelPanel.Children.Add(MakeSliderRow("G", gSlider, gNud));
            channelPanel.Children.Add(MakeSliderRow("B", bSlider, bNud));
            var bottomRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(0, 2, 0, 0),
            };
            bottomRow.Children.Add(preview);
            bottomRow.Children.Add(applyBtn);
            channelPanel.Children.Add(bottomRow);

            var flyoutContent = new StackPanel { Spacing = 2 };
            flyoutContent.Children.Add(new Border { Child = paletteGrid, Padding = new Thickness(1) });
            flyoutContent.Children.Add(channelPanel);
            flyout.Content = flyoutContent;

            FlyoutBase.SetAttachedFlyout(swatch, flyout);
            swatch.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(swatch);
            return swatch;
        }
    }
}
