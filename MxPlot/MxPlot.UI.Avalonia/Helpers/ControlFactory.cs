using Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.IO;
using System.Linq;

namespace MxPlot.UI.Avalonia.Helpers
{
    /// <summary>
    /// Factory methods for common Avalonia UI controls used throughout MxPlot.UI.Avalonia.
    /// All methods produce consistently styled widgets that match the application theme.
    /// </summary>
    internal static class ControlFactory
    {
        // ── Color palette ───────────────────────────────────────────────
        // Default palette: prioritizes colors commonly used in composite imaging
        private static readonly Color[] _colorPalette =
        [
           Colors.White,                      // 1
            Color.FromRgb(255,   0,   0),      // 2 Red
            Color.FromRgb(  0, 255,   0),      // 3 Green
            Color.FromRgb(  0,   0, 255),      // 4 Blue
            Color.FromRgb(  0, 255, 255),      // 5 Cyan
            Colors.Yellow,                     // 6 Yellow
            Colors.Magenta,                    // 7 Magenta
            Color.FromRgb(255, 128,   0),      // 8 Orange (TRITC / AF555)
            Color.FromRgb(255, 192, 203),      // 9 Pink (AF568 / AF594 pseudo)
            Color.FromRgb(128,   0, 128),      // 10 Purple (Cy5 / AF647 pseudo)
            Color.FromRgb(128, 128, 128),      // 11 Gray (mask / ROI)
            Color.FromRgb(192, 192, 192),      // 12 Light gray (background)
            Color.FromRgb(160,   0, 160),      // 13 Deep magenta (Cy5.5 / AF680 pseudo)
            Color.FromRgb(255, 215,   0),      // 14 Gold (YFP alternative)
            Color.FromRgb(139,  69,  19),      // 15 Brown (low‑freq but distinct)
            Color.FromRgb(  0, 128, 255),      // 16 Azure (CFP alternative)
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
            // No icon: still wrap in a TextBlock so fontSize is honoured. Returning the bare
            // string would let the Button inherit the ambient (larger) font size instead.
            if (icon == null)
                return new TextBlock
                {
                    Text = text,
                    FontSize = fontSize,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            var pathIcon = new PathIcon { Data = icon, Width = iconSize, Height = iconSize };
            var brush = MenuIcons.DefaultBrush(icon);
            if (brush != null) pathIcon.Foreground = brush;
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(6, 0, 0, 0),
                Children = {
                    pathIcon,
                    new TextBlock {
                        Text = text,
                        FontSize = fontSize,
                        VerticalAlignment = VerticalAlignment.Center
                    } },
            };
        }

        /// <summary>
        /// Creates a flat, full-width menu item button with the <c>menuitem</c> style class.
        /// </summary>
        internal static Button MakeMenuItem(string text, Action onClick,
            Geometry? icon = null, Thickness? padding = null, double fontSize = 12, double itemHeight = 20)
        {
            var btn = new Button
            {
                Content = MakeContent(text, icon, fontSize),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = padding ?? new Thickness(10, 5),
                Height = itemHeight,
            };
            btn.Classes.Add("menuitem");
            btn.Click += (_, _) => onClick();
            return btn;
        }

