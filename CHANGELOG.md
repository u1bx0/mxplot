## 📊 Version History

**v0.2.0** (Export extensions, Complex type support, and UI/UX enhancements)
- 🎬 **AVI Export Plugin**: New package **MxPlot.UI.Avalonia.Video** added with `IRenderExportPlugin` / `IRenderHost` abstraction. Supports main view and orthogonal view video export with axis selection, FPS control, and overlay rendering.
- 📐 **Extract Dimension Dialog**: New `Extract Along` / `Extract At` UI for extracting data along or at specific axis values. Unified title and history formatting.
- 🔢 **Complex Type Support**: Full support for `System.Numerics.Complex` with `ValueMode` (Real/Imaginary/Magnitude/Phase) display switching in UI and core.
- 📊 **Histogram Analysis**: New `HistogramPlotControl` with live bin calculation, LUT mode integration, and interactive overlay support.
- 🖼️ **XY Projection Enhancements**: Introduced `LinkedSource` delegation model — overlay analysis (line profile, stats, ROI) now updates on parent frame change. AVI export supported via parent data delegation.
- ✂️ **Crop Sync Overhaul**: Fixed crop synchronization bugs across volume axes. Added reentrancy guards and unified dirty-flag/secondary-window management.
- 💾 **Configuration Persistence**: Settings now saved to `config` folder. Data reuse logic improves memory efficiency on viewer refresh.
- 🧮 **Core Optimizations**: `GetGlobalValueRange` for multi-axis value range queries. `AsMemory<T>` optimizations for generic types. `forceInMemory` option added to `Duplicate()` / `Clone()`.
- 🎨 **Composite Rendering Base** *(beta)*: Core rendering logic implemented (`CompositeBitmapWriter`). UI layer incomplete — full composite UI planned for future release.
- 🐛 **Bug Fixes**: CSV `flipY` parameter ignored — now fixed. Profile plotter auto-axis setting not applied — fixed. Render thread safety improved.
- 🔧 **Dependency Update**: Avalonia updated to **11.3.18** (from 11.3.14).


**v0.1.2** (Crop enhancements, file session management, and bug fixes)
- ✂️ **Substack / 3D Crop**: New crop modes for hyperstacks — extract a Z-range substack or crop a full 3D volume region, with ROI synchronization across linked windows.
- 📤 **Extract Frame**: Context menu action on main and orthogonal views to extract the current frame as a new independent window.
- 💾 **File Session Management**: Unsaved-change tracking via `DirtyFlags` (Lut/Vr/Scale/Overlay/Data), close confirmation dialog, and app-exit confirmation. `SaveAsAsync` and `DuplicateAsync` public APIs added for programmatic control.
- 🔁 **View Settings Resume**: `ResumeSettingsEnabled` property on `MatrixPlotter` preserves axis indices and value-range settings when data is replaced — useful for live acquisition loops.
- 🐛 **FrameIndex Sync Fix**: Fixed `FrameIndex` not synchronizing to `ActiveIndex` when `MatrixData` is replaced in `MxView`.
- 🐛 **Sync Range Fix**: Fixed value-range sync not forcing Fixed mode on peer windows when a fixed range is changed.
- 🪟 **Window Layout**: Dashboard and plot windows now reposition to avoid overlap on open.
- 🎨 **Composite Rendering** *(beta)*: `CompositeBitmapWriter` added — multi-channel color compositing with per-layer `BlendRecipe` and parallel rendering.
- 🔌 **Export API**: Injectable export backend scaffold (`ExportFormatDescriptor`) and "Export as" submenu added to `MatrixPlotter`.

