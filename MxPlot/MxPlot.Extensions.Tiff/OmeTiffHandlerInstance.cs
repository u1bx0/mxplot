using BitMiracle.LibTiff.Classic;
using MxPlot.Core;
using MxPlot.Core.IO;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using static MxPlot.Extensions.Tiff.OmeTiffHandler;

namespace MxPlot.Extensions.Tiff
{
    /// <summary>
    /// Reads and writes OME-TIFF hyperstack files.
    /// Supports signed/unsigned 16-bit, 8-bit, 32-bit, and float pixel types.
    /// </summary>
    /// <typeparam name="T">Pixel data type (short, ushort, byte, sbyte, int, uint, float)</typeparam>
    public class OmeTiffHandlerInstance<T> where T : unmanaged
    {
        /// <summary>
        /// Maximum degree of parallelism for InMemory loading.
        /// <c>0</c> or <c>1</c> = sequential (default).
        /// <c>N &gt; 1</c> = open N LibTiff handles and decompress frames in parallel.
        /// Most effective for LZW/Deflate-compressed files where decompression is CPU-bound.
        /// Uncompressed files are already fast after the O(N) IFD traversal fix.
        /// </summary>
        public int MaxParallelDegree { get; set; } = 0;

        #region Write Methods

        /// <summary>
        /// Writes a hyperstack to an OME-TIFF file, loading all frames into memory.
        /// </summary>
        public void WriteHyperstack(string filename, List<T[]> imageStack,
            int width, int height, int channels, int zSlices, int timePoints,
            int fovCount = 1,
            double pixelSizeX = 1.0, double pixelSizeY = 1.0, double pixelSizeZ = 1.0)
        {
            var data = new HyperstackData<T>();
            data.Width = width;
            data.Height = height;
            data.ZSlices = zSlices;
            data.TimePoints = timePoints;
            data.Channels = channels;
            data.PixelSizeX = pixelSizeX;
            data.PixelSizeY = pixelSizeY;
            data.PixelSizeZ = pixelSizeZ;
            data.FovCount = fovCount;
            WriteHyperstack(filename, imageStack.AsEnumerable(), data);
        }

