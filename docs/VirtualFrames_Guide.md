# VirtualFrames Guide

**MxPlot.Core — MMF-backed Virtual Storage Architecture**

> Last Updated: 2026-04-24

*This document covers the design and usage of the Virtual (MMF-backed) storage layer in MxPlot.Core.
For how to consume Virtual data from a file format plugin, see the
[Extension Development Guide](./BluePaper_MxPlot_Extensions_Guide.md) §3.4.*

---

## Table of Contents

1. [What is "Virtual" in MxPlot?](#what-is-virtual)
2. [Storage Backend Classes](#storage-backend-classes)
3. [Creating Virtual-backed MatrixData](#creating-virtual-backed-matrixdata)
4. [Reading an Existing File as Virtual](#reading-an-existing-file-as-virtual)
5. [Saving and the Fast-Path](#saving-and-the-fast-path)
6. [Cloning Virtual Data](#cloning-virtual-data)
7. [VirtualPolicy — Threshold Configuration](#virtualpolicy)
8. [Known Limitations and Planned Work](#known-limitations)

---

## 1. What is "Virtual" in MxPlot? {#what-is-virtual}

A **Virtual** `MatrixData<T>` keeps pixel data in a **Memory-Mapped File (MMF)** rather than
in managed heap memory. Frames are paged in on demand through an LRU frame cache.

Key properties compared to InMemory:

| Property | InMemory | Virtual (MMF-backed) |
|---|---|---|
| Allocation time | Proportional to total size | Near-instant (OS pre-allocates) |
| RAM usage at rest | = total pixel data | ≈ cache size (≤ 16 frames by default) |
| Save (`.mxd`) | Full pixel copy to disk | File-move + trailer write (O(1)) |
| Max practical size | Limited by available RAM | Limited by available disk space |
| `IsVirtual` property | `false` | `true` |

A 4.95 GB dataset (1,209,600 frames × 32×32 float) benchmarked on Core i9-14900KF:

| Step | Virtual | InMemory |
|---|---|---|
| Create / allocate | 764 ms | 2,706 ms |
| SaveAs to disk (4,725 MB) | **61 ms** (file-move fast-path) | 45,857 ms (full copy) |
| LoadVirtual (reopen) | 644 ms | 1,043 ms |

---

## 2. Storage Backend Classes {#storage-backend-classes}

All backend classes live in `MxPlot.Core.IO`. They are **internal implementation details**;
consumers always interact with `MatrixData<T>`.

```
IVirtualFrames<T>                   ← read-only frame access contract
├── VirtualStrippedFrames<T>        ← read-only, strip layout (most formats)
└── VirtualTiledFrames<T>           ← read-only, tile layout (tiled TIFF, etc.)

IWritableVirtualFrames<T>           ← read-write extension
└── WritableVirtualStrippedFrames<T>   ← read-write, strip layout
    (used by AsVirtualBuilder and Clone)
```

### VirtualStrippedFrames\<T\> (read-only)

- Opens the file as a **read-only MMF**.
- `offsets[frameIndex][stripIndex]` + `byteCounts[frameIndex][stripIndex]` describe the layout.
- For most uncompressed single-strip-per-frame formats, the inner array has exactly one element.
- LRU frame cache with pluggable `ICacheStrategy` (default: `NeighborStrategy`).

### WritableVirtualStrippedFrames\<T\> (read-write)

- Opens the file as a **ReadWrite MMF**.
- Created by `MatrixDataSerializer.CreateTempVessel<T>()` or `MxBinaryFormat.AsVirtualBuilder()`.
- Can be marked `isTemporary: true` — auto-deletes on `Dispose`.
- `Flush()` commits dirty pages to the OS.
- `SaveAs(dest)` — fast-path: `File.Move` + trailer finalization (no pixel copy).

### VirtualTiledFrames\<T\> (read-only)

- Same as `VirtualStrippedFrames<T>` but assembles rectangular tiles into a contiguous frame buffer on read.
- Handles right/bottom edge clipping automatically.

---

## 3. Creating Virtual-backed MatrixData {#creating-virtual-backed-matrixdata}

### AsVirtualBuilder — New writable vessel

`MxBinaryFormat.AsVirtualBuilder(width, height, frameCount)` is the primary factory for
creating a new WritableVirtual `MatrixData<T>`.

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

Passing `null` (or omitting the path) creates a temporary file that is auto-deleted on Dispose.

### CreateAsVirtualFrames — Wrapping an existing backend

Lower-level factory used by format readers and the Clone path:

```csharp
// After building offsets/byteCounts from a file scan:
var vf = new VirtualStrippedFrames<float>(path, w, h, offsets, byteCounts, isYFlipped: false);
var md = MatrixData<float>.CreateAsVirtualFrames(w, h, vf);
```

The `MatrixData<T>` takes ownership of `vf`; `vf.Dispose()` is called when `md` is disposed.

---

## 4. Reading an Existing File as Virtual {#reading-an-existing-file-as-virtual}

### .mxd files

```csharp
// Non-compressed .mxd — mounts as read-only MMF, no pixel copy
var md = MatrixDataSerializer.LoadVirtual<float>("large.mxd");

// Type-unknown variant
IMatrixData md = MatrixDataSerializer.LoadDynamicVirtual("large.mxd");
```

Requirements: the `.mxd` file must be **non-compressed** (the default for files written via the
fast-path or `MxBinaryFormat` with `CompressionInWrite = false`).

### Other formats (plugin readers)

Format plugins implement `IVirtualLoadable` to support virtual reading.
`VirtualPolicy` decides automatically whether to use Virtual or InMemory mode:

```csharp
var format = new OmeTiffFormat { LoadingMode = LoadingMode.Auto };
var md = format.Read("large.ome.tif");   // Virtual if file > 2 GB or frames > 1000
```

### VirtualPolicy thresholds

```csharp
// Global defaults (change at app startup)
VirtualPolicy.ThresholdBytes  = 2L * 1024 * 1024 * 1024;  // 2 GB
VirtualPolicy.ThresholdFrames = 1000;

// Manual resolve
var mode = VirtualPolicy.Resolve(LoadingMode.Auto, fileBytes, frameCount);
```

---

## 5. Saving and the Fast-Path {#saving-and-the-fast-path}

When a `WritableVirtualStrippedFrames<T>`-backed `MatrixData<T>` is saved via `MxBinaryFormat`,
**no pixel data is copied**. Instead:

1. Dirty frames are flushed to the MMF.
2. The underlying temp file is moved to the destination path (`File.Move` — O(1) on the same volume).
3. A JSON config trailer is appended and the 20-byte header is back-patched.

```csharp
// Fast-path: file-move + trailer write (no pixel copy regardless of file size)
md.SaveAs("output.mxd", new MxBinaryFormat());

// Equivalent:
MatrixDataSerializer.Save("output.mxd", md, compress: false);
// ^ also uses fast-path internally when the backing store is WVSF
```

> **⚠ Compression and fast-path are mutually exclusive.**
> `MxBinaryFormat { CompressionInWrite = true }` always falls back to full pixel copy,
> and the resulting file cannot be opened with `LoadVirtual`.

---

## 6. Cloning Virtual Data {#cloning-virtual-data}

`MatrixData<T>.Clone()` (also exposed as `Duplicate()`) dispatches automatically:

- **IsVirtual == true** → `CloneAsVirtual()`: creates a new temp `.mxd` vessel, copies frames
  frame-by-frame through the MMF (peak RAM ≈ one frame), returns a WVSF-backed clone.
- **IsVirtual == false** → `CloneInMemory()`: deep copy into managed heap.

```csharp
// Clone a 15 GB virtual dataset — no OOM risk
var clone = md.Clone();  // or md.Duplicate()

// The clone is independent; SaveAs uses the fast-path
clone.SaveAs("snapshot.mxd", new MxBinaryFormat());
```

### Current limitation: source format matching

Clone currently always uses `.mxd` as the temp vessel format, regardless of the source format.
This means that if the source is an OME-TIFF virtual file and the clone is saved as `.ome.tif`,
the fast-path cannot be used (the WVSF backing is `.mxd`, not `.ome.tif`).

This limitation will be resolved by the planned `IVesselCreatable` interface, which will allow `CloneAsVirtual` to create a temp vessel
in the same format as the source.

**Current fast-path matrix:**

| Source format | Clone temp | SaveAs target | Fast-path? |
|---|---|---|---|
| `.mxd` | `.mxd` | `.mxd` | ✅ File.Move |
| InMemory | `.mxd` (default) | `.mxd` | ✅ File.Move |
| `.ome.tif` | `.mxd` (current) | `.ome.tif` | ❌ Re-encode (planned fix) |
| `.ome.tif` | `.ome.tif` (planned) | `.ome.tif` | ✅ File.Move (after `IVesselCreatable`) |

---

## 7. VirtualPolicy — Threshold Configuration {#virtualpolicy}

`VirtualPolicy` is a static class in `MxPlot.Core.IO` that centralizes the threshold for
automatic Virtual-vs-InMemory decisions.

```csharp
// Defaults
VirtualPolicy.ThresholdBytes  = 2L * 1024 * 1024 * 1024;  // 2 GB
VirtualPolicy.ThresholdFrames = 1000;

// Override at app startup (e.g., based on config file or available RAM)
VirtualPolicy.ThresholdBytes = 512L * 1024 * 1024;  // 512 MB for low-memory environments

// Resolve manually
LoadingMode resolved = VirtualPolicy.Resolve(LoadingMode.Auto, fileSizeBytes, frameCount);
// Returns LoadingMode.Virtual or LoadingMode.InMemory
```

Format readers call `VirtualPolicy.Resolve()` inside their `IVirtualLoadable.Read()` implementation.
Application code should generally set the thresholds once at startup and let readers handle the rest.

---

## 8. Known Limitations and Planned Work {#known-limitations}

| Item | Status | Notes |
|---|---|---|
| `IVesselCreatable` interface | ❌ Not yet implemented | Needed for OME-TIFF clone fast-path.  |
| `CloneAsVirtual` format-matching | ❌ Pending `IVesselCreatable` | Clone always creates `.mxd` temp vessel currently. |
| `VirtualTiledFrames` write support | ❌ Not planned | Tiled write-back is complex; use stripped for writable vessels. |
| Compressed virtual load | ❌ Not supported | `LoadVirtual` requires non-compressed `.mxd`. |
| Cross-volume `SaveAs` fast-path | ⚠ Falls back to copy | `File.Move` fails across volumes; `SaveAs` falls back to `MatrixDataSerializer.Save`. |