using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MxPlot.Core
{
    //Data access

    public partial class MatrixData<T> : IMatrixData where T : unmanaged
    {
        // Value access methods
        public double GetValueAt(int ix, int iy, int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            return ToDoubleFrom(GetInternalArray(frameIndex, needsInvalidate:false)[iy * _xcount + ix]);
        }

        public T GetValueAtTyped(int ix, int iy, int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            return GetInternalArray(frameIndex, needsInvalidate: false)[iy * _xcount + ix];
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

            GetInternalArray(frameIndex, needsInvalidate: true)[iy * _xcount + ix] = ToValueTypeFrom(v);
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

            GetInternalArray(frameIndex, needsInvalidate: true)[iy * _xcount + ix] = value;
        }

        /// <summary>
        /// Returns a read-only span over the elements in the specified frame of the array list.
        /// This should be used if no modification to the data is needed. This method does not invalidate the cached min/max values.
        /// </summary>
        /// <remarks>If the specified frame index is less than zero, this method defaults to using the
        /// active frame index. This allows callers to easily access the current frame's data without specifying an
        /// index.</remarks>
        /// <param name="frameIndex">The zero-based index of the frame from which to retrieve the span. If the value is less than zero, the span
        /// of the currently active frame is returned.</param>
        /// <returns>A read-only span containing the elements of the specified frame.</returns>
        public ReadOnlySpan<T> AsSpan(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            return GetInternalArray(frameIndex, needsInvalidate:false).AsSpan();
        }

        /// <summary>
        /// Returns a read-only memory region containing the data for the specified frame. 
        /// This does not invalidate the cached min/max values, so it can be used for read-only access without affecting the statistics. 
        /// </summary>
        /// <remarks>This method enables efficient access to frame data without copying. The returned
        /// memory is valid as long as the underlying data remains unchanged.</remarks>
        /// <param name="frameIndex">The zero-based index of the frame to retrieve. If not specified or set to a negative value, the currently
        /// active frame is used.</param>
        /// <returns>A read-only memory region representing the data of the specified frame.</returns>
        public ReadOnlyMemory<T> AsMemory(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            return GetInternalArray(frameIndex, needsInvalidate: false).AsMemory();
        }

        /// <summary>
        /// Gets the internal array for the specified frame. If no frame index is provided, the active frame's array is returned.
        /// By default (with forceInvalidation = true), this method automatically invalidates the cached min/max values 
        /// for the specified frame, ensuring that any modifications to the array will trigger a refresh of the statistics when next requested.
        /// However, users should call Invalidate explicitly when further modifying the array kept outside after calling GetValueRange.
        /// </summary>
        /// <param name="frameIndex"></param>
        /// <param name="forceInvalidation"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] GetArray(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            bool shouldInvalidate = !_arrayList.IsReadOnly; 
            return GetInternalArray(frameIndex, shouldInvalidate); 
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

            // ── Virtual (writable MMF) path ──────────────────────────────────
            // WriteDirectly writes srcArray straight to MMF, skipping the cache
            // read that GetInternalArray would trigger. Flush() commits the pages.
            // Invalidate() discards any stale cached min/max without a disk round-trip.
            if (_arrayList is MxPlot.Core.IO.IWritableFrameProvider<T> writable)
            {
                writable.WriteDirectly(frameIndex, srcArray); // length guard inside
                writable.Flush();                             // _accessor.Flush() → disk

                if (minValues != null && maxValues != null
                    && minValues.Length > 0 && maxValues.Length > 0
                    && minValues.Length == maxValues.Length)
                {
                    if (_valueRangeMap.TryGetValue(GetFrameKey(frameIndex), out var range))
                        range.Set(minValues, maxValues);
                }
                else
                {
                    Invalidate(frameIndex); // lazy recompute on next GetValueRange
                }
                return;
            }

            // ── InMemory path (original behaviour) ───────────────────────────
            var array = GetInternalArray(frameIndex, needsInvalidate: true);
            if (srcArray.Length != array.Length)
                throw new ArgumentException($"Invalid array length: {srcArray.Length}, expected: {array.Length}");

            if (srcArray != array)
                srcArray.AsSpan().CopyTo(array);

            if (minValues != null && maxValues != null
                && minValues.Length > 0 && maxValues.Length > 0
                && minValues.Length == maxValues.Length)
            {
                if (_valueRangeMap.TryGetValue(GetFrameKey(frameIndex), out var range))
                {
                    range.Set(minValues, maxValues);
                }
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
            get => GetInternalArray(_activeIndex, needsInvalidate:false)[iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                GetInternalArray(_activeIndex, needsInvalidate: true)[iy * _xcount + ix] = value;
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
            get => GetInternalArray(i_axis0, needsInvalidate: false)[iy * _xcount + ix]; //i_axis0 corresponds to frame index directly since there's only one axis

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                GetInternalArray(i_axis0, needsInvalidate: true)[iy * _xcount + ix] = value;
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
            get => GetInternalArray(Dimensions.GetFrameIndexAt(i_axis0, i_axis1), needsInvalidate:false)[iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(i_axis0, i_axis1);
                GetInternalArray(frameIndex, needsInvalidate:true)[iy * _xcount + ix] = value;
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
            get => GetInternalArray(Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2), false)[iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2);
                GetInternalArray(frameIndex, true)[iy * _xcount + ix] = value;
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
            get => GetInternalArray(Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2, i_axis3), false)[iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(i_axis0, i_axis1, i_axis2, i_axis3);
                GetInternalArray(frameIndex, needsInvalidate: true)[iy * _xcount + ix] = value;
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
            get => GetInternalArray(Dimensions.GetFrameIndexAt(axisIndices), false)[iy * _xcount + ix];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                int frameIndex = Dimensions.GetFrameIndexAt(axisIndices);
                GetInternalArray(frameIndex, true)[iy * _xcount + ix] = value;
            }
        }

        /// <summary>
        /// Zero-copy conversion to byte array for serialization
        /// </summary>
        public unsafe ReadOnlySpan<byte> GetRawBytes(int frameIndex = -1)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;
            var array = GetInternalArray(frameIndex, needsInvalidate:false);
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
            var array = GetInternalArray(frameIndex, needsInvalidate: true);
            var targetSpan = MemoryMarshal.AsBytes(array.AsSpan());

            if (bytes.Length != targetSpan.Length)
                throw new ArgumentException($"Byte length mismatch: {bytes.Length} != {targetSpan.Length}");

            bytes.CopyTo(targetSpan);
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
        public MatrixData<T> ForEach(Action<int, T[]> action, bool useParallel = true)
        {
            if (FrameCount < 2)
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
                for (int i = 0; i < FrameCount; i++)
                {
                    action(i, GetArray(i));
                    RefreshValueRange(i);
                }
            }
            return this;
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
                return GetInternalArray(frameIndex, needsInvalidate:false)[iy * _xcount + ix];
            }

            if (this is MatrixData<Complex>) //Special case for Complex type.
            {
                Complex[] array = (Complex[])(object)GetInternalArray(frameIndex, needsInvalidate:false);
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
            if (!_isSupportedPrimitive)
                throw new InvalidOperationException($"GetValueAsDouble is only supported for primitive numeric types. Current type: {typeof(T).Name}");

            if (frameIndex < 0) frameIndex = _activeIndex;

            if (!interpolate)
            {
                return _toDouble(GetValue(x, y, frameIndex, false));
            }
            //interpolation is enabled
            var array = GetInternalArray(frameIndex, needsInvalidate: false);
            if (array == null)
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
    }

}
