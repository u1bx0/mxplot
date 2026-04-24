using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Dialog for converting matrix data to a different numeric value type.
    /// Returns <c>(Type TargetType, bool DoScale, bool ReplaceData, double SrcMin, double SrcMax, double TgtMin, double TgtMax)</c>
    /// on OK, or <c>null</c> on Cancel.
    /// </summary>
    internal sealed class ConvertValueTypeDialog : Window
    {
        private readonly string _srcTypeName;
        private readonly double _lutMin;
        private readonly double _lutMax;
        private readonly IMatrixData? _srcData;

        private readonly ComboBox _targetTypeCombo;
        private readonly CheckBox _scaleCheck;
        private readonly CheckBox _replaceCheck;
        private readonly TextBlock _warningText;
        private readonly TextBlock _tgtWarningText;
        private readonly TextBlock _sizeText;

        private readonly TextBox _srcMinBox;
        private readonly TextBox _srcMaxBox;
        private readonly TextBox _tgtMinBox;
        private readonly TextBox _tgtMaxBox;
        private readonly StackPanel _scalePanel;

        // Supported target types (Complex excluded).
        private static readonly Type[] TargetTypes =
        [
            typeof(byte), typeof(sbyte),
            typeof(short), typeof(ushort),
            typeof(int), typeof(uint),
            typeof(long), typeof(ulong),
            typeof(float), typeof(double),
        ];

        // Persisted across dialog invocations.
        private static Type _lastTargetType = typeof(ushort);
        private static string? _lastTgtMin;
        private static string? _lastTgtMax;

        internal ConvertValueTypeDialog(string srcTypeName, double lutMin, double lutMax, IMatrixData? srcData = null)
        {
            _srcTypeName = srcTypeName;
            _lutMin = lutMin;
            _lutMax = lutMax;
            _srcData = srcData;

            Title = "Convert Value Type";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            CanResize = true;
            CanMaximize = false;
            CanMinimize = false;
            ShowInTaskbar = false;

            _targetTypeCombo = new ComboBox { MinWidth = 115, Width = double.NaN, Height = 24, FontSize = 11, MinHeight = 0, Padding = new Thickness(8, 0, 4, 0) };
            _targetTypeCombo.ItemsSource = Array.ConvertAll(TargetTypes, MatrixData.GetValueTypeName);
            int defaultIdx = Array.IndexOf(TargetTypes, _lastTargetType);
            _targetTypeCombo.SelectedIndex = defaultIdx >= 0 ? defaultIdx : Array.IndexOf(TargetTypes, typeof(ushort));

            _scaleCheck = ControlFactory.MakeCheckBox("Scale values during conversion", fontSize: 11);
            _replaceCheck = ControlFactory.MakeCheckBox("Replace current data", fontSize: 11,
                hint: "If checked, replaces the current data in this window. If unchecked (default), result opens in a new window.");

            _sizeText = new TextBlock { FontSize = 10, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };

            _warningText = new TextBlock
            {
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Orange,
                Margin = new Thickness(0, 2),
            };

            _tgtWarningText = new TextBlock
            {
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Orange,
                Margin = new Thickness(0, 1),
                MinHeight = 14, // reserve space so layout doesn't jump
            };

            _srcMinBox = MakeValueBox();
            _srcMaxBox = MakeValueBox();
            _tgtMinBox = MakeValueBox();
            _tgtMaxBox = MakeValueBox();

            _srcMinBox.Text = FormatValue(_lutMin);
            _srcMaxBox.Text = FormatValue(_lutMax);

            _scalePanel = BuildScalePanel();
            Content = BuildContent();

            _scaleCheck.IsCheckedChanged += (_, _) => UpdateUiState();
            _targetTypeCombo.SelectionChanged += (_, _) => UpdateUiState();
            _tgtMinBox.TextChanged += (_, _) => UpdateTgtWarning();
            _tgtMaxBox.TextChanged += (_, _) => UpdateTgtWarning();
            UpdateUiState();
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

            var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            typeRow.Children.Add(new TextBlock
            {
                Text = $"Convert \u2018{_srcTypeName}\u2019 \u2192",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            typeRow.Children.Add(_targetTypeCombo);
            if (_srcData != null)
                typeRow.Children.Add(_sizeText);
            panel.Children.Add(typeRow);

            panel.Children.Add(_warningText);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 2)));
            panel.Children.Add(_scaleCheck);
            panel.Children.Add(_scalePanel);
            panel.Children.Add(_replaceCheck);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));

            var okBtn = new Button
            {
                Content = "OK",
                FontSize = 11, Height = 24, MinHeight = 0, MinWidth = 60,
                Padding = new Thickness(8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                FontSize = 11, Height = 24, MinHeight = 0, MinWidth = 60,
                Padding = new Thickness(8, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment= VerticalAlignment.Center,
            };
            okBtn.Click += (_, _) => TryClose();
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

        private StackPanel BuildScalePanel()
        {
            var sp = new StackPanel { Margin = new Thickness(14, 2, 0, 0), Spacing = 4 };

            var useLutBtn = new Button
            {
                Content = "LUT range",
                FontSize = 11, Height = 20, MinHeight = 0,
                Padding = new Thickness(6, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(useLutBtn, "Fill Source min/max from the current LUT (display range) of this window.");
            useLutBtn.Click += (_, _) =>
            {
                _srcMinBox.Text = FormatValue(_lutMin);
                _srcMaxBox.Text = FormatValue(_lutMax);
            };
            var useDataBtn = new Button
            {
                Content = "Data range",
                FontSize = 11, Height = 20, MinHeight = 0,
                Padding = new Thickness(6, 0),
                IsEnabled = _srcData != null,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(useDataBtn, "Fill Source min/max from the global min/max of all frames (cached; may be approximate if not all frames are scanned).");
            useDataBtn.Click += (_, _) =>
            {
                if (_srcData == null) return;
                var (dataMin, dataMax) = _srcData.GetGlobalValueRange(out _, forceRefresh: false);
                if (!double.IsNaN(dataMin) && !double.IsNaN(dataMax))
                {
                    _srcMinBox.Text = FormatValue(dataMin);
                    _srcMaxBox.Text = FormatValue(dataMax);
                }
            };
            var btnRow2 = new WrapPanel { Orientation = Orientation.Horizontal };
            btnRow2.Children.Add(useLutBtn);
            btnRow2.Children.Add(new Border { Width = 6 });
            btnRow2.Children.Add(useDataBtn);
            sp.Children.Add(btnRow2);
            sp.Children.Add(MakePairRow("Source:", _srcMinBox, _srcMaxBox));
            sp.Children.Add(ControlFactory.MakeSep(new Thickness(0, 1)));
            sp.Children.Add(MakePairRow("Target:", _tgtMinBox, _tgtMaxBox));
            sp.Children.Add(_tgtWarningText);

            return sp;
        }

        private static StackPanel MakePairRow(string label, TextBox minBox, TextBox maxBox)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock { Text = label, FontSize = 11, Width = 46, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "Min:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(minBox);
            row.Children.Add(new TextBlock { Text = "Max:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(maxBox);
            return row;
        }

        private static TextBox MakeValueBox() => new TextBox
        {
            Width = 90,
            Height = 22,
            MinHeight = 0,
            FontSize = 11,
            Padding = new Thickness(4, 2),
        };

        // ── UI state ──────────────────────────────────────────────────────────

        private static bool IsFloatingPoint(Type t) => t == typeof(float) || t == typeof(double);

        private void UpdateUiState()
        {
            bool doScale = _scaleCheck.IsChecked == true;
            _scalePanel.IsVisible = doScale;

            var tgt = SelectedTargetType();

            // Update size label: show source size and (if type changes) arrow to target size.
            if (_srcData != null)
            {
                long srcBytes = (long)_srcData.XCount * _srcData.YCount * _srcData.FrameCount * _srcData.ElementSize;
                static string Fmt(long b) => b >= 1024 * 1024
                    ? $"{b / (1024.0 * 1024.0):F1}\u00a0MB"
                    : $"{b / 1024.0:F1}\u00a0KB";
                if (tgt != _srcData.ValueType)
                {
                    int tgtElemSize = System.Runtime.InteropServices.Marshal.SizeOf(tgt);
                    long tgtBytes = (long)_srcData.XCount * _srcData.YCount * _srcData.FrameCount * tgtElemSize;
                    _sizeText.Text = $"({Fmt(srcBytes)} \u2192 {Fmt(tgtBytes)})";
                }
                else
                {
                    _sizeText.Text = $"({Fmt(srcBytes)})";
                }
            }

            if (!doScale)
            {
                _tgtWarningText.Text = string.Empty;
                UpdateDirectCastWarning(tgt);
            }
            else
            {
                if (_srcData?.ValueType == tgt)
                    _warningText.Text = "\u2139 Type unchanged \u2014 values will be rescaled within the same type.";
                else
                    _warningText.Text = "\u2139 Values will be linearly mapped to the target range.";
                _warningText.Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255));

                var (lo, hi) = GetTypeDefaultScaleRange(tgt, _srcData);
                if (string.IsNullOrWhiteSpace(_tgtMinBox.Text)) _tgtMinBox.Text = _lastTgtMin ?? lo;
                if (string.IsNullOrWhiteSpace(_tgtMaxBox.Text)) _tgtMaxBox.Text = _lastTgtMax ?? hi;
                UpdateTgtWarning();
            }
        }

        private void UpdateTgtWarning()
        {
            if (_tgtWarningText == null) return;
            var tgt = SelectedTargetType();
            // Only meaningful for integral targets in scale mode.
            if (IsFloatingPoint(tgt) || _scaleCheck.IsChecked != true)
            {
                _tgtWarningText.Text = string.Empty;
                return;
            }
            var (typeMin, typeMax) = GetTypeMinMax(tgt);
            bool minOob = double.TryParse(_tgtMinBox.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double tgtMin)
                && tgtMin < typeMin;
            bool maxOob = double.TryParse(_tgtMaxBox.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double tgtMax)
                && tgtMax > typeMax;
            if (minOob || maxOob)
            {
                var (lo, hi) = GetTypeRangeText(tgt);
                _tgtWarningText.Text = $"\u26a0 Target range exceeds {MatrixData.GetValueTypeName(tgt)} [{lo}, {hi}] \u2014 out-of-range values will be clamped.";
            }
            else
            {
                _tgtWarningText.Text = string.Empty;
            }
        }

        private void UpdateDirectCastWarning(Type tgt)
        {
            if (_srcData?.ValueType == tgt)
            {
                _warningText.Text = "\u26a0 Same type selected with no scaling \u2014 this produces an identical copy with no effect.";
                _warningText.Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160));
                return;
            }

            // Float/double targets accept all finite values without meaningful clamping.
            if (IsFloatingPoint(tgt))
            {
                _warningText.Text = "\u2139 Direct cast to floating-point: full precision preserved.";
                _warningText.Foreground = new SolidColorBrush(Color.FromRgb(100, 180, 255));
                return;
            }

            // For integral targets, check actual data range if available.
            if (_srcData != null)
            {
                var (dataMin, dataMax) = _srcData.GetGlobalValueRange(out _, forceRefresh: false);
                if (!double.IsNaN(dataMin) && !double.IsNaN(dataMax))
                {
                    var (typeMin, typeMax) = GetTypeMinMax(tgt);
                    bool overflow = dataMin < typeMin || dataMax > typeMax;
                    if (!overflow)
                    {
                        _warningText.Text = $"\u2713 All data values ({FormatValue(dataMin)}\u2013{FormatValue(dataMax)}) fit in {MatrixData.GetValueTypeName(tgt)} range.";
                        _warningText.Foreground = new SolidColorBrush(Color.FromRgb(80, 200, 120));
                        return;
                    }
                    else
                    {
                        _warningText.Text = $"\u26a0 Data range [{FormatValue(dataMin)}, {FormatValue(dataMax)}] exceeds {MatrixData.GetValueTypeName(tgt)} range. Out-of-range values will be clamped.";
                        _warningText.Foreground = Brushes.Orange;
                        return;
                    }
                }
                // data range not yet computed
                _warningText.Text = "\u26a0 Direct cast: data range not yet computed. Out-of-range values will be clamped.";
                _warningText.Foreground = Brushes.Orange;
                return;
            }

            var (lo, hi) = GetTypeRangeText(tgt);
            _warningText.Text = $"\u26a0 Direct cast: values outside [{lo}, {hi}] will be clamped.";
            _warningText.Foreground = Brushes.Orange;
        }

        // ── OK / validation ───────────────────────────────────────────────────

        private void TryClose()
        {
            var tgt = SelectedTargetType();
            bool doScale = _scaleCheck.IsChecked == true;
            bool replaceData = _replaceCheck.IsChecked == true;

            _lastTargetType = tgt;

            if (!doScale)
            {
                Close(((Type, bool, bool, double, double, double, double)?)(tgt, false, replaceData, 0.0, 0.0, 0.0, 0.0));
                return;
            }

            if (!TryParseBox(_srcMinBox, "Source Min", out double srcMin)) return;
            if (!TryParseBox(_srcMaxBox, "Source Max", out double srcMax)) return;
            if (!TryParseBox(_tgtMinBox, "Target Min", out double tgtMin)) return;
            if (!TryParseBox(_tgtMaxBox, "Target Max", out double tgtMax)) return;

            _lastTgtMin = _tgtMinBox.Text;
            _lastTgtMax = _tgtMaxBox.Text;

            Close(((Type, bool, bool, double, double, double, double)?)(tgt, true, replaceData, srcMin, srcMax, tgtMin, tgtMax));
        }

        private bool TryParseBox(TextBox box, string fieldName, out double value)
        {
            if (double.TryParse(box.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value))
                return true;

            _warningText.Text = $"\u274c {fieldName}: \u2018{box.Text}\u2019 is not a valid number.";
            _warningText.Foreground = Brushes.OrangeRed;
            box.Focus();
            return false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Type SelectedTargetType()
        {
            int idx = _targetTypeCombo.SelectedIndex;
            return idx >= 0 && idx < TargetTypes.Length ? TargetTypes[idx] : typeof(ushort);
        }

        private static string FormatValue(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "0";
            return v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static (double Min, double Max) GetTypeMinMax(Type t)
        {
            if (t == typeof(byte))   return (byte.MinValue,   byte.MaxValue);
            if (t == typeof(sbyte))  return (sbyte.MinValue,  sbyte.MaxValue);
            if (t == typeof(short))  return (short.MinValue,  short.MaxValue);
            if (t == typeof(ushort)) return (ushort.MinValue, ushort.MaxValue);
            if (t == typeof(int))    return (int.MinValue,    int.MaxValue);
            if (t == typeof(uint))   return (uint.MinValue,   uint.MaxValue);
            if (t == typeof(long))   return (long.MinValue,   (double)long.MaxValue);
            if (t == typeof(ulong))  return (0.0,             (double)ulong.MaxValue);
            return (double.MinValue, double.MaxValue);
        }

        private static (string Lo, string Hi) GetTypeRangeText(Type t)
        {
            if (t == typeof(byte))   return ("0", "255");
            if (t == typeof(sbyte))  return ("-128", "127");
            if (t == typeof(short))  return ("-32768", "32767");
            if (t == typeof(ushort)) return ("0", "65535");
            if (t == typeof(int))    return ("-2147483648", "2147483647");
            if (t == typeof(uint))   return ("0", "4294967295");
            if (t == typeof(long))   return ("-2^63", "2^63\u22121");
            if (t == typeof(ulong))  return ("0", "2^64\u22121");
            if (t == typeof(float))  return ($"{float.MinValue:G4}", $"{float.MaxValue:G4}");
            return ($"{double.MinValue:G4}", $"{double.MaxValue:G4}");
        }

        private static (string Lo, string Hi) GetTypeDefaultScaleRange(Type t, IMatrixData? srcData = null)
        {
            if (t == typeof(byte))   return ("0", "255");
            if (t == typeof(sbyte))  return ("-128", "127");
            if (t == typeof(short))  return ("-32768", "32767");
            if (t == typeof(ushort)) return ("0", "65535");
            // For large integer types, default to the actual data range so the user
            // gets a meaningful starting point instead of an astronomically large number.
            if (t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong))
            {
                if (srcData != null)
                {
                    var (dataMin, dataMax) = srcData.GetGlobalValueRange(out _, forceRefresh: false);
                    if (!double.IsNaN(dataMin) && !double.IsNaN(dataMax))
                        return (FormatValue(dataMin), FormatValue(dataMax));
                }
                return ("0", "1");
            }
            // float / double: normalized 0-1
            return ("0", "1");
        }

        // ── Centered show ─────────────────────────────────────────────────────

        internal Task<(Type TargetType, bool DoScale, bool ReplaceData, double SrcMin, double SrcMax, double TgtMin, double TgtMax)?> ShowCenteredOnAsync(Window owner, Visual hostVisual)
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
            return ShowDialog<(Type TargetType, bool DoScale, bool ReplaceData, double SrcMin, double SrcMax, double TgtMin, double TgtMax)?>(owner);
        }
    }
}
