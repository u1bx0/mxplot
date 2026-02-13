using System.Text;
using MxPlot.Core;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// ImageJ互換のメタデータ（Channel, Z, Timeのみ対応）
/// </summary>
public class ImageJMetadata
{
    public readonly  int ImageJPropertyTag = 50839;
    public readonly int ImageJPropertyByteCountTag = 50838;

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
    /// MatrixDataがImageJ Hyperstack互換か判定（Channel, Z, Time/Timelapse のみ）
    /// </summary>
    public static bool IsCompatible(IMatrixData data)
    {
        var series = data.Dimensions;
        if (series.AxisCount == 0)
            return true;
        
        foreach (var axis in series.Axes)
        {
            string name = axis.Name;
            if (name != "Channel" && name != "Z" && 
                name != "Time" && name != "Timelapse")
            {
                return false; // 非対応軸
            }
        }
        return true;
    }
    
    /// <summary>
    /// MatrixDataからImageJMetadataを生成
    /// </summary>
    public static ImageJMetadata FromMatrixData(IMatrixData data)
    {
        var metadata = new ImageJMetadata
        {
            Images = data.FrameCount,
            Unit = data.XUnit,
            YUnit = data.YUnit
        };
        
        var series = data.Dimensions;
        
        if (series.Contains("Channel"))
            metadata.Channels = series["Channel"]!.Count;
        
        if (series.Contains("Z"))
        {
            var zAxis = series["Z"];
            metadata.Slices = zAxis!.Count;
            metadata.Spacing = zAxis.Step;
            metadata.ZUnit = zAxis.Unit;
            metadata.ZOrigin = -zAxis.Min / zAxis.Step;
        }
        
        string? timeKey = series.Contains("Time") ? "Time" : 
                         (series.Contains("Timelapse") ? "Timelapse" : null);
        if (timeKey != null)
        {
            var tAxis = series[timeKey];
            metadata.Frames = tAxis!.Count;
            metadata.Interval = tAxis.Step;
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
                    case "unit": metadata.Unit = value; break;
                    case "yunit": metadata.YUnit = value; break;
                    case "zunit": metadata.ZUnit = value; break;
                    case "xorigin": metadata.XOrigin = double.Parse(value); break;
                    case "yorigin": metadata.YOrigin = double.Parse(value); break;
                    case "zorigin": metadata.ZOrigin = double.Parse(value); break;
                    case "spacing": metadata.Spacing = double.Parse(value); break;
                    case "interval": metadata.Interval = double.Parse(value); break;
                }
            }
        }
        
        return metadata;
    }
}
