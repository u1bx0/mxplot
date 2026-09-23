using MxPlot.Core.IO;
using MxPlot.Core.IO.Formats;
using MxPlot.Core.Processing;
using MxPlot.Core.Utils;
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
using System.Threading;
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
        private readonly IList<T[]> _arrayList;

        /// <summary>
        /// A map that associates an array reference with its corresponding <see cref="ValueRange"/> statistics.
        /// </summary>
        private readonly Dictionary<T[], ValueRange> _valueRangeMap = new Dictionary<T[], ValueRange>();
        
        /// <summary>
        /// MinMaxFinder for this type. May be null for custom structs until registered.
        /// </summary>
        private MinMaxFinder? _minMaxFinder = _registeredDefaultFinder;

        /// <summary>
        /// The default interpolator used only for custom struct types. May be null for custom structs until registered.
        /// </summary>
        private IBilinearInterpolator<T>? _interpolator = _registeredDefaultInterpolator;

        /// <summary>The interpolator this instance uses for element types that are not supported numeric types, or <see langword="null"/>.</summary>
        internal IBilinearInterpolator<T>? Interpolator => _interpolator;

        /// <summary>
        /// Currently active frame index
        /// </summary>
        private int _activeIndex = 0;

        /// <summary>
        /// Initializes static members of the MatrixData class and registers the default MinMaxFinder for common types.
        /// </summary>
        static MatrixData()
        {
            _registeredDefaultFinder = CreateBuiltInMinMaxFinder();
            _registeredDefaultInterpolator = CreateBuiltInInterpolator();
        }

        /// <summary>
        /// Holds the currently registered default instance of the MinMaxFinder, or null if no default is set.
        /// </summary>
        private static MinMaxFinder? _registeredDefaultFinder;

        /// <summary>
        /// Indicates whether the type parameter T is a supported primitive type within the MatrixData framework.
        /// </summary>
        /// <remarks>This field is used to determine if operations involving the type T can be performed,
        /// based on its inclusion in the supported primitive types list.</remarks>
        private static readonly bool _isSupportedPrimitive =
                MatrixData.SupportedPrimitiveTypes.Contains(typeof(T));

        /// <summary>
        /// Holds the currently registered default instance of the IBilinearInterpolator for the type T, or null if no default is set.
        /// </summary>
        private static IBilinearInterpolator<T>? _registeredDefaultInterpolator;

        private static readonly Type _valueType = typeof(T);

        /// <summary>
        /// Gets the name of the value type T.
        /// </summary>
        private static readonly string _valueTypeName = default(T) switch
        {
            byte => "byte",
            sbyte => "sbyte",
            short => "short",
            ushort => "ushort",
            int => "int",
            uint => "uint",
            long => "long",
            ulong => "ulong",
            float => "float",
            double => "double",
            decimal => "decimal",
            Complex => "Complex",
            _ => typeof(T).Name
        };

        private static readonly int _elementSize = Unsafe.SizeOf<T>();

        /// <summary>
        /// Provider delegate for min and max arrays for the specified array
        /// </summary>
        /// <param name="array">The array for which to find the minimum and maximum values.</param>
        /// <returns>A tuple containing two arrays: the minimum values and the maximum values.</returns>
        public delegate (double[] minValues, double[] maxValues) MinMaxFinder(T[] array);

        // Events
        public event EventHandler? ScaleChanged;
        public event EventHandler? FrameAxisChanged;
        public event EventHandler? ActiveIndexChanged;
        public event EventHandler? UnitChanged;

        // Properties

        /// <summary>
        /// Gets whether the underlying frame list currently does not hold every frame resident in
        /// memory -- forwards to <see cref="ILazyDataSource.IsVirtual"/> on the backend (directly, or
        /// through a <see cref="RoutedFrames{T}"/> wrapper), or <see langword="false"/> for a plain
        /// in-memory <c>List&lt;T[]&gt;</c>. See <see cref="VirtualFrames{T}.IsVirtual"/> for what
        /// drives this per backend (MMF is unconditionally <see langword="true"/>; a
        /// compressed-decode or Remote backend converges to <see langword="false"/> once fully loaded).
        /// </summary>
        public bool IsVirtual
        {
            get
            {
                if (_arrayList is ILazyDataSource lazy)
                    return lazy.IsVirtual;
                else if (_arrayList is RoutedFrames<T> routed)
                    return routed.IsVirtual;
                else
                    return false;
            }
        }

        /// <inheritdoc/>
        public bool IsWritable => !_arrayList.IsReadOnly;

        protected delegate T[] InternalArrayProvider(int frameIndex, bool needsInvalidate);

        /// <summary>
        /// Gets the internal array provider to obtain T[] with parameters (int frameIndex, bool needsInvalidate), used to manage array data for this instance.
        /// </summary>
        /// <remarks>This property is intended for advanced scenarios where direct access to the
        /// underlying array management system is required. The appropriate provider is set at the constructor.</remarks>
        protected InternalArrayProvider GetInternalArray { get; }

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

        /// <summary>
        /// Gets the type of the value represented by this instance.
        /// </summary>
        public Type ValueType => _valueType;

        public string ValueTypeName => _valueTypeName;

        public int ElementSize => _elementSize;

        /// <summary>
        /// Gets whether this MatrixData instance requires explicit disposal to release resources.
        /// Default: false (does not require disposal) when using in-memory arrays.
        /// This is true if the underlying data storage is an <see cref="ILazyDataSource"/> this
        /// instance owns (see <see cref="ILazyDataSource.IsOwned"/>).
        /// </summary>
        public bool RequiresDisposal { get; private set; } = false;

        public ICacheStrategy? CacheStrategy
        {
            get
            {
                if(_arrayList is ICacheableFrameList cacheable)
                {
                    return cacheable.CacheStrategy;
                }
                return null;
            }
            set
            {
                if(value != null && _arrayList is ICacheableFrameList cacheable)
                {
                    cacheable.CacheStrategy = value;
                }
            }
        }

        /// <summary>
        /// Returns the underlying cache-control surface for diagnostic/tuning purposes, or
        /// <see langword="null"/> if the data is not backed by one. See
        /// <see cref="IMatrixData.GetDiagnosticCacheableList"/>'s own remarks.
        /// </summary>
        public ICacheableFrameList? GetDiagnosticCacheableList()
        {
            if (_arrayList is ICacheableFrameList cacheable)
                return cacheable;
            if (_arrayList is RoutedFrames<T> routed)
                return routed.GetUnderlyingCacheableList();
            return null;
        }


        /// <summary>
        /// Registers the specified MinMaxFinder instance as the default implementation to use for min/max operations.
        /// </summary>
        /// <remarks>Registering a new default MinMaxFinder will replace any previously registered
        /// instance. This method is typically called during application initialization to configure custom min/max
        /// logic for a type unsupported by the built-in implementation (e.g. a custom struct).
        /// <para>
        /// Not allowed for the built-in primitive numeric types (<see cref="byte"/>, <see cref="sbyte"/>,
        /// <see cref="short"/>, <see cref="ushort"/>, <see cref="int"/>, <see cref="uint"/>, <see cref="long"/>,
        /// <see cref="ulong"/>, <see cref="float"/>, <see cref="double"/>) -- their min/max has one unambiguous
        /// definition, so overriding it would silently corrupt behavior application-wide. <see cref="Complex"/>
        /// remains overridable: which of its Magnitude/Real/Imaginary/Phase/Power modes to report is a domain
        /// choice, not a single correct answer. To restore the built-in <see cref="Complex"/> behavior after
        /// overriding it, register <see cref="Utils.FastMinMaxFinder.FindComplex"/> again -- its signature already
        /// matches <see cref="MinMaxFinder"/>.
        /// </para>
        /// </remarks>
        /// <param name="finder">The MinMaxFinder instance to register as the default. Cannot be null.</param>
        /// <exception cref="NotSupportedException">Thrown when <typeparamref name="T"/> is one of the built-in primitive numeric types.</exception>
        public static void RegisterDefaultMinMaxFinder(MinMaxFinder finder)
        {
            if(finder == null)
                throw new ArgumentNullException(nameof(finder));

            if (IsBuiltInPrimitiveNumericType())
                throw new NotSupportedException(
                    $"Cannot override the default MinMaxFinder for built-in numeric type {typeof(T).Name}; " +
                    "its min/max has one unambiguous definition. RegisterDefaultMinMaxFinder is for types " +
                    "the built-in implementation does not cover (custom structs, or Complex's mode selection).");

            _registeredDefaultFinder = finder;
        }

        /// <summary>
        /// Gets the currently registered default <see cref="MinMaxFinder"/> for this type -- the exact delegate a
        /// freshly-constructed <see cref="MatrixData{T}"/> would use. Useful for computing per-frame min/max ahead
        /// of constructing an instance (e.g. while streaming frames into a <c>List&lt;T[]&gt;</c> during file
        /// loading), so the precomputed values are guaranteed consistent with what the instance would compute itself.
        /// Null for a custom struct type that has not called <see cref="RegisterDefaultMinMaxFinder"/>.
        /// </summary>
        public static MinMaxFinder? DefaultMinMaxFinder => _registeredDefaultFinder;

        private static bool IsBuiltInPrimitiveNumericType()
        {
            return typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte)
                || typeof(T) == typeof(short) || typeof(T) == typeof(ushort)
                || typeof(T) == typeof(int) || typeof(T) == typeof(uint)
                || typeof(T) == typeof(long) || typeof(T) == typeof(ulong)
                || typeof(T) == typeof(float) || typeof(T) == typeof(double);
        }

        /// <summary>
        /// Registers the specified <see cref="IBilinearInterpolator{T}"/> instance as the default implementation
        /// used for bilinear interpolation.
        /// This is necessary only for types that are not natively supported by the library.
        /// For primitive numeric types (e.g., int, float, double) and <see cref="System.Numerics.Complex"/>,
        /// the library provides built-in interpolation logic.
        /// If no interpolator is provided for a custom type, <c>GetValue(..., interpolate: true)</c>
        /// falls back to nearest-neighbor behavior.
        /// </summary>
        /// <param name="interpolator">The IBilinearInterpolator instance to register as the default. Cannot be null.</param>
        public static void RegisterDefaultInterpolator(IBilinearInterpolator<T> interpolator)
        {
            if (interpolator == null) 
                throw new ArgumentNullException(nameof(interpolator));
            _registeredDefaultInterpolator = interpolator;
        }

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
            // A 1-element axis has no defined physical step (XRange is 0 too, since a single
            // point can't span a range) - dividing by (_xcount - 1) = 0 gives NaN, not the "no
            // step" value every consumer actually expects. Axis.Step and Scale2D's constructor
            // already guard this the same way (Count > 1 ? ... : 0); this was the one place that
            // didn't, and a stray NaN here silently breaks aspect-ratio scaling downstream
            // (RenderSurface.GetAspectScales's "md.XStep <= 0" guard doesn't catch NaN - NaN
            // comparisons are always false - so it flows through Math.Min/division into the
            // render rect and the bitmap draws with zero size instead of falling back to 1:1).
            XRange = _xmax - _xmin;
            YRange = _ymax - _ymin;
            XStep = _xcount > 1 ? XRange / (_xcount - 1) : 0;
            YStep = _ycount > 1 ? YRange / (_ycount - 1) : 0;
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
            if ((ix < 0 || ix >= XCount) && !extendRange)
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
                {
                    throw new InvalidOperationException($"Axis must be specified to create VolumeAccessor for muti-axis data");
                }
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
        /// Creates a new <see cref="MatrixData{T}"/> instance with frames rearranged according to the specified order.
        /// The order list does not require the complete set of the original frames; it can extract a subset,
        /// reverse, or repeat frames as needed. Dimension information is removed from the result.
        /// </summary>
        /// <remarks>
        /// <para><b>Shallow copy (default):</b> The returned instance shares the same underlying <c>T[]</c> 
        /// frame buffers and <see cref="ValueRange"/> <c>List&lt;double&gt;</c> references with the original.
        /// This means:</para>
        /// <list type="bullet">
        ///   <item>Mutations to pixel data are immediately visible across all instances sharing the same <c>T[]</c>.</item>
        ///   <item>Calling <see cref="Invalidate(int)"/> on any instance that shares the same <c>T[]</c> key 
        ///         propagates to all others via the shared <c>List&lt;double&gt;</c> reference 
        ///         (see <see cref="ValueRange"/> design).</item>
        ///   <item>For virtual (MMF-backed) data, a <see cref="RoutedFrames{T}"/> wrapper is used to
        ///         route logical indices to physical frames in the underlying <c>IFrameKeyProvider&lt;T&gt;</c>.</item>
        /// </list>
        /// <para><b>Deep copy:</b> All frame buffers are duplicated. The result is fully independent;
        /// no data or cache state is shared with the original.</para>
        /// <para><b>Metadata</b> is <b>not</b> copied to the new instance.</para>
        /// </remarks>
        /// <param name="order">
        /// A list of zero-based frame indices specifying the desired order. 
        /// May contain duplicates (to alias frames) or a subset of the original indices.
        /// </param>
        /// <param name="deepCopy">
        /// <c>true</c> to allocate independent copies of all frame arrays;
        /// <c>false</c> (default) to share frame references and ValueRange cache.
        /// </param>
        /// <returns>A new <see cref="MatrixData{T}"/> with frames arranged per <paramref name="order"/>.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="order"/> is empty or contains indices outside <c>[0, FrameCount)</c>.
        /// </exception>
        public MatrixData<T> Reorder(List<int> order, bool deepCopy = false)
        {
            int num = order.Count;
            if (num == 0)
                throw new ArgumentException("order list cannot be empty."); 
            int max = order.Max();
            int min = order.Min();
            if (max >= FrameCount || min < 0)
                throw new ArgumentException($"invalid order: min = {min}, max = {max}, count = {num}");

            IList<T[]> newArrays;
            var vminList = new List<List<double>>();
            var vmaxList = new List<List<double>>();

            if(deepCopy)
            {
                var arrays = new List<T[]>();
                foreach (int index in order)
                {
                    var srcSpan = AsSpan(index);
                    var dst = new T[srcSpan.Length];
                    srcSpan.CopyTo(dst);
                    arrays.Add(dst);
                    //For deep copy, min/max value list is  not provided.
                    vminList.Add(new List<double>());
                    vmaxList.Add(new List<double>());
                }
                newArrays = arrays;
            }
            else //Shallow copy
            {
                if(_arrayList is IFrameKeyProvider<T> fkp)
                {
                    newArrays = new RoutedFrames<T>(_arrayList, order);
                }
                else //InMemory: _arrayList is List<T[]>
                {
                    var arrays = new List<T[]>();
                    foreach (int index in order)
                    {
                        arrays.Add(_arrayList[index]);
                    }
                    newArrays = arrays;
                }
                foreach (int index in order)
                {
                    var (vmins, vmaxs) = GetValueRangeList(index, true);
                    vminList.Add(vmins);
                    vmaxList.Add(vmaxs);
                }
            }

            var md = new MatrixData<T>(GetScale(), newArrays, vminList, vmaxList);
            md.XUnit = XUnit;
            md.YUnit = YUnit;

            return md;
        }

        /// <summary>
        /// Creates a deep copy of this <see cref="MatrixData{T}"/>.
        /// <para>When the source is virtual (MMF-backed), the clone is also virtual — frame data is
        /// streamed to a temporary .mxd file one frame at a time, keeping peak memory proportional
        /// to a single frame regardless of total dataset size.</para>
        /// <para>When the source is in-memory, a conventional in-memory deep copy is returned.</para>
        /// </summary>
        public object Clone()
        {
            if (IsVirtual)
                return CloneAsVirtual();
            else
                return CloneInMemory();
        }

        /// <summary>
        /// Creates a clone of this <see cref="MatrixData{T}"/> with the option to force in-memory copying.
        /// </summary>
        /// <param name="forceInMemory">If true, forces the clone to be created in memory even if the source is virtual.</param>
        /// <returns>A clone of this <see cref="MatrixData{T}"/>.</returns>
        public MatrixData<T> Clone(bool forceInMemory)
        {
            if (forceInMemory)
                return CloneInMemory();
            else
                return (MatrixData<T>)Clone();
        }

        /// <summary>
        /// Creates a clone of this <see cref="MatrixData{T}"/> with the option to force in-memory copying.
        /// </summary>
        /// <param name="forceInMemory">If true, forces the clone to be created in memory even if the source is virtual.</param>
        /// <returns>A clone of this <see cref="MatrixData{T}"/>.</returns>
        IMatrixData IMatrixData.Clone(bool forceInMemory)
        {
            return Clone(forceInMemory);
        }

        /// <summary>
        /// Creates a clone of this <see cref="MatrixData{T}"/>, with progress reporting and
        /// cancellation support -- see <see cref="IMatrixData.Clone(bool, IProgress{int}, CancellationToken)"/>.
        /// </summary>
        /// <param name="forceInMemory">If true, forces the clone to be created in memory even if the source is virtual.</param>
        /// <param name="progress">Optional progress reporter; reports -N once (N = FrameCount), then 0..N-1 as frames are copied.</param>
        /// <param name="cancellationToken">Checked between frames.</param>
        public MatrixData<T> Clone(bool forceInMemory, IProgress<int>? progress, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (forceInMemory)
                return CloneInMemory(progress, cancellationToken);
            return IsVirtual ? CloneAsVirtual(progress, cancellationToken) : CloneInMemory(progress, cancellationToken);
        }

        IMatrixData IMatrixData.Clone(bool forceInMemory, IProgress<int>? progress, CancellationToken cancellationToken)
        {
            return Clone(forceInMemory, progress, cancellationToken);
        }

        private MatrixData<T> CloneInMemory(IProgress<int>? progress = null, CancellationToken ct = default)
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

            progress?.Report(-FrameCount);
            for (int i = 0; i < FrameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                List<double>? minValueList = null;
                List<double>? maxValueList = null;
                if(_valueRangeMap.TryGetValue(GetFrameKey(i), out var range))
                {
                    minValueList = range.MinValues;
                    maxValueList = range.MaxValues;
                }
                var srcSpan = AsSpan(i);
                var dstArray = new T[srcSpan.Length];
                srcSpan.CopyTo(dstArray);
                clone.SetArray(dstArray, i, minValueList?.ToArray(), maxValueList?.ToArray());
                progress?.Report(i);
            }

            return clone;
        }

        private MatrixData<T> CloneAsVirtual(IProgress<int>? progress = null, CancellationToken ct = default)
        {
            var wvsf = IO.Formats.MatrixDataSerializer.CreateTempVessel<T>(_xcount, _ycount, FrameCount);

            // Frames whose value range is already cached on the source, collected so it can be
            // propagated to the clone below instead of being silently discarded (which would force
            // a full rescan on next access even though the source already did that work) -- the
            // Virtual-output counterpart to what CloneInMemory already does via SetArray's
            // minValues/maxValues parameters. Collected as (index, list) tuples rather than a full
            // FrameCount-length pair of lists because typically only a handful of frames are
            // actually cached (e.g. just the one the user was viewing) -- CreateAsVirtualFrames's
            // constructor only accepts an all-or-nothing per-frame list (every entry present, same
            // shape), whereas applying cached entries individually after construction has no such
            // restriction. No extra scanning: this only reuses ranges the source already computed.
            List<(int Index, List<double> Min, List<double> Max)>? cachedRanges = null;

            progress?.Report(-FrameCount);
            try
            {
                for (int i = 0; i < FrameCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    T[] src = GetInternalArray(i, needsInvalidate: false);
                    wvsf.WriteDirectly(i, src);
                    if (_valueRangeMap.TryGetValue(GetFrameKey(i), out var range) && range.IsValid)
                    {
                        cachedRanges ??= new List<(int, List<double>, List<double>)>();
                        cachedRanges.Add((i, range.MinValues, range.MaxValues));
                    }
                    progress?.Report(i);
                }
                wvsf.Flush();
            }
            catch
            {
                // Cancelled or failed mid-write -- don't leak the (possibly multi-GB) temp file.
                wvsf.Dispose();
                throw;
            }

            var clone = CreateAsVirtualFrames(_xcount, _ycount, wvsf);
            clone.SetXYScale(_xmin, _xmax, _ymin, _ymax);
            clone.XUnit = XUnit;
            clone.YUnit = YUnit;
            clone.DefineDimensions(Axis.CreateFrom(Dimensions.Axes.ToArray()));
            foreach (var key in Metadata.Keys)
                clone.Metadata[key] = Metadata[key];

            if (cachedRanges != null)
            {
                foreach (var (index, min, max) in cachedRanges)
                {
                    if (clone._valueRangeMap.TryGetValue(clone.GetFrameKey(index), out var cloneRange))
                        cloneRange.Set(min, max);
                }
            }

            return clone;
        }

        public override string ToString()
        {
            return $"MatrixData<{typeof(T).Name}>: XCount={XCount}, YCount={YCount}, FrameCount={FrameCount}";
        }

        public void Flush()
        {
            if (_arrayList is IWritableFrameProvider<T> virtualList)
            {
                virtualList.Flush();
            }
        }

        #region I/O methods and create virtual frames using IMatrixDataIO (IMatrixDataReader/IMatrixDataWriter/IVirtualFrameBuilder)
        /// <summary>
        /// Loads matrix data from a file using the specified reader.
        /// </summary>
        /// <param name="filePath">The path to the file to load.</param>
        /// <param name="reader">The reader implementation responsible for parsing the file format.</param>
        /// <returns>A new <see cref="MatrixData{T}"/> instance containing the loaded data.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is null.</exception>
        /// <example>
        /// <code>
        /// var data = MatrixData&lt;float&gt;.Load("file.mxd", new MxBinaryFormat());
        /// </code>
        /// </example>
        public static MatrixData<T> Load(string filePath, IMatrixDataReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            return reader.Read<T>(filePath);
        }

        /// <summary>
        /// Saves the current matrix data to a file using the specified writer. 
        /// </summary>
        /// <param name="filePath">The destination file path where the data will be saved.</param>
        /// <param name="writer">The writer implementation responsible for formatting and writing the data.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> is null.</exception>
        /// <example>
        /// <code>
        /// var csv = new CsvFormat { Separator = "," };
        /// matrix.SaveAs("output.csv", csv);
        /// </code>
        /// </example>
        public void SaveAs(string filePath, IMatrixDataWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            // Guard: prevent overwriting the active backing file of read-only virtual data.
            // Writers typically open the destination in truncate mode, which would destroy
            // the MMF-backed source mid-read and corrupt the data irreversibly.
            if (_arrayList is IMmfFrameList vfl && !IsWritable)
            {
                string dest = Path.GetFullPath(filePath);
                string src = Path.GetFullPath(vfl.SourcePath);
                if (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(
                        $"Cannot save to '{Path.GetFileName(filePath)}' because it is the active backing file " +
                        $"for this read-only virtual data. Choose a different destination.");
            }

            var accessor = new BackendAccessor(_arrayList);
            writer.Write(filePath, this, accessor);
        }

        /// <summary>
        /// Internal class acting as an adapter to provide access to the underlying frame data for I/O operations.
        /// </summary>
        private class BackendAccessor: IBackendAccessor {
            private readonly object _backingStore;
            public BackendAccessor(object backingStore)
            {
                _backingStore = backingStore;
            }
            public bool TryGet<TBackend>(out TBackend? backend) where TBackend : class
            {
                //If the backing store matches the requested type, return it; otherwise, return null.
                //This allows writers to access specific implementations if available.
                if (_backingStore is TBackend match)
                {
                    backend = match;
                    return true;
                }
                backend = null;
                return false;
            }
        }

        /// <summary>
        /// Creates a new, writable <see cref="MatrixData{T}"/> backed by virtual storage (e.g., a memory-mapped file) 
        /// using the specified builder.
        /// </summary>
        /// <param name="filePath">
        /// The explicit file path for the underlying virtual storage. 
        /// If <see langword="null"/>, the builder is responsible for creating and managing a temporary file.
        /// </param>
        /// <param name="builder">The builder implementation that defines the structure and allocates the virtual frames.</param>
        /// <returns>A newly constructed <see cref="MatrixData{T}"/> instance bound to the virtual storage.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is null.</exception>
        /// <example>
        /// <code>
        /// var builder = OmeTiffFormat.AsVirtualBuilder(spec);
        /// var data = MatrixData&lt;ushort&gt;.CreateVirtual(null, builder);
        /// </code>
        /// </example>
        public static MatrixData<T> CreateVirtual(string? filePath, IVirtualFrameBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            return builder.CreateWritable<T>(filePath);
        }

        #endregion



        public TResult Apply<TResult>(IOperation<TResult> operation)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            return operation switch
            {
                IMatrixDataOperation matOp => (TResult)matOp.Execute(this), 
                IVolumeOperation<TResult> volOp => volOp.Execute(AsVolume(volOp.AxisName, volOp.BaseIndices)),
                _ => throw new NotSupportedException($"Unsupported operation: {operation.GetType().Name}")
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

        /// <summary>
        /// Undoes a prior <see cref="SetMinMaxFinder"/> override on this instance, reverting it to the
        /// currently registered default (<see cref="DefaultMinMaxFinder"/>) -- the same finder a freshly
        /// constructed <see cref="MatrixData{T}"/> would use. There is no other way to undo
        /// <see cref="SetMinMaxFinder"/> in place short of constructing a new instance.
        /// </summary>
        public void ResetMinMaxFinder()
        {
            _minMaxFinder = _registeredDefaultFinder;
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

        private static IBilinearInterpolator<T>? CreateBuiltInInterpolator()
        {
            if (typeof(T) == typeof(Complex))
            {
                return (IBilinearInterpolator<T>)(object)new ComplexBilinearInterpolator();
            }

            return null;
        }


    }

}
