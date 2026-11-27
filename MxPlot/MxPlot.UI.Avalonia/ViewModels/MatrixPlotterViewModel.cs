using System;
using System.Collections.Generic;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Rendering;

namespace MxPlot.UI.Avalonia.ViewModels
{
    /// <summary>ViewModel for <see cref="MxPlot.UI.Avalonia.Views.MatrixPlotter"/>.</summary>
    public class MatrixPlotterViewModel : ViewModelBase, IDisposable
    {
        private IMatrixData? _matrixData;
        private LookupTable _lut = ColorThemes.Grayscale;
        private string _title = "MatrixPlotter";
        private string? _sourcePath;
        private ValueRangeMode _rangeMode = ValueRangeMode.Current;
        private bool _isFixedRange;
        private bool _isInvertedColor;
        private int _lutDepth = 256;   // matches MxView.LutDepthProperty's registered default
        private double _fixedMin;
        private double _fixedMax = 1.0;   // matches MxView.FixedMaxProperty's registered default

        /// <summary>
        /// The displayed data. Replacing it disposes the previous data if that requires disposal,
        /// and resets <see cref="SourcePath"/> so the new data is not associated with the old
        /// path; both happen before the new data is in place and before this property's own
        /// change notification. The caller sets <see cref="SourcePath"/> to the correct value
        /// afterward (as <see cref="Create"/> does).
        /// </summary>
        public IMatrixData? MatrixData
        {
            get => _matrixData;
            set
            {
                if (EqualityComparer<IMatrixData?>.Default.Equals(_matrixData, value)) return;
                if (_matrixData?.RequiresDisposal == true)
                    _matrixData.Dispose();
                SourcePath = null;
                _matrixData = value;
                OnPropertyChanged();
            }
        }

        public LookupTable Lut
        {
            get => _lut;
            set => SetProperty(ref _lut, value);
        }

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }

        public string? SourcePath
        {
            get => _sourcePath;
            set => SetProperty(ref _sourcePath, value);
        }

        /// <summary>
        /// The value-range mode in effect. Unlike <see cref="IsFixedRange"/> — which only says
        /// whether the range is pinned, and so reads <c>true</c> for Fixed, All and Roi alike —
        /// this names the mode exactly.
        /// </summary>
        /// <remarks>
        /// Modes the current data cannot support are downgraded when applied: All on single-frame
        /// data, and Roi with no ROI overlay, both fall back to <see cref="ValueRangeMode.Current"/>.
        /// The downgraded value is written back here, so this property always reports the mode
        /// actually in effect rather than the one that was requested.
        /// </remarks>
        public ValueRangeMode RangeMode
        {
            get => _rangeMode;
            set => SetProperty(ref _rangeMode, value);
        }

        public bool IsFixedRange
        {
            get => _isFixedRange;
            set => SetProperty(ref _isFixedRange, value);
        }

        public bool IsInvertedColor
        {
            get => _isInvertedColor;
            set => SetProperty(ref _isInvertedColor, value);
        }

        /// <summary>
        /// LUT quantization level — the number of distinct colors the lookup table is resampled
        /// to before rendering. The level spinner offers 2–4096, but a value assigned from code
        /// is passed through unclamped; anything of 1 or less means "use the LUT's own level
        /// count" (see <c>RenderSurface</c>'s <c>depth &gt; 1</c> test).
        /// </summary>
        public int LutDepth
        {
            get => _lutDepth;
            set => SetProperty(ref _lutDepth, value);
        }

        public double FixedMin
        {
            get => _fixedMin;
            set => SetProperty(ref _fixedMin, value);
        }

        public double FixedMax
        {
            get => _fixedMax;
            set => SetProperty(ref _fixedMax, value);
        }

        /// <summary>
        /// Sets <see cref="FixedMin"/> and <see cref="FixedMax"/> together and raises both
        /// change notifications only after both backing fields are updated, so a listener
        /// reacting to either notification always observes the fully-applied pair (avoids a
        /// transient state where one bound has been updated but the other has not).
        /// </summary>
        public void ApplyFixedRange(double min, double max)
        {
            if (_fixedMin == min && _fixedMax == max) return;
            _fixedMin = min;
            _fixedMax = max;
            OnPropertyChanged(nameof(FixedMin));
            OnPropertyChanged(nameof(FixedMax));
        }

        /// <summary>
        /// Disposes the current <see cref="MatrixData"/> if it requires disposal.
        /// Called when the <see cref="MxPlot.UI.Avalonia.Views.MatrixPlotter"/> window is closed.
        /// </summary>
        public void Dispose()
        {
            if (_matrixData?.RequiresDisposal == true)
                _matrixData.Dispose();
            _matrixData = null;
        }

        /// <summary>
        /// Creates a pre-configured instance.
        /// </summary>
        public static MatrixPlotterViewModel Create(
            IMatrixData data,
            LookupTable? lut = null,
            string? title = null,
            string? sourcePath = null)
        {
            var vm = new MatrixPlotterViewModel();
            vm.MatrixData = data;
            vm.Lut = lut ?? ColorThemes.Grayscale;
            vm.Title = title ?? $"{data.ElementType.Name}  [{data.XCount} × {data.YCount}]";
            vm.SourcePath = sourcePath;
            return vm;
        }
    }
}
