using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{

    public interface IMatrixData: ICloneable, IDisposable
    {

       
        /// <summary>
        /// Gets or sets the maximum value of the X-axis.
        /// </summary>
        double XMax { get; set; }

        /// <summary>
        /// Gets or sets the minimum value of the X-axis.
        /// </summary>
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

        /// <summary>
        /// Sets or gets the maximum value along the Y axis. 
        /// </summary>
        double YMax { get; set; }

        /// <summary>
        /// Sets or gets the minimum value along the Y axis. 
        /// </summary>
        double YMin { get; set; }

        /// <summary>
        /// Number of data points along Y axis (immutable)
        /// </summary>
        int YCount { get; }

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
        /// Get the list of axes defined in Dimensions for multi-frame data. For single-frame data, this will be an empty list with Axes.Count == 0. 
        /// This is a convenience alias for <see cref="DimensionStructure.Axes"/>.
        /// 
        /// </summary>
        /// <returns>The list of <see cref="Axis"/>. </returns>
        IReadOnlyList<Axis> Axes { get; }


        /// <summary>
        /// Gets the type of the value represented by the current instance.
        /// </summary>
        Type ValueType { get; }

        /// <summary>
        /// Gets the name of T used in this instance, which may be a primitive type name (e.g., "double", "int") or the actual type name for custom structs.
        /// </summary>
        string ValueTypeName { get; }

        /// <summary>
        /// Gets the size, in bytes, of each element in the frame;
        /// </summary>
        int ElementSize { get; }

        # region Properties related to virtual data and caching

        /// <summary>
        /// Gets whether the data is virtual (e.g., memory-mapped file) or in-memory. Virtual data may require explicit disposal and may have different performance characteristics.
        /// </summary>
        bool IsVirtual { get; }

        /// <summary>
        /// Gets whether pixel data modifications are reflected in the backing store.
        /// </summary>
        /// <remarks>
        /// <strong>Implementation Note (Pragmatic Hack Inversion):</strong><br/>
        /// This property is the logical inverse of the underlying <c>IList&lt;T[]&gt;.IsReadOnly</c>.
        /// In the MxPlot framework, <c>IList&lt;T[]&gt;.IsReadOnly</c> is pragmatically repurposed
        /// beyond its standard C# semantics (structural immutability) to also represent the mutability
        /// of the data layer — i.e., whether writes to <c>T[]</c> are persisted to the backing store.
        /// See the XML documentation on <see cref="VirtualFrames{T}.IsReadOnly"/> for the full rationale.
        /// <para/>
        /// <c>IsWritable</c> exposes this information at the <see cref="IMatrixData"/> level
        /// without leaking the <c>IList&lt;T[]&gt;</c> implementation detail.
        /// <list type="table">
        ///   <listheader><term>Backing</term><description>IsWritable</description></listheader>
        ///   <item><term>In-memory (<c>List&lt;T[]&gt;</c>)</term><description><c>true</c> — array writes are immediate.</description></item>
        ///   <item><term>Writable virtual (MMF, read-write)</term><description><c>true</c> — writes are flushed to the backing file.</description></item>
        ///   <item><term>Read-only virtual (MMF, read-only)</term><description><c>false</c> — the backing file cannot be modified.</description></item>
        /// </list>
        /// <para>
        /// UI layers use this to distinguish <b>Save As</b> (backing changes, title updates)
        /// from <b>Save a Copy</b> (backing unchanged, title stays).
        /// </para>
        /// </remarks>
        bool IsWritable { get; }

        /// <summary>
        /// Gets or sets the caching strategy used to manage cached data for <see cref="VirtualFrames{T}"/> instances.
        /// This is valid only if IsVirtual is true.
        /// </summary>
        /// <remarks>Selecting an appropriate caching strategy can impact performance and resource usage.
        /// Different implementations of <see cref="ICacheStrategy"/> may be suitable depending on the application's
        /// requirements and data access patterns.</remarks>
        ICacheStrategy? CacheStrategy { get; set; }

        /// <summary>
        /// Checks if the current instance contains data that requires explicit disposal.
        /// </summary>
        bool RequiresDisposal { get; }


        /// <summary>
        /// Returns the underlying virtual frame list for diagnostic purposes, or null if the data is not virtual.
        /// </summary>
        IVirtualFrameList? GetDiagnosticVirtualList();

        #endregion 


        /// <summary>
        ///  Fires when the scale of the series changes, such as when XMin, XMax, YMin, or YMax is modified.
        /// </summary>
        event EventHandler? ScaleChanged;

        /// <summary>
        /// Fires when the axis definitions in Dimensions are changed, such as when DefineDimensions is called or when the structure of Dimensions is modified.
        /// </summary>
        event EventHandler? FrameAxisChanged;

        /// <summary>
        /// Fires when the active frame index changes, such as when ActiveIndex is modified. This allows subscribers to respond to changes in the current frame being accessed or displayed.
        /// </summary>
        event EventHandler? ActiveIndexChanged;

        /// <summary>
        /// Fires when the unit strings for either X or Y axis are changed, such as when XUnit or YUnit is modified.
        /// </summary>
        event EventHandler? UnitChanged;

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

        /// <summary>
        /// Gets the X-axis value at the specified index. As an implementation, this returns  XStep * ix + XMin;
        /// </summary>
        /// <param name="ix">The zero-based index of the X-axis value to retrieve.</param>
        /// <returns>The X-axis value corresponding to the specified index.</returns>
        double XValue(int ix);
        
        /// <summary>
        /// Returns the zero-based index of the specified x-coordinate value.
        /// </summary>
        /// <remarks>If <paramref name="extendRange"/> is set to <see langword="true"/>, the method
        /// returns the nearest valid index even if the exact value is not found. This can be useful for interpolation
        /// or extrapolation scenarios.</remarks>
        /// <param name="x">The x-coordinate value to locate in the collection.</param>
        /// <param name="extendRange">If <see langword="true"/>, allows the search to extend beyond the current range to include the nearest valid
        /// index. If <see langword="false"/>, throw IndexOutOfRangeException if the index is out of bounds..</param>
        /// <returns>The zero-based index of the specified x-coordinate if found; otherwise, -1 if the value is not present and
        /// range extension is not allowed.</returns>
        int XIndexOf(double x, bool extendRange = false);

        /// <summary>
        /// Gets the Y-axis value corresponding to the specified Y index. This returns YStep * iy + YMin;
        /// </summary>
        /// <param name="iy">The zero-based index along the Y axis for which to retrieve the value.</param>
        /// <returns>The Y-axis value at the specified index.</returns>
        double YValue(int iy);

        /// <summary>
        /// Returns the index of the row corresponding to the specified Y value.
        /// </summary>
        /// <param name="y">The Y value for which to find the corresponding row index.</param>
        /// <param name="extendRange">true to allow indices outside the normal range if y is out of bounds; 
        /// otherwise (false), throw IndexOutOfRangeException when y is out of bounds.</param>
        /// 
        /// <returns>The zero-based index of the row that corresponds to the specified Y value.</returns>
        int YIndexOf(double y, bool extendRange = false);

        /// <summary>
        /// Sets the minimum and maximum values for the X and Y axes of the coordinate system.
        /// </summary>
        /// <param name="xmin">The minimum value for the X axis.</param>
        /// <param name="xmax">The maximum value for the X axis.</param>
        /// <param name="ymin">The minimum value for the Y axis.</param>
        /// <param name="ymax">The maximum value for the Y axis.</param>
        void SetXYScale(double xmin, double xmax, double ymin, double ymax);

        /// <summary>
        /// Returns the current XY axis scale as a <see cref="Scale2D"/> value containing
        /// <c>XMin</c>, <c>XMax</c>, <c>YMin</c>, and <c>YMax</c>.
        /// </summary>
        Scale2D GetScale();

        /// <summary>
        /// Defines the hyperstack axes for multi-frame data.
        /// The product of all axis counts must equal <see cref="FrameCount"/>.
        /// </summary>
        /// <param name="axes">One or more <see cref="Axis"/> objects describing the dimension structure.</param>
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
        /// Returns the minimum and maximum values in the active frame.
        /// </summary>
        /// <returns>A <c>(Min, Max)</c> tuple. Equivalent to <c>GetValueRange(ActiveIndex)</c>.</returns>
        (double Min, double Max) GetValueRange();

        /// <summary>
        /// Returns the minimum and maximum values in the specified frame.
        /// </summary>
        /// <param name="frameIndex">The zero-based frame index.</param>
        /// <returns>A <c>(Min, Max)</c> tuple.</returns>
        (double Min, double Max) GetValueRange(int frameIndex);


        /// <summary>
        /// Returns the minimum and maximum values for the specified frame and value mode.
        /// </summary>
        /// <remarks>
        /// <paramref name="valueMode"/> selects a component from structured element types.
        /// For primitive types (e.g. <c>double</c>, <c>float</c>) use <c>valueMode = 0</c>.
        /// For <see cref="System.Numerics.Complex"/>, the indices are:
        /// 0 = Magnitude, 1 = Real, 2 = Imaginary, 3 = Phase, 4 = Power.
        /// </remarks>
        /// <param name="frameIndex">The zero-based frame index.</param>
        /// <param name="valueMode">The component index within the value type.</param>
        /// <returns>A <c>(Min, Max)</c> tuple, or <c>(NaN, NaN)</c> if the range is unavailable.</returns>
        (double Min, double Max) GetValueRange(int frameIndex, int valueMode);


        /// <summary>
        /// Returns all per-component min/max values for the specified frame as two parallel lists.
        /// </summary>
        /// <remarks>
        /// For primitive element types the lists each contain a single element.
        /// For <see cref="System.Numerics.Complex"/>, the five components are returned in order:
        /// Magnitude, Real, Imaginary, Phase, Power.
        /// Use <see cref="GetValueRange(int, int)"/> when only a single component is needed.
        /// </remarks>
        /// <param name="frameIndex">The zero-based frame index.</param>
        /// <returns>
        /// A tuple of two lists: <c>MinValues</c> and <c>MaxValues</c>.
        /// Both lists are empty (or contain <c>NaN</c>) if the range is not yet computed.
        /// </returns>
        (List<double> MinValues, List<double> MaxValues) GetValueRangeList(int frameIndex);


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
        /// Returns the minimum and maximum values along the specified axis for the given value mode,
        /// optionally fixing the other axis coordinates.
        /// </summary>
        /// <param name="targetAxis">The axis to scan.</param>
        /// <param name="fixedCoordinates">
        /// Full coordinate array for all axes. The entry at the <paramref name="targetAxis"/> position
        /// is ignored during iteration. Pass <c>null</c> to aggregate across all positions.
        /// </param>
        /// <param name="valueMode">The component index within the value type (see <see cref="GetValueRange(int, int)"/>).</param>
        /// <returns>A <c>(Min, Max)</c> tuple.</returns>
        (double Min, double Max) GetValueRange(Axis targetAxis, int[]? fixedCoordinates = null, int valueMode = 0);

        /// <summary>
        /// Returns the global minimum and maximum values across all frames.
        /// </summary>
        /// <remarks>
        /// For in-memory data this performs a full scan if any frame has not yet been computed.
        /// For virtual data, only already-cached frames are considered; use the
        /// <see cref="GetGlobalValueRange(out List{int}, bool)"/> overload with <c>forceRefresh = true</c>
        /// to guarantee a complete result.
        /// </remarks>
        /// <returns>A <c>(Min, Max)</c> tuple across all frames.</returns>
        (double Min, double Max) GetGlobalValueRange();


        /// <summary>
        /// Retrieves the overall minimum and maximum values across all frames, along with a list of frames that have not yet been calculated.
        /// </summary>
        /// <param name="invalids">
        /// When this method returns, contains a <see cref="List{Int32}"/> of indices for frames whose value ranges are currently invalid or uncomputed.
        /// <para>
        /// <strong>UI Implementation Note:</strong> If <paramref name="forceRefresh"/> is <c>false</c>, this list allows the UI to 
        /// display a partial progress state or trigger background computation for specific frames while maintaining high responsiveness.
        /// </para>
        /// </param>
        /// <param name="forceRefresh">
        /// If <c>true</c>, forces a full-pixel scan (<c>RefreshValueRange</c>) for all invalid frames before returning, 
        /// ensuring the result is absolute but potentially incurring significant O(N*M) time complexity.
        /// <br/>If <c>false</c>, returns the best available range based on cached data only, prioritized for real-time performance.
        /// </param>
        /// <returns>
        /// A tuple containing the global minimum and maximum values found. 
        /// Returns <c>(NaN, NaN)</c> if no valid data is available and <paramref name="forceRefresh"/> is <c>false</c>.
        /// </returns>
        (double Min, double Max) GetGlobalValueRange(out List<int> invalids, bool forceRefresh);

        /// <summary>
        /// Sets the element value at the specified pixel coordinates in the active frame.
        /// </summary>
        /// <param name="ix">The zero-based X index.</param>
        /// <param name="iy">The zero-based Y index.</param>
        /// <param name="v">The value to write, expressed as a <c>double</c>.</param>
        void SetValueAt(int ix, int iy, double v);


        /// <summary>
        /// Sets the element value at the specified pixel coordinates in the given frame.
        /// </summary>
        /// <param name="ix">The zero-based X index.</param>
        /// <param name="iy">The zero-based Y index.</param>
        /// <param name="frameIndex">The zero-based frame index.</param>
        /// <param name="v">The value to write, expressed as a <c>double</c>.</param>
        void SetValueAt(int ix, int iy, int frameIndex, double v);

        /// <summary>
        /// Returns the element value at the specified pixel coordinates as a <c>double</c>.
        /// </summary>
        /// <param name="ix">The zero-based X index.</param>
        /// <param name="iy">The zero-based Y index.</param>
        /// <param name="frameIndex">The zero-based frame index. Pass <c>-1</c> to use the current <see cref="ActiveIndex"/>.</param>
        /// <returns>The element value cast to <c>double</c>.</returns>
        double GetValueAt(int ix, int iy, int frameIndex = -1);

        /// <summary>
        /// Returns the value at the specified coordinates (x, y) as a double. 
        /// If frameIndex is -1, the value from the current active frame is returned. 
        /// If interpolate is true, the value is calculated using an interpolation algorithm based on the surrounding data points; otherwise, the value from the nearest data point is returned.
        /// </summary>
        /// <param name="x">The physical X coordinate.</param>
        /// <param name="y">The physical Y coordinate.</param>
        /// <param name="frameIndex">The zero-based frame index. Pass <c>-1</c> to use <see cref="ActiveIndex"/>.</param>
        /// <param name="interpolate">
        /// When <c>true</c>, bilinear interpolation is applied.
        /// When <c>false</c>, the value of the nearest data point is returned.
        /// </param>
        /// <returns>The element value at the specified coordinates, cast to <c>double</c>.</returns>
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
        /// Saves the matrix data to a file using the specified writer. 
        /// <para>Example: <c>data.Save("file.csv", new CsvFormat());</c></para>
        /// </summary>
        /// <param name="filePath">The destination file path.</param>
        /// <param name="writer">The format writer implementation (e.g., CsvFormat, MxBinaryFormat).</param>
        /// <example>
        /// <code>
        /// var csv = new CsvFormat { Separator = "," };
        /// matrix.Save("output.csv", csv);
        /// </code>
        /// </example>
        void SaveAs(string filePath, IMatrixDataWriter writer);


        /// <summary>
        /// Forces any modified (dirty) cached frames to be written to the underlying storage.
        /// This method does nothing if the data is entirely in-memory.
        /// </summary>
        /// <remarks>
        /// Use this to explicitly commit changes to persistent storage (e.g., memory-mapped files) 
        /// without waiting for the instance to be disposed, ensuring data safety.
        /// </remarks>
        void Flush();

        /// <summary>
        /// Applies the specified operation to the current <see cref="IMatrixData"/> and returns the processed result.
        /// </summary>
        /// <typeparam name="TResult">The type of the result produced by the operation. This can be a single <see cref="IMatrixData"/>, a tuple, or any other type defined by the operation.</typeparam>
        /// <remarks>
        /// This method serves as a generic dispatch entry point. 
        /// Concrete implementations (e.g., <see cref="MatrixData{T}"/>) resolve their underlying generic type <c>T</c> 
        /// and execute the appropriate logic based on the runtime type of the <paramref name="operation"/> 
        /// (such as <c>IVolumeOperation{TResult}</c>, <see cref="IMatrixDataOperation"/>, or filter operations).
        /// </remarks>
        /// <param name="operation">The operation to apply (e.g., Projection, Orthogonal Slicing, or Filter). Cannot be null.</param>
        /// <returns>The result of the applied operation, strongly typed as <typeparamref name="TResult"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="operation"/> is null.</exception>
        /// <exception cref="NotSupportedException">Thrown if the specific operation type is not supported by this data implementation.</exception>
        TResult Apply<TResult>(IOperation<TResult> operation);


    }

}

