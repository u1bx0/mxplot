using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// Scales the values so that the maximum equals a target value, per frame or over the whole stack.
    /// A whole-stack scale needs the global maximum first, found by a scan before the operation runs.
    /// </summary>
    internal class NormalizeCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var answer = await NormalizeDialog.ShowAsync(host.Owner, isMultiFrame, data, host.IsReplaceBlocked);
            return answer == null ? null : CreatePlan(answer.Choices, answer.Params);
        }

        internal static CommandPlan CreatePlan(RunChoices choices, NormalizeDialog.NormalizeParameters p) => new()
        {
            Choices = choices,
            FailureTitle = "Normalize Failed",
            Progress = ProgressMode.Always,
            ProgressLabel = "Normalizing…",
            Prepare = (host, scope) => ScanForGlobalMaxAsync(host, p, scope),
            CreateOperation = run => new NormalizeOperation(
                Target: p.Target,
                Scope: p.Scope,
                PrecomputedGlobalMax: run.State is double max ? max : double.NaN,
                Progress: run.Progress,
                CancellationToken: run.Token),
            PrepareResult = (result, ctx) =>
                result.CopyPropertiesFrom(ctx.Source, copyScale: true, copyDimensions: !ctx.SingleFrame),
            HistoryLabel = $"Normalize (max→{p.Target:G4})",
            HistoryDetail = ctx =>
            {
                if (ctx.Cube != null) return $"[{ctx.CubePosition}], scope={p.Scope}, target={p.Target:G4}";
                return ctx.SingleFrame
                    ? $"frame {ctx.FrameIndex}, target={p.Target:G4}"
                    : $"scope={p.Scope}, target={p.Target:G4}";
            },
            ResultTitle = source => $"Normalized {source}",
        };

        /// <summary>
        /// The global maximum, when the scale is global and more than one frame is processed;
        /// otherwise NaN (each frame is scaled by its own maximum, so nothing needs scanning).
        /// </summary>
        private static async Task<object?> ScanForGlobalMaxAsync(
            ICommandHost host, NormalizeDialog.NormalizeParameters p, RunScope scope)
        {
            bool needsGlobalMax = p.Scope == NormalizeScope.Global && scope.IsMultiFrame
                && (!scope.ThisFrameOnly || scope.HasCube);
            if (!needsGlobalMax) return double.NaN;

            using var session = host.BeginProgress("Scanning max value…", cancellable: false);
            return await Task.Run(() => (object?)ScanGlobalMaxValue(scope.Operand));
        }

        /// <summary>Scans all frames of <paramref name="data"/> and returns the global maximum value.</summary>
        internal static double ScanGlobalMaxValue(IMatrixData data)
        {
            double max = double.NegativeInfinity;
            for (int i = 0; i < data.FrameCount; i++)
            {
                var (_, frameMax) = data.GetValueRange(i);
                if (frameMax > max) max = frameMax;
            }
            return max;
        }
    }
}
