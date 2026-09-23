using MxPlot.Core.IO.CacheStrategies;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;


namespace MxPlot.Core.IO
{

    /// <summary>
    /// Marker for "this on-demand frame list is specifically MMF-backed" -- the non-generic
    /// (<c>T</c>-erased) type predicate consumers at the <see cref="IMatrixData"/> boundary use to
    /// tell an <see cref="MmfFrames{T}"/> apart from any other <see cref="VirtualFrames{T}"/>
    /// subclass without needing <c>T</c>. Carries no members of its own: everything an MMF
    /// implementor needs is already on <see cref="ILazyDataSource"/>/<see cref="ICacheableFrameList"/>,
    /// including <see cref="ILazyDataSource.SourcePath"/> as the real local filesystem path (no
    /// separate <c>FilePath</c> member). Reached via <see cref="IMatrixData.GetDiagnosticCacheableList"/>
    /// (that method's single result pattern-matched against this marker) rather than a separate,
    /// narrower accessor of its own.
    /// </summary>
    public interface IMmfFrameList : ILazyDataSource, ICacheableFrameList
    {
    }

    /// <summary>
    /// Centralizes the heuristics <see cref="MmfFrames{T}"/> uses to size its LRU frame
    /// cache from available memory and per-frame byte size, instead of a flat percentage of the
    /// frame count that knows nothing about either.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="MemoryBudgetFraction"/> is deliberately conservative (not 1.0): opening several
    /// large virtual datasets one after another is a realistic workflow, and each dataset only
    /// queries "available" memory at its own construction/growth time. Capping the fraction any
    /// one dataset can claim leaves headroom for datasets opened later (which will in turn see a
    /// smaller "available" figure, since the earlier ones' caches already exist) -- a cheap,
    /// self-limiting approximation, not a true cross-instance budget coordinator.
    /// </para>
    /// <para>
    /// Growing a <see cref="VirtualFrames{T}.CacheCapacity"/> at runtime takes effect immediately
    /// and safely (the eviction check re-reads it on every insert). Shrinking it does not by
    /// itself proactively trim anything already cached -- callers that want memory back must call
    /// <see cref="VirtualFrames{T}.TrimCacheTo"/> explicitly (see its own remarks for why).
    /// </para>
    /// </remarks>
    public static class VirtualCachePolicy
    {
        /// <summary>
        /// Fraction of <see cref="GC.GetGCMemoryInfo"/>'s <c>TotalAvailableMemoryBytes</c> that a
        /// single <see cref="VirtualFrames{T}"/> instance may claim for its baseline frame cache
        /// (i.e. outside of a temporary, higher-budget mode like <see cref="VolumeModeMemoryBudgetFraction"/>).
        /// </summary>
        public static double MemoryBudgetFraction { get; set; } = 0.25;

        /// <summary>
        /// Fraction of available memory allowed for a temporary, deliberately-elevated budget --
        /// e.g. while <c>DimensionStrategy</c> is in Volume mode for an active orthogonal view,
        /// whose access pattern (every frame along the sliced axis, touched on every crosshair
        /// move) thrashes badly even a few frames short of fully fitting the cache (a cyclic full
        /// scan against LRU eviction degrades sharply, not gracefully, once undersized). Higher
        /// than <see cref="MemoryBudgetFraction"/> because this elevated budget is meant to be
        /// temporary and is expected to be released (via <see cref="VirtualFrames{T}.TrimCacheTo"/>)
        /// once the caller-specific mode that requested it ends.
        /// </summary>
        public static double VolumeModeMemoryBudgetFraction { get; set; } = 0.5;

        /// <summary>Absolute floor on any computed capacity, in frames.</summary>
        public static int MinCapacity { get; set; } = 16;

        /// <summary>
        /// Absolute ceiling on any computed capacity, in frames -- independent of how much memory
        /// the budget would otherwise allow, since holding very large frame counts has real
        /// per-frame bookkeeping overhead (LRU list nodes, dictionary entries) beyond raw bytes.
        /// </summary>
        public static int MaxCapacity { get; set; } = 8192;

