# VolumeAccessor<T> Guide

**MxPlot.Core 3D Volume Operations Reference**

> Last Updated: 2026-08-31  
> Version: 0.3.0

## 📚 Table of Contents

1. [Introduction](#introduction)
2. [Basic Operations](#basic-operations)
3. [Projections and Reductions](#projections-and-reductions)
4. [Pipeline Examples](#pipeline-examples)
5. [Performance](#performance)
6. [Limitations](#limitations)
7. [Method Reference](#method-reference)

---

## Introduction

`VolumeAccessor<T>` provides high-performance, read-only access to 3D volumetric data stored in `MatrixData<T>`.

### Key Features

- ✅ **Zero-copy access**: Direct 3D access via `vol[x, y, z]`
- ✅ **Orthogonal views**: Volume reorganization from different viewpoints
- ✅ **Fast projections**: MIP/MinIP/AIP projections (parallel processing)
- ✅ **Custom reductions**: Axis reduction with user-defined functions
- ✅ **Memory efficiency**: ArrayPool and Span-based design

### Creating a VolumeAccessor

```csharp
// Create 3D data: 512×512, 100 frames
var data = new MatrixData<float>(512, 512, 100);
data.DefineDimensions(Axis.Z(100, 0, 50, "µm"));

// Fill data
for (int z = 0; z < 100; z++)
    data.Set(z, (ix, iy, x, y) => (float)Math.Sin(x * 0.1) * Math.Cos(y * 0.1) * (z + 1));

// Obtain VolumeAccessor
var volume = data.AsVolume();

// Voxel access
float value = volume[256, 256, 50];  // [x, y, z]
```

---

## Basic Operations

### 1. Restack - XZ/YZ Data Generation

Reorganize the volume to view from different directions.

```csharp
// View from X direction: Stack YZ planes
var viewFromX = volume.Restack(ViewFrom.X);
// Result: Width=Y, Height=Z, Frames=X

// View from Y direction: Stack XZ planes
var viewFromY = volume.Restack(ViewFrom.Y);
// Result: Width=X, Height=Z, Frames=Y
```

**Use cases**: Inspect 3D data from multiple directions, generate XZ/YZ plane data

**Output mode, progress, and cancellation**: `Restack` also accepts an optional
`LoadingMode outputMode` (`Auto` by default, deferring to `VirtualPolicy` based on the restacked
volume's size), an `IProgress<int>? progress`, and a `CancellationToken`:

```csharp
var progress = new Progress<int>(i => Console.WriteLine($"Restacked frame {i}"));
using var cts = new CancellationTokenSource();

var large = volume.Restack(ViewFrom.X, LoadingMode.Auto, progress, cts.Token);
```

For `X`/`Y` (a genuine transpose) resolved to `Virtual` output, the write path buffers a
RAM-bounded band of output frames in managed memory before handing each completed frame to the
MMF vessel as one contiguous write — writing scattered individual rows directly into a freshly
created multi-GB sparse MMF was measured to be catastrophically slow. `Z` (already frame-ordered)
always streams one frame at a time, so its peak memory stays at one frame regardless of
`outputMode`. See `VolumeAccessor.Restack`'s XML doc and `docs/MatrixData_MethodCallMap.md` for
the full accounting.

### 2. SliceAt - 2D Cross-section Extraction

Extract a 2D plane at a specific index.

```csharp
// Extract XZ plane at Y=128
var xzSlice = volume.SliceAt(ViewFrom.Y, 128);

// Extract YZ plane at X=256
var yzSlice = volume.SliceAt(ViewFrom.X, 256);
```

**Use cases**: Examine specific cross-sections, export representative slices

**Buffer reuse**: for `X`/`Y`, `SliceAt` also accepts an optional `IMatrixData? dst` and
`int dstIndex`. When `dst`'s frame at `dstIndex` is already large enough, the slice is written
straight into it and the returned wrapper shares that buffer — a caller re-slicing repeatedly
into an on-screen frame (e.g. an orthogonal side view following the crosshair) allocates nothing
and keeps the same instance displayed. Ignored for `ViewFrom.Z`, which already returns a frame by
reference with no copy at all.

```csharp
// First call: no dst yet, allocates.
var xzSlice = volume.SliceAt(ViewFrom.Y, 128);

// Later calls: reuse xzSlice's own frame buffer -- no per-update allocation.
volume.SliceAt(ViewFrom.Y, 130, dst: xzSlice, dstIndex: 0);
```

---

## Projections and Reductions

### CreateProjection - Built-in Projections

Provides optimized fast projections. Results are **reference copies (zero-copy)**.

```csharp
using MxPlot.Core.Processing;

// Maximum Intensity Projection (MIP)
var mip = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);

// Minimum Intensity Projection (MinIP)
var minip = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Minimum);

// Average Intensity Projection (AIP)
var aip = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Average);
```

**Projection Modes**:
- `Maximum`: Detect maximum value (MIP)
- `Minimum`: Detect minimum value (MinIP)
- `Average`: Calculate average value (AIP)

**Requirements**: Type `T` is limited to types that implement `INumber<T>` and `IMinMaxValue<T>`.

### ReduceAlong - Custom Reductions

Perform axis reduction with user-defined functions.

```csharp
// Standard deviation map
var stdDevMap = volume.ReduceAlong(ViewFrom.X,
    (ix, iy, x, y, axis, values) =>
    {
        double mean = 0;
        foreach (var v in values)
            mean += v;
        mean /= values.Length;
        
        double variance = 0;
        foreach (var v in values)
        {
            double diff = v - mean;
            variance += diff * diff;
        }
        return (float)Math.Sqrt(variance / values.Length);
    });

// Median filter
var medianProj = volume.ReduceAlong(ViewFrom.X,
    (ix, iy, x, y, axis, values) =>
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    });
```

**Delegate Signature**:
```csharp
public delegate T ReduceFunc(
    int ix,                     // Grid index X
    int iy,                     // Grid index Y
    double x,                   // Spatial coordinate X
    double y,                   // Spatial coordinate Y
    Axis axis,                  // Reduction axis (with scale info)
    ReadOnlySpan<T> values      // Values along axis
);
```

---

## Pipeline Examples

### Example 1: Multi-channel Data Processing

```csharp
// Original data: 512×512, Time(100)×Channel(3)
var multiDim = new MatrixData<float>(512, 512, 300);
multiDim.DefineDimensions(
    Axis.Time(100, 0, 10, "s"),
    Axis.Channel(3)
);

// Extract channel 0 and create MIP
var channel0 = multiDim.ExtractAlong("Channel", 0);
var mip = channel0.AsVolume().CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);
```

### Example 2: Orthogonal View (Interactive Slicer)

```csharp
// Extract orthogonal cross-sections at specific position
var volume = data.AsVolume();  // 256×256×64 volume

// Class to manage orthogonal views
public class OrthogonalViews
{
    public MatrixData<float> TopView { get; set; }    // XY plane
    public MatrixData<float> SideView { get; set; }   // XZ plane
    public MatrixData<float> FrontView { get; set; }  // YZ plane
}

// Get cross-sections at specified position
OrthogonalViews GetOrthogonalViews(VolumeAccessor<float> volume, int x, int y, int z)
{
    return new OrthogonalViews
    {
        TopView = volume.SliceAt(ViewFrom.Z, z),   // XY plane at Z=z
        SideView = volume.SliceAt(ViewFrom.Y, y),  // XZ plane at Y=y
        FrontView = volume.SliceAt(ViewFrom.X, x)  // YZ plane at X=x
    };
}

// Example: Update dynamically based on mouse position
void OnMouseMove(int mouseX, int mouseY, int mouseZ)
{
    var views = GetOrthogonalViews(volume, mouseX, mouseY, mouseZ);
    
    // Display each view
    DisplayImage(views.TopView, "Top View (XY)");
    DisplayImage(views.SideView, "Side View (XZ)");
    DisplayImage(views.FrontView, "Front View (YZ)");
}

// ImageJ-style viewer with crosshair
void UpdateCrosshairViews(int cursorX, int cursorY, int cursorZ)
{
    var views = GetOrthogonalViews(volume, cursorX, cursorY, cursorZ);
    
    // Display with crosshair overlays
    DrawWithCrosshair(views.TopView, cursorX, cursorY);
    DrawWithCrosshair(views.SideView, cursorX, cursorZ);
    DrawWithCrosshair(views.FrontView, cursorY, cursorZ);
}
```

### Example 3: Time Series Analysis

```csharp
// Calculate standard deviation over time
var temporalData = multiDim.ExtractAlong("Channel", 0);
var volume = temporalData.AsVolume();

var stdDevMap = volume.ReduceAlong(ViewFrom.Z, 
    (ix, iy, x, y, timeAxis, timeValues) =>
    {
        double mean = timeValues.ToArray().Average(v => (double)v);
        double variance = timeValues.ToArray().Average(v => 
        {
            double diff = (double)v - mean;
            return diff * diff;
        });
        return (float)Math.Sqrt(variance);
    });
```

---

## Performance

### Benchmark Results

**Test Environment**: Intel Core i9-14900KF (24 cores, 32 threads), DDR5-5600 64GB, Windows 11

#### 256×256×64 Ushort (16bit integer)

| Projection Direction | MIP | MinIP | AIP |
|---------------------|-----|-------|-----|
| **XY (Z projection)** | 2.02 ms | 1.77 ms | 1.60 ms |
| **XZ (Y projection)** | 0.51 ms | 0.54 ms | 0.48 ms |
| **YZ (X projection)** | 0.42 ms | 0.43 ms | 0.48 ms |

#### 256×256×64 Float (32bit floating point)

| Projection Direction | MIP | MinIP | AIP |
|---------------------|-----|-------|-----|
| **XY (Z projection)** | 1.24 ms | 1.14 ms | 1.11 ms |
| **XZ (Y projection)** | 0.71 ms | 0.68 ms | 0.65 ms |
| **YZ (X projection)** | 0.46 ms | 0.45 ms | 0.63 ms |

**Real-time display performance**: Simultaneous display of 3-directional (Z, X, Y) MIP can achieve **100+ FPS** (ushort)

### Optimization Tips

1. **Prioritize built-in projections**: `CreateProjection` is zero-copy optimized
2. **Choose projection direction**: X/Y projections are faster than Z projection (depends on data access pattern)
3. **Reuse VolumeAccessor**: Use the same instance for multiple operations
4. **Chunk large data**: `Restack` creates a full copy — for very large volumes, pass
   `LoadingMode.Virtual` (or let `Auto`/`VirtualPolicy` decide) to keep peak RAM near one
   band's worth of frames instead of the whole output

```csharp
// ✅ Recommended (zero-copy projection)
var mip = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);

// ❌ Not recommended (slow, lambda overhead)
var mip = volume.ReduceAlong(ViewFrom.Z, (ix, iy, x, y, z, vals) => 
    vals.ToArray().Max());
```

---

## Limitations

### Axis Specification

`AsVolume()` constructs a volume with a single axis, but requires an axis name if multiple axes exist.

```csharp
// ✅ Valid: Single axis (auto-detected)
var data = new MatrixData<float>(512, 512, 100);
data.DefineDimensions(Axis.Z(100, 0, 50, "µm"));
var vol = data.AsVolume();  // OK - axis name not required

// ✅ Valid: Multiple axes (specify axis name)
var multiAxis = new MatrixData<float>(512, 512, 300);
multiAxis.DefineDimensions(
    Axis.Z(100, 0, 50, "µm"),
    Axis.Time(3, 0, 5, "s")
);
var volZ = multiAxis.AsVolume("Z");     // OK - volume along Z axis
var volTime = multiAxis.AsVolume("Time"); // OK - volume along Time axis

// ✅ Valid: Multiple axes + base index specification
var volAtTime1 = multiAxis.AsVolume("Z", baseIndices: new[] { 0, 1 });
// Z-axis volume at Time=1

// ❌ Invalid: Multiple axes without axis name
// var vol = multiAxis.AsVolume();  // InvalidOperationException
```

### Memory Usage

- `Restack` creates a full copy of the output.
  - `LoadingMode.InMemory` (or `Auto` resolving to it): requires managed heap memory equal to
    the output size, on top of the source — 512×512×100 `float`: about 100MB × 2 = 200MB.
  - `LoadingMode.Virtual` (or `Auto` resolving to it for a large enough output): the output lives
    in an MMF; for `X`/`Y` peak *managed* memory is bounded by one band of output frames (a
    fraction of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`), not the full output size — see
    [VirtualFrames Guide](VirtualFrames_Guide.md). `Z` always streams one frame at a time.
- Consider sub-region processing for large volumes

### Thread Safety

- VolumeAccessor is read-only and thread-safe for reading
- Do not modify the original MatrixData while using VolumeAccessor
- Multiple VolumeAccessors can safely access the same data

---

## Method Reference

### VolumeAccessor<T>

#### Indexer
```csharp
public T this[int ix, int iy, int iz] { get; }
```
Direct voxel access with zero-copy.

#### Core Methods

```csharp
public MatrixData<T> Restack(ViewFrom direction,
    LoadingMode outputMode = LoadingMode.Auto,
    IProgress<int>? progress = null,
    CancellationToken cancellationToken = default)
```
Reorganize volume from different viewpoint. Creates a full copy — in-memory or MMF-backed
(`Virtual`) depending on `outputMode` and, for `Auto`, `VirtualPolicy`.

```csharp
public MatrixData<T> SliceAt(ViewFrom axis, int index, IMatrixData? dst = null, int dstIndex = 0)
```
Extract 2D cross-section at specified index. `dst`/`dstIndex` let the caller reuse an existing
frame buffer instead of allocating a new one (ignored for `ViewFrom.Z`).

```csharp
public MatrixData<T> ReduceAlong(ViewFrom axis, ReduceFunc op)
```
Axis reduction with custom function.

### VolumeOperator Extensions

```csharp
public static MatrixData<T> CreateProjection<T>(
    this VolumeAccessor<T> volume, 
    ViewFrom axis, 
    ProjectionMode mode)
    where T : unmanaged, INumber<T>, IMinMaxValue<T>
```

Optimized projections (MIP/MinIP/AIP).

### ViewFrom Enum

```csharp
public enum ViewFrom
{
    X,  // Orthogonal to YZ plane (view along X axis)
    Y,  // Orthogonal to XZ plane (view along Y axis)
    Z   // XY plane (normal top view)
}
```

### ProjectionMode Enum

```csharp
public enum ProjectionMode
{
    Maximum,  // Maximum Intensity Projection (MIP)
    Minimum,  // Minimum Intensity Projection (MinIP)
    Average   // Average Intensity Projection (AIP)
}
```

---

## Related Topics

- **[MatrixData Operations Guide](MatrixData_Operations_Guide.md)** - Basic data operations
- **[DimensionStructure Guide](DimensionStructure_MemoryLayout_Guide.md)** - Memory layout details
- [API Reference](../README.md) - Complete API documentation

---

**End of Guide**

*Last Updated: 2026-08-31  (Generated by GitHub Copilot)*
