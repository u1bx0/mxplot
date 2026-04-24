using BitMiracle.LibTiff.Classic;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
    public static MatrixData<T> Load<T>(string filename, IProgress<int>? progress = null, CancellationToken ct = default, int maxParallelDegree = -1)
        where T : unmanaged
    {
        ValidateDataType<T>();

        if (!File.Exists(filename))
            throw new FileNotFoundException($"File not found: {filename}");

        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r");
        if (tiff == null)
            throw new IOException($"Failed to open TIFF file: {filename}");

        return LoadInternal<T>(tiff, filename, maxParallelDegree, progress, ct);
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

        // Create the output directory if it does not exist
        string? directory = Path.GetDirectoryName(filename);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Set the LibTiff.NET error handler
        BitMiracle.LibTiff.Classic.Tiff.SetErrorHandler(new LibTiffErrorHandler());
        
        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "w");
        if (tiff == null)
        {
            throw new IOException($"Failed to create TIFF file: {filename}. Check LibTiff error log for details.");
        }

        SaveInternal(tiff, data, progress);
    }

    /// <summary>
    /// Custom error handler for LibTiff.NET.
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

    private static MatrixData<T> LoadInternal<T>(BitMiracle.LibTiff.Classic.Tiff tiff, string filename, int maxParallelDegree, IProgress<int>? progress, CancellationToken ct = default)
        where T : unmanaged
    {
        // 1. Validate pixel type (read from frame 0, the initial directory)
        short bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToShort();
        int expectedBytes = (typeof(T) == typeof(byte)) ? 8 : 16;
        if (bitsPerSample != expectedBytes)
            throw new InvalidDataException($"File is {bitsPerSample}bit, but {expectedBytes}bit was requested.");

        // 2. Basic dimensions
        int width          = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height         = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        int directoryCount = tiff.NumberOfDirectories();

        // 3. ImageJ metadata (from frame 0)
        var ijMetadata = ReadImageJMetadata(tiff);

        // 4. Resolution (from frame 0)
        var (xResolution, yResolution) = ReadResolution(tiff);

        // 5. Create MatrixData
        var data = new MatrixData<T>(width, height, directoryCount);

        // 6. Scale
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

        // 7. Custom metadata (read from frame 0, before the frame loop)
        ReadCustomMetadata(tiff, data);

        // 7b. Store the raw IMAGEDESCRIPTION as a read-only format-header entry so it
        //     is visible in the metadata panel but excluded from re-export.
        StoreImageDescription(tiff, data);

        // 8. Compression detection: parallel is effective only for CPU-bound codecs
        int compression = tiff.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? (int)Compression.NONE;
        bool isCpuBound = compression == (int)Compression.LZW
                       || compression == (int)Compression.DEFLATE
                       || compression == (int)Compression.ADOBE_DEFLATE;
        bool useParallel = isCpuBound && (maxParallelDegree < 0 || maxParallelDegree > 1) && directoryCount > 1;

        // 9. Load all frames
        if (useParallel)
        {
            Debug.WriteLine($"Using parallel loading with {maxParallelDegree} threads for {directoryCount} frames (compression={compression}).");
            // Parallel: ReadFrameDataStripped concurrently, then SetArray sequentially
            LoadFramesParallel<T>(filename, data, width, height, directoryCount, maxParallelDegree, progress, ct);
        }
        else
        {
            Debug.WriteLine($"Using sequential loading for {directoryCount} frames (compression={compression}).");
            // Sequential: O(N) IFD traversal via SetDirectory(0) + ReadDirectory() increments
            progress?.Report(-directoryCount);
            tiff.SetDirectory(0);
            for (int i = 0; i < directoryCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                T[] frameData = ReadFrameDataStripped<T>(tiff, width, height);
                data.SetArray(frameData, i);
                progress?.Report(i);

                if (i < directoryCount - 1 && !tiff.ReadDirectory())
                    throw new InvalidDataException($"Could not advance to directory {i + 1}");
            }
        }

        // 10. Dimension layout
        if (ijMetadata != null && ijMetadata.Hyperstack)
            SetDimensionsFromImageJ(data, ijMetadata);

        progress?.Report(directoryCount);
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

    /// <summary>
    /// Stores the raw TIFF IMAGEDESCRIPTION tag value in <paramref name="data"/>.Metadata
    /// under the key <c>"ImageDescription"</c> and marks it as a format-header entry
    /// (read-only; excluded from re-export).
    /// Does nothing when the tag is absent or empty.
    /// </summary>
    private static void StoreImageDescription(BitMiracle.LibTiff.Classic.Tiff tiff, IMatrixData data)
    {
        var imageDesc = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
        if (imageDesc == null || imageDesc.Length == 0) return;
        string description = imageDesc[0].ToString()?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(description)) return;
        data.Metadata["ImageDescription"] = description;
        data.MarkAsFormatHeader("ImageDescription");
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

        return (1.0, 1.0); // default
    }

    private static T[] ReadFrameData<T>(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
        where T : unmanaged
    {
        int bytesPerPixel   = GetBytesPerPixel<T>();
        int scanlineSize    = tiff.ScanlineSize();       // actual bytes per row as stored in the file
        int filePixelStride = scanlineSize / width;      // bytes per pixel in the file (≥ bytesPerPixel for multi-sample)
        var buffer          = new byte[scanlineSize * height];

        for (int row = 0; row < height; row++)
        {
            if (!tiff.ReadScanline(buffer, row * scanlineSize, row, 0))
                throw new IOException($"Failed to read scanline at row {row}");
        }

        // Y-flip: TIFF (top-left origin) → MatrixData (bottom-left origin)
        var flipped = new T[width * height];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int srcOffset = row * scanlineSize + col * filePixelStride;
                int dstIndex  = (height - 1 - row) * width + col;
                flipped[dstIndex] = GetValue<T>(buffer, srcOffset);
            }
        }

        return flipped;
    }

    /// <summary>
    /// Reads one frame using <c>ReadEncodedStrip</c> — one API call per strip instead of one per row.
    /// Applies the TIFF (top-left origin) → MatrixData (bottom-left origin) Y-flip.
    /// Handles multi-sample files via <c>filePixelStride</c>.
    /// </summary>
    private static T[] ReadFrameDataStripped<T>(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
        where T : unmanaged
    {
        int bytesPerPixel   = GetBytesPerPixel<T>();
        int scanlineSize    = tiff.ScanlineSize();
        int filePixelStride = scanlineSize / width;

        int rowsPerStrip  = tiff.GetField(TiffTag.ROWSPERSTRIP)?[0].ToInt() ?? height;
        int numStrips     = tiff.NumberOfStrips();
        int maxStripBytes = tiff.StripSize();
        var stripBuf      = new byte[maxStripBytes];

        var result = new T[width * height];

        for (int strip = 0; strip < numStrips; strip++)
        {
            int bytesRead = tiff.ReadEncodedStrip(strip, stripBuf, 0, maxStripBytes);
            if (bytesRead < 0)
                throw new IOException($"ReadEncodedStrip failed: strip={strip}");

            int startRow   = strip * rowsPerStrip;
            int actualRows = Math.Min(rowsPerStrip, height - startRow);

            for (int r = 0; r < actualRows; r++)
            {
                int dstRow  = height - 1 - (startRow + r);  // Y-flip
                int srcBase = r * scanlineSize;
                int dstBase = dstRow * width;

                for (int col = 0; col < width; col++)
                    result[dstBase + col] = GetValue<T>(stripBuf, srcBase + col * filePixelStride);
            }
        }

        return result;
    }

    /// <summary>
    /// Parallel frame loader. Opens one <c>Tiff</c> handle per thread and advances each
    /// via sequential <c>ReadDirectory</c> hops — O(start) per thread, O(N) total.
    /// Only called for LZW/Deflate-compressed files where decompression is CPU-bound.
    /// Frames are decoded concurrently into a staging array, then written via <c>SetArray</c> sequentially.
    /// </summary>
    private static void LoadFramesParallel<T>(
        string filePath, MatrixData<T> data, int width, int height, int frameCount,
        int maxParallelDegree, IProgress<int>? progress, CancellationToken ct)
        where T : unmanaged
    {
        int degree  = maxParallelDegree <= 0 ? Environment.ProcessorCount : maxParallelDegree;
        int threads = Math.Min(degree, frameCount);
        int chunk   = (frameCount + threads - 1) / threads;

        var frames   = new T[frameCount][];
        int reported = 0;

        progress?.Report(-frameCount);

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, t =>
        {
            int start = t * chunk;
            int end   = Math.Min(start + chunk, frameCount);
            if (start >= frameCount) return;

            using var localTiff = BitMiracle.LibTiff.Classic.Tiff.Open(filePath, "r");
            if (localTiff == null)
                throw new IOException($"Thread {t}: failed to open TIFF file for parallel read.");

            // Advance to this thread's starting IFD via O(start) sequential hops
            localTiff.SetDirectory(0);
            for (int i = 0; i < start; i++) localTiff.ReadDirectory();

            for (int f = start; f < end; f++)
            {
                ct.ThrowIfCancellationRequested();
                frames[f] = ReadFrameDataStripped<T>(localTiff, width, height);
                progress?.Report(Interlocked.Increment(ref reported) - 1);

                if (f < end - 1 && !localTiff.ReadDirectory())
                    throw new InvalidDataException($"Thread {t}: could not advance to frame {f + 1}");
            }
        });

        // Write decoded frames sequentially to avoid shared-state races in SetArray
        for (int f = 0; f < frameCount; f++)
            data.SetArray(frames[f], f);
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
        progress?.Report(-frameCount); // signal start

        // Generate ImageJ metadata
        var ijMetadata = ImageJMetadata.FromMatrixData(data);

        // Compute frame order (ImageJ uses C-Z-T order)
        int[] frameOrder = CalculateFrameOrder(data, ijMetadata);

        // Write frames in order
        for (int i = 0; i < frameCount; i++)
        {
            int frameIndex = frameOrder[i];

            // Set basic TIFF tags
            SetBasicTiffTags(tiff, data.XCount, data.YCount, typeof(T));

            // Write additional metadata to the first frame only
            if (i == 0)
            {
                WriteImageJMetadata(tiff, ijMetadata);
                WriteResolution(tiff, data.XStep, data.YStep);
                WriteCustomMetadata(tiff, data);
            }

            // Write pixel data
            T[] frameData = data.GetArray(frameIndex);
            WriteFrameData(tiff, frameData, data.XCount, data.YCount);

            if (i < frameCount - 1)
                tiff.WriteDirectory();

            progress?.Report(i);
        }

        progress?.Report(frameCount); // signal completion
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

        // Compression (LZW without horizontal-differencing predictor).
        // Predictor.HORIZONTAL (tag 317 = 2) triggers FluoRender's DecodeAcc16,
        // which has a bug that causes a SIGSEGV on ARM64 macOS regardless of strip
        // layout.  Predictor.NONE avoids the call entirely with minimal size impact.
        tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
        tiff.SetField(TiffTag.PREDICTOR, Predictor.NONE);
        //tiff.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL);

        // Set RowsPerStrip
        int rowsPerStrip = CalculateOptimalRowsPerStrip(width, height, dataType);
        tiff.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip);
    }

    private static int CalculateOptimalRowsPerStrip(int width, int height, Type dataType)
    {
        // Use a single strip per frame (rowsPerStrip = height).
        //
        // Some readers (e.g. FluoRender) have a bug in their horizontal-predictor
        // undo pass where they iterate rowsPerStrip rows unconditionally, even on the
        // last strip which may contain fewer rows.  With a single strip there is no
        // partial last strip, so the bug is never triggered.
        //
        // The 64 KB target heuristic is kept as a comment for reference:
        //   int bytesPerPixel = dataType == typeof(byte) ? 1 : 2;
        //   int bytesPerRow = width * bytesPerPixel;
        //   const int targetStripSize = 64 * 1024;
        //   int optimalRows = Math.Max(1, targetStripSize / bytesPerRow);
        //   return Math.Min(optimalRows, height);
        return height;
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

        // Y-flip: MatrixData (bottom-left origin) → TIFF (top-left origin)
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int srcIndex = (height - 1 - row) * width + col;
                int dstIndex = row * width + col;
                SetValue(buffer, dstIndex * bytesPerPixel, data[srcIndex]);
            }
        }

        // Write row by row
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

        // ImageJ order: XYCZT (C varies fastest)
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
    /// ImageJ metadata tag (50839).
    /// </summary>
    private const int TiffTagIJMetadata = 50839;

    /// <summary>
    /// Tag that records the byte counts for IJMetadata (50838).
    /// </summary>
    private const int TiffTagIJMetadataByteCounts = 50838;

    private static void ReadCustomMetadata(BitMiracle.LibTiff.Classic.Tiff tiff, IMatrixData data)
    {
        // Read IJMetadata tags (50839, 50838)
        var ijMeta = tiff.GetField((TiffTag)TiffTagIJMetadata);
        var ijMetaBC = tiff.GetField((TiffTag)TiffTagIJMetadataByteCounts);

        if (ijMeta != null && ijMetaBC != null && ijMeta.Length > 0 && ijMetaBC.Length > 0)
        {
            byte[]? metaBytes = ijMeta[0].ToByteArray();

            if (metaBytes != null && metaBytes.Length > 0)
            {
                // Convert ByteCounts to uint[]
                uint[] byteCounts = ConvertToUIntArray(ijMetaBC[0]);

                if (byteCounts != null && byteCounts.Length > 0)
                {
                    string info = AnalyzeIJMetadata(metaBytes, byteCounts);

                    if (!string.IsNullOrEmpty(info))
                    {
                        // Parse "key=value" lines
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

        // Serialize metadata to "key=value\nkey=value\n..." format
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

        // Convert to IJMetadata format
        ConvertToIJMetadata(info, out uint[] byteCounts, out byte[] metaData);

        // Write as TIFF tags
        tiff.SetField((TiffTag)TiffTagIJMetadataByteCounts, byteCounts.Length, byteCounts);
        tiff.SetField((TiffTag)TiffTagIJMetadata, metaData.Length, metaData);
    }

    /// <summary>
    /// Converts a string to the IJMetadata info-section binary format.
    /// </summary>
    private static void ConvertToIJMetadata(string info, out uint[] byteCounts, out byte[] data)
    {
        bool isBE = !BitConverter.IsLittleEndian;

        var byteCountList = new List<uint>();
        var dataList = new List<byte>();

        // Header: "IJIJ" (little-endian) or "JIJI" (big-endian)
        byte[] bom = BitConverter.GetBytes(0x494a494a); // IJIJ
        dataList.AddRange(bom);

        // Type: "info" = 0x696e666f
        byte[] typeInfo = BitConverter.GetBytes(0x696e666f);
        dataList.AddRange(typeInfo);

        // Count: 1
        dataList.AddRange(BitConverter.GetBytes(isBE ? ReverseBytes((uint)1) : (uint)1));

        uint headerCount = (uint)dataList.Count;
        byteCountList.Add(headerCount);

        // Body: Unicode string
        byte[] body = isBE 
            ? System.Text.Encoding.BigEndianUnicode.GetBytes(info) 
            : System.Text.Encoding.Unicode.GetBytes(info);

        dataList.AddRange(body);
        byteCountList.Add((uint)body.Length);

        data = dataList.ToArray();
        byteCounts = byteCountList.ToArray();
    }

    /// <summary>
    /// Extracts only the "info" section from raw IJMetadata bytes.
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

            // BOM check: "IJIJ" = big-endian (bytes are stored reversed), "JIJI" = little-endian
            var bom = System.Text.Encoding.ASCII.GetString(header, 0, 4);
            bool isBE = bom == "IJIJ";

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
    /// Parses a "key=value\nkey=value\n..." formatted string into a metadata dictionary.
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
    /// Converts a <see cref="FieldValue"/> to a <c>uint[]</c>.
    /// </summary>
    private static uint[] ConvertToUIntArray(FieldValue fieldValue)
    {
        // Try to get the value as an array directly
        var valueArray = fieldValue.ToUIntArray();

        if (valueArray != null && valueArray.Length > 0)
            return valueArray;

        // Fallback: wrap the single value in an array
        return new uint[] { fieldValue.ToUInt() };
    }

    /// <summary>
    /// Reverses the byte order of a uint value.
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
