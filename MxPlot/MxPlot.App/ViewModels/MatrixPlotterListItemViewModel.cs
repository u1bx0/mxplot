using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MxPlot.App.ViewModels
{
    /// <summary>
    /// Specialised <see cref="WindowListItemViewModel"/> for <see cref="MatrixPlotter"/> windows.
    /// Tracks <see cref="MatrixData"/> changes, exposes virtual/in-memory badges,
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
                OnPropertyChanged(nameof(IsInMemory));
                OnPropertyChanged(nameof(IsVirtualReadOnly));
                OnPropertyChanged(nameof(IsVirtualWritable));
                OnPropertyChanged(nameof(DimensionsText));
                OnPropertyChanged(nameof(DimensionLine));
                RefreshMetaData();
            }
        }

        /// <inheritdoc/>
        public override bool IsInMemory => _matrixData != null && !_matrixData.IsVirtual;

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
            if (axis is ColorChannel) return "C";
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
            RefreshMetaData();

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

        /// <inheritdoc/>
        protected override void RefreshMetaData()
        {
            var md = _matrixData;
            if (md != null)
            {
                var parts = new List<string> { $"{md.XCount}×{md.YCount}" };
                if (md.FrameCount > 1) parts.Add($"{md.FrameCount} frames");
                parts.Add(md.ValueType.Name);
                MetaData = string.Join("  |  ", parts);
            }
            else
            {
                MetaData = Window.GetType().Name;
            }
        }

        private void ScheduleThumbnailUpdate(MatrixPlotter plotter)
        {
            if (_thumbnailPending) return;
            _thumbnailPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _thumbnailPending = false;
                var snap = plotter.CaptureThumbnail();
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
