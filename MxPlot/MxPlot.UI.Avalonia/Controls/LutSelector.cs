using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// A compact top-bar LUT selector.
    /// Layout: <c>Lut:</c> label followed by a ComboBox with inline gradient preview.
    /// </summary>
    /// <remarks>
    /// On first use, attempts to load any <c>*.mlut</c> files found in a <c>LUTs/</c>
    /// subdirectory next to the application executable and registers them in
    /// <see cref="ColorThemes"/>.
    /// </remarks>
    public class LutSelector : UserControl
    {
        // ── Item model ────────────────────────────────────────────────────────

        private sealed record LutItem(LookupTable Lut, WriteableBitmap Preview, WindowIcon Icon)
        {
            public string Name => Lut.Name;
        }

        // ── Dimensions ────────────────────────────────────────────────────────

        private const int PreviewW = 256;
        private const int PreviewH = 1;

        // ── Controls ──────────────────────────────────────────────────────────

        private readonly ComboBox _comboBox;   // name list with inline gradient preview
        private readonly TextBlock _lutLabel;   // "LUT:" label — hidden when control is very narrow
        private Popup?   _dropdownPopup;          // PART_Popup reference for placement control
        private Control? _popupContent;            // inner Border of PART_Popup

        private bool   _compactMode   = false;
        private double _dropDownWidth = 155.0;
        private double _comboWidth    = 155.0;

        // ── Events / Properties ───────────────────────────────────────────────

        /// <summary>Fired whenever the user selects a different LUT.</summary>
        public event EventHandler<LookupTable?>? SelectedLutChanged;

        /// <summary>Currently selected <see cref="LookupTable"/>, or <c>null</c>.</summary>
        public LookupTable? SelectedLut => (_comboBox.SelectedItem as LutItem)?.Lut;

        /// <summary>A <see cref="WindowIcon"/> representing the currently selected LUT gradient.</summary>
        public WindowIcon? SelectedIcon => (_comboBox.SelectedItem as LutItem)?.Icon;

        /// <summary>
        /// When true, the closed ComboBox shows only the gradient colorbar;
        /// the dropdown popup still shows gradient + name at <see cref="DropDownWidth"/>.
        /// Default is <c>false</c>.
        /// </summary>
        public bool CompactMode
        {
            get => _compactMode;
            set { _compactMode = value; ApplyMode(); }
        }

        /// <summary>
        /// Minimum pixel width of the dropdown popup when <see cref="CompactMode"/> is active.
        /// Has no effect when <see cref="CompactMode"/> is false.
        /// </summary>
        public double DropDownWidth
        {
            get => _dropDownWidth;
            set { _dropDownWidth = value; UpdatePopupMinWidth(); }
        }

        /// <summary>
        /// Width of the ComboBox. Independent of <see cref="CompactMode"/>;
        /// only the displayed content (gradient only vs gradient + name) changes with the mode.
        /// Default is 155.
        /// </summary>
        public double ComboWidth
        {
            get => _comboWidth;
            set { _comboWidth = value; _comboBox.Width = Math.Max(0, value); }
        }

        // ── Static init: load external .mlut files once ──────────────────────

        static LutSelector()
        {
            LoadExternalLuts();
        }

        // ── Constructor ───────────────────────────────────────────────────────

        public LutSelector()
        {
            // ComboBox: shows mini gradient preview + name for each LUT
            _comboBox = new ComboBox
            {
                Width = 155,
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
                MaxDropDownHeight = 400,
                MinHeight = 20,
                Height = 20,
                FontSize = 11,
                Padding = new Thickness(5, 1, 1, 1),
            };

            // Dropdown item template: [mini colorbar (64px)] [name]
            _comboBox.ItemTemplate = new FuncDataTemplate<LutItem>(
                (item, _) => BuildDropDownItem(item),
                supportsRecycling: false);

            // Cache popup references and apply one-time template adjustments
            _comboBox.TemplateApplied += (_, e) =>
            {
                var popup = e.NameScope.Find<Popup>("PART_Popup");
                if (popup != null)
                {
                    _dropdownPopup = popup;
                    _popupContent  = popup.Child as Control;
                    UpdatePopupPlacement();
                    UpdatePopupMinWidth();
                }
                TryShrinkArrowColumn();
            };

            // Populate
            foreach (string name in ColorThemes.Names)
            {
                var lut = ColorThemes.Get(name);
                _comboBox.Items.Add(new LutItem(lut, CreatePreview(lut), CreateIcon(lut)));
            }

            if (_comboBox.Items.Count > 0)
                _comboBox.SelectedIndex = 0;

            _comboBox.SelectionChanged += (_, _) =>
            {
                SelectedLutChanged?.Invoke(this, SelectedLut);
                // Avalonia internally resets SelectionBoxItemTemplate to ItemTemplate on each
                // selection change; re-apply the compact template here to keep our override.
                if (_compactMode) _comboBox.SelectionBoxItemTemplate = s_compactSelTemplate;
                UpdateTooltip();
            };

            // Layout: [ LUT: ] [ ComboBox ]
            _lutLabel = new TextBlock
            {
                Text = "LUT:",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            };
            _lutLabel.PointerPressed += (_, _) =>
            {
                _comboBox.IsDropDownOpen = true;
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 2, 6, 2),
                Spacing = 6,
            };
            panel.Children.Add(_lutLabel);
            panel.Children.Add(_comboBox);

            Content = panel;
        }

        // ── Public helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Selects the item matching <paramref name="lut"/> by reference or name.
        /// Does nothing if no match is found.
        /// </summary>
        public void SelectLut(LookupTable? lut)
        {
            if (lut == null) return;
            foreach (var item in _comboBox.Items.OfType<LutItem>())
            {
                if (ReferenceEquals(item.Lut, lut) ||
                    string.Equals(item.Name, lut.Name, StringComparison.OrdinalIgnoreCase))
                {
                    _comboBox.SelectedItem = item;
                    return;
                }
            }
        }

        // ── Mode management ───────────────────────────────────────────────────

        // Compact selection template: fills the closed ComboBox with just the gradient.
        private static readonly FuncDataTemplate<LutItem> s_compactSelTemplate =
            new FuncDataTemplate<LutItem>(
                (item, _) => new Image
                {
                    Source = item?.Preview,
                    Height = 10,
                    Stretch = Stretch.Fill,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                },
                supportsRecycling: false);

        private void ApplyMode()
        {
            _comboBox.SelectionBoxItemTemplate = _compactMode
                ? s_compactSelTemplate
                : _comboBox.ItemTemplate;
            UpdatePopupMinWidth();
            UpdateTooltip();
        }

        private void UpdatePopupMinWidth()
        {
            if (_popupContent == null) return;
            _popupContent.MinWidth = _compactMode ? _dropDownWidth : 0;
        }

        /// <summary>Shows the selected LUT name as a tooltip on the ComboBox when in compact mode.</summary>
        private void UpdateTooltip()
        {
            string? name = SelectedLut?.Name;
            ToolTip.SetTip(_comboBox, _compactMode && name != null ? name : null);
        }

        /// <summary>Left-aligns the dropdown popup with the ComboBox's left edge.</summary>
        private void UpdatePopupPlacement()
        {
            if (_dropdownPopup == null) return;
            _dropdownPopup.PlacementMode    = PlacementMode.AnchorAndGravity;
            _dropdownPopup.PlacementAnchor  = PopupAnchor.BottomLeft;
            _dropdownPopup.PlacementGravity = PopupGravity.BottomRight;
        }

        /// <summary>
        /// Narrows the fixed-width chevron column of the Fluent-theme ComboBox template grid,
        /// giving more horizontal space to the colorbar at the same overall control width.
        /// No-op on themes with a different template structure.
        /// </summary>
        private void TryShrinkArrowColumn()
        {
            // Fluent theme: ComboBox content is a Grid with 2 columns (* = content, fixed = arrow)
            var grid = _comboBox.GetVisualDescendants()
                                .OfType<Grid>()
                                .FirstOrDefault(g => g.ColumnDefinitions.Count == 2);
            if (grid != null)
                grid.ColumnDefinitions[1].Width = new GridLength(20);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Builds a single dropdown row: [mini gradient (64px)] [name].</summary>
        private static Control BuildDropDownItem(LutItem item)
        {
            if (item == null)
            {
                return new TextBlock { Text = "null" };
            }

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                Margin = new Thickness(1, 1),
            };

            row.Children.Add(new Image
            {
                Source = item.Preview,
                Width = 58,
                Height = 10,
                Stretch = Stretch.Fill,
                VerticalAlignment = VerticalAlignment.Center,
            });

            row.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 11,
                MinHeight = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });

            return row;
        }

        private static WriteableBitmap CreatePreview(LookupTable lut)
        {
            var data = new MatrixData<byte>(PreviewW, PreviewH);
            var arr = data.GetArray();
            for (int ix = 0; ix < PreviewW; ix++)
                arr[ix] = (byte)ix;

            return BitmapWriter.CreateBitmap(data, frameIndex: 0, lut: lut,
                                             valueMin: 0, valueMax: 255);
        }

        /// <summary>Creates a 32×32 vertical-gradient icon for the given LUT.</summary>
        private static WindowIcon CreateIcon(LookupTable lut)
        {
            const int S = 32;
            var data = new MatrixData<byte>(S, S);
            var arr = data.GetArray();
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                    arr[y * S + x] = (byte)(y * 255 / (S - 1));

            using var bmp = BitmapWriter.CreateBitmap(data, 0, lut, valueMin: 0, valueMax: 255);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }

        private static void LoadExternalLuts()
        {
            string lutDir = Path.Combine(AppContext.BaseDirectory, "LUTs");
            if (!Directory.Exists(lutDir)) return;

            foreach (string file in Directory.EnumerateFiles(lutDir, "*.mlut",
                                                             SearchOption.AllDirectories))
            {
                var lut = ColorThemes.LoadFromFile(file);
                if (lut != null) ColorThemes.Register(lut);
                Debug.WriteLine(lut != null
                    ? $"[LutSelector.LoadExternalLuts] Loaded LUT '{lut.Name}' from '{file}'."
                    : $"[LutSelector.LoadExternalLuts] Failed to load LUT from '{file}'. File is malformed or incompatible.");
            }
        }
    }

}
