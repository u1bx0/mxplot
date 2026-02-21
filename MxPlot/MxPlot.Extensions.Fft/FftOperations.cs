using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace MxPlot.Extensions.Fft
{
    /// <summary>
    ///  Represents an operation to perform a 2D Fast Fourier Transform (FFT) that can be called as a non-generic operation.<br/>
    ///  This returns an instance of MatrixData&lt;Complex&gt; containing the FFT result as IMatrixData. The behavior of the FFT (e.g., shift options) can be controlled via the parameters of this operation.<br/>
    ///  <c>e.g.  var ret = src.Apply(new Fft2DOperation(ShiftOption.None)) as MatrixData&lt;Complex&gt;;</c>
    /// </summary>
    /// <param name="Option">Specifies the shift behavior for the FFT operation. Determines how the frequency components are arranged in the
    /// output.</param>
    /// <param name="SrcIndex">The index of the source data to be transformed. A value of -1 indicates that the default source will be used.</param>
    /// <param name="Dst">An optional destination matrix in which to store the result of the FFT operation. If null, the result is not
    /// stored in a separate matrix.</param>
    /// <param name="DstIndex">The index in the destination matrix where the result will be stored. A value of -1 indicates that the result
    /// will be appended to the destination matrix.</param>
    /// <param name="SkipRefreshValueRange">Indicates whether to skip refreshing the value range of the destination matrix after the operation. The default
    /// is false.</param>
    public record Fft2DOperation
        (ShiftOption Option,
            int SrcIndex = -1, MatrixData<Complex>? Dst = null, int DstIndex = -1) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> data) where T : unmanaged
        {
            return data.Fft2D(Option, SrcIndex, Dst, DstIndex);
        }
    }

    /// <summary>
    /// Represents an operation that performs an inverse 2D-FFT (IFFT), used as a non-generic operation. This returns an instance of MatrixData&lt;Complex&gt; containing the IFFT result as IMatrixData. 
    /// The behavior of the IFFT (e.g., shift options) can be controlled via the parameters of this operation.<br/>
    /// </summary>
    /// <param name="Option">Specifies the shifting option to apply during the inverse FFT operation.</param>
    /// <param name="SrcIndex">The index of the source data to be transformed. The default value is -1, which indicates that the entire data
    /// set will be used.</param>
    /// <param name="Dst">An optional destination matrix in which to store the result of the inverse FFT. If null, the result is not
    /// stored in a separate matrix.</param>
    /// <param name="DstIndex">The index in the destination matrix where the result will be stored. The default value is -1, which indicates
    /// that the result will be stored starting from the beginning.</param>
    /// <param name="SkipRefreshValueRange">Indicates whether to skip refreshing the value range of the destination matrix after the operation. The default
    /// value is false.</param>
    public record InverseFft2DOperation
    (ShiftOption Option,
            int SrcIndex = -1, MatrixData<Complex>? Dst = null, int DstIndex = -1)
        : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> data) where T : unmanaged
        {
            return data.InverseFft2D(Option, SrcIndex, Dst, DstIndex);
        }
    }

}
