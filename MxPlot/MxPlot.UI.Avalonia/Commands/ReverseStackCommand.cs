using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>Reverses the order of the frames, along one axis or over all of them.</summary>
    internal class ReverseStackCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var answer = await ReverseStackDialog.ShowAsync(host.Owner, data.Axes, host.IsReplaceBlocked, data);
            return answer == null ? null : CreatePlan(answer.Choices, answer.Params);
        }

        internal static CommandPlan CreatePlan(RunChoices choices, ReverseStackDialog.ReverseStackParameters p) => new()
        {
            Choices = choices,
            FailureTitle = "Reverse Stack Failed",
            WholeData = true,
            Progress = ProgressMode.None,
            CreateOperation = _ => new ReverseStackOperation(p.AxisName),
            HistoryLabel = "Reverse Stack",
            HistoryDetail = _ => p.AxisName == null ? "all frames" : $"axis: {p.AxisName}",
            ResultTitle = source => $"Reversed {source}",
        };
    }
}
