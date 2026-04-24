//#define VA_DEBUG
using MxPlot.Core.Processing;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Timers;

namespace MxPlot.Core
{

    /// <summary>
    /// The direction from which to view the volume data. 
    /// This enum is used to specify the axis along which the volume data should be restacked or sliced.
    /// </summary>
    public enum ViewFrom
    {
        /// <summary>
        /// Viewed from the X-axis direction (orthogonal to the YZ plane).
        /// </summary>
        X,

        /// <summary>
        /// Viewed from the Y-axis direction (orthogonal to the XZ plane).
        /// </summary>
        Y,
        /// <summary>
        ///  Viewed from the Z-axis direction (orthogonal to the XY plane). However, similar operations are alos possible using DImensionalOperator.
        /// </summary>
        Z
    }

    /// <summary>
    /// Provides read-only, efficient access to a three-dimensional volume of unmanaged data, enabling slicing,
    /// restacking, and reduction operations along specified axes. 
    /// The instance of the VolumeAccessor<typeparamref name="T"/> is produced by MatrixData<typeparamref name="T"/> via AsVolume() method
    /// </summary>
    /// <remarks>This struct is intended for high-performance scenarios where direct, index-based access to
    /// volumetric data is required. It supports advanced operations such as extracting 2D slices, restacking the volume
    /// from different viewpoints, and reducing data along axes using custom functions. No bounds checking is performed
    /// on indexers for performance reasons; callers must ensure indices are within valid ranges to avoid undefined
    /// behavior. Thread safety is not guaranteed.</remarks>
    /// <typeparam name="T">The type of elements stored in the volume. Must be an unmanaged type.</typeparam>
    public readonly unsafe struct VolumeAccessor<T> 
        where T : unmanaged
    {
        internal readonly IList<T[]> _frames;
        internal readonly int _width;
        internal readonly int _height;
        internal readonly int _depth;
        internal readonly Scale2D _scale;
        internal readonly Axis _axis;

        internal VolumeAccessor(IList<T[]> frames, Scale2D scale, Axis axis)
        {
            _frames = frames;
            _width = scale.XCount;
            _height = scale.YCount;
            _depth = frames.Count;
            _scale = scale;
            _axis = axis;
        }

        /// <summary>
        /// Gets the element at the index [ix, iy, iz] when the MatrixData<typeparamref name="T"/> is viewed as a 3D volume.
        /// z is the frame axis.
        /// </summary>
        /// <remarks>No bounds checking is performed on the indices for performance reasons. Supplying
        /// indices outside the valid range may result in undefined behavior.</remarks>
        /// <param name="ix">The zero-based index along the X-axis of the element to retrieve.</param>
        /// <param name="iy">The zero-based index along the Y-axis of the element to retrieve.</param>
        /// <param name="iz">The zero-based index along the Z-axis of the element to retrieve.</param>
        /// <returns>The element of type T located at the specified (ix, iy, iz) position.</returns>
        public T this[int ix, int iy, int iz]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // 境界チェックが必要ならここに追加（今回はパフォーマンス優先で省略、あるいはDebugのみ推奨）
                // if ((uint)x >= _width || (uint)y >= _height || (uint)z >= _depth) throw ...

                return _frames[iz][iy * _width + ix];
            }
        }

        // =================================================================
        // 1. Restack: 3D -> 3D (Reorganize for viewpoint change)
        // =================================================================
        /// <summary>
        /// Restack the volume data to view from the specified direction. 
        /// <para>Note: The deep copy of the entire data from the origianl MatrixData<typeparamref name="T"/></para>    
        /// </summary>
        /// <param name="direction">The axis along which the restack is created.</param>
        /// 
        public MatrixData<T> Restack(ViewFrom direction)
        {
            return direction switch
            {
                ViewFrom.X => CreateStackFromViewX(), // Xから見る (YZ積層)
                ViewFrom.Y => CreateStackFromViewY(), // Yから見る (XZ積層)
                ViewFrom.Z => CreateStackFromViewZ(),
                _ => throw new ArgumentException()    // Enumで制限しているため到達しない
            };
        }



        // =================================================================
        // 2. Slice: 3D -> 2D (Extract single slice)
        // =================================================================
        /// <summary>
        /// Create a two-dimensional slice (XZ or YZ plane) from the MatrixData<typeparamref name="T"/> with a single Frame axis.
        /// </summary>
        /// <param name="axis">The axis along which to slice the matrix. Specifies whether to extract a slice parallel to the X or Y axis.</param>
        /// <param name="index">The zero-based index at which to extract the slice. Must be within the valid range for the specified axis.</param>
        /// <returns>A two-dimensional matrix representing the extracted slice at the specified index along the given axis.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when the specified index is less than zero or greater than or equal to the size of the matrix along
        /// the selected axis.</exception>
        /// <exception cref="ArgumentException">Thrown when the specified axis is not a valid value of the ViewFrom enumeration.</exception>
        public MatrixData<T> SliceAt(ViewFrom axis, int index)
        {
            
            int maxLimit = (axis == ViewFrom.X) ? _width : _height;
            if (index < 0 || index >= maxLimit)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be between 0 and {maxLimit - 1}");


            return axis switch
            {
                ViewFrom.Y => SliceY(index),
                ViewFrom.X => SliceX(index),
                ViewFrom.Z => SliceZ(index),
                _ => throw new ArgumentException()
            };
        }

        

        // =================================================================
        // Implementations: Slice (Private)
        // =================================================================

        private MatrixData<T> SliceZ(int iz)
        {
            var m = new MatrixData<T>(_width, _height, _frames[iz]);
            m.SetXYScale(_scale.XMin, _scale.XMax, _scale.YMin, _scale.YMax);
            return m;
        }


        /// <summary>
        /// Slice Y to get XZ Plane (as a different data instance)
        /// </summary>
        /// <param name="y"></param>
        /// <returns></returns>
        private MatrixData<T> SliceY(int y)
        {
            int outW = _width;
            int outH = _depth;
            var result = new T[outW * outH];

            var width = _width;
            var frames = _frames;

            fixed (T* resBase = result)
            {
                nint resPtrAddr = (nint)resBase;
                Parallel.For(0, _depth, z =>
                {
                    // BlockCopy的なSpanコピー
                    frames[z].AsSpan().Slice(y * width, width)
                        .CopyTo(new Span<T>((T*)resPtrAddr + z * outW, outW));
                });
            }
            var md = new MatrixData<T>(outW, outH, result);
            md.SetXYScale(_scale.XMin, _scale.XMax, _axis.Min, _axis.Max);
            md.XUnit = _scale.XUnit;
            md.YUnit = _axis.Unit;
            return md;
        }


        // Slice X (YZ Plane, Transposed visualization)
        private MatrixData<T> SliceX(int x)
        {
            int outW = _height;
            int outH = _depth;
            var result = new T[outW * outH];

            var width = _width;
            var height = _height;
            var frames = _frames;

            fixed (T* resBase = result)
            {
                nint resPtrAddr = (nint)resBase;
                Parallel.For(0, _depth, z =>
                {
                    fixed (T* srcBase = frames[z])
                    {
                        T* resPtr = (T*)resPtrAddr + z * outW;
                        T* srcPtr = srcBase + x;
                        int stride = width;
                        for (int y = 0; y < height; y++)
                        {
                            resPtr[y] = *srcPtr;
                            srcPtr += stride;
                        }
                    }
                });
            }
            var md = new MatrixData<T>(outW, outH, result);
            md.SetXYScale(_scale.YMin, _scale.YMax, _axis.Min, _axis.Max);
            md.XUnit = _scale.YUnit;
            md.YUnit = _axis.Unit;
            return md;
        }


        /// <summary>
        /// Simultaneously extracts XZ (SliceY) and YZ (SliceX) planes at the intersection of (x, y) in a single unified pass.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method is optimized for high-throughput data access by traversing the Z-axis once, 
        /// significantly reducing <see cref="VirtualFrameList"/> overhead and cache misses compared to discrete slice operations.
        /// </para>
        /// <para>
        /// It is designed for zero-allocation performance through destination buffer reuse and 
        /// allows granular concurrency control to prevent thread contention in nested parallel execution.
        /// </para>
        /// </remarks>
        /// <param name="x">The X-coordinate; defines the vertical plane (YZ) to be extracted.</param>
        /// <param name="y">The Y-coordinate; defines the horizontal plane (XZ) to be extracted.</param>
        /// <param name="numThreads">
        /// The degree of parallelism. 
        /// Set to <c>1</c> for sequential execution (to avoid contention when the caller is already parallelized), 
        /// or <c>-1</c> to utilize all available logical cores.
        /// </param>
        /// <param name="dstXZ">Optional destination <see cref="IMatrixData"/> to receive the XZ plane data.</param>
        /// <param name="dstXZIndex">Starting index within the <paramref name="dstXZ"/> buffer.</param>
        /// <param name="dstYZ">Optional destination <see cref="IMatrixData"/> to receive the YZ plane data.</param>
        /// <param name="dstYZIndex">Starting index within the <paramref name="dstYZ"/> buffer.</param>
        /// <returns>A tuple of <see cref="MatrixData{T}"/> wrapping the extracted orthogonal planes.</returns>
        public (MatrixData<T> XZ, MatrixData<T> YZ) SliceOrthogonal(int x, int y, int numThreads = -1, 
            IMatrixData? dstXZ = null, int dstXZIndex = 0, IMatrixData? dstYZ = null, int dstYZIndex = 0)
        {

#if VA_DEBUG
            var sw = Stopwatch.StartNew();
            var sb = new StringBuilder();
            long lap = 0;
            sb.AppendLine($"[SliceOrthogonal] Start slicing at (x={x}, y={y}), numThreads={numThreads}");
            sb.Append("[SliceOrthogonal] Initialized:");
#endif 
            if (x < 0 || x >= _width) throw new ArgumentOutOfRangeException(nameof(x));
            if (y < 0 || y >= _height) throw new ArgumentOutOfRangeException(nameof(y));

            int xzOutW = _width;
            int xzOutH = _depth;
            var resultXZ = (dstXZ is MatrixData<T> xz) ? xz.GetArray(dstXZIndex) : null;
            if (resultXZ == null || resultXZ.Length < xzOutW * xzOutH)    
                resultXZ = new T[xzOutW * xzOutH];
          
            int yzOutW = _height;
            int yzOutH = _depth;
            var resultYZ = (dstYZ is MatrixData<T> yz) ? yz.GetArray(dstYZIndex) : null;
            if (resultYZ == null || resultYZ.Length < yzOutW * yzOutH)
                resultYZ = new T[yzOutW * yzOutH];

            var width = _width;
            var height = _height;
            var frames = _frames;

            unsafe
            {
                fixed (T* resXZBase = resultXZ)
                fixed (T* resYZBase = resultYZ)
                {
                    T* pXZ = resXZBase;
                    T* pYZ = resYZBase;
                    int remainder = height % 4; 
                    int mainLoopCount = height - remainder;
                    void Proc(int iz)
                    {
                        // ========================================================
                        // Access to the original data only once per frame, minimizing overhead of VirtualFrameList and improving cache locality.
                        // ========================================================
                        T[] frame = frames[iz];

                        // --- 1. XZ plane extraction (y-th row copy) ---
                        frame.AsSpan(y * width, width)
                             .CopyTo(new Span<T>(pXZ+ iz * xzOutW, xzOutW));

                        // --- 2. YZ plane extraction (x-th column copy) ---
                        fixed (T* srcBase = frame)
                        {
                            T* resYZPtr = pYZ + iz * yzOutW;
                            T* srcPtr = srcBase + x;
                            int stride = width;
                            // Unroll loop for better performance (if height is large enough)
                            for (int iy = 0; iy < mainLoopCount; iy += 4)
                            {
                                resYZPtr[iy] = *srcPtr;
                                resYZPtr[iy + 1] = *(srcPtr + stride);
                                resYZPtr[iy + 2] = *(srcPtr + 2 * stride);
                                resYZPtr[iy + 3] = *(srcPtr + 3 * stride);
                                srcPtr += 4 * stride;
                            }
                            for (int iy = mainLoopCount; iy < height; iy++)
                            {
                                resYZPtr[iy] = *srcPtr;
                                srcPtr += stride;
                            }
                        }
                    }

#if VA_DEBUG
                    lap = sw.ElapsedMilliseconds;
                    sb.Append($"{lap} ms, Loop:");
#endif 
                    if (numThreads > 1 || numThreads < 0)
                    {
                        Parallel.For(0, _depth, new ParallelOptions() { MaxDegreeOfParallelism = numThreads }, iz =>
                        {
                            Proc(iz);
                        });
                    }
                    else
                    {
                        for(int iz = 0; iz < _depth; iz++)
                        {
                            Proc(iz);
                        }
                    }
                }
            }

#if VA_DEBUG
            lap = sw.ElapsedMilliseconds - lap;
            sb.Append($"{lap} ms, Create date:");
#endif 

            var mdYZ = new MatrixData<T>(yzOutW, yzOutH, resultYZ);
            mdYZ.SetXYScale(_scale.YMin, _scale.YMax, _axis.Min, _axis.Max);
            mdYZ.XUnit = _scale.YUnit;
            mdYZ.YUnit = _axis.Unit;

            var mdXZ = new MatrixData<T>(xzOutW, xzOutH, resultXZ);
            mdXZ.SetXYScale(_scale.XMin, _scale.XMax, _axis.Min, _axis.Max);
            mdXZ.XUnit = _scale.XUnit;
            mdXZ.YUnit = _axis.Unit;

#if VA_DEBUG
            lap = sw.ElapsedMilliseconds - lap;
            sb.Append($"{lap} ms, Total:{sw.ElapsedMilliseconds} ms");
            Trace.WriteLine(sb.ToString());
#endif 

            return (mdXZ, mdYZ);
        }


        // =================================================================
        // Implementations: Restack (Private)
        // =================================================================

        // ViewFrom.X: View from X direction = Stack YZ planes (Width=Y, Height=Z) along X
        private MatrixData<T> CreateStackFromViewX()
        {
            int newDepth = _width;
            int newWidth = _height; // Y
            int newHeight = _depth; // Z

            var newFrames = new List<T[]>(newDepth);
            for (int i = 0; i < newDepth; i++) newFrames.Add(new T[newWidth * newHeight]);

            var depth = _depth;
            var width = _width;
            var frames = _frames;
            Parallel.For(0, newDepth, x =>
            {
                T[] dstFrame = newFrames[x];

                fixed (T* dstBase = dstFrame)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        fixed (T* srcBase = frames[z])
                        {
                            T* dstPtr = dstBase + z * newWidth; // Destination: z-th row
                            // Scan and copy column x of src vertically (Y direction)
                            for (int y = 0; y < newWidth; y++)
                            {
                                dstPtr[y] = srcBase[y * width + x];
                            }
                        }
                    }
                }
            });
            // Scale: Y, Z (Depth: X)
            var md = new MatrixData<T>(newWidth, newHeight, newFrames);
            md.SetXYScale(_scale.YMin, _scale.YMax, _axis.Min, _axis.Max);
            md.XUnit = _scale.YUnit;
            md.YUnit = _axis.Unit;
            md.DefineDimensions(new Axis(_scale.XCount, _scale.XMin, _scale.XMax, "X", _scale.XUnit));
            return md;
        }

        // ViewFrom.Y: View from Y direction = Stack XZ planes (Width=X, Height=Z) along Y
        private MatrixData<T> CreateStackFromViewY()
        {
            int newDepth = _height;
            int newWidth = _width;  // X
            int newHeight = _depth; // Z

            var newFrames = new List<T[]>(newDepth);
            for (int i = 0; i < newDepth; i++) newFrames.Add(new T[newWidth * newHeight]);

            var depth = _depth;
            var width = _width;
            var frames = _frames;

            Parallel.For(0, _depth, z =>
            {
                T[] srcFrame = frames[z];
                int rowBytes = width * Unsafe.SizeOf<T>();

                // Copy one row (X) of source data to z-th row of corresponding Y frame
                for (int y = 0; y < newDepth; y++)
                {
                    Buffer.BlockCopy(
                        srcFrame, y * width * Unsafe.SizeOf<T>(),
                        newFrames[y], z * width * Unsafe.SizeOf<T>(),
                        rowBytes
                    );
                }
            });
            // Scale: X, Z (Depth: Y)
            var md = new MatrixData<T>(newWidth, newHeight, newFrames);
            md.SetXYScale(_scale.XMin, _scale.XMax, _axis.Min, _axis.Max);
            md.XUnit = _scale.XUnit;
            md.YUnit = _axis.Unit;
            md.DefineDimensions(new Axis(_scale.YCount, _scale.YMin, _scale.YMax, "Y", _scale.YUnit));
            return md;
        }

        private MatrixData<T> CreateStackFromViewZ()
        {
            var newFrames = new List<T[]>(_frames.Count);
            for (int i = 0; i < _frames.Count; i++)
            {
                // 仮想リスト(MMF)の場合、ここでデコードが発生
                T[] src = _frames[i];

                // 新しい実体配列にコピー
                T[] copy = new T[src.Length];
                Array.Copy(src, copy, src.Length);
                newFrames.Add(copy);
            }

            //var m = new MatrixData<T>(_width, _height, _frames.ConvertAll(arr => arr.AsSpan().ToArray()));
            var m = new MatrixData<T>(_width, _height, newFrames);
            m.SetXYScale(_scale.XMin, _scale.XMax, _scale.YMin, _scale.YMax);
            m.XUnit = _scale.XUnit;
            m.YUnit = _scale.YUnit;
            m.DefineDimensions(_axis);
            return m;
        }

        /// <summary>
        /// Represents a method that computes a projected value of type T based on grid coordinates, spatial positions,
        /// an axis, and a vector of values.
        /// </summary>
        /// <typeparam name="T">The type of the value returned by the projection function.</typeparam>
        /// <param name="ix">The zero-based index of the grid cell along the X-axis.</param>
        /// <param name="iy">The zero-based index of the grid cell along the Y-axis.</param>
        /// <param name="x">The X-coordinate in the spatial reference system.</param>
        /// <param name="y">The Y-coordinate in the spatial reference system.</param>
        /// <param name="zaxis">The axis along which the projection is performed.</param>
        /// <param name="vector">A read-only span containing the values to be projected. The contents and length of the span must be
        /// compatible with the projection logic.</param>
        /// <returns>A value of type T representing the result of the projection at the specified coordinates and axis.</returns>
        public delegate T VolumeReduceFunc(int ix, int iy, double x, double y, Axis zaxis, ReadOnlySpan<T> vector);


        /// <summary>
        /// Reduces the matrix along the specified axis using the provided reduction function.
        /// The resultant <see cref="MatrixData{T}"/> is 2D, having collapsed the specified axis.
        /// </summary>
        /// <param name="axis">The axis along which to perform the reduction. Specify <see cref="ViewFrom.X"/> to reduce across rows, or
        /// <see cref="ViewFrom.Y"/> to reduce across columns.</param>
        /// <param name="op">The reduction function to apply to the elements along the specified axis.</param>
        /// <returns>A new <see cref="MatrixData{T}"/> instance containing the result of the reduction operation.</returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="axis"/> is not a valid value of <see cref="ViewFrom"/>.</exception>
        public MatrixData<T> ReduceAlong(ViewFrom axis, VolumeReduceFunc op)
        {
            return axis switch
            {
                ViewFrom.X => ReduceAlongX(op), // Collapse X direction (rows)
                ViewFrom.Y => ReduceAlongY(op), // Collapse Y direction (columns)
                ViewFrom.Z => ReduceAlongZ(op),
                _ => throw new ArgumentException()
            };
        }

        /// <summary>
        /// X axis reduction (to YZ plane) with zero allocation
        /// </summary>
        /// <param name="op"></param>
        /// <returns></returns>
        private MatrixData<T> ReduceAlongX(VolumeReduceFunc op)
        {
            int outW = _height;
            int outH = _depth;
            var result = new T[outW * outH];

            var width = _width;
            var height = _height;
            var frames = _frames;

            var zmin = _axis.Min;
            var zstep = _axis.Step;
            var ymin = _scale.YMin;
            var ystep = _scale.YStep;
            var xaxis = new Axis(_scale.XCount, _scale.XMin, _scale.XMax, "X", _scale.XUnit);

            Parallel.For(0, _depth, iz =>
            {
                T[] srcFrame = frames[iz]; // 1. Frame reference (fast)
                double z = iz * zstep + zmin;
                int dstBase = iz * outW;

                for (int iy = 0; iy < height; iy++)
                {
                    // 2. Row data is contiguous in memory, slice as Span directly (no copy)
                    ReadOnlySpan<T> vector = srcFrame.AsSpan(iy * width, width);
                    var y = iy * ystep + ymin;
                    // 3. Pass to user function
                    result[dstBase + iy] = op(iy, iz, y, z, xaxis, vector);
                }

            });

            var md = new MatrixData<T>(outW, outH, result);
            md.SetXYScale(_scale.YMin, _scale.YMax, _axis.Min, _axis.Max);
            md.XUnit = _scale.YUnit;
            md.YUnit = _axis.Unit;
            return md;
        }

        /// <summary>
        /// Y axis reduction (to XZ plane) with ArrayPool buffer reuse, resulting in zero allocation.
        /// </summary>
        /// <param name="op"></param>
        /// <returns></returns>
        private MatrixData<T> ReduceAlongY(VolumeReduceFunc op)
        {
            int outW = _width;
            int outH = _depth;
            var result = new T[outW * outH];
            int vecLen = _height;

            var width = _width;
            var height = _height;
            var frames = _frames;

            var zmin = _axis.Min;
            var zstep = _axis.Step;
            var xmin = _scale.XMin;
            var xstep = _scale.XStep;
            var yaxis = new Axis(_scale.YCount, _scale.YMin, _scale.YMax, "Y", _scale.YUnit);

            // When using Parallel, each thread needs its own buffer,
            // so Rent/Return within the loop is safe and efficient
            Parallel.For(0, _depth, iz =>
            {
                T[] srcFrame = frames[iz];
                double z = iz * zstep + zmin;

                // Optimization: Rent buffer from shared pool to prevent GC allocation
                T[] poolArray = ArrayPool<T>.Shared.Rent(vecLen);

                // Rented array may be larger than vecLen, so create Span with exact size
                Span<T> gatherBuffer = poolArray.AsSpan(0, vecLen);

                try
                {
                    // Loop in X direction (output image width)
                    for (int ix = 0; ix < width; ix++)
                    {
                        // 1. Gather: Collect strided data into buffer
                        // This is the only overhead, but surprisingly fast due to CPU cache
                        for (int iy = 0; iy < vecLen; iy++)
                        {
                            gatherBuffer[iy] = srcFrame[iy * width + ix];
                        }

                        var x = ix * xstep + xmin;
                        // 2. Pass to user function (now contiguous data)
                        result[iz * outW + ix] = op(ix, iz, x, z, yaxis, gatherBuffer);
                    }
                }
                finally
                {
                    // Always return to pool
                    ArrayPool<T>.Shared.Return(poolArray);
                }
            });
            var md = new MatrixData<T>(outW, outH, result);
            md.SetXYScale(_scale.XMin, _scale.XMax, _axis.Min, _axis.Max);
            md.XUnit = _scale.XUnit;
            md.YUnit = _axis.Unit;
            return md;
        }

        private MatrixData<T> ReduceAlongZ(VolumeReduceFunc op)
        {
            var width = _width;
            var height = _height;
            var frames = _frames;
            var zaxis = _axis;
            var depth = _depth;
            double xstep = _scale.XStep;
            double xmin = _scale.XMin;
            double ystep = _scale.YStep;
            double ymin = _scale.YMin;
            var result = new T[width * height];
            // Y方向(行)で並列化
            Parallel.For(0, _height, iy =>
            {
                // スレッドごとのZバッファ確保
                T[] poolArray = ArrayPool<T>.Shared.Rent(depth);
                Span<T> gatherBuffer = poolArray.AsSpan(0, depth);

                try
                {
                    double y = iy * ystep + ymin;
                    int rowOffset = iy * width;

                    for (int ix = 0; ix < width; ix++)
                    {
                        // 1. Gather: 全フレームの (ix, iy) を集める
                        // List<T[]> なので、frames[iz][pixel] アクセスになる
                        int pixelIndex = rowOffset + ix;
                        for (int iz = 0; iz < depth; iz++)
                        {
                            gatherBuffer[iz] = frames[iz][pixelIndex];
                        }
                        double x = ix * xstep + xmin;
                        // 2. Reduce実行
                        result[pixelIndex] = op(ix, iy, x, y, zaxis, gatherBuffer);
                    }
                }
                finally
                {
                    ArrayPool<T>.Shared.Return(poolArray);
                }
            });
            var md = new MatrixData<T>(width, height, result);
            md.SetXYScale(_scale.XMin, _scale.XMax, _scale.YMin, _scale.YMax); // X-Y
            md.XUnit = _scale.XUnit;
            md.YUnit = _scale.YUnit;
            return md;
        }

    }

}
