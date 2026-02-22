using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// Represents a tag-based axis specialized for color channels.
    /// </summary>
    /// <remarks>
    /// This axis uses the default name "Channel", and therefore cannot be used
    /// together with <see cref="Axis.Channel"/> in the same dimension definition.
    /// </remarks>
    public class ColorChannel : TaggedAxis
    {
        private uint[]? _assignedColors;
        private double[]? _wavelengths;

        /// <summary>
        /// Occurs when assigned colors are modified.
        /// </summary>
        public event EventHandler? ColorAssignChanged;

        /// <summary>
        /// Occurs when assigned wavelengths are modified.
        /// </summary>
        public event EventHandler? WavelengthAssignChanged;

        /// <summary>
        /// Initializes a new <see cref="ColorChannel"/> using the specified channel tags.
        /// Example: <c>new ColorChannel("Red", "Green", "Blue")</c>.
        /// </summary>
        public ColorChannel(params string[] chTags)
            : base(chTags)
        {
            Name = "Channel";
        }

        /// <summary>
        /// Initializes a new <see cref="ColorChannel"/> with default names
        /// ("Ch0", "Ch1", …) based on the specified count.
        /// </summary>
        public ColorChannel(int count)
            : base(Enumerable.Range(0, count).Select(i => $"Ch{i}").ToArray())
        {
            Name = "Channel";
        }

        public bool HasAssignedColors => _assignedColors != null;
        public bool HasWavelengths => _wavelengths != null;

        /// <summary>
        /// Gets the assigned colors, or <c>null</c> if no colors are assigned.
        /// </summary>
        public IReadOnlyList<uint>? AssignedColors => _assignedColors;

        /// <summary>
        /// Gets the assigned wavelengths, or <c>null</c> if no wavelengths are assigned.
        /// </summary>
        public IReadOnlyList<double>? Wavelengths => _wavelengths;

        /// <summary>
        /// Assigns colors to each channel. The array length must match the number of tags.
        /// Passing <c>null</c> removes any existing assignment.
        /// </summary>
        public void AssignColors(uint[]? colors)
        {
            if (colors is null)
            {
                _assignedColors = null;
                ColorAssignChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (colors.Length != Tags.Count)
                throw new ArgumentException($"Expected {Tags.Count} colors, but got {colors.Length}.");

            _assignedColors = colors.ToArray();
            ColorAssignChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Sets the color for the specified channel index.
        /// </summary>
        public void SetColor(int index, uint argb)
        {
            EnsureColorsAssigned();
            ValidateIndex(index);
            _assignedColors![index] = argb;
            ColorAssignChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Sets the color for the specified channel tag.
        /// </summary>
        public void SetColor(string chTag, uint argb)
        {
            int index = this[chTag];
            if (index == -1)
                throw new ArgumentException($"Channel '{chTag}' not found.");

            SetColor(index, argb);
        }

        public uint GetColor(int index)
        {
            EnsureColorsAssigned();
            ValidateIndex(index);
            return _assignedColors![index];
        }

        public uint GetColor(string chTag)
        {
            int index = this[chTag];
            if (index == -1)
                throw new ArgumentException($"Channel '{chTag}' not found.");

            return GetColor(index);
        }

        /// <summary>
        /// Assigns wavelengths to each channel. The array length must match the number of tags.
        /// Passing <c>null</c> removes any existing assignment.
        /// </summary>
        public void AssignWavelengths(double[]? wavelengths)
        {
            if (wavelengths is null)
            {
                _wavelengths = null;
                WavelengthAssignChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (wavelengths.Length != Tags.Count)
                throw new ArgumentException($"Expected {Tags.Count} wavelengths, but got {wavelengths.Length}.");

            _wavelengths = wavelengths.ToArray();
            WavelengthAssignChanged?.Invoke(this, EventArgs.Empty);
        }

        public double GetWavelength(int index)
        {
            EnsureWavelengthsAssigned();
            ValidateIndex(index);
            return _wavelengths![index];
        }

        /// <summary>
        /// Sets the wavelength for the specified channel index.
        /// </summary>
        public void SetWavelength(int index, double wavelength)
        {
            EnsureWavelengthsAssigned();
            ValidateIndex(index);
            _wavelengths![index] = wavelength;
            WavelengthAssignChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetWavelength(string chTag, double wavelength)
        {
            int index = this[chTag];
            if (index == -1)
                throw new ArgumentException($"Channel '{chTag}' not found.");

            SetWavelength(index, wavelength);
        }

        // --- Helper Methods ---
        private void ValidateIndex(int index)
        {
            if (index < 0 || index >= Tags.Count)
                throw new ArgumentOutOfRangeException(nameof(index), $"Index must be 0 to {Tags.Count - 1}.");
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

        public ColorChannel CloneTyped()
        {
            var clone = new ColorChannel(Tags.ToArray())
            {
                Index = Index,
                Name = Name,
                Unit = Unit,
            };

            if (HasAssignedColors)
                clone.AssignColors(_assignedColors!);

            if (HasWavelengths)
                clone.AssignWavelengths(_wavelengths!);

            return clone;
        }

        public override ColorChannel Clone() => CloneTyped();
    }

}
