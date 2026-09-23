using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Views;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// Swaps X and Y about the origin [0,0] (the bottom-left corner). The window's overlays are mapped onto
    /// the transposed data, and a result window starts with the source's range and LUT settings.
    /// </summary>
    internal class TransposeCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var choices = await TransposeDialog.ShowAsync(host.Owner, isMultiFrame, host.IsReplaceBlocked, data);
            return choices == null ? null : CreatePlan(choices);
        }

        internal static CommandPlan CreatePlan(RunChoices choices) => new()
        {
            Choices = choices,
            FailureTitle = "Transpose Failed",
            Progress = ProgressMode.Always,
            ProgressLabel = "Transposing…",
            CopiesDisplayState = true,
            OutOfMemoryMessage = "Not enough memory to transpose this dataset.\nOperation cancelled.",
            CreateOperation = run => new TransposeOperation(run.Progress, run.Token),
            // The transpose swaps the scale itself, and its dimensions are already right (none for one 2D
            // frame, the Channel axis for a cube, all axes for the whole stack). What is missing is the
            // metadata of the source, which the intermediate that was transposed no longer carries.
            PrepareResult = (result, ctx) => result.CopyPropertiesFrom(ctx.Source, copyScale: false, copyDimensions: false),
            OnResultReady = (host, result, ctx) => CarryOverlays(host, result, ctx.Source),
            HistoryLabel = "Transpose",
            HistoryDetail = ctx =>
            {
                if (ctx.Cube != null) return $"[{ctx.CubePosition}]";
                return ctx.SingleFrame ? $"frame {ctx.FrameIndex}" : "";
            },
            ResultTitle = source => $"Transposed {source}",
        };

        /// <summary>
        /// Stores the window's overlays, mapped onto the transposed data, in <paramref name="result"/>'s
        /// metadata, from where the window showing the result restores them. This replaces the stored
        /// overlays that <paramref name="result"/> inherited, which are in untransposed coordinates.
        /// </summary>
        internal static void CarryOverlays(ICommandHost host, IMatrixData result, IMatrixData source)
        {
            string json = host.OverlaysJson;
            if (string.IsNullOrWhiteSpace(json) || json == "[]")
                result.Metadata.Remove(OverlaySerializer.MetadataKey);
            else
                result.Metadata[OverlaySerializer.MetadataKey] =
                    OverlayTranspose.TransposeXY(json, source.XCount, source.YCount);
        }
    }
}
