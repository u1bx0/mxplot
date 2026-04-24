using System.IO;
using System.Text;

namespace MxPlot.Extensions.Tiff
{
    /// <summary>
    /// Writes a minimal BigTIFF file structure (header + IFDs) and pre-allocates pixel data
    /// space using <see cref="FileStream.SetLength"/>, making vessel creation essentially
    /// instant for large files on NTFS.
    /// <para>
    /// The OME-XML is placed at the very end of the file (after all pixel data regions).
    /// IFD 0 contains a <c>IMAGEDESCRIPTION</c> tag whose value points to that offset.
    /// IFDs for frames 1..N-1 omit the tag, following the OME-TIFF convention.
    /// </para>
    /// <para>
    /// Layout:
    /// <code>
    /// [16 B header]
    /// [IFD 0 (+ ext strip arrays if multi-strip)]
    /// [frame 0 pixel data]
    /// [IFD 1 (+ ext strip arrays)]
    /// [frame 1 pixel data]
    /// ...
    /// [IFD N-1]
    /// [frame N-1 pixel data]
    /// [OME-XML bytes (null-terminated)]
    /// </code>
    /// The pixel data regions are left as zero-filled sparse space; they will be written by
    /// <see cref="MxPlot.Core.IO.WritableVirtualStrippedFrames{T}"/> via MMF.
    /// </para>
    /// </summary>
    internal static class BigTiffVesselWriter
    {
        // BigTIFF field type codes
        private const ushort TypeAscii = 2;
        private const ushort TypeShort = 3;
        private const ushort TypeLong = 4;
        private const ushort TypeLong8 = 16;

        // TIFF tag numbers (ascending order is required within each IFD)
        private const ushort TagImageWidth = 0x0100;
        private const ushort TagImageLength = 0x0101;
        private const ushort TagBitsPerSample = 0x0102;
        private const ushort TagCompression = 0x0103;
        private const ushort TagPhotometric = 0x0106;
        private const ushort TagImageDescription = 0x010E;
        private const ushort TagStripOffsets = 0x0111;
        private const ushort TagSamplesPerPixel = 0x0115;
        private const ushort TagRowsPerStrip = 0x0116;
        private const ushort TagStripByteCounts = 0x0117;
        private const ushort TagPlanarConfig = 0x011C;
        private const ushort TagSampleFormat = 0x0153;

