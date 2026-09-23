using BitMiracle.LibTiff.Classic;
using MxPlot.Core.IO;
using MxPlot.Core.IO.CacheStrategies;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MxPlot.Extensions.Tiff;

/// <summary>
/// Lazy (decode-on-access) frame source for a compressed, multi-directory TIFF -- the case
/// <see cref="MmfFrames{T}"/> cannot handle, since a memory-mapped view only works for
/// uncompressed, fixed-offset pixel data. Shared by both OME-TIFF and ImageJ/plain-TIFF readers:
/// the only behavioral difference between them is the Y-flip timing, taken as a constructor
/// parameter (see <see cref="TiffFrameCodec"/>'s own remarks on why that isn't baked in).
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of LibTiff handle, so synchronous and background reads never contend:
/// </para>
/// <list type="bullet">
///   <item>One main handle, opened in the constructor and kept for the instance's lifetime, serves
///   synchronous cache misses through <see cref="ReadFrame"/> (the indexer). It is guarded by its
///   own lock, since a single handle is not safe for concurrent directory-switch+read pairs.</item>
///   <item>Each background preload worker gets its own handle through
///   <see cref="CreatePreloadReader"/>, so up to <see cref="VirtualFrames{T}.MaxConcurrentPreloads"/>
///   frames decode truly in parallel. The base class pools these while the file is still loading
///   and closes them once every frame is cached.</item>
/// </list>
/// </remarks>
internal sealed class TiffDecodedFrames<T> : VirtualFrames<T> where T : unmanaged
{
    private readonly BitMiracle.LibTiff.Classic.Tiff _tiff;
    private readonly long[] _ifdOffsets;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _flipY;
    private readonly object _tiffLock = new();

    /// <param name="filePath">Path to the TIFF file. Opened independently for this instance.</param>
    /// <param name="width">Frame width in pixels (same for every directory).</param>
    /// <param name="height">Frame height in pixels (same for every directory).</param>
    /// <param name="frameCount">Number of TIFF directories (IFDs) to expose as logical frames.</param>
    /// <param name="flipY">Passed through to <see cref="TiffFrameCodec"/> -- see its own remarks.</param>
    public TiffDecodedFrames(string filePath, int width, int height, int frameCount, bool flipY)
        : this(filePath, width, height, TiffIfdIndex.ReadOffsets(filePath), frameCount, flipY)
    {
    }

