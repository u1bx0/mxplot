using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{
    // Min/Max calculation logic
    
    public partial class MatrixData<T> : IMatrixData where T : unmanaged
    {

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
            if (_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
            {
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
                    if (_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
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

                    if (_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
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
                /*
               throw new InvalidOperationException(
                   $"Type '{typeof(T).Name}' has no MinMaxFinder registered. " +
                   $"Call 'MatrixData<{typeof(T).Name}>.RegisterDefaultMinMaxFinder(...)' first, " +
                   $"or avoid operations that require min/max statistics.");
               */
            }

            var array = _arrayList[frameIndex];
            var (minValues, maxValues) = _minMaxFinder(array);

            if (_valueRangeMap.ContainsKey(array))
            {
                _valueRangeMap[array].Set(minValues, maxValues);
                return (_valueRangeMap[array].MinValues, _valueRangeMap[array].MaxValues);
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
            if (_valueRangeMap.TryGetValue(_arrayList[frameIndex], out var range))
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

    }
}
