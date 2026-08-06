using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.Utils;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;

namespace MxPlot.UI.Avalonia.UITest
{

    public enum RenderingMode
    {
        Lut,
        Composite,
        ColorCodedAxis
    }

    public class MatrixPlotterCompUITest : Window
    {
        // ═══════════════════════════════════════════════════════════════════════
        // Strict size constants — NO magic numbers allowed
        // ═══════════════════════════════════════════════════════════════════════
        private const double RowHeight = 28;
        private const double ControlHeight = 20;
        private const double BaseFontSize = 11;
        private const double FontSizeSmall = 10;
        private const double ToggleBtnFontSize = 18;
        private const double IconBtnSize = 22;
        private const double MinMaxBoxWidth = 64;
        private const double GainBoxWidth = 48;
        private const double GainSliderWidth = 80;
        private const double ItemSpacing = 4;
        private const double RowPaddingH = 5;
        private const double ModeBtnWidth = 52;
        private const double SparklineWidth = 120;
        private const double LabelWidth = 50;

        // ── 1. 画面に固定された「入れ物（コンテナ）」の参照 ──
        // 中身のUIを差し替えるための「的」として、フィールドに保持しておく必要があります。
        private ContentControl _headerContainer;
        private ContentControl _detailsContainer;

        // ── 2. 「各モードのUI実体」を保管する倉庫（キャッシュ） ──
        private readonly Dictionary<RenderingMode, (Control Header, Control Details)> _toolBars = new();

        //── MatrixPlotterから流用（internal化したメソッドが書き込むフィールド）──
        private ValueRangeBar _rangeBar;           // _valueRangeBar → _rangeBar に統一
        private LutSelector _lutSelector;
        private Button _settingsBtn;
        private Border _settingsPanel;
        private HistogramPlotControl _histogramPlot;
        private Button _hamburgerBtn;
        private Button _lutVrRevertBtn;
        private TextBlock _dirtyBadge;
        private TextBlock _infoText;
        private TextBlock _virtualBadge;
        private TextBlock _zoomText;
        private TextBlock _noticeText;
        private TextBlock _progressSep;
        private TextBlock _progressText;
        private ProgressBar _progressBar;

        private ToggleButton? _invertLutChk;
        private NumericUpDown? _levelNud;
        private RadioButton? _autoRadio;
        private RadioButton? _fixedRadio;
        private RadioButton? _allRadio;           // multi-frame only
        private RadioButton? _roiRadio;           // shown only when ROI overlay is designated
        private PathIcon? _allWarningIcon;     // ⚠ shown when All range is imperfect

        // ── TrackerPanel（MatrixPlotterと同じ構造）──
        private Dictionary<string, AxisTracker> _axisTrackers = [];
        private StackPanel _trackerPanel;

        // ── Composite専用（新規）──
        private Border _compositePanel;
        private Button _compositeToggleBtn;

        // ── OrthogonalPanel（ダミーView用）──
        private OrthogonalPanel _orthoPanel;
        private MxView _view;

        // ── 現在のMatrixData── 
        // MatrixPlotterでは、SetMatrixData(data)で内部が更新され、UIが再構築される。
        private IMatrixData? _currentData;

        public MatrixPlotterCompUITest()
        {
            Title = "MatrixPlotter UI Test — Composite Setting Panel Prototype";
            Width = 420;
            Height = 480;

            // ── TrackerPanel（MatrixPlotterと同じ構造）──
            _trackerPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                IsVisible = true,
                Margin = new Thickness(0, 1, 0, 1),
            };

            // ── OrthogonalPanel + ダミーデータ──
            _orthoPanel = new OrthogonalPanel();
            _view = _orthoPanel.MainView;

            var dummyData = new MatrixData<ushort>(Scale2D.Pixels(64, 64),
                new ColorChannel("Ch1", "Ch2", "Ch3"),
                Axis.Z(40, 0, 1),
                Axis.Time(11, 0, 5));
            _view.MatrixData = dummyData;
            _currentData = dummyData; //本来は_view.MatrixData が更新されたときに SetMatrixData() が呼ばれるが、ここではダミーなので直接代入する

