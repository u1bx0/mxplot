using System.Text;
using MxPlot.Core;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Metadata of an ImageJ-compatible TIFF: the contents of the ImageDescription tag
/// (channels, slices, frames, units, origins and spacing).
/// </summary>
public class ImageJMetadata
{
    //public readonly  int ImageJPropertyTag = 50839;
    //public readonly int ImageJPropertyByteCountTag = 50838;

    public int Images { get; set; }
    public int Channels { get; set; } = 1;
    public int Slices { get; set; } = 1;
    public int Frames { get; set; } = 1;
    public bool Hyperstack => (Channels > 1) || (Slices > 1) || (Frames > 1);
    
    public string? Unit { get; set; }
    public string? YUnit { get; set; }
    public string? ZUnit { get; set; }
    
    public double XOrigin { get; set; }
    public double YOrigin { get; set; }
    public double ZOrigin { get; set; }
    
    public double Spacing { get; set; } = 1.0;
    public double Interval { get; set; } = 1.0;

    /// <summary>
    /// ImageDescription keys recording the names of the axes stored as slices and frames, when
    /// they are not the standard Z / Time. ImageJ ignores them.
    /// </summary>
    internal const string ZAxisNameKey = "mxplot.axis.z";
    internal const string TAxisNameKey = "mxplot.axis.t";

    /// <summary>Recorded name of the axis stored as slices; null when it is the standard Z.</summary>
    internal string? ZAxisName { get; set; }

    /// <summary>Recorded name of the axis stored as frames; null when it is the standard Time.</summary>
    internal string? TAxisName { get; set; }

    /// <summary>
    /// Returns true when the axes of <paramref name="data"/> can be described as an ImageJ
    /// hyperstack (channels, slices, frames). Axes named Channel, Z and Time/Timelapse map to their
    /// own dimension; any other axis is stored as slices, or as frames when slices is taken.
    /// Data that does not fit is saved as a plain stack, without channels/slices/frames.
    /// </summary>
    public static bool IsCompatible(IMatrixData data)
        => ImageJAxisLayout.TryCreate(data) != null;

    /// <summary>
    /// Creates the metadata of <paramref name="data"/>. The channels/slices/frames counts are set
    /// only when the axes fit an ImageJ hyperstack (see <see cref="IsCompatible"/>).
    /// </summary>
    public static ImageJMetadata FromMatrixData(IMatrixData data)
        => FromMatrixData(data, ImageJAxisLayout.TryCreate(data));

    internal static ImageJMetadata FromMatrixData(IMatrixData data, ImageJAxisLayout? layout)
    {
        var metadata = new ImageJMetadata
        {
            Images = data.FrameCount,
            Unit = data.XUnit,
            YUnit = data.YUnit
        };

        var series = data.Dimensions;

        if (layout?.ChannelAxis is { } channelName)
            metadata.Channels = series[channelName]!.Count;

        if (layout?.ZAxis is { } zName)
        {
            var zAxis = series[zName]!;
            metadata.Slices = zAxis.Count;
            metadata.Spacing = zAxis.Step;
            metadata.ZUnit = zAxis.Unit;
            metadata.ZOrigin = -zAxis.Min / zAxis.Step;
            metadata.ZAxisName = layout.ZNameToRecord;
        }

        if (layout?.TAxis is { } tName)
        {
            var tAxis = series[tName]!;
            metadata.Frames = tAxis.Count;
            metadata.Interval = tAxis.Step;
            metadata.TAxisName = layout.TNameToRecord;
        }

        metadata.XOrigin = -data.XMin * (data.XCount - 1) / (data.XMax - data.XMin);
        metadata.YOrigin = -data.YMin * (data.YCount - 1) / (data.YMax - data.YMin);
        
        return metadata;
    }
    
    /// <summary>
    /// ImageDescription文字列を生成
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine("ImageJ=1.0");
        sb.AppendLine($"images={Images}");
        
        if (Channels > 1)
            sb.AppendLine($"channels={Channels}");
        if (Slices > 1)
            sb.AppendLine($"slices={Slices}");
        if (Frames > 1)
            sb.AppendLine($"frames={Frames}");
        
        if (Hyperstack)
            sb.AppendLine("hyperstack=true");
        