        /// <summary>
        /// Computes a cache capacity (in frames) for <paramref name="idealFrameCount"/> frames of
        /// <paramref name="frameSizeBytes"/> each, clamped to what <see cref="MemoryBudgetFraction"/>
        /// of currently available memory allows (and to <see cref="MinCapacity"/>/<see cref="MaxCapacity"/>).
        /// </summary>
        /// <param name="frameSizeBytes">Byte size of a single frame.</param>
        /// <param name="idealFrameCount">
        /// The frame count that would fully avoid thrashing for the access pattern being sized for
        /// -- e.g. the total physical frame count for an initial baseline, or
        /// (target-axis length × composite-axis length) for an orthogonal/Volume-mode working set.
        /// </param>
        public static int ComputeCapacity(long frameSizeBytes, int idealFrameCount)
            => ComputeCapacity(frameSizeBytes, idealFrameCount, MemoryBudgetFraction);

        /// <summary>
        /// Same as <see cref="ComputeCapacity(long, int)"/> but with an explicit budget fraction
        /// (e.g. <see cref="VolumeModeMemoryBudgetFraction"/>) instead of <see cref="MemoryBudgetFraction"/>.
        /// </summary>
        public static int ComputeCapacity(long frameSizeBytes, int idealFrameCount, double budgetFraction)
            => ComputeCapacity(frameSizeBytes, idealFrameCount, budgetFraction, MaxCapacity);

        /// <summary>
        /// Same as <see cref="ComputeCapacity(long, int, double)"/> but with an explicit frame-count
        /// ceiling in place of <see cref="MaxCapacity"/> -- for a backend whose cache is meant to
        /// converge to holding the whole dataset whenever the memory budget allows (e.g. a Lazy
        /// decode backend), where a fixed frame-count cap would stop a 60,000-frame file of small
        /// frames from ever loading fully even though it easily fits. Pass <see cref="int.MaxValue"/>
        /// to bound the result by the memory budget alone.
        /// </summary>
        public static int ComputeCapacity(long frameSizeBytes, int idealFrameCount, double budgetFraction, int maxCapacity)
        {
            if (frameSizeBytes <= 0 || idealFrameCount <= 0)
                return MinCapacity;

            long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            long budgetBytes = (long)(availableBytes * budgetFraction);
            long budgetFrames = budgetBytes / frameSizeBytes;

            long recommended = Math.Min(budgetFrames, idealFrameCount);
            return (int)Math.Clamp(recommended, MinCapacity, Math.Max(MinCapacity, maxCapacity));
        }
    }

