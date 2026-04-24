
# MxPlot

**Multi-Axis Matrix Visualization Library**

[![NuGet Version](https://img.shields.io/nuget/v/MxPlot?style=flat-square&color=blue)](https://www.nuget.org/packages/MxPlot)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**MxPlot** is a high-performance multi-axis matrix visualization library for scientific and engineering applications in .NET. This package serves as a **metapackage**, providing a unified entry point for the MxPlot ecosystem.

> [!TIP]
> If you only need the core engine without extra dependencies, see [**MxPlot.Core**](https://www.nuget.org/packages/MxPlot.Core) on NuGet.

## 📦 Package Structure

MxPlot consists of a modular core, a cross-platform UI layer, and specialized extensions:

### Core Package
- **MxPlot.Core**: The heart of the library. Contains the multi-axis data container (`MatrixData<T>`), the processing engine, and built-in I/O formats (`.mxd`, CSV, FITS). (Dependency-free)

### Visualization Layer
- **MxPlot.UI.Avalonia**: Cross-platform visualization built on [Avalonia UI](https://avaloniaui.net/) 11.3. Includes `MatrixPlotter` (full-featured standalone window), `MxView` (pan/zoom image surface), and `MxPlotHostApplication` for embedding inside WinForms or WPF apps. Runs on Windows, macOS, and Linux.

### Available Extensions
- **MxPlot.Extensions.Tiff**: TIFF I/O (OME-TIFF, ImageJ hyperstack) via *LibTiff.NET*.
- **MxPlot.Extensions.Hdf5**: HDF5 support via *PureHDF*.
- **MxPlot.Extensions.Images**: Image loading (PNG, JPEG, BMP, TIFF) via *SkiaSharp*.
- **MxPlot.Extensions.Fft**: Thin 2D FFT wrapper via *MathNet.Numerics* (pure managed). Optionally accelerated by MKL when `MathNet.Numerics.MKL.Win-x64` is installed. (Preliminary implementation)

---

## 📊 Version History
For the complete changelog and version history, please visit the Releases Page on GitHub.

👉 [GitHub Repository: u1bx0/MxPlot](https://github.com/u1bx0/MxPlot)
