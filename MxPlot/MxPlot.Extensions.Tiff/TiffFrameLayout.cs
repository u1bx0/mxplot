using BitMiracle.LibTiff.Classic;
using System;
using System.IO;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Where each frame's pixel bytes sit in the file when the frames do not each have their own IFD.
/// A file with one IFD per frame is addressed through <see cref="TiffStorageLayout"/> instead.
/// </summary>
/// <remarks>
/// Packed layout: a single IFD (one contiguous, uncompressed run of strips) describes the first
/// frame, and the remaining frames follow it as raw pixel data at a fixed stride, with no IFD of
/// their own. The frame count comes from the dialect's metadata, not from the IFD count.
/// </remarks>
internal static class TiffFrameLayout
{
    /// <summary>
    /// Validates that the current directory's strips form a contiguous, uncompressed run covering
    /// exactly one frame, and that the file holds enough trailing bytes for <paramref name="frameCount"/>
    /// frames at that stride.
    /// </summary>
    /// <returns>The file offset of frame 0's first byte, and the byte size of one frame.</returns>
    public static (long baseOffset, long frameSizeBytes) ValidatePacked(
        BitMiracle.LibTiff.Classic.Tiff tiff, string filename, int height, int frameCount)
    {
        int compression = tiff.GetField(TiffTag.COMPRESSION)?[0].ToInt() ?? (int)Compression.NONE;
        if (compression != (int)Compression.NONE)
            throw new NotSupportedException(
                "This file uses a single-IFD packed layout (the metadata declares more frames than there are " +
                "IFDs), which requires uncompressed, fixed-stride pixel data to locate each frame. " +
                $"Found compression={compression}.");

        // The first frame may itself be split into many strips (RowsPerStrip far smaller than
        // ImageLength). What matters is that those strips lay the frame out as one contiguous run,
        // so it has the same fixed stride as every frame after it.
        var stripOffsets = tiff.GetField(TiffTag.STRIPOFFSETS);
        var stripByteCounts = tiff.GetField(TiffTag.STRIPBYTECOUNTS);
        if (stripOffsets == null || stripOffsets.Length == 0 || stripByteCounts == null || stripByteCounts.Length == 0)
            throw new InvalidDataException("Missing StripOffsets/StripByteCounts tag.");

        // The FieldValue wraps the whole tag value (a ulong[] once more than one strip forces an
        // out-of-line array), so it is unwrapped with TolongArray(); ToLong() only handles one strip.
        long[] offsets = stripOffsets[0].TolongArray();
        long[] byteCounts = stripByteCounts[0].TolongArray();
        long baseOffset = offsets[0];

        long frameSizeBytes = (long)tiff.ScanlineSize() * height;

        long firstFrameSpan = offsets[^1] + byteCounts[^1] - offsets[0];
        if (firstFrameSpan != frameSizeBytes)
            throw new NotSupportedException(
                "The single-IFD packed layout requires the first IFD's strips to be a contiguous, " +
                $"uncompressed run covering exactly one frame; found a {firstFrameSpan}-byte span " +
                $"for a {frameSizeBytes}-byte frame.");

        long available = new FileInfo(filename).Length - baseOffset;
        if (available < frameSizeBytes * frameCount)
            throw new InvalidDataException(
                $"The metadata declares {frameCount} frames, but the file only holds enough raw pixel " +
                $"data for {available / frameSizeBytes} frame(s) after the first IFD's strip.");

        return (baseOffset, frameSizeBytes);
    }

    /// <summary>
    /// The per-frame offset and byte-count tables of a packed file, in the shape the MMF readers take.
    /// Computed from the fixed stride alone: no IFD walk and no pixel read.
    /// </summary>
    public static (long[][] offsets, long[][] byteCounts) PackedOffsets(long baseOffset, long frameSizeBytes, int frameCount)
    {
        var offsets = new long[frameCount][];
        var byteCounts = new long[frameCount][];
        for (int i = 0; i < frameCount; i++)
        {
            offsets[i] = new[] { baseOffset + i * frameSizeBytes };
            byteCounts[i] = new[] { frameSizeBytes };
        }
        return (offsets, byteCounts);
    }
}
