using System;
using CommunityToolkit.Mvvm.ComponentModel;
using MxPlot.Core;
using MxPlot.Core.Imaging;

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
        private int _activeFrame = 0;

        [ObservableProperty]
        private string _title = "MatrixPlotter";

        [ObservableProperty]
        private string? _sourcePath;

        /// <summary>
        /// Disposes the previous <see cref="MatrixData"/> when it is replaced, if it requires disposal.
        /// </summary>
        partial void OnMatrixDataChanging(IMatrixData? oldValue, IMatrixData? newValue)
        {
            if (oldValue?.RequiresDisposal == true)
                oldValue.Dispose();
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
            vm.MatrixData  = data;
            vm.Lut         = lut ?? ColorThemes.Grayscale;
            vm.Title       = title ?? $"{data.ValueTypeName}  [{data.XCount} × {data.YCount}]";
            vm.SourcePath  = sourcePath;
            return vm;
        }
    }
}
