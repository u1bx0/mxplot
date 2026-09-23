## 📊 Version History


**v0.5.0** (Lazy-decode Virtual backends, unified Processing commands, FFT, and Resample)
- 🗂️ **Lazy-Decode Virtual Backends (New)**: Compressed multi-frame OME/ImageJ TIFF can now be opened Virtual, decoded frame by frame on demand (`TiffDecodedFrames<T>`), where memory mapping cannot work. TIFFs beyond 32,767 frames now load. A priority-queue preload scheduler with per-worker readers follows the cursor: 4,000 LZW frames (256×256) load fully in 0.23 s instead of 31.1 s.
- 🧱 **Virtual Frames Hierarchy**: `VirtualFrames<T>` is now a backend-neutral cache/eviction/prefetch skeleton, with the memory-mapped parts in `MmfFrames<T>`. `IsVirtual` is no longer MMF-only, so the status-bar "(Cached NN%)" badge and the dashboard's Virtual badge follow a Lazy backend as it fills. Repeat reads of the same frame no longer cause preload churn, and eviction now drops the true LRU frame.
- 📊 **All-Mode Range Scan**: Choosing All now scans large datasets on a worker thread with progress and Cancel instead of blocking the window; a 🔄 button forces a full scan for MMF / still-filling Lazy data.
- 🔬 **Cache Monitor**: New flat-grid view (one cell per frame, square-ish) alongside the axis-grouped view, plus a Wrap toggle.
- ⏺️ **Streaming `.mxd` Write**: `MatrixDataSerializer.CreateStreamingVessel<T>` returns a `VesselWriter<T>` that appends frames one at a time — the frame count is only fixed by `Complete(params Axis[])`, which writes the trailer. For recording a live feed of unknown length.
- 🎛️ **Scale / Axis Consistency**: The Rename dialog's *Index-based* box now reflects `IsIndexBased` and states what a Color/Tagged axis carries. Composite is kept when a different axis is replaced, and *Revert Scale Settings* covers `IsIndexBased`.
- 🔌 **Host API**: `MatrixPlotter.ShowToast` is public; `RenderToBitmap` gained an overload that renders into a caller-owned bitmap, avoiding one allocation per frame when feeding an encoder. The progress bar no longer flashes for short operations.
- 🌈 **Color (RGB-Max) / Color (RGB-Add)**: Two new picks in the orthogonal-view projection selector tint every slice of the swept axis by its depth color and blend them (per-channel max, or a sum clamped at 255), so structures at different depths overlap instead of only the winning slice showing as in `Color (Max)`. Every projection mode now explains itself in a tooltip.
- 🧩 **Processing Commands**: Every one-shot Processing/Conversion menu operation (Median/Gaussian, Normalize, Log Transform, Transpose, Reverse Stack, Extract Dimension, Grayscale, Convert Value Type, and the two new commands below) now shares one implementation, offering "This frame only", "Sync source data" and "Replace data" consistently. The hamburger menu is regrouped into Data / Scale / Processing / Info tabs.
- 🌀 **FFT 2D (New)**: Processing > Frequency > FFT 2D — forward/inverse, with a Center DC option; works on a Composite channel cube and as a live Sync follower. The underlying all-frames FFT reports progress and can be cancelled.
- 📐 **Resample (New)**: Changes a dataset's pixel count (Nearest neighbor or Bilinear) while keeping its physical extent, with a "Keep ratio" option in the dialog.
- 🔄 **Transpose**: Gained "Replace data", and now carries the window's overlays over to the transposed result.
- 🐛 **Bug Fixes**: Composite Shared/Per-Channel radios could show neither checked; the Composite header of a seeded window did not show its Fixed shared range; LUT-mode Search Min/Max did not update the histogram lines; side views could stay Composite under a LUT-mode MainView after an axis change; a Sync-follower window not zoomed to Fit stopped redrawing on updates; a linked window's (e.g. an Extract Frame's) orthogonal slices/projection did not follow a content-only update to its source; a Sync/ROI-View/orthogonal window's Scale tab went stale while left open; replacing a window's data could leave its linked windows pointing at data that was just dropped; toast/status-bar and AxisTracker fps-tooltip glitches.
- ⚠️ **Breaking Changes**:
  - The file-format layer (`CsvHandler`, `FitsHandler`, `FormatRegistry`, `IMatrixDataIO`, `MatrixDataConfig`, `MatrixDataSerializer`, …) moved from `MxPlot.Core.IO` to `MxPlot.Core.IO.Formats`; update `using` directives.
  - `ILazyFrameList` → `ILazyDataSource`; `WritableVirtualStrippedFrames<T>` → `WritableStrippedMmfFrames<T>`; `IFrameContentNotifier` removed (superseded by `ILazyDataSource.FrameStored`).
  - `IPlotterAction` → `IPlotterTool` (the `Actions/` folder is now `Tools/`); `CropAction` → `CropTool`.
  - `NormalizeOperation`/`LogTransformOperation` (and the `Normalize`/`LogTransform` extension methods) dropped `SingleFrameIndex`: they now always process every frame of the data given, matching every other Core operator. To process one frame, slice it out first (`SliceAt`).


