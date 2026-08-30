# MxPlot.UI.Avalonia — Overview

**Created**: 2026-04-22  
**Updated**: 2026-08-20  
**Target**: `MxPlot.UI.Avalonia` (Avalonia 11.3.x), .NET 8 / .NET 10

---

## What Is MxPlot.UI.Avalonia?

`MxPlot.UI.Avalonia` is the cross-platform UI layer that brings `MxPlot.Core` data to the screen.  
It depends on `MxPlot.Core` as its model/backbone and provides:

- A standalone window (`MatrixPlotter`) for displaying `IMatrixData`
- An embeddable rendering control (`MxView`) with LUT, zoom, pan, and overlay support
- An orthogonal (XYZ slice) view panel (`OrthogonalPanel`)
- A profile plotter window (`ProfilePlotter`) for line-profile analysis
- A plugin/action model for extending the UI without modifying the core

The library targets Avalonia 11 and runs on Windows, macOS, and Linux.

---

## Key Components

### `MatrixPlotter` (`Views/MatrixPlotter.cs`)

A full-featured `Window` subclass. One window per `IMatrixData`.

| Responsibility | Detail |
|---|---|
| Bitmap rendering | Delegates to `MxView` (LUT + WriteableBitmap) |
| Frame navigation | `AxisTracker` controls for each `Axis` |
| Value range control | `ValueRangeBar` + inline settings panel (Auto / Fixed / All / ROI modes) |
| Overlays | Rectangles, lines, ROI — drawn on Skia surface via `OverlayManager` |
| Orthogonal views | Right (XZ) and Bottom (ZX) slices via `OrthogonalPanel` |
| Status bar | Data type, memory size, zoom %, progress, notice/toast |
| Linked plotters | `LinkRefresh` for synchronized multi-window display |
| Composite mode | Multi-channel color compositing — see [Composite Rendering Guide](./MatrixPlotter_Composite_Guide.md) |
| External control | Facade properties (`Lut`, `IsInvertedColor`, `LutDepth`, `RangeMode`, `IsFixedRange`, `FixedRange`) — see [Usage Guide](./MatrixPlotter_Usage_Guide.md) |
| Plugin actions | `IPlotterAction` / `IMatrixPlotterContext` for tool extensions |

**Factory method:**

```csharp
MatrixPlotter.Create(data, lut: ColorThemes.Jet, title: "My Data").Show();
```

### `MxView` (`Controls/MxView.axaml.cs`)

The core rendering control. Converts `IMatrixData` frames to ARGB bitmaps via `LutBitmapWriter`
and a `LookupTable`, or via `CompositeBitmapWriter` when `RenderingMode.Composite` is active.

Key properties:
- `MatrixData` — the data source
- `FrameIndex` — currently displayed frame
- `Lut` — active `LookupTable`
- `IsFixedRange` / `FixedMin` / `FixedMax` — range override
- `IsInvertedColor` / `LutDepth` — LUT inversion and quantization level
- `RenderingMode` — `Lut` (default), `Composite`, or `ColorCoded`
- `CompositeRecipes` / `CompositeBlendMode` / `CompositeFrameIndices` — multi-channel composite state
- `Zoom`, `IsFitToView` — display transform
- `OverlayManager` — manages overlay objects

Key events:
- `MatrixDataChanged` — fired when `MatrixData` is replaced
- `RenderingPropertyChanged` — fired when any of the rendering properties above changes.
  This is what lets a direct `MainView.Lut = …` assignment propagate back up to `MatrixPlotter`
  and its ViewModel (see the [Usage Guide](./MatrixPlotter_Usage_Guide.md))
- `AutoRangeComputed` — fired with frame min/max after Auto render
- `ScrollStateChanged` — zoom/pan changes

> `MxView.BitmapRefreshed` is `internal` and cannot be subscribed to from outside the assembly.
> Use `MatrixPlotter.ViewUpdated`, which forwards the same signal.

### `OrthogonalPanel` and `OrthogonalViewController`

`OrthogonalPanel` is a grid layout containing three `MxView` instances:
- `MainView` — XY plane (primary)
- `BottomView` — XZ plane (X shared with `MainView`, Z along the vertical screen direction)
- `RightView` — YZ plane (Y shared with `MainView`, Z along the horizontal screen direction)

`OrthogonalViewController` wires the three views together:
- Synchronizes `FrameIndex` and render settings across all views
- Manages the XY-projection window (Z-axis depth display)

Orthogonal mode is activated automatically when `MatrixData` has 3+ axes.

### `ProfilePlotter`

A secondary window opened from `MatrixPlotter` for line-profile analysis.  
Displays pixel value along a `LineObject` overlay as a 1D chart.  
Multiple profiles can be open simultaneously and are automatically closed when the parent `MatrixPlotter` closes.

### `MxPlotHostApplication` (`MxPlotHost.cs`)

A minimal `Avalonia.Application` subclass for hosting MxPlot windows inside a non-Avalonia
application (WinForms, WPF, console). It loads the Fluent theme and the MxPlot styles and creates
no main window, so the host project does not need an `Application` subclass of its own.

