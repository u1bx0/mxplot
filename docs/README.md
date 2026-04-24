# MxPlot Documentation

**Last Updated**: 2026-04-24

Detailed guides and technical references for the MxPlot library stack.

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
  Architecture overview of MMF-backed Virtual storage: backend classes (`VirtualStrippedFrames`, `WritableVirtualStrippedFrames`), the `AsVirtualBuilder` creation path, `LoadVirtual`, the SaveAs fast-path, Clone behavior, and `VirtualPolicy` thresholds. Includes known limitations and planned work (`IVesselCreatable`).

- **[MatrixData Method Call Map](./MatrixData_MethodCallMap.md)** ([日本語](./MatrixData_MethodCallMap_ja.md))  
  Comprehensive reference mapping the call relationships, dependencies, and zero-copy strategies of all `MatrixData<T>` operation methods across `MxPlot.Core` and `MxPlot.Core.Processing`.

- **[VolumeAccessor Guide](./VolumeAccessor_Guide.md)** ([日本語](./VolumeAccessor_Guide_ja.md))  
  3D volume operations: MIP/MinIP/AIP projections, orthogonal views, and performance optimization.

- **[Custom Value Types Guide](./CustomValueTypes_Guide.md)**  
  Working with custom unmanaged structs beyond primitive types. **Status: Preliminary**

---

## MxPlot.UI.Avalonia — UI Components

- **[MxPlot.UI.Avalonia Overview](./MxPlotUIAvalonia_Overview.md)**  
  Overview of the Avalonia UI layer: `MatrixPlotter`, `MxView`, `MxPlotHost`, and the plugin / action model.

- **[MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md)**  
  How to open a `MatrixPlotter` window, refresh data, link plotters, and integrate with non-Avalonia hosts.

- **[MatrixPlotter Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md)** ([日本語](./MatrixPlotter_MetadataFormat_Guide_ja.md))  
  Metadata key conventions used by `MatrixPlotter` for persisting view settings (`mxplot.vr.*`, etc.).

- **[WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md)** ([日本語](./MatrixPlotter_NonAvalonia_Integration_Guide_ja.md))  
  Step-by-step guide for hosting `MatrixPlotter` inside a WinForms or WPF application. Covers `AppBuilder` setup, data refresh API, thread safety, and high-frequency update patterns.

---

## Notes

- All guides include tested code examples and real-world use cases
- Performance reports and benchmarks are in separate files (not for distribution)
- Content may be updated as the library evolves

---

## Extension Development

- **[MxPlot Extension Development Guide](./BluePaper_MxPlot_Extensions_Guide.md)** ([日本語](./BluePaper_MxPlot_Extensions_Guide_ja.md))  
  How to extend MxPlot with external DLLs: file format readers/writers (`IMatrixDataReader`, `IVirtualLoadable`), MatrixPlotter plugins (`IMatrixPlotterPlugin`), and MxPlot.App plugins (`IMxPlotPlugin`). Covers progress reporting, cancellation, virtual loading implementation, and deployment conventions.

---



