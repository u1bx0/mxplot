using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MxPlot.Core
{
    // Min/Max calculation logic
    
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
        internal readonly struct ValueRange
        {
            /// <summary> Gets the list of minimum values for each value mode. </summary>
            public List<double> MinValues { get; }

            /// <summary> Gets the list of maximum values for each value mode. </summary>
            public List<double> MaxValues { get; }

            /// <summary>
            /// Gets a value indicating whether the current statistics are valid (calculated).
            /// </summary>
            public bool IsValid => MinValues != null && MaxValues != null
                                && MinValues.Count > 0
                                && MinValues.Count == MaxValues.Count;

            /// <summary>
            /// Clears the cached statistics and marks the state as invalid (<see cref="IsValid"/> = false).
            /// </summary>
            public void Invalidate()
            {
                MinValues.Clear(); 
                MaxValues.Clear();
            }

            public ValueRange()
            {
                MinValues = new List<double>();
                MaxValues = new List<double>();
            }

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
                if (MinValues == null || MaxValues == null)
                    throw new InvalidOperationException("Uninitialized ValueRange cannot be set.");

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
        /// Retrieves the array of keys associated with the specified frame index.
        /// </summary>
        /// <remarks>If the underlying data structure is a VirtualFrameList, this method returns a dummy
        /// key array without performing disk access. Otherwise, it returns a reference to the actual key array from the
        /// standard list.</remarks>
        /// <param name="frameIndex">The zero-based index of the frame for which to retrieve the key array. Must be within the valid range of the
        /// underlying data structure.</param>
        /// <returns>An array of type T containing the keys for the specified frame index.</returns>
        private T[] GetFrameKey(int frameIndex)
        {
            if (_arrayList is IFrameKeyProvider<T> fkp)
            {
                return fkp.GetKey(frameIndex);
            }

            return _arrayList[frameIndex]; //InMemory
        }

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
            return GetValueRangeList(frameIndex, false);
        }

        /// <summary>
        /// Retrieves the minimum and maximum value lists for the specified frame index.
        /// </summary>
        /// <remarks>
        /// This method returns all available min/max values for the structured value type 
        /// (e.g., [Magnitude, Real, Imaginary, Phase, Power] for Complex data).
        /// </remarks>
        /// <param name="frameIndex">The zero-based index of the frame.</param>
        /// <param name="skipRefresh">
        /// If <c>true</c>, skips the full-pixel scan (<c>RefreshValueRange</c>) even if the current 
        /// cache is invalid or missing. 
        /// <para>
        /// <strong>Crucial for Performance:</strong> In high-throughput scenarios like 
        /// real-time slicing of large volumes (e.g., 2048x2048), setting this to <c>true</c> 
        /// prevents blocking the UI thread or render loop with O(N) operations, 
        /// ensuring sub-millisecond execution by returning cached or <c>NaN</c> values.
        /// </para>
        /// </param>
        /// <returns>
        /// A tuple of two lists containing min and max values. 
        /// Returns <c>double.NaN</c> elements if <paramref name="skipRefresh"/> is <c>true</c> 
        /// and no valid cache exists.
        /// </returns>
        public (List<double> MinValues, List<double> MaxValues) GetValueRangeList(int frameIndex, bool skipRefresh)
        {
            if (!skipRefresh && RefreshValueRangeRequired(frameIndex))
            {
                //Debug.WriteLine($"[MatrixData.GetValueRangeList] Refresh value range for {frameIndex}");
                return RefreshValueRange(frameIndex);
            }
            if (_valueRangeMap.TryGetValue(GetFrameKey(frameIndex), out var range))
            {
                //Debug.WriteLine($"[MatrixData.GetValueRangeList] Use cached value range for {frameIndex}");
                return (range.MinValues, range.MaxValues);
            }
            return ([double.NaN], [double.NaN]);
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
                    if (_valueRangeMap.TryGetValue(GetFrameKey(frameIndex), out var range))
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

                    if (_valueRangeMap.TryGetValue(GetFrameKey(frameIndex), out var range))
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
            return GetGlobalValueRange(0, out _);
        }

        public (double Min, double Max) GetGlobalValueRange(out List<int> invlids, bool forceRefresh)
        {
            return GetGlobalValueRange(0, out invlids, forceRefresh);
        }

        /// <summary>
        /// Calculates the global minimum and maximum values across all cached data, identifying any uncomputed frames.
        /// </summary>
        /// <param name="valueMode">The index of the value mode (e.g., Magnitude, Phase) to evaluate.</param>
        /// <param name="invalids">
        /// When this method returns, contains a <see cref="List{Int32}"/> of representative frame indices that are currently uncalculated (invalid).
        /// <para>
        /// <strong>Optimization Note:</strong> If multiple frames share the same data reference (and thus the same cache), 
        /// only one representative index is added to this list to prevent redundant background calculations.
        /// </para>
        /// </param>
        /// <param name="forceRefresh">
        /// If <c>true</c>, forces an immediate full-pixel scan for all invalid frames before returning. 
        /// Set to <c>false</c> to maintain UI responsiveness by retrieving only currently available data.
        /// </param>
        /// <returns>
        /// A tuple containing the global minimum and maximum values. 
        /// Returns <c>(NaN, NaN)</c> if no frames are calculated and <paramref name="forceRefresh"/> is <c>false</c>.
        /// </returns>
        /// <remarks>
        /// This method iterates through the internal <c>_valueRangeMap</c> rather than the frame list. 
        /// This is significantly more efficient when many frames share the same physical data (e.g., Virtual Data or shallow copies), 
        /// as it processes each unique data source only once.
        /// </remarks>
        public (double Min, double Max) GetGlobalValueRange(int valueMode, out List<int> invalids, bool forceRefresh = false)
        {
            if (FrameCount == 0) throw new InvalidOperationException("No frames available to calculate global min/max.");

            double min = double.PositiveInfinity;
            double max = double.NegativeInfinity;

            invalids = [];
            // Iterating the map (not the frame list) is efficient when many frames share
            // the same T[] reference (Virtual / shallow-copy Reorder), since each unique
            // data source is processed only once.
            foreach (var key in _valueRangeMap.Keys)
            {
                var range = _valueRangeMap[key];
                if (!range.IsValid)
                {
                    int index = _arrayList.IndexOf(key);
                    if (index >= 0)
                    {
                        if (forceRefresh)
                            RefreshValueRange(index); // after this the shared List<double> is populated → range.IsValid becomes true
                        else
                            invalids.Add(index);      // still invalid; caller may schedule a background scan
                    }
                }

                // Catches both originally-valid frames AND frames that were just refreshed above.
                if (range.IsValid)
                {
                    var vmin = range.MinValues[valueMode];
                    var vmax = range.MaxValues[valueMode];
                    if (vmin < min) min = vmin;
                    if (vmax > max) max = vmax;
                }
            }

            if(double.IsPositiveInfinity(min) || double.IsNegativeInfinity(max))
            {
                // This can happen if all frames are invalid and forceRefresh is false
                return (double.NaN, double.NaN);
            }
            return (min, max);
        }

        /// <summary>
        /// Refreshes the min/max statistics for a specific frame.
        /// Implement a fail-safe mechanism that returns NaN instead of throwing an exception 
        /// when a suitable MinMaxFinder is not registered for the data type.
        /// </summary>
        /// <param name="frameIndex">Index of the frame to refresh.</param>
        /// <returns>A tuple of min and max value lists. Returns [NaN] if calculation is not possible.</returns>
        protected (List<double> MinValues, List<double> MaxValues) RefreshValueRange(int frameIndex)
        {
            if (frameIndex < 0) frameIndex = _activeIndex;

            // FAIL-SAFE: If no calculator is registered (e.g., for custom structs), 
            // return NaN lists to prevent application crash and allow data transport to continue.
            if (_minMaxFinder == null)
            {
                return ([double.NaN], [double.NaN]);
            }

            var array = GetInternalArray(frameIndex, needsInvalidate: false);
            var (minValues, maxValues) = _minMaxFinder(array);
            var keyArray = GetFrameKey(frameIndex);
            if (_valueRangeMap.TryGetValue(keyArray, out var range))
            {
                range.Set(minValues, maxValues);
                return (range.MinValues, range.MaxValues);
            }
            else
            {
                // FAIL-SAFE: Fallback for unexpected state to maintain consistent return type
                return ([double.NaN], [double.NaN]);
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
            if (_valueRangeMap.TryGetValue(GetFrameKey(frameIndex), out var range))
            {
                if (!range.IsValid)
                {
                    return true;
                    // Definition of IsValid for reference:
                    // public bool IsValid => MinValues != null && MaxValues != null && MinValues.Count > 0 && MinValues.Count == MaxValues.Count;
                }
                var minArr = range.MinValues;
                var maxArr = range.MaxValues;
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
            if (_valueRangeMap.TryGetValue(GetFrameKey(frameIndex), out var range))
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

        /// <summary>
        /// Invalidates all frames in the current context, ensuring that any cached data is refreshed.
        /// </summary>
        /// <remarks>This method iterates through all value ranges and calls the Invalidate method on
        /// each, which may affect performance if called frequently. It is recommended to use this method judiciously to
        /// avoid unnecessary overhead.</remarks>
        public void InvalidateAllFrames()
        {
            foreach(var range  in _valueRangeMap.Values)
            {
                range.Invalidate();
            }
        }

    }

}
