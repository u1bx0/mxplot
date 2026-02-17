using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// Reserved for future use to represent a channel-specific behavior. 
    /// </summary>
    public class ColorChannel : Axis
    {
        private string[] _chNames;

        private uint[]? _assignedColors;
        
        private double[]? _wavelengths;

        /// <summary>
        /// Example: var ch = new ColorChannel("Red", "Green", "Blue");
        /// </summary>
        public ColorChannel(params string[] chNames)
            : base(chNames.Length, 0, chNames.Length - 1, "Channel", unit: "", isIndexBasedAxis: true)
        {
            if (chNames.Length == 0)
                throw new ArgumentException("At least one channel name must be provided.");

            _chNames = new string[chNames.Length];
            Array.Copy(chNames, _chNames, chNames.Length);
        }

        /// <summary>
        /// Constructs a ColorChannel with default names ("Ch0", "Ch1", etc.) based on the specified count.
        /// </summary>
        public ColorChannel(int count)
            : base(count, 0, count - 1, "Channel", unit: "", isIndexBasedAxis: true)
        {
            if (count <= 0)
                throw new ArgumentException("Count must be greater than 0.");

            _chNames = Enumerable.Range(0, count).Select(i => $"Ch{i}").ToArray();
        }

        // 内部用のプライベートコンストラクタ（Clone用）
        private ColorChannel(int count, string[] names, uint[]? colors, double[]? wavelengths)
             : base(count, 0, count - 1, "Channel", unit: "", isIndexBasedAxis: true)
        {
            _chNames = new string[count];
            Array.Copy(names, _chNames, count);

            if (colors != null)
            {
                _assignedColors = new uint[count];
                Array.Copy(colors, _assignedColors, count);
            }

            if (wavelengths != null)
            {
                _wavelengths = new double[count];
                Array.Copy(wavelengths, _wavelengths, count);
            }
        }

        public int this[string chName] => Array.IndexOf(_chNames, chName);

        public bool HasAssignedColors => _assignedColors != null;
        public bool HasWavelengths => _wavelengths != null;

        public string[] ChannelNames => _chNames.ToArray(); // 安全のためコピーを返す

        public void AssignChannelNames(string[] chNames)
        {
            if (chNames.Length != _chNames.Length)
                throw new ArgumentException($"Expected {_chNames.Length} names, but got {chNames.Length}.");

            for (int i = 0; i < chNames.Length; i++)
            {
                if (string.IsNullOrEmpty(chNames[i]))
                    throw new ArgumentException($"Channel name at index {i} cannot be null/empty.");
                _chNames[i] = chNames[i];
            }
        }

        public void AssignColors(uint[] colors)
        {
            if (colors.Length != _chNames.Length)
                throw new ArgumentException($"Expected {_chNames.Length} colors, but got {colors.Length}.");

            _assignedColors = new uint[colors.Length];
            Array.Copy(colors, _assignedColors, colors.Length);
        }

        public void SetColor(int index, uint argb)
        {
            EnsureColorsAssigned();
            ValidateIndex(index);
            _assignedColors![index] = argb;
        }

        public void SetColor(string chName, uint argb)
        {
            int index = this[chName];
            if (index == -1) throw new ArgumentException($"Channel '{chName}' not found.");
            SetColor(index, argb);
        }

        public uint GetColor(int index)
        {
            EnsureColorsAssigned();
            ValidateIndex(index);
            return _assignedColors![index];
        }

        public uint GetColor(string chName)
        {
            int index = this[chName];
            if (index == -1) throw new ArgumentException($"Channel '{chName}' not found.");
            return GetColor(index);
        }

        public void AssignWavelengths(double[] wavelengths)
        {
            if (wavelengths.Length != _chNames.Length)
                throw new ArgumentException($"Expected {_chNames.Length} wavelengths, but got {wavelengths.Length}.");

            _wavelengths = new double[wavelengths.Length];
            Array.Copy(wavelengths, _wavelengths, wavelengths.Length);
        }

        public double GetWavelength(int index)
        {
            EnsureWavelengthsAssigned();
            ValidateIndex(index);
            return _wavelengths![index];
        }

        public void SetWavelength(int index, double wavelength)
        {
            EnsureWavelengthsAssigned();
            ValidateIndex(index);
            _wavelengths![index] = wavelength;
        }

        // --- Helper Methods ---
        private void ValidateIndex(int index)
        {
            if (index < 0 || index >= _chNames.Length)
                throw new ArgumentOutOfRangeException(nameof(index), $"Index must be 0 to {_chNames.Length - 1}.");
        }

        private void EnsureColorsAssigned()
        {
            if (_assignedColors == null)
                throw new InvalidOperationException("Colors not assigned. Call AssignColors() first.");
        }

        private void EnsureWavelengthsAssigned()
        {
            if (_wavelengths == null)
                throw new InvalidOperationException("Wavelengths not assigned. Call AssignWavelengths() first.");
        }

        // ★重要: Cloneのオーバーライド
        public override Axis Clone()
        {
            // 専用のprivateコンストラクタを使ってディープコピーを作成
            var clone = new ColorChannel(this.Count, this._chNames, this._assignedColors, this._wavelengths);

            // Axis(基底クラス)のプロパティも忘れずコピー
            clone.Index = this.Index;
            clone.Name = this.Name;
            clone.Unit = this.Unit;
            // Min/Max/IsIndexBased はコンストラクタで設定済み

            return clone;
        }
    }
}
