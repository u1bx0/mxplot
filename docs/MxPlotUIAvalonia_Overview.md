# MxPlot.UI.Avalonia — Overview

**Created**: 2026-04-22  
**Updated**: 2026-04-22  
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
| Plugin actions | `IPlotterAction` / `IMatrixPlotterContext` for tool extensions |

**Factory method:**

```csharp
MatrixPlotter.Create(data, lut: ColorThemes.Jet, title: "My Data").Show();
```

### `MxView` (`Controls/MxView.axaml.cs`)

The core rendering control. Converts `IMatrixData` frames to ARGB bitmaps via `BitmapWriter<T>` and a `LookupTable`.

Key properties:
- `MatrixData` — the data source
- `FrameIndex` — currently displayed frame
- `Lut` — active `LookupTable`
- `IsFixedRange` / `FixedMin` / `FixedMax` — range override
- `Zoom`, `IsFitToView` — display transform
- `OverlayManager` — manages overlay objects

Key events:
- `BitmapRefreshed` — fired after each render
- `AutoRangeComputed` — fired with frame min/max after Auto render
- `ScrollStateChanged` — zoom/pan changes

### `OrthogonalPanel` and `OrthogonalViewController`

`OrthogonalPanel` is a grid layout containing three `MxView` instances:
- `MainView` — XY plane (primary)
- `RightView` — XZ plane (Z along horizontal)
- `BottomView` — ZX plane (Z along vertical)

`OrthogonalViewController` wires the three views together:
- Synchronizes `FrameIndex` and render settings across all views
- Manages the XY-projection window (Z-axis depth display)

Orthogonal mode is activated automatically when `MatrixData` has 3+ axes.

### `ProfilePlotter`

A secondary window opened from `MatrixPlotter` for line-profile analysis.  
Displays pixel value along a `LineObject` overlay as a 1D chart.  
Multiple profiles can be open simultaneously and are automatically closed when the parent `MatrixPlotter` closes.

### `MxPlotHost` (`MxPlotHost.cs`)

A static helper for initializing Avalonia in non-Avalonia host applications (WinForms, WPF, console).

```csharp
// Call once at application startup
MxPlotHost.Initialize();
```

See [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md) for details.

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
    double DisplayMinValue { get; set; }
    double DisplayMaxValue { get; set; }
    TopLevel? Owner { get; }
    IPlotWindowService WindowService { get; }
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

## Related Documents

- [MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md)
- [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md)
- [MatrixPlotter Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md)
