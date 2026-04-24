using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Core FITS (Flexible Image Transport System) I/O.
    /// Supports BITPIX 8 / 16 / 32 / 64 / -32 / -64 with big-endian binary encoding.
    /// Custom MxPlot axis and metadata is stored as HIERARCH MXP keywords.
    /// </summary>
    /// <remarks>
    /// <para>Type mapping from BITPIX to MatrixData element type:</para>
    /// <list type="table">
    ///   <item><term>8</term><description>byte</description></item>
    ///   <item><term>16, BZERO=0</term><description>short</description></item>
    ///   <item><term>16, BZERO=32768</term><description>ushort</description></item>
    ///   <item><term>32</term><description>int</description></item>
    ///   <item><term>64</term><description>long</description></item>
    ///   <item><term>-32</term><description>float</description></item>
    ///   <item><term>-64</term><description>double</description></item>
    /// </list>
    /// <para>
    /// BSCALE/BZERO transforms other than the ushort convention (BZERO=32768, BSCALE=1)
    /// are not applied; raw stored values are returned as-is.
    /// </para>
    /// </remarks>
    public static class FitsHandler
    {
        /// <summary>
        /// Key for storing the original FITS header text in <see cref="IMatrixData.Metadata"/>.
        /// Follows the same pattern as <c>OmeTiffHandler.OmeXmlKey</c>.
        /// </summary>
        public const string FitsHeaderKey = "FITS_HEADER";

        #region Public API

        /// <summary>
        /// Reads a FITS file and returns the data as an <see cref="IMatrixData"/> with the element type
        /// determined by BITPIX (byte / short / ushort / int / long / float / double).
        /// </summary>
        public static IMatrixData Load(
            string filePath,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
        {
            Debug.WriteLine($"Loading FITS file '{filePath}'...");

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = ReadHeader(fs);

            if (header.NAxis.Length < 2)
                throw new NotSupportedException(
                    $"FITS file has {header.NAxis.Length} dimension(s); at least 2 (X, Y) are required.");
            
            return header.BitPix switch
            {
                8 => LoadCore<byte>(fs, header, progress, ct),
                16 when header.BZero == 0 => LoadCore<short>(fs, header, progress, ct),
                16 => LoadCore<ushort>(fs, header, progress, ct),
                32 => LoadCore<int>(fs, header, progress, ct),
                64 => LoadCore<long>(fs, header, progress, ct),
                -32 => LoadCore<float>(fs, header, progress, ct),
                -64 => LoadCore<double>(fs, header, progress, ct),
                _ => throw new NotSupportedException($"Unsupported FITS BITPIX={header.BitPix}.")
            };
        }

        /// <summary>
        /// Reads a FITS file as <see cref="MatrixData{T}"/>.
        /// Throws <see cref="InvalidOperationException"/> if the file's native type differs from <typeparamref name="T"/>.
        /// </summary>
        public static MatrixData<T> Load<T>(
            string filePath,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
            where T : unmanaged
        {
            IMatrixData raw = Load(filePath, progress, ct);
            if (raw is MatrixData<T> typed) return typed;
            throw new InvalidOperationException(
                $"File contains '{raw.ValueTypeName}' data, but '{typeof(T).Name}' was requested.");
        }

        /// <summary>
        /// Writes <see cref="MatrixData{T}"/> to a FITS file.
        /// Supported element types: byte, short, ushort, int, long, float, double.
        /// </summary>
        public static void Save<T>(
            string filePath,
            MatrixData<T> data,
            IProgress<int>? progress = null,
            CancellationToken ct = default)
            where T : unmanaged
        {
            int bitPix = GetBitPix<T>();
            int[] naxisValues = BuildNAxisValues(data);

            var header = new FitsHeader(bitPix, naxisValues);
            if (typeof(T) == typeof(ushort))
                header.AddBZeroBScale(32768.0, 1.0);

            header.AddXYScale(data.XMin, data.XMax, data.YMin, data.YMax, data.XUnit, data.YUnit);

            for (int a = 0; a < data.Axes.Count; a++)
                header.AddAxisInfo(a, data.Axes[a]);

            foreach (var kv in data.Metadata)
                if (!string.Equals(kv.Key, FitsHeaderKey, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(kv.Key, MatrixData.FormatHeaderMetaKey, StringComparison.OrdinalIgnoreCase))
                    header.AddMetadata(kv.Key, kv.Value);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(header.ToByteArray());

            int elementSize = Math.Abs(bitPix) / 8;
            byte[] buf = new byte[data.XCount * data.YCount * elementSize];

            progress?.Report(-data.FrameCount); // signal total frame count (negative = convention)

            for (int iz = 0; iz < data.FrameCount; iz++)
            {
                ct.ThrowIfCancellationRequested();
                EncodeFrame(data.AsSpan(iz), buf.AsSpan());
                fs.Write(buf);
                progress?.Report(iz); // 0-based index
            }

            // Pad data section to FITS block boundary with zeros
            long pos = fs.Position;
            long remain = FitsHeader.BlockBytes - pos % FitsHeader.BlockBytes;
            if (remain < FitsHeader.BlockBytes)
                fs.Write(new byte[(int)remain]);
        }

        #endregion

        #region Private implementation

        private static FitsHeader ReadHeader(FileStream fs)
        {
            var lines = new List<string>(36);
            Span<byte> buf = stackalloc byte[FitsHeader.LineBytes];

            while (true)
            {
                fs.ReadExactly(buf);
                string line = Encoding.ASCII.GetString(buf);
                lines.Add(line);

                if (line.StartsWith("END", StringComparison.Ordinal))
                {
                    // Advance stream to the first byte of the data section
                    long pos = fs.Position;
                    long remainder = FitsHeader.BlockBytes - pos % FitsHeader.BlockBytes;
                    if (remainder < FitsHeader.BlockBytes)
                        fs.Seek(remainder, SeekOrigin.Current);
                    break;
                }
            }

            Debug.WriteLine("---- FITS HEADER ----");
            Debug.WriteLine($"Read FITS header with {lines.Count} lines.");
            Debug.WriteLine(string.Join(Environment.NewLine, lines));

            return new FitsHeader(lines);
        }

        private static MatrixData<T> LoadCore<T>(
            FileStream fs,
            FitsHeader header,
            IProgress<int>? progress,
            CancellationToken ct)
            where T : unmanaged
        {
            int xCount = header.NAxis[0];
            int yCount = header.NAxis[1];
            int frameCount = 1;
            for (int i = 2; i < header.NAxis.Length; i++)
                if (header.NAxis[i] > 1) frameCount *= header.NAxis[i];

            var result = new MatrixData<T>(xCount, yCount, frameCount);

            if (header.HasXScale && header.HasYScale)
                result.SetXYScale(header.XMin, header.XMax, header.YMin, header.YMax);
            else if (header.HasWcs(1) && header.HasWcs(2))
                result.SetXYScale(
                    header.GetWcsMin(1), header.GetWcsMax(1, xCount),
                    header.GetWcsMin(2), header.GetWcsMax(2, yCount));
            result.XUnit = !string.IsNullOrEmpty(header.XUnit) ? header.XUnit : header.GetWcsUnit(1);
            result.YUnit = !string.IsNullOrEmpty(header.YUnit) ? header.YUnit : header.GetWcsUnit(2);

            int elementSize = Math.Abs(header.BitPix) / 8;
            byte[] buf = new byte[xCount * yCount * elementSize];

            progress?.Report(-frameCount); // signal total frame count (negative = convention)

            for (int iz = 0; iz < frameCount; iz++)
            {
                ct.ThrowIfCancellationRequested();
                fs.ReadExactly(buf);
                DecodeFrame(buf.AsSpan(), result.GetArray(iz), header.BitPix);
                progress?.Report(iz); // 0-based index (app adds +1 for display)
            }

            // Build dimension structure from MXP AXIS keywords; fall back to WCS for standard files
            if (header.AxisInfo.Count > 0 && header.NAxis.Length >= 3)
            {
                var axisList = new List<Axis>();
                foreach (var kv in header.AxisInfo)
                {
                    int idx = kv.Key;
                    var info = kv.Value;
                    int count = idx + 2 < header.NAxis.Length ? header.NAxis[idx + 2] : 1;
                    if (count <= 1) continue; // skip degenerate axes (consistent with frameCount calculation)
                    double min = double.IsNaN(info.Min) ? 0.0 : info.Min;
                    double max = double.IsNaN(info.Max) ? count - 1.0 : info.Max;
                    string name = string.IsNullOrEmpty(info.Name) ? $"Axis{idx}" : info.Name;
                    axisList.Add(BuildAxis(info, count, min, max, name));
                }
                if (axisList.Count > 0)
                    result.DefineDimensions(axisList.ToArray());
            }
            else if (header.NAxis.Length >= 3)
            {
                // WCS fallback: reconstruct Axis from CRVALn/CDELTn/CRPIXn/CUNITn/CNAMEn
                var wcAxes = new List<Axis>();
                for (int ni = 3; ni <= header.NAxis.Length; ni++)
                {
                    int count = header.NAxis[ni - 1];
                    if (count <= 1 || !header.HasWcs(ni)) continue;
                    double min = header.GetWcsMin(ni);
                    double max = header.GetWcsMax(ni, count);
                    string name = header.GetWcsName(ni);
                    string unit = header.GetWcsUnit(ni);
                    if (string.IsNullOrEmpty(name)) name = $"Axis{ni - 3}";
                    wcAxes.Add(new Axis(count, min, max, name, unit));
                }
                if (wcAxes.Count > 0)
                    result.DefineDimensions(wcAxes.ToArray());
                else
                {
                    // No WCS or legacy axis data: define simple index-based axes for all dimensions beyond X/Y
                    var axes = new List<Axis>();
                    for (int ni = 3; ni <= header.NAxis.Length; ni++)
                    {
                        int count = header.NAxis[ni - 1];
                        if (count <= 1) continue; // skip degenerate axes
                        axes.Add(new Axis(count, 0.0, count - 1.0, $"Axis{ni - 3}"));
                    }
                    if (axes.Count > 0)
                        result.DefineDimensions(axes.ToArray());
                }
            }

            foreach (var kv in header.Metadata)
                result.Metadata[kv.Key] = kv.Value;
            result.Metadata[FitsHeaderKey] = header.RawHeaderText;
            result.MarkAsFormatHeader(FitsHeaderKey);

            return result;
        }

        /// <summary>Decodes a big-endian FITS pixel buffer into a typed array.</summary>
        private static void DecodeFrame<T>(ReadOnlySpan<byte> src, T[] dst, int bitPix)
            where T : unmanaged
        {
            int n = dst.Length;

            if (typeof(T) == typeof(byte))
            {
                src[..n].CopyTo(MemoryMarshal.Cast<T, byte>(dst.AsSpan()));
            }
            else if (typeof(T) == typeof(short))
            {
                var d = MemoryMarshal.Cast<T, short>(dst.AsSpan());
                for (int i = 0; i < n; i++)
                    d[i] = BinaryPrimitives.ReadInt16BigEndian(src.Slice(i << 1, 2));
            }
            else if (typeof(T) == typeof(ushort))
            {
                // BZERO=32768 convention: stored as signed short, recovered as ushort = raw + 32768
                var d = MemoryMarshal.Cast<T, ushort>(dst.AsSpan());
                for (int i = 0; i < n; i++)
                {
                    short raw = BinaryPrimitives.ReadInt16BigEndian(src.Slice(i << 1, 2));
                    d[i] = (ushort)(raw + 32768);
                }
            }
            else if (typeof(T) == typeof(int))
            {
                var d = MemoryMarshal.Cast<T, int>(dst.AsSpan());
                for (int i = 0; i < n; i++)
                    d[i] = BinaryPrimitives.ReadInt32BigEndian(src.Slice(i << 2, 4));
            }
            else if (typeof(T) == typeof(long))
            {
                var d = MemoryMarshal.Cast<T, long>(dst.AsSpan());
                for (int i = 0; i < n; i++)
                    d[i] = BinaryPrimitives.ReadInt64BigEndian(src.Slice(i << 3, 8));
            }
            else if (typeof(T) == typeof(float))
            {
                var d = MemoryMarshal.Cast<T, float>(dst.AsSpan());
                for (int i = 0; i < n; i++)
                    d[i] = BinaryPrimitives.ReadSingleBigEndian(src.Slice(i << 2, 4));
            }
            else if (typeof(T) == typeof(double))
            {
                var d = MemoryMarshal.Cast<T, double>(dst.AsSpan());
                for (int i = 0; i < n; i++)
                    d[i] = BinaryPrimitives.ReadDoubleBigEndian(src.Slice(i << 3, 8));
            }
            else
            {
                throw new NotSupportedException($"Unsupported decode type '{typeof(T).Name}'.");
            }
        }

        /// <summary>Encodes a typed array into a big-endian FITS pixel buffer.</summary>
        private static void EncodeFrame<T>(ReadOnlySpan<T> src, Span<byte> dst)
            where T : unmanaged
        {
            int n = src.Length;

            if (typeof(T) == typeof(byte))
            {
                MemoryMarshal.Cast<T, byte>(src).CopyTo(dst);
            }
            else if (typeof(T) == typeof(short))
            {
                var s = MemoryMarshal.Cast<T, short>(src);
                for (int i = 0; i < n; i++)
                    BinaryPrimitives.WriteInt16BigEndian(dst.Slice(i << 1, 2), s[i]);
            }
            else if (typeof(T) == typeof(ushort))
            {
                // BZERO=32768 convention: ushort stored as signed short = value - 32768
                var s = MemoryMarshal.Cast<T, ushort>(src);
                for (int i = 0; i < n; i++)
                    BinaryPrimitives.WriteInt16BigEndian(dst.Slice(i << 1, 2), (short)(s[i] - 32768));
            }
            else if (typeof(T) == typeof(int))
            {
                var s = MemoryMarshal.Cast<T, int>(src);
                for (int i = 0; i < n; i++)
                    BinaryPrimitives.WriteInt32BigEndian(dst.Slice(i << 2, 4), s[i]);
            }
            else if (typeof(T) == typeof(long))
            {
                var s = MemoryMarshal.Cast<T, long>(src);
                for (int i = 0; i < n; i++)
                    BinaryPrimitives.WriteInt64BigEndian(dst.Slice(i << 3, 8), s[i]);
            }
            else if (typeof(T) == typeof(float))
            {
                var s = MemoryMarshal.Cast<T, float>(src);
                for (int i = 0; i < n; i++)
                    BinaryPrimitives.WriteSingleBigEndian(dst.Slice(i << 2, 4), s[i]);
            }
            else if (typeof(T) == typeof(double))
            {
                var s = MemoryMarshal.Cast<T, double>(src);
                for (int i = 0; i < n; i++)
                    BinaryPrimitives.WriteDoubleBigEndian(dst.Slice(i << 3, 8), s[i]);
            }
            else
            {
                throw new NotSupportedException($"Unsupported encode type '{typeof(T).Name}'.");
            }
        }

        private static int GetBitPix<T>() where T : unmanaged
        {
            if (typeof(T) == typeof(byte)) return 8;
            if (typeof(T) == typeof(short)) return 16;
            if (typeof(T) == typeof(ushort)) return 16;
            if (typeof(T) == typeof(int)) return 32;
            if (typeof(T) == typeof(long)) return 64;
            if (typeof(T) == typeof(float)) return -32;
            if (typeof(T) == typeof(double)) return -64;
            throw new NotSupportedException(
                $"Type '{typeof(T).Name}' cannot be stored in FITS format. " +
                "Supported types: byte, short, ushort, int, long, float, double.");
        }

        private static int[] BuildNAxisValues<T>(MatrixData<T> data) where T : unmanaged
        {
            if (data.FrameCount == 1)
                return [data.XCount, data.YCount];

            if (data.Axes.Count == 0)
                return [data.XCount, data.YCount, data.FrameCount];

            var result = new int[2 + data.Axes.Count];
            result[0] = data.XCount;
            result[1] = data.YCount;
            for (int i = 0; i < data.Axes.Count; i++)
                result[i + 2] = data.Axes[i].Count;
            return result;
        }

        private static Axis BuildAxis(AxisInfoRecord info, int count, double min, double max, string name)
        {
            if (info.Type == "FOV"
                && info.GridX > 0 && info.GridY > 0
                && info.OriginsByIndex.Count == info.GridX * info.GridY)
            {
                var fov = new FovAxis(info.OriginsByIndex.Values.ToList(), info.GridX, info.GridY);
                if (!string.IsNullOrEmpty(info.Unit)) fov.Unit = info.Unit;
                return fov;
            }

            if (info.TagsByIndex.Count > 0)
            {
                if (info.Type == "CHANNEL")
                    return BuildColorChannel(info, name);
                var ta = new TaggedAxis(info.TagsByIndex.Values.ToArray(), name);
                if (!string.IsNullOrEmpty(info.Unit)) ta.Unit = info.Unit;
                return ta;
            }

            var ax = new Axis(count, min, max, name);
            if (!string.IsNullOrEmpty(info.Unit)) ax.Unit = info.Unit;
            return ax;
        }

        private static ColorChannel BuildColorChannel(AxisInfoRecord info, string name)
        {
            var tags = info.TagsByIndex.Values.ToArray();
            var cc = new ColorChannel(tags);
            cc.Name = name;
            if (!string.IsNullOrEmpty(info.Unit)) cc.Unit = info.Unit;
            if (info.ColorsByIndex.Count == tags.Length)
                cc.AssignColors(info.ColorsByIndex.Values.ToArray());
            if (info.WvByIndex.Count == tags.Length)
                cc.AssignWavelengths(info.WvByIndex.Values.ToArray());
            return cc;
        }

        #endregion
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Holds parsed MXP AXIS keyword data for one dimension axis.
    /// Accumulated incrementally as HIERARCH MXP AXIS n … lines are parsed.
    /// </summary>
    internal sealed class AxisInfoRecord
    {
        public string Name = "";
        public double Min = double.NaN;
        public double Max = double.NaN;
        /// <summary>"LINEAR" (default), "TAGGED", "CHANNEL", or "FOV".</summary>
        public string Type = "LINEAR";

        // TaggedAxis / ColorChannel
        public SortedDictionary<int, string> TagsByIndex = new();
        public SortedDictionary<int, int> ColorsByIndex = new();
        public SortedDictionary<int, double> WvByIndex = new();

        // FovAxis
        public int GridX = 0;
        public int GridY = 0;
        public SortedDictionary<int, GlobalPoint> OriginsByIndex = new();

        public string Unit = "";
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses and constructs FITS primary header blocks.
    /// Custom MxPlot fields are stored as HIERARCH MXPLOT keywords.
    /// </summary>
    internal sealed class FitsHeader
    {
        internal const int LineBytes = 80;
        internal const int BlockBytes = 2880;

        private readonly List<string> _lines = new();

        // ── Standard FITS fields ───────────────────────────────────────────
        public int BitPix { get; private set; } = -32;
        public int[] NAxis { get; private set; } = [];
        public double BScale { get; private set; } = 1.0;
        public double BZero { get; private set; } = 0.0;

        // ── MxPlot extension fields ────────────────────────────────────────
        public bool HasXScale { get; private set; }
        public double XMin { get; private set; }
        public double XMax { get; private set; }
        public bool HasYScale { get; private set; }
        public double YMin { get; private set; }
        public double YMax { get; private set; }
        public string XUnit { get; private set; } = "";
        public string YUnit { get; private set; } = "";

        /// <summary>Axis info keyed by axis index (0-based relative to 3rd NAXIS).</summary>
        public SortedDictionary<int, AxisInfoRecord> AxisInfo { get; }
            = new();

        /// <summary>Key-value pairs from HIERARCH MXP META keywords.</summary>
        public Dictionary<string, string> Metadata { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        // ── WCS fallback fields (parsed from standard FITS files) ────────
        private readonly Dictionary<int, double> _crpix = new();
        private readonly Dictionary<int, double> _crval = new();
        private readonly Dictionary<int, double> _cdelt = new();
        private readonly Dictionary<int, string> _cunit = new();
        private readonly Dictionary<int, string> _cname = new();
        private string _rawHeaderText = "";

        // ── Legacy MatrixDataPlot HISTORY fields (private; promoted to AxisInfo/XYScale after parsing) ─
        private readonly Dictionary<int, (double Min, double Max)> _legacyAxisScales = new();
        private readonly List<string> _legacySeriesOrder = new();

        /// <summary>Raw FITS header text (one trimmed record per line, '\n'-separated).
        /// Stored as <c>Metadata[FitsHandler.FitsHeaderKey]</c> after loading.</summary>
        public string RawHeaderText => _rawHeaderText;

        /// <returns><see langword="true"/> if at least CRVALn is present for the given 1-based axis.</returns>
        public bool HasWcs(int n) => _crval.ContainsKey(n);

        /// <summary>World coordinate at first pixel (1-based): CRVALn + CDELTn × (1 − CRPIXn).</summary>
        public double GetWcsMin(int n)
        {
            double crpix = _crpix.TryGetValue(n, out var cp) ? cp : 1.0;
            double crval = _crval.TryGetValue(n, out var cv) ? cv : 0.0;
            double cdelt = _cdelt.TryGetValue(n, out var cd) ? cd : 1.0;
            return crval + cdelt * (1.0 - crpix);
        }

        /// <summary>World coordinate at last pixel: CRVALn + CDELTn × (NAXISn − CRPIXn).</summary>
        public double GetWcsMax(int n, int count)
        {
            double crpix = _crpix.TryGetValue(n, out var cp) ? cp : 1.0;
            double crval = _crval.TryGetValue(n, out var cv) ? cv : 0.0;
            double cdelt = _cdelt.TryGetValue(n, out var cd) ? cd : 1.0;
            return crval + cdelt * (count - crpix);
        }

        public string GetWcsUnit(int n) => _cunit.TryGetValue(n, out var u) ? u.Trim() : "";
        public string GetWcsName(int n) => _cname.TryGetValue(n, out var nm) ? nm.Trim() : "";

        // ── Reading constructor ────────────────────────────────────────────

        /// <summary>Parses a list of 80-byte FITS header records (up to and including END).</summary>
        public FitsHeader(List<string> headerLines)
        {
            if (headerLines.Count == 0 ||
                !headerLines[0].StartsWith("SIMPLE", StringComparison.Ordinal))
                throw new FormatException(
                    $"Not a valid FITS file: first record is '{headerLines[0].TrimEnd()}'.");

            int i = 0;
            while (i < headerLines.Count)
            {
                string line = headerLines[i];
                if (line.StartsWith("END", StringComparison.Ordinal)) break;
                ParseRecord(line, headerLines, ref i);
                i++;
            }
            _rawHeaderText = string.Join("\n", headerLines.Select(l => l.TrimEnd()));
            // If no MXP axis/scale data was found, promote any HISTORY legacy data
            ApplyLegacyHistoryIfPresent();
        }

        // ── Writing constructor ────────────────────────────────────────────

        /// <summary>Creates a minimal header for writing (SIMPLE / BITPIX / NAXIS / EXTEND).</summary>
        public FitsHeader(int bitPix, int[] naxisValues)
        {
            BitPix = bitPix;
            NAxis = naxisValues;
            WriteRecord("SIMPLE", "T", "FITS standard");
            WriteRecord("BITPIX",
                bitPix.ToString(CultureInfo.InvariantCulture).PadLeft(20),
                "bits per data pixel");
            WriteRecord("NAXIS",
                naxisValues.Length.ToString(CultureInfo.InvariantCulture).PadLeft(20),
                "number of data axes");
            for (int i = 0; i < naxisValues.Length; i++)
                WriteRecord($"NAXIS{i + 1}",
                    naxisValues[i].ToString(CultureInfo.InvariantCulture).PadLeft(20),
                    $"length of data axis {i + 1}");
            WriteRecord("EXTEND", "T", "FITS dataset may contain extensions");
        }

        // ── Write helpers ──────────────────────────────────────────────────

        public void AddBZeroBScale(double bzero, double bscale)
        {
            WriteRecord("BZERO", FmtDbl(bzero), "offset data range to that of unsigned");
            WriteRecord("BSCALE", FmtDbl(bscale), "default scaling factor");
        }

        public void AddXYScale(
            double xmin, double xmax,
            double ymin, double ymax,
            string xunit, string yunit)
        {
            WriteHierarch("MXP XMIN", FmtDblC(xmin));
            WriteHierarch("MXP XMAX", FmtDblC(xmax));
            WriteHierarch("MXP YMIN", FmtDblC(ymin));
            WriteHierarch("MXP YMAX", FmtDblC(ymax));
            if (!string.IsNullOrEmpty(xunit))
                WriteHierarchString("MXP XUNIT", xunit);
            if (!string.IsNullOrEmpty(yunit))
                WriteHierarchString("MXP YUNIT", yunit);
        }

        public void AddAxisInfo(int index, Axis axis)
        {
            string pfx = $"MXP AXIS {index}";
            WriteHierarchString($"{pfx} NAME", axis.Name);
            WriteHierarch($"{pfx} MIN", FmtDblC(axis.Min));
            WriteHierarch($"{pfx} MAX", FmtDblC(axis.Max));
            if (!string.IsNullOrEmpty(axis.Unit))
                WriteHierarchString($"{pfx} UNIT", axis.Unit);

            if (axis is FovAxis fov)
            {
                WriteHierarch($"{pfx} TYPE", "'FOV'");
                WriteHierarch($"{pfx} GRDX", fov.TileLayout.X.ToString(CultureInfo.InvariantCulture));
                WriteHierarch($"{pfx} GRDY", fov.TileLayout.Y.ToString(CultureInfo.InvariantCulture));
                for (int k = 0; k < fov.Origins.Length; k++)
                {
                    var o = fov.Origins[k];
                    WriteHierarch($"{pfx} O {k} X", FmtDblC(o.X));
                    WriteHierarch($"{pfx} O {k} Y", FmtDblC(o.Y));
                    WriteHierarch($"{pfx} O {k} Z", FmtDblC(o.Z));
                }
            }
            else if (axis is ColorChannel cc)
            {
                WriteHierarch($"{pfx} TYPE", "'CHANNEL'");
                for (int k = 0; k < cc.Tags.Count; k++)
                    WriteHierarchString($"{pfx} TAG {k}", cc.Tags[k]);
                if (cc.HasAssignedColors)
                    for (int k = 0; k < cc.Tags.Count; k++)
                        WriteHierarch($"{pfx} CLR {k}", cc.AssignedColors![k].ToString(CultureInfo.InvariantCulture));
                if (cc.HasWavelengths)
                    for (int k = 0; k < cc.Tags.Count; k++)
                        WriteHierarch($"{pfx} WV {k}", FmtDblC(cc.Wavelengths![k]));
            }
            else if (axis is TaggedAxis ta)
            {
                WriteHierarch($"{pfx} TYPE", "'TAGGED'");
                for (int k = 0; k < ta.Tags.Count; k++)
                    WriteHierarchString($"{pfx} TAG {k}", ta.Tags[k]);
            }
        }

        /// <summary>
        /// Writes a metadata entry as: <c>HIERARCH MXP META = 'key' : 'value_chunk'</c>.
        /// The original key is preserved verbatim (case, dots, spaces, etc.).
        /// Long values are split across multiple lines with the same key; duplicate keys are
        /// concatenated on read.
        /// </summary>
        public void AddMetadata(string key, string value)
        {
            if (key.Length == 0) return;
            // Key: FITS-quote escape only (' → '') — original chars/case preserved
            string escapedKey = key.Replace("'", "''");
            // Value: same escaping as WriteHierarchString
            string s = value
                .Replace("\\", "\\\\")
                .Replace("\r\n", "\\n")
                .Replace("\r", "\\n")
                .Replace("\n", "\\n")
                .Replace("'", "''");
            // Budget per line: 80 − "HIERARCH MXP META = '" (21) − escapedKey − "' : '" (5) − "'" (1)
            //                = 53 − escapedKey.Length
            int budget = 53 - escapedKey.Length;
            if (budget < 1) budget = 1;
            int pos = 0;
            do
            {
                int end = Math.Min(pos + budget, s.Length);
                // Don't split within '' or \\ escape sequences
                if (end < s.Length && end > pos && s[end - 1] == '\'' && s[end] == '\'')
                    end--;
                else if (end > pos && s[end - 1] == '\\')
                    end--;
                if (end == pos) end = Math.Min(pos + budget, s.Length);
                _lines.Add(Pad80($"HIERARCH MXP META = '{escapedKey}' : '{s[pos..end]}'"));
                pos = end;
            } while (pos < s.Length);
        }

        /// <summary>Serializes the header to a FITS-compliant byte array padded to block boundary.</summary>
        public byte[] ToByteArray()
        {
            var sb = new StringBuilder(BlockBytes);
            foreach (var line in _lines)
                sb.Append(line);
            sb.Append("END".PadRight(LineBytes));

            // Pad to FITS block boundary with ASCII spaces
            int len = sb.Length;
            int remain = BlockBytes - len % BlockBytes;
            if (remain < BlockBytes)
                sb.Append(' ', remain);

            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        // ── Line builders ──────────────────────────────────────────────────

        private void WriteRecord(string keyword, string value, string comment = "")
        {
            var sb = new StringBuilder(LineBytes);
            sb.Append(keyword.PadRight(8));
            sb.Append("= ");
            sb.Append(value);
            if (!string.IsNullOrEmpty(comment))
            {
                sb.Append(" / ");
                sb.Append(comment);
            }
            _lines.Add(Pad80(sb.ToString()));
        }

        private void WriteHierarch(string hierarch, string value)
            => _lines.Add(Pad80($"HIERARCH {hierarch} = {value}"));

        /// <summary>
        /// Writes a string-valued HIERARCH keyword, splitting into continuation lines when needed.
        /// Custom escape on write: <c>\</c> → <c>\\</c>, newlines → <c>\n</c>.
        /// FITS string escape: <c>'</c> → <c>''</c>.
        /// On read, consecutive lines with the same keyword are concatenated before unescaping.
        /// </summary>
        private void WriteHierarchString(string keyword, string rawValue)
        {
            // Step 1 — custom escape (backslash first, then newlines)
            string s = rawValue
                .Replace("\\", "\\\\")
                .Replace("\r\n", "\\n")
                .Replace("\r", "\\n")
                .Replace("\n", "\\n");
            // Step 2 — FITS string escape: ' → ''
            s = s.Replace("'", "''");

            // Per-line budget: "HIERARCH " (9) + keyword + " = '" (4) + chunk + "'" (1) ≤ 80
            int chunkSize = LineBytes - 9 - keyword.Length - 5;
            if (chunkSize <= 0) return; // keyword too long; silently skip

            int pos = 0;
            do
            {
                int end = Math.Min(pos + chunkSize, s.Length);
                // Don't split within a '' escape pair or a \n/\\ escape sequence
                if (end < s.Length && end > pos && s[end - 1] == '\'' && s[end] == '\'')
                    end--;
                else if (end > pos && s[end - 1] == '\\')
                    end--;
                if (end == pos) end = Math.Min(pos + chunkSize, s.Length); // safety: force advance

                WriteHierarch(keyword, $"'{s[pos..end]}'");
                pos = end;
            }
            while (pos < s.Length);
        }

        // ── Record parsing ─────────────────────────────────────────────────

        private void ParseRecord(string line, List<string> allLines, ref int i)
        {
            if (line.StartsWith("BITPIX", StringComparison.Ordinal))
            {
                BitPix = ParseInt(ValuePart(line));
            }
            else if (line.StartsWith("NAXIS ", StringComparison.Ordinal))
            {
                // "NAXIS   = N" — the next N lines are NAXIS1..NAXISn
                int naxis = ParseInt(ValuePart(line));
                var axes = new int[naxis];
                for (int a = 0; a < naxis; a++)
                {
                    i++;
                    if (i >= allLines.Count)
                        throw new FormatException(
                            $"Unexpected end of FITS header reading NAXIS{a + 1}.");
                    axes[a] = ParseInt(ValuePart(allLines[i]));
                }
                NAxis = axes;
            }
            else if (line.StartsWith("BSCALE", StringComparison.Ordinal))
            {
                BScale = ParseDbl(ValuePart(line));
            }
            else if (line.StartsWith("BZERO", StringComparison.Ordinal))
            {
                BZero = ParseDbl(ValuePart(line));
            }
            else if (line.StartsWith("HIERARCH MXP ", StringComparison.Ordinal))
            {
                ParseMxPlotHierarch(line);
            }
            else if (TryParseAxisKeyword(line, "CRPIX", out int crpixN))
                _crpix[crpixN] = ParseDbl(ValuePart(line));
            else if (TryParseAxisKeyword(line, "CRVAL", out int crvalN))
                _crval[crvalN] = ParseDbl(ValuePart(line));
            else if (TryParseAxisKeyword(line, "CDELT", out int cdeltN))
                _cdelt[cdeltN] = ParseDbl(ValuePart(line));
            else if (TryParseAxisKeyword(line, "CUNIT", out int cunitN))
                _cunit[cunitN] = ExtractValue(line[(line.IndexOf('=') + 1)..].TrimStart());
            else if (TryParseAxisKeyword(line, "CNAME", out int cnameN))
                _cname[cnameN] = ExtractValue(line[(line.IndexOf('=') + 1)..].TrimStart());
            else if (line.StartsWith("HISTORY ", StringComparison.Ordinal))
                ParseLegacyHistory(line);
            // SIMPLE, DATE, EXTEND, COMMENT, CTYPE, etc. are intentionally ignored
        }

        /// <summary>
        /// After all header lines are parsed, promotes legacy HISTORY data into the standard fields
        /// (<see cref="HasXScale"/> / <see cref="AxisInfo"/>) if no MXP HIERARCH keywords were found.
        /// This allows files written by the old MatrixDataPlot application to load correctly.
        /// </summary>
        private void ApplyLegacyHistoryIfPresent()
        {
            if (_legacyAxisScales.Count == 0) return;

            // XY scale: AXIS_SCALE0 = X, AXIS_SCALE1 = Y
            if (!HasXScale &&
                _legacyAxisScales.TryGetValue(0, out var lx) &&
                _legacyAxisScales.TryGetValue(1, out var ly))
            {
                HasXScale = true; XMin = lx.Min; XMax = lx.Max;
                HasYScale = true; YMin = ly.Min; YMax = ly.Max;
            }

            // Hyperstack axes: AXIS_SCALE2 → NAXIS3, AXIS_SCALE3 → NAXIS4, ...
            // Only promote if no MXP AXIS keywords were parsed
            if (AxisInfo.Count > 0) return;
            int orderIdx = 0;
            for (int ni = 2; ni < NAxis.Length; ni++)
            {
                if (!_legacyAxisScales.TryGetValue(ni, out var scale) &&
                    orderIdx >= _legacySeriesOrder.Count) continue;

                var info = new AxisInfoRecord();
                info.Name = orderIdx < _legacySeriesOrder.Count
                    ? _legacySeriesOrder[orderIdx] : $"Axis{ni - 2}";
                if (_legacyAxisScales.TryGetValue(ni, out scale))
                { info.Min = scale.Min; info.Max = scale.Max; }
                AxisInfo[ni - 2] = info;
                orderIdx++;
            }
        }

        /// <summary>
        /// Parses legacy <c>HISTORY AXIS_SCALE{n} = min,max</c> and <c>HISTORY SERIES_ORDER = name1,name2</c>
        /// keywords written by the old MatrixDataPlot application.
        /// </summary>
        private void ParseLegacyHistory(string line)
        {
            // "HISTORY " is 8 chars; remainder is free-form text
            string text = line[8..].TrimEnd();
            int eqIdx = text.IndexOf('=');
            if (eqIdx < 0) return;

            string key = text[..eqIdx].Trim();
            string val = text[(eqIdx + 1)..].Trim();

            if (key.StartsWith("AXIS_SCALE", StringComparison.Ordinal))
            {
                if (!int.TryParse(key["AXIS_SCALE".Length..], out int axisIdx)) return;
                int commaIdx = val.IndexOf(',');
                if (commaIdx < 0) return;
                if (!double.TryParse(val[..commaIdx], NumberStyles.Float, CultureInfo.InvariantCulture, out double min)) return;
                if (!double.TryParse(val[(commaIdx + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double max)) return;
                _legacyAxisScales[axisIdx] = (min, max);
            }
            else if (key == "SERIES_ORDER")
            {
                _legacySeriesOrder.AddRange(
                    val.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0));
            }
        }

        private void ParseMxPlotHierarch(string line)
        {
            // "HIERARCH MXP <key> = <value> [/ <comment>]"
            // "HIERARCH MXP " is 13 chars
            int eqIdx = line.IndexOf('=', 13);
            if (eqIdx < 0) return;

            string key = line[13..eqIdx].TrimEnd();   // e.g. "XMIN", "AXIS 0 NAME", "META KEY1"
            string rest = line[(eqIdx + 1)..].TrimStart();
            string raw = ExtractValue(rest);

            switch (key)
            {
                case "XMIN": HasXScale = true; XMin = ParseDbl(raw); break;
                case "XMAX": XMax = ParseDbl(raw); break;
                case "YMIN": HasYScale = true; YMin = ParseDbl(raw); break;
                case "YMAX": YMax = ParseDbl(raw); break;
                // String values: concatenate continuation chunks, unescape at end of each chunk
                // (UnescapeValue is safe to call on partial chunks; full unescape on assembled string
                //  is done by appending decoded chunks so that \n / \\ crossing line boundaries works)
                case "XUNIT": XUnit += UnescapeValue(raw); break;
                case "YUNIT": YUnit += UnescapeValue(raw); break;
                // New format: HIERARCH MXP META = 'key' : 'value_chunk'
                case "META": ParseMetaRecord(rest); break;
                default:
                    if (key.StartsWith("AXIS ", StringComparison.Ordinal))
                        ParseAxisKey(key[5..], UnescapeValue(raw));
                    else if (key.StartsWith("META ", StringComparison.Ordinal))
                    {
                        // Legacy format: HIERARCH MXP META SAFEKEY = 'value' (backward compat)
                        string metaKey = key[5..];
                        string chunk = UnescapeValue(raw);
                        Metadata[metaKey] = Metadata.TryGetValue(metaKey, out var existing)
                            ? existing + chunk
                            : chunk;
                    }
                    break;
            }
        }

        private void ParseAxisKey(string rest, string value)
        {
            // rest = "0 NAME", "0 TYPE", "0 MIN", "0 MAX",
            //        "0 GRDX", "0 GRDY",
            //        "0 TAG 2", "0 CLR 2", "0 WV 2",
            //        "0 O 5 X", "0 O 5 Y", "0 O 5 Z"
            int spaceIdx = rest.IndexOf(' ');
            if (spaceIdx < 0) return;
            if (!int.TryParse(rest[..spaceIdx], out int idx)) return;

            string field = rest[(spaceIdx + 1)..];
            if (!AxisInfo.TryGetValue(idx, out var info))
            {
                info = new AxisInfoRecord();
                AxisInfo[idx] = info;
            }

            if (field == "NAME") { info.Name += value; }
            else if (field == "MIN") { info.Min = ParseDbl(value); }
            else if (field == "MAX") { info.Max = ParseDbl(value); }
            else if (field == "UNIT") { info.Unit += value; }
            else if (field == "TYPE") { info.Type = value.Trim(); }
            else if (field == "GRDX") { info.GridX = ParseInt(value); }
            else if (field == "GRDY") { info.GridY = ParseInt(value); }
            else if (field.StartsWith("TAG ", StringComparison.Ordinal))
            {
                if (int.TryParse(field[4..], out int k))
                    info.TagsByIndex[k] = info.TagsByIndex.TryGetValue(k, out var prev) ? prev + value : value;
            }
            else if (field.StartsWith("CLR ", StringComparison.Ordinal))
            {
                if (int.TryParse(field[4..], out int k))
                    info.ColorsByIndex[k] = ParseInt(value);
            }
            else if (field.StartsWith("WV ", StringComparison.Ordinal))
            {
                if (int.TryParse(field[3..], out int k))
                    info.WvByIndex[k] = ParseDbl(value);
            }
            else if (field.StartsWith("O ", StringComparison.Ordinal))
            {
                // "O k X", "O k Y", "O k Z"
                string[] parts = field[2..].Split(' ', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out int k))
                {
                    var gp = info.OriginsByIndex.TryGetValue(k, out var prev) ? prev : new GlobalPoint(0, 0, 0);
                    gp = parts[1].Trim() switch
                    {
                        "X" => gp with { X = ParseDbl(value) },
                        "Y" => gp with { Y = ParseDbl(value) },
                        "Z" => gp with { Z = ParseDbl(value) },
                        _ => gp
                    };
                    info.OriginsByIndex[k] = gp;
                }
            }
        }

        /// <summary>
        /// Parses a new-format META record whose FITS value field is <c>'key' : 'value_chunk'</c>.
        /// The key is decoded (FITS <c>''</c> → <c>'</c>); the value is fully unescaped.
        /// Duplicate keys are concatenated to support long-value line splitting.
        /// </summary>
        private void ParseMetaRecord(string rest)
        {
            // rest = "'escaped_key' : 'value_chunk'"
            if (rest.Length == 0 || rest[0] != '\'') return;
            // Scan and decode key: '' → '
            int i = 1;
            var keySb = new StringBuilder();
            while (i < rest.Length)
            {
                if (rest[i] == '\'')
                {
                    if (i + 1 < rest.Length && rest[i + 1] == '\'') { keySb.Append('\''); i += 2; continue; }
                    i++; break; // closing quote
                }
                keySb.Append(rest[i++]);
            }
            string metaKey = keySb.ToString();
            // Find ':' separator, then the opening quote of the value chunk
            int colonIdx = rest.IndexOf(':', i);
            if (colonIdx < 0) return;
            int quoteIdx = rest.IndexOf('\'', colonIdx + 1);
            if (quoteIdx < 0) return;
            string chunk = UnescapeValue(ExtractValue(rest[quoteIdx..]));
            Metadata[metaKey] = Metadata.TryGetValue(metaKey, out var existing) ? existing + chunk : chunk;
        }

        // ── Utilities ──────────────────────────────────────────────────────

        /// <summary>Returns the value portion of a standard FITS record (between '=' and '/').</summary>
        private static string ValuePart(string line)
        {
            int eqIdx = line.IndexOf('=');
            if (eqIdx < 0) return "";
            string rest = line[(eqIdx + 1)..];
            int slashIdx = rest.IndexOf('/');
            return slashIdx >= 0 ? rest[..slashIdx].Trim() : rest.Trim();
        }

        /// <summary>Extracts the logical value from the value+comment portion of a HIERARCH record.
        /// Handles single-quoted FITS strings (including '' escaped quotes) and unquoted numbers.</summary>
        private static string ExtractValue(string rest)
        {
            if (rest.Length == 0) return "";
            if (rest[0] == '\'')
            {
                int end = 1;
                while (end < rest.Length)
                {
                    if (rest[end] == '\'')
                    {
                        if (end + 1 < rest.Length && rest[end + 1] == '\'') { end += 2; continue; }
                        break;
                    }
                    end++;
                }
                return rest[1..end].Replace("''", "'");
            }
            int slash = rest.IndexOf('/');
            return slash >= 0 ? rest[..slash].Trim() : rest.Trim();
        }

        private static int ParseInt(string s) => int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
        private static double ParseDbl(string s) => double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>20-char right-justified double for standard keyword value fields.</summary>
        private static string FmtDbl(double v) => v.ToString("G17", CultureInfo.InvariantCulture).PadLeft(20);
        /// <summary>Compact double for HIERARCH keyword values (no padding).</summary>
        private static string FmtDblC(double v) => v.ToString("G17", CultureInfo.InvariantCulture);

        private static string Pad80(string s) => s.Length >= LineBytes ? s[..LineBytes] : s.PadRight(LineBytes);

        /// <summary>Parses WCS keywords of the form PREFIX + digits (e.g. CRPIX1, CDELT10).
        /// Returns <see langword="true"/> and sets <paramref name="n"/> (1-based axis number).</summary>
        private static bool TryParseAxisKeyword(string line, string prefix, out int n)
        {
            n = 0;
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
            // FITS keyword field occupies columns 1-8; read up to first space or '='
            ReadOnlySpan<char> keyField = line.AsSpan(0, Math.Min(8, line.Length)).TrimEnd();
            if (keyField.Length <= prefix.Length) return false;
            return int.TryParse(keyField[prefix.Length..], out n) && n >= 1;
        }

        /// <summary>Reverses the custom escape applied by <see cref="WriteHierarchString"/>:
        /// <c>\n</c> → newline, <c>\\</c> → backslash.</summary>
        private static string UnescapeValue(string s)
        {
            if (s.IndexOf('\\') < 0) return s; // fast path: no escape sequences

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char next = s[++i];
                    if (next == 'n') sb.Append('\n');
                    else if (next == '\\') sb.Append('\\');
                    else { sb.Append('\\'); sb.Append(next); } // unknown escape: pass through
                }
                else
                {
                    sb.Append(s[i]);
                }
            }
            return sb.ToString();
        }

            }
        }
