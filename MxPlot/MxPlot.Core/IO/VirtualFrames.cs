//Evaluate the compuational time of each step and output to console if FFT_DEBUG is defined
//#define VF_DEBUG
//#define VF_CANCEL_NOTICE

using MxPlot.Core.IO;
using MxPlot.Core.IO.CacheStrategies;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Text;


namespace MxPlot.Core.IO
{

    /// <summary>
    /// Represents a point-in-time view of the cache and loader state.
    /// Use this to diagnose if the prefetching strategy is keeping up with the user's navigation.
    /// </summary>
    public record CacheSnapshot(List<int> CachedIndices, List<int> PreloadingIndices);

    public interface IVirtualFrameList
    {
        ICacheStrategy CacheStrategy { get; set; }
        int CacheCapacity { get; set; }
        bool IsOwned { get; set; }
        bool IsDisposed { get; }
        string FilePath { get; }

        /// <summary>Captures a diagnostic snapshot of the current cache state.</summary>
        CacheSnapshot GetCacheStatus();
    }

    /// <summary>
    /// Provides a virtualized, demand-loaded view of a large image file via a
    /// Memory-Mapped File (MMF). The backing data resides on disk; individual frames
    /// are decoded into RAM only when their index is first accessed.
    /// </summary>
    /// <typeparam name="T">Unmanaged pixel element type (e.g., <c>ushort</c>, <c>float</c>).</typeparam>
    public abstract class VirtualFrames<T>
        : IVirtualFrameList, ILazyFrameList, IList<T[]>, IFrameKeyProvider<T>, IDisposable
        where T : unmanaged
    {
#if VF_DEBUG
        protected long _accessCount = 0; 
#endif

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

        /// <summary>
        /// Indicates whether this instance is owned by an external component responsible for managing its disposal.
        /// </summary>
        /// <remarks>If the value is <see langword="true"/>, an external owner is responsible for calling
        /// <see cref="IDisposable.Dispose"/> on this instance. If <see langword="false"/>, this instance manages its
        /// own disposal. This flag should be set appropriately to avoid resource leaks or premature disposal.</remarks>
        private bool _isOwned = false;

        private int _lastAccessedIndex = 0;

        private readonly int _instanceId = VirtualFramesIdProvider.Next(); // unique instance id used for diagnostics / ToString
        private ICacheStrategy _cacheStrategy = new NeighborStrategy();     // default prefetch / eviction strategy

        #region private fields for caching and prefetching (optional, can be extended with ICacheStrategy)

        protected readonly Dictionary<int, T[]> _cache = new();
        protected readonly LinkedList<int> _lruList = new(); // doubly-linked list for LRU eviction ordering (head = most-recently-used)
        public int CacheCapacity { get; set; } = 16; // maximum number of decoded frames held in the RAM cache at once


        protected readonly object _cacheLock = new object();
        private readonly SemaphoreSlim _ioSemaphore = new SemaphoreSlim(1, 1); // serialises disk I/O: at most one read at a time
        private readonly HashSet<int> _preloadingIndices = new HashSet<int>(); // tracks in-flight preloads to prevent duplicate tasks
        private CancellationTokenSource _prefetchCts = new CancellationTokenSource();

        public ICacheStrategy CacheStrategy
        {
            get { return _cacheStrategy; }
            set
            {
                // Unsubscribe from the old strategy to prevent memory leaks.
                if (_cacheStrategy != null)
                {
                    _cacheStrategy.StrategyChanged -= OnStrategyChanged;
                }
                _cacheStrategy = value;

                if (_cacheStrategy != null) // subscribe to the new strategy
                {
                    _cacheStrategy.StrategyChanged += OnStrategyChanged;
                }
                InvalidateCachePriorities(); // force an immediate re-evaluation with the new strategy
            }

        }
        private void OnStrategyChanged(object? sender, EventArgs e)
        {
            // A strategy property changed (e.g., TargetAxis); rebuild the cache queue accordingly.
            InvalidateCachePriorities();
        }

        private void InvalidateCachePriorities()
        {
            if (IsDisposed) return;

            lock (_cacheLock)
            {
                // 1. Cancel all in-flight prefetch tasks and reset the cancellation token.
                _prefetchCts.Cancel();
                _prefetchCts.Dispose();
                _prefetchCts = new CancellationTokenSource();
                _preloadingIndices.Clear();

                if (CacheStrategy == null) return;

                // 2. Rebuild the LRU list according to the new strategy:
                //    high-priority frames move toward the head (most-recently-used, protected),
                //    low-priority frames move toward the tail (eviction candidates).
                var highPriority = new List<int>();
                var lowPriority = new List<int>();

                foreach (var idx in _lruList)
                {
                    if (CacheStrategy.IsHighPriority(idx))
                        highPriority.Add(idx);
                    else
                        lowPriority.Add(idx);
                }

                _lruList.Clear();

                // AddLast builds the list so that highPriority items become the head.
                foreach (var idx in highPriority) _lruList.AddLast(idx);
                foreach (var idx in lowPriority) _lruList.AddLast(idx);
            }

            // 3. Kick off prefetch from the last-accessed index under the new strategy.
            SchedulePreload(_lastAccessedIndex);
        }

        /// <summary>
        /// Captures a diagnostic snapshot of the current cache and background loader state.
        /// </summary>
        /// <returns>
        /// A <see cref="CacheSnapshot"/> containing two key lists for performance analysis:
        /// <list type="bullet">
        ///   <item>
        ///     <term>CachedIndices</term>
        ///     <description>
        ///       Frames currently in RAM. The order reflects the LRU (Least Recently Used) stack:
        ///       The first element is the "freshest" (most protected), and the last element is the next candidate for eviction.
        ///     </description>
        ///   </item>
        ///   <item>
        ///     <term>PreloadingIndices</term>
        ///     <description>
        ///       Frames currently being fetched from disk. These are "in-flight" tasks 
        ///       working to move data into the <c>CachedIndices</c>.
        ///     </description>
        ///   </item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// <b>Diagnostic Guide - How to interpret the state:</b>
        /// <para/>
        /// 1. <b>System Efficiency:</b> If the currently displayed frame index is in <c>CachedIndices</c>, the UI is stutter-free (Cache Hit). 
        /// If it is in <c>PreloadingIndices</c>, the prefetcher is working but hasn't finished yet (Potential Stutter).
        /// <para/>
        /// 2. <b>IO Health:</b> A high count of <c>PreloadingIndices</c> that don't transition quickly to <c>CachedIndices</c> 
        /// indicates a slow storage device or a bottleneck in the disk I/O thread.
        /// <para/>
        /// 3. <b>Strategy Accuracy:</b> Compare the indices in <c>PreloadingIndices</c> with the user's scroll direction. 
        /// If they don't match, the current <see cref="ICacheStrategy"/> is mispredicting movement.
        /// <para/>
        /// 4. <b>Eviction Risk:</b> If the current frame is near the end of the <c>CachedIndices</c> list, 
        /// it is at risk of being purged from memory soon.
        /// </remarks>
        public CacheSnapshot GetCacheStatus()
        {
            lock (_cacheLock)
            {
                return new CacheSnapshot(
                    _lruList.ToList(),
                    _preloadingIndices.ToList()
                );
            }
        }

        #endregion

        /// <summary>
        /// Gets/Sets a value indicating whether the virtual list is owned by the other component (e.g., a MatrixData instance). 
        /// If true, there is an owner having a responsibility to dispose this instance.
        /// </summary>
        public bool IsOwned
        {
            get => _isOwned;
            set
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(VirtualFrames<T>), "Cannot change ownership of a disposed VirtualFrameList.");
                if (_isOwned == true) // Cannot change ownership once set to true, to prevent accidental ownership transfer
                    throw new InvalidOperationException("This VirtualFrameList is already owned. Ownership cannot be changed.");
                _isOwned = value;
            }
        }

        public bool IsDisposed { get; private set; }

        /// <summary>
        /// File path that was set in the constructor.
        /// </summary>
        public string FilePath { get; protected set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="VirtualFrames{T}"/> class using the specified file.
        /// This provides virtualized frame access by mapping the file into the process's address space.
        /// </summary>
        /// <param name="filePath">The path to the file to be mapped. The file must exist, and the process must have 
        /// sufficient permissions according to the specified <paramref name="access"/>.</param>
        /// <param name="offsets">A jagged array representing the starting offsets for each virtual frame. 
        /// Each inner array corresponds to the physical layout of a specific frame in the file.</param>
        /// <param name="bytesCounts">A jagged array representing the data size (in bytes) for each virtual frame, 
        /// matching the structure of <paramref name="offsets"/>.</param>
        /// <param name="isYFlipped">Indicates whether the Y-axis of the frames should be flipped during access. This is relevant for certain image formats where the origin is at the bottom-left instead of the top-left.</param>
        /// <param name="access">The access mode for the memory-mapped file. 
        /// Use <see cref="MemoryMappedFileAccess.Read"/> (default) for read-only access, or 
        /// <see cref="MemoryMappedFileAccess.ReadWrite"/> to enable write-back functionality to the disk.</param>
        /// <exception cref="FileNotFoundException">Thrown if the specified <paramref name="filePath"/> cannot be found.</exception>
        /// <exception cref="UnauthorizedAccessException">Thrown if the process lacks the required permissions for the requested <paramref name="access"/> mode.</exception>
        /// <exception cref="IOException">Thrown if an I/O error occurs during file opening, or if the file is locked by another process with incompatible sharing modes.</exception>
        public VirtualFrames(string filePath, long[][] offsets, long[][] bytesCounts,
            bool isYFlipped, MemoryMappedFileAccess access = MemoryMappedFileAccess.Read)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Target file not found.", filePath);

            FilePath = filePath;
            _offsets = offsets;
            _byteCounts = bytesCounts;
            _accessMode = access;

            /*
            var fileAccess = (access == MemoryMappedFileAccess.ReadWrite) ? FileAccess.ReadWrite : FileAccess.Read;
            _fileStream = new FileStream(filePath, FileMode.Open, fileAccess, FileShare.ReadWrite);
            _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0, access, HandleInheritability.None, leaveOpen: false);
            _accessor = _mmf.CreateViewAccessor(0, 0, access);
            */
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

            // Derive an initial CacheCapacity from the number of unique physical frames.
            // Physical unique frame count (directly proportional to the number of disk reads).
            int uniquePhysicalFrames = _offsetToKeyMap.Count;

            // 1. Minimum guard: always hold enough for at least current ± 1 × channel count.
            int minCapacity = 16;

            // 2. Recommended: ~10-20% of all physical frames, or enough for one full Z-stack.
            int recommendedCapacity = Math.Max(minCapacity, uniquePhysicalFrames / 5);

            // 3. Upper bound: prevent unbounded memory growth (cap at 1024 frames).
            int maxSafeCapacity = 1024;

            this.CacheCapacity = Math.Clamp(recommendedCapacity, minCapacity, maxSafeCapacity);
            Debug.WriteLine($"[VirtualFrameList] Initialized with CacheCapacity={CacheCapacity} (Unique Physical Frames: {uniquePhysicalFrames})");
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
            // Re-open FileStream and MMF using the current FilePath (which may have changed after SaveAs).
            var fileAccess = (access == MemoryMappedFileAccess.ReadWrite) ? FileAccess.ReadWrite : FileAccess.Read;
            _fileStream = new FileStream(FilePath, FileMode.Open, fileAccess, FileShare.ReadWrite);
            _mmf = MemoryMappedFile.CreateFromFile(_fileStream, null, 0, access, HandleInheritability.None, leaveOpen: false);
            _accessor = _mmf.CreateViewAccessor(0, 0, access);
        }

        /// <summary>
        /// Returns the unique dummy key array for the specified logical frame index,
        /// used as a reference-identity key in <c>ValueRangeMap</c>.
        /// </summary>
        /// <param name="frameIndex">Zero-based logical frame index.</param>
        /// <returns>
        /// A <c>T[1]</c> dummy array whose object identity uniquely identifies the underlying
        /// physical frame. Two logical indices that share the same physical offset return the
        /// same array reference, enabling <c>ValueRangeMap</c> sharing across them.
        /// </returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown when <paramref name="frameIndex"/> is outside the valid range.
        /// </exception>
        public T[] GetKey(int frameIndex)
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
        public int IndexOf(T[] item)
        {
            return _keyToIndexMap.TryGetValue(item, out int index) ? index : -1;
        }