        if (!string.IsNullOrEmpty(Unit))
            sb.AppendLine($"unit={Unit}");
        if (!string.IsNullOrEmpty(YUnit) && YUnit != Unit)
            sb.AppendLine($"yunit={YUnit}");
        
        sb.AppendLine($"xorigin={XOrigin}");
        sb.AppendLine($"yorigin={YOrigin}");
        
        if (Slices > 1)
        {
            sb.AppendLine($"spacing={Spacing}");
            if (!string.IsNullOrEmpty(ZUnit))
                sb.AppendLine($"zunit={ZUnit}");
            sb.AppendLine($"zorigin={ZOrigin}");
        }
        
        if (Frames > 1)
            sb.AppendLine($"interval={Interval}");

        if (Slices > 1 && ZAxisName != null)
            sb.AppendLine($"{ZAxisNameKey}={EscapeJavaUnicode(ZAxisName)}");
        if (Frames > 1 && TAxisName != null)
            sb.AppendLine($"{TAxisNameKey}={EscapeJavaUnicode(TAxisName)}");

        sb.AppendLine("mode=color");
        sb.AppendLine("loop=false");
        
        return sb.ToString();
    }
    
    /// <summary>
    /// ImageDescription文字列から解析
    /// </summary>
    public static ImageJMetadata? Parse(string imageDescription)
    {
        if (string.IsNullOrEmpty(imageDescription))
            return null;
        
        string[] lines = imageDescription.Split('\n');
        if (lines.Length < 2 || !lines[0].StartsWith("ImageJ"))
            return null;
        
        var metadata = new ImageJMetadata();
        
        foreach (string line in lines)
        {
            if (line.Contains('='))
            {
                var parts = line.Split('=', 2);
                string key = parts[0].Trim();
                string value = parts[1].Trim();
                
                switch (key)
                {
                    case "images": metadata.Images = int.Parse(value); break;
                    case "channels": metadata.Channels = int.Parse(value); break;
                    case "slices": metadata.Slices = int.Parse(value); break;
                    case "frames": metadata.Frames = int.Parse(value); break;
                    case "unit": metadata.Unit = UnescapeJavaUnicode(value); break;
                    case "yunit": metadata.YUnit = UnescapeJavaUnicode(value); break;
                    case "zunit": metadata.ZUnit = UnescapeJavaUnicode(value); break;
                    case "xorigin": metadata.XOrigin = double.Parse(value); break;
                    case "yorigin": metadata.YOrigin = double.Parse(value); break;
                    case "zorigin": metadata.ZOrigin = double.Parse(value); break;
                    case "spacing": metadata.Spacing = double.Parse(value); break;
                    case "interval": metadata.Interval = double.Parse(value); break;
                    case ZAxisNameKey: metadata.ZAxisName = UnescapeJavaUnicode(value); break;
                    case TAxisNameKey: metadata.TAxisName = UnescapeJavaUnicode(value); break;
                }
            }
        }
        
        return metadata;
    }

    // The ImageDescription tag is ASCII-only, so non-ASCII characters are written as \uXXXX
    // (the inverse of UnescapeJavaUnicode).
    private static string EscapeJavaUnicode(string value)
    {
        if (value.All(c => c < 0x7F))
            return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            if (c < 0x7F)
                sb.Append(c);
            else
                sb.Append("\\u").Append(((int)c).ToString("x4"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decodes Java <c>Properties</c>-style <c>\uXXXX</c> escapes (the "native2ascii" convention
    /// ImageJ uses when serializing non-ASCII characters into the ASCII-only TIFF ImageDescription
    /// tag -- e.g. the micro sign in "unit=micron" is written as the literal 6 characters
    /// <c>µ</c>, not as an encoded byte sequence). Left untouched if no such escape is present.
    /// </summary>
    private static string UnescapeJavaUnicode(string value)
    {
        if (!value.Contains("\\u")) return value;

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 5 < value.Length && value[i + 1] == 'u'
                && ushort.TryParse(value.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out ushort code))
            {
                sb.Append((char)code);
                i += 5;
            }
            else
            {
                sb.Append(value[i]);
            }
        }
        return sb.ToString();
    }
}
