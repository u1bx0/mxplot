# MxPlot.App

**A ready-to-use scientific data viewer built on the MxPlot ecosystem.**

[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-11.3-purple)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/license-MIT-green)](../../LICENSE)

> 📦 Pre-built binaries for **Windows x64** and **macOS (Apple Silicon)** are available on the [Releases page](https://github.com/u1bx0/mxplot/releases).

---
### 🎥 MxPlot.App Demo

<div align="center">
  <img src="../../docs/images/mxplot-app-demo.gif" width=800 alt="MxPlot.App Demo"> 
</div>
---

## What is MxPlot.App?

MxPlot.App is a **standalone desktop application** for exploring and analyzing multi-dimensional matrix data — built on top of [`MxPlot.Core`](../MxPlot.Core) and [`MxPlot.UI.Avalonia`](../MxPlot.UI.Avalonia).

It is more than a demo. While it naturally showcases `MatrixData<T>` and the `MatrixPlotter` window, MxPlot.App adds a **multi-window dashboard** with real-world workflow features: synchronized viewing, batch export, LUT & range editing, and an extensible plugin system. It is designed to be genuinely useful for daily data analysis — not just a code sample.

---

## Features at a Glance

### 📂 File Management
- **Drag & drop** files onto the dashboard to open them instantly
- Supports all formats registered via the plugin system: `.mxd`, `.ome.tif`, `.tif`, `.h5`, `.csv`, `.fits`, images including animated GIF / WebP, and more
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

### 🧮 Create Data
- **Create Data…** in the main menu builds a new window from a formula such as `sin(x) * cos(y)`; the grid and the axes are set in the dialog
- Add other open windows as references (`m1`, `m2`) to compute across them, e.g. `m1 - m2`; select two or more windows first to start with them as references
- After **Create** the button becomes **Update**: change the formula, references or grid and make the same window again until it is right, then **Close**
- **Keep updated** makes the result one frame that follows the reference windows. It is marked **fx** in the list, its inputs are tinted while it is selected, and **Edit Formula…** in its context menu changes it. It stops following when it or a reference is closed, and stays as an ordinary window
- For a formula of one window's own data, use **Processing > Calculator** in that window; with "Sync source data" the result follows its source, and **Data > View > Edit Formula…** changes the formula afterwards
- See the [Calculator and Formula Guide](../../docs/Calculator_Formula_Guide.md) for the syntax and examples

### 🔌 Plugin System
- Drop a `*.dll` implementing `IMxPlotAppPlugin` into a `plugins` folder — it appears in **Tools** automatically
- **Tools → Open Add-ons Folder** shows where plugins and file-format extensions (`plugins`) and custom LUTs (`LUTs`) go: next to `MxPlot.exe` on Windows, `~/Library/Application Support/MxPlot` on macOS
- Plugins receive the full `IMxPlotAppContext` (open datasets, selected datasets, window service)

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
| Calculator | New values from a formula of the window's data; optionally follows the source |
| History / Revert | Processing steps are recorded in the metadata; a crop that replaced the data, and display and scale settings, can be reverted |

---

## Getting Started

### Download (no installer)

Grab the latest archive from the [Releases page](https://github.com/u1bx0/mxplot/releases):

| Platform | File | Requirement |
|---|---|---|
| Windows x64 | `MxPlot-vX_Y_Z-win-x64.zip` | [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| macOS Apple Silicon | `MxPlot-vX_Y_Z-osx-arm64.zip` | None (the runtime is included) |

**Windows:** Extract the zip and double-click `MxPlot.exe`.

> `MxPlot.exe` is not code-signed. If **Smart App Control** is on (Windows 11), it may be
> blocked, and Windows offers no exception for individual apps. Running it then requires turning
> Smart App Control off (Windows Security > App & browser control), which lowers protection for
> the whole PC, so decide whether that is acceptable. See Microsoft's
> [Smart App Control FAQ](https://support.microsoft.com/en-us/windows/smart-app-control-frequently-asked-questions-285ea03d-fa88-4d56-882e-6698afdb7003),
> including when it can be turned on again.

**macOS:** The app is not notarized by Apple, so macOS may block it or report it as damaged.
Extract the zip, then do the following once, and again after each update:

1. Drag `MxPlot.app` into the **Applications** folder.
2. In Terminal, run `xattr -cr /Applications/MxPlot.app`
3. Open MxPlot from the Applications folder.

The same steps are in `READ ME FIRST - How to launch on macOS.txt` in the zip. Only do this for a copy
downloaded from the official [Releases page](https://github.com/u1bx0/mxplot/releases).

---

## Build from Source

```bash
git clone https://github.com/u1bx0/mxplot.git
cd mxplot/MxPlot
dotnet build MxPlot.App/MxPlot.App.csproj -c Release
```

Or open `MxPlot/MxPlot.slnx` in Visual Studio 2022/2026 and run `MxPlot.App`.

---

## Writing a Plugin

```csharp
using MxPlot.App.Plugins;
using MxPlot.UI.Avalonia.Views;

public class MyPlugin : IMxPlotAppPlugin
{
    public string CommandName => "My Analysis";
    public string Description => "Runs my custom analysis on the selected dataset.";

    public void Run(IMxPlotAppContext ctx)
    {
        var data = ctx.PrimarySelection;
        if (data == null) return;

        var result = MyAlgorithm.Process(data);
        MatrixPlotter.Create(result, title: "My Result").Show();
    }
}
```

Put the compiled `.dll` in a `plugins` folder (**Tools → Open Add-ons Folder**) and restart the app — the plugin appears under **Tools → My Analysis**.

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
