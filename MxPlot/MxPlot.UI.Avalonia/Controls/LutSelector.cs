using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

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
        private readonly Grid _comboWrapper; //Used for custom border of _comboBox
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

        /// <summary>
        /// Text shown by the label preceding the ComboBox. Default is <c>"LUT:"</c>; a host that
        /// repurposes this same control for a different meaning (e.g. MatrixPlotter's ColorCoded
        /// projection child window, where it picks a depth palette instead of a value→colour LUT)
        /// can override it so the two are not visually indistinguishable.
        /// </summary>
        public string LabelText
        {
            get => _lutLabel.Text ?? string.Empty;
            set => _lutLabel.Text = value;
        }

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
            set
            {
                _comboWidth = value;
                _comboBox.Width = Math.Max(0, value);
                _comboWrapper.Width = Math.Max(0, value);
            }
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
                BorderThickness = new Thickness(0),
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

            // Wrap ComboBox in a Grid and overlay a persistent border on top.
            // Fluent theme's ComboBox border disappears at small widths; this Border is always visible.
            var comboWrapper = new Grid();
            comboWrapper.Children.Add(_comboBox);
            comboWrapper.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 100, 100, 100)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2), // flat rectangle
                Background = Brushes.Transparent,
                IsHitTestVisible = false,
            });

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 2, 6, 2),
                Spacing = 6,
            };
            panel.Children.Add(_lutLabel);
            //panel.Children.Add(_comboBox);
            panel.Children.Add(comboWrapper);
            _comboWrapper = comboWrapper;
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

            return LutBitmapWriter.CreateBitmap(data, frameIndex: 0, lut: lut,
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

            using var bmp = LutBitmapWriter.CreateBitmap(data, 0, lut, valueMin: 0, valueMax: 255);
            using var ms = new MemoryStream();
            bmp.Save(ms);
            ms.Position = 0;
            return new WindowIcon(ms);
        }

        /// <summary>The documented spelling of the palette folder. Matched case-insensitively.</summary>
        private const string LutFolderName = "LUTs";

        private static void LoadExternalLuts()
        {
            foreach (string dir in EnumerateLutFolders())
            {
                int count = ColorThemes.LoadFromDirectory(dir);
                Debug.WriteLine($"[LutSelector.LoadExternalLuts] {count} LUT(s) from '{dir}'.");
            }
        }

        /// <summary>
        /// The places a <c>LUTs</c> folder is honoured, nearest first. Each root is only a place to
        /// look; what to do with the folder is <see cref="ColorThemes.LoadFromDirectory"/>'s job.
        /// <list type="number">
        ///   <item>Beside the running assembly - a Windows release folder, or a LUTs folder shipped
        ///         with the application.</item>
        ///   <item>Beside the <c>.app</c> on macOS, which is where the equivalent folder has to go:
        ///         inside <c>Contents/</c> it would be hidden from Finder, lost on every update, and
        ///         would break the bundle's ad-hoc code signature.</item>
        ///   <item>The working directory, which is the only one a script can use - a file-based app
        ///         (<c>dotnet run foo.cs</c>) runs out of a hashed temp build folder, so
        ///         <see cref="AppContext.BaseDirectory"/> is nowhere near the script.</item>
        /// </list>
        /// A user-wide location (<c>~/Library/Application Support</c>, <c>%APPDATA%</c>) is
        /// deliberately not searched: it would have to be created and documented before it could be
        /// found, which is a separate decision from honouring folders the user can already see.
        /// </summary>
        private static IEnumerable<string> EnumerateLutFolders()
        {
            // Linux is the only one of the three where two paths differing only in case are two
            // different directories.
            var seen = new HashSet<string>(OperatingSystem.IsLinux()
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase);

            foreach (string? root in new[]
            {
                AppContext.BaseDirectory,
                OperatingSystem.IsMacOS() ? ResolveAppBundleParent(AppContext.BaseDirectory) : null,
                Environment.CurrentDirectory,
            })
            {
                if (root == null) continue;
                string? folder = ResolveLutFolder(root);
                if (folder != null && seen.Add(folder)) yield return folder;
            }
        }

        /// <summary>
        /// Finds the <c>LUTs</c> subfolder of <paramref name="root"/>, whatever its casing, or
        /// <c>null</c>. The exact spelling is tried first - it is what the file system resolves for
        /// free on Windows and on a default (case-insensitive) APFS volume. The sweep after it is
        /// for Linux and case-sensitive APFS, where <c>luts</c> would otherwise be silently ignored.
        /// </summary>
        internal static string? ResolveLutFolder(string root)
        {
            if (!Directory.Exists(root)) return null;

            string exact = Path.Combine(root, LutFolderName);
            if (Directory.Exists(exact)) return exact;

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (Path.GetFileName(dir).Equals(LutFolderName, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            return null;
        }

        /// <summary>
        /// Given the directory an app bundle runs from (<c>Foo.app/Contents/MacOS</c>), returns the
        /// directory the bundle itself sits in - so a <c>LUTs</c> folder can live next to
        /// <c>MxPlot.app</c> the same way it lives next to <c>MxPlot.exe</c> on Windows. Returns
        /// <c>null</c> for anything that is not that structure.
        /// <para>
        /// The <c>Info.plist</c> check is what distinguishes a real bundle from a directory that
        /// merely happens to be three levels deep. Takes the path rather than reading
        /// <see cref="AppContext.BaseDirectory"/> itself so the structure test is exercisable off
        /// macOS; the caller applies the platform guard.
        /// </para>
        /// </summary>
        internal static string? ResolveAppBundleParent(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

            var macOsDir = new DirectoryInfo(baseDirectory);
            var contents = macOsDir.Parent;
            var bundle = contents?.Parent;
            if (contents == null || bundle == null) return null;

            if (!macOsDir.Name.Equals("MacOS", StringComparison.Ordinal)) return null;
            if (!contents.Name.Equals("Contents", StringComparison.Ordinal)) return null;
            if (!bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(Path.Combine(contents.FullName, "Info.plist"))) return null;

            return bundle.Parent?.FullName;
        }
    }

}