        /// <summary>
        /// Creates an indented child menu item button (extra left indent, optional tooltip).
        /// </summary>
        internal static Button MakeChildMenuItem(string text, Action onClick,
            string? hint = null, Geometry? icon = null, double fontSize = 12, double itemHeight = 20,
            bool enabled = true)
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
                Height = itemHeight,
            };
            btn.Classes.Add("menuitem");
            btn.Click += (_, _) => onClick();
            if (hint != null) ToolTip.SetTip(btn, hint);
            return btn;
        }

        /// <summary>
        /// Creates an indented toggle button menu item with optional tooltip.
        /// </summary>
        internal static ToggleButton MakeToggleMenuItem(string text, string? hint = null,
            double fontSize = 12, double itemHeight = 20)
        {
            var btn = new ToggleButton
            {
                Content = text,
                FontSize = fontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(26, 4),
                Margin = new Thickness(10, 3),
                Height = itemHeight,
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
            double itemHeight = 20,
            bool initiallyExpanded = true, double indent = 0,
            FontWeight headerFontWeight = default)
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
            var fw = headerFontWeight == default ? FontWeight.SemiBold : headerFontWeight;
            Control headerContent = hdrIcon != null
                ? (Control)new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Height = itemHeight,
                    Children =
                    {
                        hdrIcon,
                        new TextBlock
                        {
                            Text = header,
                            FontSize = headerFontSize,
                            FontWeight = fw,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    }
                }
                : new TextBlock
                {
                    Text = header,
                    FontSize = headerFontSize,
                    FontWeight = fw,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            hdrContent.Children.Add(headerContent);
            hdrContent.Margin = new Thickness(6, 0, 0, 0);

            var hdrBtn = new Button
            {
                Content = hdrContent,
                FontSize = headerFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10 + indent, 5),
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
            group.Margin = new Thickness(indent > 0 ? indent : 0, 2, 0, 2);
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
                Margin = new Thickness(2, 1),
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
                Width = 22,
                Height = 22,
                MinHeight = 0,
                Background = new SolidColorBrush(initial),
                Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            swatch.Classes.Add("colorswatch");
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };

            Color custom = initial;
            Color flyoutInitial = initial; // Track the color when flyout opens
            var aSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.A, Width = 100, MinHeight = 0 };
            var rSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.R, Width = 100, MinHeight = 0 };
            var gSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.G, Width = 100, MinHeight = 0 };
            var bSlider = new Slider { Minimum = 0, Maximum = 255, Value = custom.B, Width = 100, MinHeight = 0 };

            void ConfigureCompactSlider(Slider slider)
            {
                slider.TemplateApplied += (_, e) =>
                {
                    if (e.NameScope.Find("thumb") is Thumb thumb)
                    {
                        thumb.MinWidth = 0;
                        thumb.MinHeight = 0;
                        thumb.Width = 12;
                        thumb.Height = 12;
                    }

                    if (e.NameScope.Find("PART_Track") is Track track)
                    {
                        track.MinHeight = 2;
                        track.Height = 2;
                        if (track.IncreaseButton is Control inc)
                            inc.Height = 1;
                        if (track.DecreaseButton is Control dec)
                            dec.Height = 1;
                    }
                };
            }
            ConfigureCompactSlider(aSlider);
            ConfigureCompactSlider(rSlider);
            ConfigureCompactSlider(gSlider);
            ConfigureCompactSlider(bSlider);

            var aNud = MakeNumericUpDown(custom.A, 0, 255, 1); aNud.Width = 58;
            var rNud = MakeNumericUpDown(custom.R, 0, 255, 1); rNud.Width = 58;
            var gNud = MakeNumericUpDown(custom.G, 0, 255, 1); gNud.Width = 58;
            var bNud = MakeNumericUpDown(custom.B, 0, 255, 1); bNud.Width = 58;

            var preview = new Border
            {
                Width = 32,
                Height = 20,
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

            Button? applyBtn = null; // Forward reference for CheckColorChanged

            void CheckColorChanged()
            {
                if (applyBtn == null) return;

                // Compare current slider values with flyoutInitial
                bool alphaChanged = showAlpha && (byte)Math.Round(aSlider.Value) != flyoutInitial.A;
                bool colorChanged = (byte)Math.Round(rSlider.Value) != flyoutInitial.R
                                 || (byte)Math.Round(gSlider.Value) != flyoutInitial.G
                                 || (byte)Math.Round(bSlider.Value) != flyoutInitial.B;

                applyBtn.IsVisible = alphaChanged || colorChanged;
            }

            void SyncFromSliders()
            {
                if (syncing) return; syncing = true;
                if (showAlpha) aNud.Value = (decimal)Math.Round(aSlider.Value);
                rNud.Value = (decimal)Math.Round(rSlider.Value);
                gNud.Value = (decimal)Math.Round(gSlider.Value);
                bNud.Value = (decimal)Math.Round(bSlider.Value);
                syncing = false;
                UpdatePreview();
                CheckColorChanged();
            }
            void SyncFromNuds()
            {
                if (syncing) return; syncing = true;
                if (showAlpha) aSlider.Value = (double)(aNud.Value ?? 0);
                rSlider.Value = (double)(rNud.Value ?? 0);
                gSlider.Value = (double)(gNud.Value ?? 0);
                bSlider.Value = (double)(bNud.Value ?? 0);
                syncing = false;
                UpdatePreview();
                CheckColorChanged();
            }

            void Apply(Color c)
            {
                swatch.Background = new SolidColorBrush(c);
                custom = c; // Update the stored color
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
                    Width = 22,
                    Height = 22,
                    MinHeight = 0,
                    Background = new SolidColorBrush(pc),
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0.5),
                };
                btn.Classes.Add("colorswatch");
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
                    CheckColorChanged();
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

            applyBtn = new Button
            {
                Content = "Apply",
                FontSize = 11,
                Height = 20,
                MinHeight = 0,
                Padding = new Thickness(10, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsVisible = false, // Initially hidden until color changes
            };
            applyBtn.Click += (_, _) =>
            {
                var newColor = Color.FromArgb(AlphaValue(),
                                        (byte)Math.Round(rSlider.Value),
                                        (byte)Math.Round(gSlider.Value),
                                        (byte)Math.Round(bSlider.Value));
                Apply(newColor);
            };

            var channelPanel = new StackPanel { Spacing = 1, Margin = new Thickness(1) };
            if (showAlpha) channelPanel.Children.Add(MakeSliderRow("A", aSlider, aNud));
            channelPanel.Children.Add(MakeSliderRow("R", rSlider, rNud));
            channelPanel.Children.Add(MakeSliderRow("G", gSlider, gNud));
            channelPanel.Children.Add(MakeSliderRow("B", bSlider, bNud));
            var bottomRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
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

            // When flyout opens, snapshot the current color and reset slider values
            flyout.Opening += (_, _) =>
            {
                flyoutInitial = custom;
                if (syncing) return;
                syncing = true;
                if (showAlpha)
                {
                    aSlider.Value = custom.A;
                    aNud.Value = custom.A;
                }
                rSlider.Value = custom.R;
                gSlider.Value = custom.G;
                bSlider.Value = custom.B;
                rNud.Value = custom.R;
                gNud.Value = custom.G;
                bNud.Value = custom.B;
                syncing = false;
                UpdatePreview();
                if (applyBtn != null) applyBtn.IsVisible = false; // Hide apply button on open
            };

            swatch.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(swatch);
            return swatch;
        }

        // ---- Composite icon --------------------------------------------------

        /// <summary>
        /// The three lobe colours shared by <see cref="MakeCompositeIcon"/> (the AxisTracker
        /// toggle button) and <see cref="CreateCompositeWindowIcon"/> (the window titlebar icon),
        /// so the two stay visually identical.
        /// </summary>
        /// <remarks>
        /// Pure R/G/B, matching <c>ColorAxis.CreateRgb()</c>'s primaries.
        /// </remarks>
        private static readonly Color[] CompositeIconColors = [Colors.Red, Colors.Lime, Colors.Blue];

        /// <summary>Lobe geometry shared by <see cref="RenderCompositeIconBitmap"/>.</summary>
        private static (double Diameter, (double Left, double Top)[] Positions) CompositeIconLobes(double size)
        {
            double d = size * 0.62;               // lobe diameter
            double cx = (size - d) / 2.0;         // horizontal centre for the top lobe
            double dx = size * 0.19;              // horizontal spread of the lower pair
            double dy = size * 0.30;              // vertical offset of the lower pair

            // Three circles on the vertices of a triangle, sized so the pairwise overlaps meet in
            // the centre - the classic additive-colour Venn figure.
            return (d, [(cx, 0), (cx - dx, dy), (cx + dx, dy)]);
        }

        /// <summary>
        /// Rasterizes the three-overlapping-circles mark used for Composite (multi-channel blend)
        /// mode, mixing the lobes with the same per-channel additive sum
        /// <see cref="Rendering.CompositeBitmapWriter"/> uses for <see cref="Rendering.BlendMode.Additive"/>
        /// (each channel is the clamped sum of every lobe covering that pixel).
        /// </summary>
        /// <remarks>
        /// This is deliberately pixel math, not alpha-blended <see cref="Ellipse"/> shapes: alpha
        /// blending darkens/mutes overlaps depending on paint order, whereas true additive mixing
        /// is what makes Composite mode meaningful — R+G gives yellow, G+B gives cyan, R+B gives
        /// magenta/pink, and all three together give white at the centre, exactly as they would
        /// on real composited data. Lobe edges are antialiased over roughly one pixel; outside all
        /// three lobes the pixel is fully transparent, so the icon drops cleanly onto any
        /// background (button chrome, titlebar, light or dark theme).
        /// </remarks>
        /// <param name="size">Side length of the square icon, in pixels.</param>
        private static unsafe WriteableBitmap RenderCompositeIconBitmap(int size)
        {
            var (d, positions) = CompositeIconLobes(size);
            double radius = d / 2.0;
            var centers = positions.Select(p => (Cx: p.Left + radius, Cy: p.Top + radius)).ToArray();

            var bmp = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96),
                                          global::Avalonia.Platform.PixelFormat.Bgra8888,
                                          global::Avalonia.Platform.AlphaFormat.Premul);
            using var fb = bmp.Lock();
            int stride = fb.RowBytes / 4;
            int* basePtr = (int*)fb.Address;

            for (int y = 0; y < size; y++)
            {
                int* row = basePtr + y * stride;
                for (int x = 0; x < size; x++)
                {
                    int sr = 0, sg = 0, sb = 0;
                    double coverage = 0;
                    for (int i = 0; i < centers.Length; i++)
                    {
                        double dx = x + 0.5 - centers[i].Cx;
                        double dy = y + 0.5 - centers[i].Cy;
                        double dist = Math.Sqrt(dx * dx + dy * dy);
                        // 1px-wide antialiased edge: 1.0 well inside the circle, 0.0 well outside.
                        double c = Math.Clamp(radius - dist + 0.5, 0.0, 1.0);
                        if (c <= 0) continue;

                        var color = CompositeIconColors[i];
                        sr += (int)(color.R * c);
                        sg += (int)(color.G * c);
                        sb += (int)(color.B * c);
                        if (c > coverage) coverage = c;
                    }

                    if (coverage <= 0) { row[x] = 0; continue; }

                    sr = Math.Min(sr, 255);
                    sg = Math.Min(sg, 255);
                    sb = Math.Min(sb, 255);
                    int a = (int)(coverage * 255);
                    // Premultiplied alpha: scale RGB by (a / 255) so partially-covered edge pixels
                    // don't come out over-bright when composited onto the background.
                    row[x] = (a << 24) | ((sr * a / 255) << 16) | ((sg * a / 255) << 8) | (sb * a / 255);
                }
            }
            return bmp;
        }

        /// <summary>Wraps <see cref="RenderCompositeIconBitmap"/> for use as a toolbar/button icon.</summary>
        /// <param name="size">Overall square size in DIPs. Defaults to 14.</param>
        internal static Control MakeCompositeIcon(double size = 14)
        {
            int px = Math.Max(1, (int)Math.Round(size));
            return new Image
            {
                Source = RenderCompositeIconBitmap(px),
                Width = size,
                Height = size,
                Stretch = Stretch.Fill,
            };
        }

        /// <summary>
        /// Wraps <see cref="RenderCompositeIconBitmap"/> as a <see cref="WindowIcon"/>, for use as
        /// the titlebar icon while a window is in Composite mode (the LUT-mode titlebar icon is a
        /// rendered LUT gradient - see <c>LutSelector.CreateIcon</c> - so Composite gets an equally
        /// mode-specific icon rather than staying on whatever LUT icon was selected beforehand).
        /// </summary>
        internal static WindowIcon CreateCompositeWindowIcon(int size = 32)
        {
            using var bmp = RenderCompositeIconBitmap(size);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }
    }
}
