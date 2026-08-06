# MxPlot.UI.Avalonia.Video

**AVI video export plugin for MxPlot**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.UI.Avalonia.Video?include_prerelease&style=flat-square)](https://www.nuget.org/packages/MxPlot.UI.Avalonia.Video)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**MxPlot.UI.Avalonia.Video** provides an AVI video export plugin for the MxPlot Avalonia UI.
It also serves as a reference implementation of `IRenderExportPlugin`.

## Features

- **AVI export**: Exports rendered frame sequences (LUT and overlays applied) as uncompressed 24-bit AVI video.
- **Hyperstack support**: Lets the user choose which axis (Channel, Z, Time, …) to iterate when exporting multi-dimensional data.
- **Correct DIB row padding**: Each BGR24 row is padded to a 4-byte boundary as required by the AVI/DIB spec.  
  Without this, widths not divisible by 4 produce error `0xC00D36B1` (`MF_E_UNSUPPORTED_FORMAT`) in Windows Media Foundation (Media Player, PowerPoint, etc.).
- **Reference plugin implementation**: `AviExporter` is a minimal, well-commented example of how to implement `IRenderExportPlugin`.

## Plugin overview

Plugins implement `IRenderExportPlugin` and are registered at application startup:

```csharp
MatrixPlotterPluginRegistry.AddExportPlugin(new AviExporter());
```

The interface has two responsibilities:

1. **Metadata** — labels, file type name, file glob, and whether the plugin requires multi-frame data (`RequiresStack`).
2. **`ExportAsync`** — shows a settings dialog, then loops over frames and writes them using `IRenderHost.RenderFrameAsync`.

`IRenderHost.RenderFrameAsync` is **thread-safe**; it may be called directly from a background `Task`.
All UI-thread marshalling is handled internally by the host.

```csharp
await Task.Run(async () =>
{
    for (int i = 0; i < frameCount; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bgra = await host.RenderFrameAsync(frameIndex, size, withOverlay);
        // write bgra bytes to the output format …
        progress?.Report((i + 1) * 100 / frameCount);
    }
});
```

## DIB padding note

SharpAVI does **not** insert DIB row padding automatically.  
For AVI output, each BGR24 row must be padded to a multiple of 4 bytes:

```csharp
int rowStride = (width * 3 + 3) & ~3;
```

Failure to do so causes playback errors in Windows Media Foundation when `width % 4 != 0`.

## Dependencies

- [SharpAvi](https://github.com/baSSiLL/SharpAvi) — MIT license

## License

MIT License
