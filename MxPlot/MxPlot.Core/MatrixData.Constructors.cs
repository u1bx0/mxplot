using MxPlot.Core.IO;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;

namespace MxPlot.Core
{
    /* ========================================================================
     * MatrixData<T> : Constructors & Initialization Architecture
     * ========================================================================
     *
     * [ CORE PRINCIPLES ]
     * - Constructor Chaining: All routes lead to a core private constructor.
     * - Delegate Injection  : GetInternalArray uses pattern matching (Strategy).
     * - Resource Lifecycle  : Tracks ownership to safely Dispose() resources.
     *
     * [ INITIALIZATION SCENARIOS ]
     *
     * 1. Fresh Allocation (In-Memory)
     * - Purpose : New empty data (1 frame to N-dim).
     * - Backing : Allocates new T[] arrays.
     * - Cache   : Unique. (Includes detailed OOM exception handling).
     *
     * 2. Shallow Copy (Unlinked Cache)
     * - Purpose : Wrap existing data arrays.
     * - Backing : Shares physical T[] by reference.
     * - Cache   : Unique. Creates new independent Min/Max containers.
     *
     * 3. Shared Reference (Linked Cache)
     * - Purpose : Synchronized views (e.g., reordered, filtered).
     * - Backing : Shares T[] AND Min/Max containers by reference.
     * - Cache   : Synchronized. Invalidate() propagates to all views.
     *
     * 4. Trusted Frames (Routed)
     * - Purpose : Advanced internal routing (e.g., RoutedFrames<T>).
     * - Backing : Injects custom frame providers.
     * - Cache   : Uses IFrameKeyProvider<T> to prevent cache duplication.
     *
     * 5. Virtual Frames (Memory-Mapped)
     * - Purpose : Out-of-core / MMF datasets (Gigabyte-scale).
     * - Backing : Injects VirtualFrames<T>.
     * - Cache   : Evaluated on-demand. Takes ownership for safe Disposal.
     *
     * ======================================================================== */

    public partial class MatrixData<T> : IMatrixData where T : unmanaged
    {

        public void Dispose()
        {
            if (RequiresDisposal && _arrayList is IDisposable disposableObj)
            {
                disposableObj.Dispose();
            }
        }

        #region Constructors and memory allocation logic, where the entire data is stored in memory

        /// <summary>
        /// Core private constructor: handles validation, dimensions, and scale.
        /// All in-memory public constructors chain to this.
        /// GetInternalArray is initialized with a default implementation that assumes _arrayList will be set in the chaining constructor.
        /// NOTE: _arrayList and Dimensions are left as null! and MUST be
        ///       overwritten in every chaining constructor body before use.
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="scale"></param>
        private MatrixData(int xnum, int ynum, Scale2D? scale = null)
        {
            CheckPlane(xnum, ynum);
            _xcount = xnum;
            _ycount = ynum;
            // Temporary placeholders: overwritten in every chaining constructor body.
            _arrayList = null!;
            Dimensions = null!;
            // Default GetInternalArray implementation for List<T[]>. If _arrayList is provided differently in a chaining constructor, this will be overwritten.
            GetInternalArray = (frameIndex, needsInvalidate) =>
            {
                if (needsInvalidate) Invalidate(frameIndex); // Ensure min/max will be recalculated in case data is modified
                return _arrayList[frameIndex];
            };
            if (scale.HasValue)
            {
                Debug.Assert(scale.Value.XCount == xnum && scale.Value.YCount == ynum,
                    "Scale2D dimensions must match xnum/ynum");
                SetXYScale(scale.Value.XMin, scale.Value.XMax, scale.Value.YMin, scale.Value.YMax);
            }
            else
            {
                SetXYScale(0, xnum - 1, 0, ynum - 1);
            }
        }

