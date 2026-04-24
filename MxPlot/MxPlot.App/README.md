# MxPlot.App

**A ready-to-use scientific data viewer built on the MxPlot ecosystem.**

[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.3-purple)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

> 📦 Pre-built binaries for **Windows x64** and **macOS (Apple Silicon)** are available on the [Releases page](https://github.com/u1bx0/mxplot/releases).

---

<!-- TODO: Replace with an animated GIF showing the full workflow.
     Suggested content: drag-drop a file to dashboard, open multiple windows,
     Tile, Sync, draw overlay, export PNG.
     Tool: https://www.screentogif.com/ — ~15 fps, keep under 10 MB
     Then uncomment: -->
<!-- ![MxPlot.App Demo](https://raw.githubusercontent.com/u1bx0/mxplot/main/docs/images/mxplot_app_demo.gif) -->

---

## What is MxPlot.App?

MxPlot.App is a **standalone desktop application** for exploring and analyzing multi-dimensional matrix data — built on top of [`MxPlot.Core`](../MxPlot.Core) and [`MxPlot.UI.Avalonia`](../MxPlot.UI.Avalonia).

It is more than a demo. While it naturally showcases `MatrixData<T>` and the `MatrixPlotter` window, MxPlot.App adds a **multi-window dashboard** with real-world workflow features: synchronized viewing, batch export, LUT & range editing, and an extensible plugin system. It is designed to be genuinely useful for daily data analysis — not just a code sample.

---

## Features at a Glance

### 📂 File Management
- **Drag & drop** files onto the dashboard to open them instantly
- Supports all formats registered via the plugin system: `.mxd`, `.ome.tif`, `.tif`, `.h5`, `.csv`, `.fits`, and more
- Large file (>500 MB) dialog automatically offers **Virtual (MMF) loading** to keep memory usage low

### 🪟 Multi-Window Dashboard

<!-- TODO: screenshot of the dashboard with multiple windows listed -->
<!-- ![Dashboard](https://raw.githubusercontent.com/u1bx0/mxplot/main/docs/images/dashboard.png) -->

- **Window list** shows all open `MatrixPlotter` windows with thumbnail previews
- Toggle between **Details** and **Icon** grid views; resize thumbnails with Ctrl+scroll
- Click to activate, double-click to show/hide
- **Rename** any window inline

### 🔲 Tile Layout
- Select multiple windows and click **Tile** to arrange them across the screen
- Smart aspect-ratio-aware layout automatically uses the space beside the dashboard

<!-- TODO: GIF of Tile in action -->
<!-- ![Tile](https://raw.githubusercontent.com/u1bx0/mxplot/main/docs/images/tile.gif) -->

### 🔗 Synchronized Viewing
- Select two or more `MatrixPlotter` windows and click **Sync** to lock them together
- LUT change, value range, frame navigation, or crop in one window is **instantly mirrored** across all synced windows
- A **Revert** button appears if the synced group diverges from the original state
- Click **Unsync** to detach

<!-- TODO: GIF showing Sync across two windows -->
<!-- ![Sync](https://raw.githubusercontent.com/u1bx0/mxplot/main/docs/images/sync.gif) -->

### 🖼️ Batch PNG Export
- Select any number of windows and export all as PNG in one step
- Output filenames are derived from window titles (existing extensions stripped automatically)
- Overwrite confirmation dialog lists all affected files

### 📋 Open from Clipboard
- Paste any image from the clipboard directly as a new `MatrixData<byte>` window
- Choose between **Grayscale** or **Color Channels** (R/G/B decomposed)

### 🔌 Plugin System
- Drop a `*.dll` implementing `IMxPlotAppPlugin` next to `MxPlot.exe` — it appears in **Tools** automatically
- Plugins receive the full `IMxPlotContext` (open datasets, selected datasets, window service)

### 🎛️ Per-Window MatrixPlotter Features
Each open window is a full `MatrixPlotter` instance from `MxPlot.UI.Avalonia`:

| Feature | Description |
|---|---|
| LUT selector | Grayscale, Hot, Cold, Jet, Plasma, Viridis, HiLo, and more |
| Value range bar | Manual min/max, Auto, Full, ROI-based modes |
| Overlay tools | Line, Rectangle, Oval, Targeting, Text — statistics and live profile plots |
| Orthogonal views | XZ / YZ side panel for 3D / multi-frame data |
| Save As | Write back to `.mxd`, `.ome.tif`, or any registered format |
| Export PNG | Save current view as a full-resolution PNG |
| Duplicate | Open an independent deep copy in a new window |
| Undo / History | Step back through data modifications |

---

## Getting Started

### Download (no installation required)

Grab the latest archive from the [Releases page](https://github.com/u1bx0/mxplot/releases):

| Platform | File |
|---|---|
| Windows x64 | `MxPlot-win-x64.zip` |
| macOS Apple Silicon | `MxPlot-osx-arm64.tar.gz` |

**Windows:** Extract and double-click `MxPlot.exe`.

**macOS:** Extract, then run from Terminal:

```sh
xattr -rd com.apple.quarantine MxPlot-osx-arm64/
MxPlot-osx-arm64/run.sh
```

> The macOS binary is unsigned. `xattr` removes the Gatekeeper quarantine flag.

---

## Build from Source

```bash
git clone https://github.com/u1bx0/mxplot.git
dotnet build MxPlot.App/MxPlot.App.csproj -c Release
```

Or open `MxPlot.sln` in Visual Studio 2022/2026 and run `MxPlot.App`.

---

## Writing a Plugin

```csharp
using MxPlot.App.Plugins;
using MxPlot.UI.Avalonia.Views;

public class MyPlugin : IMxPlotAppPlugin
{
    public string CommandName => "My Analysis";
    public string Description => "Runs my custom analysis on the selected dataset.";

    public void Run(IMxPlotContext ctx)
    {
        var data = ctx.PrimarySelection;
        if (data == null) return;

        var result = MyAlgorithm.Process(data);
        MatrixPlotter.Create(result, title: "My Result").Show();
    }
}
```

Drop the compiled `.dll` next to `MxPlot.exe` — the plugin appears under **Tools → My Analysis** automatically.

---

## Architecture

MxPlot.App is intentionally kept as a thin shell on top of the library layer:

```
MxPlot.App
  └── Dashboard (MxPlotAppWindow + ViewModel)
        ├── MatrixPlotter  ←  MxPlot.UI.Avalonia
        │     └── MatrixData<T>  ←  MxPlot.Core
        └── Plugin system  ←  IMxPlotAppPlugin
```

All the heavy lifting — rendering, LUT, overlays, I/O, virtual loading — is handled by `MxPlot.UI.Avalonia` and `MxPlot.Core`. MxPlot.App adds the multi-window orchestration layer on top.

---

## Related

- [MxPlot.UI.Avalonia](../MxPlot.UI.Avalonia) — visualization library (NuGet)
- [Root README](../../README.md) — full ecosystem overview