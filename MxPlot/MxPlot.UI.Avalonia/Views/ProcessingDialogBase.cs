using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MxPlot.UI.Avalonia.Helpers;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Base class for modal processing dialogs that follow the standard layout:
    /// <list type="bullet">
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
        /// <summary>The "Replace data" checkbox. Visible by default; hide if not applicable.</summary>
        protected readonly CheckBox ReplaceDataCheckBox;

        /// <param name="isLinkWindow">
        /// Pass <see langword="true"/> when the owning window is itself being kept live by a
        /// Log Transform / Spatial Filter sync. "Replace data" is disabled in that case — since
        /// that window's content is auto-recomputed from its source, overwriting it in place
        /// would just get silently clobbered again the next time the source refreshes.
        /// </param>
        protected ProcessingDialogBase(string title, double width = 280, bool isLinkWindow = false)
        {
            Title = title;
            Width = width;
            SizeToContent = SizeToContent.Height;
            CanResize = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            ReplaceDataCheckBox = ControlFactory.MakeCheckBox(
                "Replace data",
                hint: "Overwrite the current data instead of opening a new window");
            ReplaceDataCheckBox.Margin = new Thickness(0, 0, 0, -7);
            LockReplaceCheckBoxForLinkWindow(ReplaceDataCheckBox, isLinkWindow);
        }

        /// <summary>
        /// Disables <paramref name="replaceCheckBox"/> and swaps in a tooltip explaining why,
        /// when <paramref name="isLinkWindow"/> is <see langword="true"/>. Shared by every
        /// "Replace data" checkbox in the app — <see cref="ProcessingDialogBase"/> subclasses
        /// call it above; standalone dialogs that build their own checkbox instead of going
        /// through this base class (<c>LogTransformDialog</c>, <c>ConvertValueTypeDialog</c>,
        /// <c>ConvertComplexDialog</c>) call it directly too, so the disable-and-explain logic
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
        /// Appends a separator, the Replace data checkbox, and an OK/Cancel button row below
        /// <paramref name="mainContent"/>.
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
            panel.Children.Add(mainContent);
            panel.Children.Add(ControlFactory.MakeSep(new Thickness(0, 4)));
            panel.Children.Add(ReplaceDataCheckBox);
            panel.Children.Add(btnRow);
            Content = panel;
        }
    }
}
