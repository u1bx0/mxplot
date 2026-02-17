# MxPlot.Extensions.Fft

**Preliminary 2D FFT Support for MatrixData (Powered by MathNet.Numerics)**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.Extensions.Fft?style=flat-square)](https://www.nuget.org/packages/MxPlot.Extensions.Fft)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)


## 📦 About this Package
This package provides a thin wrapper around the 2D FFT functionality of **MathNet.Numerics**, enabling seamless integration with `MatrixData<T>`. 
It is designed to serve as a useful extension for users who require basic FFT capabilities within the MxPlot ecosystem.
**Although marked as preliminary, the design and API are already stable; remaining work may focus on performance tuning and potential bug fixes.**

## 🔍 Key Features
- Uses MathNet.Numerics with native MKL acceleration on x64 platforms for maximum performance.
- Automatically falls back to a managed 2D FFT implementation on non‑MKL environments (ARM, macOS, Linux).
- Implements a mathematically consistent `ShiftOption` model with correct handling of odd-sized transforms.
- Designed to preserve spatial origin semantics in `MatrixData<T>` during FFT/IFFT pipelines.
- Guarantees identical ShiftOption semantics across MKL and managed backends.


## 🛠️ Math Kernel Library (MKL) Support and Fallback

When running on x64 Windows and you want the maximum FFT performance, you can enable Intel's
Math Kernel Library (MKL) backend for MathNet.Numerics by installing the native runtime package:

```bash
dotnet add package MathNet.Numerics.MKL.Win-x64
```

This NuGet package provides the native MKL binaries for x64 Windows and lets MathNet.Numerics
use the optimized MKL FFT implementations. Using MKL typically yields significant speedups
for large 2D FFTs on x64 desktop/server CPUs.

If MKL is not installed, or when running on non-x64 architectures (ARM, macOS, Linux, etc.),
the extension automatically falls back to MathNet.Numerics' managed FFT implementation. In
that case the repository's own 2D FFT helper (managed implementation) is used transparently so
that `MatrixData<T>` users do not need to change their code.

Note: the MKL package is optional — the extension works out-of-the-box with the managed backend.


👉 **[GitHub Repository: u1bx0/MxPlot](https://github.com/u1bx0/mxplot)**

## 📊 Version History
For the complete changelog and version history, please visit the [Releases Page](https://github.com/u1bx0/mxplot/releases) on GitHub.