        /// <summary>
        /// Creates the BigTIFF vessel file and returns per-frame strip offset and
        /// byte-count arrays ready for use with
        /// <see cref="MxPlot.Core.IO.WritableVirtualStrippedFrames{T}"/>.
        /// </summary>
        /// <param name="filePath">Destination file path.</param>
        /// <param name="width">Frame width in pixels.</param>
        /// <param name="height">Frame height in pixels.</param>
        /// <param name="totalFrames">Total number of frames (C × Z × T × FOV).</param>
        /// <param name="bytesPerPixel">Bytes per pixel for type T.</param>
        /// <param name="bitsPerSample">Bits per sample (8, 16, 32, 64).</param>
        /// <param name="sampleFormatCode">TIFF SampleFormat tag value: 1=UINT, 2=INT, 3=IEEEFP.</param>
        /// <param name="omeXml">OME-XML string (without null terminator; one is appended internally).</param>
        /// <param name="rowsPerStrip">
        /// Rows per strip, pre-computed by <c>OmeTiffHandlerInstance.CalculateOptimalRowsPerStrip</c>.
        /// Must match what the instance uses so that strip offsets are consistent.
        /// </param>
        /// <returns>
        /// Per-frame jagged arrays <c>(offsets[frame][strip], byteCounts[frame][strip])</c>
        /// pointing into the pre-allocated pixel data regions.
        /// </returns>
        internal static (long[][] offsets, long[][] byteCounts) Build(
            string filePath,
            int width,
            int height,
            int totalFrames,
            int bytesPerPixel,
            int bitsPerSample,
            ushort sampleFormatCode,
            string omeXml,
            int rowsPerStrip)
        {
            byte[] omeXmlBytes = Encoding.UTF8.GetBytes(omeXml + "\0"); // UTF-8: preserves µ (U+00B5) and other non-ASCII chars in OME-XML

            // --- Strip geometry ---
            int numStrips = (height + rowsPerStrip - 1) / rowsPerStrip;
            int lastHeight = height - (numStrips - 1) * rowsPerStrip;
            long stripSize = (long)rowsPerStrip * width * bytesPerPixel;
            long lastStripSize = (long)lastHeight * width * bytesPerPixel;
            long frameSize = (long)height * width * bytesPerPixel;
            bool multiStrip = numStrips > 1;

            // --- IFD sizes ---
            // BigTIFF IFD: 8 (entry count) + N × 20 (entries) + 8 (next IFD offset)
            // Multi-strip needs external arrays: STRIPOFFSETS array + STRIPBYTECOUNTS array
            int extArrayBytes = multiStrip ? numStrips * 8 * 2 : 0; // 2 arrays × numStrips × 8 B
            int tagsIfd0 = 12; // includes IMAGEDESCRIPTION
            int tagsIfdN = 11;
            int ifd0Size = 8 + tagsIfd0 * 20 + 8 + extArrayBytes;
            int ifdNSize = 8 + tagsIfdN * 20 + 8 + extArrayBytes;

            // --- File layout (IFD-first, then pixel data, OME-XML at the end) ---
            long pos = 16; // after BigTIFF header
            long[] ifdOffsets = new long[totalFrames];
            long[] dataOffsets = new long[totalFrames];

            for (int i = 0; i < totalFrames; i++)
            {
                ifdOffsets[i] = pos;
                pos += (i == 0) ? ifd0Size : ifdNSize;
                dataOffsets[i] = pos;
                pos += frameSize;
            }
            long omeXmlOffset = pos;
            long totalSize = pos + omeXmlBytes.Length;

            // --- Build per-frame strip offset / bytecount arrays ---
            long[][] allStripOffsets = new long[totalFrames][];
            long[][] allStripByteCounts = new long[totalFrames][];

            for (int i = 0; i < totalFrames; i++)
            {
                allStripOffsets[i] = new long[numStrips];
                allStripByteCounts[i] = new long[numStrips];

                for (int s = 0; s < numStrips; s++)
                {
                    allStripOffsets[i][s] = dataOffsets[i] + (long)s * rowsPerStrip * width * bytesPerPixel;
                    allStripByteCounts[i][s] = (s < numStrips - 1) ? stripSize : lastStripSize;
                }
            }

            // --- Write the structure ---
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            fs.SetLength(totalSize); // Pre-allocate entire file in O(1) on NTFS (sparse/lazy allocation)
            using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

            // BigTIFF header (16 bytes)
            bw.Write((ushort)0x4949); // II — little-endian byte order
            bw.Write((ushort)0x002B); // BigTIFF magic
            bw.Write((ushort)0x0008); // sizeof(offset) = 8
            bw.Write((ushort)0x0000); // constant 0
            bw.Write(ifdOffsets[0]);  // offset to first IFD (8 bytes)

            // IFDs
            for (int i = 0; i < totalFrames; i++)
            {
                bool isFirst = (i == 0);
                int numTags = isFirst ? tagsIfd0 : tagsIfdN;
                long nextIfd = (i < totalFrames - 1) ? ifdOffsets[i + 1] : 0L;
                long ifdBase = ifdOffsets[i];

                // External array offsets come immediately after the IFD directory block
                long extBase = ifdBase + 8 + (long)numTags * 20 + 8;
                long soArrOff = extBase;                                      // STRIPOFFSETS array
                long sbcArrOff = extBase + (multiStrip ? numStrips * 8L : 0L); // STRIPBYTECOUNTS array

                fs.Seek(ifdBase, SeekOrigin.Begin);
                bw.Write((ulong)numTags); // IFD entry count (8 bytes in BigTIFF)

                // Entries MUST be in ascending tag-number order
                WriteEntry(bw, TagImageWidth, TypeLong, 1, (ulong)width);
                WriteEntry(bw, TagImageLength, TypeLong, 1, (ulong)height);
                WriteEntry(bw, TagBitsPerSample, TypeShort, 1, (ulong)bitsPerSample);
                WriteEntry(bw, TagCompression, TypeShort, 1, 1UL);   // 1 = NONE
                WriteEntry(bw, TagPhotometric, TypeShort, 1, 1UL);   // 1 = MINISBLACK

                if (isFirst)
                    WriteEntry(bw, TagImageDescription, TypeAscii, (ulong)omeXmlBytes.Length, (ulong)omeXmlOffset);

                if (multiStrip)
                    WriteEntry(bw, TagStripOffsets, TypeLong8, (ulong)numStrips, (ulong)soArrOff);
                else
                    WriteEntry(bw, TagStripOffsets, TypeLong8, 1UL, (ulong)allStripOffsets[i][0]);

                WriteEntry(bw, TagSamplesPerPixel, TypeShort, 1, 1UL);
                WriteEntry(bw, TagRowsPerStrip, TypeLong, 1, (ulong)rowsPerStrip);

                if (multiStrip)
                    WriteEntry(bw, TagStripByteCounts, TypeLong8, (ulong)numStrips, (ulong)sbcArrOff);
                else
                    WriteEntry(bw, TagStripByteCounts, TypeLong8, 1UL, (ulong)allStripByteCounts[i][0]);

                WriteEntry(bw, TagPlanarConfig, TypeShort, 1, 1UL);  // 1 = CONTIG
                WriteEntry(bw, TagSampleFormat, TypeShort, 1, (ulong)sampleFormatCode);

                bw.Write((ulong)nextIfd); // next IFD offset (0 = end of chain)

                // External strip arrays (multi-strip only)
                if (multiStrip)
                {
                    foreach (long off in allStripOffsets[i]) bw.Write((ulong)off);
                    foreach (long cnt in allStripByteCounts[i]) bw.Write((ulong)cnt);
                }
            }

            // OME-XML at end of file (pixel data regions remain zero-filled)
            fs.Seek(omeXmlOffset, SeekOrigin.Begin);
            bw.Write(omeXmlBytes);

            return (allStripOffsets, allStripByteCounts);
        }

        // Each BigTIFF IFD entry is exactly 20 bytes: tag(2) + type(2) + count(8) + value(8)
        private static void WriteEntry(BinaryWriter bw, ushort tag, ushort type, ulong count, ulong value)
        {
            bw.Write(tag);
            bw.Write(type);
            bw.Write(count);
            bw.Write(value); // inline value, or file offset to external data if count*typeSize > 8
        }
    }
}
