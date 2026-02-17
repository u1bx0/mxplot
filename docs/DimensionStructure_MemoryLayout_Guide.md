# DimensionStructure and Memory Layout Guide

**MxPlot.Core Technical Reference**

> Last Updated: 2026-02-16  

## 📚 Table of Contents

1. [Overview](#overview)
2. [Memory Layout Fundamentals](#memory-layout-fundamentals)
3. [Innermost Implementation Details](#innermost-implementation-details)
4. [Stride Calculation](#stride-calculation)
5. [Axis Reconfiguration and Reordering](#axis-reconfiguration-and-reordering)
6. [Practical Examples](#practical-examples)
7. [Best Practices](#best-practices)
8. [FovAxis: Field-of-View Tiling](#fovaxis-field-of-view-tiling)
9. [Future Extensions](#future-extensions)

---

## Overview

`DimensionStructure` manages the relationship between **linear frame indices** in `MatrixData<T>`'s backing array (`List<T[]>`) and **multi-dimensional axis coordinates**. Understanding this mapping is crucial for efficient data access and interoperability with other scientific computing libraries.

### Key Design Principle

> **First axis = Fastest-varying (innermost) dimension**

This follows the **MATLAB/Fortran column-major convention** where the first dimension varies most rapidly in memory.

---

## Memory Layout Fundamentals

### Basic Concept

```csharp
// MatrixData structure
MatrixData<T> = [X × Y] + [Frame Axis 0, Frame Axis 1, ...]
                 ↑              ↑
            Spatial (2D)    Multi-dimensional frames
```

### Frame Array Storage

```csharp
// Internal storage
private List<T[]> _arraysList;  // Each T[] is a flattened 2D image (X×Y)
```

Each frame (`T[]`) represents a single XY plane at specific coordinates in the multi-dimensional space.

### Example: 4D Data (X, Y, Z, Time)

```csharp
var data = new MatrixData<double>(512, 512, 50);
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),      // axes[0] - innermost
    Axis.Time(5, 0, 10, "s")      // axes[1] - outermost
);
```

**Frame Order in `_arraysList`:**

| Frame Index | Z Index | Time Index | Memory Position |
|-------------|---------|------------|-----------------|
| 0           | 0       | 0          | _arraysList[0]  |
| 1           | 1       | 0          | _arraysList[1]  |
| 2           | 2       | 0          | _arraysList[2]  |
| ...         | ...     | ...        | ...             |
| 9           | 9       | 0          | _arraysList[9]  |
| 10          | 0       | 1          | _arraysList[10] |
| 11          | 1       | 1          | _arraysList[11] |
| ...         | ...     | ...        | ...             |
| 49          | 9       | 4          | _arraysList[49] |

**Pattern**: Z-index cycles first (0→9), then Time-index increments.

**Visualization**:

```
Frame:  [Z0T0] [Z1T0] [Z2T0] ... [Z9T0] [Z0T1] [Z1T1] ... [Z9T4]
Index:    0      1      2    ...   9      10     11    ...  49
```

---

## Innermost Implementation Details

### Why "First Axis = Fastest-Varying"?

The internal implementation of `DimensionStructure` makes this design clear.

#### Stride Array Initialization (`RegisterAxes` Method)

```csharp
private void RegisterAxes(params Axis[] axes)
{
    _axisList.Clear();
    _strides = new int[axes.Length];
    
    int currentStride = 1;

    // Important: axis[0] is the innermost (fastest-varying) dimension
    for (int i = 0; i < axes.Length; i++)
    {
        var axis = axes[i];
        _axisList.Add(axis);
        
        _strides[i] = currentStride;  // Cache the stride
        axis.IndexChanged += AxisIndex_Changed;
        
        currentStride *= axis.Count;  // Accumulate for next axis
    }
    
    // Validate total frame count
    if (currentStride != _md.FrameCount)
        throw new ArgumentException($"Total count mismatch.");
}
```

**Key Points**:
- `_strides[0] = 1` is **always assigned to the first axis**
- `_strides[i]` is the **cumulative product of all previous axes**
- When axes[0] index increases by 1, frameIndex also increases by 1

#### Frame Index Calculation (`GetFrameIndexAt` Method)

```csharp
public int GetFrameIndexAt(int[] indeces)
{
    if (_axisList.Count == 0) return 0;
    if (indeces.Length != _axisList.Count)
        throw new ArgumentException("Invalid length of indeces!");

    int newIndex = 0;
    // Dot product: sum of (index × stride)
    for (int i = 0; i < _axisList.Count; ++i)
    {
        newIndex += indeces[i] * _strides[i];
    }
    return newIndex;
}
```

**Example**: Z=3, Channel=2, Time=4

| Axis | Index | Stride | Contribution |
|------|-------|--------|--------------|
| Z | indeces[0] | 1 | indeces[0] × 1 |
| C | indeces[1] | 3 | indeces[1] × 3 |
| T | indeces[2] | 6 | indeces[2] × 6 |

```
frameIndex = Z×1 + C×3 + T×6
Example: (Z=2, C=1, T=3) → 2×1 + 1×3 + 3×6 = 23
```

Z index is **multiplied by 1**, so incrementing Z by 1 directly adds 1 to frameIndex → **Fastest-varying**

#### Reverse Conversion (`CopyAxisIndicesTo` Method)

```csharp
public void CopyAxisIndicesTo(Span<int> buffer, int frameIndex = -1)
{
    if (frameIndex == -1) frameIndex = _md.ActiveIndex;
    if (buffer.Length < this.AxisCount)
        throw new ArgumentException("Destination span is too short.");

    // Calculate each axis index
    for (int i = 0; i < _axisList.Count; i++)
    {
        // (frameIndex / stride[i]) % count[i]
        buffer[i] = (frameIndex / _strides[i]) % _axisList[i].Count;
    }
}
```

**Example**: Reverse conversion of frameIndex = 23

```
Z = (23 / 1) % 3 = 23 % 3 = 2
C = (23 / 3) % 2 = 7 % 2 = 1
T = (23 / 6) % 4 = 3 % 4 = 3
→ (Z=2, C=1, T=3)
```

---

## Stride Calculation

### What is a Stride?

A **stride** is the number of frames to skip to move to the next index in a specific axis.

### Formula

For axis `i`:
```
stride[i] = product of all axis counts before axis i
```

### Example: 3-Axis Data

```csharp
// Z=3, Channel=2, Time=4 (Total: 24 frames)
data.DefineDimensions(
    Axis.Z(3, ...),        // stride[0] = 1
    Axis.Channel(2, ...),  // stride[1] = 3
    Axis.Time(4, ...)      // stride[2] = 6
);
```

**Stride Calculation**:
- `stride[0] = 1` (Z varies fastest)
- `stride[1] = 3` (skip 3 frames to change Channel)
- `stride[2] = 6` (skip 6 frames to change Time)

### Frame Index ↔ Axis Indices Conversion

#### **Axis Indices → Frame Index**

```csharp
// Given: Z=2, Channel=1, Time=3
frameIndex = Z * stride[0] + C * stride[1] + T * stride[2]
           = 2 * 1 + 1 * 3 + 3 * 6
           = 2 + 3 + 18
           = 23
```

**Implementation** (`GetFrameIndexAt`):
```csharp
int frameIndex = 0;
for (int i = 0; i < axisCount; i++)
{
    frameIndex += axisIndices[i] * strides[i];
}
```

#### **Frame Index → Axis Indices**

```csharp
// Given: frameIndex = 23
for (int i = 0; i < axisCount; i++)
{
    axisIndices[i] = (frameIndex / strides[i]) % axisCounts[i];
}
```

**Result**:
- `Z = (23 / 1) % 3 = 2`
- `C = (23 / 3) % 2 = 1`
- `T = (23 / 6) % 4 = 3`

---



## FovAxis: Field-of-View Tiling

### What is FovAxis?

`FovAxis` is a specialized axis for representing **spatially-arranged imaging tiles** (multiple fields of view). It extends the basic `Axis` with:

1. **Tile Layout**: Spatial arrangement (e.g., 4×3 grid)
2. **Global Origins**: Real-world coordinates for each tile

### Use Case: Multi-Tile Microscopy

In tile-scanning microscopy, a large sample is imaged by capturing multiple overlapping or adjacent fields of view:

```
       Y-axis↑
             |
+-------+-------+-------+-------+
| FOV 8 | FOV 9 |FOV 10 |FOV 11 |  ← Row 2 (top)
+-------+-------+-------+-------+
| FOV 4 | FOV 5 | FOV 6 | FOV 7 |  ← Row 1 (middle)
+-------+-------+-------+-------+
| FOV 0 | FOV 1 | FOV 2 | FOV 3 |  ← Row 0 (bottom, origin)
+-------+-------+-------+-------+
             |
             └─→ X-axis
```

**Note**: Y-axis points upward (scientific convention). FOV 0 is at the bottom-left origin.

### FovAxis Features

#### 1. **Tile Layout**

```csharp
public struct TileLayout
{
    public int X { get; init; }  // Number of tiles along X
    public int Y { get; init; }  // Number of tiles along Y
    public int Z { get; init; }  // Number of tiles along Z (for 3D tiling)
}
```

#### 2. **Global Origins**

Each FOV has a global coordinate (world position):

```csharp
public readonly struct GlobalPoint
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}
```

### Basic Usage

```csharp
// Create 4×3 tiled FOV grid
var fovAxis = new FovAxis(tilesX: 4, tilesY: 3);

// Or specify custom global origins
var origins = new List<GlobalPoint>
{
    new(0, 0, 0),     // FOV 0
    new(95, 0, 0),    // FOV 1 (95µm offset)
    new(190, 0, 0),   // FOV 2
    // ... 12 total
};
var fovAxis = new FovAxis(origins, tilesX: 4, tilesY: 3);
```

### Integration with MatrixData

```csharp
// 6D data: X, Y, Z, Time, Channel, FOV
var data = new MatrixData<ushort>(
    Scale2D.Pixels(512, 512),
    [
        Axis.Z(10, 0, 50, "µm"),
        Axis.Time(20, 0, 30, "s"),
        Axis.Channel(3),
        new FovAxis(4, 3)  // 4×3 tiling
    ]
);
// Total frames: 10 × 20 × 3 × 12 = 7,200
```

### Accessing FOV Data

```csharp
// Get specific FOV
var fov = data.Dimensions["FOV"] as FovAxis;
fov.Index = 5;  // Select FOV at grid position [1, 1]

// Get world coordinate
var globalPos = fov[5];
Console.WriteLine($"FOV center: ({globalPos.X}, {globalPos.Y})");

// Extract all data from a single FOV
var fovData = data.SliceAt("FOV", 5);
```

### Advanced Features

#### Overlapping Tiles

```csharp
// Define overlapping regions
var origins = new List<GlobalPoint>
{
    new(0, 0, 0),
    new(90, 0, 0),    // 10µm overlap (tile width = 100µm)
    new(180, 0, 0),
    // ...
};
```

#### 3D Tiling (Not Currently Supported)

```csharp
// ⚠️ WARNING: 3D tiling is not yet implemented
// The following code will throw NotSupportedException:

var fovAxis = new FovAxis(tilesX: 2, tilesY: 2, tilesZ: 3);
// ❌ Throws: "3D tiling (zNum > 1) is not currently supported.
//             Index and ZIndex synchronization is not implemented."
```

**Reason**: Proper synchronization between `Axis.Index` and `FovAxis.ZIndex` for 3D tile navigation is not yet implemented. This feature is reserved for future development.

**Current Status**: Only 2D tiling (Z = 1) is fully supported.

---

## Practical Examples

### Example 1: Z-Stack Time-Lapse

```csharp
// Confocal microscopy: Z-scanning with time-lapse
var zStack = new MatrixData<ushort>(512, 512, 200);
zStack.DefineDimensions(
    Axis.Z(20, 0, 100, "µm"),     // 20 Z-slices (fast)
    Axis.Time(10, 0, 60, "s")     // 10 time points (slow)
);

// Access Z-stack at Time=5
for (int z = 0; z < 20; z++)
{
    int frameIndex = z + 5 * 20;  // z * stride[0] + t * stride[1]
    var slice = zStack.GetArray(frameIndex);
    // Process slice...
}
```

**Memory Layout**:
```
Frames 0-19:   Z0T0, Z1T0, ..., Z19T0  (Time=0)
Frames 20-39:  Z0T1, Z1T1, ..., Z19T1  (Time=1)
...
Frames 180-199: Z0T9, Z1T9, ..., Z19T9  (Time=9)
```

### Example 2: Multi-Channel Imaging

```csharp
// 4-channel fluorescence microscopy
var multiChannel = new MatrixData<ushort>(1024, 1024, 400);
multiChannel.DefineDimensions(
    Axis.Channel(4),              // DAPI, GFP, RFP, Cy5
    Axis.Z(10, 0, 50, "µm"),
    Axis.Time(10, 0, 120, "s")
);

// Get all channels at Z=5, Time=3
for (int c = 0; c < 4; c++)
{
    int frameIndex = c + 5 * 4 + 3 * 40;  // c*1 + z*4 + t*40
    var channel = multiChannel.GetArray(frameIndex);
}
```

### Example 3: FOV Tiling with Z-Stacks

```csharp
// Large tissue imaging with tiling
var tiledImage = new MatrixData<ushort>(512, 512, 240);
tiledImage.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),      // stride = 1
    Axis.Channel(2),               // stride = 10
    new FovAxis(3, 4)              // 3×4 grid, stride = 20
);

// Process FOV at grid position [1, 2] (FOV index = 7)
var fov = tiledImage.Dimensions["FOV"] as FovAxis;
fov.TileLayout;  // {X=3, Y=4, Z=1}

// Extract Z-stack for this FOV across all channels
for (int c = 0; c < 2; c++)
{
    for (int z = 0; z < 10; z++)
    {
        int frameIndex = z + c * 10 + 7 * 20;
        var slice = tiledImage.GetArray(frameIndex);
    }
}
```

---

## Axis Reconfiguration and Reordering

### Redefining with DefineDimensions

You can **dynamically redefine** the axis structure by calling `DefineDimensions` multiple times.

```csharp
// Initial definition: Z, Time
var data = new MatrixData<double>(512, 512, 100);
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Time(10, 0, 30, "s")
);

// Later redefinition: Channel, Z, Time (add/rearrange axes)
// Note: FrameCount (100) remains unchanged, so new axis structure must also multiply to 100
data.DefineDimensions(
    Axis.Channel(2),
    Axis.Z(5, 0, 25, "µm"),
    Axis.Time(10, 0, 30, "s")
);  // 2 × 5 × 10 = 100 ✅

// Mismatched FrameCount throws exception
// data.DefineDimensions(Axis.Z(20), Axis.Time(10));  // 20 × 10 = 200 ❌
```

**Use Case**: Changing axis interpretation after data acquisition
```csharp
// Acquired as simple frame sequence
var rawData = AcquireData();  // 240 frames

// Define actual axis structure later
rawData.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(4),
    Axis.Time(6, 0, 30, "s")
);  // 10 × 4 × 6 = 240
```

### Reordering Frames with Reorder

The `Reorder` extension method allows you to change the **physical order** of frames.

#### Basic Usage

```csharp
// Original order: 0, 1, 2, 3, 4
var original = new MatrixData<double>(100, 100, 5);

// Reorder to custom sequence
var reordered = original.Reorder(new[] { 4, 2, 0, 3, 1 });
// New order: 4, 2, 0, 3, 1
```

#### Shallow Copy vs Deep Copy

```csharp
// Default: Shallow Copy (share references)
var shallowReorder = data.Reorder(newOrder, deepCopy: false);
// - Rearranges references to original arrays
// - Memory efficient (no copying)
// - Affected by changes to original data

// Deep Copy: Create new MatrixData
var deepReorder = data.Reorder(newOrder, deepCopy: true);
// - Full array copy
// - Independent new data
// - Unaffected by original data changes
```

#### Practical Example: Time Reversal

```csharp
// Reverse time series
var timeSeries = new MatrixData<ushort>(512, 512, 100);
timeSeries.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Time(10, 0, 60, "s")
);

// Reverse time axis (reverse time for each Z slice)
var reversedIndices = new List<int>();
for (int t = 9; t >= 0; t--)  // Time reversed
{
    for (int z = 0; z < 10; z++)  // Z order maintained
    {
        reversedIndices.Add(z + t * 10);
    }
}
var reversed = timeSeries.Reorder(reversedIndices, deepCopy: true);
```

#### Practical Example: Sorting by Metadata

```csharp
// Sort frames by acquisition time
var unsorted = LoadFromMicroscope();  // Acquisition order is scrambled

// Sort by actual acquisition time from metadata
var sortedIndices = Enumerable.Range(0, unsorted.FrameCount)
    .OrderBy(i => double.Parse(unsorted.Metadata[$"AcquisitionTime_{i}"]))
    .ToList();

var sorted = unsorted.Reorder(sortedIndices, deepCopy: false);  // Lightweight reorder
```

### Reordering Axes by Name

The `Reorder(string[], bool)` method allows you to change the memory layout by specifying a new axis order.

#### Basic Usage

```csharp
// Original: Z=10, Channel=3, Time=5 (Z varies fastest)
var data = new MatrixData<double>(512, 512, 150);
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(3),
    Axis.Time(5, 0, 10, "s")
);

// Reorder to make Channel vary fastest
var reordered = data.Reorder(new[] { "Channel", "Z", "Time" });

// Result: Channel=3, Z=10, Time=5 (Channel now varies fastest)
// Frame 0: (C=0, Z=0, T=0), Frame 1: (C=1, Z=0, T=0), Frame 2: (C=2, Z=0, T=0), ...
```

#### Use Cases

**1. Prepare Data for External Tools**

```csharp
// Original MxPlot format: Z, Channel, Time
var mxData = LoadFromMicroscope();

// ImageJ expects: Channel, Z, Time
var forImageJ = mxData.Reorder(new[] { "Channel", "Z", "Time" });
ExportToImageJ(forImageJ);

// NumPy (C-order) expects: Time, Channel, Z (last varies fastest)
var forNumPy = mxData.Reorder(new[] { "Time", "Channel", "Z" });
ExportToNumPy(forNumPy);
```

**2. Optimize for Processing**

```csharp
// Original: Time, Z, Channel
var timeSeries = LoadTimelapseData();

// Reorder to make Z fastest for volume processing
var optimized = timeSeries.Reorder(new[] { "Z", "Time", "Channel" });

// Now Z-slices are contiguous in memory for efficient MIP
var mip = optimized.AsVolume("Z").CreateProjection(ViewFrom.Z);
```

**3. UI/Viewer Requirements**

```csharp
// Viewer expects Channel to change fastest (for RGB interleaving)
var forDisplay = rawData.Reorder(new[] { "Channel", "X", "Y" });
```

### Redefinition vs Reordering: When to Use

| Operation | DefineDimensions | Reorder (indices) | Reorder (axis names) |
|-----------|------------------|-------------------|----------------------|
| **Purpose** | Change axis interpretation | Change frame order | Change memory layout |
| **FrameCount** | Unchangeable | Unchangeable | Unchangeable |
| **Physical Order** | Unchanged | Changed | Changed |
| **Axes Structure** | Changed | Removed | Changed (same axes) |
| **Use Case** | Redefine axis meaning | Sort, reverse, extract | Interoperability |
| **Cost** | Low (metadata only) | Medium-High | Medium-High |

**Combined Example**:
```csharp
// 1. Reorder frames
var reordered = rawData.Reorder(correctOrder, deepCopy: true);

// 2. Define new axis structure
reordered.DefineDimensions(
    Axis.Z(10, ...),
    Axis.Channel(3, ...),
    Axis.Time(8, ...)
);
```

**Axis Reordering Example**:
```csharp
// Change memory layout for compatibility
var reordered = rawData.Reorder(new[] { "Channel", "Z", "Time" });
// Axis structure is preserved but memory order changed
```

---

## Best Practices

### 1. Document Your Axis Order

```csharp
// Add metadata explaining axis order
data.Metadata["AxisOrder"] = "Z, Channel, Time";
data.Metadata["AxisDescription"] = "Record actual acquisition order";
```

**Important**: Axis order depends on your data acquisition method. The first axis (axes[0]) becomes the fastest-varying (stride=1), but the optimal order is application-specific:
- **Camera RGB**: `Channel, X, Y` (RGB varies fastest)
- **Z-stack scanning**: `Z, Channel, Time` (Z-depth varies fastest)
- **Time-lapse priority**: `Time, Z, Channel` (time varies fastest)

### 2. Use FovAxis for Tiling

```csharp
// ✅ Use FovAxis instead of generic Axis for spatial tiles
var fovAxis = new FovAxis(4, 3);  // Not: new Axis(12, ...)
```

### 3. Align with Target Platform

**For MATLAB users**:
```csharp
// Matches MATLAB convention (column-major)
data.DefineDimensions(Axis.Z(10), Axis.Channel(3), Axis.Time(5));
```

**For NumPy users**:
```csharp
// ⚠️ Consider axis order carefully
// NumPy: shape = (T, C, Z) → last varies fastest
// MxPlot equivalent (first varies fastest):
data.DefineDimensions(Axis.Z(10), Axis.Channel(3), Axis.Time(5));
```

---

## Future Extensions

### 1. Irregular Tiling

**Current Limitation**: FovAxis assumes regular grid spacing.

**Proposed Extension**: Support arbitrary tile positions
```csharp
// Future API
var irregularFov = new FovAxis(
    origins: customPositions,
    topology: TileTopology.Irregular
);
```

### 2. 3D Volume Tiling

**Current**: 2D tiling (X, Y grid) only

**Limitation**: 3D tiling requires proper synchronization between `Index` (global frame position) and `ZIndex` (current Z plane). This is not yet implemented.

**Proposed**: Full 3D tiling for cleared tissue imaging
```csharp
// Future: True 3D tiling with Index/ZIndex synchronization
var volumeTiles = new FovAxis(
    tilesX: 3, tilesY: 3, tilesZ: 5,
    overlap: new Vector3(10, 10, 5)  // µm overlap
);

// Navigation would work like:
volumeTiles.Index = 42;  // Automatically updates ZIndex
Console.WriteLine($"Z plane: {volumeTiles.ZIndex}");  // Auto-calculated
```

**Implementation Plan**:
- Make `ZIndex` a computed property: `ZIndex => Index / (X * Y)`
- Add `XIndex` and `YIndex` computed properties
- Ensure backward compatibility with existing 2D code

### 3. Stitching Metadata

**Proposed**: Embed stitching information
```csharp
public class FovAxis : Axis
{
    public StitchingParameters Stitching { get; set; }
}

public record StitchingParameters
{
    public double OverlapX { get; init; }
    public double OverlapY { get; init; }
    public BlendMode BlendMode { get; init; }
}
```

### 4. Sparse Axis Support

**Current**: All combinations of axis indices exist

**Proposed**: Support for missing frames (sparse data)
```csharp
// Future: Not all (Z, T) combinations acquired
var sparseData = new MatrixData<ushort>(512, 512);
sparseData.DefineSparseAxes(
    Axis.Z(10),
    Axis.Time(100),
    acquiredFrames: new[] { (0,0), (1,0), (0,5), (5,10) }
);
```

### 5. Adaptive Axis Resolution

**Proposed**: Variable spacing along axis
```csharp
// Future: Non-uniform Z-spacing
var adaptiveZ = new AdaptiveAxis(
    positions: new[] { 0, 1, 2, 5, 10, 20 },  // µm
    name: "Z"
);
```

---

## Troubleshooting

### Issue 1: Axis Order Confusion

**Symptom**: Data appears scrambled or non-contiguous

**Solution**: Verify axis order matches your expectations
```csharp
// Check stride values
var dims = data.Dimensions;
for (int i = 0; i < dims.AxisCount; i++)
{
    Console.WriteLine($"{dims[i].Name}: stride = ?");  // Internal strides
}
```

### Issue 2: NumPy Import Mismatch

**Symptom**: Frames in wrong order after NumPy export

**Solution**: Reverse axis order when converting
```python
# NumPy (T, C, Z) → MxPlot (Z, C, T)
mxplot_axes = numpy_array.transpose(2, 1, 0)
```

### Issue 3: FOV Global Coordinates

**Symptom**: Stitching fails due to incorrect origins

**Solution**: Verify global origins match stage coordinates
```csharp
var fov = data.Dimensions["FOV"] as FovAxis;
for (int i = 0; i < fov.Count; i++)
{
    var pos = fov[i];
    Console.WriteLine($"FOV {i}: ({pos.X}, {pos.Y}, {pos.Z})");
}
```

---

## Summary

### Key Takeaways

1. **First axis = Fastest varying** (stride = 1)
2. **MATLAB-compatible** (column-major convention)
3. **NumPy-opposite** (requires transpose for C-order arrays)
4. **FovAxis** provides spatial tile organization
5. **Stride-based indexing** enables efficient conversion

### Reference Formula

```
frameIndex = Σ(axisIndex[i] × stride[i])

where stride[i] = ∏(axisCount[j] for j < i)
```

---

**Related Documents**:
- [MatrixData Operations Guide](MatrixData_Operations_Guide.md)
- [VolumeAccessor Guide](VolumeAccessor_Guide.md)
- [Performance Reports](VolumeOperator_Performance_Report.md)

---

*Last Updated: 2026-02-08*