**v0.1.1**
- 🐛 **Crop Sync Bug Fix**: Fixed incorrect behavior when cropping within a synchronized window group.
- 🔌 **New Public APIs**: Added `GetAxisTracker(string)`, `SetOrthogonalView(string?)`, and `OrthogonalViewAxisName` to `MatrixPlotter`, enabling external control of axis trackers and orthogonal view switching from host apps.
- 🗺️ **Unified Coordinate Conversion**: Exposed `ScreenToData` / `DataToScreen` on `MxView`, consolidating all coordinate transform logic into a single authoritative path.
- 📐 **Orthogonal View Crop Support**: Added context menus and crop operations to the bottom and right orthogonal views, matching the main view experience.
- 📋 **ROI "Copy Data"**: Rectangle and oval overlays now support copying the enclosed region as an image or as CSV/TSV text to the clipboard.
- 🪟 **Multi-Window UX Improvements**: Tile/Sync actions now operate only on visible windows; hidden windows are automatically deselected. Clipboard paste extended to support both image and CSV/TSV text. Window list context menu is now dynamically generated based on selection and visibility state.
- 🎨 **Bitmap Interpolation**: Render surface now automatically switches interpolation mode based on zoom level (nearest-neighbor when zooming in, linear when zooming out).
- 💅 **Value Range Bar UX**: Keyboard handling improved; mode selection converted to a flyout menu; LUT label opens a dropdown on click.
- 🔄 **OME-TIFF Metadata Round-trip**: `mxplot.*` system metadata (LUT, overlays, display settings) is now preserved on Save As. Format headers (`OME_XML`, `FITS_HEADER`) are still excluded as before.

**v0.1.0** (Documentation and bug-fix release)
- 🔍 **XML Documentation**: Added comprehensive XML doc comments to public API (`IMatrixData`, `MatrixDataValueConverter`, `FastMinMaxFinder`, `MatrixPlotter`) — now surfaces correctly in IDE IntelliSense from NuGet.
- 🛠️ **NuGet Doc Fix**: Replaced `IncludeDocumentationFile` with `GenerateDocumentationFile` in `Directory.Build.props` so `.xml` files are reliably included in packages.
- 🖼️ **MatrixPlotter Accessors**: Added `MainView`, `BottomView`, and `RightView` public getters, enabling event registration (mouse, keyboard, etc.) from WinForms/WPF host apps.
- 🐛 **CsvHandler Bug Fix**: `CsvHandler.Load` was silently ignoring the `flipY` parameter — loaded data had its Y-axis flipped. Now corrected and covered by regression tests.
- 📦 **MathNet.Numerics Stabilized**: Downgraded `MxPlot.Extensions.Fft` dependency from `6.0.0-beta2` to the stable `5.0.0`. FFT API is unchanged.

**v0.1.0-beta**
- 💾 **Virtual Frame Streaming**: Introduced `VirtualFrames<T>` — MMF-backed on-demand frame loading for large files (>2 GB default threshold). Peak memory stays near one frame. Pluggable prefetch strategies keep navigation smooth.
- 🔌 **Format Plugin Registry**: `FormatRegistry` auto-discovers `MxPlot.Extensions.*.dll` at startup. Built-in formats (`MxBinaryFormat`, `CsvFormat`, `FitsFormat`) are always available; third-party formats are picked up with no explicit registration call.
- 📐 **New I/O Capability Interfaces**: `IProgressReportable`, `IVirtualLoadable`, `ICompressible` — format handlers declare their capabilities explicitly, enabling generic UI wiring (e.g. attaching a progress bar) without format-specific knowledge.
- ⚙️ **`LoadingMode` & `VirtualPolicy`**: `Auto / InMemory / Virtual` loading mode selection. `VirtualPolicy.ThresholdBytes` (default 2 GB) resolves `Auto` based on file size at runtime.
- 🗂️ **`.mxd` Format Overhaul**: Clarified binary layout, enabled direct MMF mount on uncompressed files, and added `CreateVessel<T>` factory + fast-path `SaveAs` (file-move + trailer rewrite, zero re-encode).
- 🧩 **`MatrixData<T>` Refactored into Partials**: Split into `Constructors`, `DataAccessors`, `Statistics`, and `Static` files. `_valueRangeMap` reference-sharing model ensures `Invalidate()` propagates correctly across shallow copies.
- 🎨 **Imaging Subsystem in Core**: `LookupTable` and `ColorThemes` (Grayscale, Hot, Cold, Spectrum, HiLo, and more) moved to `MxPlot.Core.Imaging` — no UI dependency needed for colormap access.
- 🔬 **Spatial Filters**: `MedianKernel`, `GaussianKernel`, `MeanKernel` via the `IFilterKernel` interface. Progress and cancellation supported.
- 📊 **Orthogonal Slice & Projection**: `SliceOrthogonalOperation` and `OrthogonalProjectionsOperation` — both XZ and YZ planes computed in a single memory pass, with zero-allocation buffer-reuse parameters.
- 🔭 **FITS Format**: New `FitsHandler` — standard FITS read/write with multi-HDU support and cancellation.
- 🖥️ **MxPlot.UI.Avalonia (New Package)**: Cross-platform visualization library on Avalonia 11. Includes `MxView` (pan/zoom image control), `MatrixPlotter` (full-featured plotter window), and `MxPlotHostApplication` for WinForms/WPF embedding.
- 📱 **MxPlot.App (New — included in this repository)**: Standalone scientific viewer with plugin-driven file open, multi-window dashboard, metadata editor, ROI statistics, and extensible analysis UI.
- ⚠️ **Breaking Changes**: `IOperation` → `IOperation<out TResult>` (generic `Apply<TResult>`); `Axis.MinMax` → `Axis.Range`; `OnDemand` terminology → `Virtual`; `IMatrixData.ValueType` removed.

