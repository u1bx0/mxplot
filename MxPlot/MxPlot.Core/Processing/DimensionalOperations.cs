using System;
using System.Collections.Generic;
using System.Threading;

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
    public record CropOperation(int X, int Y, int Width, int Height,
        IProgress<int>? Progress = null, CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Crop(X, Y, Width, Height, Progress, CancellationToken);
    }

    /// <summary>
    /// Crops the matrix using physical coordinates.
    /// </summary>
    public record CropByCoordinatesOperation(double XMin, double XMax, double YMin, double YMax,
        IProgress<int>? Progress = null, CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.CropByCoordinates(XMin, XMax, YMin, YMax, Progress, CancellationToken);
    }

    /// <summary>
    /// ExtractAlong: extract 1D slice along specified axis with base indices for other axes.
    /// </summary>
    public record ExtractAlongOperation(string AxisName, int[] BaseIndices, bool DeepCopy = false) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.ExtractAlong(AxisName, BaseIndices, DeepCopy);
    }

    /// <summary>
    /// Extracts a single frame at the specified linear frame index (SliceAt).
    /// Useful for isolating the currently displayed frame before applying further operations.
    /// </summary>
    public record SliceAtOperation(int FrameIndex) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.SliceAt(FrameIndex);
    }

    // =========================================================================================
    // Reverse Stack Operation
    // =========================================================================================

    /// <summary>
    /// Reverses the frame order along the specified axis.
    /// When <paramref name="AxisName"/> is <c>null</c>, all frames are reversed regardless of axis structure.
    /// For in-memory data a shallow copy is used (fast, zero-allocation);
    /// for virtual (MMF-backed) data a deep copy is always performed to avoid dangling references
    /// if the source is disposed after Replace.
    /// The axis scale (Min/Max) is left unchanged so that the physical coordinate range is preserved.
    /// </summary>
    public record ReverseStackOperation(string? AxisName) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
        {
            // Virtual data must always use deep copy: shallow copy produces a RoutedFrames<T>
            // that wraps the original VirtualFrames. If the source MatrixData is disposed after
            // a Replace operation the underlying MMF file handles are released and the result
            // holds dangling references.
            bool deepCopy = src.IsVirtual;

            int n = src.FrameCount;
            var dims = src.Dimensions;

            List<int> order;
            if (AxisName == null || dims == null || dims.AxisCount == 0)
            {
                // Reverse all frames
                order = new List<int>(n);
                for (int i = n - 1; i >= 0; i--) order.Add(i);
                var result = src.Reorder(order, deepCopy);
                if (dims != null && dims.AxisCount > 0)
                    result.DefineDimensions(Axis.CreateFrom([.. dims.Axes]));
                return result;
            }
            else
            {
                // Reverse only along the specified axis
                var axis = dims[AxisName]
                    ?? throw new ArgumentException($"Axis '{AxisName}' not found.");
                int axisIdx = -1;
                for (int i = 0; i < dims.Axes.Count; i++)
                    if (dims.Axes[i] == axis) { axisIdx = i; break; }

                // Build a reversed frame mapping by flipping the target axis index
                order = new List<int>(n);
                for (int f = 0; f < n; f++)
                {
                    // Decompose frame index into per-axis indices
                    int rem = f;
                    int[] coords = new int[dims.AxisCount];
                    for (int i = dims.AxisCount - 1; i >= 0; i--)
                    {
                        int s = 1;
                        for (int j = 0; j < i; j++) s *= dims.Axes[j].Count;
                        coords[i] = rem / s;
                        rem -= coords[i] * s;
                    }
                    // Flip the target axis
                    coords[axisIdx] = axis.Count - 1 - coords[axisIdx];
                    // Recompose
                    int mapped = 0;
                    int st = 1;
                    for (int i = 0; i < dims.AxisCount; i++)
                    {
                        mapped += coords[i] * st;
                        st *= dims.Axes[i].Count;
                    }
                    order.Add(mapped);
                }
                var result = src.Reorder(order, deepCopy);
                result.DefineDimensions(Axis.CreateFrom([.. dims.Axes]));
                return result;
            }
        }
    }
}
