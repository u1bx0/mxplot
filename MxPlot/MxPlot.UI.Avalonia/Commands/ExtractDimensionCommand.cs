using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// Extracts the data along one axis with every other axis held at its current position (Along), or the
    /// data at the current position of one axis (At). A new window is refreshed together with the source.
    /// </summary>
    internal class ExtractDimensionCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            var answer = await ExtractDimensionDialog.ShowAsync(host.Owner, data.Axes, host.IsReplaceBlocked);
            return answer == null ? null : CreatePlan(answer.Choices, answer.Params, data);
        }

        internal static CommandPlan CreatePlan(
            RunChoices choices, ExtractDimensionDialog.ExtractDimensionParameters p, IMatrixData data) => new()
        {
            Choices = choices,
            FailureTitle = "Extract Failed",
            WholeData = true,
            Progress = ProgressMode.None,
            ResultIsLinked = true,
            CreateOperation = _ => CreateOperation(p, data),
            HistoryLabel = p.Mode == ExtractDimensionDialog.ExtractMode.Along
                ? $"Extract Along {p.AxisName}"
                : $"Extract At {p.AxisName}",
            HistoryDetail = _ => HistoryDetail(p, data),
            ResultTitle = source => ResultTitle(p, data, source),
        };

        private static IMatrixDataOperation CreateOperation(ExtractDimensionDialog.ExtractDimensionParameters p, IMatrixData data)
        {
            if (p.Mode == ExtractDimensionDialog.ExtractMode.Along)
            {
                // Every axis takes part, the target included: its slot is ignored, but the length must match.
                int[] baseIndices = data.Axes.Select(a => a.Index).ToArray();
                return new ExtractAlongOperation(p.AxisName, baseIndices);
            }
            return new SelectByOperation(p.AxisName, data.Axes.FindAxis(p.AxisName)!.Index);
        }

        private static string HistoryDetail(ExtractDimensionDialog.ExtractDimensionParameters p, IMatrixData data)
        {
            if (p.Mode == ExtractDimensionDialog.ExtractMode.Along)
            {
                var fixedAxes = OtherAxes(p, data).Select(a => $"{a.Name}={MatrixPlotter.FormatAxisValue(a, a.Index, verbose: true)}");
                return $"axis={p.AxisName}; fixed: {string.Join(", ", fixedAxes)}";
            }
            var axis = data.Axes.FindAxis(p.AxisName)!;
            return $"{p.AxisName}={MatrixPlotter.FormatAxisValue(axis, axis.Index, verbose: true)}";
        }

        private static string ResultTitle(ExtractDimensionDialog.ExtractDimensionParameters p, IMatrixData data, string sourceTitle)
        {
            if (p.Mode == ExtractDimensionDialog.ExtractMode.Along)
            {
                var fixedAxes = OtherAxes(p, data).Select(a => $"{a.Name}={MatrixPlotter.FormatAxisValue(a, a.Index)}");
                return $"{sourceTitle} [Along {p.AxisName}, {string.Join(", ", fixedAxes)}]";
            }
            var axis = data.Axes.FindAxis(p.AxisName)!;
            return $"{sourceTitle} [At {p.AxisName}={MatrixPlotter.FormatAxisValue(axis, axis.Index)}]";
        }

        private static IEnumerable<Axis> OtherAxes(ExtractDimensionDialog.ExtractDimensionParameters p, IMatrixData data)
            => data.Axes.Where(a => !string.Equals(a.Name, p.AxisName, StringComparison.OrdinalIgnoreCase));
    }
}
