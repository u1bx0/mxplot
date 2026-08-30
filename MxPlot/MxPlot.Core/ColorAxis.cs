using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace MxPlot.Core
{
    /// <summary>
    /// Represents a tag-based axis specialized for color channels.
    /// </summary>
    /// <remarks>
    /// This axis uses the default name "Channel", and therefore cannot be used
    /// together with <see cref="Axis.Channel"/> in the same dimension definition.
    /// </remarks>
    public class ColorAxis : TaggedAxis
    {
        private int[]? _assignedColors;
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
        /// JSON deserialization constructor.
        /// <c>tags</c> is passed to <see cref="TaggedAxis"/>; <c>Name</c> is restored via setter.
        /// </summary>
        [JsonConstructor]
        private ColorAxis(IReadOnlyList<string> tags,
                             IReadOnlyList<int>? assignedColors, IReadOnlyList<double>? wavelengths)
            : base(tags)
        {
            _assignedColors = assignedColors as int[] ?? assignedColors?.ToArray();
            _wavelengths = wavelengths as double[] ?? wavelengths?.ToArray();
        }

        /// <summary>
        /// Initializes a new <see cref="ColorAxis"/> using the specified channel tags.
        /// Example: <c>new ColorAxis("Red", "Green", "Blue")</c>.
        /// </summary>
        public ColorAxis(params string[] chTags)
            : base(chTags)
        {
            Name = "Channel";
        }

        /// <summary>
        /// Initializes a new <see cref="ColorAxis"/> with default names
        /// ("Ch0", "Ch1", …) based on the specified count.
        /// </summary>
        public ColorAxis(int count)
            : base(Enumerable.Range(0, count).Select(i => $"Ch{i}").ToArray())
        {
            Name = "Channel";
        }

        /// <summary>
        /// Creates the standard R/G/B channel axis used by decomposed colour images.
        /// </summary>
        /// <remarks>
        /// Pure primaries are required: an additive composite of R*(1,0,0) + G*(0,1,0) + B*(0,0,1)
        /// reproduces the original (R,G,B) pixel exactly. Any desaturation introduces cross-channel
        /// bleeding and breaks that reconstruction.
        /// </remarks>
        public static ColorAxis CreateRgb()
        {
            var ch = new ColorAxis("R", "G", "B");
            ch.AssignColors(
            [
                unchecked((int)0xFFFF0000),   // R -> pure red
                unchecked((int)0xFF00FF00),   // G -> pure green
                unchecked((int)0xFF0000FF),   // B -> pure blue
            ]);
            return ch;
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="axis"/> is a three-tag channel axis whose tags
        /// are R/G/B (or Red/Green/Blue), i.e. it came from a decomposed colour image rather than
        /// from a fluorescence stack. Used to decide RGB-specific defaults (composite colours,
        /// luma weights) without hard-coding them for every three-channel dataset.
        /// </summary>
        public static bool IsRgbTriplet(Axis? axis)
        {
            if (axis is not TaggedAxis tagged || tagged.Tags.Count != 3)
                return false;

            return Matches(tagged.Tags[0], "R", "Red")
                && Matches(tagged.Tags[1], "G", "Green")
                && Matches(tagged.Tags[2], "B", "Blue");

            static bool Matches(string tag, string shortName, string longName)
                => tag.Equals(shortName, StringComparison.OrdinalIgnoreCase)
                || tag.Equals(longName, StringComparison.OrdinalIgnoreCase);
        }

        public bool HasAssignedColors => _assignedColors != null;
        public bool HasWavelengths => _wavelengths != null;

        /// <summary>
        /// Gets the assigned colors, or <c>null</c> if no colors are assigned.
        /// </summary>
        public IReadOnlyList<int>? AssignedColors => _assignedColors;

        /// <summary>
        /// Gets the assigned wavelengths, or <c>null</c> if no wavelengths are assigned.
        /// </summary>
        public IReadOnlyList<double>? Wavelengths => _wavelengths;

        /// <summary>
        /// Assigns colors to each channel. The array length must match the number of tags.
        /// Passing <c>null</c> removes any existing assignment.
        /// </summary>
        public void AssignColors(int[]? colors)
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
        public void SetColor(int index, int argb)
        {
            EnsureColorsAssigned();
            ValidateIndex(index);
            _assignedColors![index] = argb;
            ColorAssignChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Sets the color for the specified channel tag.
        /// </summary>
        public void SetColor(string chTag, int argb)
        {
            int index = this[chTag];
            if (index == -1)
                throw new ArgumentException($"Channel '{chTag}' not found.");

            SetColor(index, argb);
        }

        public int GetColor(int index)
        {
            EnsureColorsAssigned();
            ValidateIndex(index);
            return _assignedColors![index];
        }

        public int GetColor(string chTag)
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

        /// <summary>
        /// Deep-copies the channel identity (tags, colours, wavelengths, name, unit) but not the
        /// current <see cref="Axis.Index"/> - matching every other <see cref="Axis"/> subtype's
        /// <see cref="Axis.Clone"/>, none of which carry position across a clone either. Callers
        /// that need the position too (e.g. axis-object promotion in place) set <c>Index</c>
        /// explicitly on the result.
        /// </summary>
        public ColorAxis CloneTyped()
        {
            var clone = new ColorAxis(Tags.ToArray())
            {
                Name = Name,
                Unit = Unit,
            };

            if (HasAssignedColors)
                clone.AssignColors(_assignedColors!);

            if (HasWavelengths)
                clone.AssignWavelengths(_wavelengths!);

            return clone;
        }

        public override ColorAxis Clone() => CloneTyped();

        /// <summary>
        /// Narrows tags/colours/wavelengths to <c>[start, start + count)</c> instead of dropping
        /// them - e.g. Substack-ing a Channel axis down to a subset of channels keeps their names
        /// and assigned colours.
        /// </summary>
        public override ColorAxis Slice(int start, int count)
        {
            if (start < 0 || count <= 0 || start + count > Count)
                throw new ArgumentOutOfRangeException(nameof(start),
                    $"[{start}, {start + count}) is out of range for an axis of Count={Count}.");

            var slicedTags = Tags.Skip(start).Take(count).ToArray();
            var clone = new ColorAxis(slicedTags)
            {
                Name = Name,
                Unit = Unit,
            };

            if (HasAssignedColors)
                clone.AssignColors(_assignedColors!.Skip(start).Take(count).ToArray());

            if (HasWavelengths)
                clone.AssignWavelengths(_wavelengths!.Skip(start).Take(count).ToArray());

            return clone;
        }
    }

}
