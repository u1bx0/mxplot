using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Tabbed property editor for overlay objects.
    /// <list type="bullet">
    ///   <item><b>Geometry</b> — position / size in Pixel (data-index) or Scale (physical) coordinates.</item>
    ///   <item><b>Target</b> — style and centre gap (<see cref="TargetingObject"/> only).</item>
    ///   <item><b>Profile</b> — origin and interpolation of the line profile (<see cref="LineObject"/> only).</item>
    ///   <item><b>Pen</b> — colour, stroke width, dash style, fill.</item>
    /// </list>
    /// All changes are applied live; close the window to dismiss.
    /// </summary>
    internal sealed class OverlayPropertyDialog : Window
    {
        private readonly OverlayObjectBase _target;
        private readonly IMatrixData? _data;
        private readonly Action _redraw;

        // Geometry tab state
        private readonly NumericUpDown[] _geoNuds;
        private readonly TextBlock[] _geoUnits;
        private readonly TextBlock[] _geoLabels;
        private readonly Control[] _geoRows;
        // Target tab: hosts the style selector and the rows (e.g. Gap) that belong to it.
        private StackPanel? _targetPanel;
        private readonly FieldKind[] _geoKinds;
        private bool _isPixelMode = true;
        private bool _suppressSync;
        // Stores the last non-degenerate line direction so that Length can restore
        // a zero-length line without losing orientation.
        private double _lastLineAngleRad;

        // Profile tab state (LineObject only)
        private RadioButton? _originLeftRadio;
        private RadioButton? _originCenterRadio;
        private RadioButton? _originCustomRadio;
        private NumericUpDown? _originNud;
        private ComboBox? _interpCombo;
        private bool _suppressProfileSync;

        private enum FieldKind { PosX, PosY, CenterX, CenterY, SizeX, SizeY, Length, Gap }

        public OverlayPropertyDialog(OverlayObjectBase target, IMatrixData? data, Action redraw)
        {
            _target = target;
            _data = data;
            _redraw = redraw;

            string[] labels;
            if (target is LineObject)
            {
                labels = ["P1 X:", "P1 Y:", "P2 X:", "P2 Y:", "Len:"];
                _geoKinds = [FieldKind.PosX, FieldKind.PosY, FieldKind.PosX, FieldKind.PosY, FieldKind.Length];
            }
            else if (target is TargetingObject)
            {
                labels = ["X:", "Y:", "W:", "H:", "Gap:"];
                _geoKinds = [FieldKind.CenterX, FieldKind.CenterY, FieldKind.SizeX, FieldKind.SizeY, FieldKind.Gap];
            }
            else if (target is BoundingBoxBase)
            {
                labels = ["X:", "Y:", "W:", "H:"];
                _geoKinds = [FieldKind.PosX, FieldKind.PosY, FieldKind.SizeX, FieldKind.SizeY];
            }
            else
            {
                labels = [];
                _geoKinds = [];
            }
            _geoNuds = new NumericUpDown[labels.Length];
            _geoUnits = new TextBlock[labels.Length];
            _geoLabels = new TextBlock[labels.Length];
            _geoRows = new Control[labels.Length];

            Title = target switch
            {
                LineObject => "Line Properties",
                RectObject => "Rectangle Properties",
                OvalObject => "Oval Properties",
                TargetingObject => "Target Properties",
                TextObject => "Text Properties",
                RoiObject => "ROI Properties",
                _ => "Overlay Properties",
            };
            Width = 200;
            SizeToContent = SizeToContent.Height;
            CanResize = true;
            CanMaximize = false;
            CanMinimize = false;
            ShowInTaskbar = false;

            Content = BuildContent(labels);
            ApplyTargetLayout();
            SyncFromOverlay();
            SyncProfileTab();
            SubscribeGeometryChanges();
            Closed += (_, _) => UnsubscribeGeometryChanges();
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private Control BuildContent(string[] labels)
        {
            var tabControl = new TabControl
            {
                Padding = new Thickness(0),
                Margin = new Thickness(0, -10, 0, 0),
            };
            tabControl.Classes.Add("property-tabs");


            if (labels.Length > 0)
                tabControl.Items.Add(new TabItem
                {
                    Header = new TextBlock { Text = "Geometry", FontSize = 11, Margin = new Thickness(0, 0, 0, -10) },
                    Content = BuildGeometryTab(labels),
                });

            if (_targetPanel != null)
                tabControl.Items.Add(new TabItem
                {
                    Header = new TextBlock { Text = "Target", FontSize = 11, Margin = new Thickness(0, 0, 0, -10) },
                    Content = BuildTargetTab(),
                });

            if (_target is LineObject)
                tabControl.Items.Add(new TabItem
                {
                    Header = new TextBlock { Text = "Profile", FontSize = 11, Margin = new Thickness(0, 0, 0, -10) },
                    Content = BuildProfileTab(),
                });

            tabControl.Items.Add(new TabItem
            {
                Header = new TextBlock { Text = "Pen", FontSize = 11, Margin = new Thickness(0, 0, 0, -10) },
                Content = BuildPenTab(),
            });

            var closeBtn = new Button
            {
                Content = "Close",
                FontSize = 11,
                Height = 24,
                MinHeight = 0,
                Padding = new Thickness(16, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 10, 6),
            };
            closeBtn.Click += (_, _) => Close();

            var root = new StackPanel();
            root.Children.Add(tabControl);
            root.Children.Add(closeBtn);
            return root;
        }

        private Control BuildGeometryTab(string[] labels)
        {
            var panel = new StackPanel { Margin = new Thickness(10, 8), Spacing = 4 };

            // ── Pixel / Scale radio ───────────────────────────────────────
            bool hasScale = _data != null && _data.XStep != 0 && _data.YStep != 0;
            var pixelRadio = new RadioButton
            {
                Content = "Pixel", GroupName = "CoordMode", IsChecked = true,
                FontSize = 11, MinHeight = 0, Height = 20,
            };
            pixelRadio.Classes.Add("compact");
            var scaleRadio = new RadioButton
            {
                Content = "Scale", GroupName = "CoordMode", IsEnabled = hasScale,
                FontSize = 11, MinHeight = 0, Height = 20,
            };
            scaleRadio.Classes.Add("compact");

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            modeRow.Children.Add(pixelRadio);
            modeRow.Children.Add(scaleRadio);
            panel.Children.Add(modeRow);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));

            // ── NUD rows ──────────────────────────────────────────────────
            if (_target is TargetingObject)
                _targetPanel = new StackPanel { Margin = new Thickness(10, 8), Spacing = 4 };
            bool isBBox = _target is BoundingBoxBase;
            const double labelW = 42;
            for (int i = 0; i < labels.Length; i++)
            {
                // Section headers
                if (i == 0 && isBBox)
                    panel.Children.Add(new TextBlock
                    {
                        Text = _target is TargetingObject ? "Center" : "Origin (bottom-left)",
                        FontSize = 10, Opacity = 0.5,
                    });
                if (_geoKinds[i] == FieldKind.SizeX && isBBox)
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Size",
                        FontSize = 10, Opacity = 0.5,
                        Margin = new Thickness(0, 2, 0, 0),
                    });
                if (_geoKinds[i] == FieldKind.Length)
                    panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));
                var nud = ControlFactory.MakeNumericUpDown(0m, -1_000_000m, 1_000_000m, 1m, width: 100);
                nud.FormatString = "F2";
                var unitTb = new TextBlock
                {
                    Text = "px", FontSize = 11, Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                };
                _geoNuds[i] = nud;
                _geoUnits[i] = unitTb;

                int idx = i;
                nud.ValueChanged += (_, _) => OnNudChanged(idx);

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                var labelTb = new TextBlock
                {
                    Text = labels[i], FontSize = 11,
                    Width = labelW, VerticalAlignment = VerticalAlignment.Center,
                };
                _geoLabels[i] = labelTb;
                _geoRows[i] = row;
                row.Children.Add(labelTb);
                row.Children.Add(nud);
                row.Children.Add(unitTb);
                (_geoKinds[i] == FieldKind.Gap ? _targetPanel! : panel).Children.Add(row);
            }

            // ── Wire mode switch ──────────────────────────────────────────
            pixelRadio.IsCheckedChanged += (_, _) =>
            {
                if (pixelRadio.IsChecked == true) SwitchMode(pixel: true);
            };
            scaleRadio.IsCheckedChanged += (_, _) =>
            {
                if (scaleRadio.IsChecked == true) SwitchMode(pixel: false);
            };

            return panel;
        }

        private Control BuildTargetTab()
        {
            var t = (TargetingObject)_target;
            var combo = new ComboBox
            {
                FontSize = 11, MinHeight = 0, Height = 22, Width = 100,
                Padding = new Thickness(6, 0),
                ItemsSource = new[] { "Reticle", "Marker (+)", "Marker (x)" },
                SelectedIndex = (int)t.Style,
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (_suppressSync || combo.SelectedIndex < 0) return;
                t.Style = (TargetStyle)combo.SelectedIndex;
                ApplyTargetLayout();
                // Zero-delta move raises GeometryChanged so the Geometry rows resync.
                _target.Move(0, 0);
                _redraw();
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = "Type:", FontSize = 11, Width = 42, VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(combo);
            _targetPanel!.Children.Insert(0, row);
            return _targetPanel;
        }

        private Control BuildProfileTab()
        {
            var line = (LineObject)_target;
            var panel = new StackPanel { Margin = new Thickness(10, 8), Spacing = 4 };

            panel.Children.Add(new TextBlock { Text = "Origin", FontSize = 10, Opacity = 0.5 });

            RadioButton MakeOriginRadio(string text, LineProfileOrigin mode)
            {
                var radio = new RadioButton
                {
                    Content = text, GroupName = "ProfileOrigin",
                    FontSize = 11, MinHeight = 0, Height = 20,
                };
                radio.Classes.Add("compact");
                radio.IsCheckedChanged += (_, _) =>
                {
                    if (_suppressProfileSync || radio.IsChecked != true) return;
                    line.ProfileOrigin = mode;
                    _redraw();
                };
                return radio;
            }

            _originLeftRadio = MakeOriginRadio("Left end", LineProfileOrigin.LeftEnd);
            _originCenterRadio = MakeOriginRadio("Center", LineProfileOrigin.Center);
            _originCustomRadio = MakeOriginRadio("Custom", LineProfileOrigin.Custom);

            // The fraction runs along the profile: 0 at the left end, 1 at the other end.
            _originNud = ControlFactory.MakeNumericUpDown(0.5m, 0m, 1m, 0.05m, width: 72);
            _originNud.FormatString = "F3";
            global::Avalonia.Controls.ToolTip.SetTip(_originNud, "Origin position along the line: 0 at the left end, 1 at the other end.");
            _originNud.ValueChanged += (_, _) =>
            {
                if (_suppressProfileSync || _originNud.Value is not { } v) return;
                double fraction = (double)v;
                line.OriginT = line.RunsFromP1 ? fraction : 1.0 - fraction;
                _redraw();
            };

            var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            customRow.Children.Add(_originCustomRadio);
            customRow.Children.Add(_originNud);

            panel.Children.Add(_originLeftRadio);
            panel.Children.Add(_originCenterRadio);
            panel.Children.Add(customRow);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));

            _interpCombo = new ComboBox
            {
                Width = 90, Height = 20, MinHeight = 0,
                FontSize = 11, Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _interpCombo.Items.Add("Bilinear");
            _interpCombo.Items.Add("Nearest");
            _interpCombo.SelectionChanged += (_, _) =>
            {
                if (_suppressProfileSync || _interpCombo.SelectedIndex < 0) return;
                line.ProfileInterpolation = _interpCombo.SelectedIndex == 1
                    ? LineProfileOption.NearestNeighbor
                    : LineProfileOption.Bilinear;
                _redraw();
            };
            var interpRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            interpRow.Children.Add(new TextBlock
            {
                Text = "Interp:", FontSize = 11, Width = 44, VerticalAlignment = VerticalAlignment.Center,
            });
            interpRow.Children.Add(_interpCombo);
            panel.Children.Add(interpRow);

            return panel;
        }

        private void SyncProfileTab()
        {
            if (_target is not LineObject line || _originNud == null) return;
            _suppressProfileSync = true;
            try
            {
                _originLeftRadio!.IsChecked = line.ProfileOrigin == LineProfileOrigin.LeftEnd;
                _originCenterRadio!.IsChecked = line.ProfileOrigin == LineProfileOrigin.Center;
                _originCustomRadio!.IsChecked = line.ProfileOrigin == LineProfileOrigin.Custom;
                _originNud.IsEnabled = line.ProfileOrigin == LineProfileOrigin.Custom;

                double t = line.EffectiveOriginT;
                _originNud.Value = (decimal)Math.Round(line.RunsFromP1 ? t : 1.0 - t, 4);
                _interpCombo!.SelectedIndex = line.ProfileInterpolation == LineProfileOption.NearestNeighbor ? 1 : 0;
            }
            finally { _suppressProfileSync = false; }
        }

        // A marker is a square defined by one size, and has no centre gap.
        private void ApplyTargetLayout()
        {
            if (_target is not TargetingObject t) return;
            bool marker = t.IsMarker;
            _geoLabels[2].Text = marker ? "Size:" : "W:";
            _geoRows[3].IsVisible = !marker;
            _geoRows[4].IsEnabled = !marker;
        }

        private Control BuildPenTab()
        {
            var panel = new StackPanel { Margin = new Thickness(10, 8), Spacing = 0 };
            const double labelW = 44;

            // ── Color ─────────────────────────────────────────────────────
            var colorSwatch = ControlFactory.MakeColorSwatch(_target.PenColor,
                c => { _target.PenColor = c; _redraw(); });
            var colorRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            colorRow.Children.Add(new TextBlock
            {
                Text = "Color:", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Width = labelW,
            });
            colorRow.Children.Add(colorSwatch);
            panel.Children.Add(colorRow);

            // ── Width ─────────────────────────────────────────────────────
            var widthNud = ControlFactory.MakeNumericUpDown(
                (decimal)_target.PenWidth, 0.5m, 20m, 0.5m, width: 72);
            widthNud.ValueChanged += (_, _) =>
            {
                if (widthNud.Value is { } v) { _target.PenWidth = (double)v; _redraw(); }
            };
            var widthRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
            };
            widthRow.Children.Add(new TextBlock
            {
                Text = "Width:", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Width = labelW,
            });
            widthRow.Children.Add(widthNud);
            panel.Children.Add(widthRow);

            // ── Dash style ────────────────────────────────────────────────
            var dashCombo = new ComboBox
            {
                Width = 90, Height = 20, MinHeight = 0,
                FontSize = 11, Padding = new Thickness(4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            dashCombo.Items.Add("Solid");
            dashCombo.Items.Add("Dash");
            dashCombo.Items.Add("Dot");
            dashCombo.SelectedIndex = (int)_target.PenDash;
            dashCombo.SelectionChanged += (_, _) =>
            {
                _target.PenDash = (OverlayDashStyle)dashCombo.SelectedIndex;
                _redraw();
            };
            var dashRow = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
            };
            dashRow.Children.Add(new TextBlock
            {
                Text = "Style:", FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Width = labelW,
            });
            dashRow.Children.Add(dashCombo);
            panel.Children.Add(dashRow);

            // ── Fill (BoundingBoxBase only) ────────────────────────────────
            if (_target is BoundingBoxBase bbox)
            {
                var fillSwatch = ControlFactory.MakeColorSwatch(bbox.FillColor,
                    c => { bbox.FillColor = c; _redraw(); });
                var fillCb = ControlFactory.MakeCheckBox("Fill");
                fillCb.IsChecked = bbox.IsFilled;
                fillCb.IsCheckedChanged += (_, _) =>
                {
                    bbox.IsFilled = fillCb.IsChecked == true;
                    _redraw();
                };
                var fillRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                fillRow.Children.Add(fillCb);
                fillRow.Children.Add(fillSwatch);
                panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4, 0, 2)));
                panel.Children.Add(fillRow);
            }

            return panel;
        }

        // ── Overlay ↔ NUD synchronisation ─────────────────────────────────

        // Overlay world Y is top-down (row 0 = top). Data index Y is bottom-up
        // (row 0 = bottom, left-bottom origin). Flip for position fields.
        private double FlipY(double worldY) => ((_data?.YCount ?? 1) - 1) - worldY;

        private double[] ReadPixelValues() => _target switch
        {
            LineObject l => [l.P1.X, FlipY(l.P1.Y), l.P2.X, FlipY(l.P2.Y), LinePixelLength(l)],
            TargetingObject t => [t.X + t.Width / 2, FlipY(t.Y + t.Height / 2), t.Width, t.Height, t.CenterGap],
            // Origin = world bottom-left (X, Y+H) → FlipY gives data-index Y of bottom-left
            BoundingBoxBase bb => [bb.X, FlipY(bb.Origin.Y), bb.Width, bb.Height],
            _ => [],
        };

        private static double LinePixelLength(LineObject l)
        {
            double dx = l.P2.X - l.P1.X, dy = l.P2.Y - l.P1.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private void WritePixelValue(int idx, double pixelValue)
        {
            _suppressSync = true;
            try
            {
                if (_target is LineObject line)
                {
                    switch (idx)
                    {
                        case 0: line.P1 = new Point(pixelValue, line.P1.Y); break;
                        case 1: line.P1 = new Point(line.P1.X, FlipY(pixelValue)); break;
                        case 2: line.P2 = new Point(pixelValue, line.P2.Y); break;
                        case 3: line.P2 = new Point(line.P2.X, FlipY(pixelValue)); break;
                        case 4:
                            // Length: scale P2 along current direction, or fall back to last
                            // known direction when the line is degenerate (P1 == P2).
                            double ldx = line.P2.X - line.P1.X;
                            double ldy = line.P2.Y - line.P1.Y;
                            double curLen = Math.Sqrt(ldx * ldx + ldy * ldy);
                            double angleRad = curLen > 1e-10
                                ? Math.Atan2(ldy, ldx)
                                : _lastLineAngleRad;
                            line.P2 = new Point(
                                line.P1.X + pixelValue * Math.Cos(angleRad),
                                line.P1.Y + pixelValue * Math.Sin(angleRad));
                            break;
                    }
                }
                else if (_target is TargetingObject tg)
                {
                    // Edits are about the centre so that size changes keep the target point fixed.
                    switch (idx)
                    {
                        case 0: tg.X = pixelValue - tg.Width / 2; break;
                        case 1: tg.Y = FlipY(pixelValue) - tg.Height / 2; break;
                        case 2: tg.ResizeAboutCenter(pixelValue, tg.Height); break;
                        case 3: tg.ResizeAboutCenter(tg.Width, pixelValue); break;
                        case 4: tg.CenterGap = pixelValue; break;
                    }
                }
                else if (_target is BoundingBoxBase bb)
                {
                    switch (idx)
                    {
                        case 0: bb.X = pixelValue; break;
                        case 1:
                            // Display Y = FlipY(Origin.Y); write back: Origin.Y = FlipY(display)
                            // Origin.Y = Y + H  →  Y = Origin.Y − H
                            bb.Y = FlipY(pixelValue) - bb.Height;
                            break;
                        case 2: bb.Width = pixelValue; break;
                        case 3:
                            // Keep Origin (bottom-left) fixed when H changes:
                            // Origin.Y = Y + H must stay constant
                            double originWorldY = bb.Origin.Y;
                            bb.Height = pixelValue;
                            bb.Y = originWorldY - pixelValue;
                            break;
                    }
                }
                // Zero-delta move fires GeometryChanged without actual movement
                _target.Move(0, 0);
                _redraw();
            }
            finally { _suppressSync = false; }
        }

        private void SyncFromOverlay()
        {
            // Keep the last known direction up-to-date whenever the line has non-zero length.
            // This must run even under _suppressSync so that WritePixelValue (Length case)
            // can rely on _lastLineAngleRad being fresh after it moves P2.
            if (_target is LineObject la)
            {
                double dxa = la.P2.X - la.P1.X, dya = la.P2.Y - la.P1.Y;
                if (Math.Sqrt(dxa * dxa + dya * dya) > 1e-10)
                    _lastLineAngleRad = Math.Atan2(dya, dxa);
            }

            if (_suppressSync || _geoNuds.Length == 0) return;
            _suppressSync = true;
            var pixels = ReadPixelValues();
            for (int i = 0; i < _geoNuds.Length; i++)
            {
                double val = _isPixelMode ? pixels[i] : PixelToScale(_geoKinds[i], pixels[i]);
                _geoNuds[i].Value = (decimal)Math.Round(val, 6);
            }
            _suppressSync = false;
        }

        private void OnNudChanged(int fieldIndex)
        {
            if (_suppressSync) return;
            double nudVal = (double)(_geoNuds[fieldIndex].Value ?? 0m);
            double pixelVal = _isPixelMode ? nudVal : ScaleToPixel(_geoKinds[fieldIndex], nudVal);
            WritePixelValue(fieldIndex, pixelVal);
            // Resync all NUDs: Length ↔ P2 and H ↔ Y are coupled
            SyncFromOverlay();
        }

        private void SwitchMode(bool pixel)
        {
            _isPixelMode = pixel;
            for (int i = 0; i < _geoNuds.Length; i++)
            {
                _geoNuds[i].Increment = pixel ? 1m : (decimal)GetScaleIncrement(_geoKinds[i]);
                _geoNuds[i].FormatString = pixel ? "F2" : "G6";
                _geoUnits[i].Text = pixel
                    ? "px"
                    : GetScaleUnit(_geoKinds[i]);
            }
            SyncFromOverlay();
        }

        private string GetScaleUnit(FieldKind kind)
        {
            string xu = _data?.XUnit ?? "";
            string yu = _data?.YUnit ?? "";
            return kind switch
            {
                FieldKind.PosX or FieldKind.CenterX or FieldKind.SizeX or FieldKind.Gap => xu,
                FieldKind.PosY or FieldKind.CenterY or FieldKind.SizeY => yu,
                FieldKind.Length => xu == yu ? xu : "",
                _ => "",
            };
        }

        private double GetScaleIncrement(FieldKind kind) => kind switch
        {
            FieldKind.PosX or FieldKind.CenterX or FieldKind.SizeX or FieldKind.Gap => Math.Abs(_data?.XStep ?? 1),
            FieldKind.Length => PhysicalLengthPerPixel(),
            _ => Math.Abs(_data?.YStep ?? 1),
        };

        // ── Pixel ↔ Scale conversion ──────────────────────────────────────

        private double PixelToScale(FieldKind kind, double pixel) => kind switch
        {
            FieldKind.PosX or FieldKind.CenterX => _data!.XMin + pixel * Math.Abs(_data.XStep),
            FieldKind.PosY or FieldKind.CenterY => _data!.YMin + pixel * Math.Abs(_data.YStep),
            FieldKind.SizeX or FieldKind.Gap => pixel * Math.Abs(_data!.XStep),
            FieldKind.SizeY => pixel * Math.Abs(_data!.YStep),
            FieldKind.Length => pixel * PhysicalLengthPerPixel(),
            _ => pixel,
        };

        private double ScaleToPixel(FieldKind kind, double scale) => kind switch
        {
            FieldKind.PosX or FieldKind.CenterX when _data?.XStep != 0 => (scale - _data.XMin) / Math.Abs(_data.XStep),
            FieldKind.PosY or FieldKind.CenterY when _data?.YStep != 0 => (scale - _data.YMin) / Math.Abs(_data.YStep),
            FieldKind.SizeX or FieldKind.Gap when _data?.XStep != 0 => scale / Math.Abs(_data.XStep),
            FieldKind.SizeY when _data?.YStep != 0 => scale / Math.Abs(_data.YStep),
            FieldKind.Length => PhysicalLengthPerPixel() is > 1e-10 and var pp ? scale / pp : scale,
            _ => scale,
        };

        /// <summary>
        /// Physical length per one pixel-index unit along the current line direction.
        /// For isotropic data this equals <c>|XStep|</c>; for anisotropic data it
        /// depends on the direction: <c>sqrt((ux·XStep)² + (uy·YStep)²)</c>.
        /// </summary>
        private double PhysicalLengthPerPixel()
        {
            if (_target is not LineObject l || _data == null) return 1;
            double dx = l.P2.X - l.P1.X, dy = l.P2.Y - l.P1.Y;
            double pix = Math.Sqrt(dx * dx + dy * dy);
            if (pix < 1e-10) return Math.Abs(_data.XStep);
            double ux = dx / pix, uy = dy / pix;
            double xs = _data.XStep, ys = _data.YStep;
            return Math.Sqrt(ux * ux * xs * xs + uy * uy * ys * ys);
        }

        // ── Live geometry tracking from external drags ────────────────────

        private void SubscribeGeometryChanges()
        {
            switch (_target)
            {
                case LineObject l: l.GeometryChanged += OnExtLine; break;
                case RectObject r: r.GeometryChanged += OnExtBBox; break;
                case OvalObject o: o.GeometryChanged += OnExtBBox; break;
                case TargetingObject t: t.GeometryChanged += OnExtBBox; break;
                case RoiObject roi: roi.BoundsChanged += OnExtBounds; break;
            }
        }

        private void UnsubscribeGeometryChanges()
        {
            switch (_target)
            {
                case LineObject l: l.GeometryChanged -= OnExtLine; break;
                case RectObject r: r.GeometryChanged -= OnExtBBox; break;
                case OvalObject o: o.GeometryChanged -= OnExtBBox; break;
                case TargetingObject t: t.GeometryChanged -= OnExtBBox; break;
                case RoiObject roi: roi.BoundsChanged -= OnExtBounds; break;
            }
        }

        private void OnExtLine(object? s, (Point, Point) _)
        {
            SyncFromOverlay();
            SyncProfileTab();
        }
        private void OnExtBBox(object? s, (Point, double, double) _) => SyncFromOverlay();
        private void OnExtBounds(object? s, EventArgs _) => SyncFromOverlay();
    }
}
