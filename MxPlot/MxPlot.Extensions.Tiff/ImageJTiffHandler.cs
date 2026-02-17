using BitMiracle.LibTiff.Classic;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.IO;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// ImageJ互換のTIFFハンドラー（標準TIFF + ImageJ Hyperstack対応）
/// byte/ushort データ型のみサポート
/// </summary>
public static class ImageJTiffHandler
{
    /// <summary>
    /// ImageJ互換のTIFFファイルから読み込み
    /// </summary>
    /// <typeparam name="T">データ型（byte または ushort）</typeparam>
    /// <param name="filename">ファイルパス</param>
    /// <param name="progress">進捗報告（オプション）</param>
    /// <returns>読み込まれた MatrixData</returns>
    /// <exception cref="NotSupportedException">サポートされていないデータ型</exception>
    /// <exception cref="FileNotFoundException">ファイルが見つからない</exception>
    /// <exception cref="IOException">TIFF読み込みエラー</exception>
    public static MatrixData<T> Load<T>(string filename, IProgress<int>? progress = null)
        where T : unmanaged
    {
        ValidateDataType<T>();

        if (!File.Exists(filename))
            throw new FileNotFoundException($"File not found: {filename}");

        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r");
        if (tiff == null)
            throw new IOException($"Failed to open TIFF file: {filename}");

        return LoadInternal<T>(tiff, progress);
    }

    /// <summary>
    /// ImageJ互換のTIFFファイルに書き込み
    /// </summary>
    /// <typeparam name="T">データ型（byte または ushort）</typeparam>
    /// <param name="filename">ファイルパス</param>
    /// <param name="data">保存する MatrixData</param>
    /// <param name="progress">進捗報告（オプション）</param>
    /// <exception cref="NotSupportedException">サポートされていないデータ型、または非対応軸</exception>
    /// <exception cref="IOException">TIFF書き込みエラー</exception>
    public static void Save<T>(string filename, MatrixData<T> data, IProgress<int>? progress = null)
        where T : unmanaged
    {
        ValidateDataType<T>();

        // ImageJ Hyperstack 互換性チェック
        if (!ImageJMetadata.IsCompatible(data))
        {
            throw new NotSupportedException(
                "Data contains unsupported axes. Only Channel, Z, and Time/Timelapse are supported for ImageJ Hyperstack.");
        }

        // ディレクトリが存在しない場合は作成
        string? directory = Path.GetDirectoryName(filename);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // LibTiff.NET のエラーハンドラー設定
        BitMiracle.LibTiff.Classic.Tiff.SetErrorHandler(new LibTiffErrorHandler());
        
        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "w");
        if (tiff == null)
        {
            throw new IOException($"Failed to create TIFF file: {filename}. Check LibTiff error log for details.");
        }