        /// <summary>
        /// Writes a hyperstack to an OME-TIFF file using lazy enumeration (memory-efficient).
        /// </summary>
        public void WriteHyperstack(string filename, IEnumerable<T[]> imageFrames,
            HyperstackData<T> data,
            OmeTiffOptions? options = null, IProgress<int>? progress = null)
        {

            int width = data.Width;
            int height = data.Height;
            int channels = data.Channels;
            int zSlices = data.ZSlices;
            int timePoints = data.TimePoints;
            double pixelSizeX = data.PixelSizeX;
            double pixelSizeY = data.PixelSizeY;
            double pixelSizeZ = data.PixelSizeZ;
            if (channels <= 0 || zSlices <= 0 || timePoints <= 0)
                throw new ArgumentException("Channels, Z slices, and time points must each be at least 1.");

            // Use default options if not provided
            options = options ?? new OmeTiffOptions();
            int expectedFrames = channels * zSlices * timePoints * Math.Max(1, data.FovCount);
            // Estimate total pixel count to detect BigTIFF requirement
            var pixels = width * height * expectedFrames;
            if (pixels > 4L * 1024 * 1024 * 1024 / GetBytesPerPixel())
            {
                // Force BigTIFF when data exceeds 4 GB (compression may keep actual size smaller)
                options.UseBigTiff = true;
            }
            // Select mode: "w" = standard TIFF, "w8" = BigTIFF
            string mode = options.UseBigTiff ? "w8" : "w";

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, mode))
            {
                if (tiff == null) throw new IOException("Could not create Tiff file.");

                var omeXml = CreateOmeXml(data);
                //omeXml = omeXml.Replace("\r", "").Replace("\n", ""); // collapse to a single line
                Debug.WriteLine("[OMETiffHandler] XML: " + omeXml);

                // Start signal: negative value = total frame count
                progress?.Report(-expectedFrames);
                int frameIndex = 0;
                foreach (var frame in imageFrames)
                {
                    if (frameIndex >= expectedFrames) break;

                    WriteFrameSafe(tiff, frame, width, height, frameIndex, expectedFrames, omeXml, options);
                    progress?.Report(frameIndex);
                    frameIndex++;
                }
                tiff.Flush();
                progress?.Report(expectedFrames);
            }
        }

        private void WriteFrameSafe(BitMiracle.LibTiff.Classic.Tiff tiff, T[] data, int width, int height, int frameIndex, int totalFrames, string omeXml, OmeTiffOptions options)
        {
            try
            {
                SetBasicTiffTags(tiff, width, height, options);

                // Write OME-XML to the first frame only.
                // Pass UTF-8 bytes directly to avoid LibTiff's ASCII encoding
                // which corrupts non-ASCII chars like µ (U+00B5).
                if (frameIndex == 0)
                {
                    byte[] utf8 = Encoding.UTF8.GetBytes(omeXml);
                    tiff.SetField(TiffTag.IMAGEDESCRIPTION, utf8);
                }

                WriteImageData(tiff, data, width, height);

                if (frameIndex < totalFrames - 1)
                {
                    tiff.WriteDirectory();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error at {frameIndex}: {ex.Message}", ex);
            }
        }


        private void SetBasicTiffTags(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height, OmeTiffOptions options)
        {
            var sampleFormat = GetSampleFormat();
            tiff.SetField(TiffTag.IMAGEWIDTH, width);
            tiff.SetField(TiffTag.IMAGELENGTH, height);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.BITSPERSAMPLE, GetBitsPerSample());
            tiff.SetField(TiffTag.SAMPLEFORMAT, sampleFormat);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            //tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);

            // Apply compression settings
            tiff.SetField(TiffTag.COMPRESSION, options.Compression);

            // For LZW or Deflate, apply the configured predictor.
            // Default is Predictor.NONE; callers may opt in to HORIZONTAL for
            // higher compression when the target reader is known-compatible.
            if (options.Compression == Compression.LZW || options.Compression == Compression.ADOBE_DEFLATE)
            {
                if (sampleFormat == SampleFormat.IEEEFP)
                    tiff.SetField(TiffTag.PREDICTOR, Predictor.NONE);
                else
                    tiff.SetField(TiffTag.PREDICTOR, options.Predictor);
            }
            // Set RowsPerStrip
            int rowsPerStrip = CalculateOptimalRowsPerStrip(width, height);
            tiff.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip);
        }

        private int CalculateOptimalRowsPerStrip(int width, int height)
        {
            // Use a single strip per frame (rowsPerStrip = height).
            //
            // Some readers (e.g. FluoRender) have a bug in their horizontal-predictor
            // undo pass where they iterate rowsPerStrip rows unconditionally, even on the
            // last strip which may contain fewer rows.  With a single strip there is no
            // partial last strip, so the bug is never triggered.
            //
            // The 64 KB target heuristic is kept as a comment for reference:
            //   int bytesPerRow = width * GetBytesPerPixel();
            //   const int targetStripSize = 64 * 1024;
            //   int optimalRows = Math.Max(1, targetStripSize / bytesPerRow);
            //   return Math.Min(optimalRows, height);
            return height;
        }

        private void WriteImageData(BitMiracle.LibTiff.Classic.Tiff tiff, T[] data, int width, int height)
        {
            int typeSize = Marshal.SizeOf(typeof(T));
            int stride = width * typeSize;
            byte[] buffer = new byte[stride];

            Span<byte> sourceBytes = MemoryMarshal.AsBytes(data.AsSpan());

            for (int row = 0; row < height; row++)
            {
                var rowSlice = sourceBytes.Slice(row * stride, stride);
                rowSlice.CopyTo(buffer);

                // LibTiff.NET handles endianness internally via WriteScanline;
                // no manual byte-swapping is needed here.
                if (!tiff.WriteScanline(buffer, row, 0))
                {
                    throw new InvalidOperationException($"Failed to write scanline at row {row}");
                }
            }
        }

        #endregion

        #region Read Methods

        /// <summary>
        /// Read Ome-tiff file 
        /// </summary>
        public HyperstackData<T> ReadHyperstack(string filename, LoadingMode mode, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            var data = new HyperstackData<T>();

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                Debug.WriteLine($"[ReadHyperstack] File opened: {filename}");

                //Read metadata from the file header
                ReadOmeMetadata(tiff, data);
                Debug.WriteLine($"[ReadHyperstack] Metadata read.");

                // Multi-file OME-TIFF fallback:
                // If the OME-XML references external files via TiffData/UUID/FileName, we cannot
                // follow those references here.  Instead we clamp TotalFrames to the actual IFD
                // count in this file and continue with sequential reading.
                // The warning is stored in data.MultiFileWarning for the caller to surface.
                if (data.MultiFileWarning != null)
                {
                    int actualDirs = tiff.NumberOfDirectories();
                    string currentBaseName = Path.GetFileName(filename);
                    Debug.WriteLine($"[ReadHyperstack] Multi-file OME-TIFF detected. Referenced: {data.MultiFileWarning}. Clamping to {actualDirs} IFDs.");

                    // Update the warning to include the current file context
                    data.MultiFileWarning =
                        $"This is a multi-file OME-TIFF (referenced: {data.MultiFileWarning}). " +
                        $"Only '{currentBaseName}' was opened ({actualDirs} frame(s)). " +
                        "The other files were not loaded.";

                    // Clamp dimensions so TotalFrames == actualDirs
                    if (data.TotalFrames > actualDirs)
                    {
                        data.Channels = 1;
                        data.ZSlices = 1;
                        data.TimePoints = actualDirs;
                    }
                }

                // Calculate total data size
                long bytesPerPixel = System.Runtime.InteropServices.Marshal.SizeOf<T>();
                Debug.WriteLine($"[ReadHyperstack] Data value type: {typeof(T).Name}");

                long totalBytes = (long)data.TotalFrames * data.Width * data.Height * bytesPerPixel;
                long totalMB = totalBytes / 1024 / 1024;
                Debug.WriteLine($"[ReadHyperstack] Data: {data.Width} x {data.Height} x {data.TotalFrames}");
                Debug.WriteLine($"[ReadHyperstack] Data size: {(totalMB > 0 ? totalMB.ToString() + " MB" : "< 1 MB")}");

                // Check compression at frame 0 before resolving loading mode.
                // Virtual mode requires uncompressed data (NONE); compressed files must fall back to InMemory.
                FieldValue[]? compTag = tiff.GetField(TiffTag.COMPRESSION);
                bool isCompressed = compTag != null && compTag[0].ToInt() != (int)Compression.NONE;
                Debug.WriteLine($"[ReadHyperstack] Compression: {(isCompressed ? "yes (InMemory forced)" : "none")}");

                var resolvedMode = VirtualPolicy.Resolve(mode, totalBytes, frameCount: data.TotalFrames, canVirtual: !isCompressed);
                if (resolvedMode == LoadingMode.Virtual)
                {
                    Debug.WriteLine("[ReadHyperstack] Virtual loading enabled");
                    data.IsVirtualMode = true;

                    bool isTiled = tiff.GetField(TiffTag.TILEWIDTH) != null;
                    Debug.WriteLine($"[ReadHyperstack] Stored data structure: {(isTiled ? "Tile" : "Strip")}");

                    if (isTiled)
                    {
                        (int tileWidth, int tileLength, long[][] offsets, long[][] byteCounts) = ScanTileInfo(tiff, data.TotalFrames, progress, ct);
                        Debug.WriteLine($"[ReadHyperstack] offset array for tiled image was extracted. length = {offsets.Length}");
                        var vl = new VirtualTiledFrames<T>(filename, data.Width, data.Height, tileWidth, tileLength, offsets, byteCounts, isYFlipped: true);
                        data.ImageStack = vl;
                    }
                    else //Strip
                    {
                        var (offsets, byteCounts) = ScanStripInfo(tiff, data.TotalFrames, progress, ct);
                        Debug.WriteLine($"[ReadHyperstack] offset array for stripped image was extracted. length = {offsets.Length}");
                        data.ImageStack = new VirtualStrippedFrames<T>(filename, data.Width, data.Height, offsets, byteCounts, isYFlipped: true);
                    }

                    Debug.WriteLine($"[ReadHyperstack] VirtualFrameList was initialized.");
                }
                else
                {
                    Debug.WriteLine("[ReadHyperstack] InMemory loading enabled");
                    data.IsVirtualMode = false;

                    bool isTiledInMemory = tiff.GetField(TiffTag.TILEWIDTH) != null;
                    if (MaxParallelDegree > 1)
                    {
                        Debug.WriteLine($"[ReadHyperstack] Parallel loading (degree={MaxParallelDegree})");
                        ReadImageDataParallel(filename, data, MaxParallelDegree, isTiledInMemory, progress, ct);
                    }
                    else
                    {
                        ReadImageData(tiff, data, progress, ct);
                    }
                    Debug.WriteLine($"[ReadHyperstack] All frames were read.");
                }
            }

            return data;
        }

        /// <summary>
        /// Reads frames from an OME-TIFF file lazily (memory-efficient).
        /// </summary>
        public IEnumerable<T[]> ReadFramesLazy(string filename)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                int totalDirectories = tiff.NumberOfDirectories();
                var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();

                for (int directory = 0; directory < totalDirectories; directory++)
                {
                    tiff.SetDirectory((short)directory);
                    bool isTiled = tiff.GetField(TiffTag.TILEWIDTH) != null;
                    yield return isTiled
                        ? ReadSingleFrameTiled(tiff, width, height)
                        : ReadSingleFrameStripped(tiff, width, height);
                }
            }
        }

        /// <summary>
        /// Reads a single frame at the specified index.
        /// </summary>
        public T[] ReadSingleFrameAt(string filename, int frameIndex)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                if (frameIndex >= tiff.NumberOfDirectories())
                    throw new ArgumentOutOfRangeException(nameof(frameIndex), $"Out of bounds:  frameIndex= {frameIndex}");

                tiff.SetDirectory((short)frameIndex);
                var width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                var height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
                bool isTiled = tiff.GetField(TiffTag.TILEWIDTH) != null;
                return isTiled
                    ? ReadSingleFrameTiled(tiff, width, height)
                    : ReadSingleFrameStripped(tiff, width, height);
            }
        }

        /// <summary>
        /// Reads metadata only, without loading pixel data.
        /// </summary>
        public HyperstackMetadata ReadMetadata(string filename)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"No such file: {filename}");

            var data = new HyperstackMetadata();

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                ReadOmeMetadata(tiff, data);
            }

            return data;
        }

        private void ReadOmeMetadata(BitMiracle.LibTiff.Classic.Tiff tiff, HyperstackMetadata data)
        {
            var imageDescription = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
            if (imageDescription != null && imageDescription.Length > 0)
            {
                string omeXml = DecodeOmeXmlString(imageDescription[0]);
                ParseOmeXml(omeXml, data);
                data.OMEXml = omeXml;

                ReadCoordinateSystemAnnotation(omeXml, data);

                ReadMatrixDataMetadataAnnotation(omeXml, data);

                ReadLegacyCustomParameters(omeXml, data);
            }
            else
            {
                ReadBasicTiffInfo(tiff, data);
            }
        }

        /// <summary>
        /// Decodes the OME-XML string stored in the TIFF IMAGEDESCRIPTION tag.
        /// <para>
        /// Although the TIFF spec defines this tag as ASCII, OME-TIFF writers encode it as UTF-8.
        /// LibTiff.NET maps ASCII bytes 1:1 to Latin-1 chars, so µ (UTF-8: 0xC2 0xB5) becomes Âµ.
        /// </para>
        /// <para>
        /// Fix: re-encode the Latin-1 string back to bytes and decode those bytes as UTF-8.
        /// </para>
        /// </summary>
        private static string DecodeOmeXmlString(FieldValue fv)
        {
            // If LibTiff holds raw bytes internally, decode directly as UTF-8
            if (fv.Value is byte[] rawBytes)
                return Encoding.UTF8.GetString(rawBytes).TrimEnd('\0');

            // String path: re-encode as Latin-1 to recover the original UTF-8 byte sequence
            string raw = fv.ToString() ?? string.Empty;
            return Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(raw)).TrimEnd('\0');
        }

        private void ParseOmeXml(string omeXml, HyperstackMetadata data)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                if (doc == null)
                    throw new InvalidDataException("Invalid OME-XML format.");
                var ns = doc.Root?.GetDefaultNamespace() ?? null;
                if (ns == null)
                    throw new InvalidDataException("No namespace found in OME-XML.");

                // 1. Collect all Image elements (one per FOV/tile)
                var images = doc.Descendants(ns + "Image").ToList();

                if (images.Count == 0)
                    throw new InvalidDataException("No Image elements found in OME-XML.");

                // 2. Set FOV count
                data.FovCount = images.Count;
                data.GlobalOrigins = new GlobalPoint[data.FovCount];

                // 3. Read common properties (size, physical units) from the first Image element.
                //    All tiles within the same file are assumed to share the same pixel dimensions and units.
                var pixels = images[0].Descendants(ns + "Pixels").FirstOrDefault();
                if (pixels != null)
                {
                    data.Width = int.Parse(pixels.Attribute("SizeX")?.Value ?? "0");
                    data.Height = int.Parse(pixels.Attribute("SizeY")?.Value ?? "0");
                    data.Channels = int.Parse(pixels.Attribute("SizeC")?.Value ?? "1");
                    data.ZSlices = int.Parse(pixels.Attribute("SizeZ")?.Value ?? "1");
                    data.TimePoints = int.Parse(pixels.Attribute("SizeT")?.Value ?? "1");
                    data.DimensionOrder = pixels.Attribute("DimensionOrder")?.Value ?? "XYCZT";

                    // Physical pixel size
                    if (double.TryParse(pixels.Attribute("PhysicalSizeX")?.Value, out double psx))
                        data.PixelSizeX = psx;
                    if (double.TryParse(pixels.Attribute("PhysicalSizeY")?.Value, out double psy))
                        data.PixelSizeY = psy;
                    if (double.TryParse(pixels.Attribute("PhysicalSizeZ")?.Value, out double psz))
                        data.PixelSizeZ = psz;

                    // Units
                    data.UnitX = pixels.Attribute("PhysicalSizeXUnit")?.Value ?? "µm";
                    data.UnitY = pixels.Attribute("PhysicalSizeYUnit")?.Value ?? "µm";
                    data.UnitZ = pixels.Attribute("PhysicalSizeZUnit")?.Value ?? "µm";

                    // Detect multi-file OME-TIFF: any TiffData/UUID/@FileName that differs from
                    // the file being opened.  We cannot resolve the external file here (the
                    // filename is unknown at this layer), so we just collect all distinct external
                    // basenames for the caller to act on.
                    var externalFiles = pixels
                        .Descendants(ns + "TiffData")
                        .Select(td => td.Element(ns + "UUID")?.Attribute("FileName")?.Value)
                        .Where(fn => !string.IsNullOrEmpty(fn))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (externalFiles.Count > 0)
                        data.MultiFileWarning = string.Join(", ", externalFiles!);
                }

                // Compute average DeltaT interval from the first Image element
                if (pixels != null && data.TimePoints > 1)
                {
                    string targetZ = "0";
                    string targetC = "0";

                    // Get DeltaT of the first frame (T=0); FirstOrDefault avoids a full scan
                    var firstPlane = pixels.Descendants(ns + "Plane")
                        .FirstOrDefault(p =>
                            p.Attribute("TheT")?.Value == "0" &&
                            p.Attribute("TheZ")?.Value == targetZ &&
                            p.Attribute("TheC")?.Value == targetC);

                    // Get DeltaT of the last frame (T=SizeT-1)
                    string lastTIndex = (data.TimePoints - 1).ToString();
                    var lastPlane = pixels.Descendants(ns + "Plane")
                        .FirstOrDefault(p =>
                            p.Attribute("TheT")?.Value == lastTIndex &&
                            p.Attribute("TheZ")?.Value == targetZ &&
                            p.Attribute("TheC")?.Value == targetC);

                    // Only compute if both endpoints were found
                    if (firstPlane != null && lastPlane != null)
                    {
                        // Parse timestamps (TryParse returns 0 on failure; null-checked above)
                        double.TryParse(firstPlane.Attribute("DeltaT")?.Value, out double tStart);
                        double.TryParse(lastPlane.Attribute("DeltaT")?.Value, out double tEnd);

                        // Average interval = (end - start) / (TimePoints - 1)
                        data.TimeStep = (tEnd - tStart) / (data.TimePoints - 1);
                        data.StartTime = tStart;
                        data.UnitTime = firstPlane.Attribute("DeltaTUnit")?.Value ?? "s";
                    }
                }
                else
                {
                    // No time interval when there is only one time point
                    data.TimeStep = 0;
                }

                var fovOrigins = new List<GlobalPoint>();

                // Half-extent offsets to convert center coordinates back to origin (PixelSizeX = (Max-Min)/(Count-1))
                double halfWidth = (data.Width - 1) * data.PixelSizeX * 0.5;
                double halfHeight = (data.Height - 1) * data.PixelSizeY * 0.5;

                foreach (var img in images)
                {
                    var thePixels = img.Descendants(ns + "Pixels").FirstOrDefault();
                    if (thePixels == null)
                    {
                        fovOrigins.Add(new GlobalPoint(0, 0, 0));
                        continue;
                    }

                    var firstPlane = thePixels.Descendants(ns + "Plane").FirstOrDefault();

                    // Origin coordinates
                    double originX = 0;
                    double originY = 0;
                    double originZ = 0;

                    if (firstPlane != null)
                    {
                        // PositionX/Y are center coordinates; convert to left-edge origin
                        if (double.TryParse(firstPlane.Attribute("PositionX")?.Value, out double px))
                        {
                            // Center -> Origin (Left)
                            originX = px - halfWidth;
                        }

                        if (double.TryParse(firstPlane.Attribute("PositionY")?.Value, out double py))
                        {
                            // Y-flip (if needed) is applied after loading; treat top-left as origin here
                            originY = py - halfHeight;
                        }

                        // Z coordinate: position of the first slice
                        if (double.TryParse(firstPlane.Attribute("PositionZ")?.Value, out double pz))
                        {
                            originZ = pz; // equivalent to StartZ
                        }
                    }

                    fovOrigins.Add(new GlobalPoint(originX, originY, originZ)); // originZ unused for 2-D tile layouts
                }

                // Store parsed results
                data.GlobalOrigins = fovOrigins.ToArray();
                data.StartZ = fovOrigins[0].Z; // Z is shared across all FOVs; store from the first one
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OME-XML parse error: {ex.Message}");
                // Do not fall back; propagate the error to the caller
                throw new InvalidDataException($"Unable to parse OME-XML: {ex.Message}", ex);
            }
        }

        private void ReadCoordinateSystemAnnotation(string omeXml, HyperstackMetadata data)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                var nsSA = XNamespace.Get("http://www.openmicroscopy.org/Schemas/SA/2016-06");

                var coordAnnotation = doc.Descendants(nsSA + "XMLAnnotation")
                    .FirstOrDefault(a => a.Attribute("Namespace")?.Value?.Contains("coordinate-system") == true);

                if (coordAnnotation != null)
                {
                    var value = coordAnnotation.Element(nsSA + "Value");
                    var coordSystem = value?.Element("CoordinateSystem");
                    var position = coordSystem?.Element("IndexZeroPosition");
                    var unit = coordSystem?.Element("Unit");
                    var tileLayout = coordSystem?.Element("TileLayout");

                    if (position != null)
                    {
                        if (double.TryParse(position.Attribute("X")?.Value, out double ox))
                            data.StartX = ox;
                        if (double.TryParse(position.Attribute("Y")?.Value, out double oy))
                            data.StartY = oy;
                        if (double.TryParse(position.Attribute("Z")?.Value, out double oz))
                            data.StartZ = oz;
                    }
                    if (unit != null)
                    {
                        // Read unit information
                        data.UnitX = unit.Attribute("X")?.Value ?? data.UnitX;
                        data.UnitY = unit.Attribute("Y")?.Value ?? data.UnitY;
                        data.UnitZ = unit.Attribute("Z")?.Value ?? data.UnitZ;
                    }
                    if (tileLayout != null)
                    {
                        if (int.TryParse(tileLayout.Attribute("TilesX")?.Value, out int tilesX) &&
                            int.TryParse(tileLayout.Attribute("TilesY")?.Value, out int tilesY))
                        {
                            data.TileLayout = (tilesX, tilesY);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CoordinateSystem annotation read error: {ex.Message}");
                // Non-fatal: continue without coordinate system annotation
            }
        }

        private void ReadBasicTiffInfo(BitMiracle.LibTiff.Classic.Tiff tiff, HyperstackMetadata data)
        {
            var width = tiff.GetField(TiffTag.IMAGEWIDTH);
            var height = tiff.GetField(TiffTag.IMAGELENGTH);

            data.Width = width?[0].ToInt() ?? 0;
            data.Height = height?[0].ToInt() ?? 0;
            data.Channels = 1;
            data.ZSlices = 1;
            data.TimePoints = tiff.NumberOfDirectories();
        }

        private void ReadMatrixDataMetadataAnnotation(string omeXml, HyperstackMetadata data)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                var nsSA = XNamespace.Get("http://www.openmicroscopy.org/Schemas/SA/2016-06");

                // Search for a MapAnnotation whose Namespace contains "matrix-data/metadata"
                var mapAnnotation = doc.Descendants(nsSA + "MapAnnotation")
                    .FirstOrDefault(a => a.Attribute("Namespace")?.Value?.Contains("matrix-data/metadata") == true);

                if (mapAnnotation != null)
                {
                    var valueElement = mapAnnotation.Element(nsSA + "Value");

                    if (valueElement != null)
                    {
                        if (data.MatrixDataMetadata == null)
                        {
                            data.MatrixDataMetadata = new Dictionary<string, string>();
                        }
                        else
                        {
                            // Clear existing entries (merge behavior can be adjusted per requirements)
                            data.MatrixDataMetadata.Clear();
                        }

                        // Collect all <M K="Key">Value</M> elements into the dictionary
                        foreach (var mElement in valueElement.Elements(nsSA + "M"))
                        {
                            var key = mElement.Attribute("K")?.Value;

                            // XElement.Value automatically strips CDATA wrappers, returning the raw string content.
                            var value = mElement.Value;

                            if (!string.IsNullOrEmpty(key))
                            {
                                data.MatrixDataMetadata[key] = value ?? string.Empty;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MatrixDataMetadata annotation read error: {ex.Message}");
                // Non-fatal: continue without metadata annotation
            }
        }

        private void ReadLegacyCustomParameters(string omeXml, HyperstackMetadata data)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                if (doc.Root == null) return;

                // Look for a namespace-less "CustomParameters" element
                XElement? customParams = doc.Root.Element("CustomParameters");
                string? rawValues = customParams?.Value;

                if (!string.IsNullOrEmpty(rawValues))
                {
                    // Use the legacy Key=... parser to recover a Dictionary<int, string>
                    var legacyDict = ParseCustomParametersString(rawValues);

                    if (legacyDict != null)
                    {
                        if (data.MatrixDataMetadata == null)
                            data.MatrixDataMetadata = new Dictionary<string, string>();

                        // Merge into the existing dictionary (skip duplicate keys)
                        foreach (var kvp in legacyDict)
                        {
                            string key = $"IntKey_{kvp.Key}"; // Prefix to indicate legacy origin
                            if (!data.MatrixDataMetadata.ContainsKey(key))
                            {
                                data.MatrixDataMetadata[key] = kvp.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CustomParameters read error: {ex.Message}");
            }
        }

        private static Dictionary<int, string> ParseCustomParametersString(string values)
        {
            if (values == null) 
                return null;

            //CustomParameters stores Dictionary<int, string> entries as sequential "Key=XXX" / value pairs
            var result = new Dictionary<int, string>();

            try
            {
                // Split into lines
                string[] lines = values.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                int? currentKey = null;
                List<string> currentValueLines = new List<string>();

                foreach (var line in lines)
                {
                    // Check whether the line starts with "Key="
                    if (line.StartsWith("Key="))
                    {
                        // Save the accumulated value for the previous key
                        if (currentKey.HasValue)
                        {
                            result[currentKey.Value] = string.Join(Environment.NewLine, currentValueLines).TrimEnd();
                        }

                        // Parse the new key, e.g. "Key=8119" → 8119
                        if (int.TryParse(line.Substring(4), out int key))
                        {
                            currentKey = key;
                            currentValueLines.Clear();
                        }
                    }
                    else if (currentKey.HasValue)
                    {
                        // All non-Key lines accumulate as part of the current value
                        currentValueLines.Add(line);
                    }
                }

                // Save the last key-value pair
                if (currentKey.HasValue)
                {
                    result[currentKey.Value] = string.Join(Environment.NewLine, currentValueLines).TrimEnd();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Parse error in CustomParameters: " + ex.Message, ex);
            }

            return result;
        }



        private void ReadImageData(BitMiracle.LibTiff.Classic.Tiff tiff, HyperstackData<T> data, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            int totalFrames  = data.TotalFrames;
            int actualFrames = Math.Min(totalFrames, tiff.NumberOfDirectories());
            data.ImageStack  = new List<T[]>(actualFrames);

            // Determine layout once from the first directory (same for all frames).
            // SetDirectory(0) is used only here; subsequent frames advance via ReadDirectory()
            // so that each IFD is visited exactly once — O(N) instead of O(N²).
            tiff.SetDirectory(0);
            bool isTiled = tiff.GetField(TiffTag.TILEWIDTH) != null;

            progress?.Report(-actualFrames);
            for (int directory = 0; directory < actualFrames; directory++)
            {
                ct.ThrowIfCancellationRequested();
                var frameData = isTiled
                    ? ReadSingleFrameTiled(tiff, data.Width, data.Height)
                    : ReadSingleFrameStripped(tiff, data.Width, data.Height);
                data.ImageStack.Add(frameData);
                progress?.Report(directory);

                if (directory < actualFrames - 1 && !tiff.ReadDirectory())
                    throw new InvalidDataException($"Could not advance to directory {directory + 1}");
            }
            progress?.Report(actualFrames);
        }

        /// <summary>
        /// Parallel variant of <see cref="ReadImageData"/>.
        /// Opens one LibTiff handle per thread; each thread advances sequentially within its range.
        /// Effective for compressed (e.g. LZW) files where decompression is CPU-bound.
        /// </summary>
        private void ReadImageDataParallel(string filePath, HyperstackData<T> data, int parallelDegree, bool isTiled, IProgress<int>? progress, CancellationToken ct = default)
        {
            int actualFrames = data.TotalFrames;
            var imageStack   = new T[actualFrames][];

            int threads = Math.Min(parallelDegree, actualFrames);
            int chunk   = (actualFrames + threads - 1) / threads;

            progress?.Report(-actualFrames);
            int reported = 0;

            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct }, t =>
            {
                int start = t * chunk;
                int end   = Math.Min(start + chunk, actualFrames);
                if (start >= actualFrames) return;

                using var localTiff = BitMiracle.LibTiff.Classic.Tiff.Open(filePath, "r");

                // Advance to this thread's starting IFD: O(start) hops, sequential ReadDirectory
                localTiff.SetDirectory(0);
                for (int i = 0; i < start; i++) localTiff.ReadDirectory();

                for (int f = start; f < end; f++)
                {
                    ct.ThrowIfCancellationRequested();
                    imageStack[f] = isTiled
                        ? ReadSingleFrameTiled(localTiff, data.Width, data.Height)
                        : ReadSingleFrameStripped(localTiff, data.Width, data.Height);

                    progress?.Report(Interlocked.Increment(ref reported) - 1);

                    if (f < end - 1 && !localTiff.ReadDirectory())
                        throw new InvalidDataException($"Thread {t}: could not advance to frame {f + 1}");
                }
            });

            data.ImageStack = imageStack.ToList();
            progress?.Report(actualFrames);
        }

        /// <summary>
        /// Reads one frame from a strip-organized TIFF using <c>ReadEncodedStrip</c>.
        /// One API call per strip instead of one per row, reducing overhead by ×(rows/strip).
        /// </summary>
        private T[] ReadSingleFrameStripped(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
        {
            var imageData    = new T[width * height];
            int typeSize     = Marshal.SizeOf(typeof(T));

            Span<byte> dest  = MemoryMarshal.AsBytes(imageData.AsSpan());

            int rowsPerStrip = tiff.GetField(TiffTag.ROWSPERSTRIP)?[0].ToInt() ?? height;
            int numStrips    = tiff.NumberOfStrips();
            int maxStripBytes = tiff.StripSize();
            var stripBuf     = new byte[maxStripBytes];

            for (int strip = 0; strip < numStrips; strip++)
            {
                int bytesRead = tiff.ReadEncodedStrip(strip, stripBuf, 0, maxStripBytes);
                if (bytesRead < 0)
                    throw new InvalidOperationException($"ReadEncodedStrip failed: strip={strip}");

                int startRow    = strip * rowsPerStrip;
                int actualRows  = Math.Min(rowsPerStrip, height - startRow);
                int bytesToCopy = actualRows * width * typeSize;
                stripBuf.AsSpan(0, bytesToCopy)
                        .CopyTo(dest.Slice(startRow * width * typeSize));
            }
            return imageData;
        }

        /// <summary>
        /// Reads one frame from a tile-organized TIFF using <c>ReadEncodedTile</c>.
        /// One API call per tile instead of one per row, reducing overhead significantly.
        /// </summary>
        private T[] ReadSingleFrameTiled(BitMiracle.LibTiff.Classic.Tiff tiff, int width, int height)
        {
            var imageData  = new T[width * height];
            int typeSize   = Marshal.SizeOf(typeof(T));

            Span<byte> dest = MemoryMarshal.AsBytes(imageData.AsSpan());

            int tileWidth  = tiff.GetField(TiffTag.TILEWIDTH)[0].ToInt();
            int tileHeight = tiff.GetField(TiffTag.TILELENGTH)[0].ToInt();
            int tilesAcross = (width  + tileWidth  - 1) / tileWidth;
            int tilesDown   = (height + tileHeight - 1) / tileHeight;
            int maxTileBytes = tiff.TileSize();
            var tileBuf    = new byte[maxTileBytes];

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
                    int actualTileWidth  = Math.Min(tileWidth,  width  - startX);
                    int actualTileHeight = Math.Min(tileHeight, height - startY);

                    for (int row = 0; row < actualTileHeight; row++)
                    {
                        int srcOffset = row * tileWidth * typeSize;
                        int dstOffset = ((startY + row) * width + startX) * typeSize;
                        tileBuf.AsSpan(srcOffset, actualTileWidth * typeSize)
                               .CopyTo(dest.Slice(dstOffset));
                    }
                }
            }
            return imageData;
        }
                

        #region Logic for VirtualList (OmeTiffLoadMode == Virtual)


        internal (long[][] offsets, long[][] byteCounts) ScanStripInfo(BitMiracle.LibTiff.Classic.Tiff tiff, int totalFrames, IProgress<int>? progress, CancellationToken ct = default)
        {
            long[][] offsets = new long[totalFrames][];
            long[][] byteCounts = new long[totalFrames][];

            progress?.Report(-totalFrames); // Signal total frame count to the UI

            // Always start from directory 0 before entering the loop
            tiff.SetDirectory(0);

            for (short i = 0; i < totalFrames; i++)
            {
                ct.ThrowIfCancellationRequested();
                // 1. Verify that the frame is uncompressed (Virtual mode requires raw access)
                FieldValue[] compTag = tiff.GetField(TiffTag.COMPRESSION);
                if (compTag != null && compTag[0].ToInt() != (int)Compression.NONE)
                {
                    throw new NotSupportedException($"Virtual mode requires uncompressed TIFF. Frame {i} is compressed.");
                }

                // 2. Read strip offsets
                FieldValue[] offsetTag = tiff.GetField(TiffTag.STRIPOFFSETS);
                if (offsetTag == null || offsetTag.Length == 0)
                    throw new InvalidDataException($"Missing StripOffsets tag at frame {i}.");

                // 3. Read strip byte counts
                FieldValue[] byteCountTag = tiff.GetField(TiffTag.STRIPBYTECOUNTS);
                if (byteCountTag == null || byteCountTag.Length == 0)
                    throw new InvalidDataException($"Missing StripByteCounts tag at frame {i}.");

                offsets[i] = ExtractLongArray(offsetTag[0]);
                byteCounts[i] = ExtractLongArray(byteCountTag[0]);
                progress?.Report(i);

                // Advance sequentially — never use SetDirectory(i) here!
                if (i < totalFrames - 1)
                {
                    if (!tiff.ReadDirectory())
                        throw new InvalidDataException($"Could not read directory for frame {i + 1}");
                }
            }

            return (offsets, byteCounts);
        }

        /// <summary>
        /// Opens <paramref name="filePath"/> read-only, scans all IFDs and returns strip offset/bytecount arrays.
        /// Convenience overload that avoids exposing LibTiff types to callers outside this assembly.
        /// </summary>
        internal (long[][] offsets, long[][] byteCounts) ScanStripInfoFromFile(string filePath, int totalFrames, IProgress<int>? progress, CancellationToken ct = default)
        {
            using var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filePath, "r");
            if (tiff == null)
                throw new IOException($"Cannot open OME-TIFF for strip scanning: {filePath}");
            return ScanStripInfo(tiff, totalFrames, progress, ct);
        }

        /// <summary>
        /// Creates a pre-allocated OME-TIFF vessel by writing only the BigTIFF header and IFDs,
        /// then calling <see cref="FileStream.SetLength"/> for pixel data space.
        /// <para>
        /// This is O(frames) for IFD writing and effectively O(1) for pixel data allocation on NTFS,
        /// making it dramatically faster than the skeleton-write approach for large files.
        /// </para>
        /// </summary>
        /// <returns>
        /// Per-frame strip offset and byte-count arrays pointing into the pre-allocated file regions.
        /// Pass these directly to <see cref="WritableVirtualStrippedFrames{T}"/>.
        /// </returns>
        internal (long[][] offsets, long[][] byteCounts) BuildVesselFast(string filePath, HyperstackMetadata spec)
        {
            string omeXml = CreateOmeXml(spec);
            int rowsPerStrip = CalculateOptimalRowsPerStrip(spec.Width, spec.Height);
            ushort sampleFmtCode = (ushort)GetSampleFormat(); // 1=UINT, 2=INT, 3=IEEEFP (matches TIFF spec)

            return BigTiffVesselWriter.Build(
                filePath,
                spec.Width,
                spec.Height,
                spec.TotalFrames,
                GetBytesPerPixel(),
                GetBitsPerSample(),
                sampleFmtCode,
                omeXml,
                rowsPerStrip);
        }

        private (int tileWidth, int tileLength, long[][] offsets, long[][] byteCounts) ScanTileInfo(BitMiracle.LibTiff.Classic.Tiff tiff, int totalFrames, IProgress<int>? progress, CancellationToken ct = default)
        {
            long[][] offsets = new long[totalFrames][];
            long[][] byteCounts = new long[totalFrames][];

            progress?.Report(-totalFrames);

            tiff.SetDirectory(0);

            // Read tile dimensions (shared across all frames) from directory 0
            FieldValue[] twTag = tiff.GetField(TiffTag.TILEWIDTH);
            FieldValue[] tlTag = tiff.GetField(TiffTag.TILELENGTH);

            if (twTag == null || tlTag == null)
                throw new InvalidDataException("TileWidth or TileLength tag is missing in a tiled TIFF.");

            int tileWidth = twTag[0].ToInt();
            int tileLength = tlTag[0].ToInt();

            for (short i = 0; i < totalFrames; i++)
            {
                ct.ThrowIfCancellationRequested();
                FieldValue[] compTag = tiff.GetField(TiffTag.COMPRESSION);
                if (compTag != null && compTag[0].ToInt() != (int)Compression.NONE)
                {
                    throw new NotSupportedException($"Virtual mode requires uncompressed TIFF. Frame {i} is compressed.");
                }

                // Tiled TIFFs use TILEOFFSETS / TILEBYTECOUNTS instead of STRIPOFFSETS / STRIPBYTECOUNTS
                FieldValue[] offsetTag = tiff.GetField(TiffTag.TILEOFFSETS);
                if (offsetTag == null || offsetTag.Length == 0)
                    throw new InvalidDataException($"Missing TileOffsets tag at frame {i}.");

                FieldValue[] byteCountTag = tiff.GetField(TiffTag.TILEBYTECOUNTS);
                if (byteCountTag == null || byteCountTag.Length == 0)
                    throw new InvalidDataException($"Missing TileByteCounts tag at frame {i}.");

                // Each frame contains multiple tiles; array length equals the total tile count
                //offsets[i] = offsetTag[0].ToIntArray().Select(v => (long)v).ToArray();
                //byteCounts[i] = byteCountTag[0].ToIntArray().Select(v => (long)v).ToArray();

                offsets[i] = ExtractLongArray(offsetTag[0]);
                byteCounts[i] = ExtractLongArray(byteCountTag[0]);

                progress?.Report(i);

                if (i < totalFrames - 1)
                {
                    if (!tiff.ReadDirectory())
                        throw new InvalidDataException($"Could not read directory for frame {i + 1}");
                }
            }

            return (tileWidth, tileLength, offsets, byteCounts);
        }

        private long[] ExtractLongArray(BitMiracle.LibTiff.Classic.FieldValue fieldValue)
        {
            //if (fieldValue == null) return Array.Empty<long>();

            // 1. For BigTIFF (>16 GB), LibTiff may hold long[] internally; try direct cast first
            if (fieldValue.Value is long[] longArray)
            {
                return longArray;
            }

            // 2. ulong[] case
            if (fieldValue.Value is ulong[] ulongArray)
            {
                return ulongArray.Select(v => (long)v).ToArray();
            }

            // 3. Standard TIFF (or 32-bit downcast) case
            int[] intArray = fieldValue.ToIntArray();
            if (intArray != null)
            {
                // Cast int to uint before widening to long to correctly recover
                // 2 GB – 4 GB offsets that appeared negative as signed int
                return intArray.Select(v => (long)(uint)v).ToArray();
            }

            return Array.Empty<long>();
        }

        #endregion
        #endregion


        #region Type Utilities

        private int GetBitsPerSample()
        {
            if (typeof(T) == typeof(short) || typeof(T) == typeof(ushort))
                return 16;
            if (typeof(T) == typeof(byte) || typeof(T) == typeof(sbyte))
                return 8;
            if (typeof(T) == typeof(int) || typeof(T) == typeof(uint))
                return 32;
            if (typeof(T) == typeof(float))
                return 32;
            if (typeof(T) == typeof(double))
                return 64;

            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }

        private SampleFormat GetSampleFormat()
        {
            if (typeof(T) == typeof(short) || typeof(T) == typeof(sbyte) || typeof(T) == typeof(int))
                return SampleFormat.INT;
            if (typeof(T) == typeof(ushort) || typeof(T) == typeof(byte) || typeof(T) == typeof(uint))
                return SampleFormat.UINT;
            if (typeof(T) == typeof(float) || typeof(T) == typeof(double))
                return SampleFormat.IEEEFP;

            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }

        private int GetBytesPerPixel()
        {
            return System.Runtime.InteropServices.Marshal.SizeOf<T>();
        }

        private string GetOmePixelType()
        {
            if (typeof(T) == typeof(short)) return "int16";
            if (typeof(T) == typeof(ushort)) return "uint16";
            if (typeof(T) == typeof(byte)) return "uint8";
            if (typeof(T) == typeof(sbyte)) return "int8";
            if (typeof(T) == typeof(int)) return "int32";
            if (typeof(T) == typeof(uint)) return "uint32";
            if (typeof(T) == typeof(float)) return "float";
            if (typeof(T) == typeof(double)) return "double";

            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }

        #endregion

        #region OME-XML Generation

        private string CreateOmeXml(HyperstackMetadata data)
        {
            XNamespace ns = "http://www.openmicroscopy.org/Schemas/OME/2016-06";
            XNamespace sa = "http://www.openmicroscopy.org/Schemas/SA/2016-06";
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

            // Generate a file-unique UUID to match Fiji's output format
            string uuid = "urn:uuid:" + Guid.NewGuid().ToString();

            var ome = new XElement(ns + "OME",
                  new XAttribute("xmlns", ns.NamespaceName),
                  new XAttribute(XNamespace.Xmlns + "xsi", xsi.NamespaceName),
                  new XAttribute(xsi + "schemaLocation", $"{ns.NamespaceName} {ns.NamespaceName}/ome.xsd"),
                  new XAttribute("UUID", uuid),
                  new XAttribute("Creator", "OmeTiffHandler.cs C# (MxPlot)")
              );

            // Total planes per FOV (Z * C * T)
            int planesPerFov = data.ZSlices * data.Channels * data.TimePoints;

            // Generate one Image node per FOV
            for (int fov = 0; fov < data.FovCount; fov++)
            {
                // IFD start index for this FOV (offset by the number of planes in previous FOVs)
                int startIfd = fov * planesPerFov;
                (int tileX, int tileY) = data.GetTileIndices(fov);

                var imageNode = new XElement(ns + "Image",
                    new XAttribute("ID", $"Image:{fov}"), // Image:0, Image:1 ...
                    new XAttribute("Name", data.FovCount > 1 ? $"FOV:{fov} [{tileX},{tileY}]" : "Single FOV"),
                    new XElement(ns + "Pixels",
                        new XAttribute("ID", $"Pixels:{fov}"), // Pixels:0, Pixels:1 ...
                        new XAttribute("SizeX", data.Width),
                        new XAttribute("SizeY", data.Height),
                        new XAttribute("SizeZ", data.ZSlices),
                        new XAttribute("SizeC", data.Channels),
                        new XAttribute("SizeT", data.TimePoints),
                        new XAttribute("DimensionOrder", "XYCZT"),
                        new XAttribute("Type", GetOmePixelType()),
                        new XAttribute("PhysicalSizeX", data.PixelSizeX),
                        new XAttribute("PhysicalSizeY", data.PixelSizeY),
                        new XAttribute("PhysicalSizeZ", data.PixelSizeZ),

                        // Channel definitions (shared across all FOVs)
                        CreateChannels(data.Channels, ns),

                        CreateTiffData(data.Channels, data.ZSlices, data.TimePoints, ns, startIfd),

                        // Plane definitions (pass the FOV index to resolve its GlobalPoint)
                        CreatePlanes(data, ns, fov)
                    )
                );

                // Annotation reference for each Image node
                imageNode.Add(new XElement(ns + "AnnotationRef", new XAttribute("ID", "Annotation:CoordinateSystem:0")));


                if (data.MatrixDataMetadata != null && data.MatrixDataMetadata.Count > 0)
                {
                    imageNode.Add(new XElement(ns + "AnnotationRef", new XAttribute("ID", "Annotation:Map:Custom")));
                }

                ome.Add(imageNode);
            }

            // Build StructuredAnnotations (coordinate system + custom metadata)
            var structuredAnnotations = new XElement(sa + "StructuredAnnotations", 
                CreateCoordinateSystemAnnotation(data, sa),
                CreateMatrixDataMetadataAnnotation(data, sa)
                );

            ome.Add(structuredAnnotations);

            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + ome.ToString();
        }

        private IEnumerable<XElement> CreateChannels(int channelCount, XNamespace ns)
        {
            for (int i = 0; i < channelCount; i++)
            {
                yield return new XElement(ns + "Channel",
                    new XAttribute("ID", $"Channel:0:{i}"),
                    new XAttribute("SamplesPerPixel", 1), //Grayscale
                    new XElement(ns + "LightPath")
                );
            }
        }

        private IEnumerable<XElement> CreateTiffData(int c, int z, int t, XNamespace ns, int ifd)
        {
            // Total frame count
            //int totalFrames = c * z * t;

            //int ifd = 0;

            // Map frames in XYCZT order
            for (int tIndex = 0; tIndex < t; tIndex++)
            {
                for (int zIndex = 0; zIndex < z; zIndex++)
                {
                    for (int cIndex = 0; cIndex < c; cIndex++)
                    {
                        yield return new XElement(ns + "TiffData",
                            new XAttribute("IFD", ifd),
                            new XAttribute("FirstC", cIndex),
                            new XAttribute("FirstZ", zIndex),
                            new XAttribute("FirstT", tIndex),
                            new XAttribute("PlaneCount", 1)
                        );
                        ifd++;
                    }
                }
            }

        }

        private IEnumerable<XElement> CreatePlanes(HyperstackMetadata data, XNamespace ns, int fovIndex = 0)
        {
            int t = data.TimePoints;
            int c = data.Channels;
            int z = data.ZSlices;
            // GlobalOrigin is the origin (top-left / bottom-left); convert to center for OME-XML PositionX/Y
            var gorigin = (data.GlobalOrigins != null && fovIndex < data.GlobalOrigins.Length) ?
                data.GlobalOrigins[fovIndex] : new GlobalPoint(data.StartX, data.StartY, data.StartZ);
            // posX/Y are center coordinates
            double posX = gorigin.X + (data.Width - 1) * data.PixelSizeX * 0.5;
            double posY = gorigin.Y + (data.Height - 1) * data.PixelSizeY * 0.5;

            for (int tIndex = 0; tIndex < t; tIndex++)
            {
                double time = data.StartTime + tIndex * data.TimeStep;
                for (int zIndex = 0; zIndex < z; zIndex++)
                {
                    //double posZ = data.StartZ + data.PixelSizeZ * zIndex;
                    double posZ = gorigin.Z + data.PixelSizeZ * zIndex; // gorigin.Z == data.StartZ in the current MatrixData model
                    for (int cIndex = 0; cIndex < c; cIndex++)
                    {
                        yield return new XElement(ns + "Plane",
                            new XAttribute("TheC", cIndex),
                            new XAttribute("TheZ", zIndex),
                            new XAttribute("TheT", tIndex),
                            new XAttribute("PositionX", posX),
                            new XAttribute("PositionY", posY),
                            new XAttribute("PositionZ", posZ),
                            new XAttribute("DeltaT", time), // DeltaT = elapsed time from the first frame
                            new XAttribute("DeltaTUnit", data.UnitTime),
                            new XAttribute("ExposureTime", 1)
                        );
                    }
                }
            }
        }

        /// <summary>
        /// Creates a custom coordinate-system annotation and adds it to the OME-XML.
        /// </summary>
        private XElement CreateCoordinateSystemAnnotation(HyperstackMetadata data, XNamespace sa)
        {
            return new XElement(sa + "XMLAnnotation",
                new XAttribute("ID", "Annotation:CoordinateSystem:0"),
                new XAttribute("Namespace", "mxplot/matrix-data/coordinate-system/v1"),
                new XElement(sa + "Value",
                    new XElement("CoordinateSystem",
                        new XElement("IndexZeroPosition",
                            new XAttribute("X", data.StartX),
                            new XAttribute("Y", data.StartY),
                            new XAttribute("Z", data.StartZ)
                        ),
                        new XElement("Unit",
                            new XAttribute("X", data.UnitX),
                            new XAttribute("Y", data.UnitY),
                            new XAttribute("Z", data.UnitZ)
                        ),
                        new XElement("TileLayout",
                            new XAttribute("TilesX", data.TileLayout.X),
                            new XAttribute("TilesY", data.TileLayout.Y)
                         )

                    )
                )
            );
        }
        /// <summary>
        /// Converts <c>MatrixData.Metadata</c> (IDictionary&lt;string, string&gt;) to an OME-XML MapAnnotation.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="sa"></param>
        /// <returns></returns>
        private XElement? CreateMatrixDataMetadataAnnotation(HyperstackMetadata data, XNamespace sa)
        {
            // Return null if there is no metadata (XElement silently ignores null children)
            if (data.MatrixDataMetadata == null || data.MatrixDataMetadata.Count == 0)
            {
                return null;
            }

            var valueElement = new XElement(sa + "Value");
            var metadata = data.MatrixDataMetadata;
            foreach (var kvp in metadata)
            {
                if (string.Equals(kvp.Key, OmeTiffHandler.OmeXmlKey, StringComparison.OrdinalIgnoreCase))
                {
                    // Avoid embedding OME-XML within itself
                    continue;
                }

                // Escape the CDATA end sequence to safely embed arbitrary strings
                string safeValue = kvp.Value?.Replace("]]>", "]]]]><![CDATA[>") ?? "";

                valueElement.Add(
                    new XElement(sa + "M",
                        new XAttribute("K", kvp.Key.ToString()),
                        new XCData(safeValue)
                    )
                );
            }

            // Return null if all entries were excluded (e.g., OME_XML key)
            if (!valueElement.HasElements)
            {
                return null;
            }

            return new XElement(sa + "MapAnnotation",
                new XAttribute("ID", "Annotation:Map:Custom"),
                new XAttribute("Namespace", "mxplot/matrix-data/metadata/v1"),
                valueElement
            );
        }

        #endregion
    }

    #region Data class
    public class HyperstackMetadata
    {
        /// <summary>
        /// Number of pixels in the X direction.
        /// </summary>
        public int Width { get; set; }
        /// <summary>
        /// Number of pixels in the Y direction.
        /// </summary>
        public int Height { get; set; }
        public int Channels { get; set; }
        public int ZSlices { get; set; }
        public int TimePoints { get; set; }
        public double StartTime { get; set; } = 0.0;
        /// <summary>
        /// Average time interval between time points (units depend on UnitTime).
        /// </summary>
        public double TimeStep { get; set; } = 1;

        public double PixelSizeX { get; set; } = 1.0;
        public double PixelSizeY { get; set; } = 1.0;
        public double PixelSizeZ { get; set; } = 1.0;
        public string UnitX { get; set; } = "µm";
        public string UnitY { get; set; } = "µm";
        public string UnitZ { get; set; } = "µm";
        public string UnitTime { get; set; } = "s";

        // Tiling settings
        public int FovCount { get; set; } = 1;

        /// <summary>
        /// 2-D tile layout (number of tiles in X, number of tiles in Y).
        /// </summary>
        public (int X, int Y) TileLayout { get; set; } = (1, 1);
        /// <summary>
        /// Origin coordinates of each tile (FOV) in world (stage) coordinates.
        /// </summary>
        public GlobalPoint[]? GlobalOrigins { get; set; } = null;

        /// <summary>
        /// Frame origin X coordinate (= X of T[0], relative; bottom-left in MatrixData convention).
        /// </summary>
        public double StartX { get; set; } = 0.0;
        /// <summary>
        /// Frame origin Y coordinate (= Y of T[0], relative; bottom-left in MatrixData convention).
        /// </summary>
        public double StartY { get; set; } = 0.0;
        /// <summary>
        /// Starting Z coordinate of the Z stack (absolute position at zIndex = 0).
        /// </summary>
        public double StartZ { get; set; } = 0.0;

        /// <summary>
        /// Default XYCZT
        /// </summary>
        public string DimensionOrder { get; set; } = "XYCZT";
        public string? PixelType { get; set; }

        /// <summary>
        /// Stores MatrixData.Metadata entries as key-value pairs.
        /// </summary>
        public IDictionary<string, string>? MatrixDataMetadata { get; set; }

        public string? OMEXml { get; set; }

        /// <summary>
        /// Total frame count.
        /// </summary>
        public int TotalFrames => Channels * ZSlices * TimePoints * FovCount;

        public bool IsYFlipped { get; set; } = false;

        public bool IsVirtualMode { get; set; } = false;

        /// <summary>
        /// Set when the OME-XML contains TiffData entries that reference external files
        /// (multi-file OME-TIFF). The external references are ignored and only the IFDs
        /// present in this file are read. This property carries a human-readable warning message.
        /// </summary>
        public string? MultiFileWarning { get; set; }

        public (int X, int Y) GetTileIndices(int fovIndex)
        {
            int tilesX = TileLayout.X;
            int xIndex = fovIndex % tilesX;
            int yIndex = fovIndex / tilesX;
            return (xIndex, yIndex);
        }

        /// <summary>Default constructor; all properties retain their declared default values.</summary>
        public HyperstackMetadata() { }

        /// <summary>
        /// Initializes a <see cref="HyperstackMetadata"/> from a <see cref="Scale2D"/> and optional axes,
        /// which is more natural when the caller already has <see cref="MatrixData{T}"/>-style objects.
        /// </summary>
        /// <param name="scale">
        /// XY plane scale. Provides Width, Height, pixel pitches (PixelSizeX/Y), origins (StartX/Y),
        /// and units (UnitX/Y, applied only when non-empty).
        /// </param>
        /// <param name="channel">
        /// Optional channel axis. Only <see cref="Axis.Count"/> is used (index-based; Min/Max are ignored).
        /// </param>
        /// <param name="z">
        /// Optional Z axis. Provides ZSlices, StartZ, PixelSizeZ (= <see cref="Axis.Step"/>), and UnitZ.
        /// </param>
        /// <param name="time">
        /// Optional time axis. Provides TimePoints, StartTime, TimeStep (= <see cref="Axis.Step"/>), and UnitTime.
        /// </param>
        /// <param name="fov">
        /// Optional FOV axis. Provides FovCount, TileLayout, and GlobalOrigins.
        /// </param>
        public HyperstackMetadata(Scale2D scale, Axis? channel = null, Axis? z = null, Axis? time = null, FovAxis? fov = null)
        {
            Width = scale.XCount;
            Height = scale.YCount;
            // XStep is 0 when XCount == 1; fall back to 1.0 to avoid zero pixel size in metadata
            PixelSizeX = scale.XCount > 1 ? scale.XStep : 1.0;
            PixelSizeY = scale.YCount > 1 ? scale.YStep : 1.0;
            StartX = scale.XMin;
            StartY = scale.YMin;
            if (!string.IsNullOrEmpty(scale.XUnit)) UnitX = scale.XUnit;
            if (!string.IsNullOrEmpty(scale.YUnit)) UnitY = scale.YUnit;

            // Always set count fields; default to 1 when the axis is omitted
            Channels = channel?.Count ?? 1;
            ZSlices = z?.Count ?? 1;
            TimePoints = time?.Count ?? 1;

            if (z != null)
            {
                StartZ = z.Min;
                PixelSizeZ = z.Count > 1 ? z.Step : 1.0;
                if (!string.IsNullOrEmpty(z.Unit)) UnitZ = z.Unit;
            }

            if (time != null)
            {
                StartTime = time.Min;
                TimeStep = time.Count > 1 ? time.Step : 1.0;
                if (!string.IsNullOrEmpty(time.Unit)) UnitTime = time.Unit;
            }

            if (fov != null)
            {
                FovCount = fov.Count;
                TileLayout = (fov.TileLayout.X, fov.TileLayout.Y);
                GlobalOrigins = (GlobalPoint[])fov.Origins.Clone();
            }
        }

        // Flips internal data vertically: implemented in derived classes per pixel type
        public virtual void FlipY()
        {
            throw new NotSupportedException("Cannot execute FlipVertical on a metadata-only class.");
        }

        public static string FormatXml(string xmlString)
        {
            // Return as-is if the string already contains line breaks
            if (xmlString.Contains("\n") || xmlString.Contains("\r"))
                return xmlString;

            // Pretty-print if the string is a single line
            var doc = new XmlDocument();
            doc.LoadXml(xmlString);

            var sb = new StringBuilder();
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\r\n",
                NewLineHandling = NewLineHandling.Replace
            };

            using (var writer = XmlWriter.Create(sb, settings))
            {
                doc.Save(writer);
            }

            return sb.ToString();
        }

    }

    /// <summary>
    /// Stores both hyperstack data (pixel frames) and the associated metadata.
    /// </summary>
    public class HyperstackData<T> : HyperstackMetadata where T : unmanaged
    {
        public IList<T[]>? ImageStack { get; set; }

        // Override the base PixelType property
        public new string PixelType => GetPixelTypeString();

        private string GetPixelTypeString()
        {
            if (typeof(T) == typeof(short)) return "int16";
            if (typeof(T) == typeof(ushort)) return "uint16";
            if (typeof(T) == typeof(byte)) return "uint8";
            if (typeof(T) == typeof(sbyte)) return "int8";
            if (typeof(T) == typeof(int)) return "int32";
            if (typeof(T) == typeof(uint)) return "uint32";
            if (typeof(T) == typeof(float)) return "float";
            if (typeof(T) == typeof(double)) return "double";
            throw new NotSupportedException($"Type {typeof(T)} is not supported.");
        }


        /// <summary>
        /// Creates a <see cref="HyperstackData{T}"/> from a <see cref="MatrixData{T}"/>.
        /// When <paramref name="list"/> is null the frames are taken directly from <paramref name="md"/>;
        /// passing a pre-sorted list allows custom XYCZT ordering.
        /// </summary>
        /// <param name="md"></param>
        /// <param name="list"></param>
        /// <returns></returns>
        public static HyperstackData<T> CreateFrom(MatrixData<T> md, List<T[]>? list = null)
        {
            var data = new HyperstackData<T>();
            if (list == null)
            {
                int pageNum = md.FrameCount;
                list = new List<T[]>(pageNum);
                for (int i = 0; i < pageNum; i++)
                {
                    list.Add(md.GetArray(i));
                }
            }
            data.ImageStack = list;
            data.Width = md.XCount;
            data.Height = md.YCount;
            var dimensions = md.Dimensions;
            int cnum = dimensions.GetLength("Channel");
            int znum = dimensions.GetLength("Z");
            int tnum = dimensions.GetLength("Time");
            int fovNum = dimensions.GetLength("FOV");

            data.ZSlices = znum;
            data.TimePoints = tnum;
            data.Channels = cnum;
            data.FovCount = fovNum;

            data.PixelSizeX = md.XStep;
            data.PixelSizeY = md.YStep;
            data.PixelSizeZ = dimensions.Contains("Z") ? dimensions["Z"]!.Step : 1;

            //Set units; note that Bio-Formats does not support arbitrary unit strings
            //µm is safe but other values may be unsupported by some readers
            data.UnitX = md.XUnit;
            data.UnitY = md.YUnit;
            data.UnitZ = dimensions.Contains("Z") ? dimensions["Z"]!.Unit : "";
            data.UnitTime = dimensions.Contains("Time") ? dimensions["Time"]!.Unit : "s";
            data.StartX = md.XMin;// md.Width * 0.5 + md.XMin;
            data.StartY = md.YMin;// md.Height * 0.5 + md.YMin;
            data.StartZ = dimensions.Contains("Z") ? dimensions["Z"]!.Min : 0;
            data.TimeStep = dimensions.Contains("Time") ? dimensions["Time"]!.Step : 1;
            data.StartTime = dimensions.Contains("Time") ? dimensions["Time"]!.Min : 0;

            if (data.FovCount > 1 && dimensions["FOV"] is FovAxis fovAxis)
            {
                var tile = fovAxis.TileLayout;
                data.TileLayout = (tile.X, tile.Y);
                data.GlobalOrigins = fovAxis.Origins;
            }

            // Exclude format-header blobs (e.g. FITS_HEADER, OME_XML) and the tracking key
            // ("mxplot.*" reserved namespace). These describe the source file format and do not
            // belong in the OME-XML metadata store of a newly written TIFF.
            var formatHeaderKeys = md.GetFormatHeaderKeys();
            data.MatrixDataMetadata = md.Metadata
                .Where(kv => !formatHeaderKeys.Contains(kv.Key) &&
                             !kv.Key.StartsWith("mxplot.", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return data;
        }


        /// <summary>
        /// Creates a <see cref="HyperstackData{T}"/> from a <see cref="HyperstackMetadata"/> instance.
        /// The <see cref="ImageStack"/> is left null; this is intended for vessel-creation scenarios
        /// where pixel data will be written later via <see cref="WritableVirtualStrippedFrames{T}"/>.
        /// </summary>
        public static HyperstackData<T> FromMetadata(HyperstackMetadata meta)
        {
            return new HyperstackData<T>
            {
                Width = meta.Width,
                Height = meta.Height,
                Channels = meta.Channels,
                ZSlices = meta.ZSlices,
                TimePoints = meta.TimePoints,
                FovCount = meta.FovCount,
                TileLayout = meta.TileLayout,
                GlobalOrigins = meta.GlobalOrigins,
                PixelSizeX = meta.PixelSizeX,
                PixelSizeY = meta.PixelSizeY,
                PixelSizeZ = meta.PixelSizeZ,
                UnitX = meta.UnitX,
                UnitY = meta.UnitY,
                UnitZ = meta.UnitZ,
                UnitTime = meta.UnitTime,
                StartX = meta.StartX,
                StartY = meta.StartY,
                StartZ = meta.StartZ,
                StartTime = meta.StartTime,
                TimeStep = meta.TimeStep,
                DimensionOrder = meta.DimensionOrder,
                // ImageStack intentionally null — pixel data not yet available
            };
        }

        /// <summary>
        /// Flips all frames in the Y axis.
        /// </summary>
        public override void FlipY()
        {
            if (ImageStack == null)
                return;

            IsYFlipped = true;

            if (IsVirtualMode)
                return;

            // The following is only effective in InMemory mode

            int height = this.Height;
            int width = this.Width;
            int bytesPerPixel = System.Runtime.InteropServices.Marshal.SizeOf<T>();
            int bytesPerRow = width * bytesPerPixel;

            // Process frames in parallel
            Parallel.ForEach(ImageStack, frame =>
            {
                byte[] tempRow = new byte[bytesPerRow];

                for (int row = 0; row < height / 2; row++)
                {
                    int topOffset = row * width * bytesPerPixel;
                    int bottomOffset = (height - 1 - row) * width * bytesPerPixel;

                    Buffer.BlockCopy(frame, topOffset, tempRow, 0, bytesPerRow);
                    Buffer.BlockCopy(frame, bottomOffset, frame, topOffset, bytesPerRow);
                    Buffer.BlockCopy(tempRow, 0, frame, bottomOffset, bytesPerRow);
                }
            });

            if (this.FovCount > 1)
            {
                for (int i = 0; i < this.GlobalOrigins?.Length; i++)
                {
                    var origin = this.GlobalOrigins[i];
                    this.GlobalOrigins[i] = new GlobalPoint(origin.X, -origin.Y, origin.Z);
                }
            }
        }

    }

    #endregion


    #region Save Options
    /// <summary>
    /// Write options for OME-TIFF files.
    /// </summary>
    public class OmeTiffOptions
    {
        /// <summary>
        /// When true, writes the file in BigTIFF format (64-bit offsets).
        /// Required when the data size may exceed 4 GB.
        /// </summary>
        public bool UseBigTiff { get; set; } = false;

        /// <summary>
        /// Specifies the compression codec.
        /// </summary>
        public Compression Compression { get; set; } = Compression.LZW;

        /// <summary>
        /// Predictor for LZW/Deflate compression.
        /// Defaults to <see cref="Predictor.NONE"/> for broadest reader compatibility.
        /// FluoRender's DecodeAcc16 (ARM64 macOS) crashes when Predictor.HORIZONTAL
        /// is set, regardless of strip layout.  Set to <see cref="Predictor.HORIZONTAL"/>
        /// only when targeting readers that are known-good with it.
        /// </summary>
        public Predictor Predictor { get; set; } = Predictor.NONE;
    }
    #endregion

    #region Factory

    /// <summary>
    /// Type-safe factory for creating <see cref="OmeTiffHandlerInstance{T}"/> instances.
    /// </summary>
    public static class OmeTiffFactory
    {
        public static OmeTiffHandlerInstance<short> CreateSigned16() => new OmeTiffHandlerInstance<short>();
        public static OmeTiffHandlerInstance<ushort> CreateUnsigned16() => new OmeTiffHandlerInstance<ushort>();
        public static OmeTiffHandlerInstance<byte> CreateUnsigned8() => new OmeTiffHandlerInstance<byte>();
        public static OmeTiffHandlerInstance<sbyte> CreateSigned8() => new OmeTiffHandlerInstance<sbyte>();
        public static OmeTiffHandlerInstance<int> CreateSigned32() => new OmeTiffHandlerInstance<int>();
        public static OmeTiffHandlerInstance<uint> CreateUnsigned32() => new OmeTiffHandlerInstance<uint>();
        public static OmeTiffHandlerInstance<float> CreateFloat32() => new OmeTiffHandlerInstance<float>();
        public static OmeTiffHandlerInstance<double> CreateFloat64() => new OmeTiffHandlerInstance<double>();
    }

    public static class OmeTiffReader
    {
        /// <summary>
        /// Reads metadata only (no pixel type required by the caller).
        /// </summary>
        public static HyperstackMetadata ReadMetadata(string filename)
        {
            var pixelType = DetectPixelType(filename);

            dynamic handler = pixelType switch
            {
                "int16" => OmeTiffFactory.CreateSigned16(),
                "uint16" => OmeTiffFactory.CreateUnsigned16(),
                "uint8" => OmeTiffFactory.CreateUnsigned8(),
                "int8" => OmeTiffFactory.CreateSigned8(),
                "int32" => OmeTiffFactory.CreateSigned32(),
                "uint32" => OmeTiffFactory.CreateUnsigned32(),
                "float" => OmeTiffFactory.CreateFloat32(),
                "double" => OmeTiffFactory.CreateFloat64(),
                _ => throw new NotSupportedException($"Pixel type '{pixelType}' is not supported.")
            };

            var data = handler.ReadMetadata(filename);

            return data;
        }

        /// <summary>
        /// Reads the full hyperstack with automatic pixel type detection.
        /// </summary>
        public static object ReadHyperstackAuto(string filename, LoadingMode mode, IProgress<int>? progress = null, int maxParallelDegree = 0, CancellationToken ct = default)
        {
            var pixelType = DetectPixelType(filename);

            // Local helper: set MaxParallelDegree then call ReadHyperstack
            static HyperstackData<U> ReadWith<U>(OmeTiffHandlerInstance<U> h, int deg,
                string fn, LoadingMode m, IProgress<int>? p, CancellationToken c) where U : unmanaged
            {
                h.MaxParallelDegree = deg;
                return h.ReadHyperstack(fn, m, p, c);
            }

            return pixelType switch
            {
                "int16"  => ReadWith(OmeTiffFactory.CreateSigned16(),   maxParallelDegree, filename, mode, progress, ct),
                "uint16" => ReadWith(OmeTiffFactory.CreateUnsigned16(), maxParallelDegree, filename, mode, progress, ct),
                "uint8"  => ReadWith(OmeTiffFactory.CreateUnsigned8(),  maxParallelDegree, filename, mode, progress, ct),
                "int8"   => ReadWith(OmeTiffFactory.CreateSigned8(),    maxParallelDegree, filename, mode, progress, ct),
                "int32"  => ReadWith(OmeTiffFactory.CreateSigned32(),   maxParallelDegree, filename, mode, progress, ct),
                "uint32" => ReadWith(OmeTiffFactory.CreateUnsigned32(), maxParallelDegree, filename, mode, progress, ct),
                "float"  => ReadWith(OmeTiffFactory.CreateFloat32(),    maxParallelDegree, filename, mode, progress, ct),
                "double" => ReadWith(OmeTiffFactory.CreateFloat64(),    maxParallelDegree, filename, mode, progress, ct),
                _ => throw new NotSupportedException($"Pixel type '{pixelType}' is not supported.")
            };
        }

        /// <summary>
        /// Detects the pixel type of a TIFF file by first checking the OME-XML metadata and then falling back to TIFF tags if necessary.
        /// </summary>
        /// <returns>The detected pixel type as a string (e.g., "uint8", "int16").</returns>
        public static string DetectPixelType(string filename)
        {
            if (!File.Exists(filename))
                throw new FileNotFoundException($"File not found: {filename}");

            using (var tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filename, "r"))
            {
                if (tiff == null)
                    throw new IOException("Failed to open TIFF file.");

                // 1. Try to read the pixel type from OME-XML
                var imageDescription = tiff.GetField(TiffTag.IMAGEDESCRIPTION);
                if (imageDescription != null && imageDescription.Length > 0)
                {
                    string omeXml = imageDescription[0].ToString();
                    string? pixelTypeFromXml = ExtractPixelTypeFromOmeXml(omeXml);
                    if (!string.IsNullOrEmpty(pixelTypeFromXml))
                        return pixelTypeFromXml;
                }

                // 2. Fall back to TIFF tags
                var bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)?[0].ToInt() ?? 0;
                var sampleFormat = tiff.GetField(TiffTag.SAMPLEFORMAT)?[0].ToInt() ?? 1; // Default to UINT per TIFF spec

                return InferPixelType(bitsPerSample, (SampleFormat)sampleFormat);
            }
        }

        private static string? ExtractPixelTypeFromOmeXml(string omeXml)
        {
            try
            {
                var doc = XDocument.Parse(omeXml);
                if (doc.Root == null)
                    return null;
                var ns = doc.Root.GetDefaultNamespace();
                var pixels = doc.Descendants(ns + "Pixels").FirstOrDefault();

                // Look for the Type or PixelType attribute
                return pixels?.Attribute("Type")?.Value
                    ?? pixels?.Attribute("PixelType")?.Value;
            }
            catch
            {
                return null;
            }
        }

        private static string InferPixelType(int bitsPerSample, SampleFormat sampleFormat)
        {
            return (bitsPerSample, sampleFormat) switch
            {
                (8, SampleFormat.UINT) => "uint8",
                (8, SampleFormat.INT) => "int8",
                (16, SampleFormat.UINT) => "uint16",
                (16, SampleFormat.INT) => "int16",
                (32, SampleFormat.UINT) => "uint32",
                (32, SampleFormat.INT) => "int32",
                (32, SampleFormat.IEEEFP) => "float",
                (64, SampleFormat.IEEEFP) => "double",
                _ => throw new NotSupportedException($"Unsupported format: {bitsPerSample}bit, {sampleFormat}")
            };
        }
    }
    #endregion
}

