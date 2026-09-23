using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.Extensions.Fft;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// Forward or inverse 2D FFT. The result is always complex data whose XY scale is the transform's own
    /// (frequency domain for Forward, spatial domain for Inverse), with reciprocal axis units.
    /// </summary>
    /// <remarks>
    /// Transforming the whole stack shows progress and can be cancelled between frames; a single frame is
    /// quick and is not interrupted. Complex data is not blended, so a result from a Composite channel cube
    /// is a plain stack along the Channel axis and does not re-enter Composite mode.
    /// </remarks>
    internal class FftCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var answer = await FftDialog.ShowAsync(host.Owner, isMultiFrame, host.IsReplaceBlocked, data);
            return answer == null ? null : CreatePlan(answer.Choices, answer.Params);
        }

        internal static CommandPlan CreatePlan(RunChoices choices, FftDialog.FftParameters p) => new()
        {
            Choices = choices,
            FailureTitle = "FFT Failed",
            Progress = ProgressMode.WholeStackOnly,
            ProgressLabel = "Computing FFT…",
            KeepsCompositeMode = false,
            CreateOperation = run => new Fft2DAllFramesOperation(p.Shift, p.Inverse, run.Progress, run.Token),
            PrepareResult = (result, ctx) => PrepareResult(result, ctx, p),
            HistoryLabel = $"{DirectionLabel(p)} 2D",
            HistoryDetail = ctx =>
            {
                if (ctx.Cube != null)
                {
                    string position = ctx.CubePosition ?? "";
                    return position.Length > 0
                        ? $"[{position}], all channels, shift={p.Shift}"
                        : $"all channels, shift={p.Shift}";
                }
                return ctx.SingleFrame ? $"frame {ctx.FrameIndex}, shift={p.Shift}" : $"shift={p.Shift}";
            },
            ResultTitle = source => $"{DirectionLabel(p)} of {source}",
        };

        /// <summary>
        /// Copies everything except the scale from the source to the transform (whose scale is already the
        /// transform's own) and sets the reciprocal axis units. The axes of the channel cube, or of the
        /// whole source when the whole stack was transformed, become the result's dimensions.
        /// </summary>
        private static void PrepareResult(IMatrixData result, ResultContext ctx, FftDialog.FftParameters p)
        {
            var dimensionsSource = ctx.Cube ?? (ctx.SingleFrame ? null : ctx.Source);
            result.CopyPropertiesFrom(ctx.Source, copyScale: false, copyDimensions: false);
            if (dimensionsSource?.Dimensions?.Axes?.Any() == true)
                result.DefineDimensions(Axis.CreateFrom(dimensionsSource.Dimensions.Axes.ToArray()));
            result.XUnit = ReciprocalUnit(ctx.Source.XUnit, p.Inverse);
            result.YUnit = ReciprocalUnit(ctx.Source.YUnit, p.Inverse);
        }

        private static string DirectionLabel(FftDialog.FftParameters p) => p.Inverse ? "Inverse FFT" : "FFT";

        /// <summary>
        /// "µm" → "1/µm" for Forward; "1/µm" → "µm" for Inverse. An empty unit stays empty, and an
        /// Inverse on a unit that is not of the "1/x" form is left blank rather than guessed.
        /// </summary>
        private static string ReciprocalUnit(string? unit, bool inverse)
        {
            if (string.IsNullOrWhiteSpace(unit)) return string.Empty;
            if (!inverse) return $"1/{unit}";
            return unit.StartsWith("1/", StringComparison.Ordinal) ? unit.Substring(2) : string.Empty;
        }
    }
}
