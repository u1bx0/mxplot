using MxPlot.Core.IO;
using MxPlot.Core.Processing;
using MxPlot.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MxPlot.Core
{
    /// <summary>
    /// Implementaion of IMatrixData
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class MatrixData<T> : IMatrixData where T : unmanaged
    {

        /// <summary>
        /// Provider delegate for min and max arraies for the specified array
        /// </summary>
        /// <param name="array"></param>
        /// <returns></returns>
        public delegate (double[] minValues, double[] maxValues) MinMaxFinder(T[] array);

        /// <summary>
        /// Holds the currently registered default instance of the MinMaxFinder, or null if no default is set.
        /// </summary>
        private static MinMaxFinder? _registeredDefaultFinder;

        /// <summary>
        /// Initializes static members of the MatrixData class and registers the default MinMaxFinder for common types.
        /// </summary>
        static MatrixData()
        {
            _registeredDefaultFinder = CreateBuiltInMinMaxFinder();
        }

        /// <summary>
        /// Number of elements in x direction: immutable parameter for MatrixData instance
        /// </summary>
        protected readonly int _xcount;

        /// <summary>
        /// Number of elements in y direction: immutable parameter for MatrixData instance
        /// </summary>
        protected readonly int _ycount;

        private double _xmin;
        private double _xmax;
        private double _ymin;
        private double _ymax;

        private string _xunit = "";
        private string _yunit = "";

        /// <summary>
        /// Array list of actual data arrays: corresponds to frames
        /// </summary>
        private readonly List<T[]> _arrayList = [];

        /// <summary>
        /// Array holding maximum values for each frame
        /// </summary>
        private readonly List<double[]> _valueMaxArray = [[]];

        /// <summary>
        /// Array holding minimum values for each frame
        /// </summary>
        private readonly List<double[]> _valueMinArray = [[]];

        /// <summary>
        /// MinMaxFinder for this type. May be null for custom structs until registered.
        /// </summary>
        private MinMaxFinder? _minMaxFinder = _registeredDefaultFinder;

        /// <summary>
        /// Currently active frame index
        /// </summary>
        private int _activeIndex = 0;

        /// <summary>
        /// Delegate to convert T to double
        /// </summary>
        private static readonly Func<T, double> _toDouble = CreateConverter();

        /// <summary>
        /// Delegate to convert double to T
        /// </summary>
        private static readonly Func<double, T> _fromDouble = CreateReverseConverter();

        // Events
        public event EventHandler? ScaleChanged;
        public event EventHandler? FrameAxisChanged;
        public event EventHandler? ActiveIndexChanged;
        public event EventHandler? UnitChanged;

        // Properties

        public double XMax { get => _xmax; set { if (_xmax != value) { _xmax = value; UpdateScale(); } } }
        public double XMin { get => _xmin; set { if (_xmin != value) { _xmin = value; UpdateScale(); } } }
        public int XCount => _xcount;
        public double XStep { get; private set; }
        public double XRange { get; private set; }

        public double YMax { get => _ymax; set { if (_ymax != value) { _ymax = value; UpdateScale(); } } }
        public double YMin { get => _ymin; set { if (_ymin != value) { _ymin = value; UpdateScale(); } } }
        public int YCount => _ycount;
        public double YStep { get; private set; }
        public double YRange { get; private set; }

        public string XUnit { get => _xunit; set { if (_xunit != value) { _xunit = value; UnitChanged?.Invoke(this, EventArgs.Empty); } } }
        public string YUnit { get => _yunit; set { if (_yunit != value) { _yunit = value; UnitChanged?.Invoke(this, EventArgs.Empty); } } }

        public int FrameCount => _arrayList.Count;

        public int ActiveIndex
        {
            get => _activeIndex;
            set
            {
                if (_activeIndex != value)
                {
                    if (value < 0 || value >= FrameCount)
                        throw new IndexOutOfRangeException($"ActiveIndex out of range: {value}, FrameCount={FrameCount}");

                    _activeIndex = value;
                    ActiveIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public IDictionary<string, string> Metadata { get; } 
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public DimensionStructure Dimensions { get; private set; }

        public IReadOnlyList<Axis> Axes => Dimensions.Axes;

        public Type ValueType => typeof(T);

        /// <summary>
        /// Registers the specified MinMaxFinder instance as the default implementation to use for min/max operations.
        /// </summary>
        /// <remarks>Registering a new default MinMaxFinder will replace any previously registered
        /// instance. This method is typically called during application initialization to configure custom min/max
        /// logic.</remarks>
        /// <param name="finder">The MinMaxFinder instance to register as the default. Cannot be null.</param>
        public static void RegisterDefaultMinMaxFinder(MinMaxFinder finder)
        {
            _registeredDefaultFinder = finder;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixData&lt;T&gt;"/> class. Existing data array can be provided.
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="array"></param>
        public MatrixData(int xnum, int ynum, T[]? array = null)
        {
            CheckPlane(xnum, ynum);
            _xcount = xnum;
            _ycount = ynum;
            AllocateArray(1, array != null ? new List<T[]> { array } : null);
            SetXYScale(0, xnum - 1, 0, ynum - 1);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixData&lt;T&gt;"/> class with the specified number of frames.
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="frameCount"></param>
        public MatrixData(int xnum, int ynum, int frameCount)
        {
            CheckPlane(xnum, ynum);
            _xcount = xnum;
            _ycount = ynum;
            AllocateArray(frameCount);
            SetXYScale(0, xnum - 1, 0, ynum - 1);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixData&lt;T&gt;"/> class with existing data arrays.
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="arrayList"></param>
        public MatrixData(int xnum, int ynum, List<T[]> arrayList)
        {
            CheckPlane(xnum, ynum);
            _xcount = xnum;
            _ycount = ynum;
            int count = arrayList?.Count ?? 1;
            AllocateArray(count, arrayList);
            SetXYScale(0, xnum - 1, 0, ynum - 1);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance with existing data arrays and min/max specification for non-primitive types such as Complex
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="arrayList"></param>
        /// <param name="minValueList">Ignored if the number of element does not match FrameCount. </param>
        /// <param name="maxValueList">Ignored if the number of element does not match FrameCount.</param>
        public MatrixData(int xnum, int ynum, List<T[]> arrayList, List<double[]> minValueList, List<double[]> maxValueList)
        {
            CheckPlane(xnum, ynum);
            _xcount = xnum;
            _ycount = ynum;
            int count = arrayList?.Count ?? 1;
            AllocateArray(count, arrayList, minValueList, maxValueList);
            SetXYScale(0, xnum - 1, 0, ynum - 1);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance with existing data arrays and primitive min/max values.
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="arrayList"></param>
        /// <param name="primitiveMinValueList">Ignored if the number of element does not match FrameCount.</param>
        /// <param name="primitiveMaxValueList">Ignored if the numver of element does not match FrameCount.</param>
        public MatrixData(int xnum, int ynum, List<T[]> arrayList, List<double> primitiveMinValueList, List<double> primitiveMaxValueList)
        {
            CheckPlane(xnum, ynum);
            _xcount = xnum;
            _ycount = ynum;
            int count = arrayList?.Count ?? 1;
            // Convert List<double> (scalar) -> List<double[]> (array)
            // Treat as empty list if no values
            var minList = primitiveMinValueList?
                .Select(val => new double[] { val }) // Wrap double in double[1]
                .ToList() ?? [];                     // Empty list if null

            var maxList = primitiveMaxValueList?
                .Select(val => new double[] { val }) // Wrap double in double[1]
                .ToList() ?? [];                     // Empty list if null
            AllocateArray(count, arrayList, minList, maxList);

            SetXYScale(0, xnum - 1, 0, ynum - 1);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance with a single 2D frame
        /// </summary>
        /// <param name="scale"></param>
        public MatrixData(Scale2D scale)
        {
            CheckPlane(scale.XCount, scale.YCount);
            _xcount = scale.XCount;
            _ycount = scale.YCount;
            AllocateArray(1);
            SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance with the specified scale and axes.
        /// <code>
        /// Example:
        /// var md = new MatrixData&lt;float&gt;(
        ///        Scale2D.Centered(256, 256, 2, 2),
        ///        Axis.Channel(20),
        ///        Axis.Z(10, 0, 2),
        ///        Axis.Time(11, 0, 15)
        ///  );
        /// </code>
        /// </summary>
        /// <param name="scale"></param>
        /// <param name="axes"></param>
        /// <exception cref="ArgumentException"></exception>
        public MatrixData(Scale2D scale, params Axis[] axes)
        {
            CheckPlane(scale.XCount, scale.YCount);
            _xcount = scale.XCount;
            _ycount = scale.YCount;
            int frameCount = GetTotalFrameCount(axes);
            AllocateArray(frameCount);
            SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            int expectedCount = GetTotalFrameCount(axes);
            if (expectedCount != FrameCount)
                throw new ArgumentException($"Total axis count ({expectedCount}) does not match FrameCount ({FrameCount})");
            Dimensions = new DimensionStructure(this, axes);

        }

        // Type conversion methods
        private static Func<T, double> CreateConverter()
        {
            var t = typeof(T);

            if (t == typeof(double)) return v => (double)(object)v!;
            if (t == typeof(float)) return v => (float)(object)v!;
            if (t == typeof(int)) return v => (int)(object)v!;
            if (t == typeof(uint)) return v => (uint)(object)v!;
            if (t == typeof(long)) return v => (long)(object)v!;
            if (t == typeof(ulong)) return v => (ulong)(object)v!;
            if (t == typeof(short)) return v => (short)(object)v!;
            if (t == typeof(ushort)) return v => (ushort)(object)v!;
            if (t == typeof(byte)) return v => (byte)(object)v!;
            if (t == typeof(sbyte)) return v => (sbyte)(object)v!;
            if (t == typeof(decimal)) return v => (double)(decimal)(object)v!;
            if (t == typeof(Complex)) return v => ((Complex)(object)v!).Magnitude;

            // For custom structs that don't have a natural conversion to double,
            // return a dummy converter. Operations requiring double conversion
            // will throw NotSupportedException when actually used.
            return v => throw new NotSupportedException(
                $"Type {typeof(T)} does not support conversion to double. " +
                $"Operations requiring numeric conversion (interpolation, scaling) are not available for this type.");
        }

        private static Func<double, T> CreateReverseConverter()
        {
            var t = typeof(T);

            if (t == typeof(double)) return v => (T)(object)v!;
            if (t == typeof(float)) return v => (T)(object)(float)v!;
            if (t == typeof(int)) return v => (T)(object)(int)v!;
            if (t == typeof(uint)) return v => (T)(object)(uint)v!;
            if (t == typeof(long)) return v => (T)(object)(long)v!;
            if (t == typeof(ulong)) return v => (T)(object)(ulong)v!;
            if (t == typeof(short)) return v => (T)(object)(short)v!;
            if (t == typeof(ushort)) return v => (T)(object)(ushort)v!;
            if (t == typeof(byte)) return v => (T)(object)(byte)v!;
            if (t == typeof(sbyte)) return v => (T)(object)(sbyte)v!;
            if (t == typeof(decimal)) return v => (T)(object)(decimal)v!;
            if (t == typeof(Complex)) return v => (T)(object)new Complex(v!, 0);

            // For custom structs, return a dummy converter
            return v => throw new NotSupportedException(
                $"Type {typeof(T)} does not support conversion from double. " +
                $"Operations requiring numeric conversion (interpolation, scaling) are not available for this type.");
        }

        protected virtual double ToDoubleFrom(T v) => _toDouble(v);
        protected virtual T ToValueTypeFrom(double v) => _fromDouble(v);

        /// <summary>
        /// Check the dimension of the plane. If the size is invalid, throw ArgumentException.
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <exception cref="ArgumentException"></exception>
        private void CheckPlane(int xnum, int ynum)
        {
            if(xnum <= 0 || ynum <= 0)
                throw new ArgumentException($"Invalid plane size: xnum={xnum}, ynum={ynum}");
            long length = (long)xnum * (long)ynum;
            if(length > int.MaxValue)
                throw new ArgumentException($"Plane size too large: xnum={xnum}, ynum={ynum}, length={length}");
        }


        // Memory allocation
        private void AllocateArray(int frameCount, List<T[]>? buf = null, List<double[]>? minValueList = null, List<double[]>? maxValueList = null)
        {
            if (frameCount <= 0)
                throw new ArgumentException($"Frame count must be > 0: frameCount = {frameCount}");

            _arrayList.Clear(); //Ideally, no elements should exist.
            _valueMaxArray.Clear();
            _valueMinArray.Clear();
            _arrayList.Capacity = frameCount;
            _valueMaxArray.Capacity = frameCount;
            _valueMinArray.Capacity = frameCount;
            bool isMinMaxProvided = (minValueList != null && maxValueList != null) &&
                                    (minValueList.Count == frameCount) && (maxValueList.Count == frameCount);
            int length = _xcount * _ycount;
            if (buf is not null)
            {
                if (buf.Count != frameCount)
                    throw new ArgumentException($"Buffer count does not match frame count: buf.Count = {buf.Count}, frameCount = {frameCount}");
                
                for (int i = 0; i < frameCount; i++)
                {
                    if (buf[i] is not null && buf[i].Length != length)
                        throw new ArgumentException($"Invalid array size at index {i}: length = {buf[i].Length}, expected = {_xcount * _ycount}");
                    _arrayList.Add(buf[i]);
                    if (isMinMaxProvided)
                    {
                        _valueMinArray.Add((double[])minValueList![i].Clone());
                        _valueMaxArray.Add((double[])maxValueList![i].Clone());
                    }
                    else
                    {
                        _valueMaxArray.Add([]);
                        _valueMinArray.Add([]);
                        RefreshValueRange(i);
                    }
                }
            }
            else //buf is null
            {
                try
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        _arrayList.Add(new T[length]);
                        _valueMaxArray.Add([]);
                        _valueMinArray.Add([]);
                        //MinMav values will be calculated when needed
                        //RefreshValueRange(i);
                    }
                }
                catch (Exception e)
                {
                    //Unexpected situation: clear all allocated memory
                    //This object is not valid anymore.
                    int allocatedCount = _arrayList.Count;
                    long frameSize = (long)_xcount * _ycount * Unsafe.SizeOf<T>();

                    _arrayList.Clear();
                    _valueMaxArray.Clear();
                    _valueMinArray.Clear();
                    //possibly out of memory
                    Debug.WriteLine($"[AllocateArray] Exception");
                    // 2. 情報を付加して再スロー
                    if (e is OutOfMemoryException ex)
                    {
                        throw new OutOfMemoryException(
                            $"MatrixData allocation failed at frame {allocatedCount} of {frameCount}. " +
                            $"Total attempted memory: {(long)frameCount * frameSize / 1024 / 1024} MB. " +
                            $"The system may be out of memory.", ex);
                    }
                    else
                    {
                        throw;
                    }
                }
                
            }
        }

        /// <summary>
        /// Utility method to calculate total frame count from axes
        /// </summary>
        /// <param name="axes"></param>
        /// <returns></returns>
        private static int GetTotalFrameCount(params Axis[] axes)
        {
            if (axes == null || axes.Length == 0)
                return 1;

            int count = 1;
            foreach (var axis in axes)
                count *= axis.Count;
            return count;
        }


        /// <summary>
        /// Sets the minimum and maximum values for the X and Y axes of the coordinate system.
        /// </summary>
        /// <param name="xmin">The minimum value for the X axis.</param>
        /// <param name="xmax">The maximum value for the X axis.</param>
        /// <param name="ymin">The minimum value for the Y axis.</param>
        /// <param name="ymax">The maximum value for the Y axis.</param>

        public void SetXYScale(double xmin, double xmax, double ymin, double ymax)
        {
            _xmin = xmin;
            _xmax = xmax;
            _ymin = ymin;
            _ymax = ymax;
            UpdateScale();
        }

        private void UpdateScale()
        {
            XRange = _xmax - _xmin;
            YRange = _ymax - _ymin;
            XStep = XRange / (_xcount - 1);
            YStep = YRange / (_ycount - 1);
            ScaleChanged?.Invoke(this, EventArgs.Empty);
        }

        public Scale2D GetScale() => new Scale2D(_xcount, _xmin, _xmax, _ycount, _ymin, _ymax, _xunit, _yunit);

        /// <summary>
        /// Define dimension structure with specified axes. 
        /// <para><c>Example: md.DefineDimensions(Axis.Z(32,-5,5), Axis.Time(100, 0, 10));</c></para>
        /// </summary>
        /// <param name="axes"></param>
        /// <exception cref="ArgumentException"></exception>
        public void DefineDimensions(params Axis[] axes)
        {
            int expectedCount = GetTotalFrameCount(axes);
            if (expectedCount != FrameCount)
                throw new ArgumentException($"Total axis count ({expectedCount}) does not match FrameCount ({FrameCount})");

            Dimensions?.Dispose();
            Dimensions = new DimensionStructure(this, axes);

            FrameAxisChanged?.Invoke(this, EventArgs.Empty);
        }

        public Axis? this[string axisName] => Dimensions[axisName];

        /// <summary>
        /// Returns the minimum and maximum values for the currently active data set.
        /// </summary>
        /// <returns>A tuple containing the minimum and maximum values as doubles. The first item is the minimum value; the
        /// second item is the maximum value.</returns>
        public (double Min, double Max) GetMinMaxValues() => GetMinMaxValues(ActiveIndex, 0);

        /// <summary>
        /// Returns the minimum and maximum values for the specified frame with default value type.
        /// </summary>
        /// <param name="frameIndex">The zero-based index of the frame for which to retrieve the minimum and maximum values. Must be greater than
        /// or equal to 0 and less than the total number of frames.</param>
        /// <returns>A tuple containing the minimum and maximum values for the specified frame. The first item is the minimum
        /// value; the second item is the maximum value.</returns>
        public (double Min, double Max) GetMinMaxValues(int frameIndex) => GetMinMaxValues(frameIndex, 0);

        public (double Min, double Max) GetMinMaxValues(int frameIndex, int valueMode)
        {
           return GetMinMaxArrays(frameIndex) is (var minArr, var maxArr)
                ? (minArr[valueMode], maxArr[valueMode])
                : (double.NaN, double.NaN);
        }

        public (double[] MinArray, double[] MaxArray) GetMinMaxArrays(int frameIndex)
        {
            if (RefreshValueRangeRequired(frameIndex))
                RefreshValueRange(frameIndex);
            return (_valueMinArray[frameIndex], _valueMaxArray[frameIndex]);
        }

        /// <summary>
        /// Gets the minimum value associated with the current active index. The default value mode will be returned if the value type is not primitive.
        /// </summary>
        /// <returns>The minimum value for the currently active index.</returns>
        public double GetMinValue()
        {
            if (RefreshValueRangeRequired(ActiveIndex))
                RefreshValueRange(ActiveIndex);
            return _valueMinArray[ActiveIndex][0];
        }

        /// <summary>
        /// Returns the maximum value associated with the currently active index. The default value mode will be returned if the value type is not primitive.
        /// </summary>
        /// <returns>The maximum value for the active index as a double.</returns>
        public double GetMaxValue()
        {
            if (RefreshValueRangeRequired(ActiveIndex))
                RefreshValueRange(ActiveIndex);
            return _valueMaxArray[ActiveIndex][0];
        }


        /// <summary>
        /// Gets the minimum and maximum values along the specified axis, optionally fixing other axis coordinates.
        /// </summary>
        /// <param name="targetAxis">The axis along which to calculate min and max values.</param>
        /// <param name="fixedCoordinates">
        /// Optional array of coordinates for ALL axes. The array length must match
        /// the total number of axes (Dimensions.Axes.Count()). The value at the 
        /// targetAxis position will be overridden during iteration, so any value 
        /// can be specified for that position.
        /// If null, aggregates across all combinations of other axes.
        /// </param>
        /// <returns>A tuple containing the overall minimum and maximum values along the specified axis.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when targetAxis is not found in Dimensions, or when fixedCoordinates length 
        /// doesn't match the number of other axes.
        /// </exception>
        public (double Min, double Max) GetMinMaxValues(Axis targetAxis, int[]? fixedCoordinates = null)
        {
            return GetMinMaxValues(targetAxis, fixedCoordinates, 0);
        }

        /// <summary>
        /// Calculates the minimum and maximum values along the specified axis, optionally fixing other coordinates and
        /// selecting a value mode.
        /// </summary>
        /// <remarks>If only a single frame is present or there are no dimensions, the method returns the
        /// global minimum and maximum values. When fixed coordinates are provided, the method computes the min and max
        /// along the target axis while holding other axes at the specified positions.</remarks>
        /// <param name="targetAxis">The axis along which to compute the minimum and maximum values. Must exist in the current dimensions.</param>
        /// <param name="fixedCoordinates">An array of fixed coordinate indices for all axes except the target axis. If null, the method aggregates
        /// over all possible positions of the other axes.</param>
        /// <param name="valueMode">The value mode to use when retrieving minimum and maximum values. Typically corresponds to a specific data
        /// channel or value type. Must be a valid index for the value arrays.</param>
        /// <returns>A tuple containing the minimum and maximum values found along the specified axis, according to the provided
        /// coordinates and value mode.</returns>
        /// <exception cref="ArgumentException">Thrown if the specified target axis does not exist in the current dimensions.</exception>
        public (double Min, double Max) GetMinMaxValues(Axis targetAxis, int[]? fixedCoordinates = null, int valueMode = 0)
        {
            // Special case: single frame or no dimensions
            if (FrameCount == 1)
            {
                return (GetMinValue(), GetMaxValue());
            }

            // Validate targetAxis exists
            if (!Dimensions.Contains(targetAxis.Name))
                throw new ArgumentException($"Axis '{targetAxis.Name}' is not found in dimensions.", nameof(targetAxis));

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            if (fixedCoordinates == null)
            {
                // No fixed coordinates: iterate target axis and aggregate all other combinations
                // This is the simpler case - just iterate through targetAxis indices
                for (int i = 0; i < targetAxis.Count; i++)
                {
                    int frameIndex = Dimensions.GetFrameIndexFor(targetAxis, i);
                    
                    if (RefreshValueRangeRequired(frameIndex))
                        RefreshValueRange(frameIndex);

                    double vmin = _valueMinArray[frameIndex][valueMode];
                    double vmax = _valueMaxArray[frameIndex][valueMode];
                    
                    if (vmin < min) min = vmin;
                    if (vmax > max) max = vmax;
                }
            }
            else
            {
                // Fixed coordinates provided: validate and iterate with fixed positions
                int axisOrder = Dimensions.GetAxisOrder(targetAxis);

                // Build full position array, inserting targetAxis values at the correct position
                int[] position = (int[])fixedCoordinates.Clone();
                
                for (int i = 0; i < targetAxis.Count; i++)
                {
                    position[axisOrder] = i;

                    int frameIndex = Dimensions.GetFrameIndexFrom(position);
                    
                    if (RefreshValueRangeRequired(frameIndex))
                        RefreshValueRange(frameIndex);

                    double vmin = _valueMinArray[frameIndex][valueMode];
                    double vmax = _valueMaxArray[frameIndex][valueMode];
                    
                    if (vmin < min) min = vmin;
                    if (vmax > max) max = vmax;
                }
            }

            return (min, max);
        }

        /// <summary>
        /// Returns the minimum and maximum values across all data series.
        /// </summary>
        /// <returns>A tuple containing the minimum and maximum values found globally. The first item is the minimum value; the
        /// second item is the maximum value.</returns>
        public (double Min, double Max) GetGlobalMinMaxValues()
        {
            return GetGlobalMinMaxValues(0);
        }

        /// <summary>
        /// Calculates the global minimum and maximum values across all frames using the specified value mode.
        /// </summary>
        /// <param name="valueMode">An integer that specifies the value mode to use when determining the minimum and maximum values for each
        /// frame.</param>
        /// <returns>A tuple containing the minimum and maximum values found across all frames. The first element is the global
        /// minimum; the second element is the global maximum.</returns>
        /// <exception cref="InvalidOperationException">Thrown if there are no frames available to calculate the global minimum and maximum values.</exception>
        public (double Min, double Max) GetGlobalMinMaxValues(int valueMode)
        {
            if (FrameCount == 0) throw new InvalidOperationException("No frames available to calculate global min/max.");

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            for (int i = 0; i < FrameCount; i++)
            {
                var (vmin, vmax) = GetMinMaxValues(i, valueMode);
                if (vmin < min) min = vmin;
                if (vmax > max) max = vmax;
            }

            return (min, max);
        }

        public void RefreshValueRange() => RefreshValueRange(ActiveIndex);

        public void RefreshValueRange(int frameIndex)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            
            if (_minMaxFinder == null)
            {
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' has no MinMaxFinder registered. " +
                    $"Call 'MatrixData<{typeof(T).Name}>.RegisterDefaultMinMaxFinder(...)' first, " +
                    $"or avoid operations that require min/max statistics.");
            }

            var array = GetArray(frameIndex);
            var (minValues, maxValues) = _minMaxFinder(array);
            
            _valueMinArray[frameIndex] = minValues;
            _valueMaxArray[frameIndex] = maxValues;
        }


        private bool RefreshValueRangeRequired(int frameIndex)
        {
            // Check if the arrays exist and if primary dimension is invalid
            var minArr = _valueMinArray[frameIndex];
            var maxArr = _valueMaxArray[frameIndex];
            if(minArr == null || maxArr == null || minArr.Length == 0 || maxArr.Length == 0 || minArr.Length != maxArr.Length)
            {
                return true;
            }
            for (int i = 0; i < minArr.Length; i++)
            {
                if (minArr[i] > maxArr[i])
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Invalidates the cached min and max values for the specified frame index. 
        /// </summary>
        /// <param name="frameIndex"></param>
        private void Invalidate(int frameIndex)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;

            if (_valueMinArray[frameIndex].Length == 0) return; // Already invalid
            _valueMinArray[frameIndex] = [];
            _valueMaxArray[frameIndex] = [];
        }

        // Index/Coordinate conversion methods
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double XValue(int ix) => XStep * ix + XMin;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double YValue(int iy) => YStep * iy + YMin;

        public int XIndexOf(double x, bool extendRange = false)
        {
            int ix = (int)Math.Round((x - XMin) / XStep);

            // Range check
            if ((ix < 0 || ix >= YCount) && !extendRange)
            {
                // Throw exception if not allowed
                throw new IndexOutOfRangeException($"x={x}, ix={ix}, XCount={_xcount}");
            }

            return ix;
        }

        public int YIndexOf(double y, bool extendRange = false)
        {
            int iy = (int)Math.Round((y - YMin) / YStep);

            if ((iy < 0 || iy >= YCount) && !extendRange)
            {
                // Throw exception if not allowed
                throw new IndexOutOfRangeException($"y={y}, iy={iy}, YCount={_ycount}");
            }
            return iy;
        }

        // Value access methods
        public double GetValueAt(int ix, int iy, int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            return ToDoubleFrom(_arrayList[frameIndex][iy * _xcount + ix]);
        }

        public T GetValueAtTyped(int ix, int iy, int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            return _arrayList[frameIndex][iy * _xcount + ix];
        }

        public void SetValueAt(int ix, int iy, double v)
        {
            SetValueAt(ix, iy, _activeIndex, v);
        }

        /// <summary>
        /// Sets the value at the specified pixel position for a given frame index. The double value is converted to the appropriate type T before assignment.
        /// If the performance is critical, consider using SetValueAtTyped to avoid conversion overhead. Alternatively, GetArray() can be used for bulk operations.
        /// </summary>
        /// <remarks>If the specified coordinates are outside the valid range, the method does not modify
        /// any values. No exception is thrown in this case.</remarks>
        /// <param name="ix">The zero-based x-coordinate of the value to set. Must be within the valid range of x indices.</param>
        /// <param name="iy">The zero-based y-coordinate of the value to set. Must be within the valid range of y indices.</param>
        /// <param name="frameIndex">The index of the frame in which to set the value. If less than 0, the active frame index is used.</param>
        /// <param name="v">The value to assign at the specified coordinates and frame.</param>
        public void SetValueAt(int ix, int iy, int frameIndex, double v)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;

            if (ix < 0 || ix >= _xcount || iy < 0 || iy >= _ycount)
            {
                Debug.WriteLine($"[SetValueAt] IndexOutOfRange: ix={ix}/{_xcount}, iy={iy}/{_ycount}");
                return;
            }

            _arrayList[frameIndex][iy * _xcount + ix] = ToValueTypeFrom(v);
            Invalidate(frameIndex);
        }

        /// <summary>
        /// Sets the value at the specified (x, y) position and frame index.
        /// </summary>
        /// <remarks>If the specified coordinates are out of range, the method does not modify any values.
        /// The method will use the active frame index if a negative frame index is provided.</remarks>
        /// <param name="ix">The zero-based x-coordinate of the position to set. Must be within the valid range of x-coordinates.</param>
        /// <param name="iy">The zero-based y-coordinate of the position to set. Must be within the valid range of y-coordinates.</param>
        /// <param name="frameIndex">The index of the frame in which to set the value. If less than zero, the active frame index is used.</param>
        /// <param name="value">The value to assign at the specified position and frame.</param>
        public void SetValueAtTyped(int ix, int iy, int frameIndex, T value)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;

            if (ix < 0 || ix >= _xcount || iy < 0 || iy >= _ycount)
            {
                Debug.WriteLine($"[SetValueAt] IndexOutOfRange: ix={ix}/{_xcount}, iy={iy}/{_ycount}");
                return;
            }

            _arrayList[frameIndex][iy * _xcount + ix] = value;
            Invalidate(frameIndex);
        }

        /// <summary>
        /// Gets the internal array for the specified frame. If no frame index is provided, the active frame's array is returned.
        /// After modifying the returned array, call RefreshValueRange(frameIndex) to update min/max statistics.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] GetArray(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            return _arrayList[frameIndex];
        }

        /// <summary>
        /// Set the array for the specified frame. If the instance of the provied srcArray is different from the internal array, the data will be copied.
        /// </summary>
        /// <param name="srcArray"></param>
        /// <param name="frameIndex"></param>
        /// <param name="minValues"></param>
        /// <param name="maxValues"></param>
        /// <exception cref="ArgumentException"></exception>
        public void SetArray(T[] srcArray, int frameIndex = -1, double[]? minValues = null, double[]? maxValues = null)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;

            var array = _arrayList[frameIndex];
            if (srcArray.Length != array.Length)
                throw new ArgumentException($"Invalid array length: {srcArray.Length}, expected: {array.Length}");

            if (srcArray != array)
                srcArray.AsSpan().CopyTo(array);

            if (minValues != null && maxValues != null)
            {
                _valueMinArray[frameIndex] = minValues;
                _valueMaxArray[frameIndex] = maxValues;
            }
            else
            {
                RefreshValueRange(frameIndex);
            }
        }

        /// <summary>
        /// Accessor to get or set the value at the specified (ix, iy) position for the active frame. 
        /// Note1: This indexer uses (x, y) coordinates, not the standard matrix (row, column) notation.
        /// Note2: Setting a value invalidates the cached min/max statistics for the active frame.
        /// </summary>
        /// <remarks>If the specified coordinates are out of range, an IndexOutOfRangeException will be thrown.</remarks>
        /// <param name="ix">The x-coordinate (column index)</param>
        /// <param name="iy">The y-coordinate (row index)</param>
        /// <returns></returns>
        public T this[int ix, int iy]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _arrayList[_activeIndex][iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                _arrayList[_activeIndex][iy * _xcount + ix] = value;
                Invalidate(_activeIndex);
            }
        }

        /// <summary>
        /// Zero-copy conversion to byte array for serialization
        /// </summary>
        public unsafe ReadOnlySpan<byte> GetRawBytes(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            var array = _arrayList[frameIndex];
            return MemoryMarshal.AsBytes(array.AsSpan());
        }

        /// <summary>
        /// Zero-copy setting from byte array for deserialization. Min and max values are recalculated after setting.
        /// </summary>
        /// <param name="bytes"></param>
        /// <param name="frameIndex"></param>
        /// <exception cref="ArgumentException"></exception>
        public unsafe void SetFromRawBytes(ReadOnlySpan<byte> bytes, int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            var array = _arrayList[frameIndex];
            var targetSpan = MemoryMarshal.AsBytes(array.AsSpan());

            if (bytes.Length != targetSpan.Length)
                throw new ArgumentException($"Byte length mismatch: {bytes.Length} != {targetSpan.Length}");

            bytes.CopyTo(targetSpan);
            RefreshValueRange(frameIndex);
        }


        /// <summary>
        /// Sets values for the active frame using a lambda function. Min and max values are recalculated after setting.
        /// [IMPORTANT] A simple two-dimension loop is used to iterate over each (ix, iy) coordinate.
        /// </summary>
        /// <param name="func"></param>
        public void Set(Func<int, int, double, double, T> func)
        {
            Set(ActiveIndex, func);
        }


        // Utility methods

        /// <summary>
        ///  Sets values for the specified frame using a lambda function. Min and max values are recalculated after setting.
        /// Optimized with Span<typeparamref name="T"/> for better performance. 
        /// [IMPORTANT] A simple two-dimension loop is used to iterate over each (ix, iy) coordinate.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <param name="func"></param>
        public void Set(int frameIndex, Func<int, int, double, double, T> func)
        {
            var array = GetArray(frameIndex);
            var arraySpan = array.AsSpan();

            for (int iy = 0; iy < _ycount; iy++)
            {
                double y = YValue(iy);
                int rowStart = iy * _xcount;

                // Process row using Span for better cache locality
                for (int ix = 0; ix < _xcount; ix++)
                {
                    double x = XValue(ix);
                    arraySpan[rowStart + ix] = func(ix, iy, x, y);
                }
            }
            // Recalculate statistics using MinMaxFinder
            RefreshValueRange(frameIndex);
        }

        /// <summary>
        /// Invokes the specified action for each frame, providing the frame index and its associated array. 
        /// The min and max values are updated after processing each frame.
        /// </summary>
        /// <remarks>If parallel execution is enabled and there are two or more frames, the action is
        /// invoked concurrently for each frame, which may improve performance for large data sets. The action should be
        /// thread-safe if parallel execution is used.
        /// </remarks>
        /// <param name="action">The action to perform on each frame. The first parameter is the zero-based frame index; the second parameter
        /// is the array associated with that frame. Cannot be null.</param>
        /// <param name="useParallel">true to execute the action for each frame in parallel; otherwise, false. If there are fewer than two frames,
        /// parallel execution is not used regardless of this value.</param>
        public void ForEach(Action<int, T[]>action, bool useParallel = true)
        {
            if(FrameCount < 2)
            {
                useParallel = false;
            }
            if (useParallel)
            {
                Parallel.For(0, FrameCount, frameIndex =>
                {
                    action(frameIndex, GetArray(frameIndex));
                    RefreshValueRange(frameIndex);
                });
            }
            else
            {
                for(int i = 0; i < FrameCount; i++)
                {
                    action(i, GetArray(i));
                    RefreshValueRange(i);
                }
            }
        }

        /// <summary>
        /// Provides a high-performance and efficient accessor to manipulate the volume data along a specified axis.
        /// </summary>
        /// <remarks>Use this method to obtain an accessor for cross-sections, projections, or other general processing along the x, y, and z axes of the volume.
        /// For multi-dimensional data, volume access is done after extracting a MatrixData with a single axis.</remarks>
        ///  <param name="axisName"></param>
        ///  <param name="baseIndeces">If not provided, the volume containing the plane with ActiveIndex is extracted</param>
        /// <returns>A <see cref="VolumeAccessor{T}"/> instance that enables one-dimensional volume access to the data.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the dimensions contain more than one axis. This method supports only one-dimensional data.</exception>
        public VolumeAccessor<T> AsVolume(string axisName = "", int[]? baseIndeces = null)
        {
            if(string.IsNullOrEmpty(axisName))
            {
                if (Dimensions.AxisCount == 1)
                    return new VolumeAccessor<T>(_arrayList, GetScale(), Dimensions[0]);
                else
                    throw new InvalidOperationException($"Axis must be specified to create VolumeAccessor for muti-axis data");
            }
            else
            {
                //Extract specified axis by name
                if(baseIndeces == null)
                {
                    baseIndeces = Dimensions.GetAxisIndices();
                }
                return this.ExtractAlong(axisName, baseIndeces, false).AsVolume();
            }
        }

        /// <summary>
        /// Retrieves the value at the specified (x, y) coordinate.
        /// By default, uses nearest-neighbor method to return the value of the closest grid point.
        /// When interpolation is enabled, uses bilinear interpolation to calculate a smooth interpolated value.
        /// </summary>
        /// <param name="x">The X coordinate value (in physical coordinate system).</param>
        /// <param name="y">The Y coordinate value (in physical coordinate system).</param>
        /// <param name="frameIndex">The target frame index. If -1, uses the current ActiveIndex.</param>
        /// <param name="isInterpolationEnabled">
        /// Specifies whether interpolation is enabled.
        /// <list type="bullet">
        /// <item><description>false (default): Uses nearest-neighbor method, returning the value of the closest grid point.</description></item>
        /// <item><description>true: Uses bilinear interpolation, returning an interpolated value from the surrounding 4 points.</description></item>
        /// </list>
        /// </param>
        /// <returns>
        /// The value corresponding to the specified coordinate.
        /// When interpolation is disabled, returns the value of the nearest grid point.
        /// When interpolation is enabled, returns the interpolated value.
        /// For Complex types, the real and imaginary parts are interpolated independently.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This method converts physical coordinates (XMin~XMax, YMin~YMax) to grid indices to retrieve values.
        /// </para>
        /// <para>
        /// When interpolation is enabled, coordinates outside the valid range are automatically clamped within bounds (0~XCount-1, 0~YCount-1).
        /// When interpolation is disabled and coordinates outside the valid range are specified, an <see cref="IndexOutOfRangeException"/> is thrown.
        /// </para>
        /// <para>
        /// Bilinear interpolation calculates a distance-weighted average from the four surrounding grid points
        /// (bottom-left, bottom-right, top-left, top-right) around the specified coordinate.
        /// </para>
        /// <para>
        /// For Complex types, the real (Real) and imaginary (Imaginary) parts are interpolated independently,
        /// so phase information is not preserved. If phase-aware interpolation is required, a separate implementation is necessary.
        /// </para>
        /// </remarks>
        /// <exception cref="IndexOutOfRangeException">
        /// Thrown when interpolation is disabled (isInterpolationEnabled=false) and the specified coordinate
        /// is outside the valid range (XMin~XMax, YMin~YMax).
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the internal array is null (normally should not occur).
        /// </exception>
        /// <example>
        /// <code>
        /// var matrix = new MatrixData&lt;double&gt;(100, 100);
        /// matrix.SetXYScale(0, 10, 0, 10);
        /// 
        /// // Get value using nearest-neighbor
        /// double value1 = matrix.GetValue(5.3, 7.8);
        /// 
        /// // Get smoothly interpolated value using bilinear interpolation
        /// double value2 = matrix.GetValue(5.3, 7.8, isInterpolationEnabled: true);
        /// 
        /// // Get value from a specific frame
        /// double value3 = matrix.GetValue(5.3, 7.8, frameIndex: 2, isInterpolationEnabled: true);
        /// </code>
        /// </example>
        public T GetValue(double x, double y, int frameIndex = -1, bool isInterpolationEnabled = false)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;

            // Without interpolation: nearest neighbor
            if (!isInterpolationEnabled)
            {
                int ix = XIndexOf(x, false);
                int iy = YIndexOf(y, false);
                return _arrayList[frameIndex][iy * _xcount + ix];
            }

            // With interpolation: bilinear interpolation
            T[] array = _arrayList[frameIndex];
            if (array == null)
                throw new InvalidOperationException("Internal array is null");

            // Calculate index (allowing out of range)
            double iix = (x - _xmin) / XRange * (_xcount - 1);
            double iiy = (y - _ymin) / YRange * (_ycount - 1);

            // Clamp within range
            iix = Math.Clamp(iix, 0.0, _xcount - 1.0);
            iiy = Math.Clamp(iiy, 0.0, _ycount - 1.0);

            int ix0 = (int)iix;
            int iy0 = (int)iiy;
            int ix1 = (ix0 < _xcount - 1) ? ix0 + 1 : ix0;
            int iy1 = (iy0 < _ycount - 1) ? iy0 + 1 : iy0;

            double dx = iix - ix0;
            double dy = iiy - iy0;

            // For Complex type, interpolate real and imaginary parts separately
            if (typeof(T) == typeof(Complex))
            {
                var v00 = (Complex)(object)array[iy0 * _xcount + ix0]!;
                var v10 = (Complex)(object)array[iy0 * _xcount + ix1]!;
                var v01 = (Complex)(object)array[iy1 * _xcount + ix0]!;
                var v11 = (Complex)(object)array[iy1 * _xcount + ix1]!;

                double vr = v00.Real * (1 - dx) * (1 - dy)
                          + v10.Real * dx * (1 - dy)
                          + v01.Real * (1 - dx) * dy
                          + v11.Real * dx * dy;

                double vi = v00.Imaginary * (1 - dx) * (1 - dy)
                          + v10.Imaginary * dx * (1 - dy)
                          + v01.Imaginary * (1 - dx) * dy
                          + v11.Imaginary * dx * dy;

                return (T)(object)new Complex(vr, vi);
            }
            else
            {
                // For standard numeric types
                double v00 = _toDouble(array[iy0 * _xcount + ix0]);
                double v10 = _toDouble(array[iy0 * _xcount + ix1]);
                double v01 = _toDouble(array[iy1 * _xcount + ix0]);
                double v11 = _toDouble(array[iy1 * _xcount + ix1]);

                double v = v00 * (1 - dx) * (1 - dy)
                         + v10 * dx * (1 - dy)
                         + v01 * (1 - dx) * dy
                         + v11 * dx * dy;

                return _fromDouble(v);
            }
        }

        /// <summary>
        /// Set the value at the point close to the specified (x, y) coordinate.
        /// </summary>
        /// <param name="x">X coordinate</param>
        /// <param name="y">Y coordinate</param>
        /// <param name="value">Value to set</param>
        /// <param name="frameIndex">ActiveIndex if -1</param>
        public void SetValue(double x, double y, int frameIndex, T value)
        {
            int ix = XIndexOf(x, false);
            int iy = YIndexOf(y, false);
            SetValueAtTyped(ix, iy, frameIndex, value);
        }


        public object Clone()
        {
            var clone = new MatrixData<T>(_xcount, _ycount, FrameCount);
            clone.SetXYScale(_xmin, _xmax, _ymin, _ymax);
            clone.XUnit = XUnit;
            clone.YUnit = YUnit;
            clone.DefineDimensions(Axis.CreateFrom(Dimensions.Axes.ToArray()));
            foreach (var key in Metadata.Keys)
            {
                clone.Metadata[key] = Metadata[key];
            }

            for (int i = 0; i < FrameCount; i++)
            {
                var minValues = (double[])_valueMinArray[i].Clone();
                var maxValues = (double[])_valueMaxArray[i].Clone();
                var srcArray = GetArray(i);
                var dstArray = new T[srcArray.Length];
                srcArray.AsSpan().CopyTo(dstArray);
                clone.SetArray(dstArray, i, minValues, maxValues);
            }

            return clone;
        }

        public override string ToString()
        {
            return $"MatrixData<{typeof(T).Name}>: XCount={XCount}, YCount={YCount}, FrameCount={FrameCount}";
        }

        #region I/O methods using IMatrixDataIO (IMatrixDataReader/IMatrixDataWriter)
        /// <summary>
        /// Loads matrix data from a file with a specified reader.
        /// <para>Example: <c>var data = MatrixData.Load("file.mxd", new MxBinaryFormat());</c></para>
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="reader"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        public static MatrixData<T> Load(string filePath, IMatrixDataReader reader) 
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return reader.Read<T>(filePath);
        }


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
        public void Save(string filePath, IMatrixDataWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            writer.Write(filePath, this);
        }
        #endregion

        /// <summary>
        /// Sets the custom implementation used to determine minimum and maximum values.
        /// </summary>
        /// <remarks>Use this method to override the default min/max finding behavior with a custom
        /// implementation. This can be useful for specialized comparison logic or domain-specific
        /// requirements.</remarks>
        /// <param name="customFinder">The MinMaxFinder instance that provides custom logic for finding minimum and maximum values. Cannot be null.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="customFinder"/> is null.</exception>
        public void SetMinMaxFinder(MinMaxFinder customFinder)
        {
            if (customFinder == null) throw new ArgumentNullException(nameof(customFinder));
            _minMaxFinder = customFinder;
        }

        private static MinMaxFinder? CreateBuiltInMinMaxFinder()
        {
            switch (default(T))
            {
                case byte _:
                    return array =>
                    {
                        (byte min, byte max) = FastMinMaxFinder.FindInteger<byte>(array as byte[]);
                        return ([min], [max]);
                    };
                case int _:
                    return array =>
                    {
                        (int min, int max) = FastMinMaxFinder.FindInteger<int>(array as int[]);
                        return ([min], [max]);
                    };
                case uint _:
                    return array =>
                    {
                        (uint min, uint max) = FastMinMaxFinder.FindInteger<uint>(array as uint[]);
                        return ([min], [max]);
                    };
                case long _:
                    return array =>
                    {
                        (long min, long max) = FastMinMaxFinder.FindInteger<long>(array as long[]);
                        return ([min], [max]);
                    };
                case ulong _:
                    return array =>
                    {
                        (ulong min, ulong max) = FastMinMaxFinder.FindInteger<ulong>(array as ulong[]);
                        return ([min], [max]);
                    };
                case short _:
                    return array =>
                    {
                        (short min, short max) = FastMinMaxFinder.FindInteger<short>(array as short[]);
                        return ([min], [max]);
                    };
                case ushort _:
                    return array =>
                    {
                        (ushort min, ushort max) = FastMinMaxFinder.FindInteger<ushort>(array as ushort[]);
                        return ([min], [max]);
                    };
                case sbyte _:
                    return array =>
                    {
                        (sbyte min, sbyte max) = FastMinMaxFinder.FindInteger<sbyte>(array as sbyte[]);
                        return ([min], [max]);
                    };
                case float _:
                    return array =>
                    {
                        (float min, float max) = FastMinMaxFinder.FindFloatingPoint<float>(array as float[]);
                        return ([min], [max]);
                    };
                case double _:
                    return array =>
                    {
                        (double min, double max) = FastMinMaxFinder.FindFloatingPoint<double>(array as double[]);
                        return ([min], [max]);
                    };
                case Complex _:
                    return array =>
                    {
                        // Magnitude, Real, Imaginary, Phase, Power(=Magnitude^2)
                        (double[] min, double[] max) = FastMinMaxFinder.FindComplex((array as Complex[])!);
                        return (min, max);
                    };
                default:
                    return null;
            }
        }
    }



    public static class MatrixData
    {
        /// <summary>
        /// Internally calls Clone() method
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="target"></param>
        /// <returns></returns>
        public static T Duplicate<T>(this T target)
            where T : IMatrixData
        {
            return (T)target.Clone();
        }

        /// <summary>
        /// Loads matrix data from a file using the specified reader and returns it as an IMatrixData.
        /// </summary>
        /// <remarks>
        /// Use this method when the underlying data type is unknown at compile time. 
        /// The reader will determine the appropriate numeric type (e.g., double, int).
        /// </remarks>
        /// <param name="filePath">The source file path.</param>
        /// <param name="reader">The format reader implementation.</param>
        public static IMatrixData Load(string filePath, IMatrixDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return reader.Read(filePath);
        }

        public static  (double Min, double Max) GetMinMaxValues(this MatrixData<Complex> src, int frameIndex, ComplexValueMode mode)
        {
            return src.GetMinMaxValues(frameIndex, (int)mode);
        }

        /// <summary>
        /// Create a new MatrixData instance with the same size, scale settings, and Metadata as the source
        /// </summary>
        /// <param name="src"></param>
        /// <returns></returns>
        public static IMatrixData CreateNewInstanceFrom(IMatrixData src)
        {
            var dst = CreateNewInstance(src.ValueType, src.XCount, src.YCount, src.FrameCount);
            dst.SetXYScale(src.XMin, src.XMax, src.YMin, src.YMax);
            dst.DefineDimensions(Axis.CreateFrom(src.Dimensions.Axes.ToArray()));
            
            return dst;
        }

        /// <summary>
        /// MatrixData<typeparamref name="T"/>型を動的に生成する
        /// </summary>
        /// <param name="type"></param>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <returns></returns>
        public static IMatrixData CreateNewInstance(Type type, int xnum, int ynum) => CreateNewInstance(type, xnum, ynum, 1);

        /// <summary>
        /// MatrixData<typeparamref name="T"/>型のSeriesを動的に生成する
        /// </summary>
        /// <param name="type"></param>
        /// <param name="xNum"></param>
        /// <param name="yNum"></param>
        /// <param name="seriesNum"></param>
        /// <returns></returns>
        public static IMatrixData CreateNewInstance(Type type, int xNum, int yNum, int seriesNum)
        {
            var genericType = typeof(MatrixData<>);
            var constructedType = genericType.MakeGenericType(type);
            var ret = Activator.CreateInstance(constructedType, xNum, yNum, seriesNum) as IMatrixData;
            if(ret is null)
                throw new InvalidOperationException("Failed to create MatrixData instance of type: " + type);

            return ret!;
        }
       
    }

}
