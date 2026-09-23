//Evaluate the compuational time of each step and output to console if FFT_DEBUG is defined
//#define VF_DEBUG
//#define VF_CANCEL_NOTICE

using MxPlot.Core.IO.CacheStrategies;
using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;


namespace MxPlot.Core.IO
{

    /// <summary>
    /// Represents a point-in-time view of the cache and loader state.
    /// Use this to diagnose if the prefetching strategy is keeping up with the user's navigation.
    /// </summary>
    public record CacheSnapshot(List<int> CachedIndices, List<int> PreloadingIndices);

    /// <summary>
    /// Abstract skeleton for a demand-loaded, LRU-cached view over <c>Count</c> logical frames,
    /// independent of how a missing frame is actually materialized. Concrete subclasses implement
    /// <see cref="ReadFrame"/>: <see cref="MmfFrames{T}"/> reads raw bytes from a memory-mapped
    /// file, <c>TiffDecodedFrames&lt;T&gt;</c> (MxPlot.Extensions.Tiff) runs a real format
    /// decoder, and a chunk-routing backend can resolve/fetch a chunk and slice out one frame --
    /// none of that is this class's concern.
    /// </summary>
    /// <remarks>
    /// <see cref="GetKey"/>/<see cref="IndexOf"/> are virtual with a trivial (no physical-frame
    /// deduplication) default here, because whether two logical indices can legitimately point at
    /// the same physical data is backend-specific -- <see cref="MmfFrames{T}"/> overrides this with
    /// real offset-based deduplication. <see cref="IsOwned"/> lives here (not on
    /// <see cref="IMmfFrameList"/>) because the double-dispose hazard it guards against -- multiple
    /// <c>MatrixData&lt;T&gt;</c> instances sharing this same backing list by reference (e.g. via
    /// <c>Reorder(deepCopy: false)</c>) -- applies to any resource-holding
    /// <see cref="ILazyDataSource"/>, not just MMF.
    /// </remarks>
    /// <typeparam name="T">Unmanaged pixel element type (e.g., <c>ushort</c>, <c>float</c>).</typeparam>
    public abstract class VirtualFrames<T>
        : ILazyDataSource, ICacheableFrameList, IList<T[]>, IFrameKeyProvider<T>, IDisposable
        where T : unmanaged
    {
#if VF_DEBUG
        protected long _accessCount = 0;
#endif

        private readonly int _instanceId = VirtualFramesIdProvider.Next(); // unique instance id used for diagnostics / ToString
        private ICacheStrategy _cacheStrategy = new NeighborStrategy();     // default prefetch / eviction strategy

        private int _lastAccessedIndex = 0;

        // ── ILazyDataSource ──────────────────────────────────────────────────
        public string SourcePath { get; protected set; }
        public int StoredFrameCount { get { lock (_cacheLock) return _cache.Count; } }
        public event EventHandler<int>? FrameStored;
        public event EventHandler<int>? FrameEvicted;
        public event EventHandler? IsVirtualChanged;

        private bool _isOwned = false;

        /// <summary>
        /// Gets/Sets a value indicating whether this instance is owned by an external component
        /// (e.g. a <c>MatrixData&lt;T&gt;</c> instance) responsible for its disposal. First-claim-
        /// wins arbitration for a backend that may be reference-shared across several
        /// <c>MatrixData&lt;T&gt;</c> instances (e.g. via <c>Reorder(deepCopy: false)</c>), so
        /// exactly one of them ends up responsible for calling <see cref="Dispose"/>.
        /// </summary>
        public bool IsOwned
        {
            get => _isOwned;
            set
            {
                if (IsDisposed)
                    throw new ObjectDisposedException(nameof(VirtualFrames<T>), "Cannot change ownership of a disposed VirtualFrames instance.");
                if (_isOwned == true) // Cannot change ownership once set to true, to prevent accidental ownership transfer
                    throw new InvalidOperationException("This VirtualFrames instance is already owned. Ownership cannot be changed.");
                _isOwned = value;
            }
        }

        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Whether this instance currently does <em>not</em> hold every frame resident in memory.
        /// Backs <see cref="IMatrixData.IsVirtual"/>. Default: <see cref="StoredFrameCount"/> &lt;
        /// <see cref="Count"/>, so it converges to <see langword="false"/> once a backend that's
        /// meant to eventually hold everything (a decoder background-filling its cache, a fetcher
        /// that's retrieved every chunk) actually gets there. <see cref="MmfFrames{T}"/> overrides
        /// this to unconditionally <see langword="true"/>, since its cache is bounded/evicting by
        /// design (see <see cref="VirtualCachePolicy"/>) and never works toward a "fully loaded"
        /// state -- a momentarily-full cache says nothing about the next access.
        /// </summary>
        public virtual bool IsVirtual => StoredFrameCount < Count;

        // Last IsVirtual value announced via IsVirtualChanged (guarded by _cacheLock). Seeded in
        // the constructor with the initial state -- an empty cache is Virtual whenever there is at
        // least one frame -- so the very first fill-complete transition is reported too.
        private bool _lastIsVirtual;

        /// <summary>
        /// Re-evaluates <see cref="IsVirtual"/> against the last announced value. Must be called
        /// under <c>_cacheLock</c> after any change to <c>_cache</c> membership; returns
        /// <see langword="true"/> when the caller should raise <see cref="IsVirtualChanged"/> once
        /// it has released the lock.
        /// </summary>
        private bool UpdateIsVirtualUnderLock()
        {
            bool now = IsVirtual;
            if (now == _lastIsVirtual) return false;
            _lastIsVirtual = now;
            return true;
        }

        #region private fields for caching and prefetching (optional, can be extended with ICacheStrategy)

        protected readonly Dictionary<int, T[]> _cache = new();
        protected readonly LinkedList<int> _lruList = new(); // doubly-linked list for LRU eviction ordering (head = most-recently-used)

        // LinkedList<T>.Remove(T value) (as opposed to Remove(LinkedListNode<T> node)) is an O(n)
        // linear scan from the head to find a matching node -- fine for the eviction-candidate
        // path (FindEvictionCandidateUnderLock already walks the list and hands back the node it
        // found, so its Remove(node) calls are already O(1)), but "touch this index as
        // most-recently-used on every cache hit" used to call the O(n) value-based Remove, and a
        // cache hit is the single hottest path in the whole class. This dictionary records each
        // index's current node so that touch (LruTouch below) can do it in O(1) instead: every
        // AddFirst/AddLast/Clear on _lruList in this file must go through the matching Lru* helper
        // so this stays in sync with it, or a touch/eviction elsewhere would silently go back to
        // the O(n) path (or worse, operate on a stale/wrong node).
        private readonly Dictionary<int, LinkedListNode<int>> _lruNodes = new();

        public int CacheCapacity { get; set; } = 16; // maximum number of decoded frames held in the RAM cache at once


        protected readonly object _cacheLock = new object();

        // ── Background preload scheduler (all guarded by _cacheLock) ─────────────────────────
        // A small pool of workers drains one priority queue, instead of one async Task per target
        // frame waiting on a single I/O semaphore. Every SchedulePreload call replaces the queue
        // with the strategy's current target order, so reading always follows the latest cursor
        // position, and nothing needs to be cancelled when the target set moves -- at most
        // MaxConcurrentPreloads frames are ever in flight.
        //
        // Invariants: _queued, _inFlight and _cache are pairwise disjoint except for a frame a
        // synchronous indexer read inserts while a worker is still reading it (at most
        // MaxConcurrentPreloads, resolved when that read finishes). _queue may hold entries no
        // longer in _queued (taken, cached, or superseded); they are skipped when dequeued.
        private int[] _queue = Array.Empty<int>();
        private int _queueHead;
        private int _queueBuiltAtIndex = int.MinValue / 2; // cursor position the queue was last built for
        private const int ReorderDistance = 32;
        private readonly HashSet<int> _queued = new();
        private readonly HashSet<int> _inFlight = new();
        private int _activeWorkers;
        private int _maxConcurrentPreloads = 1;
        private bool _disposing;
        private readonly Stack<PreloadReader> _readerPool = new(); // idle readers, reused by the next worker
        private readonly CancellationTokenSource _disposeCts = new();

        // Bumped on every actual _cache membership change (insert or evict); used by
        // SchedulePreload's Volume-mode fast path to know whether a previously-confirmed "fully
        // covered, nothing to preload" result is still valid without re-deriving it.
        private int _cacheVersion;
        private int _lastPreloadSatisfiedAtCacheVersion = -1;

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
                // 1. Drop queued preloads (frames already in flight just finish; there are at most
                //    MaxConcurrentPreloads of them). SchedulePreload below queues the new targets.
                ClearQueueUnderLock();
                _lastPreloadSatisfiedAtCacheVersion = -1; // strategy changed; force SchedulePreload to re-derive, not trust a stale "fully covered" result

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

                LruClear();

                // AddLast builds the list so that highPriority items become the head.
                foreach (var idx in highPriority) LruAddLast(idx);
                foreach (var idx in lowPriority) LruAddLast(idx);
            }

