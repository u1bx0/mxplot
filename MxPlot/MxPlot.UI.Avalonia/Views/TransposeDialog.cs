using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.UI.Avalonia.Helpers;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for confirming a Transpose (swap X/Y) operation.
    /// Returns the dialog's checkboxes on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class TransposeDialog : ProcessingDialogBase
    {
        /// <param name="isMultiFrame">Whether the source data has more than one frame.</param>
        /// <param name="isLinkWindow">
        /// Whether the owning window cannot have its data replaced (see <see cref="ProcessingDialogBase"/>).
        /// </param>
        /// <param name="src">Source data, used only to decide whether to show the materialization warning.</param>
        internal static Task<RunChoices?> ShowAsync(
            Window owner, bool isMultiFrame, bool isLinkWindow = false, IMatrixData? src = null)
        {
            var dlg = new TransposeDialog(isMultiFrame, isLinkWindow, src);
            return dlg.ShowDialog<RunChoices?>(owner);
        }

        private TransposeDialog(bool isMultiFrame, bool isLinkWindow, IMatrixData? src)
            : base("Transpose", width: 260, isLinkWindow: isLinkWindow, src: src,
                   thisFrameOnlyDefault: isMultiFrame ? false : (bool?)null, showSyncSource: true)
        {
            var mainContent = new StackPanel { Spacing = 4 };
            mainContent.Children.Add(new TextBlock
            {
                Text = "Swaps X and Y about the origin [0,0] (the bottom-left corner).",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            var frameOptions = BuildFrameOptions();
            if (frameOptions != null)
                mainContent.Children.Add(frameOptions);

            FinalizeContent(mainContent, onOk: () => Close(ReadChoices()), okLabel: "Apply");
        }
    }
}
