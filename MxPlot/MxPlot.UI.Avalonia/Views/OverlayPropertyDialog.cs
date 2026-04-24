using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
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
        private readonly FieldKind[] _geoKinds;
        private bool _isPixelMode = true;
        private bool _suppressSync;
        // Stores the last non-degenerate line direction so that Length can restore
        // a zero-length line without losing orientation.
        private double _lastLineAngleRad;

        private enum FieldKind { PosX, PosY, SizeX, SizeY, Length }

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
            SyncFromOverlay();
            SubscribeGeometryChanges();
            Closed += (_, _) => UnsubscribeGeometryChanges();
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private Control BuildContent(string[] labels)
        {
            var tabControl = new TabControl { Padding = new Thickness(0) };

            if (labels.Length > 0)
                tabControl.Items.Add(new TabItem
                {
                    Header = new TextBlock { Text = "Geometry", FontSize = 12 },
                    Content = BuildGeometryTab(labels),
                });

            tabControl.Items.Add(new TabItem
            {
                Header = new TextBlock { Text = "Pen", FontSize = 12 },
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
            bool isBBox = _target is BoundingBoxBase;
            const double labelW = 42;
            for (int i = 0; i < labels.Length; i++)
            {
                // Section headers
                if (i == 0 && isBBox)
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Origin (bottom-left)",
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
                row.Children.Add(new TextBlock
                {
                    Text = labels[i], FontSize = 11,
                    Width = labelW, VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(nud);
                row.Children.Add(unitTb);
                panel.Children.Add(row);
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
                FieldKind.PosX or FieldKind.SizeX => xu,
                FieldKind.PosY or FieldKind.SizeY => yu,
                FieldKind.Length => xu == yu ? xu : "",
                _ => "",
            };
        }

        private double GetScaleIncrement(FieldKind kind) => kind switch
        {
            FieldKind.PosX or FieldKind.SizeX => Math.Abs(_data?.XStep ?? 1),
            FieldKind.Length => PhysicalLengthPerPixel(),
            _ => Math.Abs(_data?.YStep ?? 1),
        };

        // ── Pixel ↔ Scale conversion ──────────────────────────────────────

        private double PixelToScale(FieldKind kind, double pixel) => kind switch
        {
            FieldKind.PosX => _data!.XMin + pixel * Math.Abs(_data.XStep),
            FieldKind.PosY => _data!.YMin + pixel * Math.Abs(_data.YStep),
            FieldKind.SizeX => pixel * Math.Abs(_data!.XStep),
            FieldKind.SizeY => pixel * Math.Abs(_data!.YStep),
            FieldKind.Length => pixel * PhysicalLengthPerPixel(),
            _ => pixel,
        };

        private double ScaleToPixel(FieldKind kind, double scale) => kind switch
        {
            FieldKind.PosX when _data?.XStep != 0 => (scale - _data.XMin) / Math.Abs(_data.XStep),
            FieldKind.PosY when _data?.YStep != 0 => (scale - _data.YMin) / Math.Abs(_data.YStep),
            FieldKind.SizeX when _data?.XStep != 0 => scale / Math.Abs(_data.XStep),
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

        private void OnExtLine(object? s, (Point, Point) _) => SyncFromOverlay();
        private void OnExtBBox(object? s, (Point, double, double) _) => SyncFromOverlay();
        private void OnExtBounds(object? s, EventArgs _) => SyncFromOverlay();
    }
}
