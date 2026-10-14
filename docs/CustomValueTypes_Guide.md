# Custom Value Types Guide

**MxPlot.Core — Element Types and Value Modes**

> Last Updated: 2026-09-27 (0.6.0-dev2)
> Status: **Core API stable; UI support for custom structs is partial** (see [UI Support](#ui-support))

## 📚 Table of Contents

1. [Overview](#overview)
2. [Element Types and Value Modes](#element-types-and-value-modes)
3. [Complex (Built-in)](#complex-built-in)
4. [Registering a Custom Struct](#registering-a-custom-struct)
5. [The Functions Must Not Keep State](#the-functions-must-not-keep-state)
6. [Templates](#templates)
7. [Using a Registered Struct](#using-a-registered-struct)
8. [Processing Structured Data](#processing-structured-data)
9. [Saving and Loading (.mxd)](#saving-and-loading-mxd)
10. [UI Support](#ui-support)
11. [Checklist and API Summary](#checklist-and-api-summary)

---

## Overview

`MatrixData<T>` can hold any **unmanaged** type `T`, not just primitive numbers. Every element type falls into one
of three groups:

| Group | Types | What you do |
|---|---|---|
| Built-in primitives | `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double` | Nothing — use them directly |
| Built-in structured type | `System.Numerics.Complex` | Nothing — registered by the library, with five value modes |
| Custom structs | Any other unmanaged struct | **Register it once** before creating the first instance |

A structured element (a `Complex`, a vector, a multi-channel pixel) has no single "value". It can be *read* in
several ways — its magnitude, its real part, one of its components. Each of these readings is a **value mode**.
Displaying, histogramming, profiling and most processing all need a plain `double`, so they read the element in
one of its value modes.

```csharp
// ✅ Unmanaged: every field is a value type
public struct Velocity2D
{
    public float Vx;
    public float Vy;
}

// ❌ Not unmanaged: contains a reference
public struct Labeled
{
    public double Value;
    public string Label;   // managed reference — not allowed
}
```

---

## Element Types and Value Modes

Every `MatrixData<T>` describes its element type through `ElementType`:

```csharp
IMatrixData data = ...;
ElementType et = data.ElementType;

et.Type             // the CLR type, e.g. typeof(Complex)
et.Name             // display name: "ushort", "Complex", "Velocity2D"
et.PersistentName   // name written to .mxd files (see "Saving and Loading")
et.ValueModes       // the value modes, in index order; never empty
et.ValueModeCount   // shorthand for ValueModes.Count
et.ClampMode(mode)  // mode if it is valid, otherwise 0
```

A value mode is identified by its **index** into `ValueModes`; its `Name` is for display. A primitive type has a
single mode named `"Value"`. Every API that takes a value mode takes this index:

```csharp
double v = data.GetValueAt(ix, iy, frameIndex, valueMode: 1);
var (min, max) = data.GetValueRange(frameIndex, valueMode: 1);
```

For a single-mode type, every mode reads the value itself, so code written for structured data also works on
primitive data.

---

## Complex (Built-in)

`Complex` is registered by the library with five value modes, in the order of the `ComplexValueMode` enum:

| Index | `ComplexValueMode` | Value |
|---|---|---|
| 0 | `Magnitude` | \|z\| (the default) |
| 1 | `Real` | Re(z) |
| 2 | `Imaginary` | Im(z) |
| 3 | `Phase` | arg(z), in radians [−π, π] |
| 4 | `Power` | \|z\|² |

```csharp
using System.Numerics;
using MxPlot.Core;
using MxPlot.Core.Processing;

var field = new MatrixData<Complex>(256, 256);
field.Set((ix, iy, x, y) => Complex.FromPolarCoordinates(1.0, x * y));

// Ranges and values in a given mode
var (phaseMin, phaseMax) = field.GetValueRange(0, ComplexValueMode.Phase);
double re = field.GetValueAt(10, 20, 0, (int)ComplexValueMode.Real);

// A single element
double power = new Complex(3, 4).ToScalar(ComplexValueMode.Power);   // 25

// The whole data in one mode, as double
MatrixData<double> phase = field.ToScalars((int)ComplexValueMode.Phase);

// Complex only: extract a mode and apply log10 in one pass (e.g. a spectrum in dB-like scale)
MatrixData<double> logPower = field.ToDoubleAllFrames(ComplexValueMode.Power, applyLog10: true);
```

`ToScalar(ComplexValueMode)` is the single definition of the five modes: value reads, processing and rendering all
use it, so a value always lies within the range reported for its mode.

> **log₁₀ and signed modes.** `applyLog10` floors values at the same small epsilon as `LogTransform` with
> `NegativeHandling.Clamp`, so both give the same result. Use it with the non-negative modes (`Magnitude`, `Power`):
> the negative values of `Real`, `Imaginary` and `Phase` would all be flattened onto the floor.

---

## Registering a Custom Struct

A custom struct must be registered **once**, **before the first `MatrixData<T>` of it is created**. Creating an
instance of an unregistered type throws `InvalidOperationException`:

```csharp
MatrixData<Velocity2D>.IsRegistered;          // false until registered
var data = new MatrixData<Velocity2D>(64, 64); // ❌ InvalidOperationException
```

Registration supplies the value modes, and two **functions**: how to read an element in each mode, and how to find a
frame's range in each mode.

```csharp
MatrixData<Velocity2D>.Register(new ElementTypeRegistration<Velocity2D>
{
    TypeId       = "MyLab.Velocity2D",                                          // stable identifier for .mxd files
    ValueModes   = [new ValueMode("Speed"), new ValueMode("Vx"), new ValueMode("Vy")],
    ToScalar     = static (v, mode) => mode switch { 1 => v.Vx, 2 => v.Vy, _ => v.Speed },
    MinMaxFinder = FindVelocityRange,                                           // min/max of each mode
});
```

| Property | Required | What it is |
|---|---|---|
| `TypeId` | ✅ | A stable identifier written to `.mxd` headers. Keep it unchanged when the type is moved or renamed. |
| `ValueModes` | ✅ | The value modes, in index order. At least one. |
| `ToScalar` | ✅ | `Func<T, int, double>`: the value of an element in a mode (an index into `ValueModes`). Only valid modes are passed. |
| `ToScalars` | — | `ScalarsReader<T>`: a faster bulk form of `ToScalar` for a whole row. Must give the same values. Without one, the library loops over `ToScalar`. |
| `MinMaxFinder` | ✅ | `MatrixData<T>.MinMaxFinder`: a frame's minimum and maximum **for every mode**, in the same order. |
| `Interpolator` | — | An `IBilinearInterpolator<T>` for interpolated reads and bilinear resampling. Without one, those fall back to nearest-neighbor. |

Rules:

- **Once per type.** Registering a type again throws `InvalidOperationException`. Built-in types cannot be
  registered (`NotSupportedException`).
- **One entry per mode.** `Register` calls the finder once on a single `default(T)` element — the value every new
  frame starts with — and throws `ArgumentException` if it does not return one minimum and one maximum per mode.
- **Display name.** The display name (`ElementType.Name`) is the type's name; it is not part of the registration.
- **The functions must not keep state** — see the next section.

The finder is called once per frame and its result is cached; computing every mode in a **single pass** over the frame
is usually the most efficient. It defines the range the data reports, which is normally the exact minimum and maximum
of each mode as `ToScalar` reads it.

---

## The Functions Must Not Keep State

Everything you register — `ToScalar`, `ToScalars`, `MinMaxFinder` and the `Interpolator` — is **shared by the whole
process** for as long as the type is registered, and is **called concurrently**: from parallel loops over rows and
frames, from the UI's rendering thread, and from background processing. Each call must depend only on its arguments.

**Do not:**

| ❌ Don't | Why it breaks |
|---|---|
| Keep a "current mode" (or any setting) that one call sets for the next | Other threads change it between the set and the read |
| Reuse a buffer, cache or counter kept outside the call | Concurrent calls overwrite each other's data |
| Capture a variable that can change after registration | The reading changes silently for every existing instance |
| Do slow or blocking work (I/O, locks, allocations per element) | These run once per element, often on the UI's rendering path |

**Do:**

- Write the functions as **`static` lambdas** or **static methods**. Neither can capture anything, so the compiler
  rules out the third row above:

  ```csharp
  ToScalar = static (v, mode) => mode switch { 1 => v.Vx, 2 => v.Vy, _ => v.Speed },   // ✅ cannot capture
  ToScalar = Velocity2DModes.Read,                                                       // ✅ static method
  ```

- Keep temporary values in **local variables** inside the call.
- If a reading needs a parameter (a calibration factor, say), make it a constant, or a new value mode — not a field
  that changes.

The same applies to a `MinMaxFinder` passed later to `RegisterDefaultMinMaxFinder` or `SetMinMaxFinder`.

---

## Templates

### Template A — a struct with several value modes

A complete, copy-ready registration: the struct, its readings as static methods, a one-pass finder, and registration
at module load.

```csharp
using System;
using System.Runtime.CompilerServices;
using MxPlot.Core;

// 1. The struct: unmanaged fields only
public struct Velocity2D
{
    public float Vx;
    public float Vy;

    public Velocity2D(float vx, float vy) { Vx = vx; Vy = vy; }
    public readonly double Speed => Math.Sqrt((double)Vx * Vx + (double)Vy * Vy);
}

public static class Velocity2DModes
{
    // 2. Mode indices, for readable call sites (optional)
    public const int Speed = 0;
    public const int Vx = 1;
    public const int Vy = 2;

    // 3. How each mode is read — static, so it cannot keep state
    public static double Read(Velocity2D v, int mode) => mode switch
    {
        Vx => v.Vx,
        Vy => v.Vy,
        _ => v.Speed,
    };

    // 4. Min/max of every mode in one pass, in the same order as the modes
    public static (double[] minValues, double[] maxValues) FindRange(Velocity2D[] array)
    {
        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var v in array)
        {
            double speed = v.Speed;
            if (speed < min[0]) min[0] = speed;
            if (speed > max[0]) max[0] = speed;
            if (v.Vx < min[1]) min[1] = v.Vx;
            if (v.Vx > max[1]) max[1] = v.Vx;
            if (v.Vy < min[2]) min[2] = v.Vy;
            if (v.Vy > max[2]) max[2] = v.Vy;
        }
        return (min, max);
    }

    // 5. Register once, before any MatrixData<Velocity2D> exists.
    //    A module initializer runs when the assembly loads, so the type is always ready.
    [ModuleInitializer]
    internal static void Register()
    {
        MatrixData<Velocity2D>.Register(new ElementTypeRegistration<Velocity2D>
        {
            TypeId = "MyLab.Velocity2D",
            ValueModes = [new ValueMode("Speed"), new ValueMode("Vx"), new ValueMode("Vy")],
            ToScalar = Read,
            MinMaxFinder = FindRange,
        });
    }
}
```

If you prefer explicit startup code over a module initializer, call the registration from `Program.Main` (or your
application's startup) before creating any data, and guard it with `IsRegistered` if it can run more than once:

```csharp
if (!MatrixData<Velocity2D>.IsRegistered)
    MatrixData<Velocity2D>.Register(...);
```

### Template B — a struct with a single value mode

When one reading is all you need (a luminance, a temperature), register a single mode. A `static` lambda is enough:

```csharp
public struct RgbPixel
{
    public byte R, G, B;
    public readonly double Luminance => 0.2126 * R + 0.7152 * G + 0.0722 * B;
}

MatrixData<RgbPixel>.Register(new ElementTypeRegistration<RgbPixel>
{
    TypeId = "MyLab.RgbPixel",
    ValueModes = [new ValueMode("Luminance")],
    ToScalar = static (p, mode) => p.Luminance,   // only mode 0 exists
    MinMaxFinder = static array =>
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var p in array)
        {
            double l = p.Luminance;
            if (l < min) min = l;
            if (l > max) max = l;
        }
        return ([min], [max]);
    },
});
```

### Optional — a faster bulk read

Register `ToScalars` when reading a whole row at once can be done faster than element by element — typically by
deciding the mode once, outside the loop. It must give the same values as `ToScalar`:

```csharp
public static void ReadRow(ReadOnlySpan<Velocity2D> source, Span<double> destination, int mode)
{
    switch (mode)
    {
        case Velocity2DModes.Vx:
            for (int i = 0; i < source.Length; i++) destination[i] = source[i].Vx;
            break;
        case Velocity2DModes.Vy:
            for (int i = 0; i < source.Length; i++) destination[i] = source[i].Vy;
            break;
        default:
            for (int i = 0; i < source.Length; i++) destination[i] = source[i].Speed;
            break;
    }
}

// in the registration:
ToScalars = Velocity2DModes.ReadRow,
```

### Optional — interpolation

Supply an `IBilinearInterpolator<T>` if interpolated reads (`GetValue(x, y, interpolate: true)`) and bilinear
resampling should blend elements rather than pick the nearest one. Interpolate in the struct's own space — for a
vector, component by component. Like the functions above, it is shared and called concurrently, so it must not keep
state:

```csharp
public sealed class Velocity2DInterpolator : IBilinearInterpolator<Velocity2D>
{
    public Velocity2D Interpolate(Velocity2D v00, Velocity2D v10, Velocity2D v01, Velocity2D v11, double dx, double dy)
    {
        float Lerp2(float a, float b, float c, float d) =>
            (float)((a * (1 - dx) + b * dx) * (1 - dy) + (c * (1 - dx) + d * dx) * dy);
        return new Velocity2D(Lerp2(v00.Vx, v10.Vx, v01.Vx, v11.Vx), Lerp2(v00.Vy, v10.Vy, v01.Vy, v11.Vy));
    }
}

// in the registration:
Interpolator = new Velocity2DInterpolator(),
```

Like the finder, the interpolator can be replaced later, for instances created afterwards:
`MatrixData<Velocity2D>.RegisterDefaultInterpolator(other)`. This requires the type to be registered, and is not
allowed for the primitive types, whose values are interpolated numerically.

---

## Using a Registered Struct

Once registered, a custom struct is used like any other element type:

```csharp
var flow = new MatrixData<Velocity2D>(512, 512, frameCount: 100);
flow.Set((ix, iy, x, y) => new Velocity2D((float)-y, (float)x));

// Reads and ranges in a mode
double vx = flow.GetValueAt(10, 20, 0, Velocity2DModes.Vx);
var (sMin, sMax) = flow.GetValueRange(0, Velocity2DModes.Speed);

// Mode names, e.g. to build a UI
foreach (var m in flow.ElementType.ValueModes)
    Console.WriteLine(m.Name);            // Speed, Vx, Vy

// Plain double data in one mode
MatrixData<double> speed = flow.ToScalars(Velocity2DModes.Speed);
```

Reading without a mode (`GetValueAt(ix, iy)`, `GetValueRange()`) reads mode 0.

### Replacing the MinMaxFinder

The finder given at registration can be replaced later — for example with a faster implementation, or with a
different algorithm for a particular purpose:

```csharp
// For every instance created from now on (existing instances keep theirs)
MatrixData<Velocity2D>.RegisterDefaultMinMaxFinder(fasterFinder);

// For one instance, for a while
flow.SetMinMaxFinder(customFinder);   // cached ranges are invalidated
flow.ResetMinMaxFinder();             // back to the default; cached ranges are invalidated again
```

A replacement must also return one entry per mode (checked the same way as at registration) and must not keep state.
`ToScalar`, and therefore the value modes, cannot be replaced once registered.

> **Note:** since the finder defines the range the data reports, it can also report something other than the exact
> extremes, such as a percentile-clipped range. Consumers that map values to a range (LUT rendering, histograms)
> clamp values outside it.

---

## Processing Structured Data

Operations fall into three kinds when the element type is structured:

| Kind | Examples | With structured data |
|---|---|---|
| **Keep the element type, independent of mode** | `Transpose`, slicing, `Resample` (with the registered interpolator, or nearest-neighbor) | Work directly |
| **Reduce to double (projecting)** | `ToScalars`, `LogTransform`, histograms, line profiles | Take a `valueMode` and read the data in it |
| **Keep the element type, but defined on numbers** | `Normalize`, `ApplyFilter` (Median, Gaussian), `Grayscale` | **Not supported yet** — read the data as double first |

```csharp
using MxPlot.Core.Processing;
using MxPlot.Core.Utils;

// Projecting operations take the mode to read
var logSpeed = flow.LogTransform(LogBase.Log10, NegativeHandling.Shift, valueMode: Velocity2DModes.Speed);
int[] hist = flow.CreateHistogram(frameIndex: 0, bins: 256, valueMode: Velocity2DModes.Vx);
var (pos, values) = flow.GetLineProfile((0, 0), (100, 100), valueMode: Velocity2DModes.Vy);

// Operations defined on numbers: read the mode as double first
var smoothedVx = flow.ToScalars(Velocity2DModes.Vx).ApplyFilter(new GaussianKernel(radius: 2));

// Calling them directly throws, and the message points to ToScalars
flow.Normalize(1.0, NormalizeScope.PerFrame);   // ❌ NotSupportedException: "... does not support Velocity2D data yet ..."
```

"Not supported yet" is deliberate wording: these operations may gain direct support for structured types in the
future (for example component-wise Gaussian smoothing of a Complex field). Today, reading a value mode as double
first is the supported route, and the result is `double`.

The same applies to `Complex`: to smooth a spectrum's magnitude, use
`spectrum.ToScalars((int)ComplexValueMode.Magnitude).ApplyFilter(...)`.

---

## Saving and Loading (.mxd)

| | Built-in types (incl. `Complex`) | Custom structs |
|---|---|---|
| Save (`SaveAs(path, new MxBinaryFormat())`) | ✅ | ✅ Raw element bytes; the header records the `TypeId` and each frame's range for every mode |
| Typed load (`MatrixDataSerializer.Load<T>(path)`) | ✅ | ✅ The type must be registered; the header's `TypeId` must match |
| Untyped load (`MatrixDataSerializer.LoadDynamic(path)`) | ✅ | ❌ Not supported yet |
| Virtual (memory-mapped) load of uncompressed files | ✅ | Typed only (`LoadVirtual<T>`) |

```csharp
flow.SaveAs("flow.mxd", new MxBinaryFormat());
var loaded = MatrixDataSerializer.Load<Velocity2D>("flow.mxd");   // Velocity2D must be registered
```

Things to keep in mind for custom structs:

- **Keep `TypeId` stable.** It is how a file is matched to its type. Moving or renaming the struct is fine as long
  as the `TypeId` stays the same.
- **Change `TypeId` when the layout changes.** Elements are stored as raw bytes and there is no layout versioning
  yet, so a file written with a different field layout would be misread. Giving the new layout a new `TypeId`
  (for example `"MyLab.Velocity2D.v2"`) makes old files fail to match instead.
- **Value modes are not stored in the file.** They come from the registration of the application that reads it.

---

## UI Support

MxPlot.UI.Avalonia (`MatrixPlotter`, `MxView`) supports **`Complex` fully**:

- the value mode can be switched from the view's context menu, and the status bar shows it (e.g. `Complex (Mag)`);
- rendering, cursor values, histograms and line profiles follow the displayed mode;
- processing commands read the data in the displayed mode (a live "Sync source data" window follows mode changes);
- **Data › View › Open Synced View** opens a second, synced view of the current frame, so the same data can be shown
  in two modes side by side (e.g. its phase next to its magnitude).

For **custom structs, UI support is partial**: a registered struct can be opened and is displayed in its first value
mode (the status bar shows e.g. `Velocity2D (Speed)`), but switching modes, histograms and line profiles are not
supported yet. **Full UI support for custom structs is planned for a future release.** Until then, convert the data
to double in the mode you need (`ToScalars(mode)`) before showing it.

---

## Checklist and API Summary

### Checklist for a custom struct

1. ✅ **Define the struct** — unmanaged fields only
2. ✅ **Choose the value modes** — their order is their index; optionally define constants for the indices
3. ✅ **Write `ToScalar`** — a `static` lambda or static method reading each mode
4. ✅ **Write a `MinMaxFinder`** — one minimum and one maximum per mode, preferably in one pass
5. ✅ **Make sure nothing keeps state** — see [The Functions Must Not Keep State](#the-functions-must-not-keep-state)
6. ✅ **Pick a stable `TypeId`** — and change it if the layout changes
7. ✅ **Register once, before the first instance** — `[ModuleInitializer]` or startup code
8. ✅ **Test** — create an instance, check `GetValueRange` for each mode, and round-trip a file if you save data

### API summary

```csharp
// Registration
public sealed class ElementTypeRegistration<T> where T : unmanaged
{
    public required string TypeId { get; init; }
    public required IReadOnlyList<ValueMode> ValueModes { get; init; }
    public required Func<T, int, double> ToScalar { get; init; }
    public ScalarsReader<T>? ToScalars { get; init; }                        // optional bulk form
    public required MatrixData<T>.MinMaxFinder MinMaxFinder { get; init; }
    public IBilinearInterpolator<T>? Interpolator { get; init; }             // optional
}
public delegate void ScalarsReader<T>(ReadOnlySpan<T> source, Span<double> destination, int mode);

// MatrixData<T>
static void Register(ElementTypeRegistration<T> registration);
static bool IsRegistered { get; }
static void RegisterDefaultMinMaxFinder(MinMaxFinder finder);   // replaces the default for new instances
static void RegisterDefaultInterpolator(IBilinearInterpolator<T> interpolator);   // likewise

ElementType ElementType { get; }
double GetValueAt(int ix, int iy, int frameIndex, int valueMode);
(double Min, double Max) GetValueRange(int frameIndex, int valueMode);
void SetMinMaxFinder(MinMaxFinder finder);
void ResetMinMaxFinder();

// Reading as double (MxPlot.Core.Processing)
MatrixData<double> ToScalars<T>(this MatrixData<T> src, int valueMode = 0, IProgress<int>? progress = null, CancellationToken ct = default);

// Complex (MxPlot.Core)
double ToScalar(this Complex value, ComplexValueMode mode);
(double Min, double Max) GetValueRange(this MatrixData<Complex> src, int frameIndex, ComplexValueMode mode);
```

---

## Related Topics

- **[MatrixData Operations Guide](./MatrixData_Operations_Guide.md)** — operations available on all element types
- **[VolumeAccessor Guide](./VolumeAccessor_Guide.md)** — 3D operations
- **[MxPlot.Extensions.Fft](../MxPlot/MxPlot.Extensions.Fft/README.md)** — FFT, which produces `MatrixData<Complex>`
