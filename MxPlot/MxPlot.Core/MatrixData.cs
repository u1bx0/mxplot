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
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MxPlot.Core
{
    /// <summary>
    /// Implementaion of IMatrixData
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class MatrixData<T> : IMatrixData where T : unmanaged
    {

        /// <summary>
        /// A stateful container that manages statistical cache via shared list instances.
        /// </summary>
        /// <remarks>
        /// The essential point of this class is that it holds references to <see cref="List{Double}"/> 
        /// instances rather than cloning their values. When multiple <see cref="ValueRange"/> 
        /// instances share the same list references, calling <see cref="Invalidate"/> (which clears the lists) 
        /// synchronizes the invalidation state across all associated objects.
        /// </remarks>
        internal class ValueRange
        {
            /// <summary> Gets the list of minimum values for each value mode. </summary>
            public List<double> MinValues { get; } = [];

            /// <summary> Gets the list of maximum values for each value mode. </summary>
            public List<double> MaxValues { get; } = [];

            /// <summary>
            /// Gets a value indicating whether the current statistics are valid (calculated).
            /// </summary>
            public bool IsValid => MinValues.Count > 0;

            /// <summary>
            /// Clears the cached statistics and marks the state as invalid (<see cref="IsValid"/> = false).
            /// </summary>
            public void Invalidate() {MinValues.Clear(); MaxValues.Clear(); }   
            public ValueRange() { }

            /// <summary>
            /// Initializes a new instance of the <see cref="ValueRange"/> class 
            /// by wrapping existing list instances to enable shared synchronization.
            /// </summary>
            /// <param name="minValues">The shared list instance for minimum values.</param>
            /// <param name="maxValues">The shared list instance for maximum values.</param>
            public ValueRange(List<double> minValues, List<double> maxValues)
            {
                // Critical Logic: Capture the references of the lists to maintain 
                // synchronization across different ValueRange containers.
                MinValues = minValues;
                MaxValues = maxValues;
            }

            public void Set(IEnumerable<double> minValues, IEnumerable<double> maxValues)
            {
                MinValues.Clear();
                MaxValues.Clear();
                using (var minEnum = minValues.GetEnumerator())
                using (var maxEnum = maxValues.GetEnumerator())
                {
                    while (true)
                    {
                        bool hasMin = minEnum.MoveNext();
                        bool hasMax = maxEnum.MoveNext();
                        if (hasMin != hasMax)
                        {
                            throw new ArgumentException("Enumerables must have the same number of elements.");
                        }
                        if (!hasMin) break; // No elements anymore
                        MinValues.Add(minEnum.Current);
                        MaxValues.Add(maxEnum.Current);
                    }
                }
            }
        }

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
        /// Indicates whether the type parameter T is a supported primitive type within the MatrixData framework.
        /// </summary>
        /// <remarks>This field is used to determine if operations involving the type T can be performed,
        /// based on its inclusion in the supported primitive types list.</remarks>
        private static readonly bool _isSupportedPrimitive =
                MatrixData.SupportedPrimitiveTypes.Contains(typeof(T));

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
        //private readonly List<double[]> _valueMaxArray = [[]];

        /// <summary>
        /// Array holding minimum values for each frame
        /// </summary>
        //private readonly List<double[]> _valueMinArray = [[]];

        /// <summary>
        /// A map that associates an array reference with its corresponding <see cref="ValueRange"/> statistics.
        /// </summary>
        private readonly Dictionary<T[], ValueRange> _valueRangeMap = new Dictionary<T[], ValueRange>(); 

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
        /// Initializes a new single-frame instance of the <see cref="MatrixData&lt;T&gt;"/> class. Existing data array can be provided (shallow copy).
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
        /// Initializes a new instance of the <see cref="MatrixData&lt;T&gt;"/> class with existing data arrays (shallow copy).
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
        /// Initializes a new instance by injecting existing data arrays and pre-defined min/max containers.
        /// </summary>
        /// <remarks>
        /// <para><strong>IMPORTANT:</strong></para>
        /// This constructor is the core mechanism for establishing <b>Shared Reference Synchronization</b>. 
        /// Unlike standard constructors, this method captures the exact instances of the inner List&lt;double&gt;  
        /// within <paramref name="minValueList"/> and <paramref name="maxValueList"/>.
        /// <para/>
        /// By passing the same list instances to multiple <see cref="MatrixData"/> objects, the internal frams (T[]) and its min/max statistics become 
        /// "linked." Any modification or invalidation in one object will automatically propagate to all 
        /// others because they share the same underlying statistical "Source of Truth." 
        /// Use this constructor when performing shallow copies, reordering, or view transformations 
        /// where data integrity must be maintained across all instances.
        /// </remarks>
        /// <param name="xnum">The number of columns in the matrix.</param>
        /// <param name="ynum">The number of rows in the matrix.</param>
        /// <param name="arrayList">The list of raw data arrays to be managed.</param>
        /// <param name="minValueList">
        /// A nested list of minimum values. The inner <see cref="List{Double}"/> instances are 
        /// captured by reference to enable cross-instance cache synchronization. 
        /// Ignored if the count does not match the frame count. The order of min/max lists should correspond to the order of arrays in <paramref name="arrayList"/>.
        /// </param>
        /// <param name="maxValueList">
        /// A nested list of maximum values. Similar to <paramref name="minValueList"/>, 
        /// the inner lists are shared by reference. 
        /// Ignored if the count does not match the frame count. The order of min/max lists should correspond to the order of arrays in <paramref name="arrayList"/>.
        /// </param>
        public MatrixData(int xnum, int ynum, List<T[]> arrayList, List<List<double>> minValueList, List<List<double>> maxValueList)
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
        /// Initializes a new instance using existing data arrays and initial primitive min/max values.
        /// </summary>
        /// <remarks>
        /// <para><strong>NOTE ON SYNCHRONIZATION:</strong></para>
        /// Unlike the <c>List&lt;List&lt;double&gt;&gt;</c> constructor, this version <b>DOES NOT</b> establish 
        /// a shared statistical link. Each primitive value is wrapped in a <b>new</b> inner list instance. 
        /// While the physical data arrays (<typeparamref name="T"/>[]) are shared by reference, 
        /// the min/max cache is unique to this instance. 
        /// <br/>
        /// To maintain a fully synchronized link (where invalidation propagates across instances), 
        /// use the constructor that accepts <c>List&lt;List&lt;double&gt;&gt;</c>.
        /// </remarks>
        /// <param name="xnum">The number of columns.</param>
        /// <param name="ynum">The number of rows.</param>
        /// <param name="arrayList">The list of data arrays (shared by reference).</param>
        /// <param name="primitiveMinValueList">Initial minimum values. New cache containers will be created from these values.</param>
        /// <param name="primitiveMaxValueList">Initial maximum values. New cache containers will be created from these values.</param>
        public MatrixData(int xnum, int ynum, List<T[]> arrayList, List<double> primitiveMinValueList, List<double> primitiveMaxValueList)
        {
            CheckPlane(xnum, ynum);
            _xcount = xnum;
            _ycount = ynum;
            int count = arrayList?.Count ?? 1;
            // Convert List<double> (scalar) -> List<List<double>> (array)
            // Treat as empty list if no values
            var minList = primitiveMinValueList?
                .Select(val => new List<double> { val }).ToList() ?? [];

            var maxList = primitiveMaxValueList?
                .Select(val => new List<double> { val }).ToList() ?? [];

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
        private void AllocateArray(int frameCount, List<T[]>? buf = null, List<List<double>>? minValueList = null, List<List<double>>? maxValueList = null)
        {
            if (frameCount <= 0)
                throw new ArgumentException($"Frame count must be > 0: frameCount = {frameCount}");

            _arrayList.Clear(); //Ideally, no elements should exist.
            _arrayList.Capacity = frameCount;
            
            bool isMinMaxProvided = (minValueList != null && maxValueList != null) &&
                                    (minValueList.Count == frameCount) && (maxValueList.Count == frameCount);
            int length = _xcount * _ycount;
            try
            {
                if (buf is not null)
                {
                    if (buf.Count != frameCount)
                        throw new ArgumentException($"Buffer count mismatch: buf={buf.Count}, expected={frameCount}");

                    for (int i = 0; i < frameCount; i++)
                    {
                        var currentArray = buf[i];

                        if (currentArray is null || currentArray.Length != length)
                            throw new ArgumentException($"Invalid array at index {i}.");

                        _arrayList.Add(currentArray);

                        if (!_valueRangeMap.ContainsKey(currentArray))  //Key of currentArray does not exist in the map
                        {
                            if (isMinMaxProvided)
                            {
                                if (minValueList![i] == null || maxValueList![i] == null)
                                    throw new ArgumentException($"Min/max arrays cannot be null at index {i}.");

                                _valueRangeMap[currentArray] = new ValueRange(minValueList[i], maxValueList[i]);
                            }
                            else
                            {
                                _valueRangeMap[currentArray] = new ValueRange();
                                //Min/max values will be calculated on demand when GetValueRange is called for this frame.
                            }
                        }
                        else //currentArray already exists in the map
                        {
                            //Do nothing - ValueRange object for this array is already registered, so we assume the existing ValueRange is valid.
                        }
                    }
                }
                else // buf is null (new allocation)
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        var newArray = new T[length]; // OOM may happen here
                        _arrayList.Add(newArray);
                        // Initialize ValueRange with empty values. Actual min/max will be calculated on demand when GetValueRange is called for this frame.
                        _valueRangeMap[newArray] = new ValueRange();
                    }
                }
            }
            catch (Exception e)
            {
                // Clean up
                _arrayList.Clear();
                _valueRangeMap.Clear();

                Debug.WriteLine($"[AllocateArray] Exception: {e.Message}");

                if (e is OutOfMemoryException ex)
                {
                    long frameSize = (long)_xcount * _ycount * Unsafe.SizeOf<T>();
                    throw new OutOfMemoryException(
                        $"Allocation failed at frame {_arrayList.Count}/{frameCount}. " +
                        $"Est. memory: {(long)frameCount * frameSize / 1024 / 1024} MB.", ex);
                }
                else
                {
                    throw;
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
        public (double Min, double Max) GetValueRange() => GetValueRange(ActiveIndex, 0);

        /// <summary>
        /// Returns the minimum and maximum values for the specified frame with default value type.
        /// </summary>
        /// <param name="frameIndex">The zero-based index of the frame for which to retrieve the minimum and maximum values. Must be greater than
        /// or equal to 0 and less than the total number of frames.</param>
        /// <returns>A tuple containing the minimum and maximum values for the specified frame. The first item is the minimum
        /// value; the second item is the maximum value.</returns>
        public (double Min, double Max) GetValueRange(int frameIndex) => GetValueRange(frameIndex, 0);

        /// <summary>
        /// Retrieves the minimum and maximum values for the specified frame and value mode.
        /// </summary>
        /// <remarks>This method can be used  so as to handle a structured value type such as Complex.</remarks>
        /// <param name="frameIndex">The zero-based index of the frame for which to obtain the value range. Must be within the range of available
        /// frames.</param>
        /// <param name="valueMode">The index specifying which value mode to use when retrieving the minimum and maximum values. Must correspond
        /// to a valid entry in the value arrays.</param>
        /// <returns>A tuple containing the minimum and maximum values for the specified frame and value mode. Returns
        /// (double.NaN, double.NaN) if the value range cannot be determined.</returns>
        public (double Min, double Max) GetValueRange(int frameIndex, int valueMode)
        {
           return GetValueRangeList(frameIndex) is (var minArr, var maxArr)
                ? (minArr[valueMode], maxArr[valueMode])
                : (double.NaN, double.NaN);
        }

        /// <summary>
        /// Retrieves the minimum and maximum value lists for the specified frame index.
        /// </summary>
        /// <remarks>This method returns all the available min/max values for the structured value type. e.g. [Magnitude, Real, Imaginary, Phase, Power] for Complex </remarks>
        /// <param name="frameIndex">The zero-based index of the frame for which to obtain value ranges. Must be within the bounds of the
        /// underlying array list.</param>
        /// <returns>A tuple containing two lists: the first list contains the minimum values, and the second list contains the
        /// maximum values for the specified frame. Both lists are empty if the frame index is invalid or no value range
        /// exists.</returns>
        public (List<double> MinValues, List<double> MaxValues) GetValueRangeList(int frameIndex)
        {
            if (RefreshValueRangeRequired(frameIndex))
                return RefreshValueRange(frameIndex);
            if(_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
            {
                return (range.MinValues, range.MaxValues);
            }
            return ([], []);
        }

        /// <summary>
        /// Gets the minimum value associated with the current active index. The default value mode will be returned if the value type is not primitive.
        /// </summary>
        /// <returns>The minimum value for the currently active index.</returns>
        public double GetMinValue()
        {
            return GetValueRangeList(ActiveIndex).MinValues.FirstOrDefault(double.NaN);
        }

        /// <summary>
        /// Returns the maximum value associated with the currently active index. The default value mode will be returned if the value type is not primitive.
        /// </summary>
        /// <returns>The maximum value for the active index as a double.</returns>
        public double GetMaxValue()
        {
            return GetValueRangeList(ActiveIndex).MaxValues.FirstOrDefault(double.NaN);
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
        public (double Min, double Max) GetValueRange(Axis targetAxis, int[]? fixedCoordinates = null)
        {
            return GetValueRange(targetAxis, fixedCoordinates, 0);
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
        public (double Min, double Max) GetValueRange(Axis targetAxis, int[]? fixedCoordinates = null, int valueMode = 0)
        {
            // Special case: single frame or no dimensions
            if (FrameCount == 1)
            {
                return GetValueRange(0);
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
                    if(_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
                    {
                        double vmin = range.MinValues[valueMode];
                        double vmax = range.MaxValues[valueMode];

                        if (vmin < min) min = vmin;
                        if (vmax > max) max = vmax;
                   }
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

                    int frameIndex = Dimensions.GetFrameIndexAt(position);
                    
                    if (RefreshValueRangeRequired(frameIndex))
                        RefreshValueRange(frameIndex);

                    if(_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
                    {
                        double vmin = range.MinValues[valueMode];
                        double vmax = range.MaxValues[valueMode];
                        if (vmin < min) min = vmin;
                        if (vmax > max) max = vmax;
                    }
                }
            }

            return (min, max);
        }

        /// <summary>
        /// Returns the minimum and maximum values across all data series.
        /// </summary>
        /// <returns>A tuple containing the minimum and maximum values found globally. The first item is the minimum value; the
        /// second item is the maximum value.</returns>
        public (double Min, double Max) GetGlobalValueRange()
        {
            return GetGlobalValueRange(0);
        }

        /// <summary>
        /// Calculates the global minimum and maximum values across all frames using the specified value mode.
        /// </summary>
        /// <param name="valueMode">An integer that specifies the value mode to use when determining the minimum and maximum values for each
        /// frame.</param>
        /// <returns>A tuple containing the minimum and maximum values found across all frames. The first element is the global
        /// minimum; the second element is the global maximum.</returns>
        /// <exception cref="InvalidOperationException">Thrown if there are no frames available to calculate the global minimum and maximum values.</exception>
        public (double Min, double Max) GetGlobalValueRange(int valueMode)
        {
            if (FrameCount == 0) throw new InvalidOperationException("No frames available to calculate global min/max.");

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            //Evaluate min/max throughout the map;
            //This is more efficient if _arrayList contains many shallow copies of the same array reference, which refer to the same _valueRangeMap value;
            foreach (var range in _valueRangeMap.Values)
            {
                if (!range.IsValid)
                {
                    RefreshValueRange(_arrayList.IndexOf(_valueRangeMap.First(kvp => kvp.Value == range).Key));
                }
                var vmin = range.MinValues[valueMode];
                var vmax = range.MaxValues[valueMode];
                if (vmin < min) min = vmin;
                if (vmax > max) max = vmax;
            }

            return (min, max);

        }

        protected (List<double> MinValues, List<double> MaxValues) RefreshValueRange(int frameIndex)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            
            if (_minMaxFinder == null)
            {
                throw new InvalidOperationException(
                    $"Type '{typeof(T).Name}' has no MinMaxFinder registered. " +
                    $"Call 'MatrixData<{typeof(T).Name}>.RegisterDefaultMinMaxFinder(...)' first, " +
                    $"or avoid operations that require min/max statistics.");
            }

            var array = _arrayList[frameIndex]; 
            var (minValues, maxValues) = _minMaxFinder(array);
            if(_valueRangeMap.ContainsKey(array)) //Key exists in the map
            {
                _valueRangeMap[array].Set(minValues, maxValues);
                return (_valueRangeMap[array].MinValues, _valueRangeMap[array].MaxValues);
            }
            else
            {
                //Invalid condition; Do nothing here;
                return ([], []);
            }
        }

        
        /// <summary>
        /// Determines whether the value range for the specified frame index needs to be refreshed.
        /// </summary>
        /// <param name="frameIndex">The zero-based index of the frame to check for value range validity.</param>
        /// <returns>true if the value range for the specified frame is missing, empty, mismatched in length, or contains invalid
        /// values; otherwise, false.</returns>
        private bool RefreshValueRangeRequired(int frameIndex)
        {
            // Check if the arrays exist and if primary dimension is invalid
            if(_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
            {
                if (!range.IsValid)
                    return true;
                var minArr = range.MinValues;
                var maxArr = range.MaxValues;
                if (minArr == null || maxArr == null || minArr.Count == 0 || maxArr.Count == 0 || minArr.Count != maxArr.Count)
                {
                    return true;
                }
                for (int i = 0; i < minArr.Count; i++)
                {
                    if (minArr[i] > maxArr[i])
                    {
                        return true;
                    }
                }
            }
            //The key (array) does not exist in the map, which should not happen. Do nothing here;
            return false;
        }

        /// <summary>
        /// Invalidates the cached min and max values for the specified frame index. Min/max values for the specified frame will be recalculated when next requested.
        /// </summary>
        /// <remarks>
        /// This method is called whenever pixel data can potentially be modified, 
        /// ensuring that subsequent calls to retrieve min/max values will trigger a refresh of the cached statistics.<br/>
        /// This method is called in the following methods:
        /// <list type="bullet">  
        ///  <item>SetValueAt(int ix, int iy, int frameIndex, double v)</item>
        /// <item>SetValueAtTyped(int ix, int iy, int frameIndex, T value)</item> 
        ///  <item>[ix, iy] setter</item>
        ///  <item>GetArray(int frameIndex) </item>
        ///  <item>GetRawBytes(int frameIndex) </item>
        /// </list>
        /// </remarks>
        /// <param name="frameIndex"></param>
        public void Invalidate(int frameIndex)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            if (_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
            {
                range.Invalidate();
            }
        }

        /// <summary>
        /// Min/max values for the active index will be recalculated when next requested. 
        /// This method should be called after any operation that modifies pixel values, to ensure that subsequent calls to retrieve min/max values will return accurate results.
        /// </summary>
        public void Invalidate()
        {
            Invalidate(ActiveIndex);
        }

        public void InvalidateAllFrames()
        {
            for (int i = 0; i < FrameCount; i++)
            {
                Invalidate(i);
            }
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
        /// This method automatically invalidates the cached min/max values for the specified frame, ensuring that any modifications to the array will trigger a refresh of the statistics when next requested.
        /// However, users should call Invalidate explicitly when further modifying the array kept outside after calling GetValueRange.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] GetArray(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            Invalidate(frameIndex); // Ensure min/max will be recalculated if data is modified through the byte span
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

            if (minValues != null && maxValues != null 
                && minValues.Length > 0 && maxValues.Length > 0 
                && minValues.Length == maxValues.Length)
            {
                if (_valueRangeMap.TryGetValue(array, out var range))
                {
                    range.Set(minValues, maxValues);
                }
            }
            else
            {
                //RefreshValueRange(frameIndex);
                Invalidate(frameIndex);
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
        /// Accessor for a 3D matrix where <c>Axes.Length == 1</c>.
        /// The frame index corresponds directly to the first axis.
        /// </summary>
        /// <remarks>
        /// <para><b>Performance Note (Optimal Loop Order):</b></para>
        /// <para>The internal memory layout is optimized for spatial coordinates (X, Y, Axis0). 
        /// For maximum performance and sequential memory access, nest your loops from right to left: 
        /// <c>i_axis0</c> (outermost) &gt; <c>iy</c> &gt; <c>ix</c> (innermost).</para>
        /// </remarks>
        /// <param name="ix">The x-coordinate (column index).</param>
        /// <param name="iy">The y-coordinate (row index).</param>
        /// <param name="i_axis0">The index for the 1st axis (Axes[0]).</param>
        public T this[int ix, int iy, int i_axis0]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _arrayList[i_axis0][iy * _xcount + ix]; //i_axis0 corresponds to frame index directly since there's only one axis

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                _arrayList[i_axis0][iy * _xcount + ix] = value;
                Invalidate(i_axis0);
            }
        }

        /// <summary>
        /// Accessor for a 4D matrix where <c>Axes.Length == 2</c>.
        /// </summary>
        /// <remarks>
        /// This explicit overload prevents memory allocation and ensures maximum performance via JIT inlining.
        /// </remarks>
        /// <param name="ix">The x-coordinate (column index).</param>
        /// <param name="iy">The y-coordinate (row index).</param>
        /// <param name="i_axis0">The index for the 1st axis (Axes[0]).</param>
        /// <param name="i_axis1">The index for the 2nd axis (Axes[1]).</param>
        /// <returns>The value at the specified multidimensional coordinates.</returns>
        public T this[int ix, int iy, int i_axis0, int i_axis1]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _arrayList[Dimensions.GetFrameIndexAt(i_axis0, i_axis1)][iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(i_axis0, i_axis1);
                _arrayList[frameIndex][iy * _xcount + ix] = value;
                Invalidate(frameIndex);
            }
        }

        /// <summary>
        /// Accessor for a 5D matrix where <c>Axes.Length == 3</c>.
        /// </summary>
        /// <remarks>
        /// This explicit overload prevents memory allocation and ensures maximum performance via JIT inlining.
        /// </remarks>
        /// <param name="ix">The x-coordinate (column index).</param>
        /// <param name="iy">The y-coordinate (row index).</param>
        /// <param name="i_axis0">The index for the 1st axis (Axes[0]).</param>
        /// <param name="i_axis1">The index for the 2nd axis (Axes[1]).</param>
        /// <param name="i_axis2">The index for the 3rd axis (Axes[2]).</param>
        /// <returns>The value at the specified multidimensional coordinates.</returns>
        public T this[int ix, int iy, int i_axis0, int i_axis1, int i_axis2]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _arrayList[Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2)][iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2);
                _arrayList[frameIndex][iy * _xcount + ix] = value;
                Invalidate(frameIndex);
            }
        }

        /// <summary>
        /// Accessor for a 6D matrix where <c>Axes.Length == 4</c>.
        /// </summary>
        /// <remarks>
        /// This explicit overload prevents memory allocation and ensures maximum performance via JIT inlining.
        /// </remarks>
        /// <param name="ix">The x-coordinate (column index).</param>
        /// <param name="iy">The y-coordinate (row index).</param>
        /// <param name="i_axis0">The index for the 1st axis (Axes[0]).</param>
        /// <param name="i_axis1">The index for the 2nd axis (Axes[1]).</param>
        /// <param name="i_axis2">The index for the 3rd axis (Axes[2]).</param>
        /// <param name="i_axis3">The index for the 4th axis (Axes[3]).</param>
        /// <returns>The value at the specified multidimensional coordinates.</returns>
        public T this[int ix, int iy, int i_axis0, int i_axis1, int i_axis2, int i_axis3]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _arrayList[Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2, i_axis3)][iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2, i_axis3);
                _arrayList[frameIndex][iy * _xcount + ix] = value;
                Invalidate(frameIndex);
            }
        }

        /// <summary>
        /// Accessor for an N-dimensional matrix using an arbitrary number of axis indices.
        /// </summary>
        /// <remarks>
        /// <para><b>Warning:</b> This method allocates a new array on the heap for the <c>params</c> argument. 
        /// For high-performance tight loops, please use the explicit overloads (up to 4 axes).</para>
        /// <para><b>Performance Note (Optimal Loop Order):</b></para>
        /// <para>When iterating over multiple dimensions, structure your <c>for</c> loops strictly from right to left relative to the arguments. 
        /// The last defined axis (the right-most element in <c>axisIndices</c>) MUST be the outermost loop, and <c>ix</c> MUST be the innermost loop. 
        /// Failing to do so will result in severe cache misses and degraded performance.</para>
        /// </remarks>
        /// <param name="ix">The x-coordinate.</param>
        /// <param name="iy">The y-coordinate.</param>
        /// <param name="axisIndices">An array of indices corresponding to the layout of <c>Axes</c>.</param>
        public T this[int ix, int iy, params int[] axisIndices]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _arrayList[Dimensions.GetFrameIndexAt(axisIndices)][iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(axisIndices);
                _arrayList[frameIndex][iy * _xcount + ix] = value;
                Invalidate(frameIndex);
            }
        }

        /// <summary>
        /// Zero-copy conversion to byte array for serialization
        /// </summary>
        public unsafe ReadOnlySpan<byte> GetRawBytes(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            var array = _arrayList[frameIndex];
            Invalidate(frameIndex); // Ensure min/max will be recalculated if data is modified through the byte span
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
            //RefreshValueRange(frameIndex);
            Invalidate(frameIndex);
        }


        /// <summary>
        /// Sets values for the active frame using a lambda function. Min and max values are recalculated after setting.
        /// [IMPORTANT] A simple two-dimension loop is used to iterate over each (ix, iy) coordinate.
        /// </summary>
        /// <param name="func"></param>
        public MatrixData<T> Set(Func<int, int, double, double, T> func)
        {
            Set(ActiveIndex, func);
            return this;
        }


        // Utility methods

        /// <summary>
        ///  Sets values for the specified frame using a lambda function. Min and max values are recalculated after setting.
        /// Optimized with Span<typeparamref name="T"/> for better performance. 
        /// [IMPORTANT] A simple two-dimension loop is used to iterate over each (ix, iy) coordinate.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <param name="func"></param>
        public MatrixData<T> Set(int frameIndex, Func<int, int, double, double, T> func)
        {
            var array = GetArray(frameIndex); //Invalide is marked inside GetArray(). Min/max will be recalculated when next requested.
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
            return this;
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
        public MatrixData<T> ForEach(Action<int, T[]>action, bool useParallel = true)
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
                    RefreshValueRange(frameIndex); //Force refresh min/max here.
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
            return this;
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
        /// <param name="interpolate">
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
        /// double value2 = matrix.GetValue(5.3, 7.8, interpolate: true);
        /// 
        /// // Get value from a specific frame
        /// double value3 = matrix.GetValue(5.3, 7.8, frameIndex: 2, interpolate: true);
        /// </code>
        /// </example>
        public T GetValue(double x, double y, int frameIndex = -1, bool interpolate = false)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;

            // Without interpolation: nearest neighbor
            if (!interpolate)
            {
                int ix = XIndexOf(x, false);
                int iy = YIndexOf(y, false);
                return _arrayList[frameIndex][iy * _xcount + ix];
            }
            
            if (this is MatrixData<Complex>) //Special case for Complex type.
            {
                Complex[] array =(Complex[])(object)_arrayList[frameIndex];
                if (array == null)
                    throw new InvalidOperationException("Internal array is null");

                // Calculate index (allowing out of range)
                double iix = (x - _xmin) / XStep; // XStep = XRange * (_xcount - 1);
                double iiy = (y - _ymin) / YStep; // YStep = YRange * (_ycount - 1);

                // Clamp within range
                iix = Math.Clamp(iix, 0.0, _xcount - 1.0);
                iiy = Math.Clamp(iiy, 0.0, _ycount - 1.0);

                int ix0 = (int)iix;
                int iy0 = (int)iiy;
                int ix1 = (ix0 < _xcount - 1) ? ix0 + 1 : ix0;
                int iy1 = (iy0 < _ycount - 1) ? iy0 + 1 : iy0;

                double dx = iix - ix0;
                double dy = iiy - iy0;
                var v00 = array[iy0 * _xcount + ix0]!;
                var v10 = array[iy0 * _xcount + ix1]!;
                var v01 = array[iy1 * _xcount + ix0]!;
                var v11 = array[iy1 * _xcount + ix1]!;

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
                var v = GetValueAsDouble(x, y, frameIndex, true);
                return _fromDouble(v);
            }
        }

        /// <summary>
        /// Retrieves the value at the specified coordinates as a double, optionally interpolating between data points
        /// and selecting a specific frame.
        /// </summary>
        /// <remarks>Interpolation uses bilinear interpolation between the four nearest data points. This
        /// method is only supported for primitive numeric types.</remarks>
        /// <param name="x">The x-coordinate used to locate the value within the data array.</param>
        /// <param name="y">The y-coordinate used to locate the value within the data array.</param>
        /// <param name="frameIndex">The index of the frame from which to retrieve the value. If negative, the currently active frame is used.</param>
        /// <param name="interpolate">A value indicating whether to interpolate between adjacent data points at the specified coordinates.</param>
        /// <returns>The value at the specified coordinates, converted to double. If interpolation is enabled, the result is
        /// calculated using bilinear interpolation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the method is called on a non-primitive numeric type or if the internal array for the specified
        /// frame is null.</exception>
        public double GetValueAsDouble(double x, double y, int frameIndex = -1, bool interpolate = false)
        {
            if(!_isSupportedPrimitive)
                throw new InvalidOperationException($"GetValueAsDouble is only supported for primitive numeric types. Current type: {typeof(T).Name}"); 

            if (frameIndex < 0) frameIndex = _activeIndex;

            if(!interpolate)
            {
                return _toDouble(GetValue(x, y, frameIndex, false));
            }
            //interpolation is enabled
            var array = _arrayList[frameIndex];
            if(array == null)
                throw new InvalidOperationException("Internal array is null");

            // Calculate index (allowing out of range)
            double iix = (x - _xmin) / XStep;
            double iiy = (y - _ymin) / YStep;

            // Clamp within range
            iix = Math.Clamp(iix, 0.0, _xcount - 1.0);
            iiy = Math.Clamp(iiy, 0.0, _ycount - 1.0);

            int ix0 = (int)iix;
            int iy0 = (int)iiy;
            int ix1 = (ix0 < _xcount - 1) ? ix0 + 1 : ix0;
            int iy1 = (iy0 < _ycount - 1) ? iy0 + 1 : iy0;

            double dx = iix - ix0;
            double dy = iiy - iy0;

            // For standard numeric types
            double v00 = _toDouble(array[iy0 * _xcount + ix0]);
            double v10 = _toDouble(array[iy0 * _xcount + ix1]);
            double v01 = _toDouble(array[iy1 * _xcount + ix0]);
            double v11 = _toDouble(array[iy1 * _xcount + ix1]);

            double v = v00 * (1 - dx) * (1 - dy)
                     + v10 * dx * (1 - dy)
                     + v01 * (1 - dx) * dy
                     + v11 * dx * dy;

            return v;
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
                List<double>? minValueList = null;
                List<double>? maxValueList = null;
                if(_valueRangeMap.TryGetValue(_arrayList[i], out var range))
                {
                    minValueList = range.MinValues;
                    maxValueList = range.MaxValues;
                }
                var srcArray = GetArray(i);
                var dstArray = new T[srcArray.Length];
                srcArray.AsSpan().CopyTo(dstArray);
                clone.SetArray(dstArray, i, minValueList?.ToArray(), maxValueList?.ToArray());
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


        public IMatrixData Apply(IOperation operation)
        {
            if (operation == null) 
                throw new ArgumentNullException(nameof(operation));

            return operation switch
            {
                IMatrixDataOperation mdOp => mdOp.Execute(this),
                IVolumeOperation volOp => volOp.Execute(AsVolume(volOp.AxisName, volOp.BaseIndices)),
                _ => throw new NotSupportedException($"Unsupported operation type: {operation.GetType().Name}")
            };
        }

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
        /// Contains the set of primitive numeric types supported for MatrixData&lt;T&gt;. 
        /// This collection is used to determine if certain operations, such as GetValueAsDouble, are supported for a given type parameter T.
        /// </summary>
        /// <remarks>This static, read-only collection can be used to validate or restrict types during
        /// processing. The set includes common integral and floating-point types recognized by the system.</remarks>
        public static readonly HashSet<Type> SupportedPrimitiveTypes = new()
        {
            typeof(byte), typeof(sbyte),
            typeof(short), typeof(ushort),
            typeof(int), typeof(uint),
            typeof(long), typeof(ulong),
            typeof(float), typeof(double)
         };

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

        /// <summary>
        /// Extension method for MatrixData<Complex> to get the value range (min and max) based on the specified ComplexValueMode.
        /// </summary>
        /// <param name="src"></param>
        /// <param name="frameIndex"></param>
        /// <param name="mode"></param>
        /// <returns></returns>
        public static  (double Min, double Max) GetValueRange(this MatrixData<Complex> src, int frameIndex, ComplexValueMode mode)
        {
            return src.GetValueRange(frameIndex, (int)mode);
        }

        /*
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
        */
    }

}
