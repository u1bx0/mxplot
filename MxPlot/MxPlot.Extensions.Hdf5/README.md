# MxPlot.Extensions.Hdf5

**HDF5 Export for MatrixData (Powered by PureHDF v2)**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.Extensions.Hdf5?style=flat-square)](https://www.nuget.org/packages/MxPlot.Extensions.Hdf5)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**MxPlot.Extensions.Hdf5** is a specialized extension package that enables seamless HDF5 (`.h5`) export for the `MatrixData<T>` ecosystem. Built on top of **PureHDF v2**, it offers a fully managed, dependency-free solution (no native `hdf5.dll` required) with a strong focus on scientific software interoperability.
    

## ✨ Key Features

- **✅ Pure .NET Solution**: Built on PureHDF v2. No native dependencies or interop headaches.
- **✅ HDF5 Image Spec 1.2 Compliant**: Files are automatically recognized as images by **HDFView**, **Fiji (ImageJ)**, and **HDF Compass**.
- **✅ Multi-Dimensional Support**: Auto-handles and sorts Channel, Time, Z, and custom dimensions.
- **✅ Tiling Support**: Preserves `FovAxis` tiling metadata with GlobalPoint arrays.
- **✅ Coordinate System Control**: Options for Y-Axis flipping (MxPlot bottom-left ⇔ Image top-left).
- **✅ Complete Metadata**: Preserves physical units (`µm`, `s`), scales, and data types.

---

## 🚀 Quick Start

### Basic Export

Exporting data is as simple as a single line of code.

```CSharp
using MxPlot.Core;
using MxPlot.Extensions.Hdf5;

// 1. Prepare Data
var matrix = new MatrixData<double>(512, 512);
matrix.SetXYScale(-10, 10, -10, 10);
// ... fill data ...

// 2. Export using the Format class (Recommended)
// This creates an HDF5 1.2 compliant image.
matrix.Save("output.h5", new Hdf5Format());

// 3. Export with options
var options = new Hdf5Format
{
    GroupName = "experiment_01", // Root group name
    FlipY = true                 // Standardize coordinate system
};
matrix.Save("output_custom.h5", options);
```


## 🖼️ Interoperability & Image Spec

This library automatically attaches attributes compliant with **HDF5 Image Specification 1.2**. This ensures that your data isn't just a "bag of numbers" but a semantic image.


### Attribute Structure
The exporter attaches the following standard metadata to the parent group:
* **Image Spec**: `CLASS`, `IMAGE_VERSION`, `DISPLAY_ORIGIN`, `IMAGE_SUBCLASS`
* **Physical Scale**: `element_size_um`, `UNIT_X`, `UNIT_Y`
* **Data Range**: `IMAGE_MINMAXRANGE` (for contrast scaling)

> **Note on Attributes**: Due to current API constraints in PureHDF v2, attributes are attached to the **Group** level rather than the Dataset level. Most HDF5 viewers support this convention seamlessly.

---

## 🧩 Advanced Features

### Automatic Dimension Reordering

When exporting multi-dimensional data, `Hdf5Handler` automatically sorts dimensions to match standard C-style/HDF5 memory layout conventions:

**Priority Order (Outer → Inner):**
`Channel (0)` > `Z (90)` > `Time (80)` > `Custom (50)` > `FOV (0)`

```CSharp
// Example: Creating a [Channel, Time, Y, X] dataset
var matrix = new MatrixData<double>(256, 256, 30); // 3 Ch * 10 Time

// Define dimensions in any order
matrix.DefineDimensions(
    Axis.Time(10, 0, 10, "s"), 
    Axis.Channel(3)
);

// The exporter will automatically reorder this to:
// [Channel, Time, Y, X] (Shape: 3, 10, 256, 256)
Hdf5Handler.Save("multidim.h5", matrix);
```

### Tiling Support (FovAxis)

Full support for `FovAxis` allows you to save tiled microscopy data or multi-view datasets. Global coordinates for each tile are preserved as array attributes.

```CSharp
// Setup a 3x3 Grid
var fovAxis = new FovAxis(3, 3);
// ... setup origins ...
matrix.DefineDimensions(fovAxis);

Hdf5Handler.Save("tiled_scan.h5", matrix);

```

---

## ⚙️ Technical Details

### Why PureHDF v2?
We chose **PureHDF v2** over legacy C# HDF5 wrappers for several reasons:

| Feature | Legacy Wrappers (HDF.PInvoke) | MxPlot (PureHDF v2) |
| :--- | :--- | :--- |
| **Dependencies** | Requires `hdf5.dll` (Native) | **Zero (Pure C#)** |
| **Platform** | Often Windows-centric | **Any Platform (Linux/Mac/Win)** |
| **API Style** | Low-level C-style IDs | **Modern .NET Object API** |
| **Maintainability** | Complex | **Simple & Robust** |

### Limitations (v1.0)
* **Export Only**: Import functionality is planned for v2.0 (awaiting PureHDF reading API updates).
* **No Compression**: Currently saves raw binary for maximum speed. Compression is planned.

---

### 🛡️ License
This project is licensed under the MIT License.