```csharp
// Call once at application startup, before any MatrixPlotter.Create()
AppBuilder.Configure<MxPlotHostApplication>()
    .UseWin32()
    .UseSkia()
    .SetupWithoutStarting();
```

> There is no static `MxPlotHost.Initialize()` helper. The host picks its own platform backend
> through `AppBuilder`, as above.

See [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md) for details,
and [MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md) for the console variant,
which additionally needs `Dispatcher.UIThread.MainLoop`.

### `MxPlotScriptHost` (`MxPlotScriptHost.cs`)

Where `MxPlotHostApplication` assumes the host already runs a message loop, `MxPlotScriptHost` is
for hosts that have none — .NET 10 file-based apps (`dotnet run app.cs`), console tools, notebook
cells. `Run(script, configure?)` starts the Avalonia message loop, runs `script` off the UI thread,
and returns once every window the script opened has closed; `Start` offers a non-blocking variant
for REPL/notebook use (`PlatformNotSupportedException` on macOS, where AppKit requires the loop on
the process main thread). One `Run`/`Start` per process — Avalonia can only be initialized once.

```csharp
MxPlotScriptHost.Run(() =>
{
    var plotter = MxPlotScriptHost.Show(data, title: "My Data");
});
```

See [MatrixPlotter Basic Usage Guide § Scripting with MxPlotScriptHost](./MatrixPlotter_Usage_Guide.md#scripting-with-mxplotscripthost)
for the full API and the thread-affinity contract shared with `MxPlotHostApplication`.

---

## Plugin / Action Model

### `IPlotterAction`

Implement `IPlotterAction` to create interactive tools (crop, measure, annotate, etc.).

```csharp
public interface IPlotterAction : IDisposable
{
    event EventHandler? Completed;
    event EventHandler? Cancelled;
}
```

The active action is managed by `MatrixPlotter` internally. Only one action can be active at a time.  
When `Completed` fires, the action result is applied (e.g., cropped data replaces the current dataset).

### `IMatrixPlotterContext`

Passed to plugin code to give controlled access to the host plotter:

```csharp
public interface IMatrixPlotterContext
{
    IMatrixData Data { get; }
    double DisplayMinValue { get; set; }   // setting this also switches to fixed-range mode
    double DisplayMaxValue { get; set; }   // setting this also switches to fixed-range mode
    TopLevel? Owner { get; }
    IPlotWindowService WindowService { get; }
    int ActiveFrameIndex { get; }
    LookupTable? CurrentLut { get; }
    bool IsLutInverted { get; }
}
```

### `MatrixPlotterPluginRegistry`

Registers plugins and provides the `IPlotWindowService` used by `IMatrixPlotterContext`.

---

## Dependency Overview

```
MxPlot.UI.Avalonia
    ├── MxPlot.Core          (IMatrixData, MatrixData<T>, LookupTable, Axis, ...)
    └── Avalonia 11          (Window, Controls, Skia rendering)
```

`MxPlot.Core` has no dependency on Avalonia and can be used independently in headless/server scenarios.

---

## Custom Lookup Tables (LUTs)

`MatrixPlotter` uses `LookupTable` instances to map data values to colors. Several built-in themes are available via `ColorThemes` (Grayscale, Hot, Jet, Turbo, Viridis, etc.), but users can add custom LUTs in two ways:

### Method 1: External `.mlut` Files (Automatic Loading)

Place custom `.mlut` files in a `LUTs/` subdirectory next to the application executable.  
`LutSelector` automatically discovers and registers all `.mlut` files on first use.

**File Format:**

```
<LUT Name>
<Levels>[, <MissingColorHex>]
<R>, <G>, <B>
<R>, <G>, <B>
...
```

- **Line 1**: LUT name (displayed in the selector)
- **Line 2**: Number of color levels, optionally followed by a missing-value color in hexadecimal (e.g., `256, FF00FF`)
- **Lines 3+**: RGB triplets (0-255) or hex color values (e.g., `FF8800`)
- Empty lines and lines starting with `#` are ignored

**Example (`CustomHot.mlut`):**

```
CustomHot
256, 0000FF
0, 0, 0
128, 0, 0
255, 128, 0
255, 255, 128
255, 255, 255
```

If fewer color lines are provided than the declared level count, the last color is repeated.  
If more lines are provided, extra lines are ignored.

### Method 2: Programmatic Registration

Load and register a LUT at runtime:

```csharp
using MxPlot.Core.Imaging;

// From file
var lut = ColorThemes.LoadFromFile("path/to/custom.mlut");
if (lut != null)
    ColorThemes.Register(lut);

// Or create manually
int[] colors = new int[256];
for (int i = 0; i < 256; i++)
{
    int r = i;
    int g = i / 2;
    int b = 255 - i;
    colors[i] = r | (g << 8) | (b << 16);
}
var customLut = new LookupTable("MyCustomLUT", colors, missingColor: 0xFF00FF);
ColorThemes.Register(customLut);
```

---

## Related Documents

- [MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md) (includes [Scripting with MxPlotScriptHost](./MatrixPlotter_Usage_Guide.md#scripting-with-mxplotscripthost))
- [MatrixPlotter Composite Rendering Guide](./MatrixPlotter_Composite_Guide.md)
- [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md)
- [MatrixPlotter Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md)