        SaveInternal(tiff, data, progress);
    }

    /// <summary>
    /// LibTiff.NET のエラーハンドラー
    /// </summary>
    private class LibTiffErrorHandler : BitMiracle.LibTiff.Classic.TiffErrorHandler
    {
        public override void ErrorHandler(BitMiracle.LibTiff.Classic.Tiff tif, string method, string format, params object[] args)
        {
            string message = string.Format(format, args);
            System.Diagnostics.Debug.WriteLine($"[LibTiff Error] {method}: {message}");
            Console.WriteLine($"[LibTiff Error] {method}: {message}");
        }

        public override void WarningHandler(BitMiracle.LibTiff.Classic.Tiff tif, string method, string format, params object[] args)
        {
            string message = string.Format(format, args);
            System.Diagnostics.Debug.WriteLine($"[LibTiff Warning] {method}: {message}");
        }
    }

    #region Load Implementation

    private static MatrixData<T> LoadInternal<T>(BitMiracle.LibTiff.Classic.Tiff tiff, IProgress<int>? progress)
        where T : unmanaged
    {
        // --- 追加：ファイル側の型を確認 ---
        short bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToShort();
        int expectedBytes = (typeof(T) == typeof(byte)) ? 8 : 16;

        if (bitsPerSample != expectedBytes)
        {
            // ここでエラーにするか、あるいは自動変換ロジックに分岐させる必要がある
            throw new InvalidDataException($"File is {bitsPerSample}bit, but {expectedBytes}bit was requested.");
        }
        // ------------------------------
        

        // 1. 基本情報の読み込み
        int width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int directoryCount = tiff.NumberOfDirectories();

        progress?.Report(-directoryCount); // 処理開始

        // 2. ImageJ メタデータの読み込み
        var ijMetadata = ReadImageJMetadata(tiff);

        // 3. Resolution 情報の読み込み
        var (xResolution, yResolution) = ReadResolution(tiff);

        // 4. MatrixData の作成
        var data = new MatrixData<T>(width, height, directoryCount);

        // 5. スケール設定
        if (ijMetadata != null)
        {
            double xPitch = 1.0 / xResolution;
            double yPitch = 1.0 / yResolution;
            double xMin = -ijMetadata.XOrigin * xPitch;
            double xMax = (width - 1 - ijMetadata.XOrigin) * xPitch;
            double yMin = -ijMetadata.YOrigin * yPitch;
            double yMax = (height - 1 - ijMetadata.YOrigin) * yPitch;
            data.SetXYScale(xMin, xMax, yMin, yMax);
            data.XUnit = ijMetadata!.Unit ?? "";
            data.YUnit = ijMetadata.YUnit ?? ijMetadata!.Unit ?? "";
        }

        // 6. 全フレームを読み込み
        for (int i = 0; i < directoryCount; i++)
        {
            tiff.SetDirectory((short)i);
            T[] frameData = ReadFrameData<T>(tiff, width, height);
            data.SetArray(frameData, i);

            progress?.Report(i);
        }

        // 7. Dimension 設定
        if (ijMetadata != null && ijMetadata.Hyperstack)
        {
            SetDimensionsFromImageJ(data, ijMetadata);
        }

        // 8. Metadata の読み込み
        ReadCustomMetadata(tiff, data);

        progress?.Report(directoryCount); // 完了

        return data;
    }

    private static ImageJMetadata? ReadImageJMetadata(BitMiracle.LibTiff.Classic.Tiff tiff)
    {
        var imageDesc = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
        if (imageDesc == null || imageDesc.Length == 0)
            return null;

        string description = imageDesc[0].ToString();
        return ImageJMetadata.Parse(description);
    }

    private static (double xResolution, double yResolution) ReadResolution(BitMiracle.LibTiff.Classic.Tiff tiff)
    {
        var xres = tiff.GetField(TiffTag.XRESOLUTION);
        var yres = tiff.GetField(TiffTag.YRESOLUTION);

        if (xres != null && yres != null)
        {
            double xResolution = xres[0].ToDouble();
            double yResolution = yres[0].ToDouble();
            return (xResolution, yResolution);
        }

        return (1.0, 1.0); // デフォルト
    }

    private static T[] ReadFrameData<T>(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
        where T : unmanaged
    {
        int bytesPerPixel = GetBytesPerPixel<T>();
        var imageData = new T[width * height];
        var buffer = new byte[width * height * bytesPerPixel];

        int stride = width * bytesPerPixel;
        for (int row = 0; row < height; row++)
        {
            if (!tiff.ReadScanline(buffer, row * stride, row, 0))
            {
                throw new IOException($"Failed to read scanline at row {row}");
            }
        }

        // Y軸反転: TIFF (左上原点) → MatrixData (左下原点)
        var flipped = new T[width * height];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int srcIndex = row * width + col;
                int dstIndex = (height - 1 - row) * width + col;
                flipped[dstIndex] = GetValue<T>(buffer, srcIndex * bytesPerPixel);
            }
        }

        return flipped;
    }

    private static void SetDimensionsFromImageJ(IMatrixData data, ImageJMetadata metadata)
    {
        var axes = new List<Axis>();

        if (metadata.Channels > 1)
            axes.Add(Axis.Channel(metadata.Channels));

        if (metadata.Slices > 1)
        {
            double zMin = -metadata.ZOrigin * metadata.Spacing;
            double zMax = (metadata.Slices - 1 - metadata.ZOrigin) * metadata.Spacing;
            axes.Add(Axis.Z(metadata.Slices, zMin, zMax, metadata.ZUnit ?? ""));
        }

        if (metadata.Frames > 1)
        {
            double tMax = (metadata.Frames - 1) * metadata.Interval;
            axes.Add(Axis.Time(metadata.Frames, 0, tMax, "s"));
        }

        if (axes.Count > 0)
            data.DefineDimensions(axes.ToArray());
    }

    #endregion

    #region Save Implementation

    private static void SaveInternal<T>(BitMiracle.LibTiff.Classic.Tiff tiff, MatrixData<T> data, IProgress<int>? progress)
        where T : unmanaged
    {
        int frameCount = data.FrameCount;
        progress?.Report(-frameCount); // 処理開始

        // ImageJ メタデータの生成
        var ijMetadata = ImageJMetadata.FromMatrixData(data);

        // フレーム順序の計算 (ImageJ は C-Z-T 順に並び替え)
        int[] frameOrder = CalculateFrameOrder(data, ijMetadata);

        // 各フレームを順に書き込み
        for (int i = 0; i < frameCount; i++)
        {
            int frameIndex = frameOrder[i];

            // 基本タグの設定
            SetBasicTiffTags(tiff, data.XCount, data.YCount, typeof(T));

            // 最初のフレームのみ追加情報を書き込み
            if (i == 0)
            {
                WriteImageJMetadata(tiff, ijMetadata);
                WriteResolution(tiff, data.XStep, data.YStep);
                WriteCustomMetadata(tiff, data);
            }

            // 画像データの書き込み
            T[] frameData = data.GetArray(frameIndex);
            WriteFrameData(tiff, frameData, data.XCount, data.YCount);

            if (i < frameCount - 1)
                tiff.WriteDirectory();

            progress?.Report(i);
        }

        progress?.Report(frameCount); // 完了
    }

    private static void SetBasicTiffTags(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height, Type dataType)
    {
        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);

        if (dataType == typeof(byte))
        {
            tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
            tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        }
        else if (dataType == typeof(ushort))
        {
            tiff.SetField(TiffTag.BITSPERSAMPLE, 16);
            tiff.SetField(TiffTag.SAMPLEFORMAT, SampleFormat.UINT);
        }

        // 圧縮設定 (LZW 圧縮)
        tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
        tiff.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL);

        // RowsPerStrip の設定
        int rowsPerStrip = CalculateOptimalRowsPerStrip(width, dataType);
        tiff.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip);
    }

    private static int CalculateOptimalRowsPerStrip(int width, Type dataType)
    {
        int bytesPerPixel = dataType == typeof(byte) ? 1 : 2;
        int bytesPerRow = width * bytesPerPixel;

        // 目標: 1ストリップあたり64KB
        const int targetStripSize = 64 * 1024;
        int optimalRows = Math.Max(1, targetStripSize / bytesPerRow);

        return optimalRows;
    }

    private static void WriteImageJMetadata(BitMiracle.LibTiff.Classic.Tiff tiff, ImageJMetadata metadata)
    {
        string description = metadata.ToString();
        tiff.SetField(TiffTag.IMAGEDESCRIPTION, description);
    }

    private static void WriteResolution(BitMiracle.LibTiff.Classic.Tiff tiff, double xPitch, double yPitch)
    {
        // pixels per unit
        float xResolution = (float)(1.0 / xPitch);
        float yResolution = (float)(1.0 / yPitch);

        tiff.SetField(TiffTag.XRESOLUTION, xResolution);
        tiff.SetField(TiffTag.YRESOLUTION, yResolution);
        tiff.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.NONE); // unitless
    }

    private static void WriteFrameData<T>(BitMiracle.LibTiff.Classic.Tiff tiff, T[] data, int width, int height)
        where T : unmanaged
    {
        int bytesPerPixel = GetBytesPerPixel<T>();
        var buffer = new byte[width * height * bytesPerPixel];

        // Y軸反転: MatrixData (左下原点) → TIFF (左上原点)
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int srcIndex = (height - 1 - row) * width + col;
                int dstIndex = row * width + col;
                SetValue(buffer, dstIndex * bytesPerPixel, data[srcIndex]);
            }
        }

        // 行ごとに書き込み
        int stride = width * bytesPerPixel;
        for (int row = 0; row < height; row++)
        {
            if (!tiff.WriteScanline(buffer, row * stride, row, 0))
            {
                throw new IOException($"Failed to write scanline at row {row}");
            }
        }
    }

    private static int[] CalculateFrameOrder(IMatrixData data, ImageJMetadata metadata)
    {
        int[] order = new int[data.FrameCount];

        // ImageJ の順序: XYCZT (C が最も早く変化)
        int index = 0;
        for (int t = 0; t < metadata.Frames; t++)
        {
            for (int z = 0; z < metadata.Slices; z++)
            {
                for (int c = 0; c < metadata.Channels; c++)
                {
                    order[index++] = CalculateMatrixDataIndex(data, c, z, t);
                }
            }
        }

        return order;
    }

    private static int CalculateMatrixDataIndex(IMatrixData data, int c, int z, int t)
    {
        var dims = data.Dimensions;
        if (dims.AxisCount == 0)
            return 0;

        var indices = new Dictionary<string, int>();

        int[] axisIndices = new int[dims.AxisCount];
        
        if (dims.Contains("Channel"))
        {
            int chOrder = dims.GetAxisOrder(dims["Channel"]!);
            axisIndices[chOrder] = c;
        }
        if (dims.Contains("Z"))
        {
            int zOrder = dims.GetAxisOrder(dims["Z"]!);
            axisIndices[zOrder] = z;
        }
        if (dims.Contains("Time"))
        {
            int tOrder = dims.GetAxisOrder(dims["Time"]!);
            axisIndices[tOrder] = t;
        }
        else if (dims.Contains("Timelapse"))
        {
            int tOrder = dims.GetAxisOrder(dims["Timelapse"]!);
            axisIndices[tOrder] = t;
        }

        return dims.GetFrameIndexAt(axisIndices);
    }

    #endregion

    #region Metadata Support

    /// <summary>
    /// ImageJ用のMetadataタグ (50839)
    /// </summary>
    private const int TiffTagIJMetadata = 50839;

    /// <summary>
    /// IJMetadataのバイト数を記録するタグ (50838)
    /// </summary>
    private const int TiffTagIJMetadataByteCounts = 50838;

    private static void ReadCustomMetadata(BitMiracle.LibTiff.Classic.Tiff tiff, IMatrixData data)
    {
        // IJMetadata (50839, 50838) の読み込み
        var ijMeta = tiff.GetField((TiffTag)TiffTagIJMetadata);
        var ijMetaBC = tiff.GetField((TiffTag)TiffTagIJMetadataByteCounts);

        if (ijMeta != null && ijMetaBC != null && ijMeta.Length > 0 && ijMetaBC.Length > 0)
        {
            byte[]? metaBytes = ijMeta[0].ToByteArray();
            
            if (metaBytes != null && metaBytes.Length > 0)
            {
                // ByteCounts を uint[] に変換
                uint[] byteCounts = ConvertToUIntArray(ijMetaBC[0]);
                
                if (byteCounts != null && byteCounts.Length > 0)
                {
                    string info = AnalyzeIJMetadata(metaBytes, byteCounts);
                    
                    if (!string.IsNullOrEmpty(info))
                    {
                        // "key=value" 形式でパース
                        ParseMetadataString(info, data.Metadata);
                    }
                }
            }
        }
    }

    private static void WriteCustomMetadata(BitMiracle.LibTiff.Classic.Tiff tiff, IMatrixData data)
    {
        if (data.Metadata.Count == 0)
            return;

        // Metadata を "key=value\nkey=value\n..." 形式に変換
        var sb = new System.Text.StringBuilder();
        foreach (var kvp in data.Metadata)
        {
            if (kvp.Key == "ImageDescription")
                continue;
            
            sb.AppendLine($"{kvp.Key}={kvp.Value}");
        }

        string info = sb.ToString();
        if (string.IsNullOrEmpty(info))
            return;

        // IJMetadata 形式に変換
        ConvertToIJMetadata(info, out uint[] byteCounts, out byte[] metaData);

        // TIFF タグとして書き込み
        tiff.SetField((TiffTag)TiffTagIJMetadataByteCounts, byteCounts.Length, byteCounts);
        tiff.SetField((TiffTag)TiffTagIJMetadata, metaData.Length, metaData);
    }

    /// <summary>
    /// 文字列をIJMetadataのinfo形式に変換
    /// </summary>
    private static void ConvertToIJMetadata(string info, out uint[] byteCounts, out byte[] data)
    {
        bool isBE = !BitConverter.IsLittleEndian; // C# は基本的にリトルエンディアン

        var byteCountList = new List<uint>();
        var dataList = new List<byte>();

        // Header部分: "IJIJ" (Little-Endian) or "JIJI" (Big-Endian)
        byte[] bom = BitConverter.GetBytes(0x494a494a); // IJIJ
        dataList.AddRange(bom);

        // Type: "info" = 0x696e666f
        byte[] typeInfo = BitConverter.GetBytes(0x696e666f);
        dataList.AddRange(typeInfo);

        // Count: 1
        dataList.AddRange(BitConverter.GetBytes(isBE ? ReverseBytes((uint)1) : (uint)1));
        
        uint headerCount = (uint)dataList.Count;
        byteCountList.Add(headerCount);

        // Body部分: Unicode string
        byte[] body = isBE 
            ? System.Text.Encoding.BigEndianUnicode.GetBytes(info) 
            : System.Text.Encoding.Unicode.GetBytes(info);
        
        dataList.AddRange(body);
        byteCountList.Add((uint)body.Length);

        data = dataList.ToArray();
        byteCounts = byteCountList.ToArray();
    }

    /// <summary>
    /// IJMetadata生データから、infoデータのみを抽出
    /// </summary>
    private static string AnalyzeIJMetadata(byte[] meta, uint[] byteCounts)
    {
        try
        {
            if (byteCounts.Length < 2)
                return string.Empty;

            uint headerBytes = byteCounts[0];
            
            if (meta.Length < headerBytes)
                return string.Empty;

            byte[] header = new byte[headerBytes];
            byte[] body = new byte[meta.Length - headerBytes];
            
            Array.Copy(meta, 0, header, 0, headerBytes);
            Array.Copy(meta, headerBytes, body, 0, body.Length);

            // BOM チェック: "IJIJ" (LE) or "JIJI" (BE)
            var bom = System.Text.Encoding.ASCII.GetString(header, 0, 4);
            bool isBE = bom == "IJIJ"; // IJIJがビッグエンディアン（反転しているため逆に格納）

            int tagNum = byteCounts.Length - 1;
            int headerPos = 4;
            int dataPos = 0;

            for (int i = 0; i < tagNum; i++)
            {
                if (headerPos + 8 > header.Length)
                    break;

                var type = System.Text.Encoding.ASCII.GetString(header, headerPos, 4);
                headerPos += 4;

                var count = BitConverter.ToUInt32(header, headerPos);
                if (isBE)
                    count = ReverseBytes(count);
                headerPos += 4;

                if (type == "info")
                {
                    int bodyLength = (int)byteCounts[i + 1];
                    
                    if (dataPos + bodyLength <= body.Length)
                    {
                        var result = isBE
                            ? System.Text.Encoding.BigEndianUnicode.GetString(body, dataPos, bodyLength)
                            : System.Text.Encoding.Unicode.GetString(body, dataPos, bodyLength);
                        
                        return result;
                    }
                }

                dataPos += (int)byteCounts[i + 1];
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// "key=value\nkey=value\n..." 形式の文字列をパース
    /// </summary>
    private static void ParseMetadataString(string text, IDictionary<string, string> metadata)
    {
        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var line in lines)
        {
            var parts = line.Split(new[] { '=' }, 2);
            if (parts.Length == 2)
            {
                string key = parts[0].Trim();
                string value = parts[1].Trim();
                
                if (!string.IsNullOrEmpty(key))
                {
                    metadata[key] = value;
                }
            }
        }
    }

    /// <summary>
    /// FieldValue を uint[] に変換
    /// </summary>
    private static uint[] ConvertToUIntArray(FieldValue fieldValue)
    {
        // FieldValue から配列として取得
        var valueArray = fieldValue.ToUIntArray();
        
        if (valueArray != null && valueArray.Length > 0)
            return valueArray;
        
        // フォールバック: 単一値を配列化
        return new uint[] { fieldValue.ToUInt() };
    }

    /// <summary>
    /// uint のバイトオーダーを反転
    /// </summary>
    private static uint ReverseBytes(uint value)
    {
        return ((value & 0x000000FF) << 24) |
               ((value & 0x0000FF00) << 8) |
               ((value & 0x00FF0000) >> 8) |
               ((value & 0xFF000000) >> 24);
    }

    #endregion

    #region Type Utilities

    private static void ValidateDataType<T>()
    {
        if (typeof(T) != typeof(byte) && typeof(T) != typeof(ushort))
        {
            throw new NotSupportedException(
                $"Data type {typeof(T).Name} is not supported. Only byte and ushort are supported.");
        }
    }

    private static int GetBytesPerPixel<T>() where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
            return 1;
        if (typeof(T) == typeof(ushort))
            return 2;

        throw new NotSupportedException($"Unsupported type: {typeof(T)}");
    }

    private static T GetValue<T>(byte[] buffer, int offset) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
            return (T)(object)buffer[offset];
        if (typeof(T) == typeof(ushort))
            return (T)(object)BitConverter.ToUInt16(buffer, offset);

        throw new NotSupportedException($"Unsupported type: {typeof(T)}");
    }

    private static void SetValue<T>(byte[] buffer, int offset, T value) where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            buffer[offset] = (byte)(object)value;
        }
        else if (typeof(T) == typeof(ushort))
        {
            byte[] bytes = BitConverter.GetBytes((ushort)(object)value);
            buffer[offset] = bytes[0];
            buffer[offset + 1] = bytes[1];
        }
        else
        {
            throw new NotSupportedException($"Unsupported type: {typeof(T)}");
        }
    }

    #endregion
}
