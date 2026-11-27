using MxPlot.Core;
using MxPlot.UI.Avalonia.Formula;
using MxPlot.UI.Avalonia.Views;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// Computes new values from a formula of the window's own data (<c>m1</c>), the pixel coordinates and
    /// the position along the data's axes (<c>iz</c>, <c>ic</c>, <c>it</c>, ...). The result is double. A
    /// result that follows its source lets the user change the formula afterwards.
    /// </summary>
    /// <remarks>
    /// The axis variables are those of the window's data whatever part of it is processed. For the whole
    /// stack they are the position of each frame; for one frame they are the window's current position,
    /// so a result that follows its source follows the axis sliders too; for a Composite channel cube the
    /// composited axis runs over the cube and the others are the current position.
    /// </remarks>
    internal class CalculatorCommand : ProcessingCommand
    {
        protected override async Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data)
        {
            // Copies: the plan outlives this call, and the window may change its axes later.
            var axes = data.Axes.Select(a => a.Clone()).ToArray();
            var answer = await CalculatorDialog.ShowAsync(host.Owner, isMultiFrame, host.IsReplaceBlocked, data, axes,
                StartingFormula(axes));
            if (answer == null) return null;

            FormulaHistory.Calculator.Add(answer.Params.Formula);
            return CreatePlan(answer.Choices, answer.Params, axes);
        }

        /// <summary>
        /// The formula the dialog starts on: the one applied last, in any window, when it is valid for data
        /// with <paramref name="axes"/>; otherwise the default. A formula written for another window may
        /// name axis variables this one does not have. The history is a convenience of the session, not
        /// part of a run: every run takes its formula from the dialog.
        /// </summary>
        internal static string StartingFormula(IReadOnlyList<Axis> axes)
        {
            var last = FormulaHistory.Calculator.Items.FirstOrDefault();
            return last != null
                && CompiledFormula.Check(last, FormulaEvaluator.VariableNames(axes), CalculatorDialog.References,
                    [axes.Select(a => a.Name).ToArray()]) == null
                ? last
                : CalculatorDialog.DefaultFormula;
        }

        /// <param name="axes">The axes of the data the formula is written for.</param>
        /// <exception cref="FormulaException">The formula is not valid.</exception>
        internal static CommandPlan CreatePlan(RunChoices choices, CalculatorDialog.CalculatorParameters p,
            IReadOnlyList<Axis> axes)
        {
            var formula = CompiledFormula.Compile(p.Formula, FormulaEvaluator.VariableNames(axes), CalculatorDialog.References,
                [axes.Select(a => a.Name).ToArray()]);
            return new()
            {
                Choices = choices,
                FailureTitle = "Calculator Failed",
                WorksOnScalars = true,
                Progress = ProgressMode.WholeStackOnly,
                ProgressLabel = "Calculating…",
                // m1 reads the whole source, so that the other frames of a stack can be reached when only one frame
                // (or a channel cube) is processed.
                CreateOperation = run => new FormulaOperation(formula, run.Progress, run.Token,
                    run.Source != null ? FormulaPosition.Of(run.Source, run.FrameIndex) : null,
                    run.PartOfSource && run.Source != null
                        ? new FormulaSource(run.Source, run.ValueMode, run.FrameIndex, run.CompositeAxisName)
                        : null),
                HistoryLabel = "Calculator",
                HistoryDetail = ctx =>
                {
                    string where = ctx.Cube != null
                        ? $" ([{ctx.CubePosition}])"
                        : ctx.SingleFrame ? $" (frame {ctx.FrameIndex})" : "";
                    return $"{p.Formula}{where}";
                },
                ResultTitle = source => $"Calculator of {source}",
                EditLabel = "Edit Formula…",
                Edit = async owner =>
                {
                    var answer = await CalculatorDialog.ShowAsync(owner, isMultiFrame: false, isLinkWindow: false, src: null,
                        axes, formula: p.Formula, editOnly: true);
                    if (answer == null) return null;
                    FormulaHistory.Calculator.Add(answer.Params.Formula);
                    return CreatePlan(choices, answer.Params, axes);
                },
            };
        }
    }
}
