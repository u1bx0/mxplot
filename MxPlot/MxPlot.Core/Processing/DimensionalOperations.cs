using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core.Processing
{

    // =========================================================================================
    // Basic Structural Operations
    // =========================================================================================

    /// <summary>
    /// Transposes the matrix (swaps dimensions).
    /// </summary>
    public record TransposeOperation : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Transpose();
    }

    /// <summary>
    /// Reorders the axes of the matrix.
    /// </summary>
    public record ReorderAxesOperation(string[] NewAxisOrder, bool DeepCopy = false) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Reorder(NewAxisOrder, DeepCopy);
    }

    /// <summary>
    /// Extracts a single frame/slice at the specified index along the given axis (SelectBy).
    /// </summary>
    public record SelectByOperation(string AxisName, int Index, bool DeepCopy = false) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.SelectBy(AxisName, Index, DeepCopy);
    }

    // =========================================================================================
    // Cropping / Slicing Operations
    // =========================================================================================

    /// <summary>
    /// Crops the matrix using pixel coordinates (x, y, w, h).
    /// </summary>
    public record CropOperation(int X, int Y, int Width, int Height) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Crop(X, Y, Width, Height);
    }

    /// <summary>
    /// Crops the matrix using physical coordinates.
    /// </summary>
    public record CropByCoordinatesOperation(double XMin, double XMax, double YMin, double YMax) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.CropByCoordinates(XMin, XMax, YMin, YMax);
    }

    /// <summary>
    /// ExtractAlong: extract 1D slice along specified axis with base indices for other axes.
    /// </summary>
    public record ExtractAlongOperation(string AxisName, int[] BaseIndices, bool DeepCopy = false) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.ExtractAlong(AxisName, BaseIndices, DeepCopy);
    }
}