            // ── AxisTracker（ダミー軸から生成）──
            foreach (var axis in dummyData.Axes)
            {
                var tracker = new AxisTracker(axis);
                _trackerPanel.Children.Add(tracker);
                _axisTrackers[axis.Name] = tracker;
            }
            _orthoPanel.SetTopPanel(_trackerPanel);

            var topArea = BuildTopArea();
            var statusBar = BuildStatusBar();     // internal化したMatrixPlotterのメソッド

            var dock = new DockPanel();
            DockPanel.SetDock(topArea, Dock.Top);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            dock.Children.Add(topArea);
            dock.Children.Add(statusBar);
            //dock.Children.Add(_orthoPanel); // LastChildFill

            // ── ここから追加（_orthoPanel の上にテスト用UIを重ねる） ──
            var orthoContainer = new Grid();
            orthoContainer.Children.Add(_orthoPanel);

            // 右下に配置するテスト用ラジオボタンパネル
            var testModePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(10),
                Spacing = 10,
                Background = new SolidColorBrush(Color.FromRgb(100, 100, 100)), // 視認性確保のための半透明黒
            };

            //
            // モード切り替えテスト用の簡易UI
            //
            var rbLut = new RadioButton { Content = "LUT", Foreground = Brushes.White, IsChecked = true };
            var rbComp = new RadioButton { Content = "Composite", Foreground = Brushes.White };
            var rbColor = new RadioButton { Content = "ColorCoded", Foreground = Brushes.White };

            // モード切り替えイベントの紐付け
            rbLut.Checked += (_, _) => SwitchRenderingMode(RenderingMode.Lut);
            rbComp.Checked += (_, _) => SwitchRenderingMode(RenderingMode.Composite);
            rbColor.Checked += (_, _) => SwitchRenderingMode(RenderingMode.ColorCodedAxis);

            testModePanel.Children.Add(rbLut);
            testModePanel.Children.Add(rbComp);
            testModePanel.Children.Add(rbColor);

            orthoContainer.Children.Add(testModePanel);
            // _orthoPanel の代わりに orthoContainer を DockPanel の余白(Fill)に詰める
            dock.Children.Add(orthoContainer);

            Content = new Border { Child = dock };

            // ── 5. 初期モードのセット ──
            // アプリ起動時に初めてComposite用のUIが lazy 生成され、コンテナにセットされます
            SwitchRenderingMode(RenderingMode.Lut);


