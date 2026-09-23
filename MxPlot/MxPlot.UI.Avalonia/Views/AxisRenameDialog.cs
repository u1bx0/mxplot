using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Helpers;

namespace MxPlot.UI.Avalonia.Views
{
    internal sealed class AxisRenameDialog : Window
    {
        /// <summary>Left/right edge shared by every row, so the content lines up.</summary>
        private const double ContentMargin = 16;

        private readonly TextBox _inputBox;
        private readonly CheckBox? _indexBasedBox;
        private readonly TextBlock? _alertBlock;
        private readonly bool _initialIndexBased;
        private readonly bool _indexToggleEnabled;
        private readonly string? _indexLockedReason;
        private readonly bool _hasTagData;

        public string? Result { get; private set; }

        /// <summary>
        /// The checkbox's state at commit, or <c>null</c> if this dialog was created without one
        /// (e.g. <see cref="MatrixPlotter.RenameChannelAsync"/>'s per-channel tag rename, which never
        /// changes the axis's type). Only meaningful once <see cref="Result"/> is non-null.
        /// </summary>
        public bool? IsIndexBased { get; private set; }

        /// <param name="currentName">The axis's (or channel tag's) current name, pre-filled into the input box.</param>
        /// <param name="title">
        /// Window title. Defaults to the axis-rename wording; Composite mode reuses this dialog
        /// to rename a single channel tag and passes its own title.
        /// </param>
        /// <param name="hint">
        /// Optional single line of small print shown below the input row. Omitted entirely when null.
        /// </param>
        /// <param name="isIndexBased">
        /// When non-<c>null</c>, adds an "Index-based" checkbox initialized to this value (the axis's
        /// own <c>IsIndexBased</c>: Min = 0, Max = Count - 1, Scale not editable), plus an alert
        /// line that explains what gets discarded when the value is changed. <c>null</c> omits both
        /// entirely. Whether the axis is Composite-capable is deliberately not decided here - a
        /// plain axis is promoted automatically when it is first composited.
        /// </param>
        /// <param name="indexToggleEnabled">
        /// <c>false</c> locks the checkbox at its initial value - used for the axis currently driving
        /// Composite rendering, which must stay Index-based until the user leaves Composite mode.
        /// Ignored when <paramref name="isIndexBased"/> is <c>null</c>.
        /// </param>
        /// <param name="indexLockedReason">
        /// Shown on the alert line in place of the on/off warning while the checkbox is locked.
        /// </param>
        /// <param name="axisKindText">
        /// For an axis that carries more than a plain axis does (a <c>ColorAxis</c>/<c>TaggedAxis</c>),
        /// a short description of that extra data, shown under the checkbox from the start, e.g.
        /// "Color axis: tags, colors". Unchecking Index-based on such an axis converts it to a plain
        /// axis and discards this data - the alert line says so. <c>null</c> for a plain axis, for
        /// which toggling the checkbox loses nothing but the Scale values.
        /// </param>
        public AxisRenameDialog(
            string currentName,
            string title = "Rename Axis",
            string? hint = null,
            bool? isIndexBased = null,
            bool indexToggleEnabled = true,
            string? indexLockedReason = null,
            string? axisKindText = null)
        {
            _initialIndexBased = isIndexBased ?? false;
            _indexToggleEnabled = indexToggleEnabled;
            _indexLockedReason = indexLockedReason;
            _hasTagData = axisKindText != null;

            Title = title;
            Width = 290;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontSize = 11;

            _inputBox = new TextBox
            {
                Text = currentName,
                Width = 150,
                MinHeight = 0,
                Padding = new Thickness(6, 6),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 11,
            };
            _inputBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Return) { Commit(); e.Handled = true; }
                if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };

