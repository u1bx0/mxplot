using Avalonia.Controls;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Commands;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    // The window as processing commands see it (see ICommandHost). The members are implemented
    // explicitly so that they stay off the window's own API.
    public partial class MatrixPlotter : ICommandHost
    {
        Window ICommandHost.Owner => this;

        IMatrixData? ICommandHost.Data => _currentData;

        string ICommandHost.Title => Title ?? string.Empty;

        bool ICommandHost.IsCompositeMode => _isCompositeMode;

        bool ICommandHost.IsReplaceBlocked => IsReplaceDataBlocked;

        (double Min, double Max) ICommandHost.DisplayedRange
        {
            get
            {
                double min = _rangeBar.DisplayedMinValue;
                double max = _rangeBar.DisplayedMaxValue;
                return double.IsNaN(min) || double.IsNaN(max) ? _view.ScanCurrentFrameRange() : (min, max);
            }
        }

        bool ICommandHost.IsThisFrameOnlyAChoice(IMatrixData data) => IsThisFrameOnlyAChoice(data);

        (IMatrixData Cube, string ChannelAxisName)? ICommandHost.TryExtractCompositeCube(IMatrixData data)
            => TryExtractCompositeFrameCube(data);

        string ICommandHost.DescribeCompositeCube(IMatrixData data, string channelAxisName)
            => BuildCompositeCubeLabel(data, channelAxisName);

        void ICommandHost.HideMenu() => HideMenuPanel();

        ProgressSession ICommandHost.BeginProgress(string label, bool cancellable)
        {
            var cts = cancellable ? new CancellationTokenSource() : null;
            var progress = BeginProgress(label, blockInput: true, cts);
            return new ProgressSession(progress, cts, EndProgress);
        }

        string ICommandHost.OverlaysJson => _view.OverlayManager.SerializeOverlays();

        Task ICommandHost.ShowMessageAsync(string title, string message) => ShowMessageDialogAsync(title, message);

        void ICommandHost.ReplaceData(IMatrixData result, string? compositeAxisName)
            => ReplaceDataKeepingComposite(result, compositeAxisName);

        void ICommandHost.ShowResult(IMatrixData result, string title, string? compositeAxisName, bool copyDisplayState)
        {
            var window = Create(result, _view.Lut, title);
            if (copyDisplayState) CopyRangeAndLutStateTo(window);
            if (compositeAxisName != null) SeedChildCompositeMode(window, result, compositeAxisName);
            window.Show();
        }

        void ICommandHost.ShowLinkedResult(IMatrixData result, string title)
            => CreateLinked(result, _view.Lut, title).Show();

        void ICommandHost.ShowSyncedResult(IMatrixData result, string title, string? compositeAxisName, bool copyDisplayState,
            Func<SyncInput, CancellationToken, SyncOutput> transform)
        {
            // CreateLinked marks the child as secondary, which suppresses the unsaved-changes
            // confirmation on close: a window that follows its source is fully derived and recomputed
            // on demand.
            var follower = CreateLinked(result, _view.Lut, title, linkRefresh: false);
            if (copyDisplayState) CopyRangeAndLutStateTo(follower);
            if (compositeAxisName != null) SeedChildCompositeMode(follower, result, compositeAxisName);
            follower.Show();

            _ = new LinkedView(follower, this, (source, ct) =>
            {
                var sourceData = source.MatrixData;
                if (sourceData == null) return Task.FromResult<LinkedViewUpdate?>(null);

                // While the source is in Composite mode, take the whole channel cube instead of a
                // single-frame slice, so the follower keeps every channel instead of collapsing to
                // channel 0.
                var cube = source.TryExtractCompositeFrameCube(sourceData);
                IMatrixData operand = cube?.Cube
                    ?? sourceData.Apply(new SliceAtOperation(sourceData.ActiveIndex));

                return Task.Run<LinkedViewUpdate?>(() =>
                {
                    var output = transform(new SyncInput(sourceData, operand, cube?.ChannelAxisName), ct);
                    return new LinkedViewUpdate(output.Data, output.CompositeAxisName);
                }, ct);
            });
        }

        /// <summary>
        /// Replaces this window's data with <paramref name="result"/>. A Composite window stays in
        /// Composite mode on <paramref name="compositeAxisName"/> when that is given.
        /// </summary>
        private void ReplaceDataKeepingComposite(IMatrixData result, string? compositeAxisName)
        {
            var snapshot = compositeAxisName != null ? CaptureCompositeCubeState() : null;
            var title = Title;
            SetMatrixData(result, closeDerivedWindows: true);
            Title = title;
            if (snapshot != null && compositeAxisName != null)
                ReenterCompositeMode(snapshot.Value, compositeAxisName);
            SetDirty(DirtyFlags.Data, true);
        }
    }
}
