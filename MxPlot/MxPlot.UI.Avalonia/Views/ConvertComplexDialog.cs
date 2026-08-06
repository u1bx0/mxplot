using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Dialog for converting complex-valued matrix data to double by extracting a specific component.
    /// Returns <c>(ComplexValueMode Mode, bool ApplyLog10, bool ReplaceData)</c> on OK, or <c>null</c> on Cancel.
    /// </summary>
    internal sealed class ConvertComplexDialog : Window
    {
        private readonly IMatrixData? _srcData;
        private readonly ComboBox _modeCombo;
        private readonly CheckBox _log10Check;
        private readonly CheckBox _replaceCheck;
        private readonly TextBlock _sizeText;
        private readonly TextBlock _infoText;

        // Persisted across dialog invocations
        private static ComplexValueMode _lastMode = ComplexValueMode.Magnitude;
        private static bool _lastApplyLog10 = false;

        internal ConvertComplexDialog(IMatrixData? srcData = null)
        {
            _srcData = srcData;

            Title = "Convert Complex Value";
            Width = 360;
            SizeToContent = SizeToContent.Height;
            CanResize = true;
            CanMaximize = false;
            CanMinimize = false;
            ShowInTaskbar = false;

            _modeCombo = new ComboBox
            {
                MinWidth = 180,
                Width = double.NaN,
                Height = 24,
                FontSize = 11,
                MinHeight = 0,
                Padding = new Thickness(8, 0, 4, 0)
            };
            _modeCombo.ItemsSource = new[]
            {
                "Magnitude (|z|)",
                "Real (Re(z))",
                "Imaginary (Im(z))",
                "Phase (arg(z))",
                "Power (|z|²)"
            };
            _modeCombo.SelectedIndex = (int)_lastMode;

            _log10Check = ControlFactory.MakeCheckBox("Apply log₁₀ transformation", fontSize: 11);
            _log10Check.IsChecked = _lastApplyLog10;
            ToolTip.SetTip(_log10Check, "Applies log₁₀ to each output value (with epsilon floor to avoid log(0)).");

            _replaceCheck = ControlFactory.MakeCheckBox("Replace current data (all frames)", fontSize: 11,
                hint: "If checked, replaces the current data in this window. If unchecked (default), result opens in a new window.");

            _sizeText = new TextBlock
            {
                FontSize = 10,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            };

            _infoText = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255)),
                Margin = new Thickness(0, 2),
            };

            Content = BuildContent();
            _modeCombo.SelectionChanged += (_, _) => UpdateInfoText();
            UpdateInfoText();
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private Control BuildContent()
        {
            var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 10), Spacing = 6 };

            // Virtual data warning banner
            if (_srcData?.IsVirtual == true)
            {
                var virtualBanner = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(60, 255, 180, 0)),
                    BorderBrush = Brushes.Orange,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(0, 0, 0, 2),
                    Child = new TextBlock
                    {
                        Text = "\u26a0 Source data is virtual (file-mapped). Conversion will load all frames into memory.",
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Orange,
                    },
                };
                panel.Children.Add(virtualBanner);
            }

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new TextBlock
            {
                Text = "Convert \u2018Complex\u2019 \u2192 \u2018double\u2019",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (_srcData != null)
                headerRow.Children.Add(_sizeText);
            panel.Children.Add(headerRow);

            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            modeRow.Children.Add(new TextBlock
            {
                Text = "Extract Component:",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            modeRow.Children.Add(_modeCombo);
            panel.Children.Add(modeRow);

            panel.Children.Add(_infoText);
            panel.Children.Add(_log10Check);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));
            panel.Children.Add(_replaceCheck);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));

            var okBtn = new Button
            {
                Content = "Convert",
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
            okBtn.Click += (_, _) => OnOk();
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

            // Update size label
            if (_srcData != null)
            {
                long srcBytes = (long)_srcData.XCount * _srcData.YCount * _srcData.FrameCount * _srcData.ElementSize;
                int tgtElemSize = sizeof(double);
                long tgtBytes = (long)_srcData.XCount * _srcData.YCount * _srcData.FrameCount * tgtElemSize;
                string Fmt(long b) => b >= 1024 * 1024
                    ? $"{b / (1024.0 * 1024.0):F1}\u00a0MB"
                    : $"{b / 1024.0:F1}\u00a0KB";
                _sizeText.Text = $"({Fmt(srcBytes)} \u2192 {Fmt(tgtBytes)})";
            }

            return panel;
        }

        // ── UI state ──────────────────────────────────────────────────────────

        private void UpdateInfoText()
        {
            int idx = _modeCombo.SelectedIndex;
            var mode = idx >= 0 && idx < 5 ? (ComplexValueMode)idx : ComplexValueMode.Magnitude;

            _infoText.Text = mode switch
            {
                ComplexValueMode.Magnitude => "\u2139 Extracts |z| = \u221a(Re\u00b2 + Im\u00b2) for each complex value.",
                ComplexValueMode.Real => "\u2139 Extracts the real component Re(z) for each complex value.",
                ComplexValueMode.Imaginary => "\u2139 Extracts the imaginary component Im(z) for each complex value.",
                ComplexValueMode.Phase => "\u2139 Extracts arg(z) in radians [\u2212\u03c0, +\u03c0] for each complex value.",
                ComplexValueMode.Power => "\u2139 Extracts |z|\u00b2 = Re\u00b2 + Im\u00b2 for each complex value.",
                _ => string.Empty,
            };
        }

        // ── OK ────────────────────────────────────────────────────────────────

        private void OnOk()
        {
            int idx = _modeCombo.SelectedIndex;
            var mode = idx >= 0 && idx < 5 ? (ComplexValueMode)idx : ComplexValueMode.Magnitude;
            bool applyLog10 = _log10Check.IsChecked == true;
            bool replaceData = _replaceCheck.IsChecked == true;

            _lastMode = mode;
            _lastApplyLog10 = applyLog10;

            Close(((ComplexValueMode, bool, bool)?)(mode, applyLog10, replaceData));
        }

        // ── Centered show ─────────────────────────────────────────────────────

        internal Task<(ComplexValueMode Mode, bool ApplyLog10, bool ReplaceData)?> ShowCenteredOnAsync(Window owner, Visual hostVisual)
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
                            screenTL.X + (int)((hc.Bounds.Width - Width) / 2),
                            screenTL.Y + (int)((hc.Bounds.Height - Height) / 2));
                    }
                };
            }
            return ShowDialog<(ComplexValueMode Mode, bool ApplyLog10, bool ReplaceData)?>(owner);
        }
    }
}
