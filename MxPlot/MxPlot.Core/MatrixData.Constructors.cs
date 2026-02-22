using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace MxPlot.Core
{
    //Constructors and memory allocation logic

    public partial class MatrixData<T> : IMatrixData where T : unmanaged
    {
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
    }
}
