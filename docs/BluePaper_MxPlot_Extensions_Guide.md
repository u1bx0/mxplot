# MxPlot Extension Development Guide (BluePaper)

**Version Date:** 2026-04-24  
**Audience:** Developers who want to extend MxPlot via external DLLs

---

## 1. Introduction

MxPlot has three external extension points.
By placing a DLL in the appropriate directory you can add functionality without recompiling the application.

| Extension Point | What you can add | Required reference |
|---|---|---|
| **IMatrixDataReader / IMatrixDataWriter** | File format support | `MxPlot.Core` only |
| **IMatrixPlotterPlugin** | Commands in the MatrixPlotter Plugins tab | `MxPlot.UI.Avalonia` |
| **IMxPlotPlugin** | Commands in the MxPlot.App (dashboard) Tools menu | `MxPlot.App` |

---

## 2. Implementation Status

| Feature | Status | Notes |
|---|---|---|
| `FormatRegistry.ScanAndRegister()` | ✅ Working | Runs automatically at startup |
| `MatrixPlotterPluginRegistry.LoadFromDirectory()` | ✅ Working | Called automatically at startup |
| `MxPlotAppPluginRegistry.LoadFromDirectory()` | ✅ Working | Called automatically at startup |
| `FormatRegistry` startup auto-scan | ✅ Working | Auto-detects `MxPlot.Extensions.*.dll` |
| Plugin Registry startup auto-scan | ✅ Working | `App.axaml.cs` scans `plugins/` automatically |

---

## 3. File Format Extension

### 3.1 Overview

The most portable extension point. Only a reference to `MxPlot.Core` is required,
and if the DLL name is `MxPlot.Extensions.{Name}.dll` it is registered automatically at startup.

### 3.2 Project Setup

```xml
<!-- MyPlugin.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <!-- Follow naming convention: MxPlot.Extensions.{Name}.dll -->
    <AssemblyName>MxPlot.Extensions.Zarr</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- Only MxPlot.Core is needed -->
    <PackageReference Include="MxPlot.Core" Version="x.x.x" />
  </ItemGroup>
</Project>
```

### 3.3 Reader Implementation Example

```csharp
using MxPlot.Core;
using MxPlot.Core.IO;
using System.Collections.Generic;

namespace MxPlot.Extensions.Zarr
{
    public sealed class ZarrFormat : IMatrixDataReader, IMatrixDataWriter
    {
        public string FormatName => "Zarr";
        public IReadOnlyList<string> Extensions { get; } = [".zarr"];

        public IMatrixData Read(string path)
        {
            var md = new MatrixData<float>(256, 256);
            // ... reading logic ...
            return md;
        }

        public void Write(IMatrixData data, string path)
        {
            // ... writing logic ...
        }
    }
}
```

#### Optional: Progress Reporting

For heavy files, implement `IProgressReportable` and MxPlot's UI will show a progress bar.

**Sign convention (important):**

The reported value for `IProgress<int>` follows a special sign convention.
Because putting a `TotalFrames` property on the interface is awkward when the total is unknown
before header parsing, the convention is to **report the total count once as a negative value**.

| Call | Value | Meaning |
|---|---|---|
| Once before the loop | `-totalFrames` (negative) | Declare total frame count |
| After each frame | `i` (0-based) | Frame i+1 complete |
| After completion (optional) | `totalFrames` | UI ignores (cleared in finally) |

```csharp
public sealed class ZarrFormat : IMatrixDataReader, IProgressReportable
{
    public IProgress<int>? ProgressReporter { get; set; }

    public IMatrixData Read(string path)
    {
        int frameCount = ReadFrameCount(path);
        ProgressReporter?.Report(-frameCount);   // ① Negative value declares total

        for (int i = 0; i < frameCount; i++)
        {
            // ... read frame i ...
            ProgressReporter?.Report(i);         // ② Report 0-based index
        }
        return result;
    }
}
```

#### Optional: Cancellation Support

`CancellationToken` is a plain property on `IMatrixDataReader`.
MxPlot's UI sets it automatically. Call `ThrowIfCancellationRequested()` at frame boundaries.

