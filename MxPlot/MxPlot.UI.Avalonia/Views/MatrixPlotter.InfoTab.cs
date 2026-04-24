using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Utils;
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Info / Scale / Metadata tabs ─────────────────────────────────────

        /// <summary>Lock prefix prepended to read-only metadata key display names.</summary>
        private const string MetaLockPrefix = "\U0001F512 ";

        /// <summary>
        /// Strips the 🔒 display prefix and resolves visible-system-key display names
        /// back to the real metadata key.
        /// </summary>
        private static string ResolveMetaKey(string rawDisplayKey)
        {
            if (!rawDisplayKey.StartsWith(MetaLockPrefix))
                return rawDisplayKey;

            var stripped = rawDisplayKey[MetaLockPrefix.Length..];

            // Reverse-lookup: display name → internal key
            foreach (var kvp in PlotterConfigKeys.VisibleSystemKeys)
            {
                if (kvp.Value.Equals(stripped, System.StringComparison.OrdinalIgnoreCase))
                    return kvp.Key;
            }

            return stripped;
        }

        /// <summary>
        /// Returns <see langword="true"/> if the display key represents a read-only
        /// metadata entry (either a visible system key or a format header).
        /// </summary>
        private static bool IsDisplayKeyReadOnly(string rawDisplayKey, MxPlot.Core.IMatrixData? data)
        {
            if (rawDisplayKey.StartsWith(MetaLockPrefix))
                return true;
            if (data == null)
                return false;
            return data.IsFormatHeader(rawDisplayKey);
        }

        /// <summary>Rebuilds the Info tab grid from <see cref="_currentData"/>. Safe to call when the panel has not been created yet.</summary>
        private void RefreshInfoTab()
        {
            if (_scaleTabBody == null) 
                return;

            _scaleTabBody.Children.Clear();

            var data = _currentData;
            if (data == null)
            {
                _scaleTabBody.Children.Add(new TextBlock
                {
                    Text = "No data loaded.",
                    Margin = new Thickness(8, 10),
                    Opacity = 0.6,
                });
                RefreshMetaTab();
                return;
            }

            _scaleTabBody.Children.Add(BuildAxisInfoGrid(data));
            RefreshMetaTab();
        }

        /// <summary>
        /// Builds the axis-information table grid.
        /// Columns: Axis | N | Min | Max | Step | Unit
        /// XY Min/Max/Unit and Axis Min/Max (if not index-based) / Unit are live-editable.
        /// Changes apply immediately on LostFocus or Enter.
        /// </summary>
        private Grid BuildAxisInfoGrid(IMatrixData data)
        {
            var fmt = CultureInfo.InvariantCulture;
            var ns = System.Globalization.NumberStyles.Any;
            const double fs = 11.0;

            var g = new Grid { Margin = new Thickness(2, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(70)));  // col 0: Axis (wider to fit rename button)
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(35)));  // col 1: Num
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));     // col 2: Min
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));     // col 3: Max
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));     // col 4: Range
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));     // col 5: Step
            g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(40)));  // col 6: Unit

            int row = 0;
            void AddRow() => g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void Place(Control c, int r, int col, int colSpan = 1)
            {
                Grid.SetRow(c, r); Grid.SetColumn(c, col);
                if (colSpan > 1) Grid.SetColumnSpan(c, colSpan);
                g.Children.Add(c);
            }

            TextBlock Ro(string t, bool right = false) => new ()
            {
                Text = t,
                FontSize = fs,
                Padding = new Thickness(3, 2),
                TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            TextBlock Hdr(string t) => new ()
            {
                Text = t,
                FontSize = fs,
                FontWeight = FontWeight.SemiBold,
                Padding = new Thickness(3, 2),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Opacity = 0.75,
            };
            TextBox Ed(string t, bool readOnly = false)
            {
                // macOS adds a native scroll-view inset (~4-5 px) that clips right-aligned text.
                var padding = OperatingSystem.IsMacOS()
                    ? new Thickness(7, 1, 12, 1)
                    : new Thickness(7, 1);
                var tb = new TextBox
                {
                    Text = t,
                    FontSize = fs,
                    Padding = padding,
                    Height = 20,
                    MinHeight = 0,
                    MinWidth = 0,
                    IsReadOnly = readOnly,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                if (readOnly)
                {
                    tb.Background = Brushes.Transparent;
                    tb.BorderThickness = new Thickness(0);
                    tb.Cursor = new Cursor(StandardCursorType.Arrow);
                    tb.Opacity = 0.6;
                }
                return tb;
            }
            void Wire(TextBox tb, Action apply)
            {
                void Refresh()
                {
                    apply();
                    if (_view.IsFitToView) _view.FitToView(); else _view.InvalidateSurface();
                    _orthoController.RefreshCrosshairAndSlices();
                }
                tb.LostFocus += (_, _) => Refresh();
                tb.KeyDown += (_, e) => { if (e.Key == Key.Return) Refresh(); };
            }
            Border HSep(byte alpha = 60) => new Border
            {
                Height = 1,
                Margin = new Thickness(0, 2),
                Background = new SolidColorBrush(Color.FromArgb(alpha, 160, 160, 160)),
            };

            // ── Header ───────────────────────────────────────────────────
            AddRow();
            Place(Hdr("Axis"), row, 0);
            Place(Hdr("Num"), row, 1);
            Place(Hdr("Min"), row, 2);
            Place(Hdr("Max"), row, 3);
            Place(Hdr("Range"), row, 4);
            Place(Hdr("Step"), row, 5);
            Place(Hdr("Unit"), row, 6);
            row++;

            AddRow(); Place(HSep(80), row++, 0, 7);

            // ── X row ──────────────────────────────────────────────────────
            var xMinEd = Ed(data.XMin.ToString("G5", fmt));
            var xMaxEd = Ed(data.XMax.ToString("G5", fmt));
            var xRngEd = Ed(data.XRange.ToString("G5", fmt));
            var xStpEd = Ed(data.XStep.ToString("G4", fmt));
            var xUniEd = Ed(data.XUnit ?? ""); xUniEd.HorizontalContentAlignment = HorizontalAlignment.Left;

            // Min → Max fixed, recompute Range and Step
            Wire(xMinEd, () => { if (double.TryParse(xMinEd.Text, ns, fmt, out var v)) data.XMin = v; xRngEd.Text = data.XRange.ToString("G5", fmt); xStpEd.Text = data.XStep.ToString("G4", fmt); });
            // Max → Min fixed, recompute Range and Step
            Wire(xMaxEd, () => { if (double.TryParse(xMaxEd.Text, ns, fmt, out var v)) data.XMax = v; xRngEd.Text = data.XRange.ToString("G5", fmt); xStpEd.Text = data.XStep.ToString("G4", fmt); });
            // Range → Max = Min + Range, recompute Step
            Wire(xRngEd, () => { if (double.TryParse(xRngEd.Text, ns, fmt, out var r)) { data.XMax = data.XMin + r; xMaxEd.Text = data.XMax.ToString("G5", fmt); xStpEd.Text = data.XStep.ToString("G4", fmt); } });
            // Step  → Max = Min + Step × (N-1), recompute Range
            Wire(xStpEd, () => { if (double.TryParse(xStpEd.Text, ns, fmt, out var s)) { data.XMax = data.XMin + s * (data.XCount - 1); xMaxEd.Text = data.XMax.ToString("G5", fmt); xRngEd.Text = data.XRange.ToString("G5", fmt); } });
            Wire(xUniEd, () => data.XUnit = xUniEd.Text ?? "");

            AddRow();
            Place(Ro("x"), row, 0);
            Place(Ro(data.XCount.ToString(), right: true), row, 1);
            Place(xMinEd, row, 2);
            Place(xMaxEd, row, 3);
            Place(xRngEd, row, 4);
            Place(xStpEd, row, 5);
            Place(xUniEd, row, 6);
            row++;

            // ── Y row ──────────────────────────────────────────────────────
            var yMinEd = Ed(data.YMin.ToString("G5", fmt));
            var yMaxEd = Ed(data.YMax.ToString("G5", fmt));
            var yRngEd = Ed(data.YRange.ToString("G5", fmt));
            var yStpEd = Ed(data.YStep.ToString("G4", fmt));
            var yUniEd = Ed(data.YUnit ?? ""); yUniEd.HorizontalContentAlignment = HorizontalAlignment.Left;

            // Min → Max fixed, recompute Range and Step
            Wire(yMinEd, () => { if (double.TryParse(yMinEd.Text, ns, fmt, out var v)) data.YMin = v; yRngEd.Text = data.YRange.ToString("G5", fmt); yStpEd.Text = data.YStep.ToString("G4", fmt); });
            // Max → Min fixed, recompute Range and Step
            Wire(yMaxEd, () => { if (double.TryParse(yMaxEd.Text, ns, fmt, out var v)) data.YMax = v; yRngEd.Text = data.YRange.ToString("G5", fmt); yStpEd.Text = data.YStep.ToString("G4", fmt); });
            // Range → Max = Min + Range, recompute Step
            Wire(yRngEd, () => { if (double.TryParse(yRngEd.Text, ns, fmt, out var r)) { data.YMax = data.YMin + r; yMaxEd.Text = data.YMax.ToString("G5", fmt); yStpEd.Text = data.YStep.ToString("G4", fmt); } });
            // Step  → Max = Min + Step × (N-1), recompute Range
            Wire(yStpEd, () => { if (double.TryParse(yStpEd.Text, ns, fmt, out var s)) { data.YMax = data.YMin + s * (data.YCount - 1); yMaxEd.Text = data.YMax.ToString("G5", fmt); yRngEd.Text = data.YRange.ToString("G5", fmt); } });
            Wire(yUniEd, () => data.YUnit = yUniEd.Text ?? "");

            AddRow();
            Place(Ro("y"), row, 0);
            Place(Ro(data.YCount.ToString(), right: true), row, 1);
            Place(yMinEd, row, 2);
            Place(yMaxEd, row, 3);
            Place(yRngEd, row, 4);
            Place(yStpEd, row, 5);
            Place(yUniEd, row, 6);
            row++;

            // ── Axis rows ────────────────────────────────────────────────
            if (data.Axes.Count > 0)
            {
                AddRow(); Place(HSep(), row++, 0, 7);

                foreach (var axis in data.Axes)
                {
                    var ca = axis;
                    bool ro = axis.IsIndexBased;  // Channel etc.: Min/Max fixed at 0..Count-1

                    var aMinEd = Ed(axis.Min.ToString("G5", fmt), readOnly: ro);
                    var aMaxEd = Ed(axis.Max.ToString("G5", fmt), readOnly: ro);
                    var aRngEd = Ed((axis.Max - axis.Min).ToString("G5", fmt), readOnly: ro);
                    var aStpEd = Ed(axis.Step.ToString("G4", fmt), readOnly: ro);
                    var aUniEd = Ed(axis.Unit); aUniEd.HorizontalContentAlignment = HorizontalAlignment.Left;

                    if (!ro)
                    {
                        // Min → Max fixed, recompute Range and Step
                        Wire(aMinEd, () => { if (double.TryParse(aMinEd.Text, ns, fmt, out var v)) { ca.Min = v; aRngEd.Text = (ca.Max - ca.Min).ToString("G5", fmt); aStpEd.Text = ca.Step.ToString("G4", fmt); } });
                        // Max → Min fixed, recompute Range and Step
                        Wire(aMaxEd, () => { if (double.TryParse(aMaxEd.Text, ns, fmt, out var v)) { ca.Max = v; aRngEd.Text = (ca.Max - ca.Min).ToString("G5", fmt); aStpEd.Text = ca.Step.ToString("G4", fmt); } });
                        // Range → Max = Min + Range, recompute Step
                        Wire(aRngEd, () => { if (double.TryParse(aRngEd.Text, ns, fmt, out var r)) { ca.Max = ca.Min + r; aMaxEd.Text = ca.Max.ToString("G5", fmt); aStpEd.Text = ca.Step.ToString("G4", fmt); } });
                        // Step  → Max = Min + Step × (N-1), recompute Range
                        Wire(aStpEd, () => { if (double.TryParse(aStpEd.Text, ns, fmt, out var s)) { ca.Max = ca.Min + s * (ca.Count - 1); aMaxEd.Text = ca.Max.ToString("G5", fmt); aRngEd.Text = (ca.Max - ca.Min).ToString("G5", fmt); } });
                    }

                    Wire(aUniEd, () => ca.Unit = aUniEd.Text ?? "");

                    AddRow();
                    var editBtn = new Button
                    {
                        Content = new PathIcon { Data = MenuIcons.Edit, Width = 10, Height = 10 },
                        Padding = new Thickness(1),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Width = 16,
                        Height = 16,
                        MinWidth = 0,
                        MinHeight = 0,
                        VerticalAlignment = VerticalAlignment.Center,
                        Cursor = new Cursor(StandardCursorType.Hand),
                    };
                    ToolTip.SetTip(editBtn, "Rename axis");
                    editBtn.Click += async (_, _) => await RenameAxisAsync(data, ca);
                    var nameCell = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    nameCell.Children.Add(new TextBlock
                    {
                        Text = axis.Name,
                        FontSize = fs,
                        Padding = new Thickness(3, 2, 1, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    nameCell.Children.Add(editBtn);
                    Place(nameCell, row, 0);
                    Place(Ro(axis.Count.ToString(), right: true), row, 1);
                    Place(aMinEd, row, 2); Place(aMaxEd, row, 3); Place(aRngEd, row, 4); Place(aStpEd, row, 5); Place(aUniEd, row, 6);
                    row++;
                }
            }

            return g;
        }

        // ── Axis rename ──────────────────────────────────────────────────────

        /// <summary>
        /// Renames an axis interactively. For specialized axis types (TaggedAxis, ColorChannel, FovAxis)
        /// the user is warned that downgrading to a plain <see cref="Axis"/> will discard type-specific
        /// data. Duplicate-name validation is performed before the rename is applied.
        /// </summary>
        private async Task RenameAxisAsync(IMatrixData data, Axis axis)
        {
            bool needsDowngrade = axis is FovAxis || axis is TaggedAxis;
            if (needsDowngrade)
            {
                string typeName = axis switch
                {
                    ColorChannel => "Color Channel",
                    FovAxis => "FOV",
                    TaggedAxis => "Tagged",
                    _ => "specialized"
                };
                bool ok = await ShowConfirmDialogAsync(
                    "Rename Axis",
                    $"This axis has a specialized type ({typeName}). Renaming will convert it to a plain axis, "
                    + "discarding its specialized properties (tags, color assignments, tile layout, etc.).\n\nContinue?");
                if (!ok) return;
            }

            var dlg = new AxisRenameDialog(axis.Name);
            await dlg.ShowDialog(this);
            if (dlg.Result == null) return;

            string newName = dlg.Result.Trim();
            if (string.IsNullOrEmpty(newName) || newName == axis.Name) return;

            bool isDuplicate =
                newName.Equals("x", StringComparison.OrdinalIgnoreCase) ||
                newName.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                data.Axes.Any(a => !ReferenceEquals(a, axis) &&
                    a.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
            if (isDuplicate)
            {
                await ShowMessageDialogAsync("Rename Axis", $"An axis named \u201c{newName}\u201d already exists.");
                return;
            }

            // If the Volume view is showing this axis, deactivate it first to avoid
            // stale axis-name references inside OrthogonalViewController after the rename.
            if (_orthoController.ActiveAxisName?.Equals(axis.Name, StringComparison.OrdinalIgnoreCase) == true)
            {
                foreach (var child in _trackerPanel.Children)
                {
                    if (child is AxisTracker t && t.FreezeButton.IsChecked == true)
                    {
                        t.FreezeButton.IsChecked = false;
                        break;
                    }
                }
            }

            if (needsDowngrade)
            {
                int savedIdx = axis.Index;
                Axis newPlainAxis = newName.Equals("Channel", StringComparison.OrdinalIgnoreCase)
                    ? Axis.Channel(axis.Count, axis.Unit)
                    : new Axis(axis.Count, axis.Min, axis.Max, newName, axis.Unit, false);
                // Reuse original axis objects for non-renamed axes so existing AxisTrackers keep working
                var newAxes = data.Axes
                    .Select(a => ReferenceEquals(a, axis) ? newPlainAxis : a)
                    .ToArray();
                data.DefineDimensions(newAxes);
                newPlainAxis.Index = savedIdx;
                RebuildTrackerPanel(data);
            }
            else
            {
                if (newName.Equals("Channel", StringComparison.OrdinalIgnoreCase))
                {
                    axis.IsIndexBased = true; // auto-resets min=0, max=count-1
                    axis.Name = "Channel";
                }
                else
                {
                    axis.IsIndexBased = false;
                    axis.Name = newName; // AxisTracker updates its label automatically via Axis.NameChanged
                }
            }

            RefreshInfoTab();
        }

        // ── Metadata tab ─────────────────────────────────────────────────────

        /// <summary>Builds and wires the Metadata tab, adding it to <paramref name="tabControl"/>.</summary>
        private void BuildMetadataTab(TabControl tabControl)
        {
            // left panel: key list + add/delete toolbar + inline new-key input
            _metaKeyList = new ListBox
            {
                FontSize = 11,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
            };
            _metaNewKeyBox = new TextBox
            {
                Watermark = "New key\u2026",
                FontSize = 11,
                Padding = new Thickness(4, 2),
                Margin = new Thickness(2, 2, 2, 0),
                MinWidth = 60,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            var metaNewKeyOk = new Button { Content = "\u2713", FontSize = 10, Padding = new Thickness(6, 2), Margin = new Thickness(2, 2, 0, 0) };
            var metaNewKeyCancel = new Button { Content = "\u2717", FontSize = 10, Padding = new Thickness(6, 2), Margin = new Thickness(2, 2, 0, 0) };
            var metaNewKeyRow = new StackPanel { Orientation = Orientation.Horizontal, IsVisible = false, Margin = new Thickness(0, 0, 0, 2) };
            metaNewKeyRow.Children.Add(_metaNewKeyBox);
            metaNewKeyRow.Children.Add(metaNewKeyOk);
            metaNewKeyRow.Children.Add(metaNewKeyCancel);
            var metaAddBtn = new Button { Content = "+", FontSize = 10, Padding = new Thickness(8, 2), Margin = new Thickness(0, 0, 4, 0) };
            var metaDelBtn = new Button { Content = "\u2212", FontSize = 10, Padding = new Thickness(8, 2) };
            ToolTip.SetTip(metaAddBtn, "Add new key");
            ToolTip.SetTip(metaDelBtn, "Delete selected key");
            var metaKeyBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 2, 2) };
            metaKeyBtns.Children.Add(metaAddBtn);
            metaKeyBtns.Children.Add(metaDelBtn);
            var leftPanel = new Grid();
            leftPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            leftPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(_metaKeyList, 0);
            Grid.SetRow(metaKeyBtns, 1);
            leftPanel.Children.Add(_metaKeyList);
            leftPanel.Children.Add(metaKeyBtns);

            // right panel: value TextBox + copy/save toolbar
            _metaValueBox = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                FontSize = 11,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(6, 4),
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            _metaCopyBtn = new Button { Content = "\ud83d\udccb Copy", FontSize = 10, Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 4, 0), IsEnabled = false };
            _metaSaveBtn = new Button { Content = "Apply", FontSize = 10, Padding = new Thickness(6, 2), IsEnabled = false, IsVisible = false };
            _metaSaveBtn.Classes.Add("accent");
            ToolTip.SetTip(_metaCopyBtn, "Copy full raw value to clipboard");
            ToolTip.SetTip(_metaSaveBtn, "Write edited value back to Metadata");
            var metaValueBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 2, 2, 2) };
            metaValueBtns.Children.Add(_metaCopyBtn);
            metaValueBtns.Children.Add(_metaSaveBtn);
            var rightPanel = new Grid();
            rightPanel.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            rightPanel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(_metaValueBox, 0);
            Grid.SetRow(metaValueBtns, 1);
            rightPanel.Children.Add(_metaValueBox);
            rightPanel.Children.Add(metaValueBtns);

            async Task<bool> MetadataNeedsRevertAsync()
            {
                if (_metaSaveBtn == null || !_metaSaveBtn.IsEnabled || _metaPreviousKey == null) return false;
                var result = await MetaDirtyPromptDialog.ShowAsync(this, _metaPreviousKey);
                if (result == null)  // Cancel / window closed
                    return true;
                if (result == MetaDirtyResult.Save)
                    await SaveCurrentMetaValueAsync(_metaPreviousKey, showFeedback: true);
                // Discard: fall through
                return false;
            }

            // event handlers
            _metaKeyList.SelectionChanged += async (_, _) =>
            {
                if (_metaSwitchGuard) return;
                if (_metaValueBox == null || _metaCopyBtn == null || _metaSaveBtn == null) return;
                if (_metaKeyList?.SelectedItem is not string rawKey) return;

                // Strip the lock prefix and resolve display names to internal keys
                var key = ResolveMetaKey(rawKey);

                if(await MetadataNeedsRevertAsync())
                {//canceled
                    _metaSwitchGuard = true;
                    try
                    {
                        if (_metaKeyList != null)
                            _metaKeyList.SelectedItem = _metaPreviousKey;
                    }
                    finally
                    {
                        _metaSwitchGuard = false;
                    }
                    return;
                } //else: Save or discard


                _metaPreviousKey = rawKey;

                _metaLoadCts?.Cancel();
                _metaLoadCts = new CancellationTokenSource();
                var cts = _metaLoadCts;

                if (_currentData?.Metadata.TryGetValue(key, out var raw) != true || raw == null)
                {
                    _metaRawValue = null;
                    _metaValueBox.Text = string.Empty;
                    _metaValueBox.IsReadOnly = true;
                    _metaCopyBtn.IsEnabled = false;
                    _metaSaveBtn.IsEnabled = false;
                    _metaSaveBtn.IsVisible = false;
                    return;
                }

                _metaRawValue = raw;
                _metaCopyBtn.IsEnabled = true;
                _metaValueBox.Text = "\u2026";
                _metaValueBox.IsReadOnly = true;
                _metaSaveBtn.IsEnabled = false;
                _metaSaveBtn.IsVisible = false;

                string formatted;
                try
                {
                    formatted = await Task.Run(() => StructuredTextUtil.TryFormat(raw), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (cts.IsCancellationRequested)
                    return;

                _metaDisplayedValue = formatted;
                _metaValueBox.Text = formatted;
                _metaValueBox.IsReadOnly = IsDisplayKeyReadOnly(rawKey, _currentData);
            };

            Action confirmAdd = () =>
            {
                var newKey = _metaNewKeyBox?.Text?.Trim();
                if (string.IsNullOrEmpty(newKey) || _currentData == null) return;
                if (PlotterConfigKeys.IsReserved(newKey))
                {
                    if (_metaNewKeyBox != null)
                    {
                        ToolTip.SetTip(_metaNewKeyBox, $"\u26d4 '{PlotterConfigKeys.Prefix}*' is reserved");
                        ToolTip.SetShowDelay(_metaNewKeyBox, 0);
                        ToolTip.SetIsOpen(_metaNewKeyBox, true);
                    }
                    return;
                }
                if (!_currentData.Metadata.ContainsKey(newKey))
                    _currentData.Metadata[newKey] = string.Empty;
                metaNewKeyRow.IsVisible = false;
                if (_metaNewKeyBox != null)
                {
                    _metaNewKeyBox.Text = string.Empty;
                    _metaNewKeyBox.Watermark = "New key\u2026";
                }
                RefreshMetaTab(newKey);
            };
            Action cancelAdd = () =>
            {
                metaNewKeyRow.IsVisible = false;
                if (_metaNewKeyBox != null) _metaNewKeyBox.Text = string.Empty;
            };

            metaAddBtn.Click += async (_, _) =>
            {
                if (await MetadataNeedsRevertAsync()) 
                    return; //Canceled to return to the current key, so do not open the new-key input

                metaNewKeyRow.IsVisible = !metaNewKeyRow.IsVisible;
                if (metaNewKeyRow.IsVisible) _metaNewKeyBox?.Focus();
            };
            metaNewKeyOk.Click += (_, _) => confirmAdd();
            metaNewKeyCancel.Click += (_, _) => cancelAdd();
            _metaNewKeyBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) confirmAdd();
                else if (e.Key == Key.Escape) cancelAdd();
            };
            _metaNewKeyBox.TextChanged += (_, _) =>
            {
                if (_metaNewKeyBox != null)
                {
                    ToolTip.SetIsOpen(_metaNewKeyBox, false);
                    ToolTip.SetTip(_metaNewKeyBox, null);
                }
            };

            metaDelBtn.Click += (_, _) =>
            {
                if (_currentData == null || _metaKeyList?.SelectedItem is not string delRawKey) return;
                var delKey = ResolveMetaKey(delRawKey);
                if (IsDisplayKeyReadOnly(delRawKey, _currentData)) return;
                _currentData.Metadata.Remove(delKey);
                RefreshMetaTab();
            };

            _metaCopyBtn.Click += async (_, _) =>
            {
                var val = _metaRawValue;
                if (val == null) return;
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) return;
                try { await clipboard.SetTextAsync(val); } catch { }
            };

            _metaSaveBtn.Click += async (_, _) =>
            {
                if (_currentData == null || _metaKeyList?.SelectedItem is not string saveRawKey) return;
                var saveKey = ResolveMetaKey(saveRawKey);
                if (IsDisplayKeyReadOnly(saveRawKey, _currentData)) return;
                await SaveCurrentMetaValueAsync(saveKey, showFeedback: true);
            };

            _metaValueBox.TextChanged += (_, _) =>
            {
                if (_metaSaveBtn == null || _metaValueBox == null || _metaValueBox.IsReadOnly) return;
                _metaSaveBtn.IsEnabled = _metaValueBox.Text != _metaDisplayedValue;
                _metaSaveBtn.IsVisible = _metaSaveBtn.IsEnabled;
            };

            // assemble grid
            var metaKeyCol = new ColumnDefinition(new GridLength(110)) { MinWidth = 50 };
            var metaGrid = new Grid();
            metaGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            metaGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            metaGrid.ColumnDefinitions.Add(metaKeyCol);
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var metaSplitter = new GridSplitter
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                Background = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            };
            Grid.SetRow(leftPanel, 0);    Grid.SetColumn(leftPanel, 0);
            Grid.SetRow(metaSplitter, 0); Grid.SetColumn(metaSplitter, 1);
            Grid.SetRow(rightPanel, 0);   Grid.SetColumn(rightPanel, 2);
            Grid.SetRow(metaNewKeyRow, 1); Grid.SetColumnSpan(metaNewKeyRow, 3);
            metaGrid.Children.Add(leftPanel);
            metaGrid.Children.Add(metaSplitter);
            metaGrid.Children.Add(rightPanel);
            metaGrid.Children.Add(metaNewKeyRow);

            static Control TabHdr(string t, Geometry? icon = null)
            {
                if (icon == null)
                    return new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeight.Bold };
                var pathIcon = new PathIcon { Data = icon, Width = 12, Height = 12 };
                var brush = MenuIcons.DefaultBrush(icon);
                if (brush != null) pathIcon.Foreground = brush;
                return new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        pathIcon,
                        new TextBlock { Text = t, FontSize = 11, FontWeight = FontWeight.Bold },
                    }
                };
            }

            tabControl.Items.Add(new TabItem
            {
                Header = TabHdr("Metadata", MenuIcons.Metadata),
                Padding = new Thickness(8, 3),
                Content = metaGrid,
            });
        }

        /// <summary>
        /// Minifies and stores the current value box content under <paramref name="key"/>,
        /// then re-displays the formatted result.
        /// Pass <paramref name="showFeedback"/> = <c>true</c> to show the "✓ Formatted" button flash.
        /// </summary>
        private async Task SaveCurrentMetaValueAsync(string key, bool showFeedback = false)
        {
            if (_currentData == null) return;
            var newVal = _metaValueBox?.Text ?? string.Empty;
            var minified = StructuredTextUtil.TryMinify(newVal);
            _currentData.Metadata[key] = minified;
            _metaRawValue = minified;

            var formatted = StructuredTextUtil.TryFormat(minified);
            _metaDisplayedValue = formatted;   // set before Text to keep Apply disabled
            if (_metaValueBox != null) _metaValueBox.Text = formatted;
            if (_metaSaveBtn != null) _metaSaveBtn.IsEnabled = false;

            if (showFeedback && formatted != newVal && _metaSaveBtn != null)
            {
                _metaSaveBtn.IsVisible = true;
                _metaSaveBtn.Content = "\u2713 Formatted";
                await Task.Delay(1500);
                if (_metaSaveBtn != null) { _metaSaveBtn.Content = "Apply"; _metaSaveBtn.IsVisible = false; }
            }
            else
            {
                if (_metaSaveBtn != null) _metaSaveBtn.IsVisible = false;
            }
        }

        /// <summary>
        /// Repopulates the Metadata tab key list and clears the value box.
        /// Safe to call when the tab has not been built yet.
        /// </summary>
        private void RefreshMetaTab(string? selectKey = null)
        {
            if (_metaKeyList == null || _metaValueBox == null) return;
            _metaValueBox.Text = string.Empty;
            _metaValueBox.IsReadOnly = true;
            _metaRawValue = null;
            _metaDisplayedValue = null;
            if (_metaCopyBtn != null) _metaCopyBtn.IsEnabled = false;
            if (_metaSaveBtn != null) { _metaSaveBtn.IsEnabled = false; _metaSaveBtn.IsVisible = false; }

            var data = _currentData;
            if (data == null || data.Metadata.Count == 0)
            {
                _metaKeyList.ItemsSource = null;
                return;
            }

            // Build the display list:
            //  - Hide reserved system keys (mxplot.*) unless in VisibleSystemKeys
            //  - Visible system keys → display name with 🔒 prefix (always read-only)
            //  - Format header keys → original key with 🔒 prefix (read-only)
            //  - Normal keys → original key as-is
            _metaKeyList.ItemsSource = null;
            var fhKeys = data.GetFormatHeaderKeys();
            var visible = PlotterConfigKeys.VisibleSystemKeys;
            _metaKeyList.ItemsSource = data.Metadata.Keys
                .Where(k => !PlotterConfigKeys.IsReserved(k))
                .Select(k =>
                {
                    if (visible.TryGetValue(k, out var displayName))
                        return $"\U0001F512 {displayName}";
                    if (fhKeys.Contains(k))
                        return $"\U0001F512 {k}";
                    return k;
                })
                .ToList();

            if (selectKey != null)
                _metaKeyList.SelectedItem = selectKey;
            if (_metaKeyList.SelectedItem == null)
                _metaKeyList.SelectedIndex = 0;
        }

            }
        }