    /// <summary>
    /// Provides a virtualized, demand-loaded view of a large image file via a
    /// Memory-Mapped File (MMF). The backing data resides on disk; individual frames
    /// are decoded into RAM only when their index is first accessed.
    /// </summary>
    /// <typeparam name="T">Unmanaged pixel element type (e.g., <c>ushort</c>, <c>float</c>).</typeparam>
    public abstract class MmfFrames<T> : VirtualFrames<T>, IMmfFrameList
        where T : unmanaged
    {
        protected FileStream _fileStream;
        protected MemoryMappedFile _mmf;
        protected MemoryMappedViewAccessor _accessor;
        protected MemoryMappedFileAccess _accessMode;

        // Exposed as protected so derived classes (stripped and tiled layouts) can access the
        // physical layout directly. The jagged structure supports both strip-based and tile-based
        // formats: _offsets[frameIndex][stripOrTileIndex].
        protected readonly long[][] _offsets;
        protected readonly long[][] _byteCounts;

        /// <summary>
        /// Provides a mapping from the frame offsets (offsets[frameIndex][0]) to unique dummy arrays that serve as keys for ValueRangeMap.
        /// </summary>
        protected readonly Dictionary<long, T[]> _offsetToKeyMap;

        protected readonly Dictionary<T[], int> _keyToIndexMap;

        // When true, each frame is flipped vertically as it is decoded
        // (for formats whose pixel origin is at the bottom-left instead of the top-left).
        protected readonly bool _isYFlipped;

        // True when the source file's byte order differs from the host's, so raw MMF reads
        // (which bypass any format library's own decode-time byte-swapping) need an explicit
        // swap. Computed once from the caller-supplied _isBigEndian against the actual host
        // order, rather than assuming the host is little-endian.
        protected readonly bool _needsByteSwap;

        /// <summary>
        /// This class's cache is bounded/evicting by design (see <see cref="VirtualCachePolicy"/>),
        /// not working toward a "fully loaded" goal state -- so unlike the base class's dynamic
        /// default, MMF is unconditionally virtual regardless of how much of it happens to be
        /// cached right now. See <see cref="VirtualFrames{T}.IsVirtual"/> for the full rationale.
        /// </summary>
        public override bool IsVirtual => true;

        /// <summary>
        /// Initializes a new instance of the <see cref="MmfFrames{T}"/> class using the specified file.
        /// This provides virtualized frame access by mapping the file into the process's address space.
        /// </summary>
        /// <param name="filePath">The path to the file to be mapped. The file must exist, and the process must have
        /// sufficient permissions according to the specified <paramref name="access"/>.</param>
        /// <param name="offsets">A jagged array representing the starting offsets for each virtual frame.
        /// Each inner array corresponds to the physical layout of a specific frame in the file.</param>
        /// <param name="bytesCounts">A jagged array representing the data size (in bytes) for each virtual frame,
        /// matching the structure of <paramref name="offsets"/>.</param>
        /// <param name="isYFlipped">Indicates whether the Y-axis of the frames should be flipped during access. This is relevant for certain image formats where the origin is at the bottom-left instead of the top-left.</param>
        /// <param name="isBigEndian">Whether the source file's multi-byte samples are stored big-endian. Pass the
        /// actual property of the file (e.g. a TIFF reader's own byte-order flag) -- not a swap decision; the
        /// class compares it against the host's actual byte order itself. Irrelevant for <see cref="byte"/>/<see
        /// cref="sbyte"/> frames.</param>
        /// <param name="access">The access mode for the memory-mapped file.
        /// Use <see cref="MemoryMappedFileAccess.Read"/> (default) for read-only access, or
        /// <see cref="MemoryMappedFileAccess.ReadWrite"/> to enable write-back functionality to the disk.</param>
        /// <exception cref="FileNotFoundException">Thrown if the specified <paramref name="filePath"/> cannot be found.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown if the process lacks the required permissions for the requested <paramref name="access"/> mode.</exception>
        /// <exception cref="IOException">Thrown if an I/O error occurs during file opening, or if the file is locked by another process with incompatible sharing modes.</exception>
        public MmfFrames(string filePath, long[][] offsets, long[][] bytesCounts,
            bool isYFlipped, bool isBigEndian, MemoryMappedFileAccess access = MemoryMappedFileAccess.Read)
            : base(offsets.Length, filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Target file not found.", filePath);

            // SourcePath is already set by the base(offsets.Length, filePath) call above.
            _offsets = offsets;
            _byteCounts = bytesCounts;
            _accessMode = access;
            _needsByteSwap = isBigEndian != !BitConverter.IsLittleEndian;

            _fileStream = null!;
            _mmf = null!;
            _accessor = null!;
            // Mount the MMF here.
            Mount(access);

            _isYFlipped = isYFlipped;

            // ========================================================
            // Build offset→key dictionaries
            // ========================================================
            _offsetToKeyMap = new Dictionary<long, T[]>();
            _keyToIndexMap = new Dictionary<T[], int>();

            for (int frameIndex = 0; frameIndex < _offsets.Length; frameIndex++)
            {
                long[] offsetArray = _offsets[frameIndex];
                // Defensive guard: an empty offset array should never occur per TIFF spec.
                if (offsetArray == null || offsetArray.Length == 0) continue;

                // Only create a new dummy key for offsets not yet registered (new physical frame).
                if (!_offsetToKeyMap.ContainsKey(offsetArray[0])) // offset[0] is the primary key
                {
                    var key = new T[1];
                    _offsetToKeyMap[offsetArray[0]] = key; // T[1] dummy array
                    _keyToIndexMap[key] = frameIndex;
                }
            }

            // Derive an initial CacheCapacity from available memory and per-frame byte size, aiming
            // to cache the whole dataset (uniquePhysicalFrames) when that comfortably fits the
            // memory budget -- after one full read pass (e.g. building an orthogonal slice), every
            // subsequent access is then served from RAM regardless of access pattern.
            int uniquePhysicalFrames = _offsetToKeyMap.Count;
            long frameSizeBytes = SumByteCounts(_byteCounts.Length > 0 ? _byteCounts[0] : null);

            this.CacheCapacity = VirtualCachePolicy.ComputeCapacity(frameSizeBytes, uniquePhysicalFrames);
            Debug.WriteLine($"[MmfFrames] Initialized with CacheCapacity={CacheCapacity} (Unique Physical Frames: {uniquePhysicalFrames}, FrameSizeBytes={frameSizeBytes})");
        }

        /// <summary>Total bytes across every strip/tile of one frame (works uniformly for both layouts).</summary>
        private static long SumByteCounts(long[]? byteCounts)
        {
            if (byteCounts == null) return 0;
            long sum = 0;
            foreach (var b in byteCounts) sum += b;
            return sum;
        }

        protected void Unmount()
        {
            // Dispose the MMF and FileStream to release the OS-level file lock.
            // Cached T[] arrays in RAM are intentionally left intact.
            _accessor?.Dispose();
            _mmf?.Dispose();
            _fileStream?.Dispose();

            _accessor = null!;
            _mmf = null!;
            _fileStream = null!;
        }

        protected void Mount(MemoryMappedFileAccess access)
        {
            // Re-open FileStream and MMF using the current SourcePath (which may have changed after SaveAs).
            var fileAccess = (access == MemoryMappedFileAccess.ReadWrite) ? FileAccess.ReadWrite : FileAccess.Read;
            _fileStream = new FileStream(SourcePath, FileMode.Open, fileAccess, FileShare.ReadWrite);
            _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0, access, HandleInheritability.None, leaveOpen: false);
            _accessor = _mmf.CreateViewAccessor(0, 0, access);
        }

