using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using MxPlot.UI.Avalonia.Formula;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// A formula text box that checks its text on every edit: an invalid formula tints the box and shows
    /// the error in <see cref="ErrorText"/>, which the owner places where it fits.
    /// </summary>
    /// <remarks>
    /// With a <see cref="FormulaHistory"/>, the Up and Down keys belong to the history instead of moving
    /// the caret between wrapped lines: Down opens the list on the newest formula and Up on the oldest;
    /// in the open list they move the selection, wrapping at the ends, Enter takes the selected formula
    /// and Escape closes the list. The list offers only the formulas that are valid here.
    /// </remarks>
    internal sealed class FormulaInput
    {
        private static readonly string[] BackgroundKeys =
            ["TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused"];

        private static readonly FontFamily Monospace = new("Cascadia Code, Consolas, monospace");

        private Func<string, FormulaException?> _check;
        private readonly FormulaHistory? _history;
        private readonly Border _border;
        private readonly Popup? _popup;
        private readonly ListBox? _list;
        private bool _takingFromHistory;

        /// <summary>The text box, inside its border.</summary>
        public Control View => _border;

        /// <summary>The text box itself.</summary>
        public TextBox Box { get; }

        /// <summary>The line that shows the current error; hidden while the formula is valid.</summary>
        public TextBlock ErrorText { get; }

        /// <summary>Raised after the text changed and was checked.</summary>
        public event Action? Changed;

        /// <param name="text">The initial formula.</param>
        /// <param name="check">Returns the error of a formula text, or <see langword="null"/> when it is valid.</param>
        /// <param name="height">The height of the box; the text wraps inside it.</param>
        /// <param name="history">The formulas offered by the Up and Down keys, or <see langword="null"/> for none.</param>
        public FormulaInput(string text, Func<string, FormulaException?> check, double height = 52,
            FormulaHistory? history = null)
        {
            _check = check;
            _history = history;
            Box = new TextBox
            {
                Text = text,
                FontFamily = Monospace,
                FontSize = 12,
                AcceptsReturn = false,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
            };

            var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            layout.Children.Add(Box);
            _border = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 160, 160, 160)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Height = height,
                Child = layout,
            };

            if (history != null)
            {
                var historyBtn = new Button
                {
                    Content = "▾",
                    FontSize = 11,
                    Width = 20,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Focusable = false,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = Brushes.Transparent,
                };
                ToolTip.SetTip(historyBtn, "Recent formulas (Up / Down key)");
                historyBtn.Click += (_, _) =>
                {
                    if (_popup!.IsOpen) CloseHistory();
                    else OpenHistory(fromOldest: false);
                    Box.Focus();
                };
                Grid.SetColumn(historyBtn, 1);
                layout.Children.Add(historyBtn);

                _list = new ListBox { MaxHeight = 190, FontFamily = Monospace, FontSize = 11.5 };
                ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
                _list.Styles.Add(new Style(s => s.OfType<ListBoxItem>())
                {
                    Setters =
                    {
                        new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 2)),
                        new Setter(Layoutable.MinHeightProperty, 0.0),
                    },
                });
                _list.Tapped += (_, _) => TakeSelected();

                _popup = new Popup
                {
                    PlacementTarget = _border,
                    Placement = PlacementMode.Bottom,
                    IsLightDismissEnabled = true,
                    Child = new Border
                    {
                        BorderBrush = new SolidColorBrush(Color.FromArgb(160, 130, 130, 130)),
                        BorderThickness = new Thickness(1),
                        Child = _list,
                    },
                };
                layout.Children.Add(_popup);

                // Tunnel: the text box would otherwise use Up and Down to move the caret.
                Box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            }

            ErrorText = new TextBlock
            {
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 50, 50)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsVisible = false,
            };
            Box.TextChanged += (_, _) =>
            {
                // Typing takes over from the list.
                if (!_takingFromHistory) CloseHistory();
                Validate();
                Changed?.Invoke();
            };
            Validate();
        }

        /// <summary>The formula text.</summary>
        public string Text
        {
            get => Box.Text ?? "";
            set
            {
                Box.Text = value;
                Validate();
            }
        }

        /// <summary>The error of the current text, or <see langword="null"/> when it is valid.</summary>
        public FormulaException? Error { get; private set; }

        /// <summary>Replaces the check (the names a formula may use changed) and checks the text again.</summary>
        public void SetCheck(Func<string, FormulaException?> check)
        {
            _check = check;
            Validate();
        }

        /// <summary>Checks the text and shows or clears the error. Returns whether the formula is valid.</summary>
        public bool Validate()
        {
            Error = _check(Text);
            ErrorText.Text = Error?.Message;
            ErrorText.IsVisible = Error != null;
            ToolTip.SetTip(ErrorText, Error?.Message);

            foreach (var key in BackgroundKeys)
            {
                if (Error != null) Box.Resources[key] = new SolidColorBrush(Color.FromArgb(70, 255, 80, 80));
                else Box.Resources.Remove(key);
            }
            return Error == null;
        }

        // ── History ───────────────────────────────────────────────────────────

        /// <summary>The formulas of the history that are valid here, newest first.</summary>
        internal IReadOnlyList<string> HistoryChoices()
            => _history == null ? [] : _history.Items.Where(f => _check(f) == null).ToArray();

        /// <summary>Whether the history list is showing.</summary>
        internal bool IsHistoryOpen => _popup?.IsOpen == true;

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_popup == null || _list == null) return;

            if (_popup.IsOpen)
            {
                switch (e.Key)
                {
                    case Key.Down: MoveSelection(+1); break;
                    case Key.Up: MoveSelection(-1); break;
                    case Key.Enter: TakeSelected(); break;
                    case Key.Escape: CloseHistory(); break;
                    default: return;
                }
                e.Handled = true;
            }
            else if (e.Key is Key.Down or Key.Up)
            {
                OpenHistory(fromOldest: e.Key == Key.Up);
                e.Handled = true;
            }
        }

        /// <summary>Shows the list with the newest formula selected, or the oldest. Does nothing when there is no formula to offer.</summary>
        internal void OpenHistory(bool fromOldest)
        {
            if (_popup == null || _list == null) return;
            var choices = HistoryChoices();
            if (choices.Count == 0) return;

            _list.ItemsSource = choices;
            _list.SelectedIndex = fromOldest ? choices.Count - 1 : 0;
            _list.Width = Math.Max(160, _border.Bounds.Width);
            _popup.IsOpen = true;
            _list.ScrollIntoView(_list.SelectedIndex);
        }

        private void MoveSelection(int step)
        {
            int count = _list!.ItemCount;
            if (count == 0) return;
            // Wraps at both ends.
            _list.SelectedIndex = ((_list.SelectedIndex + step) % count + count) % count;
            _list.ScrollIntoView(_list.SelectedIndex);
        }

        /// <summary>Puts the selected formula into the box and closes the list.</summary>
        internal void TakeSelected()
        {
            if (_list?.SelectedItem is not string formula) return;

            _takingFromHistory = true;
            try { Text = formula; }
            finally { _takingFromHistory = false; }
            CloseHistory();
            Box.CaretIndex = formula.Length;
            Box.Focus();
        }

        private void CloseHistory()
        {
            if (_popup != null) _popup.IsOpen = false;
        }
    }
}