```csharp
public sealed class ZarrFormat : IMatrixDataReader
{
    public CancellationToken CancellationToken { get; set; }

    public IMatrixData Read(string path)
    {
        for (int i = 0; i < frameCount; i++)
        {
            CancellationToken.ThrowIfCancellationRequested();
            // ... read frame i ...
        }
        return result;
    }
}
```

Because `CancellationToken` is a struct with a natural default of `CancellationToken.None`,
the cost of having but not using it is zero.

#### Optional: Virtual Loading

For files larger than a few GB, implement `IVirtualLoadable` to hook into MxPlot's
"Virtual / InMemory" selection dialog.

```csharp
public sealed class ZarrFormat : IMatrixDataReader, IVirtualLoadable
{
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Auto;

    public IMatrixData Read(string path)
    {
        if (LoadingMode == LoadingMode.Virtual)
            return ReadVirtual(path);
        return ReadInMemory(path);
    }
}
```

### 3.4 Virtual Loading — Detailed Implementation

"Virtual Mode" means frames are **read on-demand via MMF** instead of loading the entire file into RAM.
This is effective for uncompressed files larger than a few GB.

See also: **[VirtualFrames Guide](./VirtualFrames_Guide.md)** for the full storage architecture.

#### Prerequisites: Format conditions suitable for Virtual Mode

| Condition | Description |
|---|---|
| **Random-accessible** | Byte offset of each frame is known in advance |
| **Uncompressed** or **independently decompressible per frame** | Sequential-only compression streams are not suitable |
| **Fixed layout** | Frame offsets can be determined by scanning the header |

#### Implementation Pattern

Virtual loading is a **2-step** process.

**Step 1: Scan the file and build an offset table** (do not read pixel data)

```csharp
private static (long[][] offsets, long[][] byteCounts) ScanOffsets(
    string path, int frameCount, int width, int height, int bytesPerPixel)
{
    var offsets    = new long[frameCount][];
    var byteCounts = new long[frameCount][];
    long frameBytes = (long)width * height * bytesPerPixel;
    long dataStart = ReadHeaderSize(path); // format-specific

    for (int i = 0; i < frameCount; i++)
    {
        offsets[i]    = [dataStart + i * frameBytes];
        byteCounts[i] = [frameBytes];
    }
    return (offsets, byteCounts);
}
```

**Step 2: Construct `VirtualStrippedFrames<T>` and pass it to `MatrixData<T>`**

```csharp
public IMatrixData ReadVirtual(string path)
{
    var (width, height, frameCount, bytesPerPixel) = ReadHeaderInfo(path);
    var (offsets, byteCounts) = ScanOffsets(path, frameCount, width, height, bytesPerPixel);

    // isYFlipped: true if the file stores rows bottom-up (BMP-style)
    var vf = new VirtualStrippedFrames<float>(
        path, width, height, offsets, byteCounts, isYFlipped: false);

    // Ownership transfers to MatrixData; Dispose is automatic
    var md = MatrixData<float>.CreateAsVirtualFrames(width, height, vf);
    md.SetXYScale(...);
    return md;
}
```

#### `IVirtualLoadable` Implementation

```csharp
public sealed class MyFormat : IMatrixDataReader, IVirtualLoadable
{
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Auto;

    public IMatrixData Read(string path)
    {
        var (width, height, frameCount) = ReadHeaderInfo(path);
        long fileBytes = new FileInfo(path).Length;
        var mode = VirtualPolicy.Resolve(LoadingMode, fileBytes, frameCount);

        return mode == LoadingMode.Virtual
            ? ReadVirtual(path, width, height, frameCount)
            : ReadInMemory(path, width, height, frameCount);
    }
}
```

`VirtualPolicy.Resolve` selects Virtual when either condition is met:
- File size exceeds `VirtualPolicy.ThresholdBytes` (default 2 GB)
- Frame count exceeds `VirtualPolicy.ThresholdFrames` (default 1000)

Both thresholds can be changed at runtime.

#### Strips vs Tiles

| Layout | Class | Typical formats |
|---|---|---|
| **Strip** (1 frame = 1–N row groups) | `VirtualStrippedFrames<T>` | FITS, Raw Binary, strip TIFF |
| **Tile** (1 frame = M×N tile groups) | `VirtualTiledFrames<T>` | Tiled TIFF, large microscopy formats |

**Strip:**

