using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.UI.Avalonia.Formula;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Metadata;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Creates new data from a formula. With no reference windows it generates data on a grid the user
    /// defines; with references (<c>m1</c>, <c>m2</c>, ...) the formula can read their data, for example
    /// <c>m1 - m2</c>. Create makes the result, double data in a new, independent window, and the dialog stays
    /// open for that one result: Create becomes Update, which makes the same window again from the formula,
    /// references and grid as they now are. A result made with "Keep updated" is a <see cref="LiveCalculation"/>,
    /// one frame that follows the reference windows; while the dialog is open, Update can make the result follow
    /// or stop following. Such a result is loaded into a dialog again with <see cref="EditLive"/>, where it keeps
    /// following.
    /// </summary>
    /// <remarks>
    /// The dialog does not look for windows itself: the host passes the windows that may be referenced,
    /// so an application that keeps a list of its windows offers exactly those.
    /// </remarks>
    public sealed class CreateDataDialog : Window
    {
        /// <summary>
        /// The roles <see cref="Axis.Z"/>, <see cref="Axis.Channel"/> and <see cref="Axis.Time"/> create,
        /// each of which can be added once and has a fixed name, and a free axis for anything else.
        /// </summary>
        private enum AxisKind { Z, Channel, Time, Other }

        private sealed class Reference
        {
            public required string Alias;
            public required MatrixPlotter Source;
            public required EventHandler ClosedHandler;
        }

        private const double WarnBytes = 1024.0 * 1024 * 1024;

        // The formula of the last result Create made and how many references it had: the next dialog starts on it
        // when it has as many references, since the same formula is often used again for other windows.
        private static (string Formula, int References)? _lastCreated;

        /// <summary>Forgets the formula the next dialog would start on.</summary>
        internal static void ForgetLastFormula() => _lastCreated = null;

        /// <summary>
        /// The width of the label column of the Name row. What each section holds is indented by it, so that it
        /// lines up with the left edge of the Name box; the section labels stay at the left.
        /// </summary>
        private const double ContentIndent = 40;

        private readonly Func<IReadOnlyList<MatrixPlotter>> _candidates;
        private readonly List<Reference> _refs = [];
        private readonly List<ExtraAxisRow> _extraAxes = [];
        // The formula the dialog put in the box itself; the box still showing it means the user has not written one.
        private string _defaultFormula = "";
        private CancellationTokenSource? _cts;

        private readonly TextBox _nameBox;
        private readonly StackPanel _refListPanel = new() { Spacing = 2 };
        private readonly ComboBox _outputGridCombo;
        private readonly CheckBox _customSizeCheck;
        private readonly AxisScaleRow _xRow = new(256, -1, 1, "");
        private readonly AxisScaleRow _yRow = new(256, -1, 1, "");
        private readonly Grid _scaleGrid;
        private readonly Button _addZBtn = SmallButton("+ Z");
        private readonly Button _addChannelBtn = SmallButton("+ Channel");
        private readonly Button _addTimeBtn = SmallButton("+ Time");
        private readonly Button _addOtherBtn = SmallButton("+ Other");
        private readonly FormulaInput _formula;
        private readonly FormulaGuide _guide = new();
        private readonly TextBlock _cheatSheet;
        private readonly TextBlock _sizeText = new() { FontSize = 10.5, Opacity = 0.8, Margin = new Thickness(8, 0, 2, 2) };
        private readonly TextBlock _statusText = new()
        {
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0),
        };
        private readonly ProgressBar _progressBar = new() { Height = 4, MinHeight = 0, IsVisible = false, Margin = new Thickness(0, 0, 0, 3) };
        private readonly Button _createBtn = new() { Content = "Create", FontSize = 11, Width = 70 };
        private readonly Button _closeBtn = new() { Content = "Close", FontSize = 11, Width = 70 };
        private readonly Button _updateBtn = new() { Content = "Update", FontSize = 11, Width = 70, IsVisible = false };
        private readonly CheckBox _keepCheck;

        // The window Create opened last, which Update computes into; null once it is closed.
        private MatrixPlotter? _lastResult;
        private EventHandler? _lastResultClosed;

        // The link keeping that window up to date, if there is one.
        private LiveCalculation? _lastLive;

        /// <summary>
        /// Whether the dialog has made a result, or was given one to change (<see cref="EditLive"/>): Create is then
        /// Update, which makes that same result again, for as long as its window is open.
        /// </summary>
        public bool HasResult => _lastResult != null;

        // Whether "Keep updated" cannot be changed: the dialog was given a result that follows its references (EditLive).
        private bool _keepFixed;

        /// <summary>Raised when a result starts being kept up to date, by Create or Update with "Keep updated".</summary>
        public event Action<LiveCalculation>? LiveCalculationStarted;

        /// <summary>Opens the dialog.</summary>
        /// <param name="owner">
        /// The window the dialog stays above and is centered on, or <see langword="null"/> for an
        /// independent window in the middle of the screen, which other windows can cover.
        /// </param>
        /// <param name="candidates">
        /// Returns the windows "Add Window" offers. It is called each time the list is opened, so it may
        /// return the windows open at that moment.
        /// </param>
        /// <param name="references">Windows to start with as <c>m1</c>, <c>m2</c>, ... in this order.</param>
        public static CreateDataDialog Show(Window? owner, Func<IReadOnlyList<MatrixPlotter>> candidates,
            IEnumerable<MatrixPlotter>? references = null)
        {
            var dialog = new CreateDataDialog(candidates ?? throw new ArgumentNullException(nameof(candidates)), references);
            if (owner != null)
            {
                dialog.Show(owner);
            }
            else
            {
                // Without an owner the window can end up behind others, so it needs a taskbar entry to be found.
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                dialog.ShowInTaskbar = true;
                dialog.Show();
            }
            return dialog;
        }

        /// <summary>
        /// Replaces the reference windows with <paramref name="references"/>, as <c>m1</c>, <c>m2</c>, ... in
        /// this order. A formula the user has edited is kept; an untouched one becomes the default for
        /// the new number of references.
        /// </summary>
        public void SetReferences(IEnumerable<MatrixPlotter> references)
        {
            ArgumentNullException.ThrowIfNull(references);
            foreach (var r in _refs) r.Source.Closed -= r.ClosedHandler;
            _refs.Clear();
            foreach (var plotter in references) AddReferenceCore(plotter);
            RefreshAll();
        }

        private CreateDataDialog(Func<IReadOnlyList<MatrixPlotter>> candidates, IEnumerable<MatrixPlotter>? references)
        {
            _candidates = candidates;

            Title = "Create Data";
            Width = 600;
            Height = 480;
            MinWidth = 520;
            MinHeight = 420;
            CanResize = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 11;

            Styles.Add(new Style(s => s.OfType<Button>())
            {
                Setters =
                {
                    new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
                    new Setter(ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
                },
            });

            _nameBox = new TextBox
            {
                Text = "Result",
                FontSize = 11,
                Height = 24,
                MinHeight = 0,
                Padding = new Thickness(6, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            _scaleGrid = CreateScaleGridSkeleton();
            _xRow.CountChanged += UpdateSize;
            _yRow.CountChanged += UpdateSize;

            _addZBtn.Click += (_, _) => AddAxis(AxisKind.Z);
            _addChannelBtn.Click += (_, _) => AddAxis(AxisKind.Channel);
            _addTimeBtn.Click += (_, _) => AddAxis(AxisKind.Time);
            _addOtherBtn.Click += (_, _) => AddAxis(AxisKind.Other);

            _outputGridCombo = new ComboBox
            {
                FontSize = 11,
                Height = 24,
                MinHeight = 0,
                MinWidth = 180,
                Padding = new Thickness(8, 2),
            };
            _outputGridCombo.SelectionChanged += (_, _) => { ApplyOutputGrid(); UpdateNames(); };

            _customSizeCheck = ControlFactory.MakeCheckBox("Custom size / scale",
                hint: "Define the grid and the axes here instead of taking them from a reference window");
            _customSizeCheck.IsCheckedChanged += (_, _) =>
            {
                UpdateGridControlsEnabled();
                ApplyOutputGrid();
                UpdateNames();
            };

            _keepCheck = ControlFactory.MakeCheckBox("Keep updated");
            _keepCheck.IsCheckedChanged += (_, _) =>
            {
                ApplyOutputGrid();
                UpdateNames();
                UpdateUpdateButton();
            };

            _formula = new FormulaInput("", CheckFormula, history: FormulaHistory.CreateData);
            _formula.Changed += UpdateStatusVisibility;

            _cheatSheet = new TextBlock
            {
                FontSize = 10.5,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            };

            _createBtn.Click += async (_, _) => await CreateAsync();
            _updateBtn.Click += async (_, _) => await UpdateAsync();
            _createBtn.IsDefault = true;
            _closeBtn.IsCancel = true;
            _closeBtn.Click += (_, _) =>
            {
                // While a computation runs, the button cancels it instead of closing.
                if (_cts != null) _cts.Cancel();
                else Close();
            };
            Closed += (_, _) =>
            {
                _cts?.Cancel();
                foreach (var r in _refs) r.Source.Closed -= r.ClosedHandler;
                TrackResult(null);
            };

            Content = BuildLayout();

            if (references != null)
                foreach (var plotter in references) AddReferenceCore(plotter);
            RefreshAll();
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private Control BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(10, 8, 10, 8) };
            // name | references | add-window | output grid | scale (stretches) | formula label | formula | cheat sheet | footer
            root.RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto,Auto,Auto");

            int r = 0;
            void AddAt(Control c) { Grid.SetRow(c, r++); root.Children.Add(c); }

            var nameSection = new Grid
            {
                ColumnDefinitions = [new ColumnDefinition(new GridLength(ContentIndent)), new ColumnDefinition(GridLength.Star)],
            };
            var nameLabel = SectionLabel("Name");
            nameLabel.Margin = new Thickness(0);
            nameLabel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_nameBox, 1);
            nameSection.Children.Add(nameLabel);
            nameSection.Children.Add(_nameBox);
            AddAt(nameSection);

            var refSection = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            refSection.Children.Add(SectionLabel("Reference Data"));
            _refListPanel.Margin = new Thickness(ContentIndent, 0, 0, 0);
            refSection.Children.Add(_refListPanel);
            AddAt(refSection);

            var addBtn = SmallButton("+ Add Window…");
            addBtn.Margin = new Thickness(ContentIndent, 2, 0, 0);
            addBtn.HorizontalAlignment = HorizontalAlignment.Left;
            addBtn.Click += (_, _) => ShowWindowChooser(addBtn);
            AddAt(addBtn);

            var gridSection = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0) };
            gridSection.Children.Add(SectionLabel("Output Grid"));
            var gridRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            gridRow.Children.Add(new TextBlock { Text = "From:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            gridRow.Children.Add(_outputGridCombo);
            gridRow.Margin = new Thickness(ContentIndent, 0, 0, 0);
            _customSizeCheck.Margin = new Thickness(ContentIndent, 0, 0, -7);
            gridSection.Children.Add(gridRow);
            gridSection.Children.Add(_customSizeCheck);
            AddAt(gridSection);

            var scaleSection = new Grid { Margin = new Thickness(0, 6, 0, 0), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            scaleSection.Children.Add(SectionLabel("Dimensions / Scale"));
            var scaleScroll = new ScrollViewer
            {
                Content = _scaleGrid,
                VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
            scaleScroll.Margin = new Thickness(ContentIndent, 0, 0, 0);
            Grid.SetRow(scaleScroll, 1);
            scaleSection.Children.Add(scaleScroll);
            var addAxisRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 0),
            };
            addAxisRow.Children.Add(_addZBtn);
            addAxisRow.Children.Add(_addChannelBtn);
            addAxisRow.Children.Add(_addTimeBtn);
            addAxisRow.Children.Add(_addOtherBtn);
            var axisFooter = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            _sizeText.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(_sizeText, 1);
            axisFooter.Children.Add(addAxisRow);
            axisFooter.Children.Add(_sizeText);
            axisFooter.Margin = new Thickness(ContentIndent, 0, 0, 0);
            Grid.SetRow(axisFooter, 2);
            scaleSection.Children.Add(axisFooter);
            AddAt(scaleSection);

            var formulaHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 2) };
            var formulaLabel = SectionLabel("Formula:  Result =");
            formulaLabel.Margin = new Thickness(0);
            formulaLabel.VerticalAlignment = VerticalAlignment.Center;
            formulaHeader.Children.Add(formulaLabel);
            // The guide opens at the right, so the dialog grows and shrinks by its width.
            var guideBtn = _guide.CreateToggleButton(shown =>
            {
                double delta = shown ? FormulaGuide.PanelWidth : -FormulaGuide.PanelWidth;
                MinWidth += delta;
                Width += delta;
            });
            Grid.SetColumn(guideBtn, 1);
            formulaHeader.Children.Add(guideBtn);
            AddAt(formulaHeader);
            _formula.View.Margin = new Thickness(ContentIndent, 0, 0, 0);
            _cheatSheet.Margin = new Thickness(ContentIndent, 2, 0, 0);
            AddAt(_formula.View);
            AddAt(_cheatSheet);

            // The hint is dropped first when the window is short, so the rows above never overlap it.
            SizeChanged += (_, e) => _cheatSheet.IsVisible = e.NewSize.Height >= 440;

            var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            bottom.Children.Add(ControlFactory.MakeSep(new Thickness(0, 0, 0, 3)));
            bottom.Children.Add(_progressBar);
            // The formula error and the status share the left end of the button row, so neither adds a row.
            var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            _keepCheck.Margin = new Thickness(8, 0, 14, -7);
            _keepCheck.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_keepCheck, 1);
            footer.Children.Add(_keepCheck);
            var messages = new Panel();
            _formula.ErrorText.VerticalAlignment = VerticalAlignment.Center;
            _formula.ErrorText.Margin = new Thickness(0, 0, 8, 0);
            messages.Children.Add(_statusText);
            messages.Children.Add(_formula.ErrorText);
            footer.Children.Add(messages);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(btnRow, 2);
            btnRow.Children.Add(_updateBtn);
            btnRow.Children.Add(_createBtn);
            btnRow.Children.Add(_closeBtn);
            footer.Children.Add(btnRow);
            bottom.Children.Add(footer);
            AddAt(bottom);

            var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            layout.Children.Add(root);
            _guide.View.Margin = new Thickness(0, 8, 10, 8);
            Grid.SetColumn(_guide.View, 1);
            layout.Children.Add(_guide.View);
            return layout;
        }

        private static Button SmallButton(string text) => new() { Content = text, FontSize = 11, MinHeight = 0, Padding = new Thickness(8, 3) };

        private static TextBlock SectionLabel(string text, double topMargin = 0) => new()
        {
            Text = text,
            FontWeight = FontWeight.SemiBold,
            FontSize = 11,
            Margin = new Thickness(0, topMargin, 0, 2),
        };

        /// <summary>
        /// The columns follow the Scale tab of <see cref="MatrixPlotter"/> (Axis, Count, Min, Max, Range,
        /// Step, Unit), with the formula variable in front and, for added axes, an index-based checkbox
        /// and a remove button behind.
        /// </summary>
        private static Grid CreateScaleGridSkeleton()
        {
            // Fixed widths: the table reads as a compact form and should not stretch with the dialog.
            var g = new Grid { Margin = new Thickness(2, 4), HorizontalAlignment = HorizontalAlignment.Left };
            foreach (double width in new double[] { 64, 78, 45, 58, 58, 58, 58, 44, 30, 22 })
                g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(width)));
            return g;
        }

        private static TextBlock Hdr(string t) => new()
        {
            Text = t,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(3, 2),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0.75,
        };

        private static TextBlock Ro(string t) => new()
        {
            Text = t,
            FontSize = 11,
            Padding = new Thickness(3, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static TextBlock VarLabel(string t) => new()
        {
            Text = t,
            FontSize = 10,
            Opacity = 0.65,
            Padding = new Thickness(3, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>
        /// Rebuilds the header, the X/Y rows and one row per added axis, and assigns each axis its formula
        /// variables. The rows keep their text boxes, so an edit in progress survives.
        /// </summary>
        private void RebuildScaleGrid()
        {
            _scaleGrid.Children.Clear();
            _scaleGrid.RowDefinitions.Clear();

            int row = 0;
            // One fixed height, so rows space alike whichever control in them is tallest.
            void AddRow() => _scaleGrid.RowDefinitions.Add(new RowDefinition(new GridLength(26)));
            void Place(Control c, int col)
            {
                Grid.SetRow(c, row);
                Grid.SetColumn(c, col);
                _scaleGrid.Children.Add(c);
            }
            void PlaceScale(AxisScaleRow s)
            {
                Place(s.NumEd, 2);
                Place(s.MinEd, 3);
                Place(s.MaxEd, 4);
                Place(s.RngEd, 5);
                Place(s.StpEd, 6);
                Place(s.UniEd, 7);
            }

            AddRow();
            string[] headers = ["", "", "Count", "Min", "Max", "Range", "Step", "Unit", "Idx"];
            for (int c = 0; c < headers.Length; c++) Place(Hdr(headers[c]), c);
            row++;

            _scaleGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var sep = new Border { Height = 1, Margin = new Thickness(0, 2), Background = new SolidColorBrush(Color.FromArgb(80, 160, 160, 160)) };
            Place(sep, 0);
            Grid.SetColumnSpan(sep, 10);
            row++;

            AddRow();
            Place(VarLabel("ix (x)"), 0);
            Place(Ro("X"), 1);
            PlaceScale(_xRow);
            row++;

            AddRow();
            Place(VarLabel("iy (y)"), 0);
            Place(Ro("Y"), 1);
            PlaceScale(_yRow);
            row++;

            int other = 0;
            foreach (var axis in _extraAxes)
            {
                (axis.IndexVariable, axis.ValueVariable) = FormulaEvaluator.AxisVariable(axis.Name, ref other);

                AddRow();
                Place(VarLabel($"{axis.IndexVariable} ({axis.ValueVariable})"), 0);
                Place(axis.NameControl, 1);
                PlaceScale(axis.Scale);
                Place(axis.IndexBasedCell, 8);
                Place(axis.RemoveBtn, 9);
                row++;
            }
        }

        // ── Axes ──────────────────────────────────────────────────────────────

        private bool IsGridEditable => _refs.Count == 0 || _customSizeCheck.IsChecked == true;

        /// <summary>
        /// Whether the result is kept up to date from the references, and so is a single frame: the extra axes are
        /// then not used. It is the choice in the dialog, except for a result loaded with <see cref="EditLive"/>, which
        /// keeps following.
        /// </summary>
        internal bool KeepUpdated
        {
            get => _lastResult != null && _keepFixed ? _lastLive != null : _keepCheck.IsChecked == true && _keepCheck.IsEnabled;
            set
            {
                if (!(_lastResult != null && _keepFixed)) _keepCheck.IsChecked = value;
            }
        }

        /// <summary>The axes the result has: none while it is kept up to date.</summary>
        private IReadOnlyList<ExtraAxisRow> ActiveAxes => KeepUpdated ? Array.Empty<ExtraAxisRow>() : _extraAxes;

        private void AddAxis(AxisKind kind)
        {
            if (kind != AxisKind.Other && _extraAxes.Any(a => a.Kind == kind)) return;

            string name = kind switch
            {
                AxisKind.Z => "Z",
                AxisKind.Channel => "Channel",
                AxisKind.Time => "Time",
                _ => $"Axis{_extraAxes.Count(a => a.Kind == AxisKind.Other) + 1}",
            };
            AddAxisRow(new ExtraAxisRow(kind, name, 5, 0, 4, "", indexBased: kind == AxisKind.Channel, source: null));
            UpdateNames();
        }

        private void AddAxisRow(ExtraAxisRow row)
        {
            row.Scale.CountChanged += UpdateSize;
            row.NameChanged += UpdateNames;
            row.RemoveBtn.Click += (_, _) =>
            {
                _extraAxes.Remove(row);
                UpdateNames();
            };
            _extraAxes.Add(row);
        }

        private void UpdateAddAxisButtons()
        {
            bool editable = IsGridEditable && !KeepUpdated;
            _addZBtn.IsEnabled = editable && !_extraAxes.Any(a => a.Kind == AxisKind.Z);
            _addChannelBtn.IsEnabled = editable && !_extraAxes.Any(a => a.Kind == AxisKind.Channel);
            _addTimeBtn.IsEnabled = editable && !_extraAxes.Any(a => a.Kind == AxisKind.Time);
            _addOtherBtn.IsEnabled = editable;
        }

        // ── References ────────────────────────────────────────────────────────

        /// <summary>
        /// Lists the windows that can still be referenced in a flyout under <paramref name="anchor"/>; choosing one
        /// adds it. A flyout is drawn inside this dialog, so it stays above the dialog and can be used when the
        /// dialog is modal, which a window of its own opened from here would not.
        /// </summary>
        private void ShowWindowChooser(Control anchor)
        {
            var candidates = _candidates()
                .Where(p => p.MatrixData != null && !_refs.Any(r => ReferenceEquals(r.Source, p)))
                .ToList();
            if (candidates.Count == 0)
            {
                ShowStatus("There is no other window to reference.", isError: true);
                return;
            }

            var list = new ListBox
            {
                ItemsSource = candidates.Select(TitleOf).ToArray(),
                MinWidth = 220,
                MaxHeight = 240,
                FontSize = 11,
            };
            var flyout = new Flyout { Content = list, Placement = PlacementMode.BottomEdgeAlignedLeft };
            list.SelectionChanged += (_, _) =>
            {
                int index = list.SelectedIndex;
                if (index < 0) return;
                flyout.Hide();
                AddReferenceCore(candidates[index]);
                RefreshAll();
            };
            flyout.ShowAt(anchor);
        }

        private void AddReferenceCore(MatrixPlotter plotter)
        {
            if (plotter.MatrixData == null || _refs.Any(r => ReferenceEquals(r.Source, plotter))) return;

            Reference? entry = null;
            // A reference whose window closes is dropped, since its data is released with the window.
            EventHandler closed = (_, _) => { if (entry != null) RemoveReference(entry); };
            entry = new Reference { Alias = $"m{_refs.Count + 1}", Source = plotter, ClosedHandler = closed };
            plotter.Closed += closed;
            _refs.Add(entry);
        }

        private void RemoveReference(Reference entry)
        {
            if (!_refs.Remove(entry)) return;
            entry.Source.Closed -= entry.ClosedHandler;
            for (int i = 0; i < _refs.Count; i++) _refs[i].Alias = $"m{i + 1}";
            RefreshAll();
        }

        private static string TitleOf(MatrixPlotter plotter) => plotter.Title is { Length: > 0 } t ? t : "(untitled)";

        // ── Refresh ───────────────────────────────────────────────────────────

        private void RefreshAll()
        {
            UpdateKeepAvailability();
            _refListPanel.Children.Clear();
            foreach (var entry in _refs)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new TextBlock { Text = $"{entry.Alias}:", FontWeight = FontWeight.SemiBold, FontSize = 11, Width = 26, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = TitleOf(entry.Source), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                var removeBtn = new Button { Content = "✕", FontSize = 10, Width = 20, Height = 18, MinHeight = 0, Padding = new Thickness(0) };
                removeBtn.Click += (_, _) => RemoveReference(entry);
                row.Children.Add(removeBtn);
                _refListPanel.Children.Add(row);
            }
            if (_refs.Count == 0)
                _refListPanel.Children.Add(new TextBlock { Text = "(none: the formula generates data from the coordinates)", FontSize = 11, Opacity = 0.55 });

            int previous = _outputGridCombo.SelectedIndex;
            _outputGridCombo.ItemsSource = _refs.Select(r => $"{r.Alias}: {TitleOf(r.Source)}").ToArray();
            _outputGridCombo.SelectedIndex = _refs.Count == 0 ? -1 : Math.Clamp(previous, 0, _refs.Count - 1);

            UpdateGridControlsEnabled();
            ApplyOutputGrid();
            // The names a formula may use (axis variables) are known after this; the starting formula is checked against them.
            UpdateNames();
            UpdateDefaultFormula();
            UpdateUpdateButton();
        }

        /// <summary>Keeping a result up to date needs a window to follow; the reason is in the tooltip.</summary>
        private void UpdateKeepAvailability()
        {
            bool hasReferences = _refs.Count > 0;
            if (_lastResult != null && _keepFixed)
            {
                // A result loaded for editing keeps following its references.
                _keepCheck.IsEnabled = false;
                _keepCheck.IsChecked = _lastLive != null;
                ToolTip.SetTip(_keepCheck, _lastLive != null
                    ? "This result follows its references."
                    : "This result no longer follows its references.");
            }
            else
            {
                _keepCheck.IsEnabled = hasReferences;
                if (!hasReferences && _keepCheck.IsChecked == true) _keepCheck.IsChecked = false;
                ToolTip.SetTip(_keepCheck, hasReferences
                    ? "Recompute whenever a reference changes. One frame."
                    : "Needs a reference window.");
            }
            ToolTip.SetShowOnDisabled(_keepCheck, true);
        }

        /// <summary>
        /// Create makes the result; once it exists the same button is Update, which makes it again. Enter does
        /// whichever is shown.
        /// </summary>
        private void UpdateButtons()
        {
            bool hasResult = _lastResult != null;
            _createBtn.IsVisible = !hasResult;
            _updateBtn.IsVisible = hasResult;
            _createBtn.IsDefault = !hasResult;
            _updateBtn.IsDefault = hasResult;
            UpdateKeepAvailability();
            UpdateUpdateButton();
        }

        /// <summary>
        /// Update cannot keep the last result up to date from a window that depends on that result, or from the result
        /// itself; the reason is in the tooltip.
        /// </summary>
        private void UpdateUpdateButton()
        {
            bool cycle = KeepUpdated && _lastResult != null
                && LiveCalculation.WouldCycle(_lastResult, _refs.Select(r => r.Source));
            bool noReference = _lastLive != null && _refs.Count == 0;
            _updateBtn.IsEnabled = !cycle && !noReference && _cts == null;
            ToolTip.SetTip(_updateBtn, cycle
                ? "A reference depends on this result."
                : noReference
                    ? "Needs a reference window."
                    : "Make this result again from the formula and references as they are");
            ToolTip.SetShowOnDisabled(_updateBtn, true);
        }

        private void UpdateGridControlsEnabled()
        {
            _outputGridCombo.IsEnabled = _refs.Count > 0 && _customSizeCheck.IsChecked != true;
            _customSizeCheck.IsEnabled = _refs.Count > 0;
        }

        /// <summary>
        /// Decides whether the grid and the axes are edited here or mirror a reference: they are editable
        /// with no reference or with Custom checked, and otherwise shown read-only as the selected
        /// reference has them, so that a stack is reproduced with its whole axis layout.
        /// </summary>
        private void ApplyOutputGrid()
        {
            if (IsGridEditable)
            {
                _xRow.SetReadOnly(false);
                _yRow.SetReadOnly(false);
                foreach (var a in _extraAxes) a.SetReadOnly(KeepUpdated);
                return;
            }

            var data = GridSource()?.MatrixData;
            if (data == null) return;

            _xRow.LoadFrom(data.XCount, data.XMin, data.XMax, data.XUnit ?? "");
            _yRow.LoadFrom(data.YCount, data.YMin, data.YMax, data.YUnit ?? "");
            _xRow.SetReadOnly(true);
            _yRow.SetReadOnly(true);

            _extraAxes.Clear();
            foreach (var axis in data.Axes)
            {
                // The Axis.Z / Channel / Time factories fix these names, so the name tells the role.
                var kind = axis.Name switch
                {
                    "Z" => AxisKind.Z,
                    "Channel" => AxisKind.Channel,
                    "Time" => AxisKind.Time,
                    _ => AxisKind.Other,
                };
                var row = new ExtraAxisRow(kind, axis.Name, axis.Count, axis.Min, axis.Max, axis.Unit, axis.IsIndexBased, source: axis);
                row.SetReadOnly(true);
                AddAxisRow(row);
            }
        }

        private MatrixPlotter? GridSource()
        {
            int index = _outputGridCombo.SelectedIndex;
            return index >= 0 && index < _refs.Count ? _refs[index].Source : null;
        }

        /// <summary>Brings everything that depends on the axis and reference names up to date.</summary>
        private void UpdateNames()
        {
            RebuildScaleGrid();
            UpdateAddAxisButtons();

            // Only what depends on this dialog's grid and references; operators and functions are in the guide.
            var text = "ix iy : pixel index    x y : physical coordinates    r = sqrt(x*x + y*y)";
            if (ActiveAxes.Count > 0)
                text += "\n" + string.Join("    ", ActiveAxes.Select(a => $"{a.IndexVariable} {a.ValueVariable} : {a.Name} index, value"));
            if (_refs.Count > 0)
                text += "\n" + string.Join(", ", _refs.Select(r => r.Alias)) + " (m1[ix, iy] or m1(x, y)) : reference";
            _cheatSheet.Text = text;

            UpdateSize();
            _formula.Validate();
            UpdateStatusVisibility();
        }

        private void UpdateSize()
        {
            double pixels = (double)_xRow.Count * _yRow.Count;
            foreach (var a in ActiveAxes) pixels *= a.Scale.Count;
            double bytes = pixels * sizeof(double);
            _sizeText.Text = $"{pixels:N0} px · {FormatBytes(bytes)}";
            // Cleared rather than set to null: a null local value would override the inherited brush.
            if (bytes > WarnBytes) _sizeText.Foreground = new SolidColorBrush(Color.FromRgb(220, 120, 0));
            else _sizeText.ClearValue(TextBlock.ForegroundProperty);
            _sizeText.FontWeight = bytes > WarnBytes ? FontWeight.SemiBold : FontWeight.Normal;
        }

        private static string FormatBytes(double bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            int u = 0;
            while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
            return $"{bytes:0.#} {units[u]}";
        }

        private void UpdateDefaultFormula()
        {
            if (_formula.Text != _defaultFormula) return;
            _defaultFormula = StartingFormula();
            _formula.Text = _defaultFormula;
        }

        /// <summary>
        /// The formula the dialog starts on: the one of the last result, when it has as many references as the dialog
        /// and is valid for them; otherwise one that suits the number of references.
        /// </summary>
        private string StartingFormula()
        {
            if (_lastCreated is { } last && last.References == _refs.Count && CheckFormula(last.Formula) == null)
                return last.Formula;

            return _refs.Count switch
            {
                0 => "sin(x) * cos(y)",
                1 => "m1",
                _ => "m1 - m2",
            };
        }

        // ── Formula ───────────────────────────────────────────────────────────

        private string[] VariableNames()
        {
            var names = new List<string>(FormulaEvaluator.PixelVariables);
            foreach (var a in ActiveAxes)
            {
                names.Add(a.IndexVariable);
                names.Add(a.ValueVariable);
            }
            return names.ToArray();
        }

        private FormulaException? CheckFormula(string text)
            => CompiledFormula.Check(text, VariableNames(), _refs.Select(r => r.Alias).ToArray(), ReferenceAxisNames());

        /// <summary>For each reference, the names of the axes of its data, which a formula can name: <c>m1[ix, iy, iz:2]</c>.</summary>
        private string[][] ReferenceAxisNames()
            => _refs.Select(r => (r.Source.MatrixData?.Axes ?? Array.Empty<Axis>()).Select(a => a.Name).ToArray()).ToArray();

        private void ShowStatus(string? message, bool isError = false)
        {
            _statusText.Text = message;
            if (isError) _statusText.Foreground = new SolidColorBrush(Color.FromRgb(200, 50, 50));
            else _statusText.ClearValue(TextBlock.ForegroundProperty);
            ToolTip.SetTip(_statusText, message);
            UpdateStatusVisibility();
        }

        // A formula error takes the place of the status line.
        private void UpdateStatusVisibility() => _statusText.IsVisible = _formula.Error == null;

        // ── Create ────────────────────────────────────────────────────────────

        /// <summary>The axes of the result: those of the grid's reference as they are, or built from the rows.</summary>
        private Axis[] BuildAxes()
            => ActiveAxes.Select(a => !IsGridEditable && a.Source != null ? a.Source.Clone() : a.ToAxis()).ToArray();

        private string? ValidateGrid()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in ActiveAxes)
            {
                if (a.Kind == AxisKind.Other && a.Name is "Z" or "Channel" or "Time")
                    return $"Add a {a.Name} axis with the + {a.Name} button instead of naming another axis so.";
                if (!names.Add(a.Name)) return $"Two axes are named '{a.Name}'.";
            }
            return null;
        }

        /// <summary>The formula box.</summary>
        internal FormulaInput Formula => _formula;

        /// <summary>Whether Update is offered: a window created from this dialog is still open.</summary>
        internal bool CanUpdate => _lastResult != null;

        /// <summary>Computes the result and opens it in a new window; what the Create button does.</summary>
        internal Task CreateAsync() => RunAsync(target: null);

        /// <summary>
        /// Computes the result into the window Create opened last, replacing its data; what the Update
        /// button does. Does nothing once that window is closed.
        /// </summary>
        internal Task UpdateAsync()
            => _lastResult is { } target
                && !(KeepUpdated && LiveCalculation.WouldCycle(target, _refs.Select(r => r.Source)))
                && !(_lastLive != null && _refs.Count == 0)
                ? RunAsync(target)
                : Task.CompletedTask;

        /// <summary>The link keeping the window created last up to date, or <see langword="null"/>.</summary>
        internal LiveCalculation? LastLive => _lastLive;

        /// <summary>
        /// Makes <paramref name="plotter"/> the window Update computes into, and shows the Update button for
        /// as long as it stays open; <see langword="null"/> forgets the window.
        /// </summary>
        private void TrackResult(MatrixPlotter? plotter)
        {
            if (_lastResult != null && _lastResultClosed != null) _lastResult.Closed -= _lastResultClosed;
            _lastResult = plotter;
            _lastResultClosed = null;
            if (plotter == null) _keepFixed = false;
            if (plotter != null)
            {
                _lastResultClosed = (_, _) => TrackResult(null);
                plotter.Closed += _lastResultClosed;
            }
            UpdateButtons();
        }

        /// <summary>
        /// Loads a result that is kept up to date into the dialog, so that Update can change its formula or its
        /// references: they become the dialog's references and formula, and the result the window Update makes
        /// again. It keeps following, as it was made.
        /// </summary>
        public void EditLive(LiveCalculation calc)
        {
            ArgumentNullException.ThrowIfNull(calc);
            if (!calc.IsActive || calc.Result.MatrixData is not { } data) return;

            foreach (var r in _refs) r.Source.Closed -= r.ClosedHandler;
            _refs.Clear();
            foreach (var input in calc.Inputs) AddReferenceCore(input);

            // The grid is the result's own, shown as a custom one.
            _customSizeCheck.IsChecked = true;
            _xRow.LoadFrom(data.XCount, data.XMin, data.XMax, data.XUnit ?? "");
            _yRow.LoadFrom(data.YCount, data.YMin, data.YMax, data.YUnit ?? "");
            _extraAxes.Clear();
            _nameBox.Text = TitleOf(calc.Result);
            _keepFixed = true;
            AttachLive(calc);
            TrackResult(calc.Result);

            RefreshAll();
            _defaultFormula = "";
            _formula.Text = calc.Formula;
            Title = "Edit Formula";
        }

        /// <summary>Ends the link of <paramref name="target"/> if it has one: the result stays, as an ordinary window.</summary>
        private void EndLastLive(MatrixPlotter target)
        {
            if (_lastLive == null || !ReferenceEquals(_lastLive.Result, target)) return;
            var old = _lastLive;
            _lastLive = null;
            old.End();
        }

        private void StartLive(MatrixPlotter target, CompiledFormula formula)
        {
            var calc = LiveCalculation.Start(target, _refs.Select(r => r.Source).ToArray(), formula);
            AttachLive(calc);
            LiveCalculationStarted?.Invoke(calc);
        }

        private void AttachLive(LiveCalculation calc)
        {
            _lastLive = calc;
            calc.Ended += (_, _) =>
            {
                if (!ReferenceEquals(_lastLive, calc)) return;
                _lastLive = null;
                UpdateButtons();
            };
            UpdateButtons();
        }

        /// <summary>Computes the result and shows it: in <paramref name="target"/> when given, otherwise in a new window.</summary>
        private async Task RunAsync(MatrixPlotter? target)
        {
            if (_cts != null) return;
            if (!_formula.Validate())
            {
                UpdateStatusVisibility();
                return;
            }
            if (ValidateGrid() is { } gridError)
            {
                ShowStatus(gridError, isError: true);
                return;
            }

            CompiledFormula formula;
            try
            {
                formula = CompiledFormula.Compile(_formula.Text, VariableNames(), _refs.Select(r => r.Alias).ToArray(), ReferenceAxisNames());
            }
            catch (FormulaException ex)
            {
                ShowStatus(ex.Message, isError: true);
                return;
            }

            var scale = new Scale2D(_xRow.Count, _xRow.Min, _xRow.Max, _yRow.Count, _yRow.Min, _yRow.Max, _xRow.Unit, _yRow.Unit);
            bool keep = KeepUpdated;
            var axes = BuildAxes();
            string name = _nameBox.Text is { Length: > 0 } n ? n : "Result";
            string formulaText = formula.Text.Trim();

            // Read on the UI thread: the data and the value mode each reference shows at this moment.
            var sources = new List<(IMatrixData Data, int ValueMode, string Title)>();
            foreach (var r in _refs)
            {
                if (r.Source.MatrixData is not { } data)
                {
                    ShowStatus($"{r.Alias} has no data.", isError: true);
                    return;
                }
                sources.Add((data, r.Source.DisplayedValueMode, TitleOf(r.Source)));
            }
            LookupTable? lut = (GridSource() ?? _refs.FirstOrDefault()?.Source)?.Lut;

            var cts = _cts = new CancellationTokenSource();
            SetBusy(true);
            ShowStatus("Calculating…");
            var progress = new Progress<int>(v =>
            {
                // The evaluator reports the negated frame count first, then each finished frame.
                if (v < 0) { _progressBar.Maximum = -v; _progressBar.Value = 0; }
                else _progressBar.Value = v + 1;
            });

            try
            {
                var result = await Task.Run(() =>
                {
                    var references = sources.Select(s => new FormulaReference(s.Data, s.ValueMode)).ToArray();
                    return FormulaEvaluator.EvaluateReferences(formula, scale, axes, references, progress, cts.Token);
                }, cts.Token);

                string? from = sources.Count == 0
                    ? null
                    : string.Join(", ", sources.Select((s, i) => $"m{i + 1}={s.Title}"));
                result.AppendHistory("Create Data", from, formulaText);
                FormulaHistory.CreateData.Add(formulaText);
                if (target != null && ReferenceEquals(target, _lastResult))
                {
                    // The same path as "Replace data": the window keeps its display settings where the
                    // new data allows, and windows derived from the replaced data are closed. An earlier link ends
                    // first, and a new one starts when the result is to be kept up to date.
                    EndLastLive(target);
                    // An update is the same result made again, so the window also stays where it is: the axis
                    // positions and the orthogonal view carry over for every axis the new data still has.
                    bool resume = target.ResumeSettingsEnabled;
                    target.ResumeSettingsEnabled = true;
                    try
                    {
                        ((ICommandHost)target).ReplaceData(result, null);
                    }
                    finally
                    {
                        target.ResumeSettingsEnabled = resume;
                    }
                    target.Title = name;
                    if (keep) StartLive(target, formula);
                    ShowStatus($"Updated '{name}'.");
                }
                else
                {
                    var plotter = MatrixPlotter.Create(result, lut, name);
                    plotter.Show();
                    if (keep) StartLive(plotter, formula);
                    TrackResult(plotter);
                    _lastCreated = (formulaText, _refs.Count);
                    ShowStatus($"Created '{name}'.");
                }
            }
            catch (OperationCanceledException)
            {
                ShowStatus("Cancelled.");
            }
            catch (OutOfMemoryException)
            {
                ShowStatus("Not enough memory for data of this size.", isError: true);
            }
            catch (Exception ex)
            {
                ShowStatus(ex.Message, isError: true);
            }
            finally
            {
                _cts = null;
                cts.Dispose();
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy)
        {
            _createBtn.IsEnabled = !busy;
            if (busy) _updateBtn.IsEnabled = false;
            else UpdateUpdateButton();
            _closeBtn.Content = busy ? "Cancel" : "Close";
            _progressBar.IsVisible = busy;
        }

        // ── Scale row ─────────────────────────────────────────────────────────

        /// <summary>
        /// One row of Count, Min, Max, Range, Step and Unit boxes that keep each other consistent, like a
        /// row of the Scale tab of <see cref="MatrixPlotter"/>. An edit takes effect on Enter or when the
        /// box loses focus.
        /// </summary>
        private sealed class AxisScaleRow
        {
            private const string ValueFormat = "G6";
            private const string StepFormat = "G4";
            private static readonly CultureInfo Fmt = CultureInfo.InvariantCulture;

            public int Count { get; private set; }
            public double Min { get; private set; }
            public double Max { get; private set; }
            public string Unit { get; private set; }

            // Index-based: Min/Max/Range/Step are pinned to 0..Count-1 and only Count stays editable.
            private bool _indexBased;
            private bool _readOnly;

            public double Range => Max - Min;
            public double Step => Count > 1 ? Range / (Count - 1) : 0;

            public event Action? CountChanged;

            public readonly TextBox NumEd;
            public readonly TextBox MinEd;
            public readonly TextBox MaxEd;
            public readonly TextBox RngEd;
            public readonly TextBox StpEd;
            public readonly TextBox UniEd;

            public AxisScaleRow(int count, double min, double max, string unit)
            {
                Count = count;
                Min = min;
                Max = max;
                Unit = unit;

                NumEd = Ed("");
                MinEd = Ed("");
                MaxEd = Ed("");
                RngEd = Ed("");
                StpEd = Ed("");
                UniEd = Ed(unit);
                UniEd.TextAlignment = TextAlignment.Left;
                ShowValues();

                Wire(NumEd, () =>
                {
                    if (int.TryParse(NumEd.Text, NumberStyles.Integer, Fmt, out var n) && n >= 1 && n != Count)
                    {
                        Count = n;
                        if (_indexBased) Max = Count - 1;
                        CountChanged?.Invoke();
                    }
                });
                Wire(MinEd, () => { if (TryRead(MinEd, out var v)) Min = v; });
                Wire(MaxEd, () => { if (TryRead(MaxEd, out var v)) Max = v; });
                Wire(RngEd, () => { if (TryRead(RngEd, out var r)) Max = Min + r; });
                Wire(StpEd, () => { if (TryRead(StpEd, out var s)) Max = Min + s * (Count - 1); });
                UniEd.LostFocus += (_, _) => Unit = UniEd.Text ?? "";
                UniEd.TextChanged += (_, _) => Unit = UniEd.Text ?? "";
            }

            private static bool TryRead(TextBox box, out double value)
                => double.TryParse(box.Text, NumberStyles.Float, Fmt, out value) && double.IsFinite(value);

            /// <summary>Applies an edit, then shows every box as the values now stand (an invalid entry reverts).</summary>
            private void Wire(TextBox box, Action apply)
            {
                void Commit()
                {
                    if (box.IsReadOnly) return;
                    apply();
                    ShowValues();
                }
                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;
                    Commit();
                    e.Handled = true;
                };
            }

            private void ShowValues()
            {
                NumEd.Text = Count.ToString(Fmt);
                MinEd.Text = Min.ToString(ValueFormat, Fmt);
                MaxEd.Text = Max.ToString(ValueFormat, Fmt);
                RngEd.Text = Range.ToString(ValueFormat, Fmt);
                StpEd.Text = Step.ToString(StepFormat, Fmt);
            }

            public void LoadFrom(int count, double min, double max, string unit)
            {
                Count = count;
                Min = min;
                Max = max;
                Unit = unit;
                UniEd.Text = unit;
                ShowValues();
                CountChanged?.Invoke();
            }

            public void SetReadOnly(bool readOnly)
            {
                _readOnly = readOnly;
                ApplyBoxStates();
            }

            public void SetIndexBased(bool indexBased)
            {
                _indexBased = indexBased;
                if (indexBased)
                {
                    Min = 0;
                    Max = Count - 1;
                    ShowValues();
                }
                ApplyBoxStates();
            }

            private void ApplyBoxStates()
            {
                SetBox(NumEd, _readOnly);
                SetBox(UniEd, _readOnly);
                foreach (var box in new[] { MinEd, MaxEd, RngEd, StpEd }) SetBox(box, _readOnly || _indexBased);
            }

            private static void SetBox(TextBox box, bool readOnly)
            {
                box.IsReadOnly = readOnly;
                if (readOnly) box.Background = Brushes.Transparent;
                else box.ClearValue(BackgroundProperty);
                box.BorderThickness = new Thickness(readOnly ? 0 : 1);
                box.Opacity = readOnly ? 0.6 : 1.0;
            }

            private static TextBox Ed(string text)
            {
                var box = new TextBox
                {
                    Text = text,
                    FontSize = 11,
                    Padding = new Thickness(6, 2),
                    Height = 22,
                    MinHeight = 0,
                    MinWidth = 0,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(box, global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
                return box;
            }
        }

        /// <summary>
        /// One axis beyond X and Y: Z, Channel or Time, whose name is fixed, or a free axis the user names.
        /// </summary>
        private sealed class ExtraAxisRow
        {
            public readonly AxisKind Kind;

            /// <summary>The axis of the reference this row mirrors, or <see langword="null"/> for a row the user added.</summary>
            public readonly Axis? Source;

            /// <summary>A label for Z, Channel and Time; a text box for a free axis.</summary>
            public readonly Control NameControl;
            private readonly TextBox? _nameEd;
            private readonly string _fixedName;

            public readonly CheckBox IndexBasedCheck;

            /// <summary>
            /// The checkbox scaled through the layout rather than the render transform: its template is
            /// taller than the row and top-aligned, so an unscaled box would sit low.
            /// </summary>
            public readonly Control IndexBasedCell;
            public readonly Button RemoveBtn;
            public readonly AxisScaleRow Scale;

            /// <summary>The formula variables of the axis, assigned whenever the rows are laid out.</summary>
            public string IndexVariable = "";
            public string ValueVariable = "";

            public event Action? NameChanged;

            public string Name => _nameEd != null ? (_nameEd.Text is { Length: > 0 } s ? s.Trim() : "Axis") : _fixedName;

            public ExtraAxisRow(AxisKind kind, string name, int count, double min, double max, string unit, bool indexBased, Axis? source)
            {
                Kind = kind;
                Source = source;
                _fixedName = name;
                if (kind == AxisKind.Other)
                {
                    _nameEd = new TextBox
                    {
                        Text = name,
                        FontSize = 11,
                        Height = 22,
                        MinHeight = 0,
                        MinWidth = 0,
                        Padding = new Thickness(4, 2),
                        VerticalContentAlignment = VerticalAlignment.Center,
                    };
                    _nameEd.LostFocus += (_, _) => NameChanged?.Invoke();
                    NameControl = _nameEd;
                }
                else
                {
                    NameControl = Ro(name);
                }

                IndexBasedCheck = new CheckBox
                {
                    FontSize = 11,
                    MinHeight = 0,
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsChecked = indexBased,
                    // Channel is always index-based and Z and Time never are; only a free axis can choose.
                    IsEnabled = kind == AxisKind.Other,
                };
                ToolTip.SetTip(IndexBasedCheck, "Index-based: the axis has no physical scale, only positions 0, 1, 2, …");
                RemoveBtn = new Button
                {
                    Content = "✕",
                    FontSize = 8,
                    Width = 16,
                    Height = 14,
                    MinHeight = 0,
                    MinWidth = 0,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Scale = new AxisScaleRow(count, min, max, unit);
                Scale.SetIndexBased(indexBased);
                IndexBasedCell = new LayoutTransformControl
                {
                    Child = IndexBasedCheck,
                    LayoutTransform = new ScaleTransform(0.8, 0.8),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                IndexBasedCheck.IsCheckedChanged += (_, _) => Scale.SetIndexBased(IndexBasedCheck.IsChecked == true);
            }

            public void SetReadOnly(bool readOnly)
            {
                if (_nameEd != null) _nameEd.IsReadOnly = readOnly;
                IndexBasedCheck.IsEnabled = !readOnly && Kind == AxisKind.Other;
                RemoveBtn.IsVisible = !readOnly;
                Scale.SetReadOnly(readOnly);
            }

            public Axis ToAxis() => Kind switch
            {
                AxisKind.Z => Axis.Z(Scale.Count, Scale.Min, Scale.Max, Scale.Unit),
                AxisKind.Channel => Axis.Channel(Scale.Count, Scale.Unit),
                AxisKind.Time => Axis.Time(Scale.Count, Scale.Min, Scale.Max, Scale.Unit),
                _ => new Axis(Scale.Count, Scale.Min, Scale.Max, Name, Scale.Unit, IndexBasedCheck.IsChecked == true),
            };
        }
    }
}
