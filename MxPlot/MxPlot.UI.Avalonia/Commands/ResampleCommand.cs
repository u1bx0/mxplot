using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>Changes the pixel count of the data, keeping its physical extent (XMin/XMax, YMin/YMax).</summary>
    internal class ResampleCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var answer = await ResampleDialog.ShowAsync(host.Owner, isMultiFrame, host.IsReplaceBlocked, data);
            return answer == null ? null : CreatePlan(answer.Choices, answer.Params);
        }

        internal static CommandPlan CreatePlan(RunChoices choices, ResampleDialog.ResampleParameters p) => new()
        {
            Choices = choices,
            FailureTitle = "Resample Failed",
            Progress = ProgressMode.WholeStackOnly,
            ProgressLabel = "Resampling…",
            CreateOperation = run => new ResampleOperation(p.Width, p.Height, p.Method, run.Progress, run.Token),
            HistoryLabel = "Resample",
            HistoryDetail = ctx =>
            {
                string where = ctx.Cube != null
                    ? $" ([{ctx.CubePosition}])"
                    : ctx.SingleFrame ? $" (frame {ctx.FrameIndex})" : "";
                return $"{p.Width}×{p.Height}, {MethodLabel(p.Method)}{where}";
            },
            ResultTitle = source => $"Resampled ({p.Width}×{p.Height}) {source}",
        };

        private static string MethodLabel(ResampleMethod method) => method switch
        {
            ResampleMethod.Nearest => "nearest neighbor",
            _ => "bilinear",
        };
    }
}
