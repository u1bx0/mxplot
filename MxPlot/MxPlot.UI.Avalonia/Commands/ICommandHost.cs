using Avalonia.Controls;
using MxPlot.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// What an <see cref="IProcessingCommand"/> may ask of the window it runs in. A command sees the
    /// window only through this interface, so it cannot reach into the window's internals.
    /// </summary>
    internal interface ICommandHost
    {
        /// <summary>The window, for use as a dialog owner.</summary>
        Window Owner { get; }

        /// <summary>The data shown in the window, or <see langword="null"/> when none is loaded.</summary>
        IMatrixData? Data { get; }

        /// <summary>The window title.</summary>
        string Title { get; }

        /// <summary>Whether the window is in Composite mode.</summary>
        bool IsCompositeMode { get; }

        /// <summary>
        /// Whether "Replace data" must not be offered: the window is kept live by a sync, or the host
        /// application opted out.
        /// </summary>
        bool IsReplaceBlocked { get; }

        /// <summary>The value range shown by the range bar, or the current frame's range when none is shown.</summary>
        (double Min, double Max) DisplayedRange { get; }

        /// <summary>
        /// Whether "This frame only" is a real choice for <paramref name="data"/>. It is not when Composite
        /// mode leaves no axis other than the composited one to step through.
        /// </summary>
        bool IsThisFrameOnlyAChoice(IMatrixData data);

        /// <summary>
        /// In Composite mode, the cube of every channel at the current position of the other axes;
        /// otherwise <see langword="null"/>.
        /// </summary>
        (IMatrixData Cube, string ChannelAxisName)? TryExtractCompositeCube(IMatrixData data);

        /// <summary>The position of a Composite cube along the other axes, e.g. <c>Z=3, T=2</c>.</summary>
        string DescribeCompositeCube(IMatrixData data, string channelAxisName);

        /// <summary>Closes the hamburger menu.</summary>
        void HideMenu();

        /// <summary>Shows progress in the status bar and blocks input; dispose the result to end it.</summary>
        ProgressSession BeginProgress(string label, bool cancellable);

        /// <summary>The window's overlays as JSON, or <c>[]</c> when there are none.</summary>
        string OverlaysJson { get; }

        /// <summary>Shows a message dialog.</summary>
        Task ShowMessageAsync(string title, string message);

        /// <summary>
        /// Shows <paramref name="result"/> in this window in place of its data. The window stays in
        /// Composite mode on <paramref name="compositeAxisName"/> when that is given.
        /// </summary>
        void ReplaceData(IMatrixData result, string? compositeAxisName);

        /// <summary>
        /// Opens <paramref name="result"/> in a new window, which starts in Composite mode on
        /// <paramref name="compositeAxisName"/> when that is given, and with this window's range and LUT
        /// settings when <paramref name="copyDisplayState"/> is set.
        /// </summary>
        void ShowResult(IMatrixData result, string title, string? compositeAxisName, bool copyDisplayState);

        /// <summary>
        /// Opens <paramref name="result"/> in a new window that is refreshed together with this one (an
        /// extract of it, not a recomputation).
        /// </summary>
        void ShowLinkedResult(IMatrixData result, string title);

        /// <summary>
        /// Opens <paramref name="result"/> in a new window that follows this one: whenever this window's
        /// active frame changes or its data is refreshed, the window's data is recomputed by
        /// <paramref name="transform"/>.
        /// </summary>
        void ShowSyncedResult(IMatrixData result, string title, string? compositeAxisName, bool copyDisplayState,
            Func<SyncInput, CancellationToken, SyncOutput> transform);
    }

    /// <summary>What a live result window recomputes from.</summary>
    /// <param name="Source">The source window's data.</param>
    /// <param name="Operand">The part of it to operate on: the current frame, or the channel cube in Composite mode.</param>
    /// <param name="CompositeAxisName">The channel axis when <paramref name="Operand"/> is a channel cube; otherwise <see langword="null"/>.</param>
    internal readonly record struct SyncInput(IMatrixData Source, IMatrixData Operand, string? CompositeAxisName);

    /// <summary>A recomputed result.</summary>
    /// <param name="Data">The new data.</param>
    /// <param name="CompositeAxisName">The axis the result window should composite on, or <see langword="null"/>.</param>
    internal readonly record struct SyncOutput(IMatrixData Data, string? CompositeAxisName);
}