            topArea.SizeChanged += (_, e) =>
            {
                if (_lutSelector == null || !_lutSelector.IsVisible)
                {
                    return;
                }
                const double fixedW = 26 + 22 + 22 + 4;
                const double labelW = 42;
                const double rangeMin = 240;
                double comboW = Math.Clamp(e.NewSize.Width - fixedW - labelW - rangeMin, 0, 97);
                _lutSelector.ComboWidth = comboW;
            };
        }

        /// <summary>
        /// モード共通のベースレイアウト（1行目の ☰、▼、および入れ物の作成）
        /// </summary>
        private Control BuildTopArea()
        {
            var topAreaStack = new StackPanel { Orientation = Orientation.Vertical };
            var firstRow = new DockPanel { LastChildFill = true };

            // 左端固定のハンバーガーボタン
            _hamburgerBtn = new Button
            {
                Content = "☰",
                Width = 26,
                Height = 20,
                FontSize = 14,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            DockPanel.SetDock(_hamburgerBtn, Dock.Left);

            // 右端固定の展開ボタン
            _settingsBtn = new Button
            {
                Content = "▾",
                Width = 22,
                Height = 20,
                MinHeight = 20,
                FontSize = 18,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Margin = new Thickness(4, 0),
            };
            DockPanel.SetDock(_settingsBtn, Dock.Right);

            // 🌟 ここで「空の入れ物」を作成し、フィールドに参照を保持する
            _headerContainer = new ContentControl { VerticalAlignment = VerticalAlignment.Stretch };
            _detailsContainer = new ContentControl
            {
                IsVisible = false,
                Padding = new Thickness(30, 0, 0, 0),
            };

            firstRow.Children.Add(_hamburgerBtn);
            firstRow.Children.Add(_settingsBtn);
            firstRow.Children.Add(_headerContainer); // 残りの隙間(Fill)をヘッダーの入れ物にする

            topAreaStack.Children.Add(firstRow);
            topAreaStack.Children.Add(new Border
            {
                Child = _detailsContainer,
                //BorderBrush = Brushes.Gray,
                //BorderThickness = new Thickness(0, 0, 0, 1),
            });

            // ▼ボタンの開閉ロジック（中身が何モードであっても共通で機能する）
            _settingsBtn.Click += (_, _) =>
            {
                // ウィンドウが最大化されている場合は、サイズ変更を行わずに表示切り替えのみ
                if (WindowState == WindowState.Maximized)
                {
                    bool op = !_detailsContainer.IsVisible;
                    _detailsContainer.IsVisible = op;
                    _settingsBtn.Content = op ? "▴" : "▾";
                    _settingsBtn.Background = op ? Brushes.LightGray : Brushes.Transparent;
                    return;
                }

                bool opening = !_detailsContainer.IsVisible;
                double panelH = _detailsContainer.Bounds.Height; // 現在（閉じる直前）の高さ

                if (!opening)
                {
                    // ── 閉じる時の処理 ──
                    _detailsContainer.IsVisible = false;
                    _settingsBtn.Content = "▾";
                    _settingsBtn.Background = Brushes.Transparent;

                    if (panelH > 0)
                        Height -= panelH; // 詳細パネルの分だけウィンドウを縮める
                }
                else
                {
                    // ── 開く時の処理 ──
                    _detailsContainer.IsVisible = true;
                    _settingsBtn.Content = "▴";
                    _settingsBtn.Background = Brushes.LightGray;

                    // IsVisible = true にした直後はまだ描画（レイアウト計算）が終わっておらず
                    // 高さが 0 になるため、Dispatcherを使って一瞬待ってから高さを計測してウィンドウを広げる
                    Dispatcher.UIThread.Post(() =>
                    {
                        double h = _detailsContainer.Bounds.Height;
                        if (h > 0)
                            Height += h; // 確定した中身の分だけウィンドウを広げる
                    }, DispatcherPriority.Background);
                }
            };

            return topAreaStack;
        }

        /// <summary>
        /// 指定されたモードのUIを倉庫から取り出し、コンテナにセットする
        /// </summary>
        public void SwitchRenderingMode(RenderingMode mode)
        {
            // 倉庫になければ生成して保管する
            if (!_toolBars.TryGetValue(mode, out var toolBar))
            {
                toolBar = mode switch
                {
                    RenderingMode.Lut => (BuildLutModeHeader(), BuildLutModeDetails()),
                    RenderingMode.Composite => (BuildCompositeModeHeader(), BuildCompositeModeDetails()),
                    RenderingMode.ColorCodedAxis => (new Border(), new Border()), // 将来用
                    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
                };
                _toolBars[mode] = toolBar;
            }

            // 🌟 画面に固定されたコンテナ（的）に中身を流し込む
            _headerContainer.Content = toolBar.Header;
            _detailsContainer.Content = toolBar.Details;
        }

        private Control BuildLutModeHeader()
        {
            _lutSelector = new LutSelector
            {
                CompactMode = true,
                ComboWidth = 97,
            };

            _lutVrRevertBtn = new Button
            {
                Content = new PathIcon
                {
                    Data = MenuIcons.Undo,
                    Width = 12,
                    Height = 12,
                    Foreground = MenuIcons.DefaultBrush(MenuIcons.Undo),
                },
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1, 0),
                IsVisible = true,
            };
            ToolTip.SetTip(_lutVrRevertBtn, "Revert LUT / Value Range to initial settings");

            _rangeBar = new ValueRangeBar();
            // Test: simulate multi-frame data with imperfect All-mode range
            _rangeBar.SetMultiFrame(true);
            _rangeBar.SetImperfect(true, 22);  // 22 frames not yet scanned

            var topRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_lutVrRevertBtn, Dock.Right);
            DockPanel.SetDock(_lutSelector, Dock.Left);
            topRow.Children.Add(_lutSelector);
            topRow.Children.Add(_lutVrRevertBtn);
            topRow.Children.Add(_rangeBar);

            // Shrink LUT ComboBox first; only after it reaches 0 does ValueRangeBar compress.
            // fixedW   = hamburger(26) + settingsBtn(22) + lutVrRevertBtn(22) + buffer(4)
            // labelW   = "LUT:" label + spacing + panel margins ≈ 42 px
            // rangeMin = ValueRangeBar minimum (both min/max boxes at MinBoxWidth=36)
            topRow.SizeChanged += (_, e) =>
            {
                const double fixedW = 26 + 22 + 22 + 4;
                const double labelW = 42;
                const double rangeMin = 240;
                double comboW = Math.Clamp(e.NewSize.Width - fixedW - labelW - rangeMin, 0, 97);
                _lutSelector.ComboWidth = comboW;
            };

            return topRow;
        }

        private Control BuildLutModeDetails()
        {
            _levelNud = new NumericUpDown
            {
                Minimum = 2,
                Maximum = 4096,
                Value = 256,
                Increment = 1,
                Width = 60,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(4, 0),
            };
            _levelNud.Classes.Add("compact");

            _invertLutChk = new ToggleButton
            {
                // ◑ half-filled circle: circle outline (Stroke) + right semicircle (Fill).
                // PathIcon supports only Fill, so two overlaid Path elements are used.
                Content = new PathIcon
                {
                    Data = Geometry.Parse(
                        "F1 " +
                        "M 8,1 A 7,7 0 0,1 8,15 A 7,7 0 0,1 8,1 Z " +
                        "M 8,2 A 6,6 0 0,0 8,14 L 8,2 Z"),
                    Width = 14,
                    Height = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(5, 0),
            };
            ToolTip.SetTip(_invertLutChk, "Invert LUT");

            _fixedRadio = new RadioButton
            {
                Content = "Fixed",
                GroupName = "VRMode",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
            };
            _fixedRadio.Classes.Add("compact");
            ToolTip.SetTip(_fixedRadio, "Fixed: user-specified numeric min/max");

            _autoRadio = new RadioButton
            {
                Content = "Auto",
                GroupName = "VRMode",
                IsChecked = true,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
            };
            _autoRadio.Classes.Add("compact");
            ToolTip.SetTip(_autoRadio, "Current frame min/max (automatic)");

            // ⚠ warning icon: filled triangle (CW) with ! cutouts (CCW)
            _allWarningIcon = new PathIcon
            {
                Data = Geometry.Parse(
                    "M 7,0 L 14,12 L 0,12 Z " +
                    "M 6.3,3.5 L 6.3,8 L 7.7,8 L 7.7,3.5 Z " +
                    "M 6.3,9.5 L 6.3,11 L 7.7,11 L 7.7,9.5 Z"),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 0)),
                Width = 10,
                Height = 10,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            var allLabel = new StackPanel { Orientation = Orientation.Horizontal };
            allLabel.Children.Add(new TextBlock
            {
                Text = "All",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            allLabel.Children.Add(_allWarningIcon);

            _allRadio = new RadioButton
            {
                Content = allLabel,
                GroupName = "VRMode",
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                IsVisible = false, // shown only in multi-frame mode
            };
            _allRadio.Classes.Add("compact");
            ToolTip.SetTip(_allRadio, "Global min/max across all frames");

            _roiRadio = new RadioButton
            {
                Content = "ROI",
                GroupName = "VRMode",
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                IsVisible = false, // shown only when an ROI overlay is designated
            };
            _roiRadio.Classes.Add("compact");
            ToolTip.SetTip(_roiRadio, "Value range from designated ROI overlay");

            var settingsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(2, 2, 10, 2),
            };
            settingsRow.Children.Add(new TextBlock
            {
                Text = "Level:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            settingsRow.Children.Add(_levelNud);
            settingsRow.Children.Add(_invertLutChk);
            settingsRow.Children.Add(new Border
            {
                Width = 1,
                Background = Brushes.Gray,
                Margin = new Thickness(5, 3),
            });
            //------------------To be replaced with HistogramPlotControl
            var dummy = new MatrixData<double>(256, 256);
            dummy.Set((ix, iy, x, y) => Random.Shared.NextDouble() * x);
            
            var lut = _lutSelector.SelectedLut;
            int level = lut?.Levels ?? 256;
            var aRgbs = lut?.AsSpan().ToArray() ?? new int[level];
            int[] histogram = dummy.CreateHistogram(bins: level);

            // ------------------HistogramPlotControl
            _histogramPlot = new HistogramPlotControl
            {
                Width = 200,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _histogramPlot.SetHistogram(histogram, valueMin: 0.0, valueMax: 1000.0, 
                viewMin: 0.0, viewMax: 1000.0, aRgbs: aRgbs, preservePlotWindow: false);
            //_histogramPlot.SetViewValueRange(-500, 1200); // 赤線テスト用
            
            _lutSelector.SelectedLutChanged += (sender, args) =>
            {
                _histogramPlot?.SetLut(_lutSelector?.SelectedLut?.AsSpan().ToArray());
            };

            settingsRow.Children.Add(_histogramPlot);

            /*
            settingsRow.Children.Add(new TextBlock
            {
                Text = "Value range:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            settingsRow.Children.Add(_fixedRadio);
            settingsRow.Children.Add(_autoRadio);
            settingsRow.Children.Add(_allRadio);
            settingsRow.Children.Add(_roiRadio);
            */

            return new Border
            {
                Child = settingsRow,
                //BorderBrush = Brushes.Gray,
                //BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = true,
            };
        }

        private ColorChannel? ExtractColorChannel(IMatrixData data)
        {
            if (data == null) return null;

            var channels = data["Channel"] ?? data.Axes.First(a => a is ColorChannel);
            if(channels is null)  return null;
            if (channels is ColorChannel cc)
            {
                return cc;
            }
            else
            {
                //Upgrade to ColorChannel from a normal axis    
                var tags = new string[channels.Count];
                for (int i = 0; i < channels.Count; i++)
                {
                    tags[i] = $"Ch{i + 1}";
                }
                var c2 = new ColorChannel(tags);
                return c2;
            }
        }

        private Control BuildCompositeModeHeader()
        {
            //Check availability of composite mode (Channel axis or ColorChannel axis must exist)
            if(_currentData is null) throw new InvalidOperationException("No data available to render as composite mode.");
            ColorChannel chAxis = ExtractColorChannel(_currentData) ?? throw new InvalidOperationException("No axis available for composite mode.");

            var compositeMenuBtn = new Button
            {
                Height = ControlHeight,
                MinHeight = 0,
                Padding = new Thickness(6, 0),
                Margin = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Composite",
                            FontSize = BaseFontSize,
                            VerticalAlignment = VerticalAlignment.Center,
                        }
                    },
                },
            };
            ToolTip.SetTip(compositeMenuBtn, "Composite settings / Switch to LUT mode");

            // Flyout
            var globalRadio = new RadioButton
            {
                Content = "Global",
                GroupName = "CompositeModeGroup",
                FontSize = BaseFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
            };
            globalRadio.Classes.Add("compact");

            var channelWiseRadio = new RadioButton
            {
                Content = "Channel-wise",
                GroupName = "CompositeModeGroup",
                IsChecked = true,
                FontSize = BaseFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
            };
            channelWiseRadio.Classes.Add("compact");

            var switchToLutBtn = ControlFactory.MakeMenuItem(
                "Switch to LUT mode", () => { /* TODO: スワップ処理 */ },
                fontSize: 8
                );

            var flyoutContent = new StackPanel
            {
                Spacing = 2,
                Margin = new Thickness(4),
                Children =
        {
            new TextBlock
            {
                Text = "Color Setting",
                FontSize = BaseFontSize,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(2, 2),
            },
            globalRadio,
            channelWiseRadio,
            ControlFactory.MakeSep(),
            switchToLutBtn,
        },
            };
            var flyout = new Flyout
            {
                Content = flyoutContent,
                Placement = PlacementMode.BottomEdgeAlignedLeft,
            };
            FlyoutBase.SetAttachedFlyout(compositeMenuBtn, flyout);
            compositeMenuBtn.Click += (_, _) =>
                FlyoutBase.ShowAttachedFlyout(compositeMenuBtn);

            // ── Ch-tag トグルボタン + 各行の参照を保持 ──────────────────────────
            var chTagPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = ItemSpacing,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var channelDefs = new[]
            {
                ("Ch1", Colors.Cyan),
                ("Ch2", Colors.Magenta),
                ("Ch3", Colors.Yellow),
            };

            // チャンネル行とトグルボタンの参照を保持（後でワイヤリング）
            _channelToggleButtons = new ToggleButton[channelDefs.Length];
            _channelRowControls = new Control[channelDefs.Length];

            for (int i = 0; i < channelDefs.Length; i++)
            {
                var (name, color) = channelDefs[i];
                var tag = new ToggleButton
                {
                    Content = name,
                    IsChecked = true,
                    Height = ControlHeight,
                    MinHeight = 0,
                    FontSize = FontSizeSmall,
                    Padding = new Thickness(6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(color),
                    BorderThickness = new Thickness(1),
                    Tag = i, // インデックスを保存
                };
                _channelToggleButtons[i] = tag;
                chTagPanel.Children.Add(tag);
            }

            // ヘッダー行アセンブル
            var headerRow = new DockPanel
            {
                Height = RowHeight,
                Margin = new Thickness(RowPaddingH, 0),
                LastChildFill = true,
            };
            DockPanel.SetDock(compositeMenuBtn, Dock.Left);
            headerRow.Children.Add(compositeMenuBtn);
            headerRow.Children.Add(chTagPanel);

            return headerRow;
        }

        // チャンネル行とトグルボタンの参照を保持
        private ToggleButton[]? _channelToggleButtons;
        private Control[]? _channelRowControls;

        private Control BuildCompositeModeDetails()
        {
            var channelDefs = new[]
            {
                ("Ch1", Colors.Cyan),
                ("Ch2", Colors.Magenta),
                ("Ch3", Colors.Yellow),
            };

            var detailPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 0,
            };

            for (int i = 0; i < channelDefs.Length; i++)
            {
                var (name, color) = channelDefs[i];
                var row = BuildChannelRow(name, color);
                _channelRowControls![i] = row; // 参照を保存
                detailPanel.Children.Add(row);
                detailPanel.Children.Add(ControlFactory.MakeSep(new Thickness(0)));
            }

            // ── イベントワイヤリング（トグルボタン ↔ チャンネル行） ──
            WireChannelToggleEvents();

            return new Border
            {
                Child = detailPanel,
            };
        }

        private void WireChannelToggleEvents()
        {
            if (_channelToggleButtons == null || _channelRowControls == null) return;

            for (int i = 0; i < _channelToggleButtons.Length; i++)
            {
                var toggle = _channelToggleButtons[i];
                var row = _channelRowControls[i];
                var channelDefs = new[] { Colors.Cyan, Colors.Magenta, Colors.Yellow };
                var color = channelDefs[i];

                toggle.IsCheckedChanged += (_, _) =>
                {
                    bool isEnabled = toggle.IsChecked == true;

                    // トグルボタンの背景色を切り替え
                    toggle.Background = isEnabled
                        ? new SolidColorBrush(color)
                        : new SolidColorBrush(Color.FromArgb(80, 128, 128, 128));

                    // チャンネル行全体のグレーアウト
                    row.Opacity = isEnabled ? 1.0 : 0.4;
                    row.IsEnabled = isEnabled;
                };
            }
        }

        // ── 各チャンネル行 ──────────────────────────────────────────────────
        private Border BuildChannelRow(string channelName, Color channelColor)
        {
            // ── チャンネル名ラベル（AxisTrackerのnameLabelと同じ）──
            var nameLabel = new TextBlock
            {
                Text = channelName,
                Width = LabelWidth,
                FontSize = BaseFontSize,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 6, 0),
            };

            // 色変更時にヒストグラムを更新するため、外部でアクセス可能にする
            HistogramPlotControl? histogramPlotRef = null;

            var swatch = ControlFactory.MakeColorSwatch(channelColor, newColor =>
            {
                channelColor = newColor;
                if (histogramPlotRef != null)
                {
                    var newLut = GenerateChannelLut(channelColor);
                    histogramPlotRef.SetLut(newLut);
                }
            }, showAlpha: false);

            var modeBtn = new Button
            {
                Content = "Auto",
                Width = ModeBtnWidth,
                Height = ControlHeight,
                MinHeight = 0,
                FontSize = FontSizeSmall,
                Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 160, 160, 160)),
                BorderThickness = new Thickness(1),
            };

            var minLabel = new TextBlock
            {
                Text = "Min",
                FontSize = BaseFontSize,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var minBox = new TextBox
            {
                Width = MinMaxBoxWidth,
                Height = ControlHeight,
                MinHeight = 0,
                MinWidth = 0,
                Text = "120",
                FontSize = FontSizeSmall,
                IsReadOnly = true,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0),
            };
            minBox.Classes.Add("grayed");

            var searchMinBtn = new Button
            {
                Width = IconBtnSize,
                Height = ControlHeight,
                MinHeight = 0,
                Padding = new Thickness(0),
                IsEnabled = false,
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new TextBlock
                {
                    Text = "↓",
                    FontSize = BaseFontSize,
                    VerticalAlignment = VerticalAlignment.Center
                },
            };

            var maxLabel = new TextBlock
            {
                Text = "Max",
                FontSize = BaseFontSize,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var maxBox = new TextBox
            {
                Width = MinMaxBoxWidth,
                Height = ControlHeight,
                MinHeight = 0,
                MinWidth = 0,
                Text = "2048",
                FontSize = FontSizeSmall,
                IsReadOnly = true,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0),
            };
            maxBox.Classes.Add("grayed");

            var searchMaxBtn = new Button
            {
                Width = IconBtnSize,
                Height = ControlHeight,
                MinHeight = 0,
                Padding = new Thickness(0),
                IsEnabled = false,
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = new TextBlock
                {
                    Text = "↑",
                    FontSize = BaseFontSize,
                    VerticalAlignment = VerticalAlignment.Center
                },
            };

            // ── Histogram（Star列で伸びる、チャンネル個別のviewMin/viewMax調整）──
            var histogramPlot = new HistogramPlotControl
            {
                MinWidth = 50,  // レイアウト完了前のクラッシュを防ぐ最小幅
                Height = ControlHeight,
                MinHeight = 0,
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 外部から参照できるよう設定
            histogramPlotRef = histogramPlot;

            // ダミーヒストグラムデータ生成
            var bins = new int[256];
            var rand = new Random(channelName.GetHashCode());
            for (int i = 0; i < bins.Length; i++)
                bins[i] = (int)(Math.Sin(i / 40.0) * 50 + 50 + rand.Next(20));

            // チャンネルカラーベースのグラデーションLUT生成
            int[] GenerateChannelLut(Color baseColor)
            {
                var lut = new int[256];
                for (int i = 0; i < 256; i++)
                {
                    byte intensity = (byte)i;
                    var gradColor = Color.FromArgb(255,
                        (byte)(baseColor.R * intensity / 255),
                        (byte)(baseColor.G * intensity / 255),
                        (byte)(baseColor.B * intensity / 255));
                    lut[i] = (int)gradColor.ToUInt32();
                }
                return lut;
            }

            var channelLut = GenerateChannelLut(channelColor);

            // 初期ヒストグラム設定
            histogramPlot.SetHistogram(
                bins: bins,
                valueMin: 0,
                valueMax: 4095,  // 12bit想定
                viewMin: double.Parse(minBox.Text),
                viewMax: double.Parse(maxBox.Text),
                aRgbs: channelLut
            );

            // ViewRangeChanged: ヒストグラムのドラッグ → TextBox更新
            histogramPlot.ViewRangeChanged += (min, max) =>
            {
                minBox.Text = $"{min:F0}";
                maxBox.Text = $"{max:F0}";
            };

            // TextBox変更 → ヒストグラムの赤線更新
            minBox.TextChanged += (_, _) =>
            {
                if (double.TryParse(minBox.Text, out double min))
                    histogramPlot.SetViewValueRange(min, histogramPlot.ViewMax);
            };

            maxBox.TextChanged += (_, _) =>
            {
                if (double.TryParse(maxBox.Text, out double max))
                    histogramPlot.SetViewValueRange(histogramPlot.ViewMin, max);
            };

            var gainLabel = new TextBlock
            {
                Text = "Gain:",
                FontSize = BaseFontSize,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var gainSlider = new Slider
            {
                Minimum = 0,
                Maximum = 5,
                Value = 1.0,
                VerticalAlignment = VerticalAlignment.Center,
                Height = ControlHeight,
                MinHeight = 0,
            };
            gainSlider.TemplateApplied += (_, e) =>
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
                    if (track.IncreaseButton is Control inc) inc.Height = 1;
                    if (track.DecreaseButton is Control dec) dec.Height = 1;
                }
            };

            var gainBox = new TextBox
            {
                Width = GainBoxWidth,
                Height = ControlHeight,
                MinHeight = 0,
                MinWidth = 0,
                Text = "1.0×",
                FontSize = FontSizeSmall,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(4, 0),
            };

            gainSlider.ValueChanged += (_, _) =>
                gainBox.Text = $"{gainSlider.Value:F1}×";

            // ── Grid（AxisTracker方式）──
            var grid = new Grid
            {
                Margin = new Thickness(RowPaddingH, 0),
                Height = RowHeight,
                ColumnSpacing = ItemSpacing,
            };

            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 0: nameLabel
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 1: swatch
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 2: modeBtn
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 3: "Min"
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 4: minBox
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 5: searchMinBtn
            grid.ColumnDefinitions.Add(new ColumnDefinition(2, GridUnitType.Star));         // 6: histogramPlot ★
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 7: "Max" ← 追加
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 8: maxBox
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 9: searchMaxBtn
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 10: "Gain:"
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));         // 11: gainSlider ★
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));              // 12: gainBox

            Grid.SetColumn(nameLabel, 0);
            Grid.SetColumn(swatch, 1);
            Grid.SetColumn(modeBtn, 2);
            Grid.SetColumn(minLabel, 3);
            Grid.SetColumn(minBox, 4);
            Grid.SetColumn(searchMinBtn, 5);
            Grid.SetColumn(histogramPlot, 6);
            Grid.SetColumn(maxLabel, 7);
            Grid.SetColumn(maxBox, 8);
            Grid.SetColumn(searchMaxBtn, 9);
            Grid.SetColumn(gainLabel, 10);
            Grid.SetColumn(gainSlider, 11);
            Grid.SetColumn(gainBox, 12);

            grid.Children.Add(nameLabel);
            grid.Children.Add(swatch);
            grid.Children.Add(modeBtn);
            grid.Children.Add(minLabel);
            grid.Children.Add(minBox);
            grid.Children.Add(searchMinBtn);
            grid.Children.Add(histogramPlot);
            grid.Children.Add(maxLabel);
            grid.Children.Add(maxBox);
            grid.Children.Add(searchMaxBtn);
            grid.Children.Add(gainLabel);
            grid.Children.Add(gainSlider);
            grid.Children.Add(gainBox);

            return new Border
            {
                Child = grid,
                Padding = new Thickness(0, 2),
            };
        }
        //-------------------------------------------------------------------------------

        

        /// <summary>
        /// Dummy status bar (Bottom)
        /// </summary>
        /// <returns></returns>
        private Border BuildStatusBar()
        {
            _dirtyBadge = new TextBlock
            {
                Text = "*",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 160, 60)),
                Margin = new Thickness(4, 0, 0, 0),
                IsVisible = false,
            };

            _infoText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
                Text = "[ushort] xxx xxxx |"
            };

            _virtualBadge = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DodgerBlue,
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(2, 0),
                IsVisible = false,
            };


            _zoomText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
                [ToolTip.TipProperty] = "Click to set zoom / output size",
            };
            _zoomText.PointerPressed += (_, e) =>
            {
            };

            _progressSep = new TextBlock
            {
                Text = "|",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                Margin = new Thickness(2, 0),
                IsVisible = false,
            };

            _noticeText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 2, 0),
                Opacity = 0.7,
                IsVisible = false,
            };

            _progressText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                IsVisible = false,
            };

            _progressBar = new ProgressBar
            {
                Width = 100,
                Height = 4,
                MinHeight = 0,
                Minimum = 0,
                Maximum = 100,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                IsVisible = false,
            };

            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal };
            statusPanel.Children.Add(_dirtyBadge);
            statusPanel.Children.Add(_infoText);
            statusPanel.Children.Add(_virtualBadge);
            statusPanel.Children.Add(_zoomText);
            statusPanel.Children.Add(_noticeText);
            statusPanel.Children.Add(_progressSep);
            statusPanel.Children.Add(_progressBar);
            statusPanel.Children.Add(_progressText);

            return new Border
            {
                Child = statusPanel,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 2),
            };
        }

    }
}
