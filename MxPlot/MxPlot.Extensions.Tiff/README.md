# MxPlot.Extensions.Tiff

**OME-TIFF and ImageJ-compatible hyperstack I/O support for MxPlot**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.Extensions.Tiff?style=flat-square)](https://www.nuget.org/packages/MxPlot.Extensions.Tiff)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**MxPlot.Extensions.TIFF** provides advanced TIFF read/write capabilities for `MatrixData<T>`. It is designed to handle 5D bio-imaging data (XY + Channel, Z, Time) with full metadata preservation, bridging MxPlot with the scientific imaging ecosystem (ImageJ/Fiji, Bio-Formats).

## Features

- **5D Hyperstack Support**: Natively handles multidimensional data (Channel, Z-Slice, Time-Frame).
- **Dual Export Strategies**:
  - **OME-TIFF**: Embeds OME-XML metadata (physical pixel size, time intervals, UUIDs) for robust data exchange.
  - **ImageJ TIFF**: Writes ImageJ-specific tags for immediate, lightweight drag-and-drop compatibility with Fiji.
- **Physical Space Awareness**: Preserves physical units (e.g., µm) and automatically handles coordinate system conversion (Bottom-Left vs Top-Left).
- **Flexible Architecture**: Implements MxPlot's `IMatrixDataWriter` / `IMatrixDataReader` interfaces.

## Components

Based on the internal file structure:

- **`OmeTiffFormat`**: The core writer strategy for generating OME-compliant TIFFs.
- **`ImageJTiffFormat`**: The writer strategy optimized for ImageJ compatibility.
- **`OmeTiffHandler`** / **`ImageJTiffHandler`**: High-level I/O handlers.
- **`ImageJMetadata`**: Helper class for managing ImageJ specific TIFF tags.

## Quick Usage

This library integrates with the `MatrixData.Save` method via the Strategy Pattern.

### Saving Data

```csharp
using MxPlot.Core;
using MxPlot.Extensions.Tiff;

// Assume 'matrix' is a MatrixData<float> with 5D dimensions (e.g., C=3, Z=24, T=100)
// and physical scaling (e.g., 0.1um/pixel).

// 1. Save as OME-TIFF (Recommended for archival/analysis)
// Preserves full physical metadata and dimension order.
matrix.Save("simulation_data.ome.tif", new OmeTiffFormat());

// 2. Save as ImageJ Hyperstack (Recommended for quick viewing)
// Lightweight format specifically tuned for Fiji.
matrix.Save("quick_view.tif", new ImageJTiffFormat());

// 3. Save with custom options (e.g., disable Y-flip)
var options = new OmeTiffFormat.Options
{
	FlipY = false // Keep original coordinate system
};
matrix.Save("no_flip.ome.tif", new OmeTiffFormat(options));
```
### Loading Data
```csharp
// Automatically detects OME-TIFF or Standard TIFF and restores axes information.
IMatrixData loadedData = MatrixData.Load("input.ome.tif");
```
## Technical Notes
- **Supported Types**: `byte`, `sbyte`, `short`, `ushort`, `int`, `float`, `double`.
- **Coordinates**: The library automatically handles the vertical flip (Y-axis) required to map MxPlot's mathematical Cartesian coordinates to the raster scan order of TIFF images. This behavior can be customized via the `FlipY` option if needed. See the API documentation for `OmeTiffFormat` and `ImageJTiffFormat` for details.
- **Dependencies**: Built on top of `BitMiracle.LibTiff.NET`.

License
MIT License