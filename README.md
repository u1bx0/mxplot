<div align="right">
  <img src="docs/images/mxplot_pre.png" width="130" alt="MxPlot Logo">
</div>

<div align="center">

# MxPlot

**High-Performance Multi-Axis Matrix Visualization Ecosystem**

[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![Package](https://img.shields.io/badge/version-0.0.4--alpha-orange)](https://github.com/u1bx0/mxplot/releases)
![NuGet Version](https://img.shields.io/nuget/v/MxPlot?style=flat-square&color=blue)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

<p>
  <b>A unified suite of libraries for handling complex scientific and engineering datasets.</b>
</p>
</div>

**MxPlot** is a modular library designed for high-performance data management and visualization. 
It enables efficient handling of multi-dimensional datasets—XY matrices extended by Time, Z-Space, Channel, Wavelength, FOV (field of view), and more—with a focus on high throughput, physical coordinate integrity, and seamless UI binding.

## 🚀 Just a Quick Look at the Code
```csharp
// You can create a multi-dimensional data with physical coordinates
var md = new MatrixData<double>(
    Scale2D.Centered(512, 512, 4, 4),
    Axis.Channel(3), Axis.Z(32, -5, 5, "µm"),Axis.Time(100, 0, 10, "s"));

// Easy access to each frame 
md.ForEach((i, array) => // array is the internal T[] of the frame at index i
{
    var (c, z, t) = md.Dimensions.GetAxisValuesStruct(i);
    SetByYourOwnFunction(array, c, z, t);
});
```
More details are provided below.

## 🏗️ Repository Structure

This repository hosts the **MxPlot ecosystem**, organized into the following library suite (including modules currently under active development):

- **MxPlot (Metapackage)**: A convenient entry point that bundles the core and common extensions.
- **MxPlot.Core**: The foundational, dependency-free data engine (`MatrixData<T>`).
- **MxPlot.Extensions.Tiff / .Hdf5**: Specialized high-performance file I/O packages.
- **MxPlot.Extensions.Fft**: FFT processing utilities.
- **MxPlot.Extensions.Images**: Image loading utilities.
- **MxPlot.Wpf / .WinForms**: Native UI controls for visualization (In Development).

At its heart, **MatrixData\<T\>** serves as the central engine, engineered to maximize data throughput. 
While the visualization layer (**MxPlot.WinForms** or **MxPlot.Wpf**) is currently under development, this core package is deliberately decoupled from any UI dependencies. 
This makes it a versatile solution for high-performance matrix manipulation and analysis. 
I hope this package serves as a robust foundation for your own novel applications!

> 💡 **This project fully leverages up-to-date AI tools, including GitHub Copilot (Claude Sonnet 4.5) and Gemini 3 pro, for coding, optimization, testing, and documentation.**


## 🎯 What is Multi-Axis Data?

Multi-axis data refers to datasets organized along multiple independent dimensions beyond simple 2D matrices such as:

- **Time-series microscopy**: `[X, Y]` with `[Time, Z, Channel]`
- **Hyperspectral imaging**: `[X, Y]` with `[Wavelength, Time]`
- **Multi-FOV (Tiling) scanning**: `[X, Y]` with`[FOV, Z, Time]`
- **Sensor arrays**: `[X, Y]` with `[Sensor, Frequency, Time]`

MxPlot.Core handles arbitrary axis combinations while maintaining physical coordinates, units, and metadata integrity.

## ✨ Features

- 🎯 **Multi-Axis Management**: Flexible dimension definition with physical coordinates and units
- 🔐 **Type Safety**: Full support for all numeric types including `Complex` and user-defined structures.
- 📊 **Dimensional Operators**: Transpose, map, reduce, slice at frame, extract along axis, select by axis
- 🧊 **Volumetric Manipulation**: 3D volume access with projections and restacking along x, y, and z axes
- 🚀 **High Performance**: Parallel and SIMD optimization, as well as Generic Math (.NET 10)
- 🧪 **Scientific-Friendly Format**: Hyperstacks as bio-format (OME-TIFF), ImageJ-hyperstack TIFF, and HDF5
- 📂 **Efficient Storage**: Direct binary serialization/deserialization (.mxd) with compression
- 🧮 **Arithmetic Operations**: Element-wise operations (add, subtract, multiply, divide)

**Coming Soon:**
- 📏 **Line Profile Extraction**: Extract intensity profiles along arbitrary lines *(To be implemented)*
- 🔬 **Filtering**: Gaussian, Median, and custom convolution filters *(To be implemented)*

## 📦 Installation

This ecosystem is provided as several NuGet packages.

- **MxPlot (Recommended)**: For most users. Includes core and library-dependent extensions.
```bash
dotnet add package MxPlot --version 0.0.4-alpha
```

- **MxPlot.Core**: For developers building their own tools without extra dependencies.
```bash
dotnet add package MxPlot.Core --version 0.0.4-alpha
```

Or add to your project file:

```xml
<PackageReference Include="MxPlot" Version="0.0.4-alpha" />
```

### 🛠️ For Developers (Manual Setup)

If you want to modify the source code, clone the repository and reference the project directly:

```Bash
git clone https://github.com/u1bx0/MxPlot.git
```
Then add a project reference to MxPlot.Core.csproj in your solution.

### **Requirements**

- .NET 10.0 or .NET 8.0


## 🗿 Design Concepts and Philosophy
**MxPlot** is designed not as a general-purpose math library, but as a backend for scientific visualization.
- **Stateful "Active Cursor"**: `MatrixData<T>` maintains an internal state as a property of `ActiveIndex`, enabling seamless binding to UI sliders without external state management.
- **Structure over Algebra**: Focuses on high-performance memory management, slicing, and reshaping of multi-dimensional data. Complex linear algebra is left to dedicated libraries.
- **Pixel-Centered Coordinates**: Physical scaling measured on pixel centers (i.e. pixel size (or step) = (x<sub>max</sub> - x<sub>min</sub>) / (num - 1)).
- **Left-Bottom Origin**: Coordinate origin is at the left-bottom corner (Y increases upwards).
- **Immutable Matrix Size**: Matrix dimensions are fixed after creation for performance.
- **Backing 1D-Array List**: Uses `List<T[]>` for frame storage to allow efficient memory pinning.
- **Reactive Cache Synchronization**: Statistics (min/max) are synchronized via shared list references across shallow copies, ensuring data integrity without redundant calculations.

## 🚀 Quick Start

### Basic 2D Matrix

```csharp
using MxPlot.Core;

// Create a 2D matrix with physical coordinates
var md = new MatrixData<double>(100, 100);
md.SetXYScale(-10, 10, -10, 10); // Physical range: -10 mm to +10 mm
md.XUnit = "mm";
md.YUnit = "mm";

// Set data using a lambda function: (ix, iy) refers to pixel indices, (x, y) to physical coordinates
md.Set((ix, iy, x, y) => Math.Sin(x) * Math.Cos(y));

// You can dirctly access the internal array (the most efficient)
double[] array = md.GetArray(); // Get the frame and access each element by array[iy * md.XCount + ix]

// Get statistics
var (min, max) = md.GetMinMaxValues();
Console.WriteLine($"Value range: [{min:F2}, {max:F2}]");
```

### Multi-Axis Data (e.g. 3D Time-series - 5D)

```csharp
using MxPlot.Core;
using MxPlot.Core.IO;

// Create 512×512 images with 10 Z-slices and 20 time points (200 frames total)
var data = new MatrixData<ushort>(
    Scale2D.Centered(512, 512, 10, 10), //X and Y have data coordinates with -5 to 5.
    Axis.Z(10, 0, 50, "µm"),        // Z: 0-50µm, 10 slices (unit is omissible)
    Axis.Time(20, 0, 10, "s")       // Time: 0-10 seconds, 20 frames
);

// Access specific frame (Z=5, Time=10)
data["Z"].Index = 5;
data["Time"].Index = 10;
// ushort[] for the selected frame
var frame = data.GetArray(); // Get current frame (Z=5, Time=10)

// Extract 3D data at specific Z-depth
var timeSeriesAtZ3 = data.SelectBy("Z", 3); // Returns MatrixData<ushort> with Time axis

// Save to compressed binary format
MatrixDataSerializer.Save("data.mxd", data, compress: true);

// Load without knowing the type
IMatrixData loaded = MatrixDataSerializer.LoadDynamic("data.mxd");
```

## 🎯 Key Features

### Multi-Axis Data Management

MxPlot.Core's **DimensionStructure** enables flexible multi-axis data organization:

```csharp
// Example: Hyperspectral Time-series Imaging
// Structure: [X, Y] × [Wavelength, Time, FOV]
var scale = new Scale2D(1024, -50, 50, 1024, -50, 50); // µm
var hyperData = new MatrixData<double>(scale,
    new Axis(31, 400, 700, "Wavelength"), // 400-700 nm, 31 channels
    Axis.Time(100, 0, 10, "s"),           // 10 seconds, 100 frames
    new FovAxis(4, 2)                      // 4×2 tiled FOV array with 8 tiles
);

// Total frames: 31 × 100 × 8 = 24,800 frames
Console.WriteLine($"Total: {hyperData.FrameCount} frames");

// Navigate axes
hyperData["Wavelength"].Index = 15; // 550nm
hyperData["Time"].Index = 50;       // 5 seconds
hyperData["FOV"].Index = 3;         // FOV tile [1,0]

// Extract data along specific axis
var timeSeriesAt550nm = hyperData.ExtractAlong("Time", 
    fixedCoords: new[] { 15, 0, 0 }); // Wavelength=15, FOV=0

// Get min/max across all time points at specific wavelength
var (minVal, maxVal) = hyperData.GetValueRange("Time", 
    fixedCoords: new[] { 15, 0, 0 });
```

### ⚙️ Element-wise Operations (The best way to process pixel data)

The recommended way to initialize or set the 5D matrix data efficiently is to use array data (T[]) directly.

```csharp
//Define 5D matrix data with Channel, Z, Time axes
var axes = new Axis[] { 
    Axis.Channel(3), //ch value= 0 - 2
    Axis.Z(11, -2, 2), 
    Axis.Time(21, 0, 20) 
};
//Note: Axif.Channel, .Z, .Time, .Frame are built-in axis types.
//          Otherwise use: new Axis(num, min, max, "name");

var scale = Scale2D.Centered(201, 201, 4, 4); // -2 to 2 for both X and Y
var md = new MatrixData<double>(scale, axes);

// Function providing the pixel value at (x,y,c,z,t)
double PixelValue(double x, double y, double c, double z, double t)
{
    //just example
    return x * y * c * z * t;
}

//  ================================//
// Set the value to each pixel at each frame
md.ForEach((i, array) => //parallel option is true by default
{
    //Axis values at the frame index i are returned as the order of axes.
    var (c, z, t) = md.Dimensions.GetAxisValuesStruct(i);
    //Best way to calculate the xy position from the array index
    for (int iy = 0; iy < scale.YCount; iy++)
    {
        double y = scale.YValue(iy); //physical position (md.YValue(iy) is also available.)
        int offset = iy * scale.XCount; //to access the array index directly
        for (int ix = 0; ix < scale.XCount; ix++)
        {
            double x = scale.XValue(ix); //physical position (md.XValue(ix) is also available.)
            //Evaluation of the value at (x, y, c, z, t)
            double val = PixelValue(x, y, c, z, t);
            //Set the value to the pixel
            array[offset + ix] = val;
        }
    }
}); //After ForEach action, the min and max values at each frame are updated automatically.

// =================================//
// If you like more simplified expression,
// (But this way may be a bit slower than the previous one.)
Enumerable.Range(0, md.FrameCount).AsParallel().ForAll( i =>
{
    var (c, z, t) = md.Dimensions.GetAxisValuesStruct(i);
    //Each point is iterated sequentially.
    md.Set(i, (ix, iy, x, y) => PixelValue(x, y, c, z, t));
});  
```
### 🔧 Primitive Arithmetic Operations

```csharp
// Background subtraction (common in microscopy)
var signal = new MatrixData<double>(512, 512);
var background = new MatrixData<double>(512, 512);
var corrected = signal.Subtract(background);

// Flat-field correction
var flatField = new MatrixData<double>(512, 512);
var normalized = signal.Divide(flatField);

// Gain and offset correction
var gainCorrected = signal.Multiply(1.5);         // Gain: ×1.5
var offsetCorrected = signal.Add(-100);  // Offset: -100
```

### 📊 Complex Number Support

```csharp
using System.Numerics;

var fftResult = new MatrixData<Complex>(256, 256);
fftResult.Set((ix, iy, x, y) => new Complex(x, y));

// Complex-specific statistics
var (magMin, magMax) = fftResult.GetMinMaxValues(0, ComplexValueMode.Magnitude);
var (phaseMin, phaseMax) = fftResult.GetMinMaxValues(0, ComplexValueMode.Phase);
var (powerMin, powerMax) = fftResult.GetMinMaxValues(0, ComplexValueMode.Power);
```

### 💾 Unified File I/O (Core + Extensions)
MxPlot handles multi-dimensional data with a flexible, format-agnostic API. By adding extensions, you can bridge MxPlot with professional scientific software.

```csharp
using MxPlot.Core;
using MxPlot.Extensions.Tiff;  // For OME-TIFF
using MxPlot.Extensions.Hdf5;  // For HDF5

// --- Saving: Choose your format strategy ---
var matrix = new MatrixData<float>(512, 512, 3, 10); // XY + 3 Channels + 10 Timepoints

// 1. Native format (Fast, compact)
matrix.Save("data.mxd", new MxBinaryFormat()); 

// 2. Scientific formats (Requires Extension packages)
matrix.Save("result.ome.tif", new OmeTiffFormat()); // For Fiji/ImageJ
matrix.Save("archive.h5", new Hdf5Format());      // For HDFView/Python


// --- Loading: Dynamic and Type-Safe ---
// MxPlot automatically detects the format and returns the best-fit MatrixData.
IMatrixData data = MatrixData.Load("unknown_file.ome.tif", new OmeTiffFormat());

Console.WriteLine($"Dimensions: {data.Dimensions}"); // e.g., "512x512, C:3, T:10"
Console.WriteLine($"Physical Size: {data.XAxis.Range} {data.XUnit}");

// Pattern matching for type-specific processing
if (data is MatrixData<float> floatData)
{
    // High-performance access to float pixels
    float val = floatData[0, 0]; 
}
```

### 📐 Data Processing

```csharp
using MxPlot.Core;
using MxPlot.Core.Processing;

// === DimensionalOperator: Multi-dimensional data manipulation ===

// Transpose (swap X and Y axes for all frames)
var transposed = matrix.Transpose();

// Crop by pixel coordinates
var cropped = matrix.Crop(startX: 25, startY: 25, width: 50, height: 50);

// Crop by physical coordinates
var physicalCrop = matrix.CropByCoordinates(xMin: -5, xMax: 5, yMin: -5, yMax: 5);

// Center crop
var centered = matrix.CropCenter(width: 50, height: 50);

// Slice at the specific indices (params of tuple: (axisName, index))
var timeSlice = data.SliceAt(("Time", 10)); //2D image from XYT

// Extract data along specific axis (creates new MatrixData with a single axis)
var zStackAtTime5 = data.ExtractAlong("Z", new[] { 0, 5 }); // Extract Z-stack (3D) at Time=5

// Snap to specific axis value (reduces dimension by 1)
var snapShot = data.SnapTo("Z", 2); // Extract hyperstack at Z=2 (N-1D)

// Map: Apply function to each pixel across all frames
var normalized = matrix.Map<double, double>((value, x, y, frame) => 
    value / 255.0
);

// Reduce: Aggregate across frame axis
var averaged = timeSeries.Reduce((x, y, values) => 
{
    double sum = 0;
    foreach (var v in values) sum += v;
    return sum / values.Length;
});

// === VolumeAccessor: 3D volume operations and projections ===

// Convert MatrixData to 3D volume (requires single axis or axis name for multi-axis)
var volume = data.AsVolume("Z"); // Create volume along Z axis

// Create orthogonal projections
var projMaxZ = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);  // Maximum intensity projection
var projMinZ = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Minimum); // Minimum intensity projection
var projAvgZ = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Average);   // Average intensity projection

// Project from different viewing directions
var projMaxX = volume.CreateProjection(ViewFrom.X, ProjectionMode.Maximum);  // YZ plane
var projMaxY = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Maximum);  // XZ plane

// Restack volume for different viewing axes
var restackedX = volume.Restack(ViewFrom.X); // Reorganize for X-axis viewing
var restackedY = volume.Restack(ViewFrom.Y); // Reorganize for Y-axis viewing

// Extract 2D slice from volume at specific depth
var sliceAtZ5 = volume.SliceAt(ViewFrom.Z, 5); // Extract XY plane at Z=5

// Reduce volume along axis with custom function
var maxProjection = volume.ReduceZ((x, y, values) => values.Max());
var customReduction = volume.ReduceZ((x, y, values) => 
{
    // Custom reduction logic (e.g., median, percentile, etc.)
    return values.OrderBy(v => v).Skip(values.Length / 2).First();
});

// Direct voxel access (no bounds checking for performance)
double voxelValue = volume[x: 10, y: 20, z: 5];

// === LineProfileExtractor: Extract profiles along arbitrary lines ===
// ⚠️ To be implemented in future release

// Extract profile along a line
// var lineProfile = matrix.ExtractLineProfile(
//     startX: 10.5, startY: 20.3,
//     endX: 80.7, endY: 90.2,
//     numPoints: 100,
//     useInterpolation: true  // Bilinear interpolation
// );

// === MatrixDataFilter: Spatial filtering ===
// ⚠️ To be implemented in future release

// Apply Gaussian filter
// var gaussianFiltered = matrix.ApplyGaussianFilter(sigma: 2.0);

// Apply median filter (noise reduction)
// var medianFiltered = matrix.ApplyMedianFilter(kernelSize: 3);

// Apply custom convolution filter
// double[,] customKernel = {
//     { -1, -1, -1 },
//     { -1,  8, -1 },
//     { -1, -1, -1 }
// };
// var edgeDetected = matrix.ApplyConvolution(customKernel);
```

## 📖 More Detailed Information and Performance Reports

The detailed documentaions and performance benchmark reports may be prepared separately.

*However, it may be better to consult your AI agent to understand the usage and the ideas behind the implementation.*

> Hey, could you please tell me the details of the following library? - http://github.com/u1bx0/mxplot


## 📊 Version History

**v0.0.5-alpha** (Current - Improving the internal logic with breaking changes)
- 🧠 Frame Sharing & Memory Model: Refined the zero-cost O(1) frame reordering (Reorder) using underlying array reference sharing.
- 🔄 Explicit Copy Semantics: Clarified mutation semantics and introduced explicit deep copying via Duplicate() and Clone().
- ⚡ Lazy Min/Max Evaluation: Implemented lazy evaluation and caching for frame min/max values (GetValueRange), optimizing performance during bulk array mutations, which largely modified the internal logics of MatrixData.
- 📚 Comprehensive Documentation: Added and updated extensive Markdown guides for Core Operations, Frame Sharing Model, Volume Accessor, and Dimension Structure.

**v0.0.4-alpha** (added new packages and  introduced breaking changes)
- 🔌 Generic Bridge: Enabled non-generic layers (UI/ViewModels) to invoke strongly-typed image processing operations without compile-time knowledge of generic type <T>.
- 🛠 Visitor Pattern: Introduced IMatrixData.Apply(IOperation) as a unified dispatch entry point to dynamically resolve and execute Volume, Filter, and Dimensional operations. 
- ➕ Added MxPlot.Extensions.Images package for useful image loading via SkiaSharp (PNG, JPEG, BMP, TIFF).
- ➕ Added MxPlot.Extensions.Fft package for 2D FFT processing via MathNet.Numerics.
- 🔄 Method Renaming (Breaking): Renamed At to GetFrameIndexAt in DimensionStructure.
- 🔄 Method Renaming (Breaking): Renamed all GetFrameIndexFrom methods to GetFrameIndexAt in DimensionStructure to unify the coordinate-based API.


**v0.0.3-alpha** (Some modifications and reorganization of packages)
- 🏗️ **Metapackage Structure**: Reorganized as a metapackage `MxPlot` bundling `MxPlot.Core` and common extensions for easier installation and management.
-  🔄 **Method Renaming**: Renamed `XAt`/`YAt` to **`XValue`/`YValue`** for better clarity and naming consistency.
 - ➕ Added MxPlot.Extensions.Tiff and MxPlot.Extensions.HDF5 packages for specialized file I/O 
- 🏗️ **Type Optimization**: Changed `Scale2D` from `record struct` to **`readonly struct`** to ensure immutability and improve performance.
- ➕ **Added `GetAxisValues` / `GetAxisValuesStruct`**: Now supports deconstruction for more intuitive axis value retrieval.
- ➕ **Added `GetAxisIndicesStruct`**: Introduced struct-based deconstruction support for axis indices to improve performance and readability.
- 🏗️ **Enhanced `IMatrixData`**: Implemented Facade pattern methods for `DimensionStructure`, simplifying the interface for complex data navigation.
- 📖 Updated README and installation guides.
- 🎯 Minor bug fixes in the core data processing engine.
  
**v0.0.2-alpha** (First core implementation )
- ✨ **NEW**: `VolumeAccessor<T>` - High-performance 3D volume operations with readonly struct
  - `AsVolume()` method for MatrixData with multi-axis support
  - Projections with maximum, minimum, and average intensity from X, Y, Z viewing directions
  - `Restack()` for efficient axis reorganization
  - Direct voxel access with `[ix, iy, iz]` indexer (no bounds checking for performance)
- ⚡ **NEW**: `VolumeOperator` - Optimized volume projections with tiled memory access
  - 2-3.4x speedup with tiling optimization for Z-axis projections
  - Cross-platform performance optimization (Intel AVX-512 & Apple ARM64)
- 🎯 **ENHANCED**: `DimensionalOperator.ExtractAlong()` - Extract data along specific axis
  - Support for multi-axis data with base indices
  - ActiveIndex-based extraction
- 📈 **IMPROVED**: Performance benchmarks and documentation
- ⚠️ **PLANNED**: `LineProfileExtractor` and `MatrixDataFilter` (to be implemented in future releases)

**v0.0.1-alpha** (Package name reservation)
- Package name reserved on NuGet
- No implementation (placeholder only)

**Initial Development (Pre-release)**
- Core multi-axis container with dimension management
- Binary I/O (.mxd) with compression
- Dimensional & cross-sectional operators
- Arithmetic operations with SIMD optimization
- OME-TIFF and ImageJ-compatible TIFF support via MxPlot.Extensions.Tiff packages (will be provided separately)
- DHF5 support via MxPlot.Extensions.HDF5 package (will be provided separately)

---
> **🚧 Disclaimer (IMPORTANT)🚧**
> This library is "over-engineered" by design, driven by AI tools.
> While I maintain it for my own purpose, I share it in the hope that it serves as a powerful engine for other developers.
> However, please be aware of potential bugs, as code testing is not yet complete.
>
> *Maintained by YK ([@u1bx0](https://github.com/u1bx0))*
