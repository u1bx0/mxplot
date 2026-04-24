# MxPlot.UI.Avalonia

**Cross-platform UI layer for the [MxPlot Ecosystem](https://github.com/u1bx0/mxplot) — built on [Avalonia UI](https://avaloniaui.net/).**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.UI.Avalonia?style=flat-square)](https://www.nuget.org/packages/MxPlot.UI.Avalonia)
[![Downloads](https://img.shields.io/nuget/dt/MxPlot.UI.Avalonia?style=flat-square)](https://www.nuget.org/packages/MxPlot.UI.Avalonia)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Avalonia](https://img.shields.io/badge/Avalonia-11-purple?style=flat-square)](https://avaloniaui.net/)

> 💡 **Recommendation**
> Most users should install the **[MxPlot](https://www.nuget.org/packages/MxPlot)** metapackage, which bundles this UI layer together with the core engine and I/O extensions.

---

<!-- TODO: Replace the placeholder below with an animated GIF showing MatrixPlotter in action.
     Suggested content: open a file → pan/zoom → overlay drawing → line profile window
     Tool: https://www.screentogif.com/ (free) — export at ~15 fps, keep under 10 MB.
     Then uncomment the line below: -->
<!-- ![MxPlot.UI.Avalonia Demo](https://raw.githubusercontent.com/u1bx0/mxplot/main/docs/images/demo.gif) -->

## 📦 About this Package

`MxPlot.UI.Avalonia` provides a fully featured, cross-platform visualization layer on top of `MxPlot.Core`.
It runs on **Windows, macOS, and Linux** via Avalonia UI and requires no WinForms or WPF dependency.

### Key Components

| Component | Description |
|---|---|
| `MatrixPlotter` | Standalone window: LUT selector, value-range bar, overlay manager, axis trackers, ortho views |
| `MxView` | Low-level Avalonia surface control — pan, zoom, bitmap rendering via SkiaSharp |
| `MxPlotHostApplication` | Minimal Avalonia `Application` for hosting MxPlot inside non-Avalonia apps (WinForms, console, etc.) |
| Overlay system | Interactive shapes drawn over the bitmap — created via right-click context menu or programmatically |
| `ProfilePlotter` | Live line-profile plot window, auto-updated on geometry change |
| `OverlayPropertyDialog` | Per-object pen/geometry editor |
| `OrthogonalPanel` | Side-by-side orthogonal slice views for 3-D / multi-frame data |

---

## 🚀 Quick Start

### 1. Install

```
dotnet add package MxPlot
```

Or reference `MxPlot.UI.Avalonia` directly if you only need the UI layer.

### 2. Show a matrix in one line

```csharp
using MxPlot.Core;
using MxPlot.UI.Avalonia.Views;

// Create a 512×512 float matrix and fill with your data
var data = new MatrixData<float>(512, 512);
// ... populate data.GetInternalArray(0) here ...

// Open a standalone viewer window
MatrixPlotter.Create(data, title: "My Data").Show();
```

### 3. Open from a file (with a format extension)

```csharp
using MxPlot.Core;
using MxPlot.Extensions.Tiff;        // MxPlot.Extensions.Tiff NuGet
using MxPlot.UI.Avalonia.Views;

var format = new OmeTiffFormat();
var data = MatrixData.Load("image.ome.tif", format);
MatrixPlotter.Create(data, title: "image.ome.tif").Show();
```

### 4. Link two plotters (shared data, synchronized refresh)

```csharp
var parent = MatrixPlotter.Create(originalData, title: "Original");
var child  = parent.CreateLinked(filteredData, title: "Filtered");
parent.Show();
child.Show();
// Calling parent.Refresh() or child.Refresh() updates both windows.
```

---

## 🎨 Overlay System

### Built-in (right-click context menu)

`MatrixPlotter` provides built-in overlay tools accessible from the **right-click context menu** on the image surface — no code required:

- **Line** — draws a line; right-click → *Plot Profile* opens a live `ProfilePlotter` window
- **Rectangle / Oval / Targeting** — region shapes; right-click → *Show Statistics* displays Min/Max/Mean
- **Text** — free text annotation
- All shapes support drag-move, handle-resize, snap modes, and a pen/color editor via double-click

### Programmatic creation

```csharp
using MxPlot.UI.Avalonia.Overlays.Shapes;

// Add a line overlay
var line = new LineObject(new Point(10, 10), new Point(200, 150));
plotter.MainView.OverlayManager.Add(line);

// Add a rectangular ROI
var roi = new RectObject(new Point(50, 50), 100, 80);
plotter.MainView.OverlayManager.Add(roi);
```

---

## 🔗 Using MxPlot.UI.Avalonia from WinForms or WPF

Embedding Avalonia UI controls inside a WinForms or WPF host requires additional setup.
Please refer to the integration guide in the repository:

👉 [Non-Avalonia Integration Guide](https://github.com/u1bx0/mxplot/blob/main/docs/NonAvalonia_Integration_Guide.md) *(documentation in progress)*

---

## 🖥️ Requirements

| | Minimum |
|---|---|
| .NET | 8.0 or 10.0 |
| Avalonia | 11.x |
| Platform | Windows / macOS / Linux |

---

## 📚 Documentation & Source

👉 **[GitHub Repository: u1bx0/MxPlot](https://github.com/u1bx0/mxplot)**

## 📊 Version History

For the complete changelog, please visit the [Releases Page](https://github.com/u1bx0/mxplot/releases) on GitHub.