**v0.4.0** (ROI View, Transpose, and Composite / side-view consistency)
- 🔍 **ROI View (New)**: "Open ROI View" on a Rectangle/Oval overlay opens the enclosed region in its own window, kept in sync through `LinkedView`. A Composite source stays Composite.
- 🔄 **Transpose**: Now exposed as a `MatrixPlotter` operation. Carries axis names/calibration over to the result, streams Virtual sources in RAM-bounded bands, and hands the result the source's range mode and LUT.
- 🎨 **Composite Consistency**: Auto/All value ranges survive `Refresh()`, line profiles take their channel colours, and the first switch into Composite no longer leaves the side views on a stale LUT.
- 📐 **Side-View Correctness**: Region statistics, value range and ROI View now account for BottomView's flipped Y, and side-view panels refresh after a re-slice.
- 🖼️ **XY Projection Lifecycle**: The projection window has a coherent mode lifecycle, and no longer keeps a stale `ActiveIndexChanged` subscription after its source data is replaced.
- ⚡ **Parallel LUT Rendering**: Nothing ever assigned `ParallelOptions`, so `LutBitmapWriter` had been rendering single-threaded while `CompositeBitmapWriter`, which defaulted its own, ran in parallel. A 4096×4096 `Refresh()` drops from 11.4 ms to 6.0 ms.
- ⚡ **Composite Refresh Cost**: `PushCompositeRecipes` assigned a fresh `ToList()` every call, so the styled property changed identity even when the recipes had not and `RenderSurface` rebuilt the bitmap for it — a second full-frame render per `Refresh()` under a live feed. 4096×4096 RGB drops from 28.7 ms to 16.0 ms.
- ⚡ **Composite RGB Fast Path**: `CompositeBitmapWriter` bypasses the general per-pixel LUT/blend/clamp path for byte/3-channel pure-RGB Fixed(0,255) recipes — a raw byte-interleave-speed render instead. New `MatrixPlotter.CompositeRecipes` lets a host (e.g. a live camera) opt in explicitly; also fixes an `EnsureLutCache` reference-equality check that never hit.
- 🐛 **Composite/LUT Round-Trip**: Switching Composite → LUT → Composite silently dropped each channel's Fixed range back to `Current`, forcing a rescan and losing Fast Path eligibility. Now preserved, the same way metadata restore already does.
- 🎛️ **`Parallelism` Control**: New `int` property on `MatrixPlotter`, `MxView` and `RenderSurface` — `0` renders on one thread, `-1` is unrestricted, a positive value caps the degree of parallelism, and the default `RenderSurface.ParallelismAuto` decides from frame size. Same encoding as `MatrixDataPlotter.Parallelism`, plus Auto, for hosts that would rather spend their cores on acquisition than on redraw.
- ⚡ **Histogram Recompute**: Replaced cancel-and-restart with a coalescing dirty/running loop — a slow computation (e.g. a large Live camera frame) could previously never finish, always pre-empted before painting. Now always completes, at most one frame behind the latest data.
- 🔌 **Host Opt-Outs**: `AllowDataReplace` and `AutoUpdateWindowIcon` let an embedding host suppress the Processing menu's "Replace data" option and the automatic titlebar icon; both default to the previous behaviour. `ControlFactory` is now public so hosts can build widgets matching MxPlot's look.
- 📁 **LUTs Folder**: External palettes are now looked up beside the running assembly, beside the `.app` bundle on macOS, and in the working directory — so file-based apps (`dotnet run app.cs`) and macOS bundles can find them.
- 🌀 **Samples**: Added a spiral hyperstack (X, Y, Z, C, T) demo, built from source.
- ⚠️ **Processing Dialogs**: Unified on a single virtual-materialization warning.
- 💅 **UI/UX Polish**: Overlay right-click follows the PowerPoint selection convention; the Gain/Gamma label bolds when either is non-default; distinct icons for Properties / Text Edit / Find Min/Max / Crop; Ctrl+click multi-select and tooltips in the dashboard; Avoid Window Overlap toggle.
- 📄 **Third-Party Notices**: Turbo's and Material Design Icons' license notices are collected in each project's `THIRD-PARTY-NOTICES.txt`, which now ships inside the NuGet packages.
- 📦 **0.3.1**: Fixed MxPlot.App having been published as the MxPlot metapackage in the 0.3.0 release.
- 🐛 **Bug Fixes**: Red/blue missing-value colours were swapped in ColorThemes; region-to-pixel index rounding and Copy Image orientation within a region; custom LUTs with few levels; clipboard CSV auto-detect; Crop's missing Replace-data guard; macOS minimize animation and the dashboard-restore focus loop with 3+ windows; MxPlot.App's Icon-view window-list thumbnails looked blurry at larger card sizes.
- ⚠️ **Breaking Changes**:
  - `LookupTable` / `ColorThemes` moved from `MxPlot.Core.Imaging` to `MxPlot.UI.Avalonia.Rendering`. Core no longer carries them.
  - `IBitmapWriter` gained `ParallelPolicy`. Third-party writers must add it; the three in this library already do. `ParallelOptions` also changed meaning: it now supplies the options used *when* the loop runs in parallel and no longer decides *whether* it does, so a writer left with `ParallelOptions = null` now renders in parallel where it previously rendered serially.
  - `IMxPlotPlugin` → `IMxPlotAppPlugin`, `IMxPlotContext` → `IMxPlotAppContext` (MxPlot.App plugin API).


