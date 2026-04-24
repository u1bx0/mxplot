using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Dialog for calibrating XStep/YStep from a drawn <see cref="LineObject"/>.
    /// The user specifies the real-world length of the line to derive new step values.
    /// </summary>
    internal sealed class CalibrateScaleDialog : Window
    {
        public enum ApplyTo { XOnly, YOnly, Both }

        public sealed record Result(double RealLength, string Unit, ApplyTo ApplyTo);

        public Result? DialogResult { get; private set; }

        private readonly IMatrixData _data;
        private readonly double _lpx;
        private readonly double _absDx;
        private readonly double _absDy;

        private readonly NumericUpDown _realLengthNud;
        private readonly TextBox _unitBox;
        private readonly RadioButton _xOnlyRadio;
        private readonly RadioButton _yOnlyRadio;
        private readonly RadioButton _bothRadio;
        private readonly TextBlock _previewText;

        public CalibrateScaleDialog(LineObject line, IMatrixData data)
        {
            _data = data;

            double dx = line.P2.X - line.P1.X;
            double dy = line.P2.Y - line.P1.Y;
            _lpx = Math.Sqrt(dx * dx + dy * dy);
            _absDx = Math.Abs(dx);
            _absDy = Math.Abs(dy);

            // Angle in data-index convention (Y-up): flip dy
            double angleDeg = Math.Atan2(-dy, dx) * (180.0 / Math.PI);
            if (angleDeg < 0) angleDeg += 360.0;

            Title = "Calibrate Scale from Line";
            SizeToContent = SizeToContent.Height;
            Width = 310;
            CanResize = false;
            CanMaximize = false;
            CanMinimize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            bool dxZero = _absDx < 0.5;
            bool dyZero = _absDy < 0.5;

            // ── Read-only info ─────────────────────────────────────────────
            var lpxLabel = Ro($"{_lpx:F2} px");
            var angleLabel = Ro($"{angleDeg:F1}°");
            var hintLabel = new TextBlock
            {
                Text = GetAngleHint(_absDx, _absDy, _lpx),
                FontSize = 10,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            };

            // ── User input ─────────────────────────────────────────────────
            var ddx = _absDx * data.XStep;
            var ddy = _absDy * data.YStep;
            decimal initialRealLength = (decimal)Math.Sqrt(ddx * ddx + ddy * ddy);
            
            _realLengthNud = ControlFactory.MakeNumericUpDown(initialRealLength, 0.000001m, 1e9m, 0.1m, width: 90);
            _realLengthNud.FormatString = "G5";

            _unitBox = new TextBox
            {
                Text = data.XUnit ?? "",
                Width = 55,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                Padding = new Thickness(4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            _xOnlyRadio = MakeRadio("X Only", !dxZero);
            _yOnlyRadio = MakeRadio("Y Only", !dyZero);
            _bothRadio = MakeRadio("Both", true);

            // Default Apply To based on geometry
            if (dxZero) { _yOnlyRadio.IsChecked = true; }
            else if (dyZero) { _xOnlyRadio.IsChecked = true; }
            else { _bothRadio.IsChecked = true; }

            // ── Preview ────────────────────────────────────────────────────
            _previewText = new TextBlock
            {
                FontSize = 11,
                Opacity = 0.85,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
            };

            // ── Event wiring ───────────────────────────────────────────────
            _realLengthNud.ValueChanged += (_, _) => UpdatePreview();
            _unitBox.TextChanged += (_, _) => UpdatePreview();
            _xOnlyRadio.IsCheckedChanged += (_, _) => { SyncUnit(); UpdatePreview(); };
            _yOnlyRadio.IsCheckedChanged += (_, _) => { SyncUnit(); UpdatePreview(); };
            _bothRadio.IsCheckedChanged += (_, _) => { SyncUnit(); UpdatePreview(); };

            // ── Buttons ────────────────────────────────────────────────────
            var okBtn = new Button
            {
                Content = "OK",
                Width = 70, Height = 26, MinHeight = 0,
                FontSize = 11, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            okBtn.Classes.Add("accent");
            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 70, Height = 26, MinHeight = 0,
                FontSize = 11, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            okBtn.Click += (_, _) =>
            {
                DialogResult = new Result(
                    (double)(_realLengthNud.Value ?? 1m),
                    _unitBox.Text ?? "",
                    GetApplyTo());
                Close();
            };
            cancelBtn.Click += (_, _) => Close();

            // ── Layout helpers ─────────────────────────────────────────────
            const double LabelW = 82;
            StackPanel Row(string label, Control control)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new TextBlock
                {
                    Text = label, FontSize = 11, Width = LabelW,
                    Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(control);
                return row;
            }

            var applyToPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            applyToPanel.Children.Add(_xOnlyRadio);
            applyToPanel.Children.Add(_yOnlyRadio);
            applyToPanel.Children.Add(_bothRadio);

            var realLenPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            realLenPanel.Children.Add(_realLengthNud);
            realLenPanel.Children.Add(_unitBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 6,
                Margin = new Thickness(0, 6, 0, 0),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);

            var root = new StackPanel
            {
                Spacing = 5,
                Margin = new Thickness(14, 12, 14, 12),
            };

            root.Children.Add(Hdr("Line Info"));
            root.Children.Add(Row("Pixel Length:", lpxLabel));
            root.Children.Add(Row("Angle:", angleLabel));
            root.Children.Add(hintLabel);
            root.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));

            root.Children.Add(Hdr("Calibration"));
            root.Children.Add(Row("Real Length:", realLenPanel));
            root.Children.Add(Row("Apply To:", applyToPanel));
            root.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));

            root.Children.Add(Hdr("Preview"));
            root.Children.Add(_previewText);
            root.Children.Add(btnRow);

            Content = root;
            UpdatePreview();
        }

        // ── Internal helpers ───────────────────────────────────────────────

        private ApplyTo GetApplyTo()
        {
            if (_xOnlyRadio.IsChecked == true) return ApplyTo.XOnly;
            if (_yOnlyRadio.IsChecked == true) return ApplyTo.YOnly;
            return ApplyTo.Both;
        }

        /// <summary>
        /// Auto-updates the unit box when Apply To changes,
        /// only if the current value still matches one of the existing data units
        /// (i.e., the user has not typed a custom value).
        /// </summary>
        private void SyncUnit()
        {
            var current = _unitBox.Text ?? "";
            string xu = _data.XUnit ?? "";
            string yu = _data.YUnit ?? "";
            if (current != xu && current != yu) return;

            var applyTo = GetApplyTo();
            _unitBox.Text = applyTo switch
            {
                ApplyTo.XOnly => xu,
                ApplyTo.YOnly => yu,
                _ => xu.Length > 0 ? xu : yu,
            };
        }

        private void UpdatePreview()
        {
            double d = (double)(_realLengthNud.Value ?? 1m);
            if (d <= 0 || _lpx < 0.5) { _previewText.Text = "\u2014"; return; }

            string unit = _unitBox.Text ?? "";
            string us = unit.Length > 0 ? $" {unit}" : "";

            // Square-pixel assumption: 1 pixel = D / Lpx regardless of line direction.
            // Apply To controls only which axis receives the value, not the formula.
            double step = d / _lpx;

            _previewText.Text = GetApplyTo() switch
            {
                ApplyTo.XOnly => $"XStep = {step:G4}{us}",
                ApplyTo.YOnly => $"YStep = {step:G4}{us}",
                _ => $"XStep = YStep = {step:G4}{us}",
            };
        }

        private static string GetAngleHint(double absDx, double absDy, double lpx)
        {
            if (lpx < 0.5) return "";
            double sinY = absDy / lpx;
            double sinX = absDx / lpx;
            if (sinY < 0.259) return "\u2248 Horizontal \u2014 X Only recommended";
            if (sinX < 0.259) return "\u2248 Vertical \u2014 Y Only recommended";
            return "Diagonal \u2014 Both applies X and Y independently";
        }

        private static TextBlock Ro(string t) => new TextBlock
        {
            Text = t, FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
        };

        private static TextBlock Hdr(string t) => new TextBlock
        {
            Text = t, FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.7,
        };

        private static RadioButton MakeRadio(string text, bool enabled) => new RadioButton
        {
            Content = text,
            GroupName = "CalibrateApplyTo",
            FontSize = 11,
            MinHeight = 0,
            Height = 20,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