    /// <param name="filePath">Path to the TIFF file. Opened independently for this instance.</param>
    /// <param name="width">Frame width in pixels (same for every directory).</param>
    /// <param name="height">Frame height in pixels (same for every directory).</param>
    /// <param name="ifdOffsets">
    /// Every IFD offset in the file (<see cref="TiffIfdIndex.ReadOffsets(string, CancellationToken)"/>), for
    /// callers that already collected them. Frames are addressed through these rather than by
    /// directory number, which LibTiff.NET cannot do past 32,767.
    /// </param>
    /// <param name="frameCount">Number of leading IFDs to expose as logical frames.</param>
    /// <param name="flipY">Passed through to <see cref="TiffFrameCodec"/> -- see its own remarks.</param>
    public TiffDecodedFrames(string filePath, int width, int height, long[] ifdOffsets, int frameCount, bool flipY)
        : base(frameCount, filePath)
    {
        if (ifdOffsets.Length < frameCount)
            throw new InvalidDataException($"TIFF file has {ifdOffsets.Length} IFDs, but {frameCount} frames were requested: {filePath}");

        _tiff = BitMiracle.LibTiff.Classic.Tiff.Open(filePath, "r")
            ?? throw new IOException($"Failed to open TIFF file: {filePath}");
        _ifdOffsets = ifdOffsets;
        _width = width;
        _height = height;
        _flipY = flipY;

        // Aim to hold the whole dataset in the decode cache when it fits the memory budget, so a
        // compressed file converges to IsVirtual == false once background prefetch catches up (see
        // VirtualFrames<T>.IsVirtual's remarks). Bounded by the memory budget only -- not by
        // VirtualCachePolicy.MaxCapacity, which caps MMF's evicting cache: with that cap a
        // 60,000-frame file of small frames never loaded fully even though it easily fit.
        long frameSizeBytes = (long)width * height * Unsafe.SizeOf<T>();
        CacheCapacity = VirtualCachePolicy.ComputeCapacity(
            frameSizeBytes, frameCount, VirtualCachePolicy.MemoryBudgetFraction, maxCapacity: int.MaxValue);

        // Decode in the background on half the cores, leaving the rest for synchronous decodes and
        // the UI.
        MaxConcurrentPreloads = Math.Max(1, Environment.ProcessorCount / 2);

        // When the whole dataset fits the computed capacity, reach for it proactively instead of
        // waiting on navigation: widen NeighborStrategy's window to the entire frame count in both
        // directions. Nothing is read here -- assigning the strategy does not start preloading
        // while the cache is empty -- but the first access anywhere queues every other frame for
        // background decode, so the file finishes loading even if nothing else is touched. Both
        // directions (not just lookAhead) matter: if the cursor moves mid-fill (e.g. MatrixPlotter
        // restoring a persisted position), an ahead-only window would drop everything behind the
        // new position from the rebuilt queue. With both unbounded, the queue always holds every
        // frame not yet loaded, ordered outward from wherever the cursor now is.
        //
        // When it does not fit, still fill the cache around the cursor rather than leaving the
        // default 4-ahead/1-behind window: half the capacity, weighted forward. The other half is
        // headroom for recently viewed frames, so shifting the window by one frame evicts the
        // oldest history instead of a frame that is still inside the window (which would then be
        // re-queued and evict another -- churn).
        CacheStrategy = CacheCapacity >= frameCount
            ? new NeighborStrategy(lookAhead: frameCount, lookBehind: frameCount)
            : new NeighborStrategy(lookAhead: Math.Max(4, CacheCapacity * 3 / 8), lookBehind: Math.Max(1, CacheCapacity / 8));
    }

    protected override T[]? ReadFrame(int index, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return null;

        lock (_tiffLock)
        {
            return DecodeFrame(_tiff, index);
        }
    }

    protected override PreloadReader CreatePreloadReader() => new HandleReader(this);

    private T[] DecodeFrame(BitMiracle.LibTiff.Classic.Tiff tiff, int index)
    {
        TiffIfdIndex.SetFrame(tiff, _ifdOffsets, index);
        bool isTiled = tiff.GetField(TiffTag.TILEWIDTH) != null;
        return isTiled
            ? TiffFrameCodec.ReadTiled<T>(tiff, _width, _height, _flipY)
            : TiffFrameCodec.ReadStripped<T>(tiff, _width, _height, _flipY);
    }

    /// <summary>A background worker's own LibTiff handle; used by one worker at a time, so no lock.</summary>
    private sealed class HandleReader : PreloadReader
    {
        private readonly TiffDecodedFrames<T> _owner;
        private readonly BitMiracle.LibTiff.Classic.Tiff _tiff;

        public HandleReader(TiffDecodedFrames<T> owner)
        {
            _owner = owner;
            _tiff = BitMiracle.LibTiff.Classic.Tiff.Open(owner.SourcePath, "r")
                ?? throw new IOException($"Failed to open TIFF file for background decode: {owner.SourcePath}");
        }

        public override T[]? Read(int index, CancellationToken ct)
            => ct.IsCancellationRequested ? null : _owner.DecodeFrame(_tiff, index);

        public override void Dispose() => _tiff.Dispose();
    }

    public override void Dispose()
    {
        bool wasDisposed = IsDisposed;
        base.Dispose();
        if (!wasDisposed)
        {
            _tiff.Dispose();
        }
    }
}
