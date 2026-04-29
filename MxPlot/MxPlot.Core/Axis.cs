using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;

namespace MxPlot.Core
{
    /// <summary>
    /// Represents an axis with defined minimum and maximum values, units, and a name, allowing for index-based or
    /// value-based positioning.
    /// </summary>
    /// <remarks>The Axis class provides functionality to manage axis properties such as scaling, indexing,
    /// and event notifications for changes in name, index, scale, and unit. It supports both index-based and
    /// value-based axes, with automatic adjustments for index-based configurations. The axis can be used to map
    /// positions or indices to values, and includes factory methods for common axis types such as 'Time', 'Channel',
    /// and 'Frame'.</remarks>
    [JsonDerivedType(typeof(Axis), "base")]
    [JsonDerivedType(typeof(FovAxis), "fov")]
    [JsonDerivedType(typeof(TaggedAxis), "tagged")]
    [JsonDerivedType(typeof(ColorChannel), "colored")]
    public class Axis : ICloneable
    {

        private double _min;
        private double _max;
        private string _unit = "";
        private string _name = "";
        private int _index;
        private bool _isIndexBased = false;


        /// <summary>
        /// Whether this axis is index-based (like Channel), where Min = 0 and Max = Count - 1.
        /// When true, Min and Max cannot be changed externally.
        /// Setting IsIndexBased to true automatically sets Min = 0 and Max = Count - 1.
        /// </summary>
        public bool IsIndexBased
        {
            get => _isIndexBased;
            set
            {
                if (_isIndexBased != value)
                {
                    _isIndexBased = value;
                    if (_isIndexBased)
                    {
                        _min = 0;
                        _max = Count - 1;
                        ScaleChanged?.Invoke(this, new EventArgs());
                    }
                }
            }
        }

        /// <summary>
        /// The minimum value of this axis.
        /// </summary>
        public virtual double Min
        {
            get => _min;
            set
            {
                if (IsIndexBased)
                    return;

                if (_min != value)
                {
                    _min = value;
                    ScaleChanged?.Invoke(this, new EventArgs());
                }
            }
        }

        /// <summary>
        /// The maximum value of this axis.
        /// </summary>
        public virtual double Max
        {
            get => _max;
            set
            {
                if (IsIndexBased)
                    return;

                if (_max != value)
                {
                    _max = value;
                    ScaleChanged?.Invoke(this, new EventArgs());
                }
            }
        }

        /// <summary>
        /// A string representing the unit of this axis.
        /// </summary>
        public string Unit
        {
            get { return _unit; }
            set
            {
                if (!_unit.Equals(value))
                {
                    _unit = value;
                    UnitChanged?.Invoke(this, new EventArgs());
                }
            }
        }

