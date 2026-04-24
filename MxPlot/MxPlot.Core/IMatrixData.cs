using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{

    public interface IMatrixData: ICloneable, IDisposable
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

        double XValue(int ix);
        
        int XIndexOf(double x, bool extendRange = false);

        double YValue(int iy);

        int YIndexOf(double y, bool extendRange = false);

        void SetXYScale(double xmin, double xmax, double ymin, double ymax);

        Scale2D GetScale();

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


        (double Min, double Max) GetValueRange(int frameIndex, int valueMode);


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


        (double Min, double Max) GetValueRange(Axis targetAxis, int[]? fixedCoordinates = null, int valueMode = 0);

        /// <summary>
        /// Get the min and max values found in all the frames as a tuple (min, max).
        /// </summary>
        /// <returns>double[]{min, max}</returns>
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

