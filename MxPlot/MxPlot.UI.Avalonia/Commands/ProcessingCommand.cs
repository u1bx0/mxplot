using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>What a run shows while it works.</summary>
    internal enum ProgressMode
    {
        /// <summary>Nothing: the operation is quick.</summary>
        None,

        /// <summary>A busy indicator without a Cancel button.</summary>
        Indeterminate,

        /// <summary>Determinate progress with a Cancel button when more than one frame is processed; otherwise <see cref="Indeterminate"/>.</summary>
        WholeStackOnly,

        /// <summary>Determinate progress with a Cancel button.</summary>
        Always,
    }

    /// <summary>What an operation may use while it runs.</summary>
    /// <param name="Progress">Reports progress, or <see langword="null"/> when the run shows none.</param>
    /// <param name="Token">Cancelled by the Cancel button; <see cref="CancellationToken.None"/> when the run cannot be cancelled.</param>
    /// <param name="State">What <see cref="CommandPlan.Prepare"/> produced, or <see langword="null"/>.</param>
    internal readonly record struct RunContext(IProgress<int>? Progress, CancellationToken Token, object? State = null);

    /// <summary>What a command may look at before its operation runs.</summary>
    /// <param name="Data">The source window's data.</param>
    /// <param name="Operand">The data the operation will process: the channel cube, or all of <paramref name="Data"/>.</param>
    /// <param name="IsMultiFrame">Whether "This frame only" is a real choice for the data.</param>
    /// <param name="ThisFrameOnly">Whether the run is limited to the active frame or cube (forced on in Composite mode when the choice is not offered).</param>
    /// <param name="SingleFrame">Whether one frame (or one cube) is processed rather than the whole stack.</param>
    /// <param name="HasCube">Whether <paramref name="Operand"/> is a channel cube.</param>
    internal sealed record RunScope(
        IMatrixData Data, IMatrixData Operand, bool IsMultiFrame, bool ThisFrameOnly, bool SingleFrame, bool HasCube);

    /// <summary>What a command needs to know about a run to finish and describe its result.</summary>
    /// <param name="Source">The source window's data.</param>
    /// <param name="Cube">The channel cube that was processed, or <see langword="null"/>.</param>
    /// <param name="CubePosition">Where the cube sits along the other axes; <see langword="null"/> without a cube.</param>
    /// <param name="SingleFrame">Whether one frame (or one cube) was processed rather than the whole stack.</param>
    /// <param name="FrameIndex">The active frame of the source.</param>
    internal sealed record ResultContext(
        IMatrixData Source, IMatrixData? Cube, string? CubePosition, bool SingleFrame, int FrameIndex);

    /// <summary>
    /// The checkboxes of a command's dialog as the user left them. A choice the dialog does not offer is
    /// <see langword="false"/>.
    /// </summary>
    /// <param name="ThisFrameOnly">Process only the active frame (in Composite mode: every channel at the current position).</param>
    /// <param name="SyncSource">Keep the result in a new window that follows the source. Takes effect only when one frame is processed.</param>
    /// <param name="ReplaceData">Show the result in the source window instead of a new one.</param>
    internal sealed record RunChoices(bool ThisFrameOnly = false, bool SyncSource = false, bool ReplaceData = false)
    {
        /// <summary>The choices of a command that has no dialog.</summary>
        public static RunChoices None { get; } = new();
    }

    /// <summary>What a command's dialog returns on OK.</summary>
    /// <param name="Params">The values the dialog collected for the operation.</param>
    /// <param name="Choices">The dialog's checkboxes.</param>
    internal sealed record DialogAnswer<T>(T Params, RunChoices Choices);

    /// <summary>
    /// Everything <see cref="ProcessingCommand"/> needs to run a command once the user has answered: how
    /// to build the operation and how to word and place the result. A command builds one from its dialog's
    /// answer; only what differs from the defaults needs to be set.
    /// </summary>
    internal sealed record CommandPlan
    {
        /// <summary>The dialog's checkboxes.</summary>
        public required RunChoices Choices { get; init; }

        /// <summary>Title of the message shown when the operation throws.</summary>
        public required string FailureTitle { get; init; }

        /// <summary>
        /// Builds the operation. It processes every frame of the data it is given: for "This frame only" that
        /// is the active frame cut out with <c>SliceAt</c>, or the channel cube in Composite mode. A window
        /// that follows its source builds the operation again with this for every refresh.
        /// </summary>
        public required Func<RunContext, IMatrixDataOperation> CreateOperation { get; init; }

        public required string HistoryLabel { get; init; }

        public required Func<ResultContext, string> HistoryDetail { get; init; }

        /// <summary>The result window's title, from the source window's title.</summary>
        public required Func<string, string> ResultTitle { get; init; }

        /// <summary>
        /// Whether the operation always gets the whole data: there is no "This frame only", Composite cube
        /// or Sync handling.
        /// </summary>
        public bool WholeData { get; init; }

        public ProgressMode Progress { get; init; } = ProgressMode.Indeterminate;

        /// <summary>The text of the progress display.</summary>
        public string ProgressLabel { get; init; } = "";

        /// <summary>Whether the result can be composited, so a result from a channel cube stays in Composite mode.</summary>
        public bool KeepsCompositeMode { get; init; } = true;

        /// <summary>Whether a result window starts with the source's range and LUT settings instead of the defaults.</summary>
        public bool CopiesDisplayState { get; init; }

        /// <summary>Whether a new window is refreshed together with the source (an extract) instead of standing alone.</summary>
        public bool ResultIsLinked { get; init; }

        /// <summary>The message shown when the operation runs out of memory.</summary>
        public string OutOfMemoryMessage { get; init; } = "Not enough memory to process this dataset.\nOperation cancelled.";

        /// <summary>
        /// Work the operation needs done first, such as a scan of the whole stack. What it returns reaches
        /// <see cref="CreateOperation"/> as <see cref="RunContext.State"/>. It may throw
        /// <see cref="OperationCanceledException"/> to end the run silently.
        /// </summary>
        public Func<ICommandHost, RunScope, Task<object?>>? Prepare { get; init; }

        /// <summary>
        /// Finishes the result before it is shown; a window that follows its source runs it for every refresh.
        /// By default the source's properties, scale included, are copied over, except with
        /// <see cref="WholeData"/>, where the operation's result is left as it is.
        /// </summary>
        public Action<IMatrixData, ResultContext>? PrepareResult { get; init; }

        /// <summary>Called once, after <see cref="PrepareResult"/>, for what only the first result needs.</summary>
        public Action<ICommandHost, IMatrixData, ResultContext>? OnResultReady { get; init; }
    }

    /// <summary>
    /// A command that runs one <see cref="IMatrixDataOperation"/>: it asks the user, then this class runs
    /// the steps every such command shares - the "This frame only" and Composite cube handling, progress
    /// and cancellation, error reporting, history, and where the result goes (replace, new window, or a
    /// window that follows the source). A subclass supplies the dialog and the <see cref="CommandPlan"/>.
    /// </summary>
    internal abstract class ProcessingCommand : IProcessingCommand
    {
        /// <summary>
        /// Shows the dialog and turns its answer into a plan; <see langword="null"/> when the user cancels
        /// or the command does not apply. A command without a dialog returns its plan at once.
        /// </summary>
        /// <param name="isMultiFrame">Whether "This frame only" is a real choice for <paramref name="data"/>.</param>
        protected abstract Task<CommandPlan?> AskAsync(ICommandHost host, bool isMultiFrame, IMatrixData data);

        public async Task RunAsync(ICommandHost host)
        {
            var data = host.Data;
            if (data == null) return;
            host.HideMenu();

            bool isMultiFrame = host.IsThisFrameOnlyAChoice(data);
            var plan = await AskAsync(host, isMultiFrame, data);
            if (plan == null) return;

            var choices = plan.Choices;
            bool frameScoped = !plan.WholeData;

            // Forced on when Composite mode has no surviving axis besides the composited one: the dialog
            // has no "This frame only" checkbox then, and the channel cube is the whole stack.
            bool thisFrameOnly = frameScoped && (choices.ThisFrameOnly || (host.IsCompositeMode && !isMultiFrame));
            bool singleFrame = frameScoped && (thisFrameOnly || !isMultiFrame);
            int frameIndex = data.ActiveIndex;
            // Composite + This frame only: every channel at the current position, instead of the one
            // channel that ActiveIndex is pinned to.
            var cube = thisFrameOnly ? host.TryExtractCompositeCube(data) : null;

            // "This frame only" of a stack: the operation gets that frame cut out, as data of its own.
            bool sliceFirst = cube == null && singleFrame && isMultiFrame;
            bool tracked = plan.Progress == ProgressMode.Always
                || (plan.Progress == ProgressMode.WholeStackOnly && !singleFrame);

            IMatrixData result;
            try
            {
                var scope = new RunScope(data, cube?.Cube ?? data, isMultiFrame, thisFrameOnly, singleFrame, cube != null);
                object? state = plan.Prepare != null ? await plan.Prepare(host, scope) : null;

                using var session = plan.Progress != ProgressMode.None
                    ? host.BeginProgress(plan.ProgressLabel, cancellable: tracked)
                    : null;
                var run = new RunContext(tracked ? session?.Progress : null, tracked ? session?.Token ?? default : default, state);
                var operation = plan.CreateOperation(run);
                result = await Task.Run(() =>
                {
                    var operand = cube?.Cube ?? (sliceFirst ? data.Apply(new SliceAtOperation(frameIndex)) : data);
                    return operand.Apply(operation);
                }, run.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (OutOfMemoryException)
            {
                await host.ShowMessageAsync("Out of Memory", plan.OutOfMemoryMessage);
                return;
            }
            catch (Exception ex)
            {
                await host.ShowMessageAsync(plan.FailureTitle, ex.Message);
                return;
            }

            var ctx = new ResultContext(data, cube?.Cube,
                cube != null ? host.DescribeCompositeCube(data, cube.Value.ChannelAxisName) : null,
                singleFrame, frameIndex);
            PrepareResult(plan, result, ctx);
            plan.OnResultReady?.Invoke(host, result, ctx);
            MatrixPlotter.AppendHistory(result, plan.HistoryLabel, host.Title, plan.HistoryDetail(ctx));

            string title = plan.ResultTitle(host.Title);
            string? compositeAxis = plan.KeepsCompositeMode ? cube?.ChannelAxisName : null;
            if (choices.ReplaceData)
                host.ReplaceData(result, compositeAxis);
            else if (choices.SyncSource && singleFrame)
                host.ShowSyncedResult(result, title, compositeAxis, plan.CopiesDisplayState, (input, ct) => Recompute(plan, input, ct));
            else if (plan.ResultIsLinked)
                host.ShowLinkedResult(result, title);
            else
                host.ShowResult(result, title, compositeAxis, plan.CopiesDisplayState);
        }

        private static void PrepareResult(CommandPlan plan, IMatrixData result, ResultContext ctx)
        {
            if (plan.PrepareResult != null)
                plan.PrepareResult(result, ctx);
            else if (!plan.WholeData)
                result.CopyPropertiesFrom(ctx.Source, copyScale: true, copyDimensions: false);
        }

        private static SyncOutput Recompute(CommandPlan plan, SyncInput input, CancellationToken ct)
        {
            // The operand is a single frame or a channel cube: process all of it.
            var operation = plan.CreateOperation(new RunContext(null, ct));
            var updated = input.Operand.Apply(operation);
            var ctx = new ResultContext(input.Source,
                input.CompositeAxisName != null ? input.Operand : null, null,
                true, input.Source.ActiveIndex);
            PrepareResult(plan, updated, ctx);
            return new SyncOutput(updated, plan.KeepsCompositeMode ? input.CompositeAxisName : null);
        }
    }
}
