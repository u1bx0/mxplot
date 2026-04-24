using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MxPlot.App.ViewModels;
using MxPlot.Core.IO;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Attached property that, when set to <c>true</c> on a <see cref="TextBox"/>,
    /// automatically focuses and selects all text when the control becomes visible,
    /// and commits the rename via <see cref="PlotWindowItemViewModel.CommitRenameCommand"/>
    /// when the control loses focus.
    /// </summary>
    internal static class FocusHelper
    {
        public static readonly AttachedProperty<bool> FocusOnVisibleProperty =
            AvaloniaProperty.RegisterAttached<Control, bool>(
                "FocusOnVisible", typeof(FocusHelper));

        static FocusHelper()
        {
            FocusOnVisibleProperty.Changed.AddClassHandler<Control>((ctrl, e) =>
            {
                if ((bool)e.NewValue!)
                    ctrl.PropertyChanged += OnControlPropertyChanged;
                // No removal handler needed: the TextBox stays in the visual tree.
            });
        }

        private static void OnControlPropertyChanged(
            object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Visual.IsVisibleProperty || sender is not TextBox tb) return;

            if (tb.IsVisible)
            {
                // Defer focus + selection until after Avalonia's layout pass so the
                // TextBox is fully rendered and can accept keyboard input.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!tb.IsVisible) return;
                    tb.Focus();

                    // Select only the base name, excluding known compound extensions
                    // (e.g. "data.ome.tiff" → select "data"; "photo.png" → select "photo").
                    // Falls back to SelectAll when the name has no known extension.
                    var text     = tb.Text ?? string.Empty;
                    var baseName = FormatRegistry.StripKnownExtension(text);
                    if (baseName.Length > 0 && baseName.Length < text.Length)
                    {
                        tb.SelectionStart = 0;
                        tb.SelectionEnd   = baseName.Length;
                    }
                    else
                    {
                        tb.SelectAll();
                    }
                }, DispatcherPriority.Input);

                tb.LostFocus += OnLostFocus;
                tb.KeyDown   += OnKeyDown;
            }
            else
            {
                tb.LostFocus -= OnLostFocus;
                tb.KeyDown   -= OnKeyDown;
            }
        }

        /// <summary>
        /// Intercepts Tab / Shift+Tab to commit the current rename and navigate
        /// to the next or previous list item without triggering Avalonia's default
        /// focus-traversal, which would leave the rename TextBox in an inconsistent state.
        /// <para>
        /// Also intercepts cursor-movement keys (Left, Right, Up, Down, Home, End) and
        /// marks them as handled.  When the cursor is already at an edge (e.g. position 0,
        /// pressing Left), the TextBox may skip setting <c>e.Handled = true</c>, which lets
        /// the event bubble up to the <see cref="ListBox"/> and trigger item-selection
        /// navigation — stealing focus from the rename TextBox.
        /// </para>
        /// </summary>
        private static void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;

            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                bool forward = !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                (tb.DataContext as WindowListItemViewModel)?.RequestRenameNavigation(forward);
                return;
            }

            // Cursor-movement keys: ensure they are always consumed at the TextBox level
            // so they cannot bubble to the ListBox's keyboard navigation handler.
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down
                                  or Key.Home or Key.End)
            {
                e.Handled = true;
            }
        }

        private static void OnLostFocus(object? sender, RoutedEventArgs e)
        {
            // Only commit if the item is still in rename mode (guard against
            // double-commit when Enter was already pressed).
            if (sender is TextBox tb
                && tb.DataContext is WindowListItemViewModel vm
                && vm.IsRenaming)
            {
                vm.CommitRenameCommand.Execute(null);
            }
        }

        public static bool GetFocusOnVisible(Control ctrl)
            => ctrl.GetValue(FocusOnVisibleProperty);

        public static void SetFocusOnVisible(Control ctrl, bool value)
            => ctrl.SetValue(FocusOnVisibleProperty, value);
    }
}
