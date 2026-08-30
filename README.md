<div align="right">
  <img src="docs/images/mxplot_pre.png" width="130" alt="MxPlot Logo">
</div>

<div align="center">

# MxPlot

**High-Performance Multi-Axis Matrix Visualization Ecosystem**

[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![Package](https://img.shields.io/badge/version-0.3.0-orange)](https://github.com/u1bx0/mxplot/releases)
![NuGet Version](https://img.shields.io/nuget/v/MxPlot?style=flat-square&color=blue)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

<p>
  <b>A unified suite of libraries for handling complex scientific and engineering datasets.</b>
</p>
</div>

**MxPlot** is a modular ecosystem for high-performance scientific data management and visualization.
It covers the full stack — from a dependency-free data engine (`MatrixData<T>`) to a cross-platform 
interactive viewer (`MxPlot.UI.Avalonia`, `MxPlot.App`) — enabling efficient handling of 
multi-dimensional datasets: XY matrices extended by Time, Z-Space, Channel, Wavelength, FOV, and more,
with a focus on high throughput, physical coordinate integrity, and seamless UI binding.

<div align="center">
  <img src="docs/images/mp1.png" height="200" alt="Single view"> 
  <img src="docs/images/mp2.png" height="200" alt="Orthogonal view"> 
    <img src="docs/images/mp4.png" height="200" alt="Composite">
  <br/>
    <img src="docs/images/mp3.png" height="200" alt="Analysis">
  <img src="docs/images/mp5.png" height="200" alt="ColorCoded">
    

</div>

---

## 🖥️ MxPlot.App — See What MxPlot Can Do

>  MxPlot.App is a fully-featured, cross-platform scientific viewer  
> that embodies the entire MxPlot library ecosystem in a single application.  
> It is also the **reference implementation** showing how to build on top of MxPlot.

| Feature | Description |
|:---|:---|
| 📂 Multi-format open | OME-TIFF, HDF5, FITS, CSV, `.mxd` and more via plugin |
| 🔭 Multi-axis navigation | Slice through Z, Time, Channel, Wavelength, FOV interactively |
| 📐 Orthogonal views | XZ / YZ projections and live cross-section display |
| ✏️ ROI & overlays | Line, Rectangle, Oval with live statistics |
| 📊 Built-in profile plots | Arbitrary-angle line profiles with Gaussian/Lorentzian fit |
| 🪟 Multi-window dashboard | Sync, tile, and link multiple datasets |
| ✂️ Crop / Substack | Extract Z-range or 3D volume regions |


📦 **Pre-built binaries** (Windows x64 / macOS Apple Silicon) →
**[Download from Releases](https://github.com/u1bx0/mxplot/releases)**
&nbsp;·&nbsp;
📖 **[MxPlot.App Details](./MxPlot/MxPlot.App/README.md)**

---

## 🚀 Just a Quick Look at the Code

> MxPlot is first and foremost a **C# library**.  
> The same capabilities you see in MxPlot.App are fully accessible from your own application.

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

This repository hosts the **MxPlot ecosystem**, organized into the following library suite:

- **MxPlot (Metapackage)**: A convenient entry point that bundles the core, UI layer, and common extensions.
- **MxPlot.Core**: The foundational, dependency-free data engine (`MatrixData<T>`).
- **MxPlot.UI.Avalonia**: Cross-platform visualization library built on [Avalonia UI](https://avaloniaui.net/). Runs on Windows, macOS, and Linux. Embeddable from WinForms or WPF via `MxPlotHostApplication`.
- **MxPlot.UI.Avalonia.Video**: Video export extension for `MxPlot.UI.Avalonia` — uncompressed AVI (via [SharpAvi](https://github.com/baSSiLL/SharpAvi)) and H.264 MP4 (via an external `ffmpeg`), plus a reusable base for adding further formats.
- **MxPlot.Extensions.Tiff / .Hdf5**: Specialized high-performance file I/O packages.
- **MxPlot.Extensions.Fft**: FFT processing utilities.
- **MxPlot.Extensions.Images**: Image loading utilities.
- **MxPlot.App** *(included in this repository)*: Standalone scientific viewer built on the full MxPlot stack. 
  See [🖥️ MxPlot.App](#️-mxplotapp--see-what-mxplot-can-do) above.

At its heart, **MatrixData\<T\>** serves as the central engine, engineered to maximize data throughput.
The visualization layer (**MxPlot.UI.Avalonia**) is deliberately separated from the core to keep `MxPlot.Core` dependency-free, while still providing a rich, ready-to-use UI for immediate data exploration.


## ✨ Features

### MxPlot.Core
- 🎯 **Multi-Axis Management**: Flexible dimension definition with physical coordinates and units
- 🔐 **Type Safety**: Full support for all numeric types including `Complex` and user-defined structures
- 📊 **Dimensional Operators**: Transpose, map, reduce, slice at frame, extract along axis, select by axis
- 🧊 **Volumetric Manipulation**: 3D volume access with projections (Max/Min/Mean) and restacking along x, y, and z axes
- 🚀 **High Performance**: Parallel and SIMD optimization, as well as Generic Math (.NET 10)
- 💾 **Virtual Frame Streaming**: MMF-backed on-demand loading for large files (>2 GB) — peak memory stays near one frame regardless of total size
- 🧪 **Scientific-Friendly Formats**: OME-TIFF, ImageJ-hyperstack TIFF, HDF5, FITS, CSV, and native `.mxd`
- 📂 **Format Plugin Registry**: Auto-discovers `MxPlot.Extensions.*.dll` at startup — no explicit registration required
- 🔬 **Spatial Filtering**: Median, Gaussian, and Mean kernels via the `IFilterKernel` plugin interface
- 📏 **Line Profile Extraction**: Arbitrary-angle intensity profiles with bilinear interpolation
- 🧮 **Arithmetic Operations**: Element-wise add, subtract, multiply, divide

### MxPlot.UI.Avalonia
- 🖥️ **Cross-Platform Viewer**: Runs on Windows, macOS, and Linux via **Avalonia UI 11.3** (Avalonia 11 / 12 have breaking API differences; the current release targets Avalonia 11.3.18)
- 🔭 **MatrixPlotter**: Full-featured standalone window — LUT selector, value-range bar, overlay manager, axis trackers, orthogonal views, profile plot, crop/sync/export
- 🖼️ **MxView**: Low-level Avalonia image surface — pan, zoom, SkiaSharp bitmap rendering
- 🔗 **WinForms / WPF Embedding**: `MxPlotHostApplication` lets you open `MatrixPlotter` windows from an existing WinForms or WPF app with minimal setup
- ✏️ **Interactive Overlays**: Line, Rectangle, Oval, Targeting, and Text shapes — drawn via right-click menu or API, with live statistics and profile plots
- ✂️ **Substack / 3D Crop**: Extract a Z-range substack or crop a full 3D volume region — with sync-group support across linked windows
- 💾 **File Session Management**: Unsaved-change tracking (`DirtyFlags`), close confirmation dialog, and `SaveAsAsync` / `DuplicateAsync` APIs for programmatic control
- 🎨 **Composite Rendering**: Multi-channel display with per-channel color, contrast, gain and gamma — as commonly used in fluorescence microscopy. RGB color images open composited automatically, and can be converted back to grayscale. See the [Composite Rendering Guide](./docs/MatrixPlotter_Composite_Guide.md)
- 🌈 **ColorCoded Rendering**: Live depth/time colour-coded projection — pick `Color (Max)`/`Color (Min)` in the orthogonal-view projection selector for a Z (or any frozen-axis) projection tinted by winning depth, in a linked child window with a drag-to-narrow depth histogram, adjustable palette, and Fixed/Auto intensity range. The projected data itself stays real values, so filtering, converting, and saving it all work normally — only the display is colour-coded.
- 🎛️ **External Control**: Drive the displayed LUT and value range from host code through Facade properties (`plotter.Lut`, `plotter.RangeMode`, `plotter.FixedRange`) — see the [MatrixPlotter Usage Guide](./docs/MatrixPlotter_Usage_Guide.md)
- 📜 **Scripting (`MxPlotScriptHost`)**: Open MatrixPlotter windows from .NET 10 file-based apps (`dotnet run app.cs`), console tools, or notebook cells — no host application required. `Run(script)` starts the message loop, runs your script off the UI thread, and returns when every window it opened has closed. See [MatrixPlotter Usage Guide § Scripting with MxPlotScriptHost](./docs/MatrixPlotter_Usage_Guide.md)

## 📦 Installation

This ecosystem is provided as several NuGet packages.

- **MxPlot (Recommended)**: For most users. Includes the core engine, UI layer, and common extensions.
```bash
dotnet add package MxPlot
```

> *WinForms / WPF host apps additionally require `Avalonia.Win32` and `Avalonia.Skia`.  
> Pin them to the same version as `MxPlot.UI.Avalonia` uses (`11.3.18`).  
> See [Integration Guide](./docs/MatrixPlotter_NonAvalonia_Integration_Guide.md).*

- **MxPlot.Core**: For developers building their own tools without extra dependencies.
```bash
dotnet add package MxPlot.Core
```

### 🛠️ For Developers (Manual Setup)

If you want to modify the source code, clone the repository and reference the projects directly:

```Bash
git clone https://github.com/u1bx0/MxPlot.git
```
Then add project references to `MxPlot.Core.csproj` and `MxPlot.UI.Avalonia.csproj` in your solution.

### Requirements

- .NET 10.0 or .NET 8.0
- `MxPlot.UI.Avalonia` requires **Avalonia 11.3.x** (host projects must pin `Avalonia.Win32` / `Avalonia.Skia` to the same version)


## 🗿 Design Concepts and Philosophy
**MxPlot** is designed not as a general-purpose math library, but as a backend for scientific visualization.
- **Stateful "Active Cursor"**: `MatrixData<T>` maintains an internal state as a property of `ActiveIndex`, enabling seamless binding to UI sliders without external state management.
- **Structure over Algebra**: Focuses on high-performance memory management, slicing, and reshaping of multi-dimensional data. Complex linear algebra is left to dedicated libraries.
- **Pixel-Centered Coordinates**: Physical scaling measured on pixel centers (i.e. pixel size (or step) = (x<sub>max</sub> - x<sub>min</sub>) / (num - 1)).
- **Left-Bottom Origin**: Coordinate origin is at the left-bottom corner (Y increases upwards).
- **Immutable Matrix Size**: Matrix dimensions are fixed after creation for performance.
- **Backing IList<T[]>**: Uses `IList<T[]>` for frame storage, allowing both in-memory arrays and MMF-backed virtual frames behind a unified interface.
- **Reactive Cache Synchronization**: Statistics (min/max) are synchronized via shared list references across shallow copies, ensuring data integrity without redundant calculations.
- **Transparent Virtual Access**: Frames may live in RAM, on disk (MMF), or anywhere else — the API never changes.

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

### Visualize in One Line (MxPlot.UI.Avalonia)

```csharp
using MxPlot.Core;
using MxPlot.UI.Avalonia.Views;

// Open a standalone MatrixPlotter window — works from console, WinForms, or WPF
MatrixPlotter.Create(data, title: "My Data").Show();
```

For **WinForms / WPF** host applications, initialize Avalonia once at startup and then call `MatrixPlotter.Create` from anywhere:

```csharp
// Program.cs (WinForms) — add Avalonia.Win32 + Avalonia.Skia NuGet packages to the host project
// ⚠️ These packages must match the Avalonia version used by MxPlot.UI.Avalonia (11.3.18).
//    A version mismatch causes a hard crash before Main() is reached.
AppBuilder.Configure<MxPlotHostApplication>()
    .UseWin32().UseSkia()
    .SetupWithoutStarting();
```

After that, MatrixPlotter.Create works from anywhere in your application:

```csharp
// Open a window with initial data:
var plotter = MatrixPlotter.Create(myData, title: "Result");
plotter.Show();

// To replace data in an existing window (e.g., live update loop),
// assign the new data on the Avalonia dispatcher thread:
await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
{
    plotter.MainView.MatrixData = newData;   // or: plotter.ViewModel.MatrixData = newData;
});

// ResumeSettingsEnabled = true carries over axis index and value-range settings
// when data is replaced, which is useful for continuous data acquisition.
plotter.ResumeSettingsEnabled = true;

// SaveAsAsync / DuplicateAsync are available for programmatic file operations:
await plotter.SaveAsAsync("output.ome.tif");
var clone = await plotter.DuplicateAsync(show: true);
```

> **Note on Avalonia versions**: `MxPlot.UI.Avalonia` currently targets **Avalonia 11.3.18**.
> Avalonia 11 and Avalonia 12 have significant breaking API changes.
> When adding `Avalonia.Win32` / `Avalonia.Skia` to a host project, always pin them to the same minor version (`11.3.x`).
> Avalonia 12 support is planned for a future release.

For **scripts, console tools, and notebook cells** — anything without a pre-existing UI loop — use `MxPlotScriptHost` instead:

```csharp
// app.cs — a .NET 10 file-based app (dotnet run app.cs)
#:package MxPlot@0.3.0

using MxPlot.Core;
using MxPlot.UI.Avalonia;

MxPlotScriptHost.Run(() =>
{
    var plotter = MxPlotScriptHost.Show(data, title: "My Data");
    // plotter and its properties are UI-thread affine; marshal through Invoke:
    var lutName = MxPlotScriptHost.Invoke(() => plotter.Lut?.Name);
});
// Run returns once every window opened by the script has closed.
```

For advanced usage of `MatrixPlotter`, see the **[MatrixPlotter Usage Guide](./docs/MatrixPlotter_Usage_Guide.md)**.


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
using MxPlot.Core.IO;
using MxPlot.Extensions.Tiff;  // For OME-TIFF

// --- Saving: Choose your format ---
var matrix = new MatrixData<float>(
        Scale2D.Centered(512,512,2,2), 
        Axis.Channel(3), 
        Axis.Time(10, 0, 1, "s")); // XY + 3 Channels + 10 Timepoints

// Native format (fast, compact, supports virtual MMF loading)
matrix.SaveAs("data.mxd", new MxBinaryFormat());

// OME-TIFF (compatible with Fiji/ImageJ and bio-imaging software)
matrix.SaveAs("result.ome.tif", new OmeTiffFormat());

// --- Virtual loading: open a 15 GB file without loading it into RAM ---
var format = new OmeTiffFormat { LoadingMode = LoadingMode.Virtual };
var big = MatrixData<ushort>.Load("big_stack.ome.tif", format);
// big.IsVirtual == true: frames are decoded from MMF only on access

// --- Loading: Dynamic and type-safe ---
IMatrixData data = MatrixDataSerializer.LoadDynamic("data.mxd");
Console.WriteLine($"Dimensions: {data.Dimensions}"); // e.g., "512x512, C:3, T:10"

if (data is MatrixData<float> floatData)
{
    float val = floatData.GetValueAt(0, 0);
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
var timeSlice = data.SliceAt(("Time", 10)); // 2D image from XYT

// Extract data along specific axis (creates new MatrixData with a single axis)
var zStackAtTime5 = data.ExtractAlong("Z", new[] { 0, 5 }); // Extract Z-stack (3D) at Time=5

// Select a specific index along an axis (reduces dimension by 1)
var snapShot = data.SelectBy("Z", 2); // Extract hyperstack at Z=2 (N-1D)

// Map: Apply function to each pixel across all frames
var normalized = matrix.Map<double, double>((value, x, y, frame) => value / 255.0);

// Reduce: Aggregate across frame axis
var averaged = timeSeries.Reduce((x, y, values) =>
{
    double sum = 0;
    foreach (var v in values) sum += v;
    return sum / values.Length;
});

// === VolumeAccessor: 3D volume operations and projections ===

var volume = data.AsVolume("Z");

// Create orthogonal projections
var projMaxZ = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum); // MIP along Z
var projMaxX = volume.CreateProjection(ViewFrom.X, ProjectionMode.Maximum); // YZ plane
var projMaxY = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Maximum); // XZ plane

// Simultaneous XZ + YZ extraction in one memory pass (zero-allocation buffer reuse)
var (xzPlane, yzPlane) = data.Apply(new SliceOrthogonalOperation(X: 128, Y: 128));

// Restack volume for different viewing axes
var restackedX = volume.Restack(ViewFrom.X);

// Direct voxel access (no bounds checking for performance)
double voxelValue = volume[x: 10, y: 20, z: 5];

// === LineProfileExtractor: Extract intensity profiles along arbitrary lines ===

var profile = matrix.Apply(new LineProfileOperation(
    startX: 10.5, startY: 20.3,
    endX: 80.7, endY: 90.2,
    numPoints: 100)); // Bilinear interpolation included

// === SpatialFilterOperation: Apply spatial filters ===

// Median filter 3×3
var medianFiltered = matrix.Apply(new SpatialFilterOperation(new MedianKernel(radius: 1)));

// Gaussian filter (radius=2, sigma=1.5)
var gaussFiltered = matrix.Apply(new SpatialFilterOperation(new GaussianKernel(radius: 2, sigma: 1.5)));
```

## 📖 More Detailed Information

For detailed guides and technical references, see the **[Documentation Index](./docs/README.md)**.


## 📊 Version History

**v0.3.0** (Composite workflow completion, ColorCoded rendering, live-link refactor, and stability/performance improvements)
- 🌈 **ColorCoded Rendering (New)**: New live depth/time colour-coded projection mode — pick `Color (Max)`/`Color (Min)` in the orthogonal-view projection selector for a Z (or any frozen-axis) projection, in a linked child window.
- 🎨 **Composite Rendering (Feature-complete)**: Promoted Composite mode from beta-level base to full workflow support with RGB auto-composite, grayscale conversion, and fallback dialog flow.
- 🧩 **Composite + Extract Integration**: `Extract Frame` / `This Frame Only` now work consistently in Composite mode, including orthogonal views and full-channel extraction.
- 🔗 **Linked View Refactor**: Replaced `LinkedSource` with a unified `LinkedView` follower model; live-derived windows now preserve their own display settings.
- 🔌 **External Control API**: New Facade properties on `MatrixPlotter` (`Lut`, `IsInvertedColor`, `LutDepth`, `RangeMode`, `IsFixedRange`, `FixedRange`); direct `MainView` assignments now up-sync to the ViewModel and UI chrome. `RangeMode` also fixes a bug where switching the value range to All or ROI silently landed in Fixed.
- 🖥️ **`MxPlotScriptHost`**: Drive `MatrixPlotter` from .NET 10 file-based apps, console tools, and notebook cells — `Run`/`Start` manage the Avalonia message loop so the script itself always runs off the UI thread, on Windows, macOS, and Linux.
- ⚡ **Live Update Pipeline Optimization**: Reduced projection/live update overhead by avoiding full re-initialization and committing lightweight content updates.
- 🎬 **MP4 Export**: New `Mp4Exporter` (H.264 via external `ffmpeg`) for macOS/QuickTime-compatible video export, alongside AVI — both now share a reusable `VideoExporterBase` for third-party video-format plugins.
- 🧊 **Restack Context Menu**: `Restack along X/Y` on the orthogonal side views reconstructs the volume as a new stack viewed from another axis, opened in its own window — Virtual output writes one whole frame at a time from a RAM-bounded band instead of buffering the whole result in memory or scattering individual rows across the output file.
- 🐛 **Sync and Stability Fixes**: Multiple fixes across Composite/Histogram sync, orthogonal cache/slice behavior, ROI copy orientation, and replace-data interactions.
- ⚠️ **Breaking Changes**: `MatrixPlotter.LinkedSource` / `LinkedSourceExcludedAxes` and `MatrixPlotterViewModel.ActiveFrame` removed; `BitmapWriter` deprecated in favour of `LutBitmapWriter`. See [CHANGELOG.md](./CHANGELOG.md) for the full list.

**v0.2.0** (Export extensions, Complex type support, and UI/UX enhancements)
- 🎬 **AVI Export Plugin**: New package **MxPlot.UI.Avalonia.Video** added with `IRenderExportPlugin` / `IRenderHost` abstraction. Supports main view and orthogonal view video export with axis selection, FPS control, and overlay rendering.
- 📐 **Extract Dimension Dialog**: New `Extract Along` / `Extract At` UI for extracting data along or at specific axis values. Unified title and history formatting.
- 🔢 **Complex Type Support**: Full support for `System.Numerics.Complex` with `ValueMode` (Real/Imaginary/Magnitude/Phase) display switching in UI and core.
- 📊 **Histogram Analysis**: New `HistogramPlotControl` with live bin calculation, LUT mode integration, and interactive overlay support.
- 🖼️ **XY Projection Enhancements**: Introduced `LinkedSource` delegation model — overlay analysis (line profile, stats, ROI) now updates on parent frame change. AVI export supported via parent data delegation.
- ✂️ **Crop Sync Overhaul**: Fixed crop synchronization bugs across volume axes. Added reentrancy guards and unified dirty-flag/secondary-window management.
- 💾 **Configuration Persistence**: Settings now saved to `config` folder. Data reuse logic improves memory efficiency on viewer refresh.
- 🧮 **Core Optimizations**: `GetGlobalValueRange` for multi-axis value range queries. `AsMemory<T>` optimizations for generic types. `forceInMemory` option added to `Duplicate()` / `Clone()`.
- 🎨 **Composite Rendering Base** *(beta)*: Core rendering logic implemented (`CompositeBitmapWriter`). UI layer incomplete — full composite UI planned for future release.
- 🐛 **Bug Fixes**: CSV `flipY` parameter ignored — now fixed. Profile plotter auto-axis setting not applied — fixed. Render thread safety improved.
- 🔧 **Dependency Update**: Avalonia updated to **11.3.18** (from 11.3.14).

**v0.1.x and earlier**
- ✂️ **Crop / Substack Improvements**: Added hyperstack crop modes with ROI-aware behavior.
- 💾 **Session and File Operations**: Introduced unsaved-change tracking and programmatic `SaveAsAsync` / `DuplicateAsync` operations.
- 🔁 **Live Loop Usability**: Added `ResumeSettingsEnabled` to preserve view settings during continuous updates.

See [CHANGELOG](./CHANGELOG.md) for full history.

---
> **⚠️ Note**
> This library is actively developed and maintained for my own research purposes,
> and shared in the hope that it serves as a useful foundation for others.
> Please be aware that testing is not yet exhaustive — use in production at your own risk.
>
> *Maintained by YK ([@u1bx0](https://github.com/u1bx0))*
