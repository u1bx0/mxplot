using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MxPlot.App.ViewModels
{
    /// <summary>
    /// Specialised <see cref="WindowListItemViewModel"/> for <see cref="MatrixPlotter"/> windows.
    /// Tracks <see cref="MatrixData"/> changes, exposes virtual badges,
    /// and schedules live thumbnail captures.
    /// </summary>
    public sealed class MatrixPlotterListItemViewModel : WindowListItemViewModel, IExportableAsImage
    {
        private IMatrixData? _matrixData;
        private bool _thumbnailPending;
        private bool _hasUnsavedChanges;

        /// <summary>The matrix data currently displayed in the associated window.</summary>
        public IMatrixData? MatrixData
        {
            get => _matrixData;
            private set
            {
                if (_matrixData == value) return;
                _matrixData = value;
                TrackLazySource(value);
                OnPropertyChanged(nameof(IsVirtualReadOnly));
                OnPropertyChanged(nameof(IsVirtualWritable));
                OnPropertyChanged(nameof(DimensionsText));
                OnPropertyChanged(nameof(DimensionLine));
                RefreshMetaData();
            }
        }

        // Backend of _matrixData whose IsVirtualChanged keeps the Virtual badge current (null for
        // in-memory data). A Lazy-decode dataset stops being Virtual once every frame is cached --
        // often before the window has even finished opening -- so evaluating the badge only when
        // the data is assigned showed it or not depending on load timing.
        private ILazyDataSource? _lazySource;

        /// <inheritdoc/>
        public override bool IsVirtualReadOnly => _matrixData?.IsVirtual == true && _matrixData.IsWritable == false;

        /// <inheritdoc/>
        public override bool IsVirtualWritable => _matrixData?.IsVirtual == true && _matrixData.IsWritable == true;

        /// <inheritdoc/>
        public override bool HasUnsavedChanges => _hasUnsavedChanges;

        /// <inheritdoc/>
        public override string DimensionsText
        {
            get
            {
                if (_matrixData == null) return string.Empty;
                return _matrixData.FrameCount > 1
                    ? $"{_matrixData.XCount}×{_matrixData.YCount}×…"
                    : $"{_matrixData.XCount}×{_matrixData.YCount}";
            }
        }

        /// <inheritdoc/>
        public override string DimensionLine
        {
            get
            {
                var md = _matrixData;
                if (md == null) return string.Empty;
                var parts = new List<string> { $"{md.XCount} (X)", $"{md.YCount} (Y)" };
                foreach (var axis in md.Axes)
                    parts.Add($"{axis.Count} ({GetAxisCode(axis)})");
                return string.Join(" × ", parts);
            }
        }

        private static string GetAxisCode(Axis axis)
        {
            if (axis is ColorAxis) return "C";
            return axis.Name switch
            {
                "Channel" => "C",
                "Time"    => "T",
                "Z"       => "Z",
                _ => axis.Name.Length <= 3 ? axis.Name : axis.Name[..1].ToUpperInvariant(),
            };
        }

        public MatrixPlotterListItemViewModel(MatrixPlotter plotter, IMatrixData? data = null)
            : base(plotter)
        {
            _matrixData = data;
            _hasUnsavedChanges = plotter.IsModified;
            RefreshMetaData();
            TrackLazySource(data);
            plotter.Closed += (_, _) => TrackLazySource(null);

            EventHandler onViewUpdated = (_, _) => ScheduleThumbnailUpdate(plotter);
            plotter.ViewUpdated += onViewUpdated;
            plotter.Closed += (_, _) => plotter.ViewUpdated -= onViewUpdated;

            EventHandler<IMatrixData?> onMatrixDataChanged = (_, newData) => MatrixData = newData;
            plotter.MatrixDataChanged += onMatrixDataChanged;
            plotter.Closed += (_, _) => plotter.MatrixDataChanged -= onMatrixDataChanged;

            EventHandler onModifiedChanged = (_, _) =>
            {
                _hasUnsavedChanges = plotter.IsModified;
                NotifyUnsavedChangesChanged();
            };
            plotter.IsModifiedChanged += onModifiedChanged;
            plotter.Closed += (_, _) => plotter.IsModifiedChanged -= onModifiedChanged;

            // Defer the initial capture to Background priority so it runs after
            // Show() and any RestoreViewSettings-triggered re-renders complete.
            ScheduleThumbnailUpdate(plotter);
        }

        /// <summary>
        /// Points the <see cref="ILazyDataSource.IsVirtualChanged"/> subscription at
        /// <paramref name="data"/>'s backend, or at nothing.
        /// </summary>
        private void TrackLazySource(IMatrixData? data)
        {
            var source = data?.GetDiagnosticCacheableList() as ILazyDataSource;
            if (ReferenceEquals(source, _lazySource)) return;

            if (_lazySource != null)
                _lazySource.IsVirtualChanged -= OnLazySourceIsVirtualChanged;
            _lazySource = source;
            if (source == null) return;

            source.IsVirtualChanged += OnLazySourceIsVirtualChanged;
            // The flip may already have happened before this subscription existed.
            Dispatcher.UIThread.Post(NotifyVirtualBadgesChanged);
        }

        // Raised on a background prefetch thread.
        private void OnLazySourceIsVirtualChanged(object? sender, EventArgs e)
            => Dispatcher.UIThread.Post(NotifyVirtualBadgesChanged);

        private void NotifyVirtualBadgesChanged()
        {
            OnPropertyChanged(nameof(IsVirtualReadOnly));
            OnPropertyChanged(nameof(IsVirtualWritable));
        }

        /// <inheritdoc/>
        protected override void RefreshMetaData()
        {
            var md = _matrixData;
            if (md != null)
            {
                var parts = new List<string> { $"{md.XCount}×{md.YCount}" };
                if (md.FrameCount > 1) parts.Add($"{md.FrameCount} frames");
                parts.Add(md.ValueTypeName);
                MetaData = string.Join("  |  ", parts);
            }
            else
            {
                MetaData = Window.GetType().Name;
            }
        }

        // Comfortably above the Icon view's largest "Thumbnail size" step (146px, see
        // MxPlotAppWindow.ViewMode.cs's CardSizeSteps), with headroom for 2x/Retina displays.
        private const int ThumbnailCaptureSize = 320;

        private void ScheduleThumbnailUpdate(MatrixPlotter plotter)
        {
            if (_thumbnailPending) return;
            _thumbnailPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _thumbnailPending = false;
                // A window can swap its data without raising MatrixDataChanged (a linked ROI view, an
                // orthogonal extract or a sync follower replaces its payload in place under a live
                // feed), so the displayed size and tooltip would keep describing the old payload.
                // Every such swap redraws the window and so reaches this callback, which runs after
                // the swap has completed; a no-op when the instance is unchanged.
                if (plotter.MatrixData is { } current) MatrixData = current;
                var snap = plotter.CaptureThumbnail(ThumbnailCaptureSize);
                if (snap != null) Thumbnail = snap;
            }, DispatcherPriority.Background);
        }

        /// <inheritdoc/>
        public Task<bool> ExportAsImageAsync(string filePath)
        {
            if (Window is not MatrixPlotter plotter || _matrixData == null)
                return Task.FromResult(false);
            plotter.ExportAsPng(filePath);
            return Task.FromResult(true);
        }
    }
}
