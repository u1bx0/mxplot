using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// A spatial filter (Median or Gaussian). Both share everything but the kernel the dialog starts on.
    /// The dialog offers Sync source data instead of Replace data.
    /// </summary>
    internal class SpatialFilterCommand : ProcessingCommand
    {
        private readonly SpatialFilterDialog.KernelType _kernel;

        public SpatialFilterCommand(SpatialFilterDialog.KernelType kernel) => _kernel = kernel;

        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var answer = await SpatialFilterDialog.ShowAsync(host.Owner, isMultiFrame, _kernel, data);
            return answer == null ? null : CreatePlan(answer.Choices, answer.Params);
        }

        internal static CommandPlan CreatePlan(RunChoices choices, SpatialFilterDialog.SpatialFilterParameters p) => new()
        {
            Choices = choices,
            FailureTitle = "Filter Failed",
            Progress = ProgressMode.WholeStackOnly,
            ProgressLabel = $"Applying {KernelLabel(p.Kernel)}…",
            CreateOperation = run => new SpatialFilterOperation(p.Kernel, run.Progress, run.Token),
            HistoryLabel = KernelLabel(p.Kernel),
            HistoryDetail = ctx =>
            {
                string where = ctx.Cube != null
                    ? $" ([{ctx.CubePosition}])"
                    : ctx.SingleFrame ? $" (frame {ctx.FrameIndex})" : "";
                return $"radius={p.Kernel.Radius}{where}";
            },
            ResultTitle = source => $"{KernelLabel(p.Kernel)} of {source}",
        };

        private static string KernelLabel(IFilterKernel kernel) => kernel switch
        {
            MedianKernel mk => $"Median {2 * mk.Radius + 1}×{2 * mk.Radius + 1}",
            GaussianKernel gk => $"Gaussian {2 * gk.Radius + 1}×{2 * gk.Radius + 1} σ={gk.Sigma:G3}",
            _ => kernel.GetType().Name,
        };
    }
}