        /// <summary>
        /// Initializes a new single-frame instance of the <see cref="MatrixData&lt;T&gt;"/> class. Existing data array can be provided (shallow copy).
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="array"></param>
        public MatrixData(int xnum, int ynum, T[]? array = null) : this(xnum, ynum, (Scale2D?)null)
        {
            _arrayList = AllocateArray(1, array != null ? new List<T[]>() { array } : null);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixData&lt;T&gt;"/> class with the specified number of frames.
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="frameCount"></param>
        public MatrixData(int xnum, int ynum, int frameCount) : this(xnum, ynum, (Scale2D?)null)
        {
            _arrayList = AllocateArray(frameCount);
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixData&lt;T&gt;"/> class with existing data arrays (shallow copy).
        /// </summary>
        /// <param name="xnum"></param>
        /// <param name="ynum"></param>
        /// <param name="arrayList"></param>
        public MatrixData(int xnum, int ynum, List<T[]> arrayList) : this(xnum, ynum, (Scale2D?)null)
        {
            _arrayList = AllocateArray(arrayList.Count, arrayList);
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
        public MatrixData(int xnum, int ynum, List<T[]> arrayList, List<List<double>> minValueList, List<List<double>> maxValueList) : this(xnum, ynum, (Scale2D?)null)
        {
            _arrayList = AllocateArray(arrayList.Count, arrayList, minValueList, maxValueList);
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
            : this(xnum, ynum, arrayList,
                   primitiveMinValueList?.Select(val => new List<double> { val }).ToList() ?? [],
                   primitiveMaxValueList?.Select(val => new List<double> { val }).ToList() ?? [])
        { }

        /// <summary>
        /// Initializes a new instance with a single 2D frame
        /// </summary>
        /// <param name="scale"></param>
        public MatrixData(Scale2D scale) : this(scale.XCount, scale.YCount, scale)
        {
            _arrayList = AllocateArray(1);
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
        public MatrixData(Scale2D scale, params Axis[] axes) : this(scale.XCount, scale.YCount, scale)
        {
            _arrayList = AllocateArray(GetTotalFrameCount(axes));
            int frameCount = GetTotalFrameCount(axes);
            Dimensions = new DimensionStructure(this, axes);
        }

        /// <summary>
        /// Memory allocation logic for InMemory data.
        /// </summary>
        /// <param name="frameCount"></param>
        /// <param name="buf"></param>
        /// <param name="minValueList"></param>
        /// <param name="maxValueList"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="OutOfMemoryException"></exception>
        private IList<T[]> AllocateArray(int frameCount, IList<T[]>? buf = null, List<List<double>>? minValueList = null, List<List<double>>? maxValueList = null)
        {
            if (frameCount <= 0)
                throw new ArgumentException($"Frame count must be > 0: frameCount = {frameCount}");

            IList<T[]> allocated = new List<T[]>(frameCount);
            if (buf == null) //new allocation with empty buffer.
            {
                try
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        var newArray = new T[_xcount * _ycount]; // OOM may happen here
                        allocated.Add(newArray);
                        _valueRangeMap[newArray] = new ValueRange();
                    }
                }
                catch (Exception e)
                {
                    // Clean up
                    _arrayList?.Clear();
                    _valueRangeMap?.Clear();

                    Debug.WriteLine($"[AllocateArray] Exception: {e.Message}");

                    if (e is OutOfMemoryException ex)
                    {
                        long frameSize = (long)_xcount * _ycount * Unsafe.SizeOf<T>();
                        throw new OutOfMemoryException(
                            $"Allocation failed at frame {_arrayList?.Count ?? 0}/{frameCount}. " +
                            $"Est. memory: {(long)frameCount * frameSize / 1024 / 1024} MB.", ex);
                    }
                    else
                    {
                        throw;
                    }
                }
                return allocated;
            }
            else if (buf is List<T[]> list) //List<T[]> provided by caller
            {
                if (list.Count != frameCount)
                    throw new ArgumentException("Buffer count mismatch: buf.Count = {list.Count}, expected = {frameCount}");


                bool isMinMaxProvided = (minValueList != null && maxValueList != null) &&
                                    (minValueList.Count == frameCount) && (maxValueList.Count == frameCount);
                int length = _xcount * _ycount;
                for (int i = 0; i < frameCount; i++)
                {
                    allocated.Add(list[i]);

                    var currentArray = allocated[i];
                    if (currentArray == null || currentArray.Length != length)
                        throw new ArgumentException($"Invalid array at index {i} in provided buffer.");
                    if (isMinMaxProvided)
                    {
                        if (!_valueRangeMap.ContainsKey(currentArray))
                        {
                            _valueRangeMap[currentArray] = new ValueRange(minValueList![i], maxValueList![i]);
                        }
                    }
                    else
                    {
                        _valueRangeMap[currentArray] = new ValueRange();
                    }
                }
                return allocated;
            }
            else
            {
                throw new ArgumentException($"Invalid buf type: {buf} for AllocateArray");
            }
        }
        #endregion

        #region Initializing logic for RoutedFrames<T> and  VirtualFrameList<T>

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixData{T}"/> class using a pre-validated and trusted list of frames.
        /// This constructor is typically utilized for on-memory data or internally routed frames (such as those generated by reorder operations) where explicit resource ownership management is not required.
        /// </summary>
        /// <param name="scale">The spatial dimensions and scale structure associated with this data.</param>
        /// <param name="trustedFrames">
        /// The backing list of frame arrays. This can be a standard <see cref="List{T}">List&lt;T[]&gt;</see> or a specialized list implementing 
        /// <see cref="IFrameKeyProvider{T}"/> for custom cache key resolution, and/or <see cref="IWritableFrameProvider{T}"/> for advanced write-back capabilities.
        /// </param>
        /// <param name="minValueList">A pre-calculated list of minimum values corresponding to each frame.</param>
        /// <param name="maxValueList">A pre-calculated list of maximum values corresponding to each frame.</param>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="trustedFrames"/>, <paramref name="minValueList"/>, or <paramref name="maxValueList"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown when the number of elements in <paramref name="trustedFrames"/>, <paramref name="minValueList"/>, and <paramref name="maxValueList"/> do not strictly match.</exception>
        /// <remarks>
        /// <para>
        /// <b>Architecture Note:</b><br/>
        /// This constructor executes two critical setup operations tailored to the capabilities of the provided <paramref name="trustedFrames"/>:
        /// <list type="number">
        /// <item>
        /// <term>Cache Key Resolution:</term>
        /// <description>
        /// Populates the internal value range map. If the list implements <see cref="IFrameKeyProvider{T}"/>, it relies on the provider to yield deterministic keys, which safely prevents cache duplication (e.g., when multiple indices route to the same underlying array reference). Otherwise, it falls back to using the raw array references as keys.
        /// </description>
        /// </item>
        /// <item>
        /// <term>Delegate Injection &amp; Access Control:</term>
        /// <description>
        /// Dynamically configures the <c>GetInternalArray</c> delegate to serve as a unified gateway. 
        /// If the list implements <see cref="IWritableFrameProvider{T}"/>, write requests (<c>needsInvalidate = true</c>) will appropriately trigger dirty flags via <c>GetWritableArray</c>. 
        /// It also strictly enforces the <c>IsReadOnly</c> pragmatic semantic, proactively throwing an <see cref="InvalidOperationException"/> if cache invalidation is attempted on an immutable data layer.
        /// </description>
        /// </item>
        /// </list>
        /// </para>
        /// </remarks>
        private MatrixData(Scale2D scale, IList<T[]> trustedFrames,
            List<List<double>> minValueList, List<List<double>> maxValueList)
            : this(scale.XCount, scale.YCount, scale)
        {
            if (trustedFrames == null || minValueList == null || maxValueList == null)
                throw new ArgumentNullException("Arguments cannot be null for this constructor.");

            if (minValueList.Count != maxValueList.Count || minValueList.Count != trustedFrames.Count)
                throw new ArgumentException($"Count mismatch among arguments: trustedFrames.Count = {trustedFrames.Count}, minValueList.Count = {minValueList.Count}, maxValueList.Count = {maxValueList.Count}");

            _arrayList = trustedFrames;
            int frameCount = trustedFrames.Count;

            if (trustedFrames is IFrameKeyProvider<T> fkp)
            {
                for (int i = 0; i < frameCount; i++)
                {
                    var key = fkp.GetKey(i);
                    if (!_valueRangeMap.ContainsKey(key))
                    {
                        _valueRangeMap[key] = new ValueRange(minValueList![i], maxValueList![i]);
                    }
                }
            }
            else
            {
                for (int i = 0; i < frameCount; i++)
                {
                    var key = trustedFrames[i];
                    if (!_valueRangeMap.ContainsKey(key))
                    {
                        _valueRangeMap[key] = new ValueRange(minValueList![i], maxValueList![i]);
                    }
                }
            }

            GetInternalArray = _arrayList switch
            {
                IWritableFrameProvider<T> writableFrameProvider => (frameIndex, needsInvalidate) =>
                {
                    if (!needsInvalidate)
                        return _arrayList[frameIndex]; //Readonly access without invalidation
                    else //needsInvalidate
                    {
                        if (_arrayList.IsReadOnly)
                            throw new InvalidOperationException("Cannot invalidate a read-only frame provider. Invalidation is only supported for writable providers.");

                        Invalidate(frameIndex); // Ensure min/max will be recalculated in case data is modified
                        return writableFrameProvider.GetWritableArray(frameIndex); // Dirty flag will be set in the frame
                    }
                }
                ,
                _ => (frameIndex, needsInvalidate) => //InMemory 
                {
                    if (needsInvalidate)
                    {
                        if (!_arrayList.IsReadOnly)
                            Invalidate(frameIndex); // Ensure min/max will be recalculated in case data is modified
                        else
                            throw new InvalidOperationException("Cannot invalidate a read-only frame provider. Invalidation is only supported for writable providers.");
                    }
                    return _arrayList[frameIndex];
                }
            };
            Dimensions = new DimensionStructure(this);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="MatrixData{T}"/> class from a memory-mapped file (MMF) backed virtual frame list.
        /// Configures read-only or on-demand writable data access and takes ownership of the resources if necessary.
        /// </summary>
        /// <param name="xnum">The width of the frame (number of pixels in the X direction). Must be greater than 0.</param>
        /// <param name="ynum">The height of the frame (number of pixels in the Y direction). Must be greater than 0.</param>
        /// <param name="frameList">
        /// The backing virtual frame list. Accepts either <see cref="WritableVirtualStrippedFrames{T}"/> or <see cref="VirtualFrames{T}"/>.
        /// If the caller does not own the list, this instance takes over the disposal responsibility to prevent memory leaks.
        /// </param>
        /// <param name="minValueList">
        /// (Optional) A pre-calculated list of minimum values for each frame.
        /// If null, the minimum values will be calculated on-demand when requested.
        /// </param>
        /// <param name="maxValueList">
        /// (Optional) A pre-calculated list of maximum values for each frame.
        /// If null, the maximum values will be calculated on-demand when requested.
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="xnum"/> or <paramref name="ynum"/> is invalid, <paramref name="frameList"/> is empty,
        /// or the number of elements in the provided min/max lists is incorrect.
        /// </exception>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="frameList"/> is null.</exception>
        /// <remarks>
        /// <para>
        /// <b>Architecture Note:</b><br/>
        /// This constructor dynamically injects the <c>GetInternalArray</c> delegate to serve as the unified gateway for internal array access.
        /// This design eliminates the overhead of runtime type checking (the <c>is</c> operator) during data access,
        /// and safely handles dirty flag setting and cache invalidation (<c>Invalidate</c>) transparently when write access is intended.
        /// </para>
        /// </remarks>
        internal MatrixData(int xnum, int ynum,
            VirtualFrames<T> frameList,
            List<List<double>>? minValueList,
            List<List<double>>? maxValueList)
            : this(xnum, ynum, (Scale2D?)null)
        {
            if (frameList == null)
                throw new ArgumentNullException("frameList is null");
            if (frameList.Count == 0)
                throw new ArgumentException("frameList.Count == 0");

            //If no one owns the frameList, this MatrixData needs to take ownership to ensure proper disposal.
            if (!frameList.IsOwned)
            {
                //Delegate ownership to this MatrixData instance.
                //This requires to dispose the frameList when this MatrixData is disposed.
                frameList.IsOwned = true;
                RequiresDisposal = true;
            }

            // Injection of the  preloaded VirtualFrameList instance
            _arrayList = frameList;
            // Dynamic injection of GetInternalArray implementation based on the actual type of frameList. The default GetInternalArray is overwritten.
            GetInternalArray = _arrayList switch
            {
                WritableVirtualStrippedFrames<T> writableVF => (frameIndex, needsInvalidate) =>
                {
                    if (needsInvalidate)
                    {
                        Invalidate(frameIndex); // Ensure min/max will be recalculated in case data is modified
                        return writableVF.GetWritableArray(frameIndex); // Dirty flag will be set in the frame
                    }
                    else
                    {
                        return writableVF[frameIndex]; //Readonly access without invalidation
                    }
                }
                ,
                VirtualFrames<T> readOnlyVF => (frameIndex, needsInvalidate) =>
                {
                    if (needsInvalidate)
                        throw new InvalidOperationException("Cannot invalidate a read-only VirtualFrames. Invalidation is only supported for WritableVirtualStrippedFrames.");
                    return readOnlyVF[frameIndex];
                }
                ,
                _ => throw new ArgumentException($"Invalid type for virtual list: {frameList.GetType()}")
            };

            int frameCount = frameList.Count;
            bool isMinMaxProvided = (minValueList != null && maxValueList != null) &&
                        (minValueList.Count == frameCount) && (maxValueList.Count == frameCount);
            int valueRangeElementNum = isMinMaxProvided ? minValueList![0].Count : 0; //This should be equal to maxValueList[0].Count;
            for (int i = 0; i < frameCount; i++)
            {
                T[] key = frameList.GetKey(i);
                if (!_valueRangeMap.ContainsKey(key))
                {
                    if (isMinMaxProvided)
                    {
                        if (minValueList![i] == null || maxValueList![i] == null)
                            throw new ArgumentException($"Min/max arrays cannot be null at index {i}.");
                        if (minValueList![i].Count != valueRangeElementNum || maxValueList![i].Count != valueRangeElementNum)
                            throw new ArgumentException($"Number of value range elements is invalid at index {i}. minValue num ={minValueList[i].Count}, maxValue num = {maxValueList[i].Count}");

                        _valueRangeMap[key] = new ValueRange(minValueList[i], maxValueList[i]);
                    }
                    else
                    {
                        _valueRangeMap[key] = new ValueRange();
                        //Min/max values will be calculated on demand when GetValueRange is called for this frame.
                    }
                }
                else
                {
                    //Do nothing because it has already been added.
                }
            }
            //Initialize Dimensions with a default Frame axis
            Dimensions = new DimensionStructure(this);
        }

        public static MatrixData<T> CreateAsVirtualFrames(int xnum, int ynum, VirtualFrames<T> frameList)
        {
            return new MatrixData<T>(xnum, ynum, frameList, null, null);
        }
        #endregion
    }
}
