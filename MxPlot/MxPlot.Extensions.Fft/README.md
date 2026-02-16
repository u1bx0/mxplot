# MxPlot.Extensions.Fft

**Preliminary 2D FFT Support for MatrixData (Powered by MathNet.Numerics)**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.Core?style=flat-square)](https://www.nuget.org/packages/MxPlot.Extensions.Fft)
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


👉 **[GitHub Repository: u1bx0/MxPlot](https://github.com/u1bx0/mxplot)**

## 📊 Version History
For the complete changelog and version history, please visit the [Releases Page](https://github.com/u1bx0/mxplot/releases) on GitHub.