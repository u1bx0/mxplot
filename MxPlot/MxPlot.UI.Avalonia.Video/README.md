# MxPlot.UI.Avalonia.Video

**Video export plugins for MxPlot, and a small framework for writing more**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.UI.Avalonia.Video?include_prerelease&style=flat-square)](https://www.nuget.org/packages/MxPlot.UI.Avalonia.Video)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**MxPlot.UI.Avalonia.Video** provides frame-sequence video export plugins for the MxPlot Avalonia UI
(`AviExporter`, `Mp4Exporter`), plus `VideoExporterBase`/`IVideoFrameWriter` -- the reusable base
those two are themselves built on -- for adding further formats without reimplementing the settings
dialog or frame-rendering loop.

## Included exporters

- **`AviExporter`** — uncompressed 24-bit BGR AVI via [SharpAvi](https://github.com/baSSiLL/SharpAvi).
  Plays natively on Windows (Media Foundation / Video for Windows). No external dependency.
  **Correct DIB row padding**: each BGR24 row is padded to a 4-byte boundary as required by the
  AVI/DIB spec -- without this, widths not divisible by 4 produce error `0xC00D36B1`
  (`MF_E_UNSUPPORTED_FORMAT`) in Windows Media Foundation (Media Player, PowerPoint, etc.).
- **`Mp4Exporter`** — H.264 MP4 by piping raw frames into an external `ffmpeg` process. Plays
  natively on Windows *and* macOS (QuickTime included), unlike uncompressed AVI. Requires `ffmpeg`
  on `PATH`; check `Mp4Exporter.IsFfmpegAvailable()` before registering it, so the export menu never
  advertises a format that would just fail on a machine without ffmpeg installed:
  ```csharp
  MatrixPlotterPluginRegistry.AddExportPlugin(new AviExporter());
  if (Mp4Exporter.IsFfmpegAvailable())
      MatrixPlotterPluginRegistry.AddExportPlugin(new Mp4Exporter());
  ```

Both share **hyperstack support** (choosing which axis — Channel, Z, Time, … — to iterate when
exporting multi-dimensional data) via the same `VideoExportDialog`.

## Writing a new format: `VideoExporterBase` + `IVideoFrameWriter`

Every frame-sequence video export needs the same settings (animation axis, interval/fps, output
size, overlay toggle) and the same render-frame-then-write loop; only *how a frame gets written* to
the target container/codec actually differs. `VideoExporterBase` (implements `IRenderExportPlugin`)
owns the former; you only supply the latter via `IVideoFrameWriter`:

```csharp
public sealed class MyFormatExporter : VideoExporterBase
{
    public override string Label => "MyFormat…";
    public override string Hint => "Exports the frames as MyFormat";
    public override string FileTypeName => "MyFormat Video";
    public override string FilePattern => "*.myf";

    protected override string FormatLabel => "MyFormat";       // used in the dialog's title
    protected override bool SizeEstimateIsExact => false;      // false for anything compressed

    protected override IVideoFrameWriter CreateWriter(string path, int width, int height, decimal fps)
        => new MyFormatFrameWriter(path, width, height, fps);
}

public sealed class MyFormatFrameWriter : IVideoFrameWriter
{
    public void WriteFrame(byte[] bgra, int width, int height) { /* bgra is top-down BGRA32 */ }
    public void Finish() { /* called once, success path only, before Dispose */ }
    public void Dispose() { /* release resources regardless of outcome */ }
}
```

Register it the same way as the built-in exporters:

```csharp
MatrixPlotterPluginRegistry.AddExportPlugin(new MyFormatExporter());
```

See `AviExporter`/`AviFrameWriter` (SharpAvi, in-process) and `Mp4Exporter`/`FfmpegFrameWriter`
(external subprocess) for two working reference implementations with different shapes.

`IRenderHost.RenderFrameAsync` (what `VideoExporterBase`'s frame loop calls on your behalf) is
**thread-safe** and always returns top-down BGRA32; all UI-thread marshalling is handled internally
by the host, so an `IVideoFrameWriter` only needs to worry about its own format's pixel layout (row
order, padding, channel order) when converting from that.

## Dependencies

- [SharpAvi](https://github.com/baSSiLL/SharpAvi) — MIT license (used by `AviExporter` only)
- `ffmpeg` — external, user-installed, **not bundled**; only needed to use `Mp4Exporter`

## License

MIT License
