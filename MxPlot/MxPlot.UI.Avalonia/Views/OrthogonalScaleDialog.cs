using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Dialog for configuring the depth-axis display scale in orthogonal side views.
    /// Show with <c>await dlg.ShowDialog(ownerWindow)</c> then read <see cref="ResultMode"/>
    /// and <see cref="ResultCustomRatio"/>.
    /// </summary>
    internal sealed class OrthogonalScaleDialog : Window
    {
        /// <summary>The chosen scale mode, or <c>null</c> when the user cancelled.</summary>
        public OrthoScaleMode? ResultMode { get; private set; }

        /// <summary>
        /// The custom depth/width display ratio chosen by the user.
        /// <c>ratio = (ZCount × d) / (XCount × XStep)</c> — i.e. displayed height ÷ displayed width.
        /// Only meaningful when <see cref="ResultMode"/> is <see cref="OrthoScaleMode.Custom"/>.
        /// </summary>
        public double ResultCustomRatio { get; private set; }

        public OrthogonalScaleDialog(
            OrthoScaleMode currentMode,
            double currentCustomRatio,
            string axisName,
            double xStep, string xUnit,
            double yStep, string yUnit,
            double xyDisplayW, double xyDisplayH, int physicalAxisPx)
        {
            Title = "Axis View Scale";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 11;

            string xStepText = xStep > 0
                ? xStep.ToString("G4", CultureInfo.InvariantCulture) + (xUnit.Length > 0 ? " " + xUnit : "")
                : "\u2014";
            string yStepText = yStep > 0
                ? yStep.ToString("G4", CultureInfo.InvariantCulture) + (yUnit.Length > 0 ? " " + yUnit : "")
                : "\u2014";

            int xyW = (int)System.Math.Round(xyDisplayW);
            int xyH = (int)System.Math.Round(xyDisplayH);

            var xyInfoLabel = new TextBlock
            {
                Text = $"XY Pixels (Scaled): {xyW} × {xyH} px\n(XStep: {xStepText}  YStep: {yStepText})",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
            };

            var physicalRadio = new RadioButton
            {
                Content = "Physical (step-size aspect ratio)",
                FontSize = 11,
                IsChecked = currentMode != OrthoScaleMode.Custom,
                GroupName = "ScaleMode",
            };

            string depthAxisLabel = axisName.Length > 0 ? axisName : "Axis";
            var customRadio = new RadioButton
            {
                Content = $"Custom {depthAxisLabel}/Width ratio:",
                FontSize = 11,
                IsChecked = currentMode == OrthoScaleMode.Custom,
                GroupName = "ScaleMode",
            };

            double initRatio = currentCustomRatio > 0 ? currentCustomRatio : 1.0;
            var customInput = ControlFactory.MakeNumericUpDown(
                (decimal)initRatio, 0.01m, 100m, 0.1m, width: 76);
            customInput.FormatString = "G4";
            customInput.IsEnabled = currentMode == OrthoScaleMode.Custom;
            var xWidthLabel = new TextBlock
            {
                Text = "× Width (Bottom View)",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                IsEnabled = currentMode == OrthoScaleMode.Custom,
                Opacity = 0.6,
            };
            var customRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(22, 0, 0, 0),
                Children = { customInput, xWidthLabel },
            };

            static int CalcCustomPx(int xw, double ratio)
                => (int)System.Math.Round(ratio * xw);

            int initDisplayPx = currentMode == OrthoScaleMode.Custom
                ? CalcCustomPx(xyW, initRatio)
                : physicalAxisPx;

            var heightInfoLabel = new TextBlock
            {
                Text = $"Displaed Height ({depthAxisLabel}): {initDisplayPx} px (100%)",
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(22, 0, 0, 0),
            };

            void UpdateHeightLabel()
            {
                int px;
                if (customRadio.IsChecked == true)
                {
                    double r = customInput.Value.HasValue ? (double)customInput.Value.Value : initRatio;
                    px = CalcCustomPx(xyW, r);
                }
                else
                {
                    px = physicalAxisPx;
                }
                heightInfoLabel.Text = $"Displayed Height ({depthAxisLabel}): {px} px (100%)";
            }

            customRadio.IsCheckedChanged += (_, _) =>
            {
                bool isCustom = customRadio.IsChecked == true;
                customInput.IsEnabled = isCustom;
                xWidthLabel.IsEnabled = isCustom;
                UpdateHeightLabel();
            };
            customInput.ValueChanged += (_, _) => UpdateHeightLabel();

            var okBtn = new Button { Content = "OK", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            var cancelBtn = new Button { Content = "Cancel", Width = 80, HorizontalContentAlignment = HorizontalAlignment.Center };

            okBtn.Click += (_, _) =>
            {
                if (customRadio.IsChecked == true)
                {
                    double parsed = customInput.Value.HasValue ? (double)customInput.Value.Value : 0;
                    if (parsed <= 0)
                    {
                        customInput.BorderBrush = Brushes.Red;
                        return;
                    }
                    ResultMode = OrthoScaleMode.Custom;
                    ResultCustomRatio = parsed;
                }
                else
                {
                    ResultMode = OrthoScaleMode.Physical;
                }
                Close();
            };
            cancelBtn.Click += (_, _) => Close();

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 12, 0, 0),
                Children = { okBtn, cancelBtn },
            };

            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 6,
                Children =
                {
                    xyInfoLabel,
                    physicalRadio,
                    customRadio,
                    customRow,
                    heightInfoLabel,
                    btnRow,
                },
            };
        }
    }
}
