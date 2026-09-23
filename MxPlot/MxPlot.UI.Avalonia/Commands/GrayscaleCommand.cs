using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// Collapses the Channel axis into a single grayscale channel: Rec.709 luma for an R/G/B triplet, the
    /// mean otherwise. It needs a Channel axis to collapse.
    /// </summary>
    internal class GrayscaleCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            if (data.Axes.FindAxis("Channel") == null) return null;

            var choices = await ShowDialogAsync(host, data);
            return choices == null ? null : CreatePlan(choices, data);
        }

        protected virtual Task<RunChoices?> ShowDialogAsync(ICommandHost host, IMatrixData data)
            => GrayscaleDialog.ShowAsync(host.Owner, host.IsReplaceBlocked, data);

        internal static CommandPlan CreatePlan(RunChoices choices, IMatrixData data) => new()
        {
            Choices = choices,
            FailureTitle = "Convert to Grayscale Failed",
            WholeData = true,
            Progress = ProgressMode.Always,
            ProgressLabel = "Converting to grayscale…",
            CreateOperation = run => new GrayscaleOperation(
                data.Axes.FindAxis("Channel")!.Name, GrayscaleMethod.Auto, run.Progress, run.Token),
            HistoryLabel = "Convert to Grayscale",
            HistoryDetail = _ =>
            {
                var channelAxis = data.Axes.FindAxis("Channel")!;
                return ColorAxis.IsRgbTriplet(channelAxis)
                    ? $"{channelAxis.Count} channels, Rec.709 luma"
                    : $"{channelAxis.Count} channels, mean";
            },
            ResultTitle = source => $"Grayscale of {source}",
        };
    }
}
