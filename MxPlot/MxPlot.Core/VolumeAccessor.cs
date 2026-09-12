//#define VA_DEBUG
// Diagnostic: stage timings (vessel creation, banding, per-100-frame write timeline, gen2 GC
// events) for Restack's X/Y Virtual-output path, printed straight to the console (not Debug/Trace,
// which are stripped or listener-less in a published Release build) so they show up when running a
// `dotnet publish` build from a terminal. Off by default -- uncomment to re-enable if a future
// Restack-latency report needs the same investigation (see VolumeAccessor.Restack's XML doc and the
// banded write-out path in CreateStackFromViewX/Y for what this traced: the original catastrophic
// scatter-write bug is fixed; a residual multi-second stall partway through very large (multi-GB)
// writes on some machines was traced to a fixed wall-clock point regardless of write pattern or GC,
// consistent with an SSD/OS write-cache characteristic outside this code's control).
//#define RESTACK_DIAG
using MxPlot.Core.IO;
using MxPlot.Core.Processing;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
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
        /// <param name="outputMode">
        /// In-memory or memory-mapped output; <see cref="LoadingMode.Auto"/> defers to
        /// <see cref="VirtualPolicy"/> based on the restacked volume's size. When resolved to
        /// <see cref="LoadingMode.Virtual"/>, <c>X</c>/<c>Y</c> (a genuine transpose) write the MMF
        /// vessel one whole output frame at a time -- never one scattered row at a time -- by
        /// buffering a RAM-bounded band of output frames in managed memory (built by parallelizing
        /// over source frames, one full sweep per band) before handing each completed frame to the
        /// vessel as a single contiguous write; see <see cref="CreateStackFromViewX"/>'s Virtual
        /// branch for why this matters (writing scattered individual rows directly into a freshly
        /// created multi-GB sparse MMF was measured to be catastrophically slow, apparently from
        /// per-page-fault cost, and much worse on some platforms/filesystems than others). The
        /// trade-off is that a source frame is read/decoded once per band rather than once total.
        /// <c>Z</c> (already frame-ordered) streams one frame at a time either way, so its peak
        /// memory stays at one frame regardless of <paramref name="outputMode"/>.
        /// </param>
        /// <param name="progress">
        /// Optional progress reporter. Reports <c>-N</c> once, then <c>0 ... N-1</c> as work
        /// completes. For <c>Z</c>, and for <c>X</c>/<c>Y</c> resolved to in-memory output, <c>N</c> is
        /// the source frame count. For <c>X</c>/<c>Y</c> resolved to Virtual output, <c>N</c> is
        /// (source-frame-count &#215; band-count) + output-frame-count (see <paramref
        /// name="outputMode"/>): each band re-sweeps every source frame (the first term), and every
        /// output frame is written to the vessel exactly once across all bands (the second term) --
        /// both phases report progress, so <c>N</c> is only known once the band size has been chosen
        /// and can be larger than the source frame count. Counting only the sweep phase would let
        /// progress reach "done" while the (often much slower) per-band disk write was still running.
        /// </param>
        /// <param name="cancellationToken">Cancels the operation.</param>
        public MatrixData<T> Restack(ViewFrom direction,
            LoadingMode outputMode = LoadingMode.Auto,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return direction switch
            {
                ViewFrom.X => CreateStackFromViewX(outputMode, progress, cancellationToken), // YZ stack along X
                ViewFrom.Y => CreateStackFromViewY(outputMode, progress, cancellationToken), // XZ stack along Y
                ViewFrom.Z => CreateStackFromViewZ(outputMode, progress, cancellationToken), // XY stack along Z
                _ => throw new ArgumentException()
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
        /// <param name="dst">
        /// Optional destination to receive the slice, mirroring <see cref="SliceOrthogonal"/>.
        /// When its frame at <paramref name="dstIndex"/> is large enough the pixels are written
        /// straight into it and the returned wrapper shares that buffer, so a caller repeatedly
        /// re-slicing into a displayed frame allocates nothing and keeps the same instance on
        /// screen. Ignored for <see cref="ViewFrom.Z"/>, which already returns a frame by reference.
        /// </param>
        /// <param name="dstIndex">Frame index within <paramref name="dst"/> to write into.</param>
        public MatrixData<T> SliceAt(ViewFrom axis, int index, IMatrixData? dst = null, int dstIndex = 0)
        {
            
            int maxLimit = (axis == ViewFrom.X) ? _width : _height;
            if (index < 0 || index >= maxLimit)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Index must be between 0 and {maxLimit - 1}");


            return axis switch
            {
                ViewFrom.Y => SliceY(index, dst, dstIndex),
                ViewFrom.X => SliceX(index, dst, dstIndex),
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
        private MatrixData<T> SliceY(int y, IMatrixData? dst = null, int dstIndex = 0)
        {
            int outW = _width;
            int outH = _depth;
            var result = (dst is MatrixData<T> d) ? d.GetArray(dstIndex) : null;
            if (result == null || result.Length < outW * outH)
                result = new T[outW * outH];

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
        private MatrixData<T> SliceX(int x, IMatrixData? dst = null, int dstIndex = 0)
        {
            int outW = _height;
            int outH = _depth;
            var result = (dst is MatrixData<T> d) ? d.GetArray(dstIndex) : null;
            if (result == null || result.Length < outW * outH)
                result = new T[outW * outH];

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
        private MatrixData<T> CreateStackFromViewX(LoadingMode outputMode, IProgress<int>? progress, CancellationToken ct)
        {
#if RESTACK_DIAG
            var diagSw = Stopwatch.StartNew();
#endif
            int newDepth = _width;
            int newWidth = _height; // Y
            int newHeight = _depth; // Z
            int depth = _depth;

            var width = _width;
            var frames = _frames;
            var newAxis = new Axis(_scale.XCount, _scale.XMin, _scale.XMax, "X", _scale.XUnit);
            // VolumeAccessor<T> is a readonly struct -- the closures below can't capture `this`
            // implicitly (via _scale/_axis field access), so copy what they need into locals first.
            var scale = _scale;
            var axis = _axis;

            long estimatedBytes = (long)newWidth * newHeight * newDepth * Unsafe.SizeOf<T>();
            // frameCount: 0 -- ThresholdFrames targets file formats where per-frame IFD/header
            // parsing overhead makes many-small-frames costly to load InMemory (see VirtualPolicy's
            // own remarks); that has nothing to do with VolumeAccessor's in-memory scatter/copy, and
            // newDepth here is just the source's spatial pixel count along one axis, not a proxy for
            // memory cost -- a perfectly ordinary 1024-wide image already exceeds the default 1000
            // threshold, which was forcing Virtual (and its per-row MMF overhead) on outputs a few
            // tens of MB in size. ThresholdBytes alone is the right signal for Restack.
            var resolved = VirtualPolicy.Resolve(outputMode, estimatedBytes, frameCount: 0);

            long completed = 0;

            if (resolved == LoadingMode.Virtual)
            {
                // Writing straight into the vessel one row at a time (frameIndex = x, up to newDepth
                // widely-separated output frames touched per source z) was measured to be
                // catastrophically slow on real data: it scatters writes across the WHOLE multi-GB
                // sparse output file, and the first source frame to touch each of those far-apart
                // pages pays a page-fault cost that (at least on macOS) runs single-digit milliseconds
                // PER ROW -- ~22 seconds just for the first of several hundred source frames on a
                // production dataset, vastly worse than the same scatter pattern on Windows. The fix
                // is to stop scattering writes across the file at all: buffer a RAM-sized band of
                // output frames in managed memory (built by parallelizing over source frames, exactly
                // like the in-memory branch below), then hand each completed frame to the vessel as
                // ONE contiguous whole-frame write -- the same known-cheap sequential pattern
                // CreateStackFromViewZ already uses. Trade-off: each band needs its own full sweep
                // over every source frame, so a source frame is read/decoded once per band instead of
                // once total; ComputeBandSize sizes bands generously (by available RAM) to keep the
                // number of bands -- and so the redundant re-reads -- small.
#if RESTACK_DIAG
                Console.WriteLine($"[Restack:X] Virtual resolved, estimatedBytes={estimatedBytes:N0}, depth={depth}, newDepth={newDepth} @ {diagSw.ElapsedMilliseconds} ms");
#endif
                var vessel = MatrixDataSerializer.CreateTempVessel<T>(newWidth, newHeight, newDepth);
#if RESTACK_DIAG
                Console.WriteLine($"[Restack:X] CreateTempVessel returned @ {diagSw.ElapsedMilliseconds} ms");
#endif
                try
                {
                    int bandSize = ComputeBandSize(newWidth, newHeight, newDepth);
                    int bandCountTotal = (newDepth + bandSize - 1) / bandSize;
#if RESTACK_DIAG
                    Console.WriteLine($"[Restack:X] Banded Virtual write: bandSize={bandSize} frames, {bandCountTotal} band(s) @ {diagSw.ElapsedMilliseconds} ms");
#endif
                    // See CreateStackFromViewY's Virtual branch for why totalWorkUnits includes both
                    // the gather phase (per source frame) and the vessel write-out phase (per output
                    // frame) -- omitting the latter made progress hit "done" while the actual
                    // multi-GB disk write was still running silently.
                    long totalWorkUnits = (long)depth * bandCountTotal + newDepth;
                    progress?.Report((int)-totalWorkUnits);
                    long workDone = 0;
#if RESTACK_DIAG
                    // See CreateStackFromViewY's Virtual branch for why this exists -- correlating a
                    // reproducible mid-run stall with a blocking gen2/LOH GC pass.
                    int lastGen2 = GC.CollectionCount(2);
#endif

                    for (int bandStart = 0; bandStart < newDepth; bandStart += bandSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        int bandCount = Math.Min(bandSize, newDepth - bandStart);
                        var bandFrames = new T[bandCount][];
                        for (int i = 0; i < bandCount; i++) bandFrames[i] = new T[newWidth * newHeight];

                        Parallel.For(0, depth, new ParallelOptions { CancellationToken = ct }, z =>
                        {
                            fixed (T* srcBase = frames[z]) // decode/fetch happens here on a cache miss
                            {
                                for (int i = 0; i < bandCount; i++)
                                {
                                    // See CreateStackFromViewY's Virtual branch for why this needs its
                                    // own check, not just ParallelOptions.CancellationToken's
                                    // between-iteration polling.
                                    ct.ThrowIfCancellationRequested();
                                    int x = bandStart + i;
                                    fixed (T* dstBase = bandFrames[i])
                                    {
                                        T* dstPtr = dstBase + z * newWidth; // Destination: z-th row
                                        T* srcPtr = srcBase + x;
                                        // Scan and copy column x of src vertically (Y direction)
                                        for (int y = 0; y < newWidth; y++)
                                        {
                                            dstPtr[y] = *srcPtr;
                                            srcPtr += width;
                                        }
                                    }
                                }
                            }
                            progress?.Report((int)(Interlocked.Increment(ref workDone) - 1));
#if RESTACK_DIAG
                            int g2a = GC.CollectionCount(2);
                            if (g2a != lastGen2)
                            {
                                Console.WriteLine($"[Restack:X] GC gen2 {lastGen2}->{g2a} noticed during gather (source frame z={z}) @ {diagSw.ElapsedMilliseconds} ms");
                                lastGen2 = g2a;
                            }
#endif
                        });

                        for (int i = 0; i < bandCount; i++)
                        {
                            ct.ThrowIfCancellationRequested();
#if RESTACK_DIAG
                            long writeStartMs = diagSw.ElapsedMilliseconds;
#endif
                            vessel.WriteDirectly(bandStart + i, bandFrames[i]);
                            bandFrames[i] = null!; // no longer needed -- let the GC reclaim it as we go
                            progress?.Report((int)(Interlocked.Increment(ref workDone) - 1));
#if RESTACK_DIAG
                            int g2b = GC.CollectionCount(2);
                            if (g2b != lastGen2)
                            {
                                Console.WriteLine($"[Restack:X] GC gen2 {lastGen2}->{g2b} noticed while writing output frame {bandStart + i} @ {diagSw.ElapsedMilliseconds} ms");
                                lastGen2 = g2b;
                            }
                            if ((bandStart + i) % 100 == 0)
                            {
                                long writeMs = diagSw.ElapsedMilliseconds - writeStartMs;
                                Console.WriteLine($"[Restack:X] wrote output frame {bandStart + i} (this write: {writeMs} ms) @ {diagSw.ElapsedMilliseconds} ms");
                            }
#endif
                        }
#if RESTACK_DIAG
                        Console.WriteLine($"[Restack:X] Band [{bandStart}, {bandStart + bandCount}) written @ {diagSw.ElapsedMilliseconds} ms");
#endif
                    }
                    vessel.Flush();
#if RESTACK_DIAG
                    Console.WriteLine($"[Restack:X] All bands written, Flush() done @ {diagSw.ElapsedMilliseconds} ms");
#endif
                }
                catch
                {
                    vessel.Dispose();
                    throw;
                }

                var mv = MatrixData<T>.CreateAsVirtualFrames(newWidth, newHeight, vessel);
                // Scale: Y, Z (Depth: X)
                mv.SetXYScale(scale.YMin, scale.YMax, axis.Min, axis.Max);
                mv.XUnit = scale.YUnit;
                mv.YUnit = axis.Unit;
                mv.DefineDimensions(newAxis);
                return mv;
            }

            progress?.Report(-depth);
            var newFrames = new List<T[]>(newDepth);
            for (int i = 0; i < newDepth; i++) newFrames.Add(new T[newWidth * newHeight]);

            // Parallelize over z (source frames), not x (output frames): each source frame must be
            // fetched from `frames` exactly once. The previous x-outer/z-inner nesting read every
            // source frame `newDepth` (= _width) times over -- one redundant fetch per output frame,
            // and for Virtual/MMF-backed data a redundant decode on every one of those, potentially
            // thrashing a cache sized for `_depth` frames. CreateStackFromViewY already parallelizes
            // by z for the same reason; this mirrors it instead of drifting from it. This scatter
            // shape is also why the whole result has to be assembled in `newFrames` before any of it
            // can be written out (see ResolveAndFinish) -- no single output frame (column x) is
            // complete until every z has been visited. (The Virtual branch above sidesteps this by
            // writing straight into the vessel's MMF instead of a managed `newFrames` buffer.)
            Parallel.For(0, depth, new ParallelOptions { CancellationToken = ct }, z =>
            {
                fixed (T* srcBase = frames[z])
                {
                    for (int x = 0; x < newDepth; x++)
                    {
                        // See the Virtual branch above for why this needs its own check, not just
                        // ParallelOptions.CancellationToken's between-iteration polling.
                        ct.ThrowIfCancellationRequested();
                        fixed (T* dstBase = newFrames[x])
                        {
                            T* dstPtr = dstBase + z * newWidth; // Destination: z-th row
                            T* srcPtr = srcBase + x;
                            // Scan and copy column x of src vertically (Y direction)
                            for (int y = 0; y < newWidth; y++)
                            {
                                dstPtr[y] = *srcPtr;
                                srcPtr += width;
                            }
                        }
                    }
                }
                progress?.Report((int)Interlocked.Increment(ref completed) - 1);
            });

            return ResolveAndFinish(newWidth, newHeight, newFrames, outputMode, ct,
                md =>
                {
                    // Scale: Y, Z (Depth: X)
                    md.SetXYScale(scale.YMin, scale.YMax, axis.Min, axis.Max);
                    md.XUnit = scale.YUnit;
                    md.YUnit = axis.Unit;
                    md.DefineDimensions(newAxis);
                });
        }

        // ViewFrom.Y: View from Y direction = Stack XZ planes (Width=X, Height=Z) along Y
        private MatrixData<T> CreateStackFromViewY(LoadingMode outputMode, IProgress<int>? progress, CancellationToken ct)
        {
#if RESTACK_DIAG
            var diagSw = Stopwatch.StartNew();
#endif
            int newDepth = _height;
            int newWidth = _width;  // X
            int newHeight = _depth; // Z
            int depth = _depth;

            var width = _width;
            var frames = _frames;
            var newAxis = new Axis(_scale.YCount, _scale.YMin, _scale.YMax, "Y", _scale.YUnit);
            // VolumeAccessor<T> is a readonly struct -- the closures below can't capture `this`
            // implicitly (via _scale/_axis field access), so copy what they need into locals first.
            var scale = _scale;
            var axis = _axis;

            long estimatedBytes = (long)newWidth * newHeight * newDepth * Unsafe.SizeOf<T>();
            // frameCount: 0 -- see CreateStackFromViewX's identical resolve call for why.
            var resolved = VirtualPolicy.Resolve(outputMode, estimatedBytes, frameCount: 0);

            long completed = 0;

            if (resolved == LoadingMode.Virtual)
            {
                // See CreateStackFromViewX's Virtual branch for the full rationale: writing straight
                // into the vessel row-by-row (frameIndex = y, up to newDepth widely-separated output
                // frames per source z) scatters writes across the whole multi-GB sparse output file,
                // which was measured to be catastrophically slow (worst on macOS). Instead, band
                // output frames in managed memory (parallelized by source frame, exactly like the
                // in-memory branch below -- Y's scatter is already a contiguous row copy, no strided
                // gather needed), then hand each completed frame to the vessel as ONE contiguous
                // whole-frame write.
#if RESTACK_DIAG
                Console.WriteLine($"[Restack:Y] Virtual resolved, estimatedBytes={estimatedBytes:N0}, depth={depth}, newDepth={newDepth} @ {diagSw.ElapsedMilliseconds} ms");
#endif
                var vessel = MatrixDataSerializer.CreateTempVessel<T>(newWidth, newHeight, newDepth);
#if RESTACK_DIAG
                Console.WriteLine($"[Restack:Y] CreateTempVessel returned @ {diagSw.ElapsedMilliseconds} ms");
#endif
                try
                {
                    int bandSize = ComputeBandSize(newWidth, newHeight, newDepth);
                    int bandCountTotal = (newDepth + bandSize - 1) / bandSize;
#if RESTACK_DIAG
                    Console.WriteLine($"[Restack:Y] Banded Virtual write: bandSize={bandSize} frames, {bandCountTotal} band(s) @ {diagSw.ElapsedMilliseconds} ms");
#endif
                    // Work units cover BOTH phases of every band: gathering (one unit per source
                    // frame swept) and the subsequent vessel write-out (one unit per output frame
                    // written). The write-out phase used to report nothing at all, so progress hit
                    // "done" as soon as the (comparatively fast, pure in-memory) gather phase
                    // finished, then sat silently at "done" for however long the actual multi-GB
                    // disk write took -- exactly backwards from what the label promised.
                    long totalWorkUnits = (long)depth * bandCountTotal + newDepth;
                    progress?.Report((int)-totalWorkUnits);
                    long workDone = 0;
                    int sizeOfT = Unsafe.SizeOf<T>();
#if RESTACK_DIAG
                    // Diagnostic only: correlate a mid-run stall with a blocking gen2/LOH GC pass.
                    // Both phases churn large (LOH-sized) arrays -- source frames re-fetched on cache
                    // misses in the gather phase, and populated band buffers dropped as they're
                    // written out in the write-out phase -- so if a reproducible stall lines up with
                    // a gen2 count bump here, that's the cause, not the write pattern itself (already
                    // fixed to be sequential/contiguous).
                    int lastGen2 = GC.CollectionCount(2);
#endif

                    for (int bandStart = 0; bandStart < newDepth; bandStart += bandSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        int bandCount = Math.Min(bandSize, newDepth - bandStart);
                        var bandFrames = new T[bandCount][];
                        for (int i = 0; i < bandCount; i++) bandFrames[i] = new T[newWidth * newHeight];

                        Parallel.For(0, depth, new ParallelOptions { CancellationToken = ct }, z =>
                        {
                            T[] srcFrame = frames[z]; // decode/fetch happens here on a cache miss
                            int rowBytes = width * sizeOfT;
                            for (int i = 0; i < bandCount; i++)
                            {
                                // See CreateStackFromViewX's Virtual branch for why this needs its own
                                // check, not just ParallelOptions.CancellationToken's between-iteration
                                // polling.
                                ct.ThrowIfCancellationRequested();
                                Buffer.BlockCopy(
                                    srcFrame, (bandStart + i) * rowBytes,
                                    bandFrames[i], z * rowBytes,
                                    rowBytes
                                );
                            }
                            progress?.Report((int)(Interlocked.Increment(ref workDone) - 1));
#if RESTACK_DIAG
                            int g2a = GC.CollectionCount(2);
                            if (g2a != lastGen2)
                            {
                                Console.WriteLine($"[Restack:Y] GC gen2 {lastGen2}->{g2a} noticed during gather (source frame z={z}) @ {diagSw.ElapsedMilliseconds} ms");
                                lastGen2 = g2a;
                            }
#endif
                        });

                        for (int i = 0; i < bandCount; i++)
                        {
                            ct.ThrowIfCancellationRequested();
#if RESTACK_DIAG
                            long writeStartMs = diagSw.ElapsedMilliseconds;
#endif
                            vessel.WriteDirectly(bandStart + i, bandFrames[i]);
                            bandFrames[i] = null!; // no longer needed -- let the GC reclaim it as we go
                            progress?.Report((int)(Interlocked.Increment(ref workDone) - 1));
#if RESTACK_DIAG
                            int g2b = GC.CollectionCount(2);
                            if (g2b != lastGen2)
                            {
                                Console.WriteLine($"[Restack:Y] GC gen2 {lastGen2}->{g2b} noticed while writing output frame {bandStart + i} @ {diagSw.ElapsedMilliseconds} ms");
                                lastGen2 = g2b;
                            }
                            if ((bandStart + i) % 100 == 0)
                            {
                                long writeMs = diagSw.ElapsedMilliseconds - writeStartMs;
                                Console.WriteLine($"[Restack:Y] wrote output frame {bandStart + i} (this write: {writeMs} ms) @ {diagSw.ElapsedMilliseconds} ms");
                            }
#endif
                        }
#if RESTACK_DIAG
                        Console.WriteLine($"[Restack:Y] Band [{bandStart}, {bandStart + bandCount}) written @ {diagSw.ElapsedMilliseconds} ms");
#endif
                    }
                    vessel.Flush();
#if RESTACK_DIAG
                    Console.WriteLine($"[Restack:Y] All bands written, Flush() done @ {diagSw.ElapsedMilliseconds} ms");
#endif
                }
                catch
                {
                    vessel.Dispose();
                    throw;
                }

                var mv = MatrixData<T>.CreateAsVirtualFrames(newWidth, newHeight, vessel);
                // Scale: X, Z (Depth: Y)
                mv.SetXYScale(scale.XMin, scale.XMax, axis.Min, axis.Max);
                mv.XUnit = scale.XUnit;
                mv.YUnit = axis.Unit;
                mv.DefineDimensions(newAxis);
                return mv;
            }

            progress?.Report(-depth);
            var newFrames = new List<T[]>(newDepth);
            for (int i = 0; i < newDepth; i++) newFrames.Add(new T[newWidth * newHeight]);

            Parallel.For(0, depth, new ParallelOptions { CancellationToken = ct }, z =>
            {
                T[] srcFrame = frames[z];
                int rowBytes = width * Unsafe.SizeOf<T>();

                // Copy one row (X) of source data to z-th row of corresponding Y frame
                for (int y = 0; y < newDepth; y++)
                {
                    // See CreateStackFromViewX's Virtual branch for why this needs its own check.
                    ct.ThrowIfCancellationRequested();
                    Buffer.BlockCopy(
                        srcFrame, y * width * Unsafe.SizeOf<T>(),
                        newFrames[y], z * width * Unsafe.SizeOf<T>(),
                        rowBytes
                    );
                }
                progress?.Report((int)Interlocked.Increment(ref completed) - 1);
            });

            return ResolveAndFinish(newWidth, newHeight, newFrames, outputMode, ct,
                md =>
                {
                    // Scale: X, Z (Depth: Y)
                    md.SetXYScale(scale.XMin, scale.XMax, axis.Min, axis.Max);
                    md.XUnit = scale.XUnit;
                    md.YUnit = axis.Unit;
                    md.DefineDimensions(newAxis);
                });
        }

        private MatrixData<T> CreateStackFromViewZ(LoadingMode outputMode, IProgress<int>? progress, CancellationToken ct)
        {
            int frameCount = _frames.Count;
            long estimatedBytes = (long)_width * _height * frameCount * Unsafe.SizeOf<T>();
            // frameCount: 0 -- see CreateStackFromViewX's resolve call for why ThresholdFrames
            // doesn't apply to VolumeAccessor output at all, Z included: it targets file-format
            // per-frame parsing overhead, not an in-memory/MMF copy operation.
            var resolved = VirtualPolicy.Resolve(outputMode, estimatedBytes, frameCount: 0);
            // Clone(), not the live _axis instance directly: DefineDimensions registers
            // axis.IndexChanged on the new MatrixData's own DimensionStructure. Sharing the same Axis
            // object the source data's DimensionStructure already has registered would make two
            // independent MatrixData instances silently drive each other's ActiveIndex through one
            // shared mutable Axis.Index. CreateStackFromViewX/Y already avoid this by constructing a
            // brand new Axis (there is no pre-existing Axis object for the X/Y spatial extent to
            // clone from); here one already exists, so Clone() is the equivalent fix, matching the
            // same pattern DimensionStructure.CreateAxesWithout and Composite's CloneChannelAxis use.
            var newAxis = _axis.Clone();
            progress?.Report(-frameCount);

            MatrixData<T> m;
            if (resolved == LoadingMode.Virtual)
            {
                // Already frame-ordered (identity along Z): unlike X/Y's transpose, each output
                // frame is independently complete as soon as it's read, so this streams one frame
                // at a time -- peak memory stays at a single frame instead of the whole volume.
                var vessel = MatrixDataSerializer.CreateTempVessel<T>(_width, _height, frameCount);
                for (int i = 0; i < frameCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    // No defensive copy needed here: WriteDirectly only reads data, and the array
                    // isn't kept beyond the call (unlike the InMemory branch, which must hand back
                    // independent buffers per the class's documented "always deep copy" contract).
                    vessel.WriteDirectly(i, _frames[i]);
                    progress?.Report(i);
                }
                vessel.Flush();
                m = MatrixData<T>.CreateAsVirtualFrames(_width, _height, vessel);
            }
            else
            {
                var newFrames = new List<T[]>(frameCount);
                for (int i = 0; i < frameCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    // For virtual (MMF-backed) source data, indexing _frames decodes the frame here.
                    T[] src = _frames[i];
                    // Deep copy to ensure that the new frames are independent of the original frames.
                    var copy = new T[src.Length];
                    src.AsSpan().CopyTo(copy);
                    newFrames.Add(copy);
                    progress?.Report(i);
                }
                m = new MatrixData<T>(_width, _height, newFrames);
            }

            m.SetXYScale(_scale.XMin, _scale.XMax, _scale.YMin, _scale.YMax);
            m.XUnit = _scale.XUnit;
            m.YUnit = _scale.YUnit;
            m.DefineDimensions(newAxis);
            return m;
        }

        /// <summary>
        /// Chooses how many output frames <see cref="CreateStackFromViewX"/>/<see
        /// cref="CreateStackFromViewY"/>'s Virtual-output branch buffers in managed memory per band
        /// (see their Virtual branches for the full rationale) -- large enough to keep the number of
        /// bands, and so the number of times each source frame gets redundantly re-read/decoded, small,
        /// but bounded by available memory since a band's frames are fully materialized in RAM at once.
        /// </summary>
        /// <remarks>
        /// Deliberately more generous than <see cref="VirtualCachePolicy.MemoryBudgetFraction"/>: a
        /// band's buffers are transient (freed as soon as that band is written to the vessel), not held
        /// for the operation's whole duration like a frame cache, so claiming a larger share of
        /// available memory for a short time is a reasonable trade.
        /// </remarks>
        /// <summary>
        /// Sizes a RAM-buffered band of whole frames (by available system memory) for callers that
        /// build Virtual output frame-by-frame elsewhere in <c>MxPlot.Core</c> — e.g.
        /// <see cref="Processing.DimensionalOperator.Transpose{T}(MatrixData{T}, IProgress{int}?, CancellationToken)"/>
        /// — and want the same generous-but-bounded batching this type already uses for
        /// <see cref="Restack"/>, rather than a second, independently-tuned heuristic.
        /// </summary>
        internal static int ComputeBandSize(int frameWidth, int frameHeight, int totalFrames)
        {
            long frameBytes = (long)frameWidth * frameHeight * Unsafe.SizeOf<T>();
            if (frameBytes <= 0) return totalFrames;

            long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long budgetBytes = (long)(availableBytes * 0.4);
            long framesPerBand = budgetBytes / frameBytes;

            return (int)Math.Clamp(framesPerBand, 1, totalFrames);
        }

        /// <summary>
        /// Shared tail for <see cref="CreateStackFromViewX"/>/<see cref="CreateStackFromViewY"/>'s
        /// in-memory branch: <paramref name="frames"/> is already fully assembled by the caller (that
        /// branch is only reached when <see cref="VirtualPolicy.Resolve"/> already resolved to
        /// in-memory output, so this always takes the non-Virtual path below -- callers whose scatter
        /// resolved to Virtual write frame-by-frame straight into their own vessel instead, bypassing
        /// this method entirely; see those methods' Virtual branches) and applies <paramref
        /// name="finish"/> (scale/axis setup) to the resulting <see cref="MatrixData{T}"/>.
        /// </summary>
        private static MatrixData<T> ResolveAndFinish(
            int width, int height, List<T[]> frames, LoadingMode outputMode, CancellationToken ct,
            Action<MatrixData<T>> finish)
        {
            long estimatedBytes = (long)width * height * frames.Count * Unsafe.SizeOf<T>();
            // frameCount: 0, matching the callers' own early resolve -- see CreateStackFromViewX's
            // resolve call for why. Keeping this call's own decision consistent with the caller's
            // early one matters: callers only reach this in-memory branch when their early resolve
            // already said non-Virtual, so re-resolving with the old frame-count heuristic here could
            // flip to Virtual after `frames` was already built as a plain in-memory List<T[]>.
            var resolved = VirtualPolicy.Resolve(outputMode, estimatedBytes, frameCount: 0);

            MatrixData<T> md;
            if (resolved == LoadingMode.Virtual)
            {
                var vessel = MatrixDataSerializer.CreateTempVessel<T>(width, height, frames.Count);
                try
                {
                    for (int i = 0; i < frames.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        vessel.WriteDirectly(i, frames[i]);
                        frames[i] = null!; // no longer needed -- let the GC reclaim it as we go
                    }
                    vessel.Flush();
                }
                catch
                {
                    // Cancelled or failed mid-write -- don't leak the (possibly multi-GB) temp file.
                    vessel.Dispose();
                    throw;
                }
                md = MatrixData<T>.CreateAsVirtualFrames(width, height, vessel);
            }
            else
            {
                md = new MatrixData<T>(width, height, frames);
            }

            finish(md);
            return md;
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