            // 3. Kick off prefetch from the last-accessed index under the new strategy -- unless
            //    nothing has been read yet, so there is no position to preload around. The first
            //    read's own SchedulePreload starts it instead. This keeps constructing a backend,
            //    which typically assigns its strategy, from starting background reads (and opening
            //    per-worker resources) before a MatrixData owns it.
            if (StoredFrameCount == 0) return;
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
        ///       Frames being read in the background right now, followed by the frames still queued
        ///       for background reading (in priority order).
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
                var preloading = new List<int>(_inFlight.Count + _queued.Count);
                preloading.AddRange(_inFlight);
                for (int i = _queueHead; i < _queue.Length; i++)
                    if (_queued.Contains(_queue[i])) preloading.Add(_queue[i]);
                return new CacheSnapshot(_lruList.ToList(), preloading);
            }
        }

        #endregion

        /// <summary>Total number of logical frames. Fixed at construction.</summary>
        public int Count { get; }

        /// <summary>
        /// Initializes a new instance with the given logical frame count.
        /// </summary>
        /// <param name="frameCount">Total number of logical frames.</param>
        /// <param name="sourcePath">
        /// Where this data actually comes from (a local file path, a URL, etc.) -- surfaced via
        /// <see cref="SourcePath"/> for diagnostics, independent of how the concrete subclass reads it.
        /// </param>
        protected VirtualFrames(int frameCount, string sourcePath)
        {
            Count = frameCount;
            SourcePath = sourcePath;
            _lastIsVirtual = frameCount > 0;
        }

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
                if (IsDisposed) throw new ObjectDisposedException(GetType().Name);
                int previousIndex = _lastAccessedIndex;
                _lastAccessedIndex = index;

