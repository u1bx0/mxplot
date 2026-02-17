# MxPlot

**Multi-Axis Matrix Visualization Library**

[![NuGet Version](https://img.shields.io/nuget/v/MxPlot?style=flat-square&color=blue)](https://www.nuget.org/packages/MxPlot)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**MxPlot** is a high-performance multi-axis matrix visualization library for scientific and engineering applications in .NET. This package serves as a **metapackage**, providing a unified entry point for the MxPlot ecosystem.

> [!TIP]
> If you only need the core engine without extra dependencies, see [**MxPlot.Core**](https://www.nuget.org/packages/MxPlot.Core) on NuGet.

## 📦 Package Structure

MxPlot consists of a modular core and specialized extensions:

### Core Package
- **MxPlot.Core**: The heart of the library. Contains the multi-axis data container (`MatrixData<T>`) and the processing engine. (Dependency-free)

### Available Extensions
- **MxPlot.Extensions.Tiff**: TIFF I/O (OME-TIFF, ImageJ) via *LibTiff.NET*.
- **MxPlot.Extensions.HDF5**: HDF5 support via *PureHDF*.
- **MxPlot.Extensions.FFT**: Signal processing via *Math.NET Numerics*. (Coming Soon)

## 🖼️ Visualization Layer (In Development)
Separated package for UI controls and rendering.
- **MxPlot.Wpf / MxPlot.WinForms**: Native UI controls for rich data rendering.

---

## 📊 Version History

### **v0.0.3-alpha** (Current)
- 🏗️ **Metapackage Architecture**: Reorganized as a metapackage that bundles `MxPlot.Core` and common extensions.
- 🔄 **Refined API**: Renamed `XAt`/`YAt` to `XValue`/`YValue` for naming consistency.
- ⚡ **Performance**: Optimized `Scale2D` (now `readonly struct`) and improved 2D matrix manipulation overhead.
- ✨ **New Features**: Added `GetAxisValues`/`GetAxisIndices` with C# deconstruction support and `IMatrixData` facade methods.

### **v0.0.2-alpha**
- ✨ **Core Features**: Introduced `VolumeAccessor<T>` for high-performance 3D operations.
- ⚡ **Optimization**: `VolumeOperator` with tiled memory access (up to 3.4x speedup).
- 🎯 **Enhanced Extraction**: Improved `DimensionalOperator.ExtractAlong()` for multi-axis data.

### **v0.0.1-alpha**
- Initial package name reservation.

