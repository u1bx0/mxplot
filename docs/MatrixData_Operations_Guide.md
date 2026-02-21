# MatrixData<T> Operations Guide

**MxPlot.Core Comprehensive Reference**

> Last Updated: 2026-02-21


*Note: This document is largely based on AI-generated content and requires further review for accuracy. Some descriptions may be outdated due to changes to the library.*

## 📚 Table of Contents

1. [Introduction](#introduction)
2. [Core Concepts](#core-concepts)
3. [Tutorial for a Single-Frame MatrixData\<T\>](#tutorial-for-a-single-frame-matrixdatat)
4. [Tutorial for a Multi-Dimensional Data](#tutorial-for-a-multi-dimensional-data)
5. [Dimensional Operations](#dimensional-operations)
   - *Transpose, Crop, Slice, Extract, Select, Map & Reduce, Reorder*
6. [Volume Operations](#volume-operations)
   - *VolumeAccessor, Projections (MIP/MinIP/AIP), Manipulation*
7. [Arithmetic Operations](#arithmetic-operations)
   - *Matrix-to-Matrix, Scalar Operations, Broadcasting*
8. [Pipeline Examples](#pipeline-examples)
9. [Extreme Examples](#extreme-examples)

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
## Tutorial for a Single-Frame MatrixData\<T\>

```csharp
//
// This is a comprehensive tutorial (Cheat Sheet) on how to create, access, 
// and manipulate MatrixData<T> with a single frame (2D image/matrix).
//

// --------------------------------------------------------------------------------
// 1. Creation and Coordinate System
// --------------------------------------------------------------------------------

// Create a single frame of MatrixData<T>, where T can be any unmanaged primitive 
// value type (int, float, double, etc.). System.Numerics.Complex is also supported.
// Custom structs can be used by providing a custom MinMaxFinder.
var md = new MatrixData<int>(128, 128); // Allocates an int array with 128x128 dimensions

// Define the physical data coordinate system. 
// (XMin, YMin) corresponds to md[0, 0], and (XMax, YMax) corresponds to md[127, 127].
md.SetXYScale(-1, 1, -1, 1); 

// Alternatively, you can initialize MatrixData with a Scale2D object that defines 
// the dimensions and coordinate system in one step:
md = new MatrixData<int>(new Scale2D(128, -1, 1, 128, -1, 1));

// Scale2D provides some handy factory methods:
var scale1 = Scale2D.Pixels(128, 128);     // X and Y range from 0 to 127
var scale2 = Scale2D.Centered(128, 128, 2, 2); // X and Y range from -1.0 to 1.0

// You can also wrap an existing array (Zero-allocation initialization).
// The length of T[] must exactly match XCount * YCount (128 * 128 = 16384).
var array = new int[128 * 128];
md = new MatrixData<int>(128, 128, array);
// ⚠️ IMPORTANT: The array is stored by reference. Any external modifications to it 
// will immediately affect the MatrixData instance, and vice versa.

// Setting metadata and physical units (optional but useful for plotting/UI)
md.XUnit = "mm"; 
md.YUnit = "mm"; 
md.Metadata["key"] = "value"; 


// --------------------------------------------------------------------------------
// 2. Raw Array Access (Maximum Performance)
// --------------------------------------------------------------------------------
// Directly accessing the internal 1D array is the absolute fastest way to process 
// elements with minimal overhead.

var data = md.GetArray(); // data.Length == md.XCount * md.YCount

for(int iy = 0; iy < md.YCount; iy++)
{
    double y = md.YValue(iy);    // Get the physical Y coordinate for the grid index iy
    int offset = iy * md.XCount; // Pre-calculate the row starting index for speed
    
    for (int ix = 0; ix < md.XCount; ix++)
    {
        double x = md.XValue(ix); // Get the physical X coordinate for the grid index ix
        int index = offset + ix;  // Calculate the flat 1D index
        
        var value = data[index];
        // Apply some high-performance operation...
        
        data[index] = ix + iy;    // Write back the value
    }
}

// Retrieve the minimum and maximum values in the frame.
// This is calculated on demand and automatically cached for subsequent calls.
var (min, max) = md.GetValueRange(); 


// --------------------------------------------------------------------------------
// 3. High-Level Data Access and Modification
// --------------------------------------------------------------------------------

// Method A: Functional Generation via lambda (`Set`)
// Highly readable, but comes with a slight delegate overhead per pixel.
md.Set((ix, iy, x, y) => (int)(x * x + y * y)); 

// Method B: The 2D Indexer [ix, iy]
// Accepts and returns the exact underlying data type (int).
// Note: ix (column) comes first, then iy (row).
var value11 = md[1, 1]; 
md[1, 1] = value11 + 2;

// Method C: Type-Agnostic Set Methods (useful when working with IMatrixData interface)
// Equivalent to md[1, 1] = 50, but takes a double and casts it internally.
md.SetValueAt(1, 1, 50.0); 

// Method D: Typed Set Methods
// Bypass the double casting if you already know the specific type.
// The 3rd parameter is the frameIndex (0 for a single frame).
md.SetValueAtTyped(1, 1, 0, 50); 

// Method E: Setting via Physical Coordinates
// Sets the value at physical position (0.5, 0.5) to 100. 
// The coordinates are automatically resolved to the nearest grid index.
md.SetValue(0.5, 0.5, 0, 100); 

// Method F: Index-based Get Methods
var value11AsDouble = md.GetValueAt(1, 1);    // Returns casted double
var value11AsInt = md.GetValueAtTyped(1, 1);  // Returns exact int


// --------------------------------------------------------------------------------
// 4. Spatial Interpolation (Physical Coordinates)
// --------------------------------------------------------------------------------
// MatrixData<T> can sample data at arbitrary physical coordinates (x, y).

// By default (interpolate: false), it returns the exact T value (int) of the nearest grid point.
var nearestValue = md.GetValue(0.7, -0.2); 

// You can estimate the T value using Bilinear Interpolation of the 4 nearest neighbors.
var valueByBilinear = md.GetValue(0.7, -0.2, interpolate: true);

// ⚠️ NOTE ON INTEGER TYPES: 
// When T is an integer, `GetValue` with interpolation returns a casted (truncated) int.
// To preserve the precision of the bilinear interpolation, use `GetValueAsDouble`.
var preciseInterpolatedValue = md.GetValueAsDouble(0.7, -0.2, interpolate: true);
```

## Tutorial for a Multi-Dimensional Data

```csharp
//
// This is a comprehensive tutorial (Cheat Sheet) on how to create, access, 
// and manipulate MatrixData<T> with multiple frames and hyper-dimensions.
//

// --------------------------------------------------------------------------------
// 1. Creation and Axis Definitions
// --------------------------------------------------------------------------------

// Create MatrixData<T> with multiple frames. It allocates List<T[]> internally to store the data.
var md = new MatrixData<float>(128, 128, 32); 
md.SetXYScale(-1, 1, -1, 1);

// By default, md just has a simple 1D "Frame" axis (0 to 31). 
// You can define hyper-stack dimensions using Axis objects.
md.DefineDimensions(
    Axis.Channel(2),    // Channel axis with 2 channels (index: 0 to 1)
    Axis.Z(16, -1, 1)   // Z axis with 16 slices (index: 0 to 15, physical: -1 to 1)
);
// Note: The total product of axis lengths must match the number of frames (2 * 16 = 32 frames).
// Axis.Channel, Axis.Z, and Axis.Time are factory methods for common axes.

// A more elegant way is to initialize everything directly in the constructor:
var xyczt = new MatrixData<ushort>(
    Scale2D.Centered(256, 256, 2, 2), // Scale2D defines X/Y dimensions and coordinates
    Axis.Channel(2),                  // C = 2
    Axis.Z(16, -1, 1),                // Z = 16 (-1 to 1 µm)
    Axis.Time(64, 0, 10)              // T = 64 (0 to 10 seconds)
);
// This creates a 5D dataset with 256x256 pixels and 2048 frames (2 * 16 * 64).

// You can also define custom generic axes:
var hyperStack = new MatrixData<double>(
    Scale2D.Pixels(128, 128),
    new Axis(5, 400, 800, "Wavelength", "nm"),          // Maps index 0~4 to physical 400~800 nm
    new Axis(4, 0, 1, "Sensor", isIndexBasedAxis: true) // Simple index-based axis
);

// You can wrap existing arrays without copying data (Zero-allocation initialization):
var list = new List<byte[]> { new byte[128*128], new byte[128*128], new byte[128*128] };
var md2 = new MatrixData<byte>(128, 128, list);


// --------------------------------------------------------------------------------
// 2. Handling Frames and Data Access
// --------------------------------------------------------------------------------

// Get the raw 1D array for a specific frame index.
var array1 = md.GetArray(1); 

// If you omit the frame index (or pass -1), it returns the array for the ActiveIndex.
var arrayActiveIndex = md.GetArray(); 

// ActiveIndex is mainly used for UI visualization (e.g., slider changes).
// Changing it fires the ActiveIndexChanged event.
md.ActiveIndex = 2; 
var array2 = md.GetArray(); // Now returns the array for frame index 2

// For bulk processing across all frames, ForEach is the fastest approach.
md.ForEach((frameIndex, array) =>
{
    // Apply operations to the 1D 'array' here.
    // Parallel processing is enabled by default.
}, useParallel: true); 

// Random access using the explicit frame index:
var val1 = md.GetValueAt(1, 1, 2);             // Grid index (ix=1, iy=1) at frame 2
var val2 = md.GetValue(0.2, 0.5, 2, true);     // Interpolated physical (x=0.2, y=0.5) at frame 2

md.SetValueAt(1, 1, 2, 100);                   // Set via grid index
md.SetValue(0.2, 0.5, 2, 200);                 // Set via physical coordinates

// Multi-dimensional indexer [ix, iy, c, z, t]:
var val3 = xyczt[2, 2, 0, 2, 3]; // X=2, Y=2, Channel=0, Z=2, Time=3
xyczt[2, 2, 0, 2, 3] = 10; 
// The flat frame index is calculated automatically and efficiently in the background.


// --------------------------------------------------------------------------------
// 3. Accessing Axis Properties
// --------------------------------------------------------------------------------

// Retrieve an axis object by its name (case-insensitive).
var zaxis = xyczt["Z"]; 
double zmin = zaxis.Min;                // e.g., -1.0
double zmax = zaxis.Max;                // e.g., 1.0
int znum = zaxis.Count;                 // e.g., 16
int zindex = zaxis.IndexOf(0.2);        // Returns the index nearest to the physical value 0.2
double zpos = zaxis.ValueAt(2);         // Returns the physical value at index 2


// --------------------------------------------------------------------------------
// 4. Slicing, Projection, and Volume Operations (Zero-copy design)
// --------------------------------------------------------------------------------
// NOTE: Most structural operations return light-weight views or utilize zero-copy 
// mechanics for maximum performance.

// Slicing: Extract sub-datasets based on specific axis values.
var xytz = xyczt.SelectBy("Channel", 2);                 // Drops "Channel" axis, returns 4D data
var xyz = xyczt.ExtractAlong("Z", [1, 0, 2]);            // Extracts a 3D Z-stack at C=1, T=2
var xy = xyczt.SliceAt(("Channel", 0), ("Z", 1), ("Time", 2)); // Returns a single 2D frame

// VolumeAccessor: View multi-dimensional data as a 3D volume (X, Y, and one target axis).
// Uses ActiveIndex to fix the remaining dimensions (like Time or Channel).
var volume = xyczt.AsVolume("Z"); 

// Projections (Dimensionality Reduction):
// Create a Maximum Intensity Projection (MIP) along the Y axis (resulting in an X-Z image).
var xzProj = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Maximum); 

// Reshaping/Restacking:
// Re-slice the volume along the X axis, returning a new stack of Y-Z planes.
var yzx = volume.Restack(ViewFrom.X); 

// Single Plane Extraction from Volume:
var xzSlice = volume.SliceAt(ViewFrom.Y, 2); // Get the X-Z slice at Y index = 2
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

### SliceAt (2D), ExtractAlong (3D), and SelectBy (N-1D)

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

// SelectBy: Extract (N-1)D data by fixing one axis
var xyzt = timeLapse.SelectBy("Channel", indexInAxis: 1);
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
var reordered = matrix.Reorder([2, 0, 4, 1, 3]);

// Use case: Sort frames by acquisition time from metadata
var sortedIndices = Enumerable.Range(0, matrix.FrameCount)
    .OrderBy(i => matrix.Metadata[$"Time_{i}"])
    .ToList();
var sorted = matrix.Reorder(sortedIndices);

// You can repeat the same frame because it creates the reference to each frame without copying data
var repeated = matrix.Reorder([0, 0, 1, 1, 2, 2]);
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
// Restack: Reorganize volume for different viewing axes (as a new MatrixData)
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
    new Axis(31, 400, 700, "Wavelength", "nm"),  // 400-700 nm
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
    .SelectBy("Channel", 1)              // Extract GFP channel
    .SelectBy("FOV", 1)                  // Select center FOV
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
    .SelectBy("DayOfWeek", 1)        // Mondays only
    .SelectBy("Humidity", 0)     // Dry conditions only
    .SelectBy("Pressure", 1)      // Mid-pressure only
    .ExtractAlong("Altitude", new[] { 1, 6, 0, 1 });  // Altitude stack of sensor 1 on 6 AM in January

```


### 🎨 Creative Use Cases? 

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

The following are just representative methods and may not be comprehensive.

### MatrixData<T> Core Methods

#### Construction
- `MatrixData(int xCount, int yCount, T[]? array = null)` — single-frame; optionally supply a preallocated array for the frame.
- `MatrixData(int xCount, int yCount, int frameCount)` — allocate `frameCount` frames (new arrays).
- `MatrixData(int xCount, int yCount, List<T[]> arrayList)` — use provided arrays (one per frame). Each array must have length `xCount*yCount`.
- `MatrixData(int xCount, int yCount, List<T[]> arrayList, List<List<double>> minValueList, List<List<double>> maxValueList)` — provide structured per-frame min/max lists (useful for `Complex` and other structured types).
- `MatrixData(int xCount, int yCount, List<T[]> arrayList, List<double> primitiveMinValueList, List<double> primitiveMaxValueList)` — provide scalar per-frame min/max values; converted internally to structured lists.
- `MatrixData(Scale2D scale)` — create a single frame using `scale` for XY coordinates.
- `MatrixData(Scale2D scale, params Axis[] axes)` — create with specified `scale` and frame `axes`; total frames = product of axis counts; constructor validates the resulting frame count.

Notes:
- All constructors validate plane size and initialize the internal arrays and `Dimensions` object.
- Min/max statistics are cached per-array and computed on demand; if you provide min/max lists they are used as the initial cache. Otherwise the cache is empty and will be populated on first request.

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

#### Statistics (updated)
- `(double Min, double Max) GetValueRange()`
- `(double Min, double Max) GetValueRange(int frameIndex)`
- `(double Min, double Max) GetGlobalValueRange()`
- `double GetMinValue()`
- `double GetMaxValue()`
- `void InvalidateValueRange()`
- `void InvalidateValueRange(int frameIndex)`


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