        /// <summary>
        /// Returns the unique dummy key array for the specified logical frame index,
        /// used as a reference-identity key in <c>ValueRangeMap</c>. Overrides the base
        /// (no-dedup) default with real physical-offset-based deduplication: two logical
        /// indices that share the same physical offset return the same array reference,
        /// enabling <c>ValueRangeMap</c> sharing across them.
        /// </summary>
        /// <param name="frameIndex">Zero-based logical frame index.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="frameIndex"/> is outside the valid range.
        /// </exception>
        public override T[] GetKey(int frameIndex)
        {
            if ((uint)frameIndex >= (uint)_offsets.Length)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));

            // 1. Resolve the physical offset array for this frame index.
            long[] offsets = _offsets[frameIndex];

            // 2. Return the unique dummy array keyed by the first (primary) offset.
            return _offsetToKeyMap[offsets[0]];
        }

        /// <summary>
        /// Returns the first logical frame index whose key matches <paramref name="item"/>
        /// by reference identity, or <c>-1</c> if not found.
        /// </summary>
        public override int IndexOf(T[] item)
        {
            return _keyToIndexMap.TryGetValue(item, out int index) ? index : -1;
        }

        /// <summary>
        /// Reverses the byte order of every element in <paramref name="data"/> in place. Raw MMF reads copy bytes
        /// verbatim (no format library involved to normalize them, unlike e.g. LibTiff's scanline/strip decode),
        /// so derived classes must call this themselves after reading when <see cref="_needsByteSwap"/> is set.
        /// A no-op for <see cref="byte"/>/<see cref="sbyte"/>, which have no byte order. Uses the vectorized
        /// <see cref="BinaryPrimitives.ReverseEndianness(ReadOnlySpan{ushort}, Span{ushort})"/>-family overloads
        /// via a same-size reinterpret cast, not a per-element scalar loop.
        /// </summary>
        protected static void SwapEndiannessInPlace(Span<T> data)
        {
            if (typeof(T) == typeof(ushort) || typeof(T) == typeof(short))
            {
                var s = MemoryMarshal.Cast<T, ushort>(data);
                BinaryPrimitives.ReverseEndianness(s, s);
            }
            else if (typeof(T) == typeof(uint) || typeof(T) == typeof(int) || typeof(T) == typeof(float))
            {
                var s = MemoryMarshal.Cast<T, uint>(data);
                BinaryPrimitives.ReverseEndianness(s, s);
            }
            else if (typeof(T) == typeof(ulong) || typeof(T) == typeof(long) || typeof(T) == typeof(double))
            {
                var s = MemoryMarshal.Cast<T, ulong>(data);
                BinaryPrimitives.ReverseEndianness(s, s);
            }
            // byte/sbyte: single byte, no byte order -- nothing to do.
        }

        public override bool Contains(T[] item) => throw new NotSupportedException("Contains is not supported on MmfFrames.");

        public override void Dispose()
        {
            bool wasDisposed = IsDisposed;
            base.Dispose();
            if (!wasDisposed)
            {
                _accessor?.Dispose();
                _mmf?.Dispose();
            }
        }
    }

    public class StrippedMmfFrames<T> : MmfFrames<T> where T : unmanaged
    {
        protected readonly int _width;
        protected readonly int _height;

        public StrippedMmfFrames(string path, int w, int h, long[][] offsets, long[][] byteCounts, bool isYFlipped, bool isBigEndian, MemoryMappedFileAccess access = MemoryMappedFileAccess.Read)
            : base(path, offsets, byteCounts, isYFlipped, isBigEndian, access)
        {
            _width = w; _height = h;
        }

        protected override T[]? ReadFrame(int index, CancellationToken ct)
        {
            return ReadStripsFromMMF(index, ct);
        }

        private unsafe T[]? ReadStripsFromMMF(int frameIndex, CancellationToken ct)
        {
#if VF_DEBUG
    var sw = Stopwatch.StartNew();
    long elapsed = 0; long totalTime = 0;
    var sb = new StringBuilder();
    sb.AppendLine($"[StrippedMmfFrames] Reading frame {frameIndex} from MMF as stripes... Total access count = {_accessCount}");
#endif
            T[] frameData = new T[_width * _height];
            long[] stripOffsets = _offsets[frameIndex];
            long[] stripByteCounts = _byteCounts[frameIndex];
            int sizeOfT = Unsafe.SizeOf<T>();
            long rowBytes = (long)_width * sizeOfT;
#if VF_DEBUG
    elapsed = sw.ElapsedMilliseconds; totalTime += elapsed;
    sb.AppendLine($"[StrippedMmfFrames] Prepared buffer in {elapsed} ms."); sw.Restart();
#endif

            if (!_isYFlipped)
            {
                // fast path: sequential strip layout, no vertical flip needed
                int destIndex = 0;
                for (int i = 0; i < stripOffsets.Length; i++)
                {
                    if (ct.IsCancellationRequested) return null;
                    long offset = stripOffsets[i];
                    long byteCount = stripByteCounts[i];
                    if (offset == 0 || byteCount == 0) continue;
                    int elementCount = (int)(byteCount / sizeOfT);
                    _accessor.ReadArray(offset, frameData, destIndex, elementCount);
                    destIndex += elementCount;
                }
            }
            else
            {
                // Y-flip path: file row f is written to destination row (height-1-f) — the inverse of WriteBackToDisk
                var handle = _accessor.SafeMemoryMappedViewHandle;
                byte* pBase = null;
                int currentFileRow = 0;
                try
                {
                    handle.AcquirePointer(ref pBase);
                    fixed (T* pDest = frameData)
                    {
                        for (int i = 0; i < stripOffsets.Length; i++)
                        {
                            if (ct.IsCancellationRequested)
                                return null;

                            long offset = stripOffsets[i];
                            long byteCount = stripByteCounts[i];
                            if (offset == 0 || byteCount == 0)
                                continue;

                            int rowsInStrip = (int)(byteCount / rowBytes);
                            // Guard: the last strip may be shorter than a full strip height.
                            rowsInStrip = Math.Min(rowsInStrip, _height - currentFileRow);
                            for (int rowInStrip = 0; rowInStrip < rowsInStrip; rowInStrip++)
                            {
                                if (ct.IsCancellationRequested) return null;
                                byte* pSrcRow = pBase + offset + (long)rowInStrip * rowBytes;
                                int destRow = (_height - 1) - currentFileRow;
                                T* pDestRow = pDest + (long)destRow * _width;
                                Buffer.MemoryCopy(pSrcRow, pDestRow, rowBytes, rowBytes);
                                currentFileRow++;
                            }
                        }
                    }
                }
                finally
                {
                    if (pBase != null) handle.ReleasePointer(); // always release, even on early null return
                }
            }
            if (_needsByteSwap)
                SwapEndiannessInPlace(frameData.AsSpan());
#if VF_DEBUG
    elapsed = sw.ElapsedMilliseconds; totalTime += elapsed;
    sb.AppendLine($"[StrippedMmfFrames] Read done in {elapsed} ms. Total={totalTime} ms.");
    Trace.WriteLine(sb.ToString()); sw.Stop();
#endif
            return frameData;
        }
    }

    public class TiledMmfFrames<T> : MmfFrames<T> where T : unmanaged
    {
        private readonly int _imageWidth;
        private readonly int _imageHeight;
        private readonly int _tileWidth;
        private readonly int _tileLength;

        public TiledMmfFrames(string path, int imgW, int imgH, int tileW, int tileH, long[][] offsets, long[][] byteCounts, bool isYFlipped, bool isBigEndian)
            : base(path, offsets, byteCounts, isYFlipped, isBigEndian)
        {
            _imageWidth = imgW;
            _imageHeight = imgH;
            _tileWidth = tileW;
            _tileLength = tileH;
        }

        protected override T[]? ReadFrame(int index, CancellationToken ct)
        {
            return ReadAndStitchTilesFromMMF(index, ct);
        }

        private T[]? ReadAndStitchTilesFromMMF(int frameIndex, CancellationToken ct)
        {
#if VF_DEBUG
            var sw = Stopwatch.StartNew();
            long elapsed = 0;
            long totalTime = 0;
            var sb = new StringBuilder();
            sb.AppendLine($"[TiledMmfFrames] Reading frame {frameIndex} from MMF as tiles ... Total access count = {_accessCount}");
#endif

            // Output buffer: the fully assembled frame in row-major order.
            T[] frameData = new T[_imageWidth * _imageHeight];

            // Tile layout for the requested frame.
            long[] tileOffsets = _offsets[frameIndex];
            long[] tileByteCounts = _byteCounts[frameIndex];

            // Number of tile columns and rows (ceiling division to cover the full image).
            int tilesAcross = (_imageWidth + _tileWidth - 1) / _tileWidth;
            int tilesDown = (_imageHeight + _tileLength - 1) / _tileLength;

            // Reusable scratch buffer for one tile; allocated once and reused per iteration.
            T[] tileBuffer = new T[_tileWidth * _tileLength];
            int sizeOfT = Unsafe.SizeOf<T>(); // byte size of T (e.g., 2 for ushort)

#if VF_DEBUG
            elapsed = sw.ElapsedMilliseconds;
            totalTime += elapsed;
            sb.AppendLine($"[TiledMmfFrames] Prepared frameData and tileBuffer, calculated tile grid in {elapsed} ms.");
            sw.Restart();
#endif
            // Iterate over every tile and stitch it into the output frame buffer.
            for (int tileIndex = 0; tileIndex < tileOffsets.Length; tileIndex++)
            {
                if (ct.IsCancellationRequested) return null;

                long offset = tileOffsets[tileIndex];
                long byteCount = tileByteCounts[tileIndex];

                if (offset == 0 || byteCount == 0) continue; // skip empty tiles (rare but valid per TIFF spec)

                // 1. Read one tile's raw data from the MMF directly into the scratch buffer.
                int elementCount = (int)(byteCount / sizeOfT);
                _accessor.ReadArray(offset, tileBuffer, 0, elementCount);

                if (_needsByteSwap)
                    SwapEndiannessInPlace(tileBuffer.AsSpan(0, elementCount));

                // 2. Compute the tile's grid position (column and row index in the tile grid).
                int tileCol = tileIndex % tilesAcross;
                int tileRow = tileIndex / tilesAcross;

                // Top-left pixel coordinate of this tile in the full image.
                int startX = tileCol * _tileWidth;
                int startY = tileRow * _tileLength;

                // 3. Clip to image boundaries: edge tiles carry padding that must be discarded.
                int actualTileWidth = Math.Min(_tileWidth, _imageWidth - startX);
                int actualTileHeight = Math.Min(_tileLength, _imageHeight - startY);

                // 4. Row-by-row stitch: copy each tile row into the correct position in the output buffer.
                for (int y = 0; y < actualTileHeight; y++)
                {
                    if (ct.IsCancellationRequested) return null;
                    int srcOffset = y * _tileWidth;

                    // If Y-flipped, mirror the destination row index vertically.
                    int destRow = _isYFlipped ? (_imageHeight - 1 - startY - y) : (startY + y);
                    int destOffset = destRow * _imageWidth + startX;

                    tileBuffer.AsSpan(srcOffset, actualTileWidth)
                              .CopyTo(frameData.AsSpan(destOffset, actualTileWidth));
                }
            }
#if VF_DEBUG
            elapsed = sw.ElapsedMilliseconds;
            totalTime += elapsed;
            sb.AppendLine($"[TiledMmfFrames] Frame data read and assembled in {elapsed} ms");
            sb.AppendLine($"[TiledMmfFrames] Total time to read frame {frameData}: {totalTime} ms");
            Trace.WriteLine(sb.ToString());
            sw.Stop();
#endif
            return frameData;
        }

    }

}
