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
        /// この軸がChannelのようにMin = 0, Max = Count -1 のインデックスベースの軸かどうか
        /// trueの場合，Min, Maxは変更されない
        /// IsIndexBasedをtrueにすると自動的にMin = 0, Max = Count -1が設定される
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
        /// この軸の最小値
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
        /// この軸の最大値
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
        /// この軸の単位を表す文字列
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
        /// 分割数 = Seriesの要素数：不変
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// 軸の名称
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
        /// このAxisの現在位置を設定・取得する
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
        /// 現在のindexにおけるスケール上の位置を設定・取得する
        /// 設定するときには最も近いindexになる
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
        /// = Max - Min (都度計算する），取得のみ
        /// </summary>
        public double Size => Max - Min;

        /// <summary>
        /// = (Max - Min) / (Count - 1) （都度計算する）Stepを設定するとMax値が変わる
        /// </summary>
        public double Step
        {
            get => Count > 1 ? (Max - Min) / (Count - 1) : 0;
            set
            {
                //Pitchを変える場合はMaxが変わる
                Max = Min + value * (Count - 1);
            }
        }


        /// <summary>
        /// Nameが変化した場合に通知される
        /// </summary>
        public event EventHandler? NameChanged;



        /// <summary>
        /// Index値が変化した場合に通知される
        /// </summary>
        public event EventHandler? IndexChanged;

        /// <summary>
        /// AxisのMin, Max が変化した場合に通知される
        /// </summary>
        public event EventHandler? ScaleChanged;

        /// <summary>
        /// Axisの単位文字列が変化した場合に通知される
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
        /// 最小値・最大値をnumで分割した範囲を定義する
        /// 領域を変更することができない
        /// </summary>
        /// <param name="min"></param>
        /// <param name="max"></param>
        /// <param name="num"></param>
        /// <param name="name"></param>
        /// <param name="unit">軸の単位</param>
        /// <param name="isIndexBased">インデックスベースの軸かどうか trueの場合はmin = 0, max = num - 1に固定される</param>
        [JsonConstructor]
        public Axis(int count, double min, double max, string name = "Series", string unit = "", bool isIndexBased = false)
        {
            this.Count = count;
            this.Name = name;
            this.Unit = unit;

            this.IsIndexBased = isIndexBased; //trueの場合は自動的にmin, maxが設定される（コンストラクタなので、イベントは発生し得ない）
            if (!IsIndexBased)
            {
                _min = min;
                _max = max;
            }
            if (count <= 0)
                throw new ArgumentException("Division number should be >= 1, num = " + count);

            Step = count > 1 ? (max - min) / (count - 1) : 0;
        }

        /// <summary>
        /// コピーコンストラクタ, Name, Num, Min, Maxがコピーされる
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
        /// 指定した値に対応するindex値(0 - (num - 1))を返す
        /// posが範囲を超えているとクロップされる
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
        /// index値に対応するスケール値を返す
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
