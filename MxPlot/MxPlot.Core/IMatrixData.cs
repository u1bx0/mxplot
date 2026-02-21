using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{

    public interface IMatrixData: ICloneable
    {
     

        double XMax { get; set; }

        double XMin { get; set; }

        /// <summary>
        /// Number of data points along X axis (immutable)
        /// </summary>
        int XCount { get; }

        /// <summary>
        ///  = (XMax - XMin) / (XCount - 1) = XRange / (XCount - 1)
        /// </summary>
        double XStep { get; }

        /// <summary>
        ///  = XMax - XMin
        /// </summary>
        double XRange { get; }

        double YMax { get; set; }

        double YMin { get; set; }

        int YCount{ get; }

        /// <summary>
        ///  = (YMax - YMin) / (YCount - 1) = YRange / (YCount - 1)
        /// </summary>

        double YStep { get; }

        /// <summary>
        /// = MaxY - MinY
        /// </summary>
        double YRange { get; }

        /// <summary>
        /// Unit string for X axis
        /// </summary>
        string XUnit { get; set; }

        /// <summary>
        /// Unit string for Y axis
        /// </summary>
        string YUnit { get; set; }

        /// <summary>
        /// Total number of frames (1 if not a series)
        /// </summary>
        int FrameCount { get; }

        /// <summary>
        /// Active frame index (0 if not a series)
        /// </summary>
        int ActiveIndex { get; set; }

        /// <summary>
        /// Metadata dictionary with case-insensitive string keys and string values.
        /// Complex data types should be serialized to string (e.g., JSON, CSV).
        /// </summary>
        IDictionary<string, string> Metadata { get; }

        /// <summary>
        /// Define hyperstack dimensions
        /// </summary>
        DimensionStructure Dimensions { get; }

        /// <summary>
        /// Get the list of axes defined in Dimensions. 
        /// This is a convenience alias for <see cref="DimensionStructure.Axes"/>.
        /// 
        /// </summary>
        /// <returns>The list of <see cref="Axis"/>. </returns>
        IReadOnlyList<Axis> Axes { get; }

        /// <summary>
        /// 
        /// XYスケールが変化した場合に呼ばれる
        /// </summary>
        event EventHandler? ScaleChanged;

        /// <summary>
        /// Seriesのスケールが変化した場合に呼ばれる
        /// </summary>
        event EventHandler? FrameAxisChanged;

        /// <summary>
        /// アクティブなMatrixDataのIndexが変化した場合に呼ばれる
        /// </summary>
        event EventHandler? ActiveIndexChanged;

        /// <summary>
        /// 単位表記が変わった場合に呼ばれる
        /// </summary>
        event EventHandler? UnitChanged;


        Type ValueType { get; }

        /// <summary>
        /// Returns the minimum value supported by the implementation.
        /// </summary>
        /// <returns>The smallest value that can be represented or processed. The exact value depends on the specific
        /// implementation.</returns>
        double GetMinValue();

        /// <summary>
        /// Returns the largest value available in the current frame with a default value type.
        /// </summary>
        /// <returns>The maximum value as a double. </returns>
        double GetMaxValue();

        double XValue(int ix);
        
        int XIndexOf(double x, bool extendRange = false);

        double YValue(int iy);

        int YIndexOf(double y, bool extendRange = false);

        void SetXYScale(double xmin, double xmax, double ymin, double ymax);

        /// <summary>
        /// Define axes for series data: FrameCount must match the sum of counts in each axis.
        /// </summary>
        /// <param name="axes"></param>
        void DefineDimensions(params Axis[] axes);

        /// <summary>
        /// Get axis by its name. 
        /// This is a convenience alias for the indexer <see cref="DimensionStructure.this[string]"/>.
        /// </summary>
        /// <param name="axisName"></param>
        /// <returns>The <see cref="Axis"/> object, or null if not found.</returns>
        /// <seealso cref="DimensionStructure.this[string]"/>
        Axis? this [string axisName] { get; }

        /// <summary>
        /// Find and update max and min values in the specific frame. This is necessary when an array data is directly modified.
        /// </summary>
        /// <param name="frameIndex"></param>
        //void RefreshValueRange(int frameIndex);

        /// <summary>
        /// Find and update max and min values in the active frame. This is necessary when an array data is directly modified.
        /// </summary>
        //void RefreshValueRange();

        /// <summary>
        /// Marks the current frame is modified and forces the system to recalculate the min and max values when needed.
        /// </summary>
        void Invalidate();

        /// <summary>
        /// Invalidates the specified frame, marking it for recalculation of min and max values when needed. This is necessary when an array is directly modified <b>AFTER</b> calling GetValueRange.
        /// </summary>
        /// <param name="frameIndex">The zero-based index of the frame to invalidate. Must be within the valid range of frames.</param>
        void Invalidate(int frameIndex);

        /// <summary>
        /// Get the min and max values in the active frame as a tuple (min, max).
        /// </summary>
        /// <returns></returns>
        (double Min, double Max) GetValueRange();

        /// <summary>
        /// Get the min and max values in the specific frame as a tuple (min, max).
        /// </summary>
        /// <param name="index"></param>
        /// <returns>double[]{min, max}</returns>
        (double Min, double Max) GetValueRange(int frameIndex);

        /// <summary>
        /// Retrieves the minimum and maximum values along the specified axis.
        /// If <paramref name="fixedCoordinates"/> is not specified, the indices of the current active frame are used.
        /// </summary>
        /// <param name="targetAxis">The axis to scan.</param>
        /// <param name="fixedCoordinates">
        /// The coordinate (index) array defining the fixed axes (must include the target axis index). 
        /// The value for the <paramref name="targetAxis"/> index in this array is ignored.
        /// </param>
        /// <returns>A tuple containing the minimum and maximum values.</returns>
        (double Min, double Max) GetValueRange(Axis targetAxis, int[]? fixedCoordinates = null);

        /// <summary>
        /// Get the min and max values found in all the frames as a tuple (min, max).
        /// </summary>
        /// <returns>double[]{min, max}</returns>
        (double Min, double Max) GetGlobalValueRange();

        /// <summary>
        /// 
        /// </summary>
        /// <param name="ix"></param>
        /// <param name="iy"></param>
        /// <param name="v"></param>
        void SetValueAt(int ix, int iy, double v);


        /// <summary>
        /// </summary>
        /// <param name="ix"></param>
        /// <param name="iy"></param>
        /// <param name="indexInSeries"></param>
        /// <param name="v"></param>
        void SetValueAt(int ix, int iy, int frameIndex, double v);

        /// <summary>
        /// Get value at (ix, iy) as double
        /// </summary>
        /// <param name="ix"></param>
        /// <param name="iy"></param>
        /// <param name="iz">-1の場合は現在のActiveIndex</param>
        /// <returns></returns>
        double GetValueAt(int ix, int iy, int frameIndex = -1);

        /// <summary>
        /// Returns the value at the specified coordinates (x, y) as a double. 
        /// If frameIndex is -1, the value from the current active frame is returned. 
        /// If interpolate is true, the value is calculated using an interpolation algorithm based on the surrounding data points; otherwise, the value from the nearest data point is returned.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="frameIndex"></param>
        /// <param name="interpolate"></param>
        /// <returns></returns>
        double GetValueAsDouble(double x, double y, int frameIndex = -1, bool interpolate = false);

        /// <summary>
        /// Returns a read-only span containing the raw byte data for the specified frame.
        /// </summary>
        /// <param name="frameIndex">The zero-based index of the frame to retrieve. Specify -1 to retrieve the raw bytes for the current frame.</param>
        /// <returns>A read-only span of bytes representing the raw data of the specified frame.</returns>
        ReadOnlySpan<byte> GetRawBytes(int frameIndex = -1);
        
        /// <summary>
        /// Sets the value of the object from the specified raw byte data.
        /// </summary>
        /// <param name="bytes">A read-only span of bytes containing the raw data to use for setting the value.</param>
        /// <param name="frameIndex">The zero-based index of the frame to set from the raw bytes, or -1 to set all frames. Defaults to -1.</param>
        void SetFromRawBytes(ReadOnlySpan<byte> bytes, int frameIndex = -1);


        /// <summary>
        /// Applies the specified operation to the current IMatrixData and returns the processed result.
        /// </summary>
        /// <remarks>
        /// This method serves as a generic dispatch entry point. 
        /// Concrete implementations (e.g., <see cref="MatrixData{T}"/>) resolve the underlying generic type <typeparamref name="T"/> 
        /// and execute the appropriate logic based on the runtime type of the <paramref name="operation"/> 
        /// (such as <see cref="IVolumeOperation"/> for volume processing or <see cref="IFilterOperation"/> for image filtering).
        /// </remarks>
        /// <param name="operation">The operation to apply (e.g., Projection, Slice, or Filter). Cannot be null.</param>
        /// <returns>An <see cref="IMatrixData"/> instance containing the result of the applied operation.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="operation"/> is null.</exception>
        /// <exception cref="NotSupportedException">Thrown if the specific operation type is not supported by this data implementation.</exception>
        IMatrixData Apply(IOperation operation);
    }

}

