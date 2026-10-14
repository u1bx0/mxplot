# MxPlot.App.Plugins

**Plugin contract for extending MxPlot.App's Tools menu**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.App.Plugins?include_prerelease&style=flat-square)](https://www.nuget.org/packages/MxPlot.App.Plugins)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**MxPlot.App.Plugins** is the two-interface contract a DLL implements to add a command to
**MxPlot.App**'s ☰ → Tools menu: `IMxPlotAppPlugin` (the command itself) and `IMxPlotAppContext`
(what it sees of the running app — open/selected datasets, the main window, and the window-opening
service). MxPlot.App itself is not published as a NuGet package (it is the reference application, not
a library), so this package exists purely so a plugin project can compile against those two interfaces
without a local copy of MxPlot.App to reference.

## Writing a plugin

```csharp
using MxPlot.App.Plugins;
using MxPlot.Core.IO.Formats;

public sealed class BatchCsvExportPlugin : IMxPlotAppPlugin
{
    public string CommandName => "Batch CSV Export";
    public string Description => "Exports all open datasets to CSV.";

    public void Run(IMxPlotAppContext ctx)
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
```

`IMxPlotAppContext` also exposes `SelectedDatasets`, `PrimarySelection`, `Owner` (the main window, for
use as a dialog owner), `WindowService` (open a new `MatrixPlotter` window), and `OpenFileAsync`.

`IMxPlotAppPlugin` also has an optional `GroupName`: plugins sharing the same non-null group name are
collected under a shared submenu in the Tools menu instead of sitting at the top level.

## Project setup

```xml
<ItemGroup>
  <PackageReference Include="MxPlot" Version="0.6.0" />
  <PackageReference Include="MxPlot.App.Plugins" Version="0.6.0" />
</ItemGroup>
```

Target `net10.0` — a plugin DLL is loaded into MxPlot.App's own process (`Assembly.LoadFrom`), so it
must target the same framework MxPlot.App does. Build the DLL and drop it into MxPlot.App's `plugins`
folder; it is picked up on the next launch.

## License

MIT License