        /// <summary>
        /// The number of divisions, equal to the number of elements in the Series. Immutable.
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// The name of the axis.
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                if (!_name.Equals(value))
                {
                    _name = value;
                    NameChanged?.Invoke(this, new EventArgs());

                }
            }
        }

        /// <summary>
        /// Gets or sets the current position of this axis.
        /// </summary>
        public int Index
        {
            set
            {
                if (value < 0 || value >= Count)
                    throw new IndexOutOfRangeException("Invalid index = " + value);

                if (_index != value)
                {
                    _index = value;
                    IndexChanged?.Invoke(this, new EventArgs());
                }

            }
            get => _index;
        }

        /// <summary>
        /// Gets or sets the scale position at the current index.
        /// When setting, the nearest index to the given value is selected.
        /// </summary>
        public virtual double Value
        {
            get
            {
                return ValueAt(Index);
            }
            set
            {
                int pos = IndexOf(value);
                Index = pos;
            }
        }

        /// <summary>
        /// = Max - Min (computed on each access). Read-only.
        /// </summary>
        public double Range => Max - Min;

        /// <summary>
        /// = (Max - Min) / (Count - 1) (computed on each access). Setting Step changes the Max value.
        /// </summary>
        public double Step
        {
            get => Count > 1 ? (Max - Min) / (Count - 1) : 0;
            set
            {
                //Changing Step changes Max, so we set Max here. Step = 0 is allowed when Count = 1, but not when Count > 1
                Max = Min + value * (Count - 1);
            }
        }


        /// <summary>
        /// Notify when the Name property changes.
        /// </summary>
        public event EventHandler? NameChanged;



        /// <summary>
        /// Notify when the Index property changes. 
        /// </summary>
        public event EventHandler? IndexChanged;

        /// <summary>
        /// Notify when the Min or Max properties change. 
        /// </summary>
        public event EventHandler? ScaleChanged;

        /// <summary>
        /// Notify when the Unit property changes. 
        /// </summary>
        public event EventHandler? UnitChanged;


        #region Factory methods for common axes
        /// <summary>
        /// Axis with a key (Name) of "Z
        /// </summary>
        /// <param name="num"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        public static Axis Z(int num, double min, double max, string unit = "")
        {
            return new Axis(num, min, max, "Z", unit, false);
        }

        /// <summary>
        /// Axis with a key (Name) = "Time"
        /// </summary>
        /// <param name="num"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <param name="unit"></param>
        /// <returns></returns>
        public static Axis Time(int num, double min, double max, string unit = "")
        {
            return new Axis(num, min, max, "Time", unit, false);
        }

        /// <summary>
        /// Axis with a key (Name)  = "Channel"
        /// min = 0, max = num -1, isIndexBased = true
        /// </summary>
        /// <param name="num"></param>
        /// <param name="unit"></param>
        /// <returns></returns>
        public static Axis Channel(int num, string unit = "")
        {
            return new Axis(num, 0, num-1, "Channel", unit, true);
        }

        /// <summary>
        /// General axis with a key (Name) = "Frame"
        /// </summary>
        /// <param name="num"></param>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <returns></returns>
        public static Axis Frame(int num, double min, double max)
        {
            return new Axis(num, min, max, "Frame");
        }

        /// <summary>
        /// General axis based on index with a specified name
        /// </summary>
        /// <param name="name"></param>
        /// <param name="num"></param>
        /// <returns></returns>
        public static Axis IndexBased(string name, int num)
        {
            return new Axis(num, 0, num - 1, name, "", true);
        }

        /// <summary>
        /// DeepCopy of axis[] without its index (position)
        ///
        /// </summary>
        /// <param name="axes"></param>
        /// <returns></returns>
        public static Axis[] CreateFrom(Axis[] axes)
        {
            Axis[] ret = new Axis[axes.Length];
            if (ret.Length == 0)
                return ret;

            int i = 0;
            foreach (Axis axis in axes)
            {
                if (axis is FovAxis fov)
                {
                    var grid = fov.TileLayout;
                    ret[i] = new FovAxis(fov.Origins.ToList(), grid.X, grid.Y, grid.Z);
                }
                else
                {
                    ret[i] = new Axis(axis.Count, axis.Min, axis.Max, axis.Name);
                }
                ret[i].Unit = axis.Unit;
                i++;
            }
            return ret;
        }

        #endregion

        
        /// <summary>
        /// Defines a range divided into <paramref name="count"/> segments between the specified minimum and maximum values.
        /// The range cannot be changed after construction.
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <param name="count"></param>
        /// <param name="name"></param>
        /// <param name="unit">The unit of the axis.</param>
        /// <param name="isIndexBased">Whether the axis is index-based. When true, min = 0 and max = count - 1 are enforced.</param>
        [JsonConstructor]
        public Axis(int count, double min, double max, string name = "Series", string unit = "", bool isIndexBased = false)
        {
            if (count <= 0)
                throw new ArgumentException("Division number should be >= 1, num = " + count);

            this.Count = count;
            this.Name = name;
            this.Unit = unit;

            this.IsIndexBased = isIndexBased; // When true, min and max are set automatically (no events fire in the constructor)
            if (!IsIndexBased)
            {
                _min = min;
                _max = max;
            }

            //Step is computed on each access. Setting Step changes Max. The line below is not needed.
            //Step = count > 1 ? (max - min) / (count - 1) : 0;
        }

        /// <summary>
        /// Copy constructor. Copies Name, Count, Min, Max, Unit, and IsIndexBased.
        /// </summary>
        /// <param name="source"></param>
        public Axis(Axis source)
        {
            this.Count = source.Count;
            this.Min = source.Min;
            this.Max = source.Max;
            this.Name = source.Name;
            this.Unit = source.Unit;
            this.IsIndexBased = source.IsIndexBased;
        }



        /// <summary>
        /// Returns the index value (0 to Count - 1) corresponding to the specified scale value.
        /// If <paramref name="pos"/> is out of range, the result is clamped.
        /// </summary>
        /// <param name="pos"></param>
        /// <returns></returns>
        public int IndexOf(double pos)
        {
            //y = (max - min) * i / (num - 1) + min
            //i = (y - min) / (max - min) * (num - 1);
            int i = Convert.ToInt32(System.Math.Round((pos - Min) * (Count - 1.0) / (Max - Min)));
            if (i < 0)
            {
                Console.WriteLine("Axis: Index < 0 for " + pos + ", i = 0");
                i = 0;
            }
            else if (i >= Count)
            {
                Console.WriteLine("Axis: Index >= num for " + pos + ", i = " + (Count - 1));
                i = Count - 1;
            }
            return i;
        }

        /// <summary>
        /// Returns the scale value corresponding to the specified index.
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public double ValueAt(int index)
        {
            if (index < 0 || index >= Count)
                throw new IndexOutOfRangeException("index = " + index);

            return index * Step + Min;
        }
        public override string ToString() => Name;

        public virtual Axis Clone()
        {
            var axis = new Axis(this.Count, this.Min, this.Max, this.Name, this.Unit, this.IsIndexBased);
            return axis;
        }

        object ICloneable.Clone()
        {
            return this.Clone();
        }
    }

}
