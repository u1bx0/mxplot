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
    public partial class MatrixData<T> : IMatrixData where T : unmanaged
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

}
