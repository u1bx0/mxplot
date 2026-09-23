using System;
using System.Collections.Generic;
using System.Threading;

namespace MxPlot.Core.Processing
{
    // =========================================================================================
    // Basic Structural Operations
    // =========================================================================================

    /// <summary>
    /// Transposes the matrix (swaps X/Y dimensions). Always a deep copy.
    /// </summary>
    public record TransposeOperation(
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Transpose(Progress, CancellationToken);
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
    public record SliceAtOperation(int FrameIndex, bool DeepCopy = false) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.SliceAt(FrameIndex, DeepCopy);
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
            => src.ReverseStack(AxisName);
    }

    // =========================================================================================
    // Channel Collapse (Grayscale) Operation
    // =========================================================================================

    /// <summary>How <see cref="DimensionalOperator.Grayscale{T}"/> combines the channels into one value.</summary>
    public enum GrayscaleMethod
    {
        /// <summary>
        /// Rec.709 luma for an R/G/B triplet (see <see cref="ColorAxis.IsRgbTriplet"/>),
        /// plain mean for every other channel axis.
        /// </summary>
        Auto,

        /// <summary>Arithmetic mean of all channels. Works for any channel count.</summary>
        Mean,

        /// <summary>Rec.709 luma <c>0.2126 R + 0.7152 G + 0.0722 B</c>. Requires exactly 3 channels.</summary>
        LumaRec709,
    }

    /// <summary>
    /// Collapses a channel axis into a single grayscale channel, leaving every other axis
    /// (Z, Time, …) intact. The axis itself disappears from the result; collapsing the only
    /// axis yields a plain single-frame matrix.
    /// </summary>
    /// <remarks>
    /// A colour image decomposed into R/G/B needs luma weighting to look natural, whereas a
    /// fluorescence stack has no such convention and is simply averaged. <see cref="GrayscaleMethod.Auto"/>
    /// tells the two apart by the channel tags rather than by the channel count, so a three-colour
    /// fluorescence stack is not silently treated as RGB.
    /// </remarks>
    public record GrayscaleOperation(
        string AxisName = "Channel",
        GrayscaleMethod Method = GrayscaleMethod.Auto,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Grayscale(AxisName, Method, Progress, CancellationToken);
    }

    // =========================================================================================
    // Substack / Volume Crop Operations
    // =========================================================================================

    /// <summary>
    /// Extracts a contiguous range along the specified axis while preserving all other axes (Hyperstack-safe).
    /// </summary>
    public record SubstackOperation(string AxisName, int Start, int Count, bool DeepCopy = true) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Substack(AxisName, Start, Count, DeepCopy);
    }

    /// <summary>
    /// Crops both a contiguous axis range (Substack) and an XY rectangle in one operation.
    /// Internally applies Substack first, then XY Crop, for efficiency.
    /// </summary>
    public record VolumeCropOperation(
        string AxisName, int ZStart, int ZCount,
        int X, int Y, int Width, int Height,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.VolumeCrop(AxisName, ZStart, ZCount, X, Y, Width, Height, Progress, CancellationToken);
    }
}