**v0.3.0** (Composite workflow completion, ColorCoded rendering, live-link refactor, and stability/performance improvements)
- 🌈 **ColorCoded Rendering (New)**: New live depth/time colour-coded projection mode — pick `Color (Max)`/`Color (Min)` in the orthogonal-view projection selector for a Z (or any frozen-axis) projection, in a linked child window.
- 🔢 **AxisTracker 0-based Display**: The axis position indicator, its tooltip, and the drag overlay now show the true 0-based index (`7 [100]`) instead of the previous 1-based `8/100`, matching every other 0-based index already used throughout the codebase — including the new ColorCoded Start/End range.
- 🎨 **Composite Rendering (Feature-complete)**: Promoted Composite mode from beta-level base to full workflow support. Added RGB auto-composite behavior, grayscale conversion path, and a dedicated grayscale dialog/fallback path.
- 🧩 **Composite + Extract Integration**: `Extract Frame` / `This Frame Only` now work consistently in Composite mode, including orthogonal views (XZ/YZ) and full-channel extraction across the composite axis.
- 🔗 **Linked View Refactor**: Replaced the previous `LinkedSource`-style linkage with a unified `LinkedView` follower model. Live-derived windows now keep independent display settings while preserving source-follow behavior.
- ⚡ **Live Update Pipeline Optimization**: Reduced heavy re-initialization during projection/live updates. `UpdateProjectionData` now emits content-change notifications and commits incremental updates more lightweightly.
- 📊 **Value Range Reliability**: Improved automatic value-range handling by re-deriving **All-mode** ranges on each content change, reducing stale or inconsistent range states after live updates.
- 🧭 **Axis Handling Consistency**: Unified axis lookup to case-insensitive behavior (`FindAxis` + dictionary key normalization), improving robustness across rename/sync flows.
- 🖼️ **Rendering Backend Cleanup**: Migrated to `LutBitmapWriter`, deprecated legacy `BitmapWriter`, and introduced safer blit/render paths (`BitmapBlit`) with reduced contention issues.
- 📈 **Histogram / Projection Performance**: Refactored histogram processing (including async separation) and improved synchronization behavior between projection-related windows.
- 🎛️ **UI/UX Polish**: Unified per-axis settings menu behavior, integrated composite-mode switching UX, refined LUT panel/layout, refreshed icons/labels/dialog wording, and improved dashboard/window positioning.
- 🔌 **External Control API**: Added Facade properties on `MatrixPlotter` (`Lut`, `IsInvertedColor`, `LutDepth`, `RangeMode`, `IsFixedRange`, `FixedRange` with the new `RangeInfo` record struct) so display state can be driven without touching `ViewModel` or `MainView`. A direct `MainView` assignment now up-syncs to both through the new `MxView.RenderingPropertyChanged` event, so all three entry points converge on the same state. Frame position is deliberately controlled through `MatrixData.ActiveIndex` instead. Covered by the new `Tests.MxPlot.UI.Avalonia.Headless` suite, alongside axis/scale sync and reentrancy fixes.
- 🎚️ **Value-Range Mode Control**: `MatrixPlotter.RangeMode` (`Current` / `All` / `Roi` / `Fixed`) makes the mode itself externally settable, where previously only the pinned/unpinned `IsFixedRange` flag was exposed. Modes the data cannot support are downgraded rather than rejected (`All` on single-frame data, `Roi` with no ROI overlay → `Current`), and reading the property back reports the mode actually in effect.
- 🖥️ **`MxPlotScriptHost`**: New entry point for driving MxPlot from .NET 10 file-based apps (`dotnet run app.cs`) and other UI-less hosts — scripts, console tools, notebook cells. `Run(script, configure?)` starts the Avalonia message loop, runs `script` off the UI thread, and returns once every window it opened has closed; `Start` offers a non-blocking variant for REPL/notebook use (throws `PlatformNotSupportedException` on macOS, where AppKit requires the loop on the process main thread). `Show`, `Invoke`/`Invoke<T>`/`InvokeAsync`, `WaitForExit`, `Shutdown`, `IsRunning`, and `OpenWindowCount` round out the API. One session per process — a second `Run`/`Start` call, or use inside a host that already owns the Avalonia lifetime (`MxPlotHostApplication`), throws with a message pointing at the right alternative. Verified on Windows and macOS (both the `#:project` source path and the `#:package` NuGet path); see `docs/MatrixPlotter_Usage_Guide.md` ("Scripting with MxPlotScriptHost").
- 📄 **`.mxd` Header Cleanup**: `MatrixDataConfig` now names its `TypeInfoResolver` explicitly instead of relying on `System.Text.Json`'s implicit reflection resolver, so `.mxd` I/O works in hosts that build with trimming/AOT settings — notably .NET 10 file-based apps (`dotnet run app.cs`), which previously threw `InvalidOperationException` on the first save. Alongside it, `FovAxis.TileLayout` became a `TileGrid` record struct instead of a `(int, int, int)` tuple, and the origins array is now serialized from the public `Origins` property rather than a `[JsonInclude]` private field. The header no longer needs `IncludeFields`, and reads as `{"TileLayout":{"X":2,"Y":3,"Z":1},"Origins":[…]}` instead of `Item1`/`Item2`/`Item3`.
- 🎬 **MP4 Export + Reusable Video-Export Base**: New `Mp4Exporter` in **MxPlot.UI.Avalonia.Video** — H.264 via an external `ffmpeg` process, so unlike uncompressed AVI it plays natively on macOS/QuickTime too. Registered only when `Mp4Exporter.IsFfmpegAvailable()`. `AviExporter`/`Mp4Exporter` now share a new `VideoExporterBase`/`IVideoFrameWriter` base (settings dialog + render loop), so a third-party video-format plugin only needs to implement frame writing.
- 🧊 **Restack Context Menu + Banded Virtual Write**: New `Restack along X/Y` context-menu action on the orthogonal side views (`BottomView`/`RightView`), reconstructing the volume as a new stack viewed from another axis, opened in its own child window with history recording which axis/direction/positions it was built from. `VolumeAccessor<T>.Restack`'s X/Y (transpose) paths, when resolved to Virtual output, buffer a RAM-bounded band of output frames in managed memory (gathered by sweeping the source once per band) and then write each completed frame to the MMF vessel as a single contiguous whole-frame write, instead of assembling the whole result in managed heap first (avoiding the RAM exhaustion Virtual output exists to avoid) or scattering individual rows across the vessel directly (measured to cost several milliseconds per row from MMF page-fault overhead on some platforms). Also fixed `VirtualPolicy`'s frame-count threshold (intended for file-format per-frame parsing overhead) incorrectly forcing Virtual output on small Restack results, since its output frame count is just a spatial pixel count, not a memory-size proxy. Cancellation is now checked per row instead of per parallel chunk, so pressing Cancel on a large Restack actually stops promptly.
- 🧬 **Cancellable `Clone`/`Duplicate`**: `IMatrixData.Clone(bool, IProgress<int>?, CancellationToken)` and `Duplicate<T>(...)` overloads let large/Virtual duplications report progress and be cancelled; `MatrixPlotter.DuplicateAsync` now wires this up with a working status-bar Cancel button. Virtual-to-Virtual duplication also now propagates any per-frame value range the source had already cached, instead of discarding it and forcing a rescan on next access.
- 📐 **Composite-Aware ROI Statistics**: An overlay's "Show Statistics" label now breaks down Min/Max/Avg per visible composite channel (tagged by name, e.g. "GFP") when Composite/ColorCoded rendering is active, instead of silently reflecting only one channel's raw values — capped to a handful of lines unless the overlay is selected, in which case every visible channel is shown. "Show Statistics" and "Use ROI for Value Range" now show a checkbox-style icon reflecting their on/off state instead of a static label. Fixed pasted overlays incorrectly inheriting the source's "Use ROI for Value Range" designation, which is a single-owner relationship.
- 🐛 **Bug Fixes**: Fixed multiple issues around Composite/Histogram sync, orthogonal-view slice/cache behavior, ROI copy orientation, Extract-in-live guard logic, and replace-data interactions with sync/link states.
  - **Switching the value range to All or ROI silently landed in Fixed.** The ViewModel had no notion of the range mode, so the window re-derived one from the `IsFixedRange` boolean — which cannot express All or ROI — one step after the mode was set. Fixed by the `RangeMode` work above; `IsFixedRange` now only acts when it genuinely disagrees with the mode in effect.
  - **Switching the frozen (orthogonal) axis left the side views blank.** `OrthogonalViewController.Activate()` always re-centers the crosshair before rebuilding the side views, so re-freezing at that same centered position (switching directly to another axis, or unfreezing then freezing a different one) looked like "no crosshair movement" to the position-diff check that decides whether a rebuild is needed — the check has no way to know the axis being sliced changed, only whether the pixel position did. Fixed by resetting the tracked position to its "not yet established" sentinel right before the rebuild, forcing it through.
  - **MxPlot.App (macOS): renaming a window from the dashboard list only accepted lowercase ASCII** — Shift, CapsLock, and IME/Japanese input all silently produced raw lowercase characters. Root cause: the inline rename `TextBox` gets programmatic focus (`Focus()`) right after the triggering context menu closes, and on macOS that doesn't always hand the dashboard window back its OS-level key/active status in time, even though Avalonia's own logical focus succeeds — Shift/CapsLock/IME routing depends on the window actually being key. Fixed by explicitly reactivating the window before focusing the `TextBox`.
