using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Locates every IFD (one per frame) in a TIFF file and moves a LibTiff handle to one of them by
/// position. The read-side counterpart of <see cref="BigTiffVesselWriter"/>: it walks only the
/// file's structural chain (header, then each IFD's entry count and next-IFD pointer) and never
/// interprets tags -- tag parsing, decoding and writing all stay with LibTiff.NET.
/// </summary>
/// <remarks>
/// <para>
/// Exists because LibTiff.NET 2.4.660 addresses directories with <see cref="short"/>:
/// <c>NumberOfDirectories()</c> returns <c>Int16</c> (70,000 IFDs report as 4,464),
/// <c>SetDirectory(short)</c> cannot reach index 32,768 or beyond, and even sequential
/// <c>ReadDirectory()</c> throws <see cref="IndexOutOfRangeException"/> after 32,767 IFDs because
/// its internal IFD-loop list is short-indexed. <c>SetSubDirectory(long offset)</c> has none of these
/// limits, so frames are addressed by the offsets collected here instead of by index.
/// </para>
/// <para>
/// Walking the chain directly is also far cheaper than enumerating with LibTiff, which parses every
/// tag of every IFD along the way.
/// </para>
/// </remarks>
internal static class TiffIfdIndex
{
    /// <summary>Collects the file offset of every IFD in <paramref name="path"/>, in file-chain order.</summary>
    /// <exception cref="InvalidDataException">The file does not start with a valid TIFF/BigTIFF header.</exception>
    public static long[] ReadOffsets(string path, CancellationToken ct = default)
    {
        // FileShare.ReadWrite: callers typically already hold a LibTiff handle on the same file.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        return ReadOffsets(stream, ct);
    }

    /// <summary>
    /// Collects the IFD offsets of the file behind an open LibTiff handle (re-opened by name, so the
    /// handle's own position is left untouched).
    /// </summary>
    public static long[] ReadOffsets(BitMiracle.LibTiff.Classic.Tiff tiff, CancellationToken ct = default)
        => ReadOffsets(tiff.FileName(), ct);

    /// <summary>Collects the file offset of every IFD in a seekable TIFF/BigTIFF stream.</summary>
    /// <remarks>
    /// Stops at a zero next-IFD pointer, and also -- rather than throwing -- at a pointer that falls
    /// outside the file or revisits an IFD already seen (a truncated or cyclic chain), keeping every
    /// IFD read up to that point. LibTiff treats those cases the same way.
    /// </remarks>
    public static long[] ReadOffsets(Stream stream, CancellationToken ct = default)
    {
        long length = stream.Length;
        Span<byte> buf = stackalloc byte[16];

        stream.Position = 0;
        if (length < 8 || stream.Read(buf[..8]) != 8)
            throw new InvalidDataException("File is too short to be a TIFF.");

        bool little = buf[0] == (byte)'I' && buf[1] == (byte)'I';
        bool big = buf[0] == (byte)'M' && buf[1] == (byte)'M';
        if (!little && !big)
            throw new InvalidDataException("Not a TIFF file: missing II/MM byte-order mark.");

        ushort version = ReadUInt16(buf[2..], little);
        bool isBigTiff;
        long offset;
        if (version == 42)
        {
            isBigTiff = false;
            offset = ReadUInt32(buf[4..], little);
        }
        else if (version == 43)
        {
            isBigTiff = true;
            if (length < 16 || stream.Read(buf[8..16]) != 8)
                throw new InvalidDataException("File is too short to be a BigTIFF.");
            if (ReadUInt16(buf[4..], little) != 8)
                throw new InvalidDataException("Unsupported BigTIFF offset size.");
            offset = checked((long)ReadUInt64(buf[8..], little));
        }
        else
        {
            throw new InvalidDataException($"Not a TIFF file: unexpected version {version}.");
        }

        int countSize = isBigTiff ? 8 : 2;
        int entrySize = isBigTiff ? 20 : 12;
        int pointerSize = isBigTiff ? 8 : 4;

        var offsets = new List<long>();
        var visited = new HashSet<long>();

        while (offset != 0)
        {
            if ((offsets.Count & 0x3FF) == 0) ct.ThrowIfCancellationRequested();

            if (offset < 0 || offset + countSize > length || !visited.Add(offset))
                break;

            stream.Position = offset;
            if (stream.Read(buf[..countSize]) != countSize) break;
            ulong entryCount = isBigTiff ? ReadUInt64(buf, little) : ReadUInt16(buf, little);

            long pointerPos = offset + countSize + (long)entryCount * entrySize;
            if (entryCount > (ulong)length || pointerPos < 0 || pointerPos + pointerSize > length)
                break;

            offsets.Add(offset);

            stream.Position = pointerPos;
            if (stream.Read(buf[..pointerSize]) != pointerSize) break;
            ulong next = isBigTiff ? ReadUInt64(buf, little) : ReadUInt32(buf, little);
            if (next > long.MaxValue) break;
            offset = (long)next;
        }

        return offsets.ToArray();
    }

    /// <summary>
    /// Makes <paramref name="frameIndex"/>'s IFD the current directory of <paramref name="tiff"/>.
    /// Use this in place of <c>SetDirectory((short)index)</c> or <c>ReadDirectory()</c> hops.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="frameIndex"/> is outside <paramref name="ifdOffsets"/>.</exception>
    /// <exception cref="InvalidDataException">LibTiff could not read the IFD at that offset.</exception>
    public static void SetFrame(BitMiracle.LibTiff.Classic.Tiff tiff, long[] ifdOffsets, int frameIndex)
    {
        if ((uint)frameIndex >= (uint)ifdOffsets.Length)
            throw new ArgumentOutOfRangeException(nameof(frameIndex), $"Frame {frameIndex} is outside the file's {ifdOffsets.Length} IFDs.");
        if (!tiff.SetSubDirectory(ifdOffsets[frameIndex]))
            throw new InvalidDataException($"Could not read the IFD of frame {frameIndex}.");
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> s, bool little)
        => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);

    private static uint ReadUInt32(ReadOnlySpan<byte> s, bool little)
        => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

    private static ulong ReadUInt64(ReadOnlySpan<byte> s, bool little)
        => little ? BinaryPrimitives.ReadUInt64LittleEndian(s) : BinaryPrimitives.ReadUInt64BigEndian(s);
}
