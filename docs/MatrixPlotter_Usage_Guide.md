# MatrixPlotter — Basic Usage Guide

**Created**: 2026-04-22  
**Updated**: 2026-08-20  
**Target**: `MxPlot.UI.Avalonia` (Avalonia 11.3.x), .NET 8 / .NET 10

This guide covers opening a `MatrixPlotter` window and driving it from code — from an Avalonia
app, a WinForms/WPF host, or a plain console application.

---

## Contents

1. [Opening a Window](#opening-a-window)
2. [Host Setup](#host-setup)
3. [Refreshing the Display](#refreshing-the-display)
4. [Replacing the Data](#replacing-the-data)
5. [Controlling the Display from Code](#controlling-the-display-from-code)
6. [Scripting with MxPlotScriptHost](#scripting-with-mxplotscripthost)
7. [Thread Safety](#thread-safety)
8. [Linked Plotters](#linked-plotters)
9. [Events](#events)
10. [Export, Progress, Notices](#export-progress-notices)
11. [Checking State](#checking-state)
12. [Settings Resume](#settings-resume)

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
    sourcePath: @"C:\data.mxd"   // enables Save / ShouldConfirmClose
).Show();
```

The caller is responsible for calling `Show()` (or `ShowDialog()`).

`Create()` also builds the `MatrixPlotterViewModel` and assigns it as `DataContext`. **Use it
rather than `new MatrixPlotter()`** unless you intend to manage the ViewModel yourself — the
external-control properties described below require a ViewModel and throw
`InvalidOperationException` without one.

### `sourcePath` and close behaviour

| Value | Save | Close confirmation |
|---|---|---|
| `null` (default) | Always opens a file picker | **Shown** — data that has never been written anywhere counts as unsaved |
| A real file path | Direct overwrite | Shown when modified |
| Colon-prefixed sentinel, e.g. `":measurement"` | Always opens a file picker (`HasFile` is `false` for any path starting with `:`) | Shown when modified |

The sentinel form is intended for live acquisition: the data is worth protecting on close, but
there is no file to overwrite in place.

Set `plotter.SuppressCloseConfirmation = true` to close without prompting — what an unattended
script that closes its own windows wants.

---

## Host Setup

`MatrixPlotter` is an Avalonia `Window`, so Avalonia must be initialized once before any window
is created. What that means depends on the host.

### Avalonia application

Nothing extra. The application's own `AppBuilder` / `Application` is already in place — just
call `MatrixPlotter.Create(...).Show()`.

> Projects that already have an Avalonia `Application` (for example anything referencing
> `MxPlot.App`) should keep their existing startup code and **not** use `MxPlotHostApplication`.

### WinForms / WPF host

Initialize Avalonia once at startup, sharing the host's Win32 message pump:

```csharp
// Program.cs / App.OnStartup — call before any MatrixPlotter.Create()
using Avalonia;
using MxPlot.UI.Avalonia;

AppBuilder.Configure<MxPlotHostApplication>()
    .UseWin32()
    .UseSkia()
    .SetupWithoutStarting();
```

`MxPlotHostApplication` is a minimal `Avalonia.Application` that loads the Fluent theme and the
MxPlot styles and creates no main window. There is no static `MxPlotHost.Initialize()` helper —
the host picks its own backend through `AppBuilder`, as above.

For the full setup (package version pinning, keyboard forwarding, direct `MxView` embedding) see
the [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md).

### Console application or C# script

A console app has **no message pump of its own**, so `SetupWithoutStarting()` alone is not
enough — nothing would ever render. `MxPlotScriptHost` packages the whole arrangement:

```csharp
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia;

MxPlotScriptHost.Run(() =>
{
    var scan = new MatrixData<double>(256, 256);
    scan.Set((ix, iy, x, y) => Math.Sin(ix * 0.1) * Math.Cos(iy * 0.1));

    var main = MxPlotScriptHost.Show(scan, ColorThemes.Jet, "Scan");

    var filtered = Analyze(scan);                       // the windows stay responsive
    MxPlotScriptHost.Show(filtered, title: "Filtered");

    MxPlotScriptHost.Invoke(() => main.RangeMode = ValueRangeMode.All);
});   // returns once every window opened inside has been closed
```

See [Scripting with MxPlotScriptHost](#scripting-with-mxplotscripthost) for the full picture,
including what runs on which thread and the one-session-per-process rule.

The manual arrangement below is what `Run` does internally; reach for it only when the host needs
control the class does not offer.

```csharp
using Avalonia;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.UI.Avalonia;
using MxPlot.UI.Avalonia.Views;

AppBuilder.Configure<MxPlotHostApplication>()
    .UseWin32()
    .UseSkia()
    .SetupWithoutStarting();

var data = new MatrixData<double>(128, 128);
data.Set((ix, iy, x, y) => ix * iy);

var plotter = MatrixPlotter.Create(data);
plotter.Show();

var cts = new CancellationTokenSource();
plotter.Closed += (_, _) => cts.Cancel();

// Blocks until the window is closed.
Dispatcher.UIThread.MainLoop(cts.Token);
```

Two things to watch for:

- **Do not call blocking console APIs on the UI thread.** `Console.ReadLine()` invoked from a
  `Dispatcher.UIThread.Post(...)` callback freezes rendering and input until Enter is pressed.
  Wrap it as `await Task.Run(() => Console.ReadLine())` — the continuation resumes on the UI
  thread automatically, because Avalonia's `SynchronizationContext` is installed.
- **Work scheduled before `MainLoop` starts is fine.** `Dispatcher.UIThread.Post(...)` queues the
  callback; it runs once the loop begins pumping.

A complete working example lives in `MxPlotAppExamples/MxPlotConsoleApp1/Program.cs`.

---

## Refreshing the Display

Call `Refresh()` after modifying the underlying pixel data in place:

```csharp
// Modify data on any thread, then:
plotter.Refresh();   // safe to call from any thread
```

`Refresh()` re-derives the All-mode value range, rebuilds the bitmap from current `MatrixData`
values, re-runs overlay analysis and the histogram, and then fires the `Refreshed` event.

```csharp
plotter.Refresh(rebuildOrthogonalData: true);
```

Pass `rebuildOrthogonalData: true` when the pixel change also invalidates the orthogonal side
views (XZ / YZ slices and projections). The default (`false`) only redraws the existing bitmaps,
which is what a plain LUT or value-range change needs.

Use `Refresh()` — not data replacement — for a live feed whose shape does not change. Replacing
the data rebuilds the whole window chrome (trackers, menus, range bar) and resets overlay state.

---

## Replacing the Data

There are two supported routes to replace the displayed data:

**Via `ViewModel` (recommended for MVVM)**

```csharp
if (plotter.ViewModel is { } vm)
    vm.MatrixData = newData;
```

**Via `MainView.MatrixData` (code-behind)**

```csharp
plotter.MainView.MatrixData = newData;
```

Both paths trigger the same internal synchronisation: axis trackers are rebuilt, the frame index
is reset to 0, value-range mode is reapplied (Fixed mode is preserved; other modes revert to the
appropriate default), and the `MatrixDataChanged` event fires.

> **Note**: `BottomView.MatrixData` and `RightView.MatrixData` are managed by the
> orthogonal-view controller. Direct assignment to these views throws
> `InvalidOperationException` by design.

> The previous `IMatrixData` is disposed on replacement when its `RequiresDisposal` is `true`
> (Virtual / MMF-backed data). Do not keep using an instance after handing its replacement to the
> plotter.

---

## Controlling the Display from Code

### The three entry points

Display state can be written from three places, and **all three converge on the same state** —
this is covered by the automated tests in `Tests.MxPlot.UI.Avalonia.Headless`.

| # | Entry point | Example | When to use |
|---|---|---|---|
| ① | **Facade properties** on `MatrixPlotter` (`Lut`, `IsInvertedColor`, `LutDepth`, `RangeMode`, `IsFixedRange`, `FixedRange`) | `plotter.Lut = ColorThemes.Jet` | **Default choice.** Hides whether the state lives in the ViewModel, in `MxView`, or in the UI chrome; works from every host, including those with no ViewModel of their own |
| ② | `MatrixPlotter.ViewModel` | `plotter.ViewModel.Lut = ...` | MVVM binding, and tests that want to inspect ViewModel state directly |
| ③ | `MatrixPlotter.MainView` | `plotter.MainView.Lut = ...` | Properties that have no Facade yet (see below), and code that already holds an `MxView` |

Route ③ propagates back up to ① and ② through `MxView.RenderingPropertyChanged`, so a direct
`MainView` assignment no longer leaves the LUT selector or the range bar out of sync — that was
the main gap closed in 0.3.0.

Facade setters throw `InvalidOperationException` when the window has no
`MatrixPlotterViewModel` as its `DataContext` (i.e. it was built with `new MatrixPlotter()`
instead of `MatrixPlotter.Create(...)`). Getters return `null` / `default` in that state.

### LUT

```csharp
plotter.Lut = ColorThemes.Jet;
LookupTable? current = plotter.Lut;

plotter.IsInvertedColor = true;   // apply the LUT inverted
plotter.LutDepth = 64;            // quantize to 64 distinct colors (default 256)
```

`LutDepth` is passed through unclamped even though the level spinner in the settings panel offers
2–4096, so that it behaves identically to a direct `MainView.LutDepth` assignment. A value of 1 or
less means "use the LUT's own level count".

Custom LUTs are registered through `ColorThemes.Register(...)`, or dropped into a `LUTs/` folder
next to the executable — see the
[MxPlot.UI.Avalonia Overview](./MxPlotUIAvalonia_Overview.md#custom-lookup-tables-luts).

### Value range

The plotter has four value-range modes:

| `ValueRangeMode` | Range source |
|---|---|
| `Current` | Auto-scan of the displayed frame (shown as "Auto") |
| `All` | The whole stack |
| `Roi` | The overlay currently designated as the range source |
| `Fixed` | The explicit `FixedRange` bounds |

```csharp
plotter.RangeMode = ValueRangeMode.All;      // whole-stack range
plotter.RangeMode = ValueRangeMode.Fixed;
plotter.FixedRange = new RangeInfo(0, 4095);

RangeInfo r = plotter.FixedRange;            // r.Min, r.Max
```

A mode the current data cannot support is **downgraded rather than rejected**: `All` on
single-frame data, and `Roi` with no ROI overlay designated, both fall back to `Current`. Reading
`RangeMode` back reports the mode actually in effect, not the one that was requested.

`RangeInfo` is a `readonly record struct (double Min, double Max)`. It bundles the two bounds the
way `Window.Position` bundles X/Y, so assigning it applies both at once —
`ViewModel.ApplyFixedRange(min, max)` raises both change notifications only after both fields
have been written, so no listener ever observes a half-applied range.

> **`IsFixedRange` does not name the range mode.** It only distinguishes "the range is pinned"
> from "the range follows the current frame", so it reads `true` in Fixed, All *and* ROI mode,
> and `false` only in Current mode. Setting it to `true` while already in All or ROI mode is a
> no-op — it does **not** force Fixed. Setting it to `false` from any pinned mode returns to
> Current.
>
> `IsFixedRange` predates `RangeMode` and is kept because it mirrors `MxView.IsFixedRange`
> one-for-one. **Prefer `RangeMode`** in new code.

### Frame position

Frame navigation has **no Facade property by design**. `IMatrixData.ActiveIndex` is already the
canonical source of truth — it is bounds-checked and raises its own `ActiveIndexChanged`:

```csharp
plotter.MatrixData!.ActiveIndex = 12;          // flat frame index
```

Out-of-range values throw `IndexOutOfRangeException` rather than being silently clamped.

To move along a named axis instead of a flat index, set the axis index — this is exactly what the
on-screen slider does internally:

```csharp
var z = plotter.MatrixData!.Axes.FindAxis("Z");   // case-insensitive since 0.3.0
if (z != null) z.Index = 5;
```

`MainView.FrameIndex` also works and up-syncs to `ActiveIndex`, but the two routes above are the
preferred entry points.

> Driving this from `MxPlotScriptHost`? Both writes above need `Invoke` once `data` is attached to
> an open plotter — see [Thread Safety](#thread-safety).

Axis sliders themselves are reachable when their events are needed (drag start/end, animation):

```csharp
AxisTracker? tracker = plotter.GetAxisTracker("Z");
```

### Orthogonal views

```csharp
plotter.SetOrthogonalView("Z");             // activate XZ / YZ side views on axis "Z"
plotter.SetOrthogonalView(null);            // deactivate
string? axis = plotter.OrthogonalViewAxisName;
```

Equivalent to toggling the 🧊 freeze button on the corresponding `AxisTracker`. Side views
activate automatically for data with 3 or more axes.

### Properties without a Facade yet

Composite state has no Facade yet, and is reachable only through route ③:

| Property | Route |
|---|---|
| `RenderingMode` (`Lut` / `Composite` / `ColorCoded`) | `plotter.MainView.RenderingMode = ...` |
| `CompositeRecipes` / `CompositeBlendMode` / `CompositeFrameIndices` | `plotter.MainView.CompositeRecipes = ...` |

These are the low-level rendering contract only. The orchestration `MatrixPlotter` performs when
the user switches into Composite mode — promoting the axis to a `ColorChannel`, building default
recipes, pinning `ActiveIndex`, rebuilding the header and settings panel — is `internal`, so
assigning these wholesale is not equivalent to entering the mode. See the
[Composite Rendering Guide](./MatrixPlotter_Composite_Guide.md).

---

## Scripting with MxPlotScriptHost

`MxPlotScriptHost` (in `MxPlot.UI.Avalonia`) runs MxPlot windows from a program that has no UI of
its own — a C# script (`dotnet run app.cs`), a console tool, or a notebook cell. It owns the two
chores that otherwise leak into every such program: keeping a message loop alive, and making sure
each window is only touched from the thread that owns it.

> **Not for WinForms, WPF, or Avalonia applications.** Those already have a message loop, and a
> second one breaks Avalonia's thread affinity. `Run` and `Start` detect an application that is
> already initialized and throw. Use the arrangement in the
> [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md) instead.

### `Run` — the portable entry point

```csharp
#:package MxPlot@0.3.0
#:package Avalonia.Desktop@11.3.18
#:package Tmds.DBus.Protocol@0.92.0

using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia;
using MxPlot.UI.Avalonia.Controls;

MxPlotScriptHost.Run(() =>
{
    var scan = LoadScan();
    var main = MxPlotScriptHost.Show(scan, ColorThemes.Jet, "Scan");

    var filtered = Analyze(scan);                   // blocking work is fine here
    MxPlotScriptHost.Show(filtered, title: "Filtered");

    MxPlotScriptHost.Invoke(() => main.RangeMode = ValueRangeMode.All);
});
```

The lambda runs **off the UI thread**, so it may block, compute, or sleep freely without freezing
the windows. `Run` returns once every window opened through `Show` has been closed — or
immediately, if the script opened none. An exception inside the script is rethrown to the caller
after the loop has been shut down.

Which thread ends up running what differs per platform, deliberately:

| Platform | Message loop | Script body |
|---|---|---|
| Windows | a new STA thread | the calling thread |
| macOS, Linux | the calling thread | a worker thread |

macOS requires the event loop to own the process's main thread (an AppKit rule), while Windows
needs it on an STA thread that a top-level-statement program does not provide. Either way the
script body runs somewhere that is not the UI thread, so **the contract seen by script code is the
same everywhere**.

### `Start` — non-blocking, where `Run` does not fit

A REPL or notebook executes cell by cell and cannot wrap everything in a lambda:

```csharp
MxPlotScriptHost.Start();
var plotter = MxPlotScriptHost.Show(data, title: "Live");
// … a later cell …
MxPlotScriptHost.Invoke(() => plotter.FixedRange = new RangeInfo(0, 4095));
// … eventually …
MxPlotScriptHost.Shutdown();
```

`Start` runs the loop on a background thread and returns immediately. **It is not available on
macOS** — the main thread is exactly what it declines to take — and throws
`PlatformNotSupportedException` there. Prefer `Run` if portability matters.

By default, closing the last window also stops the loop. Pass `exitWhenAllWindowsClosed: false` to
keep it alive so that further windows can be opened later.

### Touching a window from the script

Everything on a `MatrixPlotter` — **including property reads** — reaches Avalonia's thread-affine
property system, so it must happen on the UI thread:

```csharp
var plotter = MxPlotScriptHost.Show(data);

MxPlotScriptHost.Invoke(() => plotter.Lut = ColorThemes.Viridis);       // write
var lutName = MxPlotScriptHost.Invoke(() => plotter.Lut?.Name);         // read — also via Invoke
```

The `IMatrixData` itself is **not** thread-affine, because `MxPlot.Core` has no dependency on
Avalonia. Reading the model directly from the script thread is fine:

```csharp
int frames = data.FrameCount;      // no Invoke needed
var (min, max) = data.GetValueRange();
```

**Except `ActiveIndex` and `Axis.Index`, once the data is attached to an open plotter.** A
`MatrixPlotter` subscribes to `data.ActiveIndexChanged` and forwards it straight into
`MxView.FrameIndex`, which *is* thread-affine — so changing the displayed frame this way reaches
the UI thread synchronously, even though the write itself is a plain `MxPlot.Core` property:

```csharp
MxPlotScriptHost.Invoke(() => data["Z"]!.Index = 5);   // needed — see Thread Safety below
```

Everything else on `IMatrixData` (`Set`, `ForEach`, `GetValueRange`, …) is unaffected either way,
because pixel edits don't raise an event a plotter listens to.

### Things to know

**One session per process.** Avalonia can only be initialized once, so `Run` and `Start` may each
be called once, and not after one another. Open every window from inside a single session; a
second attempt throws with an explanation.

**Windows ask to save on close.** Data handed to a plotter counts as unsaved until it is written
somewhere, so closing prompts even without a `sourcePath`. An unattended script that closes its
own windows should pass `confirmOnClose: false` to `Show`, or it will stall on the dialog.

**Platform backend.** `Run` and `Start` call `UsePlatformDetect()` by default, which needs the
`Avalonia.Desktop` package referenced by the script. To pick a backend explicitly — or to avoid
that package — pass a `configure` callback:

```csharp
MxPlotScriptHost.Run(script, configure: b => b.UseWin32().UseSkia());
```

### Verifying on your platform

`MxPlot/Samples/ScriptHostCheck.cs` is a self-checking harness that exercises both entry points
and prints a PASS/FAIL report. It needs a desktop session — the windows it opens close themselves
after about a second — and, because of the one-session rule, runs one mode per invocation:

```bash
dotnet run Samples/ScriptHostCheck.cs -- run     # portable entry point
dotnet run Samples/ScriptHostCheck.cs -- start   # non-blocking entry point
dotnet run Samples/ScriptHostCheck.cs -- reuse   # error paths
```

Exit code 0 means every check in that mode passed.

---

## Thread Safety

| API | Thread |
|---|---|
| `Refresh()`, `SetNotice()`, `BeginProgress()`, `EndProgress()` | Any — they dispatch internally |
| Facade properties (`Lut`, `IsInvertedColor`, `LutDepth`, `RangeMode`, `IsFixedRange`, `FixedRange`) | UI thread |
| `ViewModel.*`, `MainView.*`, `MatrixData.ActiveIndex`, `Axis.Index` | UI thread |
| `CaptureThumbnail()`, `ExportAsPng()` | UI thread |

Anything not marked "any" must be marshalled:

```csharp
await Dispatcher.UIThread.InvokeAsync(() =>
{
    plotter.IsFixedRange = true;
    plotter.FixedRange = new RangeInfo(0, 4095);
});
```

Pixel data itself may be produced on a background thread; only the hand-off to the plotter needs
the UI thread.

---

## Linked Plotters

Use `LinkRefresh` to keep two plotters synchronized. When either calls `Refresh()`, the other
refreshes automatically. This matters when both windows share the same `T[]` frame buffers, since
a `ValueRange` invalidation on one must be reflected in the other.

```csharp
var child = plotter.CreateLinked(
    derivedData,
    lut: ColorThemes.Plasma,
    title: "Derived View"
);
child.Show();
```

Pass `linkRefresh: false` to `CreateLinked` when the child recomputes its own content instead of
sharing buffers.

Or manually:

```csharp
plotter.LinkRefresh(otherPlotter);
// ...
plotter.UnlinkRefresh(otherPlotter);
```

When the parent closes, all linked children are closed automatically.

> `LinkRefresh` is a peer-to-peer refresh relay. It is unrelated to the internal `LinkedView`
> mechanism that drives live-derived windows (Spatial Filter sync, Log Transform sync, orthogonal
> live extracts), which is not part of the public API. The public `LinkedSource` /
> `LinkedSourceExcludedAxes` properties that existed up to 0.2.0 were removed in 0.3.0.

---

## Events

| Event | Description |
|---|---|
| `Refreshed` | Fired after `Refresh()` or a content change — used for linked plotter sync |
| `ViewUpdated` | Fired after each bitmap render (LUT, zoom, range change) — use for thumbnails |
| `MatrixDataChanged` | Fired when the `IMatrixData` instance is replaced |
| `IsModifiedChanged` | Fired when the modified flag changes |

```csharp
plotter.ViewUpdated += (_, _) =>
{
    var thumb = plotter.CaptureThumbnail(maxSize: 64);
    // update preview UI
};
```

`MxView.BitmapRefreshed` is `internal`; `ViewUpdated` forwards the same signal and is the public
way to observe renders. For rendering-property changes, subscribe to
`plotter.MainView.RenderingPropertyChanged`.

---

## Export, Progress, Notices

### Export

```csharp
// Full-resolution PNG export (UI thread required)
plotter.ExportAsPng(@"C:\output.png");

// Thumbnail bitmap (UI thread required)
Bitmap? thumb = plotter.CaptureThumbnail(maxSize: 128);

// Programmatic save / duplicate
await plotter.SaveAsAsync(@"C:\output.ome.tif");
var clone = await plotter.DuplicateAsync(show: true);
```

Additional entries in the "Export as…" submenu can be injected through `plotter.ExportFormats`
(`ExportFormatDescriptor`).

### Progress indicator

```csharp
var progress = plotter.BeginProgress("Saving…", blockInput: true);
// pass to an IProgressReportable writer, or use directly:
// progress.Report(-total);   // declare total steps
// progress.Report(i);        // update step i
await Task.Run(() => DoWork(progress));
plotter.EndProgress();
```

Protocol:
- `Report(-N)` — declare N total steps (switches to determinate mode)
- `Report(i)` — completed step i (0-based)
- Call `EndProgress()` when done

Pass a `CancellationTokenSource` as the third argument to show a cancel affordance for
long-running work.

### Status bar notice

```csharp
plotter.SetNotice("ROI: 128 × 128 px");  // persistent
plotter.SetNotice(null);                  // clear
```

For temporary messages (auto-fade), the toast is shown internally (e.g., after clipboard copy).

### Sync border

Used to visually group synchronized windows:

```csharp
plotter.SetSyncBorder(Brushes.DodgerBlue);
plotter.SetSyncBorder(null); // remove
```

---

## Checking State

```csharp
bool hasFile     = plotter.HasFile;            // backed by a real file path
bool isModified  = plotter.IsModified;         // data changed since last open/save
bool shouldAsk   = plotter.ShouldConfirmClose; // !IsSecondaryWindow && IsModified
IMatrixData? data = plotter.MatrixData;        // currently displayed data

plotter.SuppressCloseConfirmation = true;      // close without prompting
plotter.DiscardChanges();                      // clear the modified flag
```

---

## Settings Resume

When `ResumeSettingsEnabled` is `true`, display settings (LUT, value range, axis position, frozen
axis) are carried over when the displayed data is replaced:

```csharp
plotter.ResumeSettingsEnabled = true; // default: false
```

Axis indices are clamped to the new data's range, and the frozen (Volume) axis is re-applied when
an axis of the same name exists. This is what you want when a plotter is repeatedly updated with
new scan results and the user's view configuration should survive each update.

---

## Related Documents

- [MxPlot.UI.Avalonia Overview](./MxPlotUIAvalonia_Overview.md)
- [MatrixPlotter Composite Rendering Guide](./MatrixPlotter_Composite_Guide.md)
- [WinForms / WPF Integration Guide](./MatrixPlotter_NonAvalonia_Integration_Guide.md)
- [MatrixPlotter Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md)
- [MxView Coordinate Systems Guide](./MxView_CoordinateSystems_Guide.md)
