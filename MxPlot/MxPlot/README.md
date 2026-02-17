
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
- **MxPlot.Extensions.Hdf5**: HDF5 support via *PureHDF*.
- **MxPlot.Extensions.Images**: Image loading (PNG, JPEG, BMP, TIFF) via *SkiaSharp*.
- **MxPlot.Extensions.Fft**: Thin 2D FFT wrapper using *Math Kernel Library*  via *MathNet.Numerics*. (Preliminary implementation)


## 🖼️ Visualization Layer (In Development)
Separated package for UI controls and rendering.
- **MxPlot.Wpf / MxPlot.WinForms**: Native UI controls for rich data rendering.

---

## 📊 Version History
For the complete changelog and version history, please visit the Releases Page on GitHub.

👉 [GitHub Repository: u1bx0/MxPlot](https://github.com/u1bx0/MxPlot)

