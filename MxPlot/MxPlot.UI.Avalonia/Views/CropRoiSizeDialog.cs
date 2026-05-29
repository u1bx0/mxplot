using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// A compact dialog for directly setting the width and height of a ROI (or Z start/count).
    /// Values can be entered in pixel (data-index) or physical-scale units.
    /// Returns <c>(double W, double H)</c> in pixel-edge units on OK, or <c>null</c> on Cancel.
    /// </summary>
    internal sealed class CropRoiSizeDialog : Window
    {
        private readonly IMatrixData? _data;
        private readonly NumericUpDown _widthNud;
        private readonly NumericUpDown _heightNud;
        private readonly TextBlock _widthUnit;
        private readonly TextBlock _heightUnit;
        private bool _isPixelMode = true;
        private bool _suppressSync;

        // Pixel-space values — source of truth while the dialog is open
        private double _widthPx;
        private double _heightPx;

        // Custom axis overrides (used for the Z-range dialog)
        private readonly string _widthLabel;
        private readonly string _heightLabel;
        private readonly string _widthScaleUnit;
        private readonly string _heightScaleUnit;
        private readonly double _widthScaleStep;
        private readonly double _heightScaleStep;
        private readonly bool _hasScale;
        // Optional upper bounds (used for Z-range: start ≤ maxStart, count ≤ dataZCount - start)
        private readonly double _maxWidthPx;
        private readonly double _maxHeightPx;
        private readonly bool _linkHeightMaxToWidth; // when true, heightMax = _maxWidthPx - widthPx

        /// <summary>Creates an XY ROI size dialog driven by the XY axes of <paramref name="data"/>.</summary>
        internal CropRoiSizeDialog(double currentWidthPx, double currentHeightPx, IMatrixData? data)
            : this(currentWidthPx, currentHeightPx,
                   widthLabel: "Width:", heightLabel: "Height:",
                   widthScaleStep:  data?.XStep ?? 0,
                   heightScaleStep: data?.YStep ?? 0,
                   widthScaleUnit:  data?.XUnit ?? "",
                   heightScaleUnit: data?.YUnit ?? "",
                   data: data)
        {
            Title = "ROI Size";
        }

        /// <summary>
        /// Creates a generic size dialog with explicit axis labels and scale parameters.
        /// Used for Z-range editing where XY axis metadata is not applicable.
        /// </summary>
        internal CropRoiSizeDialog(
            double currentWidthPx, double currentHeightPx,
            string widthLabel, string heightLabel,
            double widthScaleStep, double heightScaleStep,
            string widthScaleUnit, string heightScaleUnit,
            double maxWidthPx = double.MaxValue,
            double maxHeightPx = double.MaxValue,
            bool linkHeightMaxToWidth = false,
            IMatrixData? data = null)
        {
            _data = data;
            _widthPx = Math.Max(1.0, currentWidthPx);
            _heightPx = Math.Max(1.0, currentHeightPx);
            _widthLabel = widthLabel;
            _heightLabel = heightLabel;
            _widthScaleStep = widthScaleStep;
            _heightScaleStep = heightScaleStep;
            _widthScaleUnit = widthScaleUnit;
            _heightScaleUnit = heightScaleUnit;
            _hasScale = widthScaleStep != 0 && heightScaleStep != 0;
            _maxWidthPx = maxWidthPx > 0 ? maxWidthPx : double.MaxValue;
            _maxHeightPx = maxHeightPx > 0 ? maxHeightPx : double.MaxValue;
            _linkHeightMaxToWidth = linkHeightMaxToWidth;

            Title = "ROI Size";
            Width = 210;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            CanMaximize = false;
            CanMinimize = false;
            ShowInTaskbar = false;

            _widthNud = ControlFactory.MakeNumericUpDown(0m, 1m, 1_000_000m, 1m, width: 96);
            _heightNud = ControlFactory.MakeNumericUpDown(0m, 1m, 1_000_000m, 1m, width: 96);
            if (_maxWidthPx < double.MaxValue)
                _widthNud.Maximum = (decimal)_maxWidthPx;
            if (_maxHeightPx < double.MaxValue)
                _heightNud.Maximum = (decimal)_maxHeightPx;
            _widthUnit = new TextBlock { Text = "px", FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };
            _heightUnit = new TextBlock { Text = "px", FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center };

            Content = BuildContent();
            SyncFromPixels();
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private Control BuildContent()
        {
            var panel = new StackPanel { Margin = new Thickness(10, 10, 10, 8), Spacing = 6 };

            var pixelRadio = new RadioButton
            {
                Content = "Pixel", GroupName = "SizeMode", IsChecked = true,
                FontSize = 11, MinHeight = 0, Height = 20,
            };
            pixelRadio.Classes.Add("compact");
            var scaleRadio = new RadioButton
            {
                Content = "Scale", GroupName = "SizeMode", IsEnabled = _hasScale,
                FontSize = 11, MinHeight = 0, Height = 20,
            };
            scaleRadio.Classes.Add("compact");

            pixelRadio.IsCheckedChanged += (_, _) => { if (pixelRadio.IsChecked == true) SwitchMode(pixel: true); };
            scaleRadio.IsCheckedChanged += (_, _) => { if (scaleRadio.IsChecked == true) SwitchMode(pixel: false); };

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            modeRow.Children.Add(pixelRadio);
            modeRow.Children.Add(scaleRadio);
            panel.Children.Add(modeRow);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 1)));

            _widthNud.ValueChanged += (_, _) => OnNudChanged(isWidth: true);
            _heightNud.ValueChanged += (_, _) => OnNudChanged(isWidth: false);

            panel.Children.Add(MakeNudRow(_widthLabel, _widthNud, _widthUnit));
            panel.Children.Add(MakeNudRow(_heightLabel, _heightNud, _heightUnit));
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));

            var okBtn = new Button
            {
                Content = "OK",
                FontSize = 11,
                Height = 24,
                MinHeight = 0,
                MinWidth = 60,
                Padding = new Thickness(8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                FontSize = 11,
                Height = 24,
                MinHeight = 0,
                MinWidth = 60,
                Padding = new Thickness(8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            okBtn.Click += (_, _) => Close((_widthPx, _heightPx));
            cancelBtn.Click += (_, _) => Close(null);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);
            panel.Children.Add(btnRow);

            return panel;
        }

        private static Control MakeNudRow(string label, NumericUpDown nud, TextBlock unit)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Width = 48,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(nud);
            row.Children.Add(unit);
            return row;
        }

        // ── Sync ──────────────────────────────────────────────────────────────

        private void SyncFromPixels()
        {
            _suppressSync = true;
            if (_isPixelMode)
            {
                _widthNud.Value = (decimal)Math.Round(_widthPx);
                _heightNud.Value = (decimal)Math.Round(_heightPx);
            }
            else
            {
                _widthNud.Value = (decimal)Math.Round(PixelToScaleW(_widthPx), 6);
                _heightNud.Value = (decimal)Math.Round(PixelToScaleH(_heightPx), 6);
            }
            _suppressSync = false;
        }

        private void OnNudChanged(bool isWidth)
        {
            if (_suppressSync) return;
            double nudVal = (double)((isWidth ? _widthNud : _heightNud).Value ?? 1m);
            double px = _isPixelMode ? nudVal : (isWidth ? ScaleToPixelW(nudVal) : ScaleToPixelH(nudVal));
            px = Math.Max(1.0, px);
            if (isWidth) _widthPx = px; else _heightPx = px;

            // When linked (Z-range: width = start, height = count),
            // update height maximum so start + count never exceeds the data Z extent.
            if (isWidth && _linkHeightMaxToWidth && _maxWidthPx < double.MaxValue)
            {
                double maxCountPx = Math.Max(1.0, _maxWidthPx - _widthPx);
                decimal maxCountNud = _isPixelMode
                    ? (decimal)Math.Floor(maxCountPx)
                    : (decimal)Math.Round(PixelToScaleH(maxCountPx), 6);
                _heightNud.Maximum = Math.Max(1m, maxCountNud);
                if (_heightNud.Value > _heightNud.Maximum)
                {
                    _heightNud.Value = _heightNud.Maximum;
                    _heightPx = maxCountPx;
                }
            }
        }

        private void SwitchMode(bool pixel)
        {
            _isPixelMode = pixel;
            if (pixel)
            {
                _widthNud.Increment = 1m;
                _heightNud.Increment = 1m;
                _widthNud.FormatString = "F0";
                _heightNud.FormatString = "F0";
                _widthUnit.Text = "px";
                _heightUnit.Text = "px";
            }
            else
            {
                _widthNud.Increment = (decimal)Math.Abs(_widthScaleStep != 0 ? _widthScaleStep : 1);
                _heightNud.Increment = (decimal)Math.Abs(_heightScaleStep != 0 ? _heightScaleStep : 1);
                _widthNud.FormatString = "G6";
                _heightNud.FormatString = "G6";
                _widthUnit.Text = _widthScaleUnit;
                _heightUnit.Text = _heightScaleUnit;
            }
            SyncFromPixels();
        }

        // ── Centered show ─────────────────────────────────────────────────────

        /// <summary>
        /// Shows the dialog centered over <paramref name="hostVisual"/> (the MatrixPlotter control).
        /// Repositioning is deferred to the <see cref="Window.Opened"/> event so the dialog's
        /// actual rendered size is known before computing the position.
        /// </summary>
        internal Task<(double W, double H)?> ShowCenteredOnAsync(Window owner, Visual hostVisual)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (hostVisual is Control hc)
            {
                Opened += (_, _) =>
                {
                    var topLeft = hc.TranslatePoint(new Point(0, 0), owner);
                    if (topLeft.HasValue)
                    {
                        var screenTL = owner.PointToScreen(topLeft.Value);
                        Position = new PixelPoint(
                            screenTL.X + (int)((hc.Bounds.Width  - Width)  / 2),
                            screenTL.Y + (int)((hc.Bounds.Height - Height) / 2));
                    }
                };
            }
            return ShowDialog<(double W, double H)?>(owner);
        }

        // ── Scale conversion ──────────────────────────────────────────────────

        private double PixelToScaleW(double px) => px * Math.Abs(_widthScaleStep != 0 ? _widthScaleStep : 1);
        private double PixelToScaleH(double px) => px * Math.Abs(_heightScaleStep != 0 ? _heightScaleStep : 1);
        private double ScaleToPixelW(double s) => _widthScaleStep != 0 ? s / Math.Abs(_widthScaleStep) : s;
        private double ScaleToPixelH(double s) => _heightScaleStep != 0 ? s / Math.Abs(_heightScaleStep) : s;
    }
}
