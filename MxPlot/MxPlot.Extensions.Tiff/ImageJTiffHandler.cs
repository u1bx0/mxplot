using BitMiracle.LibTiff.Classic;
using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.Core.IO.Formats;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Reads and writes plain TIFF with ImageJ hyperstack metadata (ImageDescription and the IJMetadata
/// tags). Pixel types are the ones <see cref="TiffPixelType"/> lists, the same as OME-TIFF.
/// </summary>
public static class ImageJTiffHandler
{
    // LibTiff silently drops tags it does not know (SetField returns false, GetField returns null),
    // so the two IJMetadata tags have to be registered before any file is opened. The extender is
    // process-wide; the previously installed one is chained so other users of LibTiff keep working.
    private static readonly BitMiracle.LibTiff.Classic.Tiff.TiffExtendProc? _previousTagExtender;

    static ImageJTiffHandler()
    {
        _previousTagExtender = BitMiracle.LibTiff.Classic.Tiff.SetTagExtender(RegisterImageJTags);
    }

    private static void RegisterImageJTags(BitMiracle.LibTiff.Classic.Tiff tiff)
    {
        // Both tags carry a variable-length array whose element count is passed with the value,
        // so GetField returns { count, data }. Variable2 passes the count as a 32-bit integer; with
        // Variable it would be 16-bit, which a metadata block of 32 KB or more overflows.
        tiff.MergeFieldInfo(new[]
        {
            new TiffFieldInfo((TiffTag)TiffTagIJMetadataByteCounts, TiffFieldInfo.Variable2, TiffFieldInfo.Variable2, TiffType.LONG, FieldBit.Custom, true, true, "IJMetadataByteCounts"),
            new TiffFieldInfo((TiffTag)TiffTagIJMetadata, TiffFieldInfo.Variable2, TiffFieldInfo.Variable2, TiffType.BYTE, FieldBit.Custom, true, true, "IJMetadata"),
        }, 2);
        _previousTagExtender?.Invoke(tiff);
    }

    /// <summary>
    /// ImageJ互換のTIFFファイルから読み込み
    /// </summary>
    /// <typeparam name="T">Pixel type (see <see cref="TiffPixelType"/>).</typeparam>
    /// <param name="filename">ファイルパス</param>
    /// <param name="progress">進捗報告（オプション）</param>
    /// <returns>読み込まれた MatrixData</returns>
    /// <exception cref="NotSupportedException">サポートされていないデータ型</exception>
    /// <exception cref="FileNotFoundException">ファイルが見つからない</exception>
    /// <exception cref="IOException">TIFF読み込みエラー</exception>
    public static MatrixData<T> Load<T>(string filename, IProgress<int>? progress = null, CancellationToken ct = default, int maxParallelDegree = -1, LoadingMode mode = LoadingMode.InMemory)
        where T : unmanaged
    {
        TiffPixelType.EnsureSupported<T>();

        if (!File.Exists(filename))
            throw new FileNotFoundException($"File not found: {filename}");

        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r");
        if (tiff == null)
            throw new IOException($"Failed to open TIFF file: {filename}");

        return LoadInternal<T>(tiff, filename, maxParallelDegree, progress, mode, ct);
    }

    /// <summary>
    /// Writes <paramref name="data"/> as an ImageJ-compatible TIFF with one IFD per frame.
    /// </summary>
    /// <remarks>
    /// Channel, Z and Time/Timelapse axes are written as the channels, slices and frames of an
    /// ImageJ hyperstack. Any other axis is written as slices (or as frames when slices is taken)
    /// and its name is recorded so it is restored on load. Data of 4 GiB or more is written as BigTIFF
    /// (see <see cref="RequiresBigTiff"/>). When the axes do not fit these three
    /// dimensions, the frames are written in their own order and the ImageDescription declares only
    /// the image count, so the file reads back as a plain stack with its XY calibration.
    /// </remarks>
    /// <typeparam name="T">Pixel type (see <see cref="TiffPixelType"/>).</typeparam>
    /// <param name="filename">Destination file path.</param>
    /// <param name="data">The data to save.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="compress">True (default) writes LZW-compressed frames; false writes them uncompressed, which a Virtual load can then memory-map.</param>
    /// <exception cref="NotSupportedException">Unsupported pixel type.</exception>
    /// <exception cref="IOException">TIFF write error.</exception>
    public static void Save<T>(string filename, MatrixData<T> data, IProgress<int>? progress = null, bool compress = true)
        where T : unmanaged
        => Save(filename, data, progress, compress, forceBigTiff: false);

    /// <summary>
    /// File offsets of a classic TIFF are 32 bit, so a file cannot exceed 4 GiB. Past that size the
    /// file is written as BigTIFF (64-bit offsets), which ImageJ/Fiji itself does not open. The
    /// decision uses the uncompressed size (compression may keep the real size smaller, but it is
    /// not known up front), less a margin for the IFDs and metadata.
    /// </summary>
    internal static bool RequiresBigTiff(long rawPixelBytes)
        => rawPixelBytes > 0xFFFFFFFFL - ClassicTiffMarginBytes;

    private const long ClassicTiffMarginBytes = 16L * 1024 * 1024;

    internal static void Save<T>(string filename, MatrixData<T> data, IProgress<int>? progress, bool compress, bool forceBigTiff)
        where T : unmanaged
    {
        TiffPixelType.EnsureSupported<T>();

        // Create the output directory if it does not exist
        string? directory = Path.GetDirectoryName(filename);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Set the LibTiff.NET error handler
        BitMiracle.LibTiff.Classic.Tiff.SetErrorHandler(new LibTiffErrorHandler());
        
        long rawPixelBytes = (long)data.XCount * data.YCount * data.FrameCount * TiffPixelType.BytesPerSample(typeof(T));
        // "w" = classic TIFF, "w8" = BigTIFF
        string mode = forceBigTiff || RequiresBigTiff(rawPixelBytes) ? "w8" : "w";

        using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, mode);
        if (tiff == null)
        {
            throw new IOException($"Failed to create TIFF file: {filename}. Check LibTiff error log for details.");
        }

        SaveInternal(tiff, data, progress, compress);
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

    /// <summary>
    /// Reports the Virtual backend and frame count of <paramref name="filename"/> without reading
    /// pixel data. Uses the same decisions as <see cref="Load{T}"/>. Returns
    /// <see cref="VirtualLoadProbe.Unknown"/> for a file that cannot be opened as TIFF.
    /// </summary>
    internal static VirtualLoadProbe ProbeVirtualLoad(string filename)
    {
        try
        {
            using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r");
            if (tiff == null)
                return VirtualLoadProbe.Unknown;

            var widthTag = tiff.GetField(TiffTag.IMAGEWIDTH);
            if (widthTag == null)
                return VirtualLoadProbe.Unknown;

            int width = widthTag[0].ToInt();
            int directoryCount = TiffIfdIndex.ReadOffsets(filename).Length;
            var (isPacked, frameCount) = ResolveFrameLayout(ReadImageJMetadata(tiff), directoryCount);
            return new VirtualLoadProbe(ResolveVirtualBackend(tiff, width, isPacked), frameCount);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return VirtualLoadProbe.Unknown;
        }
    }

    /// <summary>
    /// ImageJ "packed" hyperstack quirk: ImageJ itself writes large uncompressed stacks as a single
    /// IFD (with a single strip) whose ImageDescription declares channels*slices*frames planes, and
    /// simply appends the remaining planes as raw, contiguous, fixed-stride pixel data right after
    /// that IFD's strip -- with no per-plane IFD/strip metadata at all. Detected whenever the
    /// declared plane count exceeds the actual IFD count.
    /// </summary>
    private static (bool isPacked, int frameCount) ResolveFrameLayout(ImageJMetadata? ijMetadata, int directoryCount)
    {
        int declaredFrameCount = ijMetadata != null ? ijMetadata.Channels * ijMetadata.Slices * ijMetadata.Frames : 0;
        bool isPacked = directoryCount == 1 && declaredFrameCount > 1;
        return (isPacked, isPacked ? declaredFrameCount : directoryCount);
    }

    /// <summary>
    /// Which backend a Virtual load uses, judged from the current (first) directory. "Packed" and
    /// "compressed" cannot co-occur, since the packed layout requires fixed-stride raw pixel data:
    /// <list type="bullet">
    ///   <item>Packed layout: <see cref="VirtualBackendKind.Mapped"/> (StrippedMmfFrames via
    ///   <see cref="LoadPackedFramesVirtual{T}"/>, offsets computed from the fixed stride), or
    ///   <see cref="VirtualBackendKind.None"/> when compressed.</item>
    ///   <item>Multi-IFD layout whose strips can be mapped verbatim (uncompressed, strip-organized,
    ///   one sample per pixel): <see cref="VirtualBackendKind.Mapped"/> via
    ///   <see cref="LoadStrippedFramesVirtual{T}"/>, from a per-IFD strip-offset table -- the same
    ///   route OME-TIFF takes.</item>
    ///   <item>Any other multi-IFD layout (compressed, tiled, multi-sample):
    ///   <see cref="VirtualBackendKind.Decoded"/> via <see cref="LoadDecodedFramesVirtual{T}"/>,
    ///   which decodes each frame through LibTiff and so has no layout restriction.</item>
    /// </list>
    /// </summary>
    private static VirtualBackendKind ResolveVirtualBackend(BitMiracle.LibTiff.Classic.Tiff tiff, int width, bool isPacked)
    {
        if (isPacked)
        {
            int compression = tiff.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? (int)Compression.NONE;
            return compression == (int)Compression.NONE ? VirtualBackendKind.Mapped : VirtualBackendKind.None;
        }
        return TiffStorageLayout.IsStripMappable(tiff, width) ? VirtualBackendKind.Mapped : VirtualBackendKind.Decoded;
    }

    private static MatrixData<T> LoadInternal<T>(BitMiracle.LibTiff.Classic.Tiff tiff, string filename, int maxParallelDegree, IProgress<int>? progress, LoadingMode mode, CancellationToken ct = default)
        where T : unmanaged
    {
        // 1. Validate pixel type (read from frame 0, the initial directory)
        TiffPixelType.EnsureFileMatches<T>(tiff);

        // 2. Basic dimensions
        int width          = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
        int height         = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
        // Not tiff.NumberOfDirectories(): it returns Int16 and wraps past 32,767 (see TiffIfdIndex).
        long[] ifdOffsets  = TiffIfdIndex.ReadOffsets(filename, ct);
        int directoryCount = ifdOffsets.Length;

        // 3. ImageJ metadata (from frame 0), and the frame layout it implies
        var ijMetadata = ReadImageJMetadata(tiff);
        var (isPacked, frameCount) = ResolveFrameLayout(ijMetadata, directoryCount);

        // 4. Resolution (from frame 0)
        var (xResolution, yResolution) = ReadResolution(tiff);

        // 5. Create MatrixData (see ResolveVirtualBackend for the Virtual routes)
        int compression = tiff.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? (int)Compression.NONE;
        var backend = ResolveVirtualBackend(tiff, width, isPacked);
        LoadingMode resolvedMode = VirtualPolicy.Resolve(mode, new FileInfo(filename).Length, frameCount,
            canVirtual: backend != VirtualBackendKind.None);

        MatrixData<T> data;
        if (resolvedMode != LoadingMode.Virtual)
            data = new MatrixData<T>(width, height, frameCount);
        else if (isPacked)
            data = LoadPackedFramesVirtual<T>(tiff, filename, width, height, frameCount);
        else if (backend == VirtualBackendKind.Mapped)
            data = LoadStrippedFramesVirtual<T>(tiff, filename, width, height, ifdOffsets, frameCount, progress, ct);
        else
            data = LoadDecodedFramesVirtual<T>(filename, width, height, ifdOffsets, frameCount);

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

        // 8/9. Load all frames (Virtual data was already fully constructed in step 5 above --
        // nothing left to read into it here, by design; that's the point of Virtual loading).
        if (resolvedMode == LoadingMode.Virtual)
        {
            Debug.WriteLine(isPacked
                ? $"Using ImageJ packed single-IFD Virtual (MMF) loading for {frameCount} frames."
                : data.GetDiagnosticCacheableList() is IMmfFrameList
                    ? $"Using multi-IFD Virtual (MMF) loading for {frameCount} frames."
                    : $"Using multi-IFD Virtual (Lazy decode) loading for {frameCount} frames.");
        }
        else if (isPacked)
        {
            Debug.WriteLine($"Using ImageJ packed single-IFD loading for {frameCount} frames.");
            LoadPackedFrames<T>(tiff, filename, data, width, height, frameCount, progress, ct);
        }
        else
        {
            int threads = TiffFrameCodec.ResolveLoadThreads(maxParallelDegree, compression != (int)Compression.NONE, directoryCount);

            if (threads > 1)
            {
                Debug.WriteLine($"Using parallel loading with {threads} threads for {directoryCount} frames (compression={compression}).");
                // Parallel: ReadFrameDataStripped concurrently, then SetArray sequentially
                LoadFramesParallel<T>(filename, data, width, height, ifdOffsets, threads, progress, ct);
            }
            else
            {
                Debug.WriteLine($"Using sequential loading for {directoryCount} frames (compression={compression}).");
                // Sequential: each IFD visited once, addressed by offset (ReadDirectory() hops fail past 32,767)
                progress?.Report(-directoryCount);
                for (int i = 0; i < directoryCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    TiffIfdIndex.SetFrame(tiff, ifdOffsets, i);
                    T[] frameData = ReadFrameDataStripped<T>(tiff, width, height);
                    data.SetArray(frameData, i);
                    data.GetValueRange(i); // recalc min/max for this frame (FastMinMaxFinder, cache-hot right after the copy)
                    progress?.Report(i);
                }
            }
        }

        // 10. Dimension layout
        if (ijMetadata != null && ijMetadata.Hyperstack)
            SetDimensionsFromImageJ(data, ijMetadata);

        progress?.Report(frameCount);
        return data;
    }

    /// <summary>
    /// Reads frames for the ImageJ "packed" single-IFD layout: the IFD's own strip holds
    /// only the first plane, and the remaining planes are raw, uncompressed, fixed-stride
    /// pixel data appended directly after it in the file. LibTiff has no notion of this
    /// (an IFD/strip only ever describes one plane), so this bypasses LibTiff's strip API
    /// entirely and reads the pixel bytes directly via a plain <see cref="FileStream"/>.
    /// </summary>
    private static void LoadPackedFrames<T>(
        BitMiracle.LibTiff.Classic.Tiff tiff, string filename, MatrixData<T> data,
        int width, int height, int frameCount, IProgress<int>? progress, CancellationToken ct)
        where T : unmanaged
    {
        var (baseOffset, frameSizeBytes) = TiffFrameLayout.ValidatePacked(tiff, filename, height, frameCount);

        int scanlineSize    = tiff.ScanlineSize();
        int filePixelStride = scanlineSize / width;
        bool bigEndian       = tiff.IsBigEndian();

        progress?.Report(-frameCount);

        using var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        stream.Seek(baseOffset, SeekOrigin.Begin);

        var buffer = new byte[frameSizeBytes];
        for (int i = 0; i < frameCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            ReadExact(stream, buffer, (int)frameSizeBytes);

            // Write straight into MatrixData's own pre-allocated frame buffer (GetArray also
            // invalidates that frame's cached min/max, same as SetArray would) -- there is no
            // separate frame array to copy in afterwards.
            T[] dst = data.GetArray(i);
            CopyPackedFrame(buffer, dst, width, height, scanlineSize, filePixelStride, bigEndian);

            data.GetValueRange(i); // recalc min/max for this frame

            progress?.Report(i);
        }
    }

    /// <summary>
    /// Virtual (MMF-backed) counterpart of <see cref="LoadPackedFrames{T}"/>: since every packed
    /// plane sits at a fixed stride from <c>baseOffset</c>, the whole offset table can be computed
    /// up front with no IFD walk or pixel read at all.
    /// </summary>
    private static MatrixData<T> LoadPackedFramesVirtual<T>(
        BitMiracle.LibTiff.Classic.Tiff tiff, string filename, int width, int height, int frameCount)
        where T : unmanaged
    {
        var (baseOffset, frameSizeBytes) = TiffFrameLayout.ValidatePacked(tiff, filename, height, frameCount);
        var (offsets, byteCounts) = TiffFrameLayout.PackedOffsets(baseOffset, frameSizeBytes, frameCount);

        var vsf = new StrippedMmfFrames<T>(
            filename, width, height, offsets, byteCounts, isYFlipped: true, isBigEndian: tiff.IsBigEndian());
        return MatrixData<T>.CreateAsVirtualFrames(width, height, vsf);
    }

    /// <summary>
    /// Virtual (MMF-backed) route for the general multi-IFD layout when every frame's strips can be
    /// mapped verbatim (see <see cref="TiffStorageLayout.IsStripMappable"/>). Walks every IFD once
    /// to collect its strip offsets; no pixel data is read here.
    /// </summary>
    private static MatrixData<T> LoadStrippedFramesVirtual<T>(
        BitMiracle.LibTiff.Classic.Tiff tiff, string filename, int width, int height, long[] ifdOffsets, int frameCount,
        IProgress<int>? progress, CancellationToken ct)
        where T : unmanaged
    {
        var (offsets, byteCounts) = TiffStorageLayout.ScanStrips(tiff, ifdOffsets, frameCount, progress, ct);
        // Steps 6-7 read frame-0 tags from the current directory
        TiffIfdIndex.SetFrame(tiff, ifdOffsets, 0);

        var vsf = new StrippedMmfFrames<T>(
            filename, width, height, offsets, byteCounts, isYFlipped: true, isBigEndian: tiff.IsBigEndian());
        return MatrixData<T>.CreateAsVirtualFrames(width, height, vsf);
    }

    /// <summary>
    /// Virtual (Lazy decode) route for the general multi-IFD layout: each logical frame is decoded
    /// from its own TIFF directory on access, via <see cref="TiffDecodedFrames{T}"/>. Unlike the
    /// MMF routes, this works for any compression, tiling or samples-per-pixel layout.
    /// </summary>
    private static MatrixData<T> LoadDecodedFramesVirtual<T>(string filename, int width, int height, long[] ifdOffsets, int frameCount)
        where T : unmanaged
    {
        var tdf = new TiffDecodedFrames<T>(filename, width, height, ifdOffsets, frameCount, flipY: true);
        return MatrixData<T>.CreateAsVirtualFrames(width, height, tdf);
    }

    /// <summary>
    /// Copies one packed plane into <paramref name="dst"/>, applying the TIFF (top-left origin) ->
    /// MatrixData (bottom-left origin) Y-flip and converting the samples to host byte order.
    /// Rows are independent, so they are processed in parallel. A file pixel stride larger than the
    /// sample size (several samples per pixel) keeps only each pixel's first sample.
    /// </summary>
    private static void CopyPackedFrame<T>(byte[] raw, T[] dst, int width, int height, int scanlineSize, int filePixelStride, bool bigEndian)
        where T : unmanaged
    {
        int sampleSize = TiffPixelType.BytesPerSample(typeof(T));
        bool swap = sampleSize > 1 && TiffPixelType.NeedsByteSwap(bigEndian);

        Parallel.For(0, height, row =>
        {
            int dstRow = height - 1 - row;
            var dstRowSpan = dst.AsSpan(dstRow * width, width);
            var dstBytes = MemoryMarshal.AsBytes(dstRowSpan);
            int srcBase = row * scanlineSize;

            if (filePixelStride == sampleSize)
            {
                raw.AsSpan(srcBase, width * sampleSize).CopyTo(dstBytes);
            }
            else
            {
                for (int col = 0; col < width; col++)
                    raw.AsSpan(srcBase + col * filePixelStride, sampleSize).CopyTo(dstBytes.Slice(col * sampleSize, sampleSize));
            }

            if (swap)
                TiffPixelType.SwapEndiannessInPlace(dstRowSpan);
        });
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>, looping
    /// as needed since <see cref="Stream.Read(byte[], int, int)"/> may return short reads.
    /// </summary>
    private static void ReadExact(Stream stream, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = stream.Read(buffer, offset, count - offset);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of file while reading a packed ImageJ hyperstack plane.");
            offset += read;
        }
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

    /// <summary>
    /// Reads one frame using <c>ReadEncodedStrip</c> — one API call per strip instead of one per row.
    /// Applies the TIFF (top-left origin) → MatrixData (bottom-left origin) Y-flip inline.
    /// Delegates to <see cref="TiffFrameCodec.ReadStripped{T}"/> (shared with
    /// <c>OmeTiffHandlerInstance&lt;T&gt;</c>), which handles multi-sample files via its own
    /// stride check/fallback.
    /// </summary>
    private static T[] ReadFrameDataStripped<T>(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
        where T : unmanaged
        => TiffFrameCodec.ReadStripped<T>(tiff, width, height, flipY: true);

    /// <summary>
    /// Parallel frame loader. Opens one <c>Tiff</c> handle per thread and jumps each straight to
    /// its frames by IFD offset (<see cref="TiffIfdIndex.SetFrame"/>) -- no hops to reach a
    /// thread's starting frame.
    /// Only called for compressed files, where decompression is CPU-bound
    /// (see <see cref="TiffFrameCodec.ResolveLoadThreads"/>).
    /// Frames are decoded concurrently into a staging array, then written via <c>SetArray</c> sequentially.
    /// </summary>
    private static void LoadFramesParallel<T>(
        string filePath, MatrixData<T> data, int width, int height, long[] ifdOffsets,
        int threads, IProgress<int>? progress, CancellationToken ct)
        where T : unmanaged
    {
        int frameCount = ifdOffsets.Length;
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

            for (int f = start; f < end; f++)
            {
                ct.ThrowIfCancellationRequested();
                TiffIfdIndex.SetFrame(localTiff, ifdOffsets, f);
                frames[f] = ReadFrameDataStripped<T>(localTiff, width, height);
                progress?.Report(Interlocked.Increment(ref reported) - 1);
            }
        });

        // Write decoded frames sequentially to avoid shared-state races in SetArray
        for (int f = 0; f < frameCount; f++)
        {
            data.SetArray(frames[f], f);
            data.GetValueRange(f); // recalc min/max for this frame (FastMinMaxFinder, cache-hot right after the copy)
        }
    }

    private static void SetDimensionsFromImageJ(IMatrixData data, ImageJMetadata metadata)
    {
        var axes = new List<Axis>();

        bool hasChannel = metadata.Channels > 1;
        string? channelName = hasChannel ? "Channel" : null;
        string zName = PickAxisName("Z", metadata.ZAxisName, channelName, metadata.TAxisName ?? "Time");
        string tName = PickAxisName("Time", metadata.TAxisName, channelName, zName);

        if (hasChannel)
            axes.Add(Axis.Channel(metadata.Channels));

        if (metadata.Slices > 1)
        {
            double zMin = -metadata.ZOrigin * metadata.Spacing;
            double zMax = (metadata.Slices - 1 - metadata.ZOrigin) * metadata.Spacing;
            string zUnit = metadata.ZUnit ?? "";
            axes.Add(zName == "Z"
                ? Axis.Z(metadata.Slices, zMin, zMax, zUnit)
                : new Axis(metadata.Slices, zMin, zMax, zName, zUnit, IsIndexLike(zMin, zMax, metadata.Slices, zUnit)));
        }

        if (metadata.Frames > 1)
        {
            double tMax = (metadata.Frames - 1) * metadata.Interval;
            axes.Add(tName == "Time"
                ? Axis.Time(metadata.Frames, 0, tMax, "s")
                : new Axis(metadata.Frames, 0, tMax, tName, "", IsIndexLike(0, tMax, metadata.Frames, "")));
        }

        if (axes.Count > 0)
            data.DefineDimensions(axes.ToArray());
    }

    // Uses the recorded name unless it would duplicate another axis of the same data.
    private static string PickAxisName(string standardName, string? recordedName, params string?[] takenNames)
    {
        if (recordedName == null)
            return standardName;
        foreach (string? taken in takenNames)
        {
            if (string.Equals(taken, recordedName, StringComparison.OrdinalIgnoreCase))
                return standardName;
        }
        return recordedName;
    }

    // An axis that counts 0, 1, 2, ... without a unit is an index axis.
    private static bool IsIndexLike(double min, double max, int count, string unit)
        => unit.Length == 0 && Math.Abs(min) < 1e-9 && Math.Abs(max - (count - 1)) < 1e-9;

    #endregion

    #region Save Implementation

    private static void SaveInternal<T>(BitMiracle.LibTiff.Classic.Tiff tiff, MatrixData<T> data, IProgress<int>? progress, bool compress)
        where T : unmanaged
    {
        int frameCount = data.FrameCount;
        progress?.Report(-frameCount); // signal start

        // Generate ImageJ metadata (null layout: the axes do not fit a hyperstack)
        var layout = ImageJAxisLayout.TryCreate(data);
        var ijMetadata = ImageJMetadata.FromMatrixData(data, layout);

        // Compute frame order (ImageJ uses C-Z-T order)
        int[] frameOrder = CalculateFrameOrder(data, ijMetadata, layout);

        // Write frames in order
        for (int i = 0; i < frameCount; i++)
        {
            int frameIndex = frameOrder[i];

            // Set basic TIFF tags
            SetBasicTiffTags(tiff, data.XCount, data.YCount, typeof(T), compress);

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

    private static void SetBasicTiffTags(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height, Type dataType, bool compress)
    {
        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);

        tiff.SetField(TiffTag.BITSPERSAMPLE, TiffPixelType.BitsPerSample(dataType));
        tiff.SetField(TiffTag.SAMPLEFORMAT, TiffPixelType.SampleFormatOf(dataType));

        // Compression (LZW without horizontal-differencing predictor).
        // Predictor.HORIZONTAL (tag 317 = 2) triggers FluoRender's DecodeAcc16,
        // which has a bug that causes a SIGSEGV on ARM64 macOS regardless of strip
        // layout.  Predictor.NONE avoids the call entirely with minimal size impact.
        if (compress)
        {
            tiff.SetField(TiffTag.COMPRESSION, Compression.LZW);
            tiff.SetField(TiffTag.PREDICTOR, Predictor.NONE);
        }
        else
        {
            tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
        }
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
        int bytesPerPixel = TiffPixelType.BytesPerSample(typeof(T));
        int stride = width * bytesPerPixel;
        var buffer = new byte[stride * height];

        // Y-flip: MatrixData (bottom-left origin) → TIFF (top-left origin). Samples go out in host
        // byte order, which is the order LibTiff declares in the file header it writes.
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(data, (height - 1 - row) * stride, buffer, row * stride, stride);

        // Write row by row
        for (int row = 0; row < height; row++)
        {
            if (!tiff.WriteScanline(buffer, row * stride, row, 0))
            {
                throw new IOException($"Failed to write scanline at row {row}");
            }
        }
    }

    private static int[] CalculateFrameOrder(IMatrixData data, ImageJMetadata metadata, ImageJAxisLayout? layout)
    {
        int[] order = new int[data.FrameCount];

        // Without a hyperstack layout the frames are written as they are stored
        if (layout == null)
        {
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            return order;
        }

        // ImageJ order: XYCZT (C varies fastest)
        int index = 0;
        for (int t = 0; t < metadata.Frames; t++)
        {
            for (int z = 0; z < metadata.Slices; z++)
            {
                for (int c = 0; c < metadata.Channels; c++)
                {
                    order[index++] = layout.GetFrameIndex(data, c, z, t);
                }
            }
        }

        return order;
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

        // [0] is the element count, [1] the data (see RegisterImageJTags).
        if (ijMeta != null && ijMetaBC != null && ijMeta.Length > 1 && ijMetaBC.Length > 1)
        {
            byte[]? metaBytes = ijMeta[1].ToByteArray();

            if (metaBytes != null && metaBytes.Length > 0)
            {
                // Convert ByteCounts to uint[]
                uint[] byteCounts = ConvertToUIntArray(ijMetaBC[1]);

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

        // Serialize metadata to "key=value\nkey=value\n..." format. Format-header entries describe
        // the file they came from (ImageDescription, FITS_HEADER, ...) and are not carried over.
        var headerKeys = data.GetFormatHeaderKeys();
        var sb = new System.Text.StringBuilder();
        foreach (var kvp in data.Metadata)
        {
            if (kvp.Key == "ImageDescription" || headerKeys.Contains(kvp.Key))
                continue;
            // A key containing '=' or a line break cannot be told apart from a value.
            if (kvp.Key.Length == 0 || kvp.Key.AsSpan().IndexOfAny('=', '\r', '\n') >= 0)
                continue;

            sb.Append(kvp.Key).Append('=').Append(EscapeInfoValue(kvp.Value)).Append('\n');
        }

        if (sb.Length == 0)
            return;

        string info = InfoEscapedMarker + "\n" + sb;

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

                // The type is a 4-byte integer ("info" = 0x696e666f) stored in the file's byte order,
                // so a little-endian file holds "ofni".
                var type = System.Text.Encoding.ASCII.GetString(header, headerPos, 4);
                if (!isBE)
                    type = string.Create(4, type, static (span, s) => { for (int k = 0; k < 4; k++) span[k] = s[3 - k]; });
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
        // Only text written by this handler is unescaped; an info block written by ImageJ or an
        // older version may legitimately contain backslashes.
        bool escaped = Array.IndexOf(lines, InfoEscapedMarker) >= 0;

        foreach (var line in lines)
        {
            if (line == InfoEscapedMarker)
                continue;

            var parts = line.Split(new[] { '=' }, 2);
            if (parts.Length == 2)
            {
                string key = parts[0].Trim();
                string value = escaped ? UnescapeInfoValue(parts[1]) : parts[1].Trim();

                if (!string.IsNullOrEmpty(key))
                {
                    metadata[key] = value;
                }
            }
        }
    }

    // First line of an info block written by this handler: its values escape backslashes and
    // line breaks, so multi-line values survive the one-entry-per-line layout.
    private const string InfoEscapedMarker = "mxplot.info.escaped=1";

    private static string EscapeInfoValue(string value)
    {
        if (value.AsSpan().IndexOfAny('\\', '\r', '\n') < 0)
            return value;
        return value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    private static string UnescapeInfoValue(string value)
    {
        if (value.IndexOf('\\') < 0)
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char n = value[i + 1];
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 'r') { sb.Append('\r'); i++; continue; }
                if (n == '\\') { sb.Append('\\'); i++; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
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


}
