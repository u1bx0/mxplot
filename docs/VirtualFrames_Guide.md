# VirtualFrames Guide

**MxPlot.Core — On-demand (Virtual) Frame Storage Architecture**

> Last Updated: 2026-09-15

*This document covers the design and usage of the Virtual storage layer in MxPlot.Core: the
generic `VirtualFrames<T>` skeleton, the memory-mapped (MMF) backends built on it, and the
decode-on-access (Lazy decode) backend for compressed TIFF.
For how to consume Virtual data from a file format plugin, see the
[Extension Development Guide](./MxPlot_Extensions_Guide.md) §3.4.*

---

## Table of Contents

1. [What is "Virtual" in MxPlot?](#what-is-virtual)
2. [Class Hierarchy and Interfaces](#class-hierarchy)
3. [Caching and Prefetch](#caching-and-prefetch)
4. [Creating Virtual-backed MatrixData](#creating-virtual-backed-matrixdata)
5. [Reading an Existing File as Virtual](#reading-an-existing-file-as-virtual)
6. [Saving and the Fast-Path](#saving-and-the-fast-path)
7. [Cloning Virtual Data](#cloning-virtual-data)
8. [VirtualPolicy — Threshold Configuration](#virtualpolicy)
9. [Diagnostics and UI Integration](#diagnostics)
10. [Known Limitations and Planned Work](#known-limitations)

---

## 1. What is "Virtual" in MxPlot? {#what-is-virtual}

A **Virtual** `MatrixData<T>` does not hold every frame in managed heap memory up front.
Frames are materialized on demand and kept in an LRU frame cache. *How* a missing frame is
materialized depends on the backend:

| Backend | How a frame is read | Typical source |
|---|---|---|
| **MMF** (`MmfFrames<T>` family) | Raw bytes copied from a memory-mapped file | Uncompressed `.mxd`, uncompressed OME-TIFF / ImageJ packed TIFF |
| **Lazy decode** (`TiffDecodedFrames<T>`) | Real per-frame decode through LibTiff | Compressed multi-IFD TIFF (OME-TIFF / ImageJ) |

Key properties compared to InMemory:

| Property | InMemory | Virtual (MMF) | Virtual (Lazy decode) |
|---|---|---|---|
| Open time | Proportional to total size | Near-instant (offset table only) | Near-instant (header only) |
| RAM usage at rest | = total pixel data | ≈ cache size | ≈ cache size, grows toward full size |
| Writable | Yes | Yes (`WritableStrippedMmfFrames<T>`) or read-only | Read-only |
| Save (`.mxd`) | Full pixel copy to disk | File-move + trailer write (writable `.mxd` vessel) | Full pixel copy |
| `IsVirtual` property | `false` | Always `true` | `true` until every frame is cached, then `false` |

### What `IsVirtual` means

`IMatrixData.IsVirtual` answers "does this data currently **not** hold every frame resident in
memory?" — not "is this backed by a file?".

- **MMF backends** override it to be unconditionally `true`. Their cache is bounded and evicting
  by design; a momentarily-full cache says nothing about the next access.
- **Other `VirtualFrames<T>` backends** use the default `StoredFrameCount < Count`, so the value
  converges to `false` once the backend has everything in RAM (e.g. a Lazy-decode dataset whose
  background prefetch finished).

Because the value can change without any change to pixel content, `ILazyDataSource` raises
`IsVirtualChanged` whenever it flips (in either direction — a later `TrimCacheTo` can make a fully
loaded backend Virtual again). MMF backends never raise it. The event may arrive on a background
prefetch thread and carries no value; handlers should marshal to their own thread and re-read
`IsVirtual`.

Code that needs to know *which* backend is in use should not rely on `IsVirtual` alone — see
[§9 Diagnostics](#diagnostics).

A 4.95 GB dataset (1,209,600 frames × 32×32 float, MMF-backed) benchmarked on Core i9-14900KF:

| Step | Virtual | InMemory |
|---|---|---|
| Create / allocate | 764 ms | 2,706 ms |
| SaveAs to disk (4,725 MB) | **61 ms** (file-move fast-path) | 45,857 ms (full copy) |
| LoadVirtual (reopen) | 644 ms | 1,043 ms |

---

## 2. Class Hierarchy and Interfaces {#class-hierarchy}

The generic skeleton and MMF backends live in `MxPlot.Core.IO`; the Lazy-decode TIFF backend
lives in `MxPlot.Extensions.Tiff`. Application code normally interacts with `MatrixData<T>`
only — the backend classes are what format readers use to *build* a Virtual `MatrixData<T>`.

```
Interfaces (MxPlot.Core.IO)
  ILazyDataSource        SourcePath, StoredFrameCount, FrameStored, FrameEvicted,
                         IsOwned, IsDisposed, IsVirtual, IsVirtualChanged
  ICacheableFrameList    CacheStrategy, CacheCapacity, GetCacheStatus(), TrimCacheTo()
  └─ IMmfFrameList       (empty marker: ILazyDataSource + ICacheableFrameList, "this is MMF")
  IWritableFrameProvider<T>   GetWritableArray, WriteDirectly, Flush, SaveAs

Classes
  VirtualFrames<T>                      abstract · MxPlot.Core.IO
  │   LRU cache, prefetch, eviction, diagnostics; abstract ReadFrame(index, ct)
  │
  ├─ MmfFrames<T>                       abstract · MxPlot.Core.IO · IMmfFrameList
  │   │   MMF mount/unmount, offset-based frame-key dedup, byte-order swap, IsVirtual => true
  │   ├─ StrippedMmfFrames<T>           read-only, strip layout
  │   │   └─ WritableStrippedMmfFrames<T>   read-write, strip layout · IWritableFrameProvider<T>
  │   └─ TiledMmfFrames<T>              read-only, tile layout
  │
  └─ TiffDecodedFrames<T>               internal sealed · MxPlot.Extensions.Tiff
          one LibTiff handle per instance, per-frame decode via TiffFrameCodec
```

### VirtualFrames\<T\> — the generic skeleton

Everything that does not depend on *how* a frame is read:

- The LRU frame cache (`CacheCapacity`, eviction, `TrimCacheTo`), with O(1) touch on cache hit.
- Background prefetch: a pluggable `ICacheStrategy` picks the frames, and a worker-pool scheduler
  reads them in priority order with up to `MaxConcurrentPreloads` in parallel (see
  [§3](#caching-and-prefetch)).
- The first-claim-wins ownership flag `IsOwned`, which guards against double disposal when
  several `MatrixData<T>` instances share one backend by reference (e.g. `Reorder(deepCopy: false)`).
- Diagnostics (`GetCacheStatus()`) and per-frame events (`FrameStored` when a frame enters the
  cache, `FrameEvicted` when it leaves; both may be raised on a prefetch thread).
- Extension hooks for subclasses: `ReadFrame` (required), `CreatePreloadReader` (optional, see
  below), `CanEvict`, `OnFrameStored` / `OnFrameEvicted` (the event raisers — overrides call the
  base implementation), `RemoveFromCache` (drop a cached copy after the backing data changed),
  `GetKey` / `IndexOf` (frame-key identity for the shared `ValueRange` cache).

A new backend (e.g. a future remote/chunk-based reader) derives from `VirtualFrames<T>` and
implements only `ReadFrame`. `ReadFrame` should return `null` rather than throw when its
`CancellationToken` is cancelled.

**Background readers.** Synchronous reads (the indexer) always call `ReadFrame`. Background
workers instead read through a `PreloadReader` from `CreatePreloadReader()`, one per worker and
never shared. The default reader just calls `ReadFrame`, which suits a backend whose `ReadFrame`
is safe to run concurrently (MMF). A backend with a stateful resource overrides it to give each
worker its own — `TiffDecodedFrames<T>` returns a reader with its own LibTiff handle — so
background decodes neither contend with each other nor with synchronous reads. Readers are
pooled while the source is loading and disposed once every frame is cached or the source is
disposed.

### MmfFrames\<T\> family — memory-mapped files

- `offsets[frameIndex][stripOrTileIndex]` + `byteCounts[frameIndex][stripOrTileIndex]` describe
  where each frame's raw bytes sit in the file. For single-strip-per-frame formats, the inner
  array has one element.
- `isYFlipped: true` reverses row order on read (for files stored top-down, such as TIFF, since
  MatrixData uses a bottom-left origin).
- `isBigEndian` is the **file's** byte order, not a swap decision; the class compares it with the
  host's byte order and swaps only when needed.
- Logical frames that share the same physical offset share one frame key, so their cached
  `ValueRange` is shared too.

| Class | Access | Notes |
|---|---|---|
| `StrippedMmfFrames<T>` | Read-only | Most uncompressed formats (`.mxd`, strip TIFF, ImageJ packed stacks) |
| `TiledMmfFrames<T>` | Read-only | Assembles tiles into one frame; clips right/bottom edge tiles automatically |
| `WritableStrippedMmfFrames<T>` | Read-write | Temp vessels, clones, writable `.mxd` / OME-TIFF. Dirty frames are protected from eviction until `Flush()`. `isTemporary: true` deletes the file on `Dispose` unless `Retain()` is called. |

### TiffDecodedFrames\<T\> — Lazy decode for compressed TIFF

A memory map only works for uncompressed, fixed-offset pixel data. For a compressed multi-IFD
TIFF, `TiffDecodedFrames<T>` opens its own LibTiff handle and decodes one directory per frame
on access (strip or tile, via `TiffFrameCodec`). OME-TIFF and ImageJ readers share it; the only
difference between them is the `flipY` constructor argument.

- **Cache sizing** uses `VirtualCachePolicy.ComputeCapacity` bounded by the memory budget only —
  not by `MaxCapacity` (8,192), which is MMF's cap. A file of many small frames can therefore be
  held in full.
- **Whole-file background fill:** when the whole dataset fits within that capacity, the
  constructor installs `NeighborStrategy(lookAhead: frameCount, lookBehind: frameCount)`. A single
  access then schedules every other frame for background decode, so the file finishes loading
  even if nothing else is touched, and `IsVirtual` eventually becomes `false`. Because both
  directions are unbounded, the queue always holds every frame not yet loaded; moving the cursor
  reorders it outward from the new position (see [§3](#caching-and-prefetch)).
- **Windowed fill:** when the dataset does not fit, the constructor installs a `NeighborStrategy`
  covering half the capacity (3/8 ahead, 1/8 behind), leaving the other half as headroom for
  recently viewed frames so that shifting the window evicts old history rather than frames still
  inside it.
- **Handles and parallelism:** a single LibTiff handle cannot service concurrent directory-switch +
  read pairs. Synchronous cache misses use one locked main handle; background workers each use
  their own handle, with `MaxConcurrentPreloads` defaulting to half the logical cores. Measured on
  32 cores with 4,000 LZW frames of 256×256: the whole file loads in 0.23 s (1 worker: 2.2 s),
  faster than the parallel InMemory loader (0.47 s).
- **Frame addressing:** frames are located through `TiffIfdIndex` (IFD offsets collected once)
  rather than LibTiff.NET's directory numbers, which are `Int16` and break past 32,767 frames.

---

## 3. Caching and Prefetch {#caching-and-prefetch}

### Cache capacity — VirtualCachePolicy

`VirtualCachePolicy` (static, `MxPlot.Core.IO`) sizes a backend's cache from available memory
and per-frame byte size, aiming to hold the whole dataset when that comfortably fits:

| Setting | Default | Meaning |
|---|---|---|
| `MemoryBudgetFraction` | `0.25` | Fraction of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` one instance may claim |
| `VolumeModeMemoryBudgetFraction` | `0.5` | Temporarily elevated budget while an orthogonal (Volume-mode) view is active |
| `MinCapacity` / `MaxCapacity` | `16` / `8192` | Absolute clamp, in frames. Lazy decode passes its own ceiling (`int.MaxValue`) through the `maxCapacity` overload and is bounded by the budget alone |

```csharp
int capacity = VirtualCachePolicy.ComputeCapacity(frameSizeBytes, idealFrameCount);
```

Growing `CacheCapacity` takes effect immediately. Assigning a smaller value does **not** evict
anything by itself — call `TrimCacheTo(newCapacity)` to actually release memory.

### Prefetch strategy — ICacheStrategy

`ICacheStrategy` decides *which* frames to preload around the current index and which cached
frames are high priority (protected from eviction). It says nothing about how many reads run at
once.

| Strategy | Behavior |
|---|---|
| `NeighborStrategy(lookAhead = 4, lookBehind = 1)` | Default. Preloads nearby frames in index order, forward first. |
| `DimensionStrategy(dimStruct, target, compositeAxis)` | Axis-aware. `SinglePlane` mode prefetches along the target axis; `Volume` mode keeps the whole target-axis × composite-axis working set for orthogonal views. |

```csharp
// Via MatrixData (works for any ICacheableFrameList backend; ignored for InMemory data)
md.CacheStrategy = new NeighborStrategy(lookAhead: 8, lookBehind: 2);
```

### Preload scheduler — how the targets get read

The strategy only says *what*; `VirtualFrames<T>` decides *when* and *how many at once*:

1. On every access, the strategy's targets (in its order, which is the read priority) **replace**
   the pending queue. Frames already cached or being read are left out. Nothing is queued before
   the first read: assigning or changing a strategy while the cache is empty only resets state, so
   constructing a backend never starts background work on its own.
2. Up to `MaxConcurrentPreloads` workers (default 1) take frames from the front of the queue,
   each reading through its own `PreloadReader`, and store them in the cache.
3. Frames already being read when the queue is replaced simply finish; nothing is cancelled.
   A strategy change also just rebuilds the queue (and the LRU order).
4. Workers exit when the queue is empty, and start again when new targets arrive. Lowering
   `MaxConcurrentPreloads` takes effect after the frames in flight.
5. `Dispose` waits (up to 10 s) for in-flight background reads before the backend releases the
   resources they use.

Because the queue follows the latest access, reading tracks the cursor: after a jump, the frames
around the new position are read next. While a whole-file fill has every remaining frame queued
already, the queue is rebuilt only when the cursor has moved 32 frames or more from where it was
last built, so scrolling does not re-enumerate the whole dataset on every access.

```csharp
// Via the backend (not exposed on IMatrixData)
if (md.GetDiagnosticCacheableList() is VirtualFrames<ushort> vf)
    vf.MaxConcurrentPreloads = 4;
```

---

## 4. Creating Virtual-backed MatrixData {#creating-virtual-backed-matrixdata}

### AsVirtualBuilder — New writable vessel

`MxBinaryFormat.AsVirtualBuilder(width, height, frameCount)` is the primary factory for
creating a new writable, MMF-backed `MatrixData<T>`.

```csharp
// Create an MMF-backed writable MatrixData — peak RAM stays near one frame
var builder = MxBinaryFormat.AsVirtualBuilder(32, 32, 1_209_600);
using var md = builder.CreateWritable<float>("weather.mxd");
md.DefineDimensions(axes);
md.SetXYScale(0, 31, 0, 31);

// Write frames directly — each write goes to the MMF
for (int f = 0; f < md.FrameCount; f++)
    md.GetArray(f).AsSpan().Fill(f + 1f);

md.Flush();  // commit to OS
```

Passing `null` as the path creates a temporary file that is auto-deleted on Dispose.
`OmeTiffFormat.AsVirtualBuilder(spec)` provides the same pattern for a writable OME-TIFF vessel.

### CreateAsVirtualFrames — Wrapping an existing backend

Lower-level factory used by format readers and the Clone path. It accepts any `VirtualFrames<T>`:

```csharp
// After building offsets/byteCounts from a file scan:
var vf = new StrippedMmfFrames<float>(
    path, w, h, offsets, byteCounts, isYFlipped: false, isBigEndian: false);
var md = MatrixData<float>.CreateAsVirtualFrames(w, h, vf);
```

If `vf` is not yet owned, `md` claims it (`vf.IsOwned = true`, `md.RequiresDisposal == true`) and
calls `vf.Dispose()` when `md` is disposed. A `MatrixData<T>` that shares an already-owned backend
does not dispose it.

---

## 5. Reading an Existing File as Virtual {#reading-an-existing-file-as-virtual}

### .mxd files

```csharp
// Non-compressed .mxd — mounts as read-only MMF, no pixel copy
var md = MatrixDataSerializer.LoadVirtual<float>("large.mxd");

// Type-unknown variant
IMatrixData md = MatrixDataSerializer.LoadDynamicVirtual("large.mxd");
```

The `.mxd` file must be **non-compressed**. A compressed `.mxd` throws `NotSupportedException`.

### Other formats (plugin readers)

Format plugins implement `IVirtualLoadable` to support virtual reading.
`VirtualPolicy` decides whether `LoadingMode.Auto` becomes Virtual or InMemory:

```csharp
var format = new OmeTiffFormat { LoadingMode = LoadingMode.Auto };
var md = format.Read("large.ome.tif");   // Virtual if file > 2 GB or frames > 1000
```

### Which backend the TIFF readers choose

| Reader | File layout | Virtual backend |
|---|---|---|
| OME-TIFF | Uncompressed, strips | `StrippedMmfFrames<T>` |
| OME-TIFF | Uncompressed, tiles | `TiledMmfFrames<T>` |
| OME-TIFF | Compressed | `TiffDecodedFrames<T>` (Lazy decode) |
| ImageJ TIFF | Uncompressed single-IFD "packed" stack | `StrippedMmfFrames<T>` (fixed-stride offsets) |
| ImageJ TIFF | Compressed multi-IFD | `TiffDecodedFrames<T>` (Lazy decode) |
| ImageJ TIFF | Uncompressed multi-IFD (not packed) | Always InMemory (no IFD offset scan implemented) |

---

## 6. Saving and the Fast-Path {#saving-and-the-fast-path}

When a `MatrixData<T>` backed by a `WritableStrippedMmfFrames<T>` whose file is a `.mxd` is saved
via `MxBinaryFormat` without compression, **no pixel data is copied**. Instead:

1. Dirty frames are flushed to the MMF.
2. The underlying file is moved to the destination path (`File.Move` — O(1) on the same volume).
3. A JSON config trailer is appended and the 20-byte header is back-patched.

```csharp
// Fast-path: file-move + trailer write (no pixel copy regardless of file size)
md.SaveAs("output.mxd", new MxBinaryFormat());
```

Any other backend (read-only MMF, Lazy decode, InMemory) goes through a normal frame-by-frame
write.

> **⚠ Compression and fast-path are mutually exclusive.**
> `MxBinaryFormat { CompressionInWrite = true }` always falls back to full pixel copy,
> and the resulting file cannot be opened with `LoadVirtual`.

> **Read-only MMF guard:** `SaveAs` refuses to overwrite the file that currently backs read-only
> MMF data (it would truncate the file mid-read). Choose a different destination.

---

## 7. Cloning Virtual Data {#cloning-virtual-data}

`MatrixData<T>.Clone()` (also exposed as `Duplicate()`) dispatches on `IsVirtual`:

- **IsVirtual == true** → `CloneAsVirtual()`: creates a new temp `.mxd` vessel, copies frames
  one at a time (peak RAM ≈ one frame), and returns a clone backed by `WritableStrippedMmfFrames<T>`.
- **IsVirtual == false** → `CloneInMemory()`: deep copy into managed heap.

Because `IsVirtual` is dynamic for Lazy-decode data, a compressed TIFF clones as Virtual while its
background fill is still in progress, and as InMemory once every frame is already in RAM.
Pass `forceInMemory: true` to always get an in-memory copy.

```csharp
// Clone a 15 GB virtual dataset — no OOM risk
var clone = md.Clone();  // or md.Duplicate()

// The clone is independent; SaveAs uses the fast-path
clone.SaveAs("snapshot.mxd", new MxBinaryFormat());
```

### Progress and cancellation

Both `Clone` and `Duplicate` have an overload that reports per-frame progress and honors a
`CancellationToken`, for either dispatch path (Virtual or InMemory):

```csharp
var progress = new Progress<int>(i => Console.WriteLine($"Cloned frame {i}"));
using var cts = new CancellationTokenSource();

var clone = md.Clone(forceInMemory: false, progress, cts.Token);
// or: md.Duplicate(forceInMemory: false, progress, cts.Token);
```

If cancelled mid-copy, `CloneAsVirtual` disposes and deletes the partially-written temp vessel
before the `OperationCanceledException` propagates — no orphaned multi-GB temp file is left behind.

### ValueRange propagation

`CloneAsVirtual` propagates any already-cached per-frame `ValueRange` (Min/Max) from the source
to the clone, mirroring what `CloneInMemory` already does via `SetArray`'s `minValues`/`maxValues`
parameters. Only frames whose range was actually cached on the source are copied — this reuses
prior work, it never triggers a scan. A frame the source never scanned stays "not yet calculated"
on the clone too, and will lazily scan on first access as usual.

### Current limitation: source format matching

Clone currently always uses `.mxd` as the temp vessel format, regardless of the source format.
This means that if the source is an OME-TIFF virtual file and the clone is saved as `.ome.tif`,
the fast-path cannot be used (the clone's backing file is `.mxd`, not `.ome.tif`).

This limitation will be resolved by the planned `IVesselCreatable` interface, which will allow
`CloneAsVirtual` to create a temp vessel in the same format as the source.

**Current fast-path matrix:**

| Source format | Clone temp | SaveAs target | Fast-path? |
|---|---|---|---|
| `.mxd` | `.mxd` | `.mxd` | ✅ File.Move |
| InMemory | `.mxd` (default) | `.mxd` | ✅ File.Move |
| `.ome.tif` | `.mxd` (current) | `.ome.tif` | ❌ Re-encode (planned fix) |
| `.ome.tif` | `.ome.tif` (planned) | `.ome.tif` | ✅ File.Move (after `IVesselCreatable`) |

---

## 8. VirtualPolicy — Threshold Configuration {#virtualpolicy}

`VirtualPolicy` is a static class in `MxPlot.Core.IO` that centralizes the threshold for
automatic Virtual-vs-InMemory decisions. (Not to be confused with `VirtualCachePolicy`, which
sizes the cache *after* Virtual has been chosen.)

```csharp
// Defaults
VirtualPolicy.ThresholdBytes  = 2L * 1024 * 1024 * 1024;  // 2 GB
VirtualPolicy.ThresholdFrames = 1000;

// Override at app startup (e.g., based on config file or available RAM)
VirtualPolicy.ThresholdBytes = 512L * 1024 * 1024;  // 512 MB for low-memory environments

// Resolve manually
LoadingMode resolved = VirtualPolicy.Resolve(LoadingMode.Auto, fileSizeBytes, frameCount, canVirtual: true);
// Returns LoadingMode.Virtual or LoadingMode.InMemory
```

- `Resolve` returns the requested mode unchanged unless it is `Auto`.
- `canVirtual: false` forces `InMemory` for `Auto` (e.g. a compressed `.mxd`).
- Virtual is chosen when the file size exceeds `ThresholdBytes` **or** the frame count exceeds
  `ThresholdFrames`.

Format readers call `VirtualPolicy.Resolve()` inside their `IVirtualLoadable` read path.
Application code should generally set the thresholds once at startup and let readers handle the rest.

> **Process-wide values.** The thresholds are read when each load resolves its mode. Changing them
> while another thread is loading with `LoadingMode.Auto` is not an error — that load gets either
> mode, and both are valid — but which one is unpredictable. If your code depends on a specific
> mode, pass `LoadingMode.InMemory` or `LoadingMode.Virtual` explicitly, and dispose the result
> whenever `RequiresDisposal` is `true` instead of assuming `Auto` produced in-memory data.
> (Tests that temporarily lower a threshold must not run in parallel with other loading tests.)

---

## 9. Diagnostics and UI Integration {#diagnostics}

`IMatrixData` exposes one backend-agnostic accessor for cache control and diagnostics:

```csharp
ICacheableFrameList? cacheable = md.GetDiagnosticCacheableList();
```

It returns the backend directly, or looks through a `RoutedFrames<T>` wrapper to find it. It
returns `null` for InMemory data. To tell MMF apart from other backends, pattern-match against the
marker interface:

```csharp
switch (md.GetDiagnosticCacheableList())
{
    case IMmfFrameList mmf:
        // MMF-backed — mmf.SourcePath is the mapped file
        break;
    case ILazyDataSource lazy:
        // Another on-demand backend (e.g. Lazy decode) — lazy.StoredFrameCount / Count shows fill progress
        break;
    case null:
        // InMemory
        break;
}
```

`MatrixPlotter` (MxPlot.UI.Avalonia) uses this to label the data in its status bar:

| Backend | Badge |
|---|---|
| InMemory | *(none)* |
| MMF, read-only | `(Mapped)` in blue — click to open the Cache Monitor |
| MMF, writable | `(Mapped)` in red — click to open the Cache Monitor |
| Other Virtual (e.g. Lazy decode) | `(Cached NN%)` in amber, updated live. The wording is "Cached", not "Loading", because data larger than the cache capacity may never reach 100%. The badge disappears once every frame is cached (`IsVirtual` becomes `false`). |

`MatrixPlotter` also subscribes to `IsVirtualChanged` on the current data's backend (reached via
`GetDiagnosticCacheableList() as ILazyDataSource`). When a Lazy-decode dataset finishes filling,
it re-derives the All-mode value range with a full scan and clears the "not all frames scanned"
asterisk, since nothing else signals that moment.

When an orthogonal view enters Volume mode on Virtual data, `MatrixPlotter` saves the data's
current `CacheStrategy` and `CacheCapacity`, grows the capacity for the working set (using
`VolumeModeMemoryBudgetFraction`), and switches to `DimensionStrategy` in Volume mode. On exit it
restores the saved strategy first, then the capacity via `TrimCacheTo`. This works for any
`ICacheableFrameList` backend, not only MMF.

---

## 10. Known Limitations and Planned Work {#known-limitations}

| Item | Status | Notes |
|---|---|---|
| `IVesselCreatable` interface | ❌ Not yet implemented | Needed for OME-TIFF clone fast-path. |
| `CloneAsVirtual` format-matching | ❌ Pending `IVesselCreatable` | Clone always creates `.mxd` temp vessel currently. |
| `TiledMmfFrames` write support | ❌ Not planned | Tiled write-back is complex; use stripped for writable vessels. |
| Compressed `.mxd` virtual load | ❌ Not supported | `LoadVirtual` requires non-compressed `.mxd`. |
| Compressed TIFF virtual load | ✅ Lazy decode | `TiffDecodedFrames<T>` (OME-TIFF and ImageJ). |
| Lazy decode throughput | ✅ Parallel | One LibTiff handle per background worker; `MaxConcurrentPreloads` defaults to half the cores. |
| TIFF with more than 32,767 frames | ✅ Supported | Frames addressed by IFD offset (`TiffIfdIndex`). |
| Background preloads racing `WriteDirectly` | ⚠ Open | A frame read before a direct write can land in the cache after it; needs per-frame versioning. |
| Uncompressed non-packed ImageJ TIFF | ⚠ InMemory only | No IFD offset scan for the MMF route in `ImageJTiffHandler`. |
| Cache Monitor window | ⚠ MMF only | Lazy-decode data shows the `(Cached NN%)` badge but has no monitor window. |
| Cross-volume `SaveAs` fast-path | ⚠ Falls back to copy | `File.Move` fails across volumes; `SaveAs` falls back to `MatrixDataSerializer.Save`. |
