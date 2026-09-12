using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Rendering;

namespace MxPlot.UI.Avalonia.ViewModels
{
    /// <summary>ViewModel for <see cref="MxPlot.UI.Avalonia.Views.MatrixPlotter"/>.</summary>
    public partial class MatrixPlotterViewModel : ViewModelBase, IDisposable
    {
        [ObservableProperty]
        private IMatrixData? _matrixData;

        [ObservableProperty]
        private LookupTable _lut = ColorThemes.Grayscale;

        [ObservableProperty]
        private string _title = "MatrixPlotter";

        [ObservableProperty]
        private string? _sourcePath;

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
        [ObservableProperty]
        private ValueRangeMode _rangeMode = ValueRangeMode.Current;

        [ObservableProperty]
        private bool _isFixedRange;

        [ObservableProperty]
        private bool _isInvertedColor;

        /// <summary>
        /// LUT quantization level — the number of distinct colors the lookup table is resampled
        /// to before rendering. The level spinner offers 2–4096, but a value assigned from code
        /// is passed through unclamped; anything of 1 or less means "use the LUT's own level
        /// count" (see <c>RenderSurface</c>'s <c>depth &gt; 1</c> test).
        /// </summary>
        [ObservableProperty]
        private int _lutDepth = 256;   // matches MxView.LutDepthProperty's registered default

        [ObservableProperty]
        private double _fixedMin;

        [ObservableProperty]
        private double _fixedMax = 1.0;   // matches MxView.FixedMaxProperty's registered default

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
        /// Disposes the previous <see cref="MatrixData"/> when it is replaced, if it requires disposal.
        /// Also resets <see cref="SourcePath"/> so the new data is not falsely associated with the old path.
        /// The caller is responsible for setting <see cref="SourcePath"/> to the correct value afterward
        /// (e.g. <see cref="Create"/> sets it after this call).
        /// </summary>
        partial void OnMatrixDataChanging(IMatrixData? oldValue, IMatrixData? newValue)
        {
            if (oldValue?.RequiresDisposal == true)
                oldValue.Dispose();
            if (oldValue != newValue)
                SourcePath = null;
        }

        /// <summary>
        /// Disposes the current <see cref="MatrixData"/> if it requires disposal.
        /// Called when the <see cref="MatrixPlotter"/> window is closed.
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
            vm.Title = title ?? $"{data.ValueTypeName}  [{data.XCount} × {data.YCount}]";
            vm.SourcePath = sourcePath;
            return vm;
        }
    }
}