- ⚠️ **Breaking Changes**:
  - `MatrixPlotter.LinkedSource` / `LinkedSourceExcludedAxes` removed. The replacement (`LinkedView`, `OverlayAxisSource`) is internal; there is no public successor. Use `LinkRefresh` / `CreateLinked` for peer refresh linkage.
  - `MatrixPlotterViewModel.ActiveFrame` removed. Use `plotter.MatrixData.ActiveIndex` (bounds-checked, raises `ActiveIndexChanged`).
  - `OrthogonalViewController`: `ComputeXYProjectionForExportAsync()`, `OpenAsNewDataRequested` and `UpdateHyperstackState(bool)` removed, superseded by `CreateProjectedDataRequested` and `UpdateProjectedDataAvailability(bool)`.
  - `BitmapWriter` is `[Obsolete]` — use `LutBitmapWriter`. The public `OldCompositeBitmapWriter` was removed.
  - `MatrixData<T>.SliceAt` and the `SliceOperation` record gained optional parameters: source-compatible, but binary-incompatible (recompile required).
  - `FovAxis.TileLayout` changed type from `(int X, int Y, int Z)` to the new `TileGrid` record struct. Member access (`.X`/`.Y`/`.Z`) is unchanged; tuple comparisons such as `Assert.Equal((3, 2, 1), fov.TileLayout)` need `new TileGrid(3, 2, 1)`.
  - **`.mxd` header format changed** (schema version 1 → 2). A version-1 file that contains a `FovAxis` loads with an empty tile grid, because the old `Item1`/`Item2`/`Item3` and private-field member names are no longer read. Files without a `FovAxis` are unaffected. No migration path is provided — the format predates any production use.


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