**v0.0.5-alpha** (Improving the internal logic with breaking changes)
- 🧠 Frame Sharing & Memory Model: Refined the zero-cost O(1) frame reordering (Reorder) using underlying array reference sharing.
- 🔄 Explicit Copy Semantics: Clarified mutation semantics and introduced explicit deep copying via Duplicate() and Clone().
- ⚡ Lazy Min/Max Evaluation: Implemented lazy evaluation and caching for frame min/max values (GetValueRange), optimizing performance during bulk array mutations, which largely modified the internal logics of MatrixData.
- 📚 Comprehensive Documentation: Added and updated extensive Markdown guides for Core Operations, Frame Sharing Model, Volume Accessor, and Dimension Structure.

**v0.0.4-alpha** (Added new packages and introduced breaking changes)
- 🔌 Generic Bridge: Enabled non-generic layers (UI/ViewModels) to invoke strongly-typed image processing operations without compile-time knowledge of generic type `<T>`.
- 🛠 Visitor Pattern: Introduced `IMatrixData.Apply(IOperation)` as a unified dispatch entry point to dynamically resolve and execute Volume, Filter, and Dimensional operations.
- ➕ Added MxPlot.Extensions.Images package for useful image loading via SkiaSharp (PNG, JPEG, BMP, TIFF).
- ➕ Added MxPlot.Extensions.Fft package for 2D FFT processing via MathNet.Numerics.
- 🔄 Method Renaming (Breaking): Renamed `At` to `GetFrameIndexAt` in DimensionStructure.

**v0.0.3-alpha** (Some modifications and reorganization of packages)
- 🏗️ **Metapackage Structure**: Reorganized as a metapackage `MxPlot` bundling `MxPlot.Core` and common extensions for easier installation and management.
- 🔄 **Method Renaming**: Renamed `XAt`/`YAt` to **`XValue`/`YValue`** for better clarity and naming consistency.
- ➕ Added MxPlot.Extensions.Tiff and MxPlot.Extensions.HDF5 packages for specialized file I/O.
- 🏗️ **Type Optimization**: Changed `Scale2D` from `record struct` to **`readonly struct`** to ensure immutability and improve performance.
- ➕ **Added `GetAxisValues` / `GetAxisValuesStruct`**: Now supports deconstruction for more intuitive axis value retrieval.
- 🏗️ **Enhanced `IMatrixData`**: Implemented Facade pattern methods for `DimensionStructure`, simplifying the interface for complex data navigation.

**v0.0.2-alpha** (First core implementation)
- ✨ **NEW**: `VolumeAccessor<T>` — High-performance 3D volume operations with readonly struct.
- ⚡ **NEW**: `VolumeOperator` — Optimized volume projections with tiled memory access (2–3.4× speedup).
- 🎯 **ENHANCED**: `DimensionalOperator.ExtractAlong()` — Extract data along specific axis with multi-axis and ActiveIndex support.

**v0.0.1-alpha** (Package name reservation)
- Package name reserved on NuGet. No implementation (placeholder only).

**Initial Development (Pre-release)**
- Core multi-axis container with dimension management
- Binary I/O (.mxd) with compression
- Dimensional & cross-sectional operators
- Arithmetic operations with SIMD optimization
- OME-TIFF and ImageJ-compatible TIFF support via MxPlot.Extensions.Tiff packages
- HDF5 support via MxPlot.Extensions.HDF5 package