#if VF_DEBUG
                _accessCount++;
#endif
                T[]? data;
                bool hit;
                lock (_cacheLock)
                {
                    hit = _cache.TryGetValue(index, out data);
                    if (hit)
                    {
#if VF_DEBUG
                        _cacheHitCount++;
#endif
                        // The LRU touch itself must stay under the lock (it mutates shared state),
                        // but nothing else does -- SchedulePreload is called below, after releasing
                        // this lock, same as the miss path already does. Calling it from here (while
                        // still holding _cacheLock, which is reentrant so this never deadlocked) used
                        // to serialize its own preload-target enumeration -- for DimensionStrategy's
                        // Volume mode, a walk of the whole target-axis x composite-axis working set,
                        // done again on every single hit -- behind this one lock, effectively
                        // single-threading concurrent readers (e.g. VolumeAccessor.SliceOrthogonal's
                        // Parallel.For) even though the actual cache lookup is nearly instant.
                        // LruTouch (not the old Remove(index)+AddFirst(index)) is what makes the
                        // touch itself O(1): LinkedList<T>.Remove(int) is an O(n) linear scan from
                        // the head to find a matching node, and this is the single hottest path in
                        // the class -- every cache hit pays for it.
                        LruTouch(index);
                    }
                }

                if (hit)
                {
                    // Trigger prefetch for neighbouring frames -- but only when this access actually
                    // moved somewhere new. A repeat read of the same index (e.g. a cursor read-out
                    // re-sampling the still-current frame on every pointer move) carries no new
                    // navigation information: the preload target set SchedulePreload would recompute
                    // is identical to the one already built (or in progress) from the prior call, so
                    // recomputing it again is pure overhead. Once genuinely oversubscribed (target
                    // set bigger than CacheCapacity), that overhead stops being merely wasted CPU and
                    // starts being unbounded churn: SchedulePreload's "already satisfied" fast path
                    // (see its own comment) can never trip because the full target set never fits, so
                    // every repeat call would otherwise re-enqueue the same not-yet-cached remainder
                    // and keep evicting/refetching among equally-high-priority frames forever, even
                    // though nothing the caller is looking at has changed. Skipping here lets a
                    // landed-on queue drain once and settle instead of endlessly re-arming itself.
                    if (index != previousIndex)
                        SchedulePreload(index);
                    return data!;
                }
#if VF_DEBUG
                _cacheMissCount++;
#endif
                // Cache miss: synchronous read is unavoidable here.
                data = ReadFrame(index, CancellationToken.None);
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
                return data!;
            }

            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Reads and assembles one logical frame from the backing store.
        /// Implemented by concrete subclasses for each backend (MMF strip/tile layout, a real
        /// format decoder, a chunk-routing fetch, ...).
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
        protected abstract T[]? ReadFrame(int index, CancellationToken ct);

        // ── IFrameKeyProvider<T> ─────────────────────────────────────────────
        // Trivial default: no physical-frame deduplication (every logical index gets its own,
        // permanently-stable dummy key). Whether two logical indices can legitimately point at the
        // same physical data is backend-specific -- MmfFrames<T> overrides this with real
        // offset-based deduplication (see its own GetKey/IndexOf).

        private T[][]? _defaultKeys;

        private T[][] EnsureDefaultKeys()
        {
            if (_defaultKeys == null)
            {
                var keys = new T[Count][];
                for (int i = 0; i < Count; i++) keys[i] = new T[1];
                _defaultKeys = keys;
            }
            return _defaultKeys;
        }

        /// <summary>
        /// Returns the unique dummy key array for the specified logical frame index, used as a
        /// reference-identity key in <c>ValueRangeMap</c>. The default implementation returns a
        /// distinct, stable key per index (no deduplication); override to share a key across
        /// logical indices that point at the same physical data.
        /// </summary>
        public virtual T[] GetKey(int frameIndex)
        {
            if ((uint)frameIndex >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
            return EnsureDefaultKeys()[frameIndex];
        }

        /// <summary>
        /// Returns the first logical frame index whose key matches <paramref name="item"/>
        /// by reference identity, or <c>-1</c> if not found.
        /// </summary>
        public virtual int IndexOf(T[] item)
        {
            if (_defaultKeys == null) return -1;
            return Array.IndexOf(_defaultKeys, item);
        }

#if VF_DEBUG
        // Counters for diagnostics; incremented under _cacheLock to avoid races.
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
                Trace.WriteLine($"[VirtualFrames] --Cache Stats--");
            }
        }
