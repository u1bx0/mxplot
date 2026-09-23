# MxPlot Documentation

**Last Updated**: 2026-08-20

- Detailed guides and technical references for the MxPlot library stack.
- Content may be updated as the library evolves

> For API overview and quick start, see the main [README.md](../README.md) at the solution level.


---

## MxPlot.Core — Data Model

- **[MatrixData Operations Guide](./MatrixData_Operations_Guide.md)** ([日本語](./MatrixData_Operations_Guide_ja.md))  
  Comprehensive reference for MatrixData operations: transformations, slicing, projections, and pipelines.

- **[DimensionStructure & Memory Layout Guide](./DimensionStructure_MemoryLayout_Guide.md)** ([日本語](./DimensionStructure_MemoryLayout_Guide_ja.md))  
  Technical deep-dive into multi-axis data structures, memory layouts, and stride calculations.

- **[MatrixData Multi-Dimensional Access Guide](./MatrixData_MultiDimensional_Access_Guide.md)**  
  Tips for handling multi-dimensional data efficiently for optimal performance.

- **[MatrixData Frame Sharing Model](./MatrixData_Frame_Sharing_Model.md)**  
  Explains how MatrixData manages min/max values per frame and shares them across instances (ValueRange / Invalidate design).

- **[VirtualFrames Guide](./VirtualFrames_Guide.md)**  
  Architecture overview of on-demand Virtual storage: the `VirtualFrames<T>` skeleton, MMF backends (`StrippedMmfFrames`, `TiledMmfFrames`, `WritableStrippedMmfFrames`), Lazy decode for compressed TIFF (`TiffDecodedFrames`), cache sizing and prefetch strategies, the `AsVirtualBuilder` creation path, `LoadVirtual`, the SaveAs fast-path, Clone behavior, `VirtualPolicy` thresholds, and diagnostics (`GetDiagnosticCacheableList`). Includes known limitations and planned work (`IVesselCreatable`).

- **[MatrixData Method Call Map](./MatrixData_MethodCallMap.md)** ([日本語](./MatrixData_MethodCallMap_ja.md))  
  Comprehensive reference mapping the call relationships, dependencies, and zero-copy strategies of all `MatrixData<T>` operation methods across `MxPlot.Core` and `MxPlot.Core.Processing`.

- **[VolumeAccessor Guide](./VolumeAccessor_Guide.md)** ([日本語](./VolumeAccessor_Guide_ja.md))  
  3D volume operations: MIP/MinIP/AIP projections, orthogonal views, and performance optimization.

- **[Custom Value Types Guide](./CustomValueTypes_Guide.md)**  
  Working with custom unmanaged structs beyond primitive types. **Status: Preliminary**

---

## MxPlot.UI.Avalonia — UI Components

- **[MxPlot.UI.Avalonia Overview](./MxPlotUIAvalonia_Overview.md)**  
  Overview of the Avalonia UI layer: `MatrixPlotter`, `MxView`, `MxPlotHostApplication`, and the plugin / action model.

- **[MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md)**  
  How to open a `MatrixPlotter` window and drive it from code: host setup for Avalonia / WinForms / WPF / console apps, refreshing and replacing data, the three external-control entry points (Facade properties, ViewModel, `MainView`), frame and orthogonal-view control, linked plotters, events, and export.

- **[MatrixPlotter Composite Rendering Guide](./MatrixPlotter_Composite_Guide.md)**  
  Multi-channel composite display: entry points, `BlendRecipe` per-channel settings, blend modes, Global vs Channel-wise value ranges, RGB auto-composite and grayscale conversion, and how Composite interacts with Extract / projections / persistence.

- **[MxView Coordinate Systems Guide](./MxView_CoordinateSystems_Guide.md)**  
  Explains the three coordinate layers used by `MxView` (Screen, World/bitmap-pixel, Data/physical), their axis conventions, and how to convert between them using `ScreenToData` / `DataToScreen`. Covers overlay coordinate conventions and `ViewTransform` handling.

- **[MatrixPlotter Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md)** ([日本語](./MatrixPlotter_MetadataFormat_Guide_ja.md))  
  Metadata key conventions used by `MatrixPlotter` for persisting view settings (`mxplot.vr.*`, etc.).

- **[WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md)** ([日本語](./MatrixPlotter_NonAvalonia_Integration_Guide_ja.md))  
  Step-by-step guide for hosting `MatrixPlotter` inside a WinForms or WPF application. Covers `AppBuilder` setup, data refresh API, thread safety, and high-frequency update patterns.

- **Scripting with `MxPlotScriptHost`** — see [MatrixPlotter Basic Usage Guide § Scripting with MxPlotScriptHost](./MatrixPlotter_Usage_Guide.md)  
  Open `MatrixPlotter` windows from .NET 10 file-based apps (`dotnet run app.cs`), console tools, or notebook cells — for UI-less hosts that have no message loop of their own, as opposed to the WinForms/WPF guide above.

---

## MxPlot.Extensions — Optional Add-ons

### MxPlot.Extensions.Fft

- **[FFT2D ShiftOption Operation](./Extensions_Fft_Shift_Operation.md)**  
  Detailed reference for `ShiftOption` behavior (`None`, `Centered`, `BothCentered`) and the underlying circular-swap mechanics in `Fft2D` / `InverseFft2D`.  
  For a broader overview of the FFT extension (pipeline API, usage examples), see the `MxPlot.Extensions.Fft` package README.

---

## Extension Development

- **[MxPlot Extension Development Guide](./MxPlot_Extensions_Guide.md)** ([日本語](./MxPlot_Extensions_Guide_ja.md))  
  How to extend MxPlot with external DLLs: file format readers/writers (`IMatrixDataReader`, `IVirtualLoadable`), MatrixPlotter plugins (`IMatrixPlotterPlugin`), and MxPlot.App plugins (`IMxPlotAppPlugin`). Covers progress reporting, cancellation, virtual loading implementation, and deployment conventions.

---



