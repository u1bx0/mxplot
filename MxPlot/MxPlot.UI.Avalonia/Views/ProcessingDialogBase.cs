using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Base class for modal processing dialogs that follow the standard layout:
    /// <list type="bullet">
    ///   <item>Optional materialization warning banner (source is virtual and the whole stack will be processed)</item>
    ///   <item>Custom content area (provided by subclass via <see cref="FinalizeContent"/>)</item>
    ///   <item>Horizontal separator</item>
    ///   <item>Optional "Replace data" checkbox</item>
    ///   <item>OK / Cancel button row</item>
    /// </list>
    /// <para>
    /// Usage pattern in subclass constructor:
    /// <code>
    /// public MyDialog() : base("My Title")
    /// {
    ///     var myControl = BuildMyContent();
    ///     FinalizeContent(myControl, okLabel: "Apply");
    /// }
    /// </code>
    /// </para>
    /// </summary>
    internal abstract class ProcessingDialogBase : Window
    {
        /// <summary>The "Replace data" checkbox. Visible by default; hide via <c>showReplaceData: false</c> if not applicable.</summary>
        protected readonly CheckBox ReplaceDataCheckBox;

        /// <summary>
        /// The shared "This frame only" checkbox, or <see langword="null"/> when the constructor's
        /// <c>thisFrameOnlyDefault</c> was <see langword="null"/> (not applicable for this dialog/data).
        /// Subclasses place this control wherever it belongs in their own content — unlike
        /// <see cref="ReplaceDataCheckBox"/>, it is not auto-inserted by <see cref="FinalizeContent"/>,
        /// since its natural position varies (e.g. next to a "Sync source data" checkbox).
        /// </summary>
        protected readonly CheckBox? ThisFrameOnlyCheckBox;

        /// <summary>
        /// The materialization warning banner (or <see langword="null"/> if <c>src</c> was never
        /// given). <see cref="FinalizeContent"/> inserts this automatically; subclasses that build
        /// their own fully custom layout instead of calling it can place this control themselves.
        /// </summary>
        protected Control? MaterializationWarningBanner => _materializationWarning;

        private readonly IMatrixData? _src;
        private readonly bool _showReplaceData;
        private readonly Border? _materializationWarning;
        private readonly TextBlock? _materializationWarningText;

        /// <param name="isLinkWindow">
        /// Pass <see langword="true"/> when the owning window is itself being kept live by a
        /// Log Transform / Spatial Filter sync. "Replace data" is disabled in that case — since
        /// that window's content is auto-recomputed from its source, overwriting it in place
        /// would just get silently clobbered again the next time the source refreshes.
        /// </param>
        /// <param name="title">The dialog window's title.</param>
        /// <param name="width">The dialog window's initial width.</param>
        /// <param name="canResize">Whether the window can be resized by the user.</param>
        /// <param name="src">
        /// The source data this operation will read, used only to decide whether to show the
        /// materialization warning banner (<see cref="UpdateMaterializationWarning"/>). Pass
        /// <see langword="null"/> to never show it.
        /// </param>
        /// <param name="thisFrameOnlyDefault">
        /// <see langword="null"/> when "This frame only" does not apply to this dialog (single-frame
        /// data, or the operation has no per-frame notion) — <see cref="ThisFrameOnlyCheckBox"/> is
        /// then left <see langword="null"/> and not created at all. Otherwise, the checkbox is
        /// created with this value as its initial checked state.
        /// </param>
        /// <param name="showReplaceData">
        /// Whether this dialog offers "Replace data" at all. Some operations (e.g. Spatial Filter,
        /// which instead offers a live "Sync source data" mode) never replace in place.
        /// </param>
        protected ProcessingDialogBase(
            string title,
            double width = 280,
            bool isLinkWindow = false,
            bool canResize = true,
            IMatrixData? src = null,
            bool? thisFrameOnlyDefault = null,
            bool showReplaceData = true)
        {
            Title = title;
            Width = width;
            SizeToContent = SizeToContent.Height;
            CanResize = canResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _src = src;
            _showReplaceData = showReplaceData;

            ReplaceDataCheckBox = ControlFactory.MakeCheckBox(
                "Replace data",
                hint: "Overwrite the current data instead of opening a new window");
            ReplaceDataCheckBox.Margin = new Thickness(0, 0, 0, -7);
            LockReplaceCheckBoxForLinkWindow(ReplaceDataCheckBox, isLinkWindow);

            if (thisFrameOnlyDefault is bool defaultChecked)
            {
                ThisFrameOnlyCheckBox = ControlFactory.MakeCheckBox(
                    "This frame only",
                    hint: "Apply only to the currently active frame");
                ThisFrameOnlyCheckBox.Margin = new Thickness(0, 4, 0, -7);
                ThisFrameOnlyCheckBox.IsChecked = defaultChecked;
                ThisFrameOnlyCheckBox.IsCheckedChanged += (_, _) => UpdateMaterializationWarning();
            }

            if (src != null)
            {
                _materializationWarningText = new TextBlock
                {
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Orange,
                };
                _materializationWarning = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(60, 255, 180, 0)),
                    BorderBrush = Brushes.Orange,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = _materializationWarningText,
                };
                UpdateMaterializationWarning();
            }
        }

        /// <summary>
        /// Shows/hides and updates the text of the materialization warning banner: visible
        /// whenever the source is virtual (file-mapped) and the operation is not restricted to a
        /// single frame (no <see cref="ThisFrameOnlyCheckBox"/>, or it is unchecked). Re-evaluated
        /// on every "This frame only" toggle.
        /// <para>
        /// Only <see cref="IMatrixData.IsVirtual"/> is checked today. If a non-resident backend
        /// other than the current MMF-backed Virtual frames is added later (e.g. a Remote data
        /// source), extend the condition and wording here rather than adding a parallel check
        /// elsewhere.
        /// </para>
        /// </summary>
        private void UpdateMaterializationWarning()
        {
            if (_materializationWarning == null || _materializationWarningText == null || _src == null) return;

            bool wholeStack = ThisFrameOnlyCheckBox == null || ThisFrameOnlyCheckBox.IsChecked != true;
            bool warn = wholeStack && _src.IsVirtual;
            _materializationWarning.IsVisible = warn;
            if (warn)
            {
                long bytes = (long)_src.XCount * _src.YCount * _src.FrameCount * _src.ElementSize;
                _materializationWarningText.Text =
                    $"⚠ Source data is virtual (file-mapped). Processing the whole stack will load all frames into memory (~{FormatBytes(bytes)}).";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / 1024.0:F1} KB";
        }

        /// <summary>
        /// Disables <paramref name="replaceCheckBox"/> and swaps in a tooltip explaining why,
        /// when <paramref name="isLinkWindow"/> is <see langword="true"/>. Shared by every
        /// "Replace data" checkbox in the app — <see cref="ProcessingDialogBase"/> subclasses
        /// call it above; standalone dialogs that build their own checkbox instead of going
        /// through this base class call it directly too, so the disable-and-explain logic
        /// only ever lives in this one place.
        /// <para>
        /// A plain <c>IsEnabled = false</c> alone would silently swallow the explanatory tooltip
        /// — Avalonia (like most XAML frameworks) skips pointer/tooltip handling on disabled
        /// controls by default. <see cref="ToolTip.ShowOnDisabledProperty"/> opts back in.
        /// </para>
        /// </summary>
        internal static void LockReplaceCheckBoxForLinkWindow(CheckBox replaceCheckBox, bool isLinkWindow)
        {
            if (!isLinkWindow) return;
            replaceCheckBox.IsEnabled = false;
            replaceCheckBox.IsChecked = false;
            ToolTip.SetTip(replaceCheckBox,
                "Disabled: this window is itself kept live by a sync, so its content would be overwritten again on the next update");
            ToolTip.SetShowOnDisabled(replaceCheckBox, true);
        }

        /// <summary>
        /// Called at the end of the subclass constructor to assemble and set the window content.
        /// Prepends the materialization warning banner (if applicable) above
        /// <paramref name="mainContent"/>, then appends a separator, the Replace data checkbox
        /// (unless <c>showReplaceData: false</c> was passed to the constructor), and an OK/Cancel
        /// button row below it.
        /// </summary>
        /// <param name="mainContent">The dialog-specific controls.</param>
        /// <param name="onOk">
        /// Action invoked when OK is clicked.
        /// Typically calls <c>Close(result)</c> with the collected parameters.
        /// </param>
        /// <param name="okLabel">Label for the OK button (default: "OK").</param>
        protected void FinalizeContent(Control mainContent, Action onOk, string okLabel = "OK")
        {
            var okBtn = new Button
            {
                Content = okLabel,
                Width = 80,
                MinHeight = 26,
                Padding = new Thickness(8, 4),
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            okBtn.Classes.Add("accent");

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 80,
                MinHeight = 26,
                Padding = new Thickness(8, 4),
                FontSize = 11,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            okBtn.Click += (_, _) => onOk();
            cancelBtn.Click += (_, _) => Close(null);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 8, 0, 0),
            };
            btnRow.Children.Add(okBtn);
            btnRow.Children.Add(cancelBtn);

            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(16, 14, 16, 14) };
            if (_materializationWarning != null)
                panel.Children.Add(_materializationWarning);
            panel.Children.Add(mainContent);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));
            if (_showReplaceData)
                panel.Children.Add(ReplaceDataCheckBox);
            panel.Children.Add(btnRow);
            Content = panel;
        }
    }
}
