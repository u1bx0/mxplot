using MxPlot.Core;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Assigns the axes of an <see cref="IMatrixData"/> to the three dimensions an ImageJ hyperstack
/// can describe: channels (c), slices (z) and frames (t).
/// </summary>
/// <remarks>
/// <para>
/// Axes named <c>Channel</c>, <c>Z</c> and <c>Time</c>/<c>Timelapse</c> take their own dimension.
/// Any other axis with more than one position goes to the first dimension still free, <c>z</c>
/// before <c>t</c>, in axis order; it is never assigned to <c>c</c>. When there are more such axes
/// than free dimensions the data cannot be described as a hyperstack and
/// <see cref="TryCreate"/> returns <see langword="null"/>.
/// </para>
/// <para>
/// ImageJ stores no axis names, so the name of a reassigned axis is recorded in the
/// ImageDescription (<see cref="ImageJMetadata.ZAxisNameKey"/> / <see cref="ImageJMetadata.TAxisNameKey"/>)
/// and applied again on load.
/// </para>
/// </remarks>
internal sealed class ImageJAxisLayout
{
    /// <summary>Name of the axis stored as ImageJ channels, or <see langword="null"/>.</summary>
    public string? ChannelAxis { get; }

    /// <summary>Name of the axis stored as ImageJ slices, or <see langword="null"/>.</summary>
    public string? ZAxis { get; }

    /// <summary>Name of the axis stored as ImageJ frames, or <see langword="null"/>.</summary>
    public string? TAxis { get; }

    /// <summary>
    /// The name to record when the slices axis is not the standard <c>Z</c> axis, or
    /// <see langword="null"/> when nothing needs to be recorded (or the name cannot be stored).
    /// </summary>
    public string? ZNameToRecord => ZAxis != "Z" && IsStorable(ZAxis) ? ZAxis : null;

    /// <summary>
    /// The name to record when the frames axis is not the standard <c>Time</c>/<c>Timelapse</c>
    /// axis, or <see langword="null"/> when nothing needs to be recorded (or the name cannot be stored).
    /// </summary>
    public string? TNameToRecord => TAxis is not ("Time" or "Timelapse") && IsStorable(TAxis) ? TAxis : null;

    private ImageJAxisLayout(string? channelAxis, string? zAxis, string? tAxis)
    {
        ChannelAxis = channelAxis;
        ZAxis = zAxis;
        TAxis = tAxis;
    }

    /// <summary>
    /// Returns the layout of <paramref name="data"/>, or <see langword="null"/> when its axes
    /// do not fit into channels, slices and frames.
    /// </summary>
    public static ImageJAxisLayout? TryCreate(IMatrixData data)
    {
        string? channel = null, z = null, t = null;
        List<string>? others = null;

        foreach (var axis in data.Dimensions.Axes)
        {
            string name = axis.Name;
            if (name == "Channel")
                channel = name;
            else if (name == "Z")
                z = name;
            else if ((name == "Time" || name == "Timelapse") && t == null)
                t = name;
            else if (axis.Count > 1)
                (others ??= []).Add(name); // a single-position axis needs no dimension
        }

        if (others != null)
        {
            foreach (string name in others)
            {
                if (z == null)
                    z = name;
                else if (t == null)
                    t = name;
                else
                    return null;
            }
        }

        return new ImageJAxisLayout(channel, z, t);
    }

    /// <summary>
    /// Index of the frame at (<paramref name="c"/>, <paramref name="z"/>, <paramref name="t"/>)
    /// in <paramref name="data"/>. A dimension without an axis is ignored.
    /// </summary>
    public int GetFrameIndex(IMatrixData data, int c, int z, int t)
    {
        var dims = data.Dimensions;
        if (dims.AxisCount == 0)
            return 0;

        int[] axisIndices = new int[dims.AxisCount];
        if (ChannelAxis != null)
            axisIndices[dims.GetAxisOrder(dims[ChannelAxis]!)] = c;
        if (ZAxis != null)
            axisIndices[dims.GetAxisOrder(dims[ZAxis]!)] = z;
        if (TAxis != null)
            axisIndices[dims.GetAxisOrder(dims[TAxis]!)] = t;

        return dims.GetFrameIndexAt(axisIndices);
    }

    // The ImageDescription is a list of "key=value" lines in Java Properties style: values are
    // trimmed on read, and a backslash starts an escape. A name that spans lines, has outer
    // whitespace or contains a backslash would not come back unchanged.
    private static bool IsStorable(string? name)
        => !string.IsNullOrEmpty(name) && name == name.Trim() && name.IndexOfAny(['\r', '\n', '\\']) < 0;
}