#region Usage Examples

/*
// Example 1: signed 16-bit
var signedHandler = OmeTiffFactory.CreateSigned16();
List<short[]> signedData = GetSignedImageData();
signedHandler.WriteHyperstack("signed.ome.tiff", signedData, 1024, 1024, channels: 1, zSlices: 10, timePoints: 5);

// Example 2: unsigned 16-bit
var unsignedHandler = OmeTiffFactory.CreateUnsigned16();
List<ushort[]> unsignedData = GetUnsignedImageData();
unsignedHandler.WriteHyperstack("unsigned.ome.tiff", unsignedData, 1024, 1024, channels: 3, zSlices: 10, timePoints: 20);

// Example 3: lazy enumeration (memory-efficient, large data)
// The IEnumerable<T[]> overload requires a HyperstackData<T> spec object, not raw int dimensions.
IEnumerable<ushort[]> frames = GenerateFramesLazy(2048, 2048, 1000); // 1000 frames
var spec = new HyperstackData<ushort> { Width = 2048, Height = 2048, Channels = 1, ZSlices = 1, TimePoints = 1000 };
unsignedHandler.WriteHyperstack("large.ome.tiff", frames, spec);

// Example 4: lazy read
foreach (var frame in unsignedHandler.ReadFramesLazy("large.ome.tiff"))
{
    ProcessSingleFrame(frame);
}

// Example 5: read a single frame by index
var frame50 = unsignedHandler.ReadSingleFrameAt("large.ome.tiff", 49);

// Example 6: read metadata only
var metadata = unsignedHandler.ReadMetadata("data.ome.tiff");
Console.WriteLine($"Size: {metadata.Width}x{metadata.Height}, Frames: {metadata.TotalFrames}");
*/

#endregion