            var okBtn = new Button
            {
                Content = "OK",
                Width = 72,
                Height = 28,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            okBtn.Click += (_, _) => Commit();

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 72,
                Height = 28,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            cancelBtn.Click += (_, _) => Close();

            var inputRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(ContentMargin, 14, ContentMargin, 10),
            };
            inputRow.Children.Add(new TextBlock
            {
                Text = "New name:",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            });
            inputRow.Children.Add(_inputBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(ContentMargin, 0, ContentMargin, 14),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);

            var rootPanel = new StackPanel();
            rootPanel.Children.Add(inputRow);
            if (!string.IsNullOrEmpty(hint))
            {
                var hintBlock = new TextBlock
                {
                    Text = hint,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(ContentMargin, -4, ContentMargin, 8),
                };
                // "toast" resolves to ToastNoticeForeground (Themes/Default.axaml), a warm
                // amber tuned per theme (bright in Dark, dark in Light) - readable on both,
                // unlike a single flat gray which washes out against a dark window background.
                hintBlock.Classes.Add("toast");
                rootPanel.Children.Add(hintBlock);
            }

            if (isIndexBased.HasValue)
            {
                _indexBasedBox = ControlFactory.MakeCheckBox("Index-based", 11);
                _indexBasedBox.IsChecked = isIndexBased.Value;
                _indexBasedBox.IsEnabled = indexToggleEnabled;
                // What the axis carries beyond a plain axis, above the checkbox that can discard it.
                if (axisKindText != null)
                {
                    rootPanel.Children.Add(new TextBlock
                    {
                        Text = axisKindText,
                        FontSize = 11,
                        Opacity = 0.75,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(ContentMargin, 0, ContentMargin, 6),
                    });
                }
                _indexBasedBox.Margin = new Thickness(ContentMargin, 0, ContentMargin, 4);
                rootPanel.Children.Add(_indexBasedBox);

                _alertBlock = new TextBlock
                {
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(ContentMargin, 0, ContentMargin, 8),
                    IsVisible = false,
                };
                _alertBlock.Classes.Add("toast");
                rootPanel.Children.Add(_alertBlock);

                _indexBasedBox.IsCheckedChanged += (_, _) => UpdateAlert();
                UpdateAlert();
            }

            rootPanel.Children.Add(btnRow);

            Content = rootPanel;
            Opened += (_, _) => { _inputBox.Focus(); _inputBox.SelectAll(); };
        }

        /// <summary>
        /// Refreshes the alert line to match the checkbox: the lock reason while disabled, the
        /// relevant promote/demote warning while the value differs from where it started, or nothing
        /// once it matches the starting value again.
        /// </summary>
        private void UpdateAlert()
        {
            if (_alertBlock == null || _indexBasedBox == null) return;

            if (!_indexToggleEnabled)
            {
                _alertBlock.Text = _indexLockedReason;
                _alertBlock.IsVisible = !string.IsNullOrEmpty(_indexLockedReason);
                return;
            }

            bool nowIndexBased = _indexBasedBox.IsChecked == true;
            if (nowIndexBased == _initialIndexBased)
            {
                _alertBlock.IsVisible = false;
                return;
            }

            if (nowIndexBased)
            {
                _alertBlock.Text = "Turning this on resets Min/Max to 0 .. Count-1 (the Scale values are discarded).";
                _alertBlock.IsVisible = true;
            }
            else if (_hasTagData)
            {
                _alertBlock.Text = "Turning this off converts the axis to a plain axis and discards the tag/color data listed above.";
                _alertBlock.IsVisible = true;
            }
            else
            {
                // A plain index-based axis loses nothing when this is turned off - Min/Max/Step/Unit
                // just become editable.
                _alertBlock.IsVisible = false;
            }
        }

        private void Commit()
        {
            var text = _inputBox.Text?.Trim();
            if (string.IsNullOrEmpty(text)) { Close(); return; }
            Result = text;
            if (_indexBasedBox != null) IsIndexBased = _indexBasedBox.IsChecked == true;
            Close();
        }
    }
}