#if VF_DEBUG
        // 統計用カウンタ
        private long _cacheHitCount = 0;
        private long _cacheMissCount = 0;
        private long _prefetchSuccessCount = 0;

        public void PrintCacheStats()
        {
            lock (_cacheLock)
            {
                long total = _cacheHitCount + _cacheMissCount;
                double hitRate = total == 0 ? 0 : (double)_cacheHitCount / total * 100;

                // Note: whether the cache is full (pinned at the ceiling) is also diagnostically important.
                Trace.WriteLine($"[VirtualFrameList] --Cache Stats--
            }
        }
#endif


        // ==========================================
        // IList<T[]> core implementation (read)
        // ==========================================

        public int Count => _offsets.GetLength(0); // logical frame count equals the number of entries in the offset table

        /// <summary>
        /// Gets a value indicating whether the <see cref="T:System.Collections.Generic.IList`1" /> is read-only.
        /// </summary>
        /// <remarks>
        /// <strong>Architectural Note (Pragmatic Hack):</strong><br/>
        /// In standard C# semantics, this flag indicates whether the list structure itself (adding, removing, or replacing elements) is immutable. 
        /// However, in this framework, we pragmatically repurpose this flag to also represent the mutability of the underlying data layer 
        /// (i.e., whether the contents of the retrieved array <c>T[]</c> can be modified and reflected in the actual data store).
        /// 
        /// <c>MatrixData.GetArray()</c> relies on this property to control cache invalidation:
        /// <list type="bullet">
        /// <item>
        /// <term><c>false</c> (On-Memory or Writable MMF)</term>
        /// <description>Assumes the retrieved raw array (<c>T[]</c>) will be modified. It defensively invalidates caches (such as Min/Max values).</description>
        /// </item>
        /// <item>
        /// <term><c>true</c> (Read-Only MMF)</term>
        /// <description>Assumes no valid write operations will occur (since modifying the array won't affect the underlying read-only file). It safely skips cache invalidation and avoids unnecessary exceptions. Any modification to the array in this state is strictly at the caller's own risk.</description>
        /// </item>
        /// </list>
        /// 
        /// <strong>Note:</strong> For strict read-only access and zero-allocation performance, use <c>AsSpan()</c> or <c>AsMemory()</c> instead of <c>GetArray()</c>.
        /// </remarks>
        public virtual bool IsReadOnly => true;

        public virtual T[] this[int index]
        {
            get
            {
                if (IsDisposed) throw new ObjectDisposedException(nameof(VirtualFrames<T>));
                _lastAccessedIndex = index;

#if VF_DEBUG
                _accessCount++;
#endif
                T[]? data;
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(index, out data))
                    {
#if VF_DEBUG
                        _cacheHitCount++;
#endif
                        _lruList.Remove(index);
                        _lruList.AddFirst(index);
                        // Even on a cache hit, trigger prefetch for neighbouring frames.
                        SchedulePreload(index);
                        return data;
                    }
                }
#if VF_DEBUG
                _cacheMissCount++;
#endif
                // Cache miss: synchronous read is unavoidable here.
                data = ReadFrameFromSource(index, CancellationToken.None);
                UpdateCache(index, data);

                // UpdateCache skips insertion when the index is already present.
                // If a background preload stored a different array reference first,
                // the local 'data' variable would be a dangling orphan. Always
                // return the canonical array that is actually in the cache.
                lock (_cacheLock)
                {
                    if (_cache.TryGetValue(index, out T[]? canonical))
                        data = canonical;
                }

                // Start prefetching neighbours after the read completes.
                SchedulePreload(index);
#if VF_DEBUG
                PrintCacheStats();
#endif
                return data;
            }

            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Reads and assembles one logical frame from the backing store.
        /// Implemented by concrete subclasses for each physical layout (stripped or tiled).
        /// </summary>
        /// <param name="index">Zero-based logical frame index.</param>
        /// <param name="ct">
        /// Cancellation token. Implementations should return <see langword="null"/>
        /// (rather than throwing) when cancellation is requested.
        /// </param>
        /// <returns>
        /// A <c>T[]</c> of length <c>Width × Height</c> in row-major order,
        /// or <see langword="null"/> if the operation was cancelled.
        /// </returns>
        protected abstract T[]? ReadFrameFromSource(int index, CancellationToken ct);

        /// <summary>
        /// Called after a frame has been evicted from the cache, outside <c>_cacheLock</c>.
        /// Override in derived classes to perform post-eviction work such as flushing dirty data.
        /// The base implementation is a no-op.
        /// </summary>
        /// <param name="frameIndex">Index of the evicted frame.</param>
        /// <param name="frameData">The evicted <c>T[]</c> array.</param>
        protected virtual void OnFrameEvicted(int frameIndex, T[] frameData)
        {
            // Do nothing in the base class, but derived classes can override this to perform actions when a frame is evicted from the cache.
        }

        /// <summary>
        /// Override hook that allows a derived class to veto eviction of a specific frame.
        /// <see cref="WritableVirtualStrippedFrames{T}"/> overrides this to protect dirty (unsaved) frames.
        /// </summary>
        /// <remarks>
        /// Always called while <c>_cacheLock</c> is held inside <see cref="UpdateCache"/>.
        /// Implementations must not block or acquire additional locks.
        /// </remarks>
        /// <param name="frameIndex">Index of the candidate frame to evict.</param>
        /// <returns>
        /// <see langword="true"/> if the frame may be evicted;
        /// <see langword="false"/> to keep it in the cache.
        /// </returns>
        protected virtual bool CanEvict(int frameIndex) => true;

        /// <summary>
        /// Inserts a decoded frame into the LRU cache, evicting the least-recently-used
        /// frame(s) as needed to stay within <see cref="CacheCapacity"/>.
        /// </summary>
        /// <remarks>
        /// If <paramref name="index"/> is already in the cache this method is a no-op,
        /// so concurrent preloads that finish after a synchronous read are safely discarded.
        /// <see cref="OnFrameEvicted"/> is called outside the lock to allow derived classes
        /// to perform I/O (e.g., flushing dirty frames) without blocking the cache.
        /// </remarks>
        private void UpdateCache(int index, T[] data)
        {
            // Collect eviction info inside the lock; call OnFrameEvicted outside to avoid
            // derived-class I/O from blocking _cacheLock.
            List<(int evictedIndex, T[] evictedData)>? pendingEvictions = null;

            lock (_cacheLock)
            {
                if (_cache.ContainsKey(index)) return;

                while (_cache.Count >= CacheCapacity && _lruList.Count > 0)
                {
                    // Walk the LRU list from the tail (least-recently-used) to find an eviction candidate.
                    var node = _lruList.Last;
                    LinkedListNode<int>? candidate = null;

                    while (node != null)
                    {
                        if (CanEvict(node.Value))
                        {
                            // Evictable: prefer low-priority frames; fall back to the head as a last resort.
                            if (CacheStrategy == null || !CacheStrategy.IsHighPriority(node.Value) || node == _lruList.First)
                            {
                                candidate = node;
                                break;
                            }
                        }
                        // Dirty or high-priority frame: skip and try the next newer one.
                        node = node.Previous;
                    }

                    if (candidate == null) break; // no evictable candidate (e.g., all frames are dirty)

                    int evictedIndex = candidate.Value;
                    T[] evictedData = _cache[evictedIndex];

                    _cache.Remove(evictedIndex);
                    _lruList.Remove(candidate);

                    // Only collect here; actual I/O happens outside the lock below.
                    pendingEvictions ??= new List<(int, T[])>();
                    pendingEvictions.Add((evictedIndex, evictedData));
                }

                _cache[index] = data;
                _lruList.AddFirst(index);
            }

            // Call OnFrameEvicted after releasing the lock so derived classes
            // can safely perform I/O without blocking _cacheLock.
            if (pendingEvictions != null)
                foreach (var (ei, ed) in pendingEvictions)
                    OnFrameEvicted(ei, ed);
        }

        private void SchedulePreload(int currentIndex)
        {
            if (CacheStrategy == null) return;

            // Compute preload targets outside the lock; dimension calculations in the
            // strategy may be non-trivial.
            var newTargetSet = new HashSet<int>(CacheStrategy.GetPreloadIndices(currentIndex, Count));

            lock (_cacheLock)
            {
                // Check whether any in-flight preload is no longer in the new target set
                // (i.e., the navigation context changed and those frames are no longer needed).
                // Exclude currentIndex itself since it may be mid-synchronous-read.
                bool hasStalePreloads = _preloadingIndices
                    .Any(i => i != currentIndex && !newTargetSet.Contains(i));

                if (hasStalePreloads)
                {
                    // Cancel only when the context has changed (e.g., user switched to a different axis).
                    _prefetchCts.Cancel();
                    _prefetchCts.Dispose();
                    _prefetchCts = new CancellationTokenSource();
                    _preloadingIndices.Clear();
#if VF_DEBUG
                    Trace.WriteLine($"[VirtualFrameList.SchedulePreload] Stale preloads detected → Canceled and restarted from index {currentIndex}.");
#endif
                }

                var token = _prefetchCts.Token;

                foreach (var target in newTargetSet)
                {
                    if (_cache.ContainsKey(target) || _preloadingIndices.Contains(target))
                        continue;
                    _preloadingIndices.Add(target);
                    _ = PreloadInternalAsync(target, token);
                }
            }
        }

        /// <summary>
        /// Background task that acquires the I/O semaphore, reads one frame from disk,
        /// and stores the result in the cache. Designed for silent cancellation:
        /// cancellation is treated as a normal exit rather than an exception.
        /// </summary>
        /// <remarks>
        /// The semaphore wait is raced against <c>Task.Delay(-1, ct)</c> so that
        /// cancellation during the wait returns immediately without throwing
        /// <see cref="OperationCanceledException"/>. If the semaphore is acquired just
        /// after cancellation, a continuation ensures it is released immediately to prevent leaks.
        /// </remarks>
        private async Task PreloadInternalAsync(int index, CancellationToken ct)
        {
            try
            {
                // Race semaphore acquisition against cancellation to exit silently.
                // The direct await _ioSemaphore.WaitAsync(ct) pattern is intentionally avoided
                // because it surfaces OperationCanceledException in the debug output.
                //await _ioSemaphore.WaitAsync(ct);

                Task waitTask = _ioSemaphore.WaitAsync();
                // Race "semaphore acquired" vs "cancelled".
                // Task.Delay(-1, ct) never completes unless cancelled, so whichever wins first exits.
                if (await Task.WhenAny(waitTask, Task.Delay(-1, ct)) != waitTask)
                {
#if VF_CANCEL_NOTICE
                    // Primary silent-exit route.
                    Trace.WriteLine($"[VirtualFrameList.PreloadInternalAsync] Frame {index}: Canceled during semaphore wait (New request arrived).");
#endif
                    // Cancellation won the race. If waitTask eventually acquires the semaphore,
                    // schedule a continuation to release it immediately (leak prevention).
                    _ = waitTask.ContinueWith(t =>
                    {
                        if (t.Status == TaskStatus.RanToCompletion) _ioSemaphore.Release();
                    });
                    return; // exit silently — no exception
                }

                try
                {
                    if (ct.IsCancellationRequested)
                    {
#if VF_CANCEL_NOTICE
                        Trace.WriteLine($"[VirtualFrameList.PreloadInternalAsync] Frame {index}: Canceled after acquiring semaphore.");
#endif
                        return;
                    }

                    // Offload the blocking disk read to a thread-pool thread.
                    var data = await Task.Run(() => ReadFrameFromSource(index, ct), ct);

                    if (ct.IsCancellationRequested)
                    {
#if VF_CANCEL_NOTICE
                        Trace.WriteLine($"[VirtualFrameList.PreloadInternalAsync] Frame {index}: Canceled after IO complete (Discarding data).");
#endif
                        return;
                    }
                    if (data != null)
                    {
                        UpdateCache(index, data);
#if VF_DEBUG
                        Interlocked.Increment(ref _prefetchSuccessCount); 
#endif
                    }
                }
                finally
                {
                    _ioSemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Cancellation is normal flow. The design eliminates most paths that raise this
                // exception, so reaching here is unexpected but harmless.
#if VF_DEBUG || DEBUG
                Trace.WriteLine($"[VirtualFrameList] Catch OperationCanceledException: Preload for frame {index} was canceled.");
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VirtualFrameList] Preload Error: {ex.Message}");
            }
            finally
            {
                lock (_cacheLock) { _preloadingIndices.Remove(index); }
            }
        }


        // ==========================================
        // IList<T[]> enumeration
        // ==========================================

        public IEnumerator<T[]> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i]; // each call goes through the indexer (cache/prefetch-aware)
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ==========================================
        // IDisposable implementation
        // ==========================================
        public virtual void Dispose()
        {
            if (!IsDisposed)
            {
#if VF_DEBUG
                Trace.WriteLine("--- Final Cache Statistics ---");
                PrintCacheStats();
#endif
                lock (_cacheLock)
                {
                    _prefetchCts.Cancel(); // abort any in-flight prefetch tasks
                    _cache.Clear();
                    _lruList.Clear();
                }
                _ioSemaphore.Dispose();
                _accessor?.Dispose();
                _mmf?.Dispose();
                IsDisposed = true;

#if VF_DEBUG || DEBUG
                Trace.WriteLine($"[VirtualFrameList] Disposed. obj={this}");
#endif
            }
        }

        public override string? ToString()
        {
            try
            {
                var className = GetType().Name.Split('`')[0];
                var typeName = typeof(T) switch
                {
                    var t when t == typeof(double) => "double",
                    var t when t == typeof(ushort) => "ushort",
                    var t when t == typeof(short) => "short",
                    var t when t == typeof(byte) => "byte",
                    var t when t == typeof(float) => "float",
                    var t when t == typeof(int) => "int",
                    var t when t == typeof(uint) => "uint",
                    var t when t == typeof(long) => "long",
                    var t when t == typeof(ulong) => "ulong",
                    var t when t == typeof(sbyte) => "sbyte",
                    _ => typeof(T).Name
                };

                return $"{className}<{typeName}>#{_instanceId}";
            }
            catch (Exception ex)
            {
                return base.ToString() + " with " + ex.Message;
            }
        }

        // ==========================================
        // IList<T[]> mutation methods (all unsupported)
        // The virtual list is a read-only view over an existing file; structural mutations are not permitted.
        // ==========================================
        public void Add(T[] item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, T[] item) => throw new NotSupportedException();
        public bool Remove(T[] item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public bool Contains(T[] item) => throw new NotSupportedException("Contains is not supported on VirtualFrameList.");

        public void CopyTo(T[][] array, int arrayIndex) => throw new NotSupportedException("Use explicit loop for copying to avoid memory exhaustion.");
    }

    internal static class VirtualFramesIdProvider
    {
        private static int _instanceCounter = 0;
        public static int Next() => Interlocked.Increment(ref _instanceCounter);
    }

    public class VirtualStrippedFrames<T> : VirtualFrames<T> where T : unmanaged
    {
        protected readonly int _width;
        protected readonly int _height;
        //private readonly int _frameLength; // element count per frame (Width × Height)

        public VirtualStrippedFrames(string path, int w, int h, long[][] offsets, long[][] byteCounts, bool isYFlipped, MemoryMappedFileAccess access = MemoryMappedFileAccess.Read)
            : base(path, offsets, byteCounts, isYFlipped, access)
        {
            _width = w; _height = h;
            //_frameLength = w * h;
        }

        protected override T[]? ReadFrameFromSource(int index, CancellationToken ct)
        {
            return ReadStripsFromMMF(index, ct);
        }

        private unsafe T[]? ReadStripsFromMMF(int frameIndex, CancellationToken ct)
        {
#if VF_DEBUG
    var sw = Stopwatch.StartNew();
    long elapsed = 0; long totalTime = 0;
    var sb = new StringBuilder();
    sb.AppendLine($"[VirtualStrippedFrameList] Reading frame {frameIndex} from MMF as stripes... Total access count = {_accessCount}");
#endif
            T[] frameData = new T[_width * _height];
            long[] stripOffsets = _offsets[frameIndex];
            long[] stripByteCounts = _byteCounts[frameIndex];
            int sizeOfT = Unsafe.SizeOf<T>();
            long rowBytes = (long)_width * sizeOfT;
#if VF_DEBUG
    elapsed = sw.ElapsedMilliseconds; totalTime += elapsed;
    sb.AppendLine($"[VirtualStrippedFrameList] Prepared buffer in {elapsed} ms."); sw.Restart();
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
#if VF_DEBUG
    elapsed = sw.ElapsedMilliseconds; totalTime += elapsed;
    sb.AppendLine($"[VirtualStrippedFrameList] Read done in {elapsed} ms. Total={totalTime} ms.");
    Trace.WriteLine(sb.ToString()); sw.Stop();
#endif
            return frameData;
        }
    }

    public class VirtualTiledFrames<T> : VirtualFrames<T> where T : unmanaged
    {
        private readonly int _imageWidth;
        private readonly int _imageHeight;
        private readonly int _tileWidth;
        private readonly int _tileLength;

        public VirtualTiledFrames(string path, int imgW, int imgH, int tileW, int tileH, long[][] offsets, long[][] byteCounts, bool isYFlipped)
            : base(path, offsets, byteCounts, isYFlipped)
        {
            _imageWidth = imgW;
            _imageHeight = imgH;
            _tileWidth = tileW;
            _tileLength = tileH;
        }

        protected override T[]? ReadFrameFromSource(int index, CancellationToken ct)
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
            sb.AppendLine($"[VirtualTiledFrameList] Reading frame {frameIndex} from MMF as tiles ... Total access count = {_accessCount}");
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
            sb.AppendLine($"[VirtualTiledFrameList] Prepared frameData and tileBuffer, calculated tile grid in {elapsed} ms.");
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
            sb.AppendLine($"[VirtualTiledFrameList] Frame data read and assembled in {elapsed} ms");
            sb.AppendLine($"[VirtualTiledFrameList] Total time to read frame {frameData}: {totalTime} ms");
            Trace.WriteLine(sb.ToString());
            sw.Stop();
#endif
            return frameData;
        }

    }

}
