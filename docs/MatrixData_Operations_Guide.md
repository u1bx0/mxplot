# MatrixData<T> Operations Guide

**MxPlot.Core Comprehensive Reference**

> Last Updated: 2026-02-8  
> Version: 0.0.2-alpha

*Note: This document is largely based on AI-generated content and may require further review for accuracy.*

## 📚 Table of Contents

1. [Introduction](#introduction)
2. [Core Concepts](#core-concepts)
3. [Data Creation & Initialization](#data-creation--initialization)
4. [Dimensional Operations](#dimensional-operations)
5. [Volume Operations](#volume-operations)
6. [Arithmetic Operations](#arithmetic-operations)
7. [Pipeline Examples](#pipeline-examples)
8. [Extreme Examples](#extreme-examples)
9. [Method Reference](#method-reference)

---

## Introduction

`MatrixData<T>` is the central data container in MxPlot.Core, designed for handling multi-dimensional scientific data with physical coordinates, units, and flexible axis management.

### Key Features

- ✅ **Multi-Axis Support**: Time × Z × Channel × Wavelength × FOV × ...
- ✅ **Physical Coordinates**: Real-world scaling with units (µm, nm, seconds, etc.)
- ✅ **Type Safety**: Generic support for all numeric types including `Complex`
- ✅ **High Performance**: SIMD optimization, Span<T>, parallel processing
- ✅ **Volume Rendering**: 3D volume access with MIP/MinIP/AIP projections
- ✅ **Metadata Management**: Rich metadata dictionary

---

## Core Concepts

### 1. XY Plane + Frame Axis

```
MatrixData<T> = [X × Y] + [Frame Axis]
                 ↑          ↑
            Spatial    Multi-dimensional
            (2D Image)  (Time, Z, Channel, etc.)
```

### 2. Dimension Structure

```csharp
// Example: 4D Data (X, Y, Z, Time)
var data = new MatrixData<double>(512, 512, 30);  // 512×512, 30 frames
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),      // Z: 10 slices
    Axis.Time(3, 0, 5, "s")       // Time: 3 time points
);
// Total: 10 × 3 = 30 frames
```

### 3. Coordinate System

- **Pixel-Centered**: Physical scaling measured at pixel centers
- **Left-Bottom Origin**: Y increases upwards (scientific convention)
- **Immutable Size**: Matrix dimensions fixed after creation

---

## Data Creation & Initialization

### Basic Creation

```csharp
// 1. Simple 2D matrix
var matrix2D = new MatrixData<double>(100, 100);
matrix2D.SetXYScale(0, 10, 0, 10);  // 0-10mm range
matrix2D.XUnit = "mm";

// 2. 3D data with explicit frame count
var matrix3D = new MatrixData<ushort>(512, 512, 50);

// 3. With Scale2D
var scale = new Scale2D(1024, -50, 50, 1024, -50, 50);
var matrixScaled = new MatrixData<double>(scale, 100);

// 4. With pre-allocated arrays
var arrays = new List<double[]> { new double[100*100], new double[100*100] };
var matrixFromArrays = new MatrixData<double>(100, 100, arrays);
```

### Multi-Axis Initialization

```csharp
// Method 1: Collection expression (C# 12)
var xyczt = new MatrixData<int>(
    Scale2D.Pixels(5, 5),
    [ 
        Axis.Time(4, 0, 2, "s"),     // T=4
        Axis.Z(3, 0, 4, "µm"),       // Z=3
        Axis.Channel(2)              // C=2
    ]  // Total: 4×3×2 = 24 frames
);
xyczt.SetXYScale(-1, 1, -1, 1);

// Method 2: Traditional params array
var xyczt2 = new MatrixData<int>(5, 5, 24);
xyczt2.DefineDimensions(
    Axis.Time(4, 0, 2, "s"),
    Axis.Z(3, 0, 4, "µm"),
    Axis.Channel(2)
);

// Initialize data
for (int i = 0; i < xyczt.FrameCount; i++)
    xyczt.Set(i, (ix, iy, x, y) => i + ix * iy);
```

### Data Population

```csharp
// 1. Lambda function (with indices and coordinates)
matrix.Set((ix, iy, x, y) => Math.Sin(x) * Math.Cos(y));

// 2. Frame-by-frame with lambda
for (int frame = 0; frame < matrix.FrameCount; frame++)
    matrix.Set(frame, (ix, iy, x, y) => frame * x * y);

// 3. Direct array access
var array = matrix.GetArray(0);
for (int i = 0; i < array.Length; i++)
    array[i] = i * 0.5;

// 4. SetArray (with pre-computed data)
var newArray = new double[matrix.XCount * matrix.YCount];
// ... fill newArray ...
matrix.SetArray(newArray, frameIndex: 5);
```

---

## Dimensional Operations

### Transpose

**Swaps X and Y axes for all frames**

```csharp
var original = new MatrixData<double>(100, 50);  // 100×50
var transposed = original.Transpose();           // 50×100

// Use case: Convert row-major to column-major data
```

### Crop Operations

```csharp
// 1. Pixel-based crop
var cropped = matrix.Crop(startX: 25, startY: 25, width: 50, height: 50);

// 2. Physical coordinate crop
var physCrop = matrix.CropByCoordinates(xMin: -5, xMax: 5, yMin: -5, yMax: 5);

// 3. Center crop
var centered = matrix.CropCenter(width: 256, height: 256);
```

### SliceAt (2D), ExtractAlong (3D), and SnapTo (N-1D)

```csharp
// 5D data: X, Y, C=2, Z=10, Time=5 (100 frames)
var hyperStack = new MatrixData<double>(512, 512, 100);
hyperStack.DefineDimensions(
   Axis.Channel(2), 
   Axis.Z(10, 0, 50, "µm"), 
   Axis.Time(5, 0, 10, "s")
);

// SliceAt: Extract 2D slice at specific axis indices
var xy = hyperStack.SliceAt(("Channel", 1),("Z",0), ("Time", 2));
// Result: 512×512 (Channel=1, Z=0, Time=2)

// ExtractAlong: Extract 3D volume along specific axis with fixed coordinates
var xyz = hyperStack.ExtractAlong("Z", baseIndices: new[] {0, 0, 3 });
// Result: 512×512, 10 Z frames (Channel=0, Time=3)

// SnapTo: Extract N-1D data by fixing one axis
var xyzt = timeLapse.SnapTo("Channel", indexInAxis: 1);
// Result: 512×512, 10 Z, 5 T (Channel=1)

```

### Map & Reduce

```csharp
// Map: Apply function to each pixel across all frames
var normalized = matrix.Map<double, double>(
    (value, x, y, frameIndex) => value / 255.0
);

// Map with type conversion
var converted = matrixInt.Map<int, double>(
    (value, x, y, frame) => value * 0.01
);

// Reduce: Aggregate across frame axis
var averaged = timeSeries.Reduce((x, y, values) =>
{
    return values.Average();  // Simple average across all frames
});

// Custom reduction (e.g., maximum)
var maxProjection = stack.Reduce((x, y, values) => values.Max());
```

### Reorder

```csharp
// Reorder frames by custom order
var reordered = matrix.Reorder(new[] { 2, 0, 4, 1, 3 });

// Use case: Sort frames by acquisition time from metadata
var sortedIndices = Enumerable.Range(0, matrix.FrameCount)
    .OrderBy(i => matrix.Metadata[$"Time_{i}"])
    .ToList();
var sorted = matrix.Reorder(sortedIndices);
```

---

## Volume Operations

### AsVolume - Create VolumeAccessor

```csharp
// 1. Simple 3D data (single axis)
var volume3D = new MatrixData<ushort>(256, 256, 64);
volume3D.DefineDimensions(Axis.Z(64, 0, 32, "µm"));
var volume = volume3D.AsVolume();  // No axis name needed for single axis

// 2. Multi-axis data: specify axis name
var xyczt = new MatrixData<double>(128, 128, 60);  // Z=20, Time=3
xyczt.DefineDimensions(Axis.Z(20, 0, 100, "µm"), Axis.Time(3, 0, 5, "s"));

// Extract volume along Z at Time=1
xyczt.Dimensions["Time"].Index = 1;
var volumeAtT1 = xyczt.AsVolume("Z");  // Uses ActiveIndex

// Or specify exact coordinates
var volumeAtT2 = xyczt.AsVolume("Z", baseIndices: new[] { 0, 2 });  // Z=0, Time=2
```

### Volume Projections

```csharp
var volume = matrix3D.AsVolume();

// Maximum Intensity Projection (MIP)
var mipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);  // Top view
var mipXZ = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Maximum);  // Side view
var mipYZ = volume.CreateProjection(ViewFrom.X, ProjectionMode.Maximum);  // Front view

// Minimum Intensity Projection (MinIP) - useful for dark features
var minipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Minimum);

// Average Intensity Projection (AIP) - reduces noise
var aipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Average);
```

### Volume Manipulation

```csharp
// Restack: Reorganize volume for different viewing axes
var restackedX = volume.Restack(ViewFrom.X);  // YZ slices
var restackedY = volume.Restack(ViewFrom.Y);  // XZ slices

// SliceAt: Extract 2D slice at specific depth
var sliceAtZ20 = volume.SliceAt(ViewFrom.Z, 20);  // XY plane at Z=20

// ReduceZ: Custom reduction along Z-axis
var medianProj = volume.ReduceZ((x, y, values) =>
{
    var sorted = values.OrderBy(v => v).ToArray();
    return sorted[sorted.Length / 2];  // Median
});

// Direct voxel access (no bounds checking for performance)
double voxelValue = volume[x: 10, y: 20, z: 5];
```

---

## Arithmetic Operations

### Matrix-to-Matrix Operations

```csharp
// Element-wise operations
var sum = matrixA.Add(matrixB);
var diff = signal.Subtract(background);  // Background subtraction
var product = matrixA.Multiply(matrixB);
var quotient = matrixA.Divide(matrixB);  // Flat-field correction

// Broadcasting: Single frame can be applied to multi-frame data
var multiFrame = new MatrixData<double>(512, 512, 100);
var singleBackground = new MatrixData<double>(512, 512, 1);
var corrected = multiFrame.Subtract(singleBackground);  // Background subtracted from all frames
```

### Scalar Operations

```csharp
// Arithmetic with scalar values
var scaled = matrix.Multiply(1.5);        // Gain correction
var offset = matrix.Add(-100);            // Offset correction
var shifted = matrix.Subtract(50);

// Use case: Calibration
var calibrated = rawData
    .Subtract(darkCurrent)  // Remove dark current
    .Divide(flatField)      // Flat-field correction
    .Multiply(gainFactor);  // Apply gain
```

### Important Notes

- **Coordinate Inheritance**: Result inherits scale/metadata from **first argument**
- **Dimension Validation**: Axes must match in count and structure
- **Complex Support**: Matrix-to-matrix OK, scalar operations limited (see docs)

---

## Pipeline Examples

### Example 1: Time-Series Analysis

```csharp
// Load 4D data: X, Y, Z, Time
var timeLapse = MatrixDataSerializer.LoadTyped<ushort>("timelapse.mxd");

// Pipeline: Crop ROI → Extract Z-stack → MIP → Average over time
var result = timeLapse
    .CropCenter(width: 256, height: 256)           // Focus on center
    .ExtractAlong("Z", new[] { 0, 5 })             // Get Z-stack at Time=5
    .AsVolume()                                     // Convert to volume
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);  // MIP

// Further processing
var calibrated = result
    .Subtract(background)
    .Multiply(calibrationFactor);
```

### Example 2: Multi-Channel Processing

```csharp
// 5D data: X, Y, Z=10, Channel=3, Time=20 (600 frames)
var multiChannel = new MatrixData<double>(512, 512, 600);
multiChannel.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(3),
    Axis.Time(20, 0, 30, "s")
);

// Extract green channel (Channel=1) across all Z and Time
var greenChannel = multiChannel.SliceAt("Channel", 1);  // 512×512, Z=10, Time=20

// Create time-lapse MIP for green channel
var mipSequence = new List<MatrixData<double>>();
for (int t = 0; t < 20; t++)
{
    var zStackAtT = greenChannel.ExtractAlong("Z", new[] { 0, 0, t });
    var mip = zStackAtT.AsVolume().CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);
    mipSequence.Add(mip);
}
```

### Example 3: Hyperspectral Analysis

```csharp
// Hyperspectral imaging: X, Y, Wavelength=31, Time=100
var hyperData = new MatrixData<double>(1024, 1024, 3100);
hyperData.DefineDimensions(
    new Axis(31, 400, 700, "Wavelength", "nm"),  // 400-700nm
    Axis.Time(100, 0, 10, "s")
);

// Extract spectral signature at specific location
int xPos = 512, yPos = 512;
var signature = new double[31];
for (int w = 0; w < 31; w++)
{
    var frameIdx = hyperData.Dimensions.GetFrameIndexFrom(new[] { w, 50 });  // Wavelength=w, Time=50
    signature[w] = hyperData.GetValueAt(xPos, yPos, frameIdx);
}

// Average intensity across all wavelengths
var avgAcrossWavelength = hyperData.Reduce((x, y, values) =>
{
    // values has length 3100 (31 wavelengths × 100 time points)
    // Group by wavelength and average
    return Enumerable.Range(0, 31)
        .Select(w => Enumerable.Range(0, 100).Select(t => values[t * 31 + w]).Average())
        .Average();
});
```

---

## Extreme Examples

### 🎪 Practical Multi-Dimensional Pipeline

```csharp
// 6D data: X, Y, Z, Time, Channel, FOV (common in microscopy)
var multiModal = new MatrixData<ushort>(
    Scale2D.Pixels(512, 512),
    [
        Axis.Z(20, 0, 100, "µm"),     // Z-scanning
        Axis.Time(50, 0, 60, "s"),    // Time-lapse
        Axis.Channel(4),                // DAPI, GFP, RFP, Cy5
        new FovAxis(3, 1, 1)          // Tiling (X:3, Y:1, Z:1)
    ]  // Total: 20×50×4×3 = 12,000 frames (practical!)
);

// Practical analysis pipeline
var result = multiModal
    .SnapTo("Channel", 1)              // Extract GFP channel
    .SnapTo("FOV", 1)                  // Select center FOV
    .CropCenter(256, 256)               // Focus on ROI
    .ExtractAlong("Z", new[] { 0, 25 }) // Z-stack at Time=25
    .AsVolume()
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum)
    .Subtract(darkCurrent)
    .Divide(flatField)
    .Multiply(1.5);

MatrixDataSerializer.Save("processed.mxd", result, compress: true);
```

### 🚀 A more extreme example: 9-dimensional weather data (is this even possible?)

```csharp
var bigData = new MatrixData<float>(
    Scale2D.Pixels(32, 32),
    [
        new Axis(12, 0, 11, "Month"),           // Month
        new Axis(24, 0, 23, "Hour"),            // Hour
        new Axis(10, 0, 10000, "Altitude", "m"),  // Altitude
        new Axis(7, 0, 6, "DayOfWeek"),         // Day of week
        new Axis(4, 0, 3, "Humidity"),            // Humidity level
        new Axis(3, 0, 2, "Pressure"),          // Pressure level
        new Axis(5, 0, 4, "Sensor")             // Sensor type
    ]  // Total: 12×24×10×7×4×3×5 = 1,209,600 frames! => (Be aware of OutOfMemory on your PCs)
);

// Manipulation and processing as SQL-like queries
var result = bigData
    .SnapTo("DayOfWeek", 1)        // Mondays only
    .SnapTot("Humidity", 0)     // Dry conditiions only
    .SnapTo("Pressure", 1)      // Mid-pressure only
    .ExtractAlong("Altitude", new[] { 1, 6, 0, 1 });  // Altitude stack of sensor 1 on 6 AM in January

// Try to evaluate how much time it takes!
```

### 🚀 Performance Monster

```csharp
// Process 1TB of data (hypothetically)
var hugeData = new MatrixData<ushort>(4096, 4096, 10000);  // ~335GB if ushort

// Parallel processing with Map
var processed = hugeData.Map<ushort, double>(
    (value, x, y, frame) =>
    {
        // Complex per-pixel processing
        double normalized = value / 65535.0;
        double filtered = ApplyGaussianKernel(normalized, x, y);
        return filtered * CalibrationFactor;
    },
    useParallel: true  // Frame-level parallelization
);

// This actually works! (if you have enough RAM)
```

### 🎨 Creative (Ab)use Cases

#### 1. Fractal Generation Across Dimensions

```csharp
var fractal4D = new MatrixData<double>(512, 512, 100);
fractal4D.DefineDimensions(Axis.Z(10, 0, 1, ""), Axis.Time(10, 0, 1, ""));

fractal4D.ForEach((frame, array) =>
{
    var coords = fractal4D.Dimensions.GetCoordinatesFrom(frame);
    double z = coords[0] * 0.1;
    double t = coords[1] * 0.1;
    
    for (int iy = 0; iy < 512; iy++)
    {
        for (int ix = 0; ix < 512; ix++)
        {
            double x = (ix - 256) / 256.0;
            double y = (iy - 256) / 256.0;
            array[iy * 512 + ix] = MandelbrotValue(x, y, z, t);
        }
    }
}, useParallel: true);

// Create 4D MIP visualization
var mipAcrossZT = fractal4D.Reduce((x, y, values) => values.Max());
```

#### 2. Time-Reversed Video Processing

```csharp
var video = LoadVideo("input.mxd");  // MatrixData<byte> with Time axis

// Reverse time
var indices = Enumerable.Range(0, video.FrameCount).Reverse().ToList();
var reversed = video.Reorder(indices);

// Apply temporal smoothing
var smoothed = reversed.Map<byte, byte>((value, x, y, frame) =>
{
    int prevFrame = Math.Max(0, frame - 1);
    int nextFrame = Math.Min(reversed.FrameCount - 1, frame + 1);
    
    byte prev = reversed.GetValueAtTyped(x / scale, y / scale, prevFrame);
    byte next = reversed.GetValueAtTyped(x / scale, y / scale, nextFrame);
    
    return (byte)((prev + value + next) / 3);
});
```

#### 3. Multi-Scale Pyramid

```csharp
var pyramid = new List<MatrixData<double>>();
var current = originalImage;

for (int level = 0; level < 5; level++)
{
    pyramid.Add(current);
    
    // Downsample by 2x
    int newWidth = current.XCount / 2;
    int newHeight = current.YCount / 2;
    var downsampled = new MatrixData<double>(newWidth, newHeight);
    
    downsampled.Set((ix, iy, x, y) =>
    {
        return (current.GetValueAt(ix*2, iy*2) + 
                current.GetValueAt(ix*2+1, iy*2) +
                current.GetValueAt(ix*2, iy*2+1) +
                current.GetValueAt(ix*2+1, iy*2+1)) / 4.0;
    });
    
    current = downsampled;
}
```

---

## Method Reference

### MatrixData<T> Core Methods

#### Construction
- `MatrixData(int xCount, int yCount)`
- `MatrixData(int xCount, int yCount, int frameCount)`
- `MatrixData(Scale2D scale, params Axis[] axes)`
- `MatrixData(Scale2D scale, IEnumerable<Axis> axes)`
- `MatrixData(int xCount, int yCount, List<T[]> arrays)`

#### Data Access
- `T GetValueAt(int ix, int iy, int frameIndex = -1)`
- `T GetValueAtTyped(int ix, int iy, int frameIndex = -1)`
- `double GetValueAt(int ix, int iy, int frameIndex = -1)`  // via double conversion
- `T[] GetArray(int frameIndex = -1)`
- `ReadOnlySpan<byte> GetRawBytes(int frameIndex = -1)`

#### Data Modification
- `void SetValueAt(int ix, int iy, double v)`
- `void SetValueAt(int ix, int iy, int frameIndex, double v)`
- `void SetValueAtTyped(int ix, int iy, int frameIndex, T value)`
- `void SetArray(T[] srcArray, int frameIndex = -1)`
- `void SetFromRawBytes(ReadOnlySpan<byte> bytes, int frameIndex = -1)`
- `void Set(Func<int, int, double, double, T> func)`
- `void Set(int frameIndex, Func<int, int, double, double, T> func)`

#### Statistics
- `(double Min, double Max) GetMinMaxValues()`
- `(double Min, double Max) GetMinMaxValues(int frameIndex)`
- `(double Min, double Max) GetGlobalMinMaxValues()`
- `double GetMinValue()`
- `double GetMaxValue()`
- `void RefreshValueRange()`, `RefreshValueRange(int frameIndex)`

#### Scaling & Units
- `void SetXYScale(double xmin, double xmax, double ymin, double ymax)`
- `Scale2D GetScale()`
- `double XAt(int ix)`, `double YAt(int iy)`
- `int XIndexOf(double x, bool extendRange = false)`
- `int YIndexOf(double y, bool extendRange = false)`

#### Dimensions
- `void DefineDimensions(params Axis[] axes)`
- `VolumeAccessor<T> AsVolume(string axisName = "", int[]? baseIndices = null)`

### DimensionalOperator Extensions

#### Transformation
- `MatrixData<T> Transpose<T>()`
- `MatrixData<T> Crop<T>(int startX, int startY, int width, int height)`
- `MatrixData<T> CropByCoordinates<T>(double xMin, double xMax, double yMin, double yMax)`
- `MatrixData<T> CropCenter<T>(int width, int height)`

#### Slicing & Extraction
- `MatrixData<T> SliceAt<T>(string axisName, int indexInAxis)`
- `MatrixData<T> ExtractAlong<T>(string axisName, int[] baseIndices, bool deepCopy = false)`

#### Mapping & Reduction
- `MatrixData<TDst> Map<TSrc, TDst>(Func<TSrc, double, double, int, TDst> func, bool useParallel = false)`
- `MatrixData<T> Reduce<T>(Func<int, int, T[], T> aggregator)`
- `void ForEach<T>(Action<int, T[]> action, bool useParallel = true)`

#### Reordering
- `MatrixData<T> Reorder<T>(IEnumerable<int> order, bool deepCopy = false)`

### VolumeAccessor<T> Methods

#### Indexing
- `T this[int ix, int iy, int iz]` - Direct voxel access

#### Projections
- `MatrixData<T> CreateProjection(ViewFrom axis, ProjectionMode mode)` where mode = Maximum | Minimum | Average

#### Restructuring
- `MatrixData<T> Restack(ViewFrom direction)`
- `MatrixData<T> SliceAt(ViewFrom direction, int index)`

#### Reduction
- `MatrixData<T> ReduceZ<T>(Func<int, int, T[], T> reduceFunc)`
- `MatrixData<T> ReduceY<T>(Func<int, int, T[], T> reduceFunc)`
- `MatrixData<T> ReduceX<T>(Func<int, int, T[], T> reduceFunc)`

### MatrixArithmetic Extensions

#### Matrix-to-Matrix
- `MatrixData<T> Add<T>(MatrixData<T> a, MatrixData<T> b)`
- `MatrixData<T> Subtract<T>(MatrixData<T> signal, MatrixData<T> background)`
- `MatrixData<T> Multiply<T>(MatrixData<T> a, MatrixData<T> b)`
- `MatrixData<T> Divide<T>(MatrixData<T> a, MatrixData<T> b)`

#### Scalar Operations
- `MatrixData<T> Multiply<T>(MatrixData<T> data, double scaleFactor)`
- `MatrixData<T> Add<T>(MatrixData<T> data, double scalar)`
- `MatrixData<T> Subtract<T>(MatrixData<T> data, double scalar)`

### I/O Operations

#### MatrixDataSerializer
- `void Save<T>(string filename, MatrixData<T> data, bool compress = false)`
- `MatrixData<T> Load<T>(string filename)`
- `IMatrixData LoadDynamic(string filename)`
- `FileInfo GetFileInfo(string filename)`

#### CSV Handler
- `void SaveCsv<T>(string filename, MatrixData<T> data)`
- `MatrixData<double> LoadCsv(string filename)`

---

## Best Practices

### 1. Memory Management

```csharp
// ✅ Good: Reuse MatrixData instances
var temp = new MatrixData<double>(512, 512, 100);
for (int iteration = 0; iteration < 10; iteration++)
{
    // Process in-place when possible
    ProcessData(temp);
}

// ❌ Bad: Creating new instances in loop
for (int iteration = 0; iteration < 10; iteration++)
{
    var temp = new MatrixData<double>(512, 512, 100);  // Allocates every time!
    ProcessData(temp);
}
```

### 2. Pipeline Design

```csharp
// ✅ Good: Chain operations, minimize intermediate allocations
var result = data
    .CropCenter(256, 256)
    .ExtractAlong("Z", indices)
    .AsVolume()
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);

// ❌ Bad: Store every intermediate result
var cropped = data.CropCenter(256, 256);
var extracted = cropped.ExtractAlong("Z", indices);
var volume = extracted.AsVolume();
var result = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);
```

### 3. Parallel Processing

```csharp
// Use parallel processing for large frame counts
var processed = largeData.Map<ushort, double>(
    (value, x, y, frame) => ExpensiveProcessing(value),
    useParallel: true  // ← Enable for >10 frames typically
);

// For small frame counts (<10), overhead outweighs benefit
```

### 4. Dimension Design

```csharp
// ✅ Good: Logical axis order (fast-varying first)
data.DefineDimensions(
    Axis.Z(10, ...),      // Varies fastest
    Axis.Channel(3, ...),
    Axis.Time(100, ...)   // Varies slowest
);
// Frame order: Z0C0T0, Z1C0T0, ..., Z9C0T0, Z0C1T0, ...

// Convention: Inner (fast) → Outer (slow)
```

---

## Performance Characteristics

| Operation | Complexity | Parallelizable | Memory |
|-----------|-----------|----------------|--------|
| `GetValueAt()` | O(1) | No | Minimal |
| `Transpose()` | O(N×M×F) | Yes | Full copy |
| `Crop()` | O(W×H×F) | Yes | Partial copy |
| `Map()` | O(N×M×F) | Yes | Full copy |
| `Reduce()` | O(N×M×F) | Yes | 1 frame |
| `AsVolume()` | O(1) | No | Zero-copy view |
| `CreateProjection()` | O(N×M×D) | Yes | 1 frame |
| `Add()/Subtract()` | O(N×M×F) | Yes | Full copy |

*N=XCount, M=YCount, F=FrameCount, D=Depth, W=CropWidth, H=CropHeight*

---

## Troubleshooting

### Common Issues

**Q: Why does `AsVolume()` throw exception for multi-axis data?**

A: For multi-axis data, you must specify which axis represents the Z-direction:
```csharp
// ❌ Wrong
var volume = multiAxisData.AsVolume();

// ✅ Correct
var volume = multiAxisData.AsVolume("Z");
```

**Q: Arithmetic operations fail with "Dimension mismatch"**

A: Frame counts and dimension structures must match:
```csharp
// Dimensions must be compatible
var a = new MatrixData<double>(100, 100, 10);
a.DefineDimensions(Axis.Z(10, 0, 50, "µm"));

var b = new MatrixData<double>(100, 100, 10);
b.DefineDimensions(Axis.Time(10, 0, 5, "s"));  // ❌ Different structure!

// var result = a.Add(b);  // Throws ArgumentException
```

**Q: Memory usage too high?**

A: Consider:
1. Use `deepCopy: false` when possible
2. Process in chunks for very large datasets
3. Use `ushort` or `float` instead of `double` if precision allows
4. Enable compression for storage: `MatrixDataSerializer.Save(..., compress: true)`

---

## See Also

- [VolumeAccessor Guide](VolumeAccessor_Guide.md)
- [Performance Reports](VolumeOperator_Performance_Report.md)
- [API Reference](../README.md)

---

**End of Guide**

*Last Updated: 2026-02-08  (Generated by GitHub Copilot --It may contain incorrect explanations.)*
