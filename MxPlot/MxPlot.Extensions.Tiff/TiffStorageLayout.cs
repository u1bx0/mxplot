using BitMiracle.LibTiff.Classic;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Collects the per-frame strip or tile offset tables of an uncompressed multi-IFD TIFF, for the
/// MMF-backed Virtual route (<see cref="MxPlot.Core.IO.StrippedMmfFrames{T}"/> /
/// <see cref="MxPlot.Core.IO.TiledMmfFrames{T}"/>). Shared by <c>OmeTiffHandlerInstance&lt;T&gt;</c>
/// and <see cref="ImageJTiffHandler"/>: nothing here depends on OME or ImageJ metadata.
/// </summary>
/// <remarks>
/// The MMF readers copy each strip/tile verbatim, so every frame must be uncompressed; a
/// compressed frame throws <see cref="NotSupportedException"/>.
/// </remarks>
internal static class TiffStorageLayout
{
    /// <summary>
    /// True when <paramref name="tiff"/>'s current directory can be mapped by
    /// <see cref="MxPlot.Core.IO.StrippedMmfFrames{T}"/> as-is: uncompressed, strip-organized, and
    /// with exactly one sample per pixel (the MMF reader has no per-pixel stride).
    /// </summary>
    public static bool IsStripMappable(BitMiracle.LibTiff.Classic.Tiff tiff, int width)
    {
        int compression = tiff.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? (int)Compression.NONE;
        if (compression != (int)Compression.NONE)
            return false;
        if (tiff.GetField(TiffTag.TILEWIDTH) != null)
            return false;
        int bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 1;
        return bitsPerSample % 8 == 0 && tiff.ScanlineSize() == (long)width * (bitsPerSample / 8);
    }

    /// <summary>Reads every frame's StripOffsets / StripByteCounts.</summary>
    public static (long[][] offsets, long[][] byteCounts) ScanStrips(
        BitMiracle.LibTiff.Classic.Tiff tiff, long[] ifdOffsets, int totalFrames, IProgress<int>? progress, CancellationToken ct = default)
    {
        RequireIfdCount(ifdOffsets, totalFrames);
        long[][] offsets = new long[totalFrames][];
        long[][] byteCounts = new long[totalFrames][];

        progress?.Report(-totalFrames); // Signal total frame count to the UI

        for (int i = 0; i < totalFrames; i++)
        {
            ct.ThrowIfCancellationRequested();
            TiffIfdIndex.SetFrame(tiff, ifdOffsets, i);
            RequireUncompressed(tiff, i);

            FieldValue[] offsetTag = tiff.GetField(TiffTag.STRIPOFFSETS);
            if (offsetTag == null || offsetTag.Length == 0)
                throw new InvalidDataException($"Missing StripOffsets tag at frame {i}.");

            FieldValue[] byteCountTag = tiff.GetField(TiffTag.STRIPBYTECOUNTS);
            if (byteCountTag == null || byteCountTag.Length == 0)
                throw new InvalidDataException($"Missing StripByteCounts tag at frame {i}.");

            offsets[i] = ToLongArray(offsetTag[0]);
            byteCounts[i] = ToLongArray(byteCountTag[0]);
            progress?.Report(i);
        }

        return (offsets, byteCounts);
    }

    /// <summary>
    /// Reads every frame's TileOffsets / TileByteCounts. The tile size is taken from frame 0 and
    /// assumed to be the same for all frames.
    /// </summary>
    public static (int tileWidth, int tileLength, long[][] offsets, long[][] byteCounts) ScanTiles(
        BitMiracle.LibTiff.Classic.Tiff tiff, long[] ifdOffsets, int totalFrames, IProgress<int>? progress, CancellationToken ct = default)
    {
        RequireIfdCount(ifdOffsets, totalFrames);
        long[][] offsets = new long[totalFrames][];
        long[][] byteCounts = new long[totalFrames][];

        progress?.Report(-totalFrames);

        TiffIfdIndex.SetFrame(tiff, ifdOffsets, 0);
        FieldValue[] twTag = tiff.GetField(TiffTag.TILEWIDTH);
        FieldValue[] tlTag = tiff.GetField(TiffTag.TILELENGTH);
        if (twTag == null || tlTag == null)
            throw new InvalidDataException("TileWidth or TileLength tag is missing in a tiled TIFF.");

        int tileWidth = twTag[0].ToInt();
        int tileLength = tlTag[0].ToInt();

        for (int i = 0; i < totalFrames; i++)
        {
            ct.ThrowIfCancellationRequested();
            TiffIfdIndex.SetFrame(tiff, ifdOffsets, i);
            RequireUncompressed(tiff, i);

            // Each frame holds many tiles; the array length is the frame's tile count
            FieldValue[] offsetTag = tiff.GetField(TiffTag.TILEOFFSETS);
            if (offsetTag == null || offsetTag.Length == 0)
                throw new InvalidDataException($"Missing TileOffsets tag at frame {i}.");

            FieldValue[] byteCountTag = tiff.GetField(TiffTag.TILEBYTECOUNTS);
            if (byteCountTag == null || byteCountTag.Length == 0)
                throw new InvalidDataException($"Missing TileByteCounts tag at frame {i}.");

            offsets[i] = ToLongArray(offsetTag[0]);
            byteCounts[i] = ToLongArray(byteCountTag[0]);
            progress?.Report(i);
        }

        return (tileWidth, tileLength, offsets, byteCounts);
    }

    private static void RequireIfdCount(long[] ifdOffsets, int totalFrames)
    {
        if (ifdOffsets.Length < totalFrames)
            throw new InvalidDataException($"The file has {ifdOffsets.Length} IFDs, but the metadata declares {totalFrames} frames.");
    }

    private static void RequireUncompressed(BitMiracle.LibTiff.Classic.Tiff tiff, int frameIndex)
    {
        FieldValue[] compTag = tiff.GetField(TiffTag.COMPRESSION);
        if (compTag != null && compTag[0].ToInt() != (int)Compression.NONE)
            throw new NotSupportedException($"Virtual mode requires uncompressed TIFF. Frame {frameIndex} is compressed.");
    }

    private static long[] ToLongArray(FieldValue fieldValue)
    {
        // BigTIFF: LibTiff may hold the offsets as long[] or ulong[]
        if (fieldValue.Value is long[] longArray)
            return longArray;

        if (fieldValue.Value is ulong[] ulongArray)
            return ulongArray.Select(v => (long)v).ToArray();

        // Classic TIFF: offsets are 32-bit unsigned; go through uint so 2-4 GB offsets,
        // which read back negative as signed int, widen correctly
        int[] intArray = fieldValue.ToIntArray();
        if (intArray != null)
            return intArray.Select(v => (long)(uint)v).ToArray();

        return Array.Empty<long>();
    }
}