#endif

        /// <summary>
        /// Called after a frame has been inserted into the cache, outside <c>_cacheLock</c>.
        /// Raises <see cref="FrameStored"/>; an override must call the base implementation.
        /// </summary>
        /// <param name="frameIndex">Index of the stored frame.</param>
        protected virtual void OnFrameStored(int frameIndex)
        {
            FrameStored?.Invoke(this, frameIndex);
        }

        /// <summary>
        /// Called after a frame has been evicted from the cache, outside <c>_cacheLock</c>.
        /// Raises <see cref="FrameEvicted"/>. Override in derived classes to perform post-eviction
        /// work such as flushing dirty data, and call the base implementation afterward.
        /// </summary>
        /// <param name="frameIndex">Index of the evicted frame.</param>
        /// <param name="frameData">The evicted <c>T[]</c> array.</param>
        protected virtual void OnFrameEvicted(int frameIndex, T[] frameData)
        {
            FrameEvicted?.Invoke(this, frameIndex);
        }

        /// <summary>
        /// Override hook that allows a derived class to veto eviction of a specific frame.
        /// <c>WritableStrippedMmfFrames{T}</c> overrides this to protect dirty (unsaved) frames.
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

        // ── _lruList / _lruNodes helpers (must be called under _cacheLock) ──────────────────
        // All of _lruList's mutation goes through these so _lruNodes never drifts out of sync.
        // See the field comment on _lruNodes above for why this exists.

        /// <summary>Adds <paramref name="index"/> as most-recently-used (list head) in O(1).</summary>
        private void LruAddFirst(int index) => _lruNodes[index] = _lruList.AddFirst(index);

        /// <summary>Adds <paramref name="index"/> as least-recently-used (list tail) in O(1).</summary>
        private void LruAddLast(int index) => _lruNodes[index] = _lruList.AddLast(index);

        /// <summary>
        /// Moves <paramref name="index"/> to most-recently-used (list head) in O(1) -- the "cache
        /// hit" touch. Removing then re-adding the SAME <see cref="LinkedListNode{T}"/> object (via
        /// the node overload of <c>AddFirst</c>) repositions it without allocating a new node or
        /// needing to update <see cref="_lruNodes"/> at all, since the node reference itself is
        /// unchanged. A no-op if <paramref name="index"/> isn't currently tracked.
        /// </summary>
        private void LruTouch(int index)
        {
            if (_lruNodes.TryGetValue(index, out var node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
            }
        }

        /// <summary>Removes a specific, already-known node (e.g. from an eviction-candidate walk) in O(1).</summary>
        private void LruRemoveNode(LinkedListNode<int> node)
        {
            _lruList.Remove(node);
            _lruNodes.Remove(node.Value);
        }

        /// <summary>Clears both the LRU list and its node lookup together.</summary>
        private void LruClear()
        {
            _lruList.Clear();
            _lruNodes.Clear();
        }

        /// <summary>
        /// Walks the LRU list from the tail (least-recently-used) to find one evictable candidate.
        /// Must be called under <c>_cacheLock</c>. Shared by <see cref="UpdateCache"/> (evict as
        /// needed while inserting) and <see cref="TrimCacheTo"/> (proactive shrink) so both apply
        /// identical selection rules.
        /// </summary>
        private LinkedListNode<int>? FindEvictionCandidateUnderLock()
        {
            var node = _lruList.Last;
            LinkedListNode<int>? fallback = null; // oldest evictable node seen so far, in case none are low-priority
            while (node != null)
            {
                // Pinned (actually on screen right now, e.g. the active frame or every currently-
                // blended Composite channel): never a candidate, not even as the high-priority
                // fallback below -- unlike IsHighPriority, which a whole oversubscribed axis sweep
                // can legitimately hold all at once, this is reserved for the handful of frames a
                // caller has said must survive no matter what.
                if (CacheStrategy?.IsPinned(node.Value) != true && CanEvict(node.Value))
                {
                    if (CacheStrategy == null || !CacheStrategy.IsHighPriority(node.Value))
                        return node; // genuine low-priority candidate: always prefer it

                    // High-priority, but still evictable -- remember it (first one found = LRU-oldest
                    // among evictable nodes) in case the whole list turns out to be high-priority.
                    // Falling back to _lruList.First here used to mean "evict whatever was touched
                    // most recently" -- for a strategy that marks its entire working set high-priority
                    // at once (e.g. DimensionStrategy's Volume mode on a single-axis dataset, where
                    // every frame is high-priority and none ever isn't), that is *always* the frame
                    // currently on screen: every hover-driven read re-touches it to the front, a
                    // background fetch then evicts it as the "last resort", the next read is a miss
                    // that re-arms preloading, and the cycle repeats without ever settling. Evicting
                    // the LRU-oldest evictable node instead (ordinary LRU semantics) sacrifices
                    // whichever high-priority frame has gone the longest untouched, not the one still
                    // actively being looked at.
                    fallback ??= node;
                }
                // Dirty frame: skip and try the next newer one.
                node = node.Previous;
            }
            return fallback;
        }

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
            bool inserted = false;
            bool isVirtualChanged;

            lock (_cacheLock)
            {
                if (_cache.ContainsKey(index)) return;

                while (_cache.Count >= CacheCapacity && _lruList.Count > 0)
                {
                    var candidate = FindEvictionCandidateUnderLock();
                    if (candidate == null) break; // no evictable candidate (e.g., all frames are dirty)

                    int evictedIndex = candidate.Value;
                    T[] evictedData = _cache[evictedIndex];

                    _cache.Remove(evictedIndex);
                    LruRemoveNode(candidate);
                    _cacheVersion++;

                    // Only collect here; actual I/O happens outside the lock below.
                    pendingEvictions ??= new List<(int, T[])>();
                    pendingEvictions.Add((evictedIndex, evictedData));
                }

                _cache[index] = data;
                LruAddFirst(index);
                _queued.Remove(index); // cached by some other path first (e.g. a synchronous read); no longer needs a worker
                _cacheVersion++;
                inserted = true;
                isVirtualChanged = UpdateIsVirtualUnderLock();
            }

            // Call OnFrameEvicted after releasing the lock so derived classes
            // can safely perform I/O without blocking _cacheLock.
            if (pendingEvictions != null)
                foreach (var (ei, ed) in pendingEvictions)
                    OnFrameEvicted(ei, ed);

            if (inserted)
                OnFrameStored(index);

            if (isVirtualChanged)
                IsVirtualChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Drops the cached copy of <paramref name="frameIndex"/>, if any, with the same bookkeeping
        /// as a capacity eviction (LRU list, cache version, <see cref="IsVirtual"/> tracking,
        /// <see cref="OnFrameEvicted"/>). For subclasses whose backing data changed underneath the
        /// cache (e.g. a direct write); never remove entries from <c>_cache</c> by hand, or the LRU
        /// list keeps an index the cache no longer holds.
        /// </summary>
        /// <remarks>
        /// Must be called without holding <c>_cacheLock</c>, since <see cref="OnFrameEvicted"/> runs
        /// here and may do I/O. <see cref="CanEvict"/> is not consulted: the caller has decided the
        /// cached copy is no longer valid.
        /// </remarks>
        /// <returns><see langword="true"/> if a cached copy was dropped.</returns>
        protected bool RemoveFromCache(int frameIndex)
        {
            T[]? data;
            bool isVirtualChanged;

            lock (_cacheLock)
            {
                if (!_cache.Remove(frameIndex, out data)) return false;
                if (_lruNodes.TryGetValue(frameIndex, out var node))
                    LruRemoveNode(node);
                _cacheVersion++;
                isVirtualChanged = UpdateIsVirtualUnderLock();
            }

            OnFrameEvicted(frameIndex, data);

            if (isVirtualChanged)
                IsVirtualChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>
        /// Sets <see cref="CacheCapacity"/> to <paramref name="newCapacity"/> and proactively
        /// evicts down to it immediately, instead of relying on future cache-miss traffic to do it
        /// lazily one insertion at a time (which <see cref="UpdateCache"/> alone can starve
        /// indefinitely if nothing new is requested afterward). Use this to actually release
        /// memory once a temporary elevated-budget mode ends.
        /// </summary>
        /// <remarks>
        /// If every remaining frame is protected by the active <see cref="CacheStrategy"/> (e.g.
        /// still in a mode that marks them all high-priority), eviction stops early via the same
        /// "evict the most-recently-used as a last resort" rule <see cref="UpdateCache"/> uses --
        /// callers that need a hard guarantee should switch to a non-protective strategy (or clear
        /// it) before trimming.
        /// </remarks>
        public void TrimCacheTo(int newCapacity)
        {
            List<(int evictedIndex, T[] evictedData)>? pendingEvictions = null;
            bool isVirtualChanged;

            lock (_cacheLock)
            {
                CacheCapacity = newCapacity;

                while (_cache.Count > CacheCapacity && _lruList.Count > 0)
                {
                    var candidate = FindEvictionCandidateUnderLock();
                    if (candidate == null) break;

                    int evictedIndex = candidate.Value;
                    T[] evictedData = _cache[evictedIndex];

                    _cache.Remove(evictedIndex);
                    LruRemoveNode(candidate);
                    _cacheVersion++;

                    pendingEvictions ??= new List<(int, T[])>();
                    pendingEvictions.Add((evictedIndex, evictedData));
                }
                isVirtualChanged = UpdateIsVirtualUnderLock();
            }

            if (pendingEvictions != null)
                foreach (var (ei, ed) in pendingEvictions)
                    OnFrameEvicted(ei, ed);

            if (isVirtualChanged)
                IsVirtualChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SchedulePreload(int currentIndex)
        {
            if (CacheStrategy == null) return;

            // Nothing left to prefetch once every logical frame is already resident -- true once
            // navigation (or a background fill, see TiffDecodedFrames<T>) has caught up to a cache
            // capacity that comfortably holds the whole dataset. Without this, a plain strategy
            // (unlike DimensionStrategy's own Volume-mode fast path below) would keep rebuilding
            // and scanning its target set on every single frame access forever, for no benefit.
            if (StoredFrameCount >= Count) return;

            // Similar while a whole-dataset fill is still running (e.g. TiffDecodedFrames<T>'s
            // unbounded NeighborStrategy): once every frame is cached, in flight or queued, a
            // rebuild could only reorder the queue, not add to it. Rebuilding on every access
            // enumerated and scanned all Count indices on the caller's (usually UI) thread --
            // measured ~2 ms per access, spiking to ~20 ms, for 60,000 frames -- so reorder only
            // once the cursor has moved ReorderDistance frames from where the queue was last built:
            // a jump is followed at once, while scrolling rebuilds every ReorderDistance frames. A
            // bounded strategy never reaches this state short of a window as large as the dataset,
            // and an eviction drops the sum below Count again, so it keeps rebuilding every call.
            lock (_cacheLock)
            {
                if (_disposing) return;
                if (_cache.Count + _inFlight.Count + _queued.Count >= Count
                    && Math.Abs(currentIndex - _queueBuiltAtIndex) < ReorderDistance)
                    return;
            }

            // Fast path for DimensionStrategy's Volume mode specifically: its preload target set
            // does not depend on currentIndex's own position along TargetAxis -- every value of
            // TargetAxis is always included regardless of which one is "current" (DimensionStrategy
            // itself already memoizes the enumeration for exactly this reason, see its
            // GetPreloadIndices). So once every target has been confirmed already cached/in-flight
            // for the CURRENT _cache contents, nothing changes for a later call with a different
            // currentIndex either, as long as nothing has actually been inserted into or evicted
            // from _cache since (tracked by _cacheVersion) and nothing is still in flight. Skipping
            // straight to return here avoids rebuilding+rescanning the whole target set (up to
            // targetLength * channelCount entries) on every single call -- measured to be the
            // dominant remaining per-access cost once this class' own lock/LRU overhead was
            // fixed, since a fully warmed cache would otherwise still redo this every touch. Not
            // applied to other strategies/modes (e.g. SinglePlane's neighbor set genuinely changes
            // as currentIndex moves, so those must keep recomputing every call).
            if (CacheStrategy is DimensionStrategy { Mode: DimensionStrategy.CacheMode.Volume })
            {
                lock (_cacheLock)
                {
                    if (_inFlight.Count == 0 && _queued.Count == 0 && _cacheVersion == _lastPreloadSatisfiedAtCacheVersion)
                        return;
                }
            }

            // Compute preload targets outside the lock; dimension calculations in the
            // strategy may be non-trivial. The strategy's order is the read priority.
            int[] targets = CacheStrategy.GetPreloadIndices(currentIndex, Count).ToArray();

            lock (_cacheLock)
            {
                if (_disposing) return;

                // Replace -- not append to -- the queue: whatever was still waiting belonged to an
                // older cursor position. Frames already in flight simply finish and land in the
                // cache, so no cancellation is needed when the navigation context changes.
                ClearQueueUnderLock();
                var queue = new List<int>(targets.Length);
                foreach (int target in targets)
                {
                    if ((uint)target >= (uint)Count || _cache.ContainsKey(target) || _inFlight.Contains(target))
                        continue;
                    if (_queued.Add(target)) // first occurrence keeps its priority
                        queue.Add(target);
                }
                _queue = queue.ToArray();
                _queueHead = 0;
                _queueBuiltAtIndex = currentIndex;

                // Remember: as of the current _cache contents, this target set is fully covered
                // and nothing is in flight -- the Volume-mode fast path above can trust that until
                // _cacheVersion moves (a real insert/evict) or the strategy changes.
                if (_queued.Count == 0 && _inFlight.Count == 0)
                    _lastPreloadSatisfiedAtCacheVersion = _cacheVersion;

                StartWorkersUnderLock();
            }
        }

        /// <summary>
        /// Maximum number of frames read in the background at the same time (default 1). Every
        /// concurrent worker reads through its own <see cref="PreloadReader"/> from
        /// <see cref="CreatePreloadReader"/>, so a value above 1 only helps a backend whose readers
        /// are genuinely independent -- e.g. a decoder that opens its own file handle per reader.
        /// Synchronous reads through the indexer are not counted and never wait on this.
        /// </summary>
        public int MaxConcurrentPreloads
        {
            get { lock (_cacheLock) return _maxConcurrentPreloads; }
            set
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(value), "MaxConcurrentPreloads must be at least 1.");
                lock (_cacheLock)
                {
                    _maxConcurrentPreloads = value;
                    StartWorkersUnderLock(); // takes effect for work already queued
                }
            }
        }

        /// <summary>
        /// Reads frames for one background preload worker. A worker keeps the same reader for as
        /// long as it runs and never shares it, so an implementation may hold per-reader state that
        /// is not thread-safe (e.g. its own file handle and decoder position).
        /// </summary>
        /// <remarks>
        /// Readers are pooled while the source is still loading and disposed once every frame is
        /// cached or the source is disposed; a later need (e.g. after a capacity trim) creates new
        /// ones through <see cref="CreatePreloadReader"/>.
        /// </remarks>
        protected abstract class PreloadReader : IDisposable
        {
            /// <summary>Reads one frame, or returns <see langword="null"/> if <paramref name="ct"/> is cancelled.</summary>
            public abstract T[]? Read(int index, CancellationToken ct);

            public virtual void Dispose() { }
        }

        /// <summary>
        /// Creates a reader for a background preload worker. The default reads through
        /// <see cref="ReadFrame"/> on this instance, which is right for a backend whose
        /// <see cref="ReadFrame"/> is safe to call concurrently with the indexer (e.g. MMF). Override
        /// to give each worker an independent resource so background reads neither contend with
        /// each other nor with synchronous reads.
        /// </summary>
        protected virtual PreloadReader CreatePreloadReader() => new ReadFrameReader(this);

        private sealed class ReadFrameReader : PreloadReader
        {
            private readonly VirtualFrames<T> _owner;
            public ReadFrameReader(VirtualFrames<T> owner) => _owner = owner;
            public override T[]? Read(int index, CancellationToken ct) => _owner.ReadFrame(index, ct);
        }

        private void ClearQueueUnderLock()
        {
            _queue = Array.Empty<int>();
            _queueHead = 0;
            _queued.Clear();
        }

        private bool TryDequeueUnderLock(out int index)
        {
            while (_queueHead < _queue.Length)
            {
                int candidate = _queue[_queueHead++];
                if (_queued.Remove(candidate)) // skips entries already taken, cached, or superseded
                {
                    index = candidate;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        private void StartWorkersUnderLock()
        {
            if (_disposing) return;
            int toStart = Math.Min(_maxConcurrentPreloads - _activeWorkers, _queued.Count);
            for (int i = 0; i < toStart; i++)
            {
                _activeWorkers++;
                _ = Task.Run(RunPreloadWorker);
            }
        }

        /// <summary>
        /// One background worker: takes the highest-priority queued frame, reads it through its own
        /// reader, stores it, and repeats until the queue is empty.
        /// </summary>
        /// <remarks>
        /// An exiting worker closes (or pools) its reader first and only then leaves
        /// <c>_activeWorkers</c>, so <see cref="Dispose"/> never returns while a reader is still open.
        /// Because the exit decision and the decrement are therefore not atomic, the decrement is
        /// followed by <see cref="StartWorkersUnderLock"/>: work queued in between is never left
        /// behind with no worker to take it.
        /// </remarks>
        private void RunPreloadWorker()
        {
            PreloadReader? reader = null;
            while (true)
            {
                int index;
                List<PreloadReader>? toDispose = null;
                bool exit = false;

                lock (_cacheLock)
                {
                    // Also exit when MaxConcurrentPreloads was lowered below the running worker
                    // count, so a smaller limit takes effect after the current frame, not only for
                    // workers started later.
                    if (_disposing || _activeWorkers > _maxConcurrentPreloads || !TryDequeueUnderLock(out index))
                    {
                        exit = true;
                        index = -1;
                        toDispose = ReturnReaderUnderLock(reader);
                        reader = null;
                    }
                    else
                    {
                        _inFlight.Add(index);
                    }
                }

                if (exit)
                {
                    // Close readers before leaving the active count: Dispose returns once the count
                    // reaches zero, and the subclass then expects no handle of its file to be open.
                    DisposeReaders(toDispose);
                    lock (_cacheLock)
                    {
                        _activeWorkers--;
                        Monitor.PulseAll(_cacheLock); // Dispose may be waiting for workers to finish
                        // Work queued while this worker was closing its reader saw it as still active
                        // and may not have started a replacement; start one now.
                        StartWorkersUnderLock();
                    }
                    return;
                }

                try
                {
                    reader ??= RentPreloadReader();
                    T[]? data = reader.Read(index, _disposeCts.Token);
                    if (data != null && !_disposeCts.IsCancellationRequested)
                    {
                        UpdateCache(index, data);
#if VF_DEBUG
                        Interlocked.Increment(ref _prefetchSuccessCount);
#endif
                    }
                }
                catch (OperationCanceledException)
                {
                    // Disposal in progress; the next loop iteration exits.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VirtualFrames] Preload error at frame {index}: {ex.Message}");
                }
                finally
                {
                    lock (_cacheLock) { _inFlight.Remove(index); }
                }
            }
        }

        private PreloadReader RentPreloadReader()
        {
            lock (_cacheLock)
            {
                if (_readerPool.Count > 0) return _readerPool.Pop();
            }
            return CreatePreloadReader();
        }

        /// <summary>
        /// Returns an exiting worker's reader to the pool, or -- once every frame is cached or the
        /// source is being disposed -- collects it and every pooled reader for disposal outside the
        /// lock.
        /// </summary>
        private List<PreloadReader>? ReturnReaderUnderLock(PreloadReader? reader)
        {
            bool release = _disposing || _cache.Count >= Count;
            List<PreloadReader>? toDispose = null;
            if (reader != null)
            {
                if (release) (toDispose ??= new()).Add(reader);
                else _readerPool.Push(reader);
            }
            if (release)
                while (_readerPool.Count > 0) (toDispose ??= new()).Add(_readerPool.Pop());
            return toDispose;
        }

        private static void DisposeReaders(List<PreloadReader>? readers)
        {
            if (readers == null) return;
            foreach (var r in readers)
            {
                try { r.Dispose(); }
                catch (Exception ex) { Debug.WriteLine($"[VirtualFrames] Preload reader dispose error: {ex.Message}"); }
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
                List<PreloadReader> readers;
                lock (_cacheLock)
                {
                    if (_disposing) return; // a concurrent Dispose is already tearing down
                    _disposing = true;
                    _disposeCts.Cancel(); // readers that honor the token stop mid-frame
                    ClearQueueUnderLock();

                    // Wait for in-flight background reads to finish: subclasses release the
                    // resources those reads use (MMF view, file handles) right after this returns.
                    // Monitor.Wait releases the lock, so workers can still reach their exit path.
                    // Bounded so a reader that ignores cancellation, or a Dispose called from a
                    // worker's own event handler, cannot hang the caller forever.
                    long deadline = Environment.TickCount64 + 10_000;
                    while (_activeWorkers > 0)
                    {
                        long remaining = deadline - Environment.TickCount64;
                        if (remaining <= 0)
                        {
                            Debug.WriteLine($"[VirtualFrames] Dispose: {_activeWorkers} preload worker(s) still running after 10 s. obj={this}");
                            break;
                        }
                        Monitor.Wait(_cacheLock, (int)remaining);
                    }

                    _cache.Clear();
                    LruClear();
                    readers = new List<PreloadReader>(_readerPool);
                    _readerPool.Clear();
                }
                DisposeReaders(readers);
                IsDisposed = true;

#if VF_DEBUG || DEBUG
                Trace.WriteLine($"[VirtualFrames] Disposed. obj={this}");
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
        // The list is a read-only view over an on-demand source; structural mutations are not permitted.
        // ==========================================
        public void Add(T[] item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, T[] item) => throw new NotSupportedException();
        public bool Remove(T[] item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public virtual bool Contains(T[] item) => throw new NotSupportedException("Contains is not supported on VirtualFrames.");

        public void CopyTo(T[][] array, int arrayIndex) => throw new NotSupportedException("Use explicit loop for copying to avoid memory exhaustion.");
    }

    internal static class VirtualFramesIdProvider
    {
        private static int _instanceCounter = 0;
        public static int Next() => Interlocked.Increment(ref _instanceCounter);
    }
}
