using BitMiracle.LibTiff.Classic;
using System;
using System.Runtime.InteropServices;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Shared low-level TIFF strip/tile pixel-reading routines, used by both
/// <c>OmeTiffHandlerInstance&lt;T&gt;</c> and <c>ImageJTiffHandler</c>. Consolidates what were
/// previously two independent implementations of "read one frame from a strip/tile-organized
/// TIFF into a T[]".
/// </summary>
/// <remarks>
/// The TIFF (top-left origin) to MxPlot (bottom-left origin) Y-flip is an optional inline step
/// (<c>flipY</c> on each method), not baked in unconditionally, because the two
/// existing callers apply it differently: <c>OmeTiffHandlerInstance</c> reads raw and applies a
/// separate bulk <c>HyperstackData&lt;T&gt;.FlipY()</c> pass afterward (needed because that same
/// method also has to cover Virtual-mode data, whose flip happens at the MMF layer instead), while
/// <c>ImageJTiffHandler</c> always flips inline. Passing the caller's own existing choice through
/// preserves both call sites' behavior unchanged.
/// </remarks>
internal static class TiffFrameCodec
{
    /// <summary>
    /// Reads one frame from a strip-organized TIFF using <c>ReadEncodedStrip</c> --
    /// one API call per strip instead of one per row.
    /// </summary>
    /// <param name="tiff">The open TIFF handle, positioned on the directory to read.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="flipY">
    /// When <see langword="true"/>, rows are written in reverse order (TIFF top-left origin to
    /// MxPlot bottom-left origin) as part of this same pass. When <see langword="false"/>, rows
    /// are written in file order and flipping (if needed) is the caller's responsibility.
    /// </param>
    public static T[] ReadStripped<T>(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height, bool flipY) where T : unmanaged
    {
        int typeSize = Marshal.SizeOf(typeof(T));
        int scanlineSize = tiff.ScanlineSize();
        int filePixelStride = width > 0 ? scanlineSize / width : typeSize;

        int rowsPerStrip = tiff.GetField(TiffTag.ROWSPERSTRIP)?[0].ToInt() ?? height;
        int numStrips = tiff.NumberOfStrips();
        int maxStripBytes = tiff.StripSize();
        var stripBuf = new byte[maxStripBytes];

        var result = new T[width * height];

        for (int strip = 0; strip < numStrips; strip++)
        {
            int bytesRead = tiff.ReadEncodedStrip(strip, stripBuf, 0, maxStripBytes);
            if (bytesRead < 0)
                throw new InvalidOperationException($"ReadEncodedStrip failed: strip={strip}");

            int startRow = strip * rowsPerStrip;
            int actualRows = Math.Min(rowsPerStrip, height - startRow);

            CopyRows(stripBuf, result, actualRows, startRow, width, height, scanlineSize, filePixelStride, typeSize, flipY);
        }
        return result;
    }

    /// <summary>
    /// Reads one frame from a tile-organized TIFF using <c>ReadEncodedTile</c> --
    /// one API call per tile instead of one per row.
    /// </summary>
    /// <param name="tiff">The open TIFF handle, positioned on the directory to read.</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="flipY">See <see cref="ReadStripped{T}"/>.</param>
    public static T[] ReadTiled<T>(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height, bool flipY) where T : unmanaged
    {
        int typeSize = Marshal.SizeOf(typeof(T));

        int tileWidth = tiff.GetField(TiffTag.TILEWIDTH)[0].ToInt();
        int tileHeight = tiff.GetField(TiffTag.TILELENGTH)[0].ToInt();
        int tilesAcross = (width + tileWidth - 1) / tileWidth;
        int tilesDown = (height + tileHeight - 1) / tileHeight;
        int maxTileBytes = tiff.TileSize();
        var tileBuf = new byte[maxTileBytes];

        var result = new T[width * height];
        Span<byte> dest = MemoryMarshal.AsBytes(result.AsSpan());

        for (int tileRow = 0; tileRow < tilesDown; tileRow++)
        {
            for (int tileCol = 0; tileCol < tilesAcross; tileCol++)
            {
                int tileIndex = tileRow * tilesAcross + tileCol;
                int bytesRead = tiff.ReadEncodedTile(tileIndex, tileBuf, 0, maxTileBytes);
                if (bytesRead < 0)
                    throw new InvalidOperationException($"ReadEncodedTile failed: tile={tileIndex}");

                int startX = tileCol * tileWidth;
                int startY = tileRow * tileHeight;
                int actualTileWidth = Math.Min(tileWidth, width - startX);
                int actualTileHeight = Math.Min(tileHeight, height - startY);

                for (int row = 0; row < actualTileHeight; row++)
                {
                    int srcOffset = row * tileWidth * typeSize;
                    int fileRow = startY + row;
                    int dstRow = flipY ? height - 1 - fileRow : fileRow;
                    int dstOffset = (dstRow * width + startX) * typeSize;
                    tileBuf.AsSpan(srcOffset, actualTileWidth * typeSize)
                           .CopyTo(dest.Slice(dstOffset, actualTileWidth * typeSize));
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Copies one strip's decoded rows into <paramref name="result"/>, taking the fast (bulk
    /// memory copy) path when the file's per-pixel byte layout exactly matches
    /// <typeparamref name="T"/>, and falling back to a per-pixel conversion only when it does not
    /// (extra samples per pixel). The fallback uses <see cref="MemoryMarshal.Read{T}"/> to
    /// reinterpret bytes as <typeparamref name="T"/> directly -- no boxing, no per-type branching.
    /// </summary>
    private static void CopyRows<T>(byte[] stripBuf, T[] result, int actualRows, int startRow, int width, int height,
        int scanlineSize, int filePixelStride, int typeSize, bool flipY) where T : unmanaged
    {
        Span<byte> dest = MemoryMarshal.AsBytes(result.AsSpan());

        if (filePixelStride == typeSize)
        {
            int bytesPerRow = width * typeSize;
            if (!flipY)
            {
                stripBuf.AsSpan(0, actualRows * bytesPerRow).CopyTo(dest.Slice(startRow * bytesPerRow));
            }
            else
            {
                for (int r = 0; r < actualRows; r++)
                {
                    int dstRow = height - 1 - (startRow + r);
                    stripBuf.AsSpan(r * bytesPerRow, bytesPerRow).CopyTo(dest.Slice(dstRow * bytesPerRow));
                }
            }
        }
        else
        {
            for (int r = 0; r < actualRows; r++)
            {
                int dstRow = flipY ? height - 1 - (startRow + r) : startRow + r;
                int srcRowBase = r * scanlineSize;
                int dstRowBase = dstRow * width;
                for (int col = 0; col < width; col++)
                {
                    result[dstRowBase + col] = MemoryMarshal.Read<T>(stripBuf.AsSpan(srcRowBase + col * filePixelStride, typeSize));
                }
            }
        }
    }
}
