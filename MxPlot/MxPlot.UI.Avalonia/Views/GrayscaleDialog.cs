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
    /// Modal dialog for converting the current Channel axis to grayscale.
    /// Returns a <see cref="GrayscaleParameters"/> record on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class GrayscaleDialog : ProcessingDialogBase
    {
        internal sealed record GrayscaleParameters(bool ReplaceData);

        internal static Task<GrayscaleParameters?> ShowAsync(Window owner, bool isLinkWindow = false, IMatrixData? src = null)
        {
            var dlg = new GrayscaleDialog(isLinkWindow, src);
            return dlg.ShowDialog<GrayscaleParameters?>(owner);
        }

        private GrayscaleDialog(bool isLinkWindow, IMatrixData? src)
            : base("Convert to Grayscale", width: 320, isLinkWindow: isLinkWindow, src: src)
        {
            var mainContent = new StackPanel { Spacing = 4 };
            mainContent.Children.Add(new TextBlock
            {
                Text = "Convert the Channel axis to a single grayscale frame. \r\nThe result opens in a new window by default.",
                FontSize = 11,
            });
            
            FinalizeContent(mainContent, onOk: () =>
            {
                Close(new GrayscaleParameters(
                    ReplaceData: ReplaceDataCheckBox.IsChecked == true));
            }, okLabel: "Apply");
        }
    }
}
