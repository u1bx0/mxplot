using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>Logarithm of every value (ln, log10 or log2); the result is double.</summary>
    internal class LogTransformCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var (min, _) = data.GetValueRange(data.ActiveIndex);
            bool hasNegOrZero = !double.IsNaN(min) && min <= 0;
            var answer = await LogTransformDialog.ShowAsync(host.Owner, isMultiFrame, hasNegOrZero, host.IsReplaceBlocked, data);
            return answer == null ? null : CreatePlan(answer.Choices, answer.Params);
        }

        internal static CommandPlan CreatePlan(RunChoices choices, LogTransformDialog.LogTransformParameters p) => new()
        {
            Choices = choices,
            FailureTitle = "Log Transform Failed",
            Progress = ProgressMode.Always,
            ProgressLabel = "Applying log transform…",
            CreateOperation = run => new LogTransformOperation(
                Base: p.Base,
                Handling: p.Handling,
                Progress: run.Progress,
                CancellationToken: run.Token),
            PrepareResult = (result, ctx) =>
                result.CopyPropertiesFrom(ctx.Source, copyScale: true, copyDimensions: !ctx.SingleFrame),
            HistoryLabel = $"Log Transform ({BaseLabel(p)})",
            HistoryDetail = ctx =>
            {
                string options = $"base={BaseLabel(p)}, handling={p.Handling}";
                if (ctx.Cube != null) return $"[{ctx.CubePosition}], {options}";
                return ctx.SingleFrame ? $"frame {ctx.FrameIndex}, {options}" : options;
            },
            ResultTitle = source => $"{BaseLabel(p)} of {source}",
        };

        private static string BaseLabel(LogTransformDialog.LogTransformParameters p) => p.Base switch
        {
            LogBase.Log10 => "Log₁₀",
            LogBase.Log2 => "Log₂",
            _ => "Ln",
        };
    }
}
