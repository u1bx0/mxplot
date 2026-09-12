using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for confirming a Transpose (swap X/Y) operation.
    /// Returns a <see cref="TransposeParameters"/> record on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class TransposeDialog : ProcessingDialogBase
    {
        internal sealed record TransposeParameters(bool ThisFrameOnly, bool SyncSource);

        /// <param name="isMultiFrame">Whether the source data has more than one frame.</param>
        /// <param name="src">Source data, used only to decide whether to show the materialization warning.</param>
        internal static Task<TransposeParameters?> ShowAsync(Window owner, bool isMultiFrame, IMatrixData? src = null)
        {
            var dlg = new TransposeDialog(isMultiFrame, src);
            return dlg.ShowDialog<TransposeParameters?>(owner);
        }

        // Transposing swaps every active overlay's coordinate space (and any text on a text
        // overlay would need re-baking), which is enough of a mess that it's simplest to never
        // carry overlays across at all -- so this always opens a new window and never offers
        // "Replace data" (see showReplaceData: false below).
        private TransposeDialog(bool isMultiFrame, IMatrixData? src)
            : base("Transpose", width: 260, src: src,
                   thisFrameOnlyDefault: isMultiFrame ? false : (bool?)null, showReplaceData: false)
        {
            var mainContent = new StackPanel { Spacing = 4 };
            mainContent.Children.Add(new TextBlock
            {
                Text = "Swap X and Y. The result always opens in a new window.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            if (ThisFrameOnlyCheckBox != null)
                mainContent.Children.Add(ThisFrameOnlyCheckBox);

            // Same rules as SpatialFilterDialog/LogTransformDialog: always available for
            // single-frame data (e.g. a live-acquisition window whose one frame is repeatedly
            // refreshed in place -- watching a corrected orientation as new data comes in is
            // exactly the point), only enabled once "This frame only" is checked otherwise.
            var syncCheck = ControlFactory.MakeCheckBox(
                "Sync source data",
                hint: "Keep the result live: re-apply the transpose whenever the source frame changes");
            syncCheck.Margin = new Thickness(0, 4, 0, -7);
            if (ThisFrameOnlyCheckBox != null)
            {
                syncCheck.IsEnabled = false;
                ThisFrameOnlyCheckBox.IsCheckedChanged += (_, _) =>
                    syncCheck.IsEnabled = ThisFrameOnlyCheckBox.IsChecked == true;
            }
            mainContent.Children.Add(syncCheck);

            FinalizeContent(mainContent, onOk: () =>
            {
                Close(new TransposeParameters(
                    ThisFrameOnlyCheckBox?.IsChecked == true,
                    syncCheck.IsChecked == true));
            }, okLabel: "Apply");
        }
    }
}
