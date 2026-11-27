using MxPlot.Core;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Formula
{
    /// <summary>
    /// What a formula reads while it is evaluated for one pixel. An instance is used by one thread at a time.
    /// </summary>
    internal sealed class FormulaContext
    {
        /// <summary>The variable values, in the order of <see cref="CompiledFormula.Variables"/>.</summary>
        public readonly double[] V;

        /// <summary>The references, in the order of <see cref="CompiledFormula.References"/>.</summary>
        public readonly FormulaRef[] M;

        public FormulaContext(double[] variables, FormulaRef[] references)
        {
            V = variables;
            M = references;
        }
    }

    /// <summary>
    /// The minimum and maximum of a reference's frames, found once and shared by every
    /// <see cref="FormulaRef"/> that reads the same data during one evaluation. Safe to use from several threads.
    /// </summary>
    internal sealed class FormulaStats
    {
        private readonly IMatrixData _data;
        private readonly object _gate = new();
        private readonly Dictionary<int, (double Min, double Max)> _frames = [];
        private (double Min, double Max)? _all;

        /// <summary>The number of positions along each axis of the data, in the order of its axes.</summary>
        public int[] AxisCounts { get; }

        /// <summary>The name of each axis of the data, in the order of its axes.</summary>
        public string[] AxisNames { get; }

        /// <summary>How far apart in frame numbers two neighbors along each axis are, in the order of the axes.</summary>
        public int[] AxisStrides { get; }

        /// <summary>The value mode the data is read in, within the modes of its element type.</summary>
        public int ValueMode { get; }

        public FormulaStats(IMatrixData data, int valueMode = 0)
        {
            _data = data;
            ValueMode = data.ElementType.ClampMode(valueMode);

            var axes = data.Axes;
            AxisCounts = new int[axes.Count];
            AxisStrides = new int[axes.Count];
            AxisNames = new string[axes.Count];
            // The stride of an axis is the frame number of one step along it, read from the data's own layout.
            var step = new int[axes.Count];
            for (int i = 0; i < axes.Count; i++)
            {
                AxisCounts[i] = axes[i].Count;
                AxisNames[i] = axes[i].Name;
                step[i] = 1;
                AxisStrides[i] = data.Dimensions.GetFrameIndexAt(step);
                step[i] = 0;
            }
        }

        /// <summary>The value range of <paramref name="frame"/>.</summary>
        public (double Min, double Max) OfFrame(int frame)
        {
            lock (_gate)
            {
                if (!_frames.TryGetValue(frame, out var range))
                    _frames[frame] = range = _data.GetValueRange(frame, ValueMode);
                return range;
            }
        }

        /// <summary>
        /// The value range over all frames. For Virtual data only the frames whose range is already known
        /// are counted, as in a window's "All" range: scanning the rest could read the whole dataset from
        /// disk. When no frame is known yet, it is the range of <paramref name="fallbackFrame"/>. Found
        /// once and kept for the evaluation.
        /// </summary>
        public (double Min, double Max) OfAllFrames(int fallbackFrame)
        {
            lock (_gate)
            {
                if (_all is { } known) return known;

                // In memory, scanning the frames not yet scanned is cheap, so the range is complete.
                var range = _data.GetGlobalValueRange(ValueMode, out _, forceRefresh: !_data.IsVirtual);
                if (double.IsNaN(range.Min) || double.IsNaN(range.Max))
                    range = (uint)fallbackFrame < (uint)_data.FrameCount ? _data.GetValueRange(fallbackFrame, ValueMode) : range;
                _all = range;
                return range;
            }
        }
    }

    /// <summary>
    /// A matrix a formula reads from. Structured data (Complex, ...) is read in one value mode, which gives a
    /// number for each element. A read outside the data yields NaN.
    /// </summary>
    internal sealed class FormulaRef
    {
        private readonly IMatrixData _data;
        private readonly FormulaStats _stats;
        private readonly int _mode;
        private readonly int _xCount;
        private readonly int _yCount;
        private readonly int _frameCount;
        private readonly double _xMin;
        private readonly double _yMin;
        private readonly double _xStep;
        private readonly double _yStep;

        // The range asked for last, so that a formula using Max() in every pixel looks it up once per frame.
        private int _rangeFrame = -1;
        private (double Min, double Max) _range;

        /// <summary>The frame read when the formula names none.</summary>
        public int Frame;

        /// <param name="stats">The shared value ranges of <paramref name="data"/>, or <see langword="null"/> to keep private ones.</param>
        /// <param name="valueMode">The value mode to read in; used only when <paramref name="stats"/> is <see langword="null"/>, otherwise the mode of the statistics.</param>
        public FormulaRef(IMatrixData data, int frame, FormulaStats? stats = null, int valueMode = 0)
        {
            _data = data;
            _stats = stats ?? new FormulaStats(data, valueMode);
            _mode = _stats.ValueMode;
            _xCount = data.XCount;
            _yCount = data.YCount;
            _frameCount = data.FrameCount;
            _xMin = data.XMin;
            _yMin = data.YMin;
            _xStep = data.XStep;
            _yStep = data.YStep;
            Frame = frame;
        }

        /// <summary>The frame a formula reads when it names none, as a number the formula can build on (see <see cref="WithAxis"/>).</summary>
        public double CurrentFrame() => Frame;

        /// <summary>
        /// <paramref name="frame"/> moved to position <paramref name="value"/> along the axis called
        /// <paramref name="axisName"/>, the other axes as they are. NaN when there is no such axis,
        /// position or frame, which a read of that frame turns into NaN.
        /// </summary>
        /// <remarks>
        /// The axis is found by name in the data actually read, which can have fewer axes than the data the
        /// formula was written for: one frame of a stack has none, and a Composite channel cube has only the
        /// composited axis.
        /// </remarks>
        public double WithAxis(double frame, string axisName, double value)
        {
            int a = Array.IndexOf(_stats.AxisNames, axisName);
            if (a < 0 || double.IsNaN(frame) || double.IsNaN(value)) return double.NaN;

            int f = (int)frame;
            if ((uint)f >= (uint)_frameCount) return double.NaN;

            int count = _stats.AxisCounts[a];
            int stride = _stats.AxisStrides[a];
            double position = Math.Round(value);
            if (position < 0 || position >= count) return double.NaN;

            int current = f / stride % count;
            return f + ((int)position - current) * stride;
        }

        /// <summary>The value at pixel (<paramref name="ix"/>, <paramref name="iy"/>) of <see cref="Frame"/>.</summary>
        public double At(double ix, double iy) => Read(ix, iy, Frame);

        /// <summary>The value at pixel (<paramref name="ix"/>, <paramref name="iy"/>) of <paramref name="frame"/>.</summary>
        public double At(double ix, double iy, double frame) => Read(ix, iy, ToFrame(frame));

        /// <summary>The value at the physical position (<paramref name="x"/>, <paramref name="y"/>) in <see cref="Frame"/>, interpolated bilinearly between the surrounding pixels.</summary>
        public double AtPosition(double x, double y) => ReadPhysical(x, y, Frame);

        /// <summary>The interpolated value at a physical position in <paramref name="frame"/>.</summary>
        public double AtPosition(double x, double y, double frame) => ReadPhysical(x, y, ToFrame(frame));

        /// <summary>The largest value of <see cref="Frame"/>.</summary>
        public double Max() => RangeOf(Frame).Max;

        /// <summary>The largest value of <paramref name="frame"/>; NaN when there is no such frame.</summary>
        public double Max(double frame) => RangeOf(ToFrame(frame)).Max;

        /// <summary>The smallest value of <see cref="Frame"/>.</summary>
        public double Min() => RangeOf(Frame).Min;

        /// <summary>The smallest value of <paramref name="frame"/>; NaN when there is no such frame.</summary>
        public double Min(double frame) => RangeOf(ToFrame(frame)).Min;

        /// <summary>The largest value over all frames (see <see cref="FormulaStats.OfAllFrames"/> for Virtual data).</summary>
        public double MaxAll() => _stats.OfAllFrames(Frame).Max;

        /// <summary>The smallest value over all frames (see <see cref="FormulaStats.OfAllFrames"/> for Virtual data).</summary>
        public double MinAll() => _stats.OfAllFrames(Frame).Min;

        private (double Min, double Max) RangeOf(int frame)
        {
            if ((uint)frame >= (uint)_frameCount) return (double.NaN, double.NaN);
            if (frame != _rangeFrame)
            {
                _range = _stats.OfFrame(frame);
                _rangeFrame = frame;
            }
            return _range;
        }

        private static int ToFrame(double frame) => double.IsNaN(frame) ? -1 : (int)Math.Round(frame);

        private double Read(double ix, double iy, int frame)
        {
            // A NaN index fails both comparisons below, so it is rejected with the out-of-range ones.
            if (!(ix >= -0.5 && ix < _xCount - 0.5 && iy >= -0.5 && iy < _yCount - 0.5)) return double.NaN;
            if ((uint)frame >= (uint)_frameCount) return double.NaN;
            return _data.GetValueAt((int)Math.Round(ix), (int)Math.Round(iy), frame, _mode);
        }

        private double ReadPhysical(double x, double y, int frame)
        {
            // A one-pixel axis has no step; every position along it is that pixel.
            double fx = _xStep != 0.0 ? (x - _xMin) / _xStep : 0.0;
            double fy = _yStep != 0.0 ? (y - _yMin) / _yStep : 0.0;
            if (_xStep == 0.0 || _yStep == 0.0) return Read(fx, fy, frame);

            // A NaN position fails every comparison, so it is rejected with the out-of-range ones.
            if (!(fx >= -0.5 && fx <= _xCount - 0.5 && fy >= -0.5 && fy <= _yCount - 0.5)) return double.NaN;
            if ((uint)frame >= (uint)_frameCount) return double.NaN;
            return _data.GetValueAsDouble(x, y, frame, interpolate: true, _mode);
        }
    }
}
