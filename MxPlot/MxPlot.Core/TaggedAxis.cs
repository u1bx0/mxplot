using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// Represents an axis defined by a set of tags, enabling tag-based indexing and lookup.
    /// </summary>
    /// <remarks>
    /// This class serves as the base for tag-oriented axes such as <see cref="ColorChannel"/>.
    /// A defensive copy of the provided tag array is created to protect internal state.
    /// </remarks>
    public class TaggedAxis : Axis
    {
        private readonly string[] _tags;

        /// <summary>
        /// Occurs when a tag value is modified via <see cref="SetTag"/>.
        /// </summary>
        public event EventHandler? TagNameChanged;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaggedAxis"/> class with the specified tags and axis name.
        /// </summary>
        /// <param name="tags">The tags associated with this axis. Must not be null or empty.</param>
        /// <param name="axisName">The axis name. Defaults to "Tag".</param>
        public TaggedAxis(string[] tags, string axisName = "Tag")
            : base(tags.Length, 0, tags.Length - 1, axisName, "", isIndexBased: true)
        {
            if (tags is null || tags.Length == 0)
                throw new ArgumentException("No valid tags are provided.", nameof(tags));

            // Check duplication
            if (tags.Distinct().Count() != tags.Length)
                throw new ArgumentException("Duplicate tags are not allowed.", nameof(tags));

            _tags = (string[])tags.Clone();// defensive copy
        }

        /// <summary>
        /// Gets the list of tags associated with this axis.
        /// </summary>
        public IReadOnlyList<string> Tags => _tags;

        /// <summary>
        /// Gets the zero-based index of the specified tag, or -1 if the tag does not exist.
        /// </summary>
        public int this[string tag] => Array.IndexOf(_tags, tag);

        /// <summary>
        /// Gets the tag at the current <see cref="Axis.Index"/>.
        /// </summary>
        public string CurrentTag => _tags[Index];

        /// <summary>
        /// Gets the tag at the specified index.
        /// </summary>
        public string this[int index] => _tags[index];

        /// <summary>
        /// Updates the tag at the specified index and raises <see cref="TagNameChanged"/>.
        /// </summary>
        public void SetTag(int index, string newTag)
        {
            if (index < 0 || index >= _tags.Length)
                throw new IndexOutOfRangeException($"Index {index} is out of range.");

            if (_tags[index] == newTag)
                return;

            // Check duplication
            if (Array.IndexOf(_tags, newTag) != -1)
                throw new ArgumentException($"The tag '{newTag}' already exists.", nameof(newTag));

            _tags[index] = newTag;
            TagNameChanged?.Invoke(this, EventArgs.Empty);
        }

        public override TaggedAxis Clone() => new TaggedAxis(_tags, Name);

        /// <summary>
        /// Creates a <see cref="TaggedAxis"/> with the default axis name "Tag".
        /// Example: <c>TaggedAxis.Of("Sensor1", "Sensor2", "Sensor3")</c>.
        /// <para>
        /// Note: When using <c>DefineDimensions</c>, creating multiple
        /// <see cref="TaggedAxis"/> instances inline will cause name conflicts
        /// because they all start with the default name "Tag".
        /// Use this factory method first, then assign unique names.
        /// </para>
        /// </summary>
        public static TaggedAxis Of(params string[] tags) => new TaggedAxis(tags);
    }

}
