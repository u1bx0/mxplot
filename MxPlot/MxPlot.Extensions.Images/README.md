# MxPlot.Extensions.Images

**High-Performance Image Loading for MatrixData (Powered by SkiaSharp)**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.Extensions.Images?style=flat-square)](https://www.nuget.org/packages/MxPlot.Extensions.Images)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)

## 📦 About this Package
This package provides a simple image loader for `MatrixData<T>`, utilizing **SkiaSharp** as the decoding engine. 
It enables seamless conversion of standard image formats (PNG, JPEG, BMP, TIFF, etc.) into mathematical matrices, supporting various numeric types including `double`, `float`, and even `System.Numerics.Complex`.

It is designed for scientific analysis, signal processing, and visualization workflows within the MxPlot ecosystem.

## 🔍 Key Features
- **Broad Format Support**: Powered by SkiaSharp, supporting PNG, JPEG, BMP, WEBP, and TIFF.
- **Multi-Channel Decomposition**: Easily load images as Grayscale or decomposed RGB channels.
- **Flexible Data Types**: Direct loading into `byte`, `int`, `float`, or `double` matrices.
- **Complex Number Integration**: Specialized support for `Complex` type (pixel values mapped to the real part), ideal for immediate FFT processing.
- **Normalization Control**: Built-in `NormalizationDivisor` to scale pixel values (e.g., 0-255 to 0.0-1.0) during the loading process.
- **Axis Correction**: Includes a `flipY` option (default: true) to align image coordinates with mathematical Cartesian coordinates (origin at bottom-left).

## 🛠️ Usage Example

The `BitmapImageFormat` class integrates directly with the `MatrixData<T>.Load` static method.

### Loading as Normalized RGB (Double)
```csharp
using MxPlot;
using MxPlot.Extensions.Images;

// Load an image, decompose to RGB, and normalize to 0.0 - 1.0
var format = new BitmapImageFormat 
{ 
    Mode = BitmapReadMode.RGBDecomposed, 
    NormalizationDivisor = 255.0 
};

var md = MatrixData<double>.Load("photo.png", format);

// Access channels: md.GetArray(0) is Red, (1) is Green, (2) is Blue
```
### Loading for Frequency Analysis (Complex)
```csharp
// Pixel values (0-255) are stored in the Real part of the Complex numbers
var format = new BitmapImageFormat { Mode = BitmapReadMode.GrayScale };
var complexMatrix = MatrixData<Complex>.Load("signal.bmp", format);

```

## ⚙️ Dependencies
SkiaSharp: Used for cross-platform image decoding.

MxPlot.Core: Provides the base MatrixData<T> structure and IMatrixDataReader interface.

## 🚀 Origin Semantics
By default, this loader uses flipY = true. This ensures that the data index [0] corresponds to the bottom-left of the image, making it consistent with standard mathematical plotting and coordinate systems.

## 📊 Version History
For the complete changelog and version history, please visit the Releases Page on GitHub.

👉 [GitHub Repository: u1bx0/MxPlot](https://github.com/u1bx0/MxPlot)

