# MatrixPlotter — Basic Usage Guide

**Created**: 2026-04-22  
**Updated**: 2026-04-22  
**Target**: `MxPlot.UI.Avalonia` (Avalonia 11.3.x), .NET 8 / .NET 10

---

## Opening a Window

The simplest way to display `IMatrixData` is `MatrixPlotter.Create()`:

```csharp
using MxPlot.Core;
using MxPlot.UI.Avalonia.Views;

IMatrixData data = ...; // MatrixData<float>, MatrixData<ushort>, etc.

MatrixPlotter.Create(data).Show();
```

With options:

```csharp
MatrixPlotter.Create(
    data,
    lut: ColorThemes.Jet,        // initial LUT (default: Grayscale)
    title: "My Result",          // window title
    sourcePath: @"C:\data.mxd"  // enables Save / ShouldConfirmClose
).Show();
```

The caller is responsible for calling `Show()` (or `ShowDialog()`).

---

## Initialization in Non-Avalonia Apps

If the host application is WinForms, WPF, or a console app, initialize Avalonia once before showing any window:

```csharp
// Program.cs or startup code — call before any MatrixPlotter.Create()
MxPlot.UI.Avalonia.MxPlotHost.Initialize();
```

For detailed setup see [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md).

---

## Refreshing the Display

Call `Refresh()` after modifying the underlying pixel data:

```csharp
// Modify data on any thread, then:
plotter.Refresh();   // safe to call from any thread
```

`Refresh()` rebuilds the bitmap from current `MatrixData` values, then fires the `Refreshed` event.

---

## Replacing the Data

Assign new data through the `ViewModel`:

```csharp
if (plotter.ViewModel is { } vm)
    vm.MatrixData = newData;
```

This triggers `SetMatrixData` internally, which rebuilds axis trackers, resets frame index, and reapplies value range defaults.

---

## Linked Plotters

Use `LinkRefresh` to keep two plotters synchronized. When either calls `Refresh()`, the other refreshes automatically.

```csharp
var child = plotter.CreateLinked(
    derivedData,
    lut: ColorThemes.Plasma,
    title: "Derived View"
);
child.Show();
```

Or manually:

```csharp
plotter.LinkRefresh(otherPlotter);
// ...
plotter.UnlinkRefresh(otherPlotter);
```

When the parent closes, all linked children are closed automatically.

---

## Events

| Event | Description |
|---|---|
| `Refreshed` | Fired after `Refresh()` or `SetMatrixData()` — used for linked plotter sync |
| `ViewUpdated` | Fired after each bitmap render (LUT, zoom, range change) — use for thumbnails |
| `MatrixDataChanged` | Fired when `IMatrixData` instance is replaced |
| `IsModifiedChanged` | Fired when the modified flag changes |

```csharp
plotter.ViewUpdated += (_, _) =>
{
    var thumb = plotter.CaptureThumbnail(maxSize: 64);
    // update preview UI
};
```

---

## Value Range Control

Value range mode is set programmatically via `ViewModel.Lut` or through the inline settings panel.

To read current display range from plugin code:

```csharp
var ctx = plotter.CreatePluginContext();
double min = ctx.DisplayMinValue;
double max = ctx.DisplayMaxValue;

// Force a fixed range
ctx.DisplayMinValue = 0;
ctx.DisplayMaxValue = 4095;
```

---

## Export

```csharp
// Full-resolution PNG export (UI thread required)
plotter.ExportAsPng(@"C:\output.png");

// Thumbnail bitmap (UI thread required)
Bitmap? thumb = plotter.CaptureThumbnail(maxSize: 128);
```

---

## Progress Indicator

```csharp
var progress = plotter.BeginProgress("Saving…", blockInput: true);
// pass to IProgressReportable writer, or use directly:
// progress.Report(-total);   // declare total steps
// progress.Report(i);        // update step i
await Task.Run(() => DoWork(progress));
plotter.EndProgress();
```

Protocol:
- `Report(-N)` — declare N total steps (switches to determinate mode)
- `Report(i)` — completed step i (0-based)
- Call `EndProgress()` when done

---

## Status Bar Notice

```csharp
plotter.SetNotice("ROI: 128 × 128 px");  // persistent
plotter.SetNotice(null);                  // clear
```

For temporary messages (auto-fade), the toast is shown internally (e.g., after clipboard copy).

---

## Sync Border

Used to visually group synchronized windows:

```csharp
plotter.SetSyncBorder(Brushes.DodgerBlue);
plotter.SetSyncBorder(null); // remove
```

---

## Checking State

```csharp
bool hasFile     = plotter.HasFile;           // backed by a real file path
bool isModified  = plotter.IsModified;        // data changed since last open/save
bool shouldAsk   = plotter.ShouldConfirmClose; // HasFile && IsModified
IMatrixData? data = plotter.MatrixData;       // currently displayed data
```

---

## Related Documents

- [MxPlot.UI.Avalonia Overview](./MxPlotUIAvalonia_Overview.md)
- [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md)
- [MatrixPlotter Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md)