```csharp
var vf = new VirtualStrippedFrames<float>(
    path, width, height, offsets, byteCounts, isYFlipped: false);
```

**Tile:**

```csharp
// offsets[frameIndex][tileIndex] — tiles in left→right, top→bottom order
var vf = new VirtualTiledFrames<ushort>(
    path, imageWidth, imageHeight,
    tileWidth, tileHeight,
    offsets, byteCounts, isYFlipped: false);
// Right/bottom edge tile clipping is handled automatically
```

#### Cache Settings

Default: LRU cache (16 frames) + NeighborStrategy (prefetch ±N).

```csharp
vf.CacheCapacity = 32;
vf.CacheStrategy = new NeighborStrategy(ahead: 4, behind: 2);
```

#### Y-axis Orientation

| Format | `isYFlipped` |
|---|---|
| TIFF (top-down) | `false` |
| FITS (top-down) | `false` |
| BMP / many astronomy formats (bottom-up) | `true` |

---

### 3.5 Deployment

```
MxPlot.App.exe
MxPlot.Core.dll
MxPlot.Extensions.Zarr.dll    ← just place alongside the exe
```

`FormatRegistry` scans `AppContext.BaseDirectory` for `MxPlot.Extensions.*.dll` at startup.
No application code changes are required.

---

## 4. MatrixPlotter Plugin (IMatrixPlotterPlugin)

### 4.1 Overview

Adds commands to the **Plugins tab** of the MatrixPlotter window.
Best suited for "process the currently displayed data" scenarios.

### 4.2 Project Setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>MyCompany.MxPlotPlugin.GaussianFit</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MxPlot.UI.Avalonia" Version="x.x.x" />
  </ItemGroup>
</Project>
```

### 4.3 Implementation Example

```csharp
using MxPlot.Core;
using MxPlot.UI.Avalonia.Plugins;

namespace MyCompany.MxPlotPlugin.GaussianFit
{
    public sealed class GaussianFitPlugin : IMatrixPlotterPlugin
    {
        public string CommandName => "Gaussian Fit";
        public string Description => "Fits a Gaussian function to the current frame.";
        public string? GroupName => "Analysis";

        public void Run(IMatrixPlotterContext ctx)
        {
            var result = RunGaussianFit(ctx.Data);
            ctx.WindowService.ShowMatrixPlotter(result, "Gaussian Fit Result");
        }

        private IMatrixData RunGaussianFit(IMatrixData data) { /* ... */ return data; }
    }
}
```

#### Grouping Example

```csharp
// Commands with the same GroupName are folded into a subgroup in the Plugins tab
public string? GroupName => "Deconvolution";
public string CommandName => "Wiener Filter";
```

### 4.4 Registration

```csharp
// Direct
MatrixPlotterPluginRegistry.AddPlugin(new GaussianFitPlugin());

// Directory scan (called automatically from App.axaml.cs)
var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
MatrixPlotterPluginRegistry.LoadFromDirectory(pluginsDir);
```

`LoadFromDirectory()` is called automatically from `App.axaml.cs`.
Just place the plugin DLL in the `plugins/` folder.

---

## 5. MxPlot.App Plugin (IMxPlotPlugin)

### 5.1 Overview

Adds commands to the **☰ → Tools menu** of the MxPlot dashboard.
Has access to all currently open datasets; suited for cross-window processing or batch export.

### 5.2 Project Setup

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>MyCompany.MxPlotAppPlugin.BatchExport</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MxPlot.App" Version="x.x.x" />
  </ItemGroup>
</Project>
```

### 5.3 Implementation Example

```csharp
using MxPlot.App.Plugins;
using MxPlot.Core.IO;
using System.IO;

namespace MyCompany.MxPlotAppPlugin.BatchExport
{
    public sealed class BatchCsvExportPlugin : IMxPlotPlugin
    {
        public string CommandName => "Batch CSV Export";
        public string Description => "Exports all open datasets to CSV.";

        public void Run(IMxPlotContext ctx)
        {
            foreach (var data in ctx.OpenDatasets)
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"{data.XCount}x{data.YCount}.csv");
                FormatRegistry.CreateWriter(path)?.Write(data, path);
            }
        }
    }
}
```

#### File Dialog Example (async)

```csharp
public async void Run(IMxPlotContext ctx)
{
    if (ctx.Owner is null) return;
    var file = await ctx.Owner.StorageProvider.SaveFilePickerAsync(
        new FilePickerSaveOptions { Title = "Save Result", SuggestedFileName = "result.csv" });
    if (file?.TryGetLocalPath() is { } path && ctx.PrimarySelection is { } data)
        FormatRegistry.CreateWriter(path)?.Write(data, path);
}
```

### 5.4 Registration

```csharp
MxPlotAppPluginRegistry.AddPlugin(new BatchCsvExportPlugin());
// or directory scan — same pattern as IMatrixPlotterPlugin
```

---

## 6. Mixing Multiple Extensions in One DLL

```csharp
// MxPlot.Extensions.MyCompanyTools.dll
public class MyPropFormat    : IMatrixDataReader      { ... }  // ① File format (auto-registered)
public class MyAnalysisPlugin: IMatrixPlotterPlugin   { ... }  // ② MatrixPlotter plugin
public class MyBatchPlugin   : IMxPlotPlugin          { ... }  // ③ MxPlot.App plugin
```

Each Registry selects only the types implementing its own interface, so mixing is safe.

---

## 7. Current Limitations and Future Extension Points

The following are **intentionally not exposed** at this time.

| Feature | Description | Plan |
|---|---|---|
| Adding MxView overlays | Plugin adds drawing objects to MxView | Add `IMatrixPlotterViewContext` in future |
| Crosshair move event subscription | Plugin hooks crosshair position changes | Same as above |
| Data replacement (Volume op) | Plugin swaps displayed data in MatrixPlotter | Add `IMatrixPlotterVolumeContext` in future |
| MxView behavior hooks | Intercept pointer/key events | Under careful consideration |

MxPlot's extension API follows the **allowlist-context** principle: `MatrixPlotter` and `MxView`
objects are never handed directly to plugins. Future extensions will always be added as thin
abstract interfaces to protect plugins from internal refactoring and to keep the same DLL
working across Avalonia and WinForms hosts.

---

## 8. Deployment Summary

```
[Application directory]
├─ MxPlot.App.exe
├─ MxPlot.Core.dll
├─ MxPlot.UI.Avalonia.dll
├─ MxPlot.App.dll
├─ MxPlot.Extensions.OmeTiff.dll          ← auto-scanned by FormatRegistry
├─ MxPlot.Extensions.Hdf5.dll
├─ MxPlot.Extensions.MyPropFormat.dll
└─ plugins/
   ├─ MyCompany.MxPlotPlugin.GaussianFit.dll
   └─ MyCompany.MxPlotAppPlugin.BatchExport.dll
```

| Type | Naming convention | Auto-detected |
|---|---|---|
| File format DLL | `MxPlot.Extensions.{Name}.dll` | ✅ From `AppContext.BaseDirectory` |
| MatrixPlotter plugin DLL | Any (`*.dll`) | Via `LoadFromDirectory()` |
| MxPlot.App plugin DLL | Any (`*.dll`) | Via `LoadFromDirectory()` |

---

## 9. Quick Start Checklist

### Adding a File Format

- [ ] Implement `IMatrixDataReader`
- [ ] Return `FormatName` and `Extensions`
- [ ] Name the DLL `MxPlot.Extensions.{Name}.dll` and place alongside the exe
- [ ] (Optional) `IProgressReportable` — progress bar support
- [ ] (Optional) `CancellationToken` property — cancellation support
- [ ] (Optional) `IVirtualLoadable` — virtual loading (§3.4)
  - [ ] Build offset table by header scan
  - [ ] `VirtualStrippedFrames<T>` (strips) or `VirtualTiledFrames<T>` (tiles)
  - [ ] `MatrixData<T>.CreateAsVirtualFrames()`
  - [ ] `VirtualPolicy.Resolve()` for Auto mode

### Adding a MatrixPlotter Plugin

- [ ] Implement `IMatrixPlotterPlugin`; return `CommandName` and `Description`
- [ ] Optionally return `GroupName`
- [ ] Place DLL in `plugins/`

### Adding a MxPlot.App Plugin

- [ ] Implement `IMxPlotPlugin`; return `CommandName` and `Description`
- [ ] Place DLL in `plugins/`