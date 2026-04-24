# Spatial Filter Operations Guide

**MxPlot.Core.Processing — Comprehensive Reference**

> Last Updated: 2026-04-12

*Note: This document is largely based on AI-generated content and requires further review for accuracy.*

## 📚 Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Built-in Kernels](#built-in-kernels)
   - [MedianKernel](#mediankernel)
   - [GaussianKernel](#gaussiankernel)
4. [Basic Usage](#basic-usage)
   - [Direct Extension Method](#direct-extension-method)
   - [Operation Pipeline (Type-Erased)](#operation-pipeline-type-erased)
   - [Single-Frame / Specific-Frame Filtering](#single-frame--specific-frame-filtering)
5. [Practical Examples](#practical-examples)
   - [Hot Pixel Removal](#hot-pixel-removal)
   - [Noise Reduction with Gaussian](#noise-reduction-with-gaussian)
   - [Multi-Frame Batch Processing](#multi-frame-batch-processing)
   - [Extracting and Filtering Specific Axis Frames with Reorder](#extracting-and-filtering-specific-axis-frames-with-reorder)
   - [Progress Reporting and Cancellation](#progress-reporting-and-cancellation)
   - [Parallel vs Sequential Performance](#parallel-vs-sequential-performance)
6. [Extending with Custom Kernels](#extending-with-custom-kernels)
   - [IFilterKernel Interface](#ifilterkernel-interface)
   - [Example: Mean (Box) Filter](#example-mean-box-filter)
   - [Example: Min/Max (Erosion/Dilation) Filter](#example-minmax-erosiondilation-filter)
   - [Example: Weighted Custom Kernel](#example-weighted-custom-kernel)
7. [Design Notes](#design-notes)
   - [Three-Layer Architecture](#three-layer-architecture)
   - [Edge Handling (Clamp)](#edge-handling-clamp)
   - [T↔double Conversion](#tdouble-conversion)
   - [CopyPropertiesFrom Helper](#copypropertiesfrom-helper)
8. [File Map](#file-map)
9. [API Reference Summary](#api-reference-summary)

---

## Overview

The spatial filter framework provides a **kernel-injection** model for applying per-pixel neighborhood filters (median, Gaussian, etc.) to `MatrixData<T>`. It supports:

- ✅ **Any numeric type T** — `double`, `float`, `int`, `byte`, `ushort`, `Complex`, etc.
- ✅ **All frames processed** — Every frame is filtered in a single call
- ✅ **Frame-level parallelism** — `Parallel.For` across frames when `FrameCount ≥ 2`
- ✅ **Edge-clamped neighborhoods** — Border pixels are handled by repeating the nearest edge value
- ✅ **Pluggable kernels** — Add new filter types by implementing `IFilterKernel`
- ✅ **IOperation pipeline** — Works with `IMatrixData.Apply()` for type-erased UI code
- ✅ **Progress and cancellation** — `IProgress<int>` and `CancellationToken` support

---

## Architecture

```
UI Layer (IMatrixData — T unknown)
  │
  │  data.Apply(new SpatialFilterOperation(kernel))
  ▼
SpatialFilterOperation : IMatrixDataOperation         ← Type-erased bridge
  │
  │  Execute<T>(MatrixData<T> src)                    ← T resolved here
  ▼
FilterOperator.ApplyFilter<T>(source, kernel, ...)    ← Generic algorithm
  │
  │  kernel.Apply(Span<double>, count)
  ▼
IFilterKernel (MedianKernel / GaussianKernel / ...)   ← Strategy injection
```

| Layer | File | Responsibility |
|---|---|---|
| Kernel Strategy | `FilterKernel.cs` | `IFilterKernel` interface + built-in implementations |
| Generic Algorithm | `FilterOperator.cs` | `ApplyFilter<T>()` extension method on `MatrixData<T>` |
| Operation Bridge | `FilterOperations.cs` | `SpatialFilterOperation` record (`IMatrixDataOperation`) |

---

## Built-in Kernels

### MedianKernel

Sorts the neighborhood values and returns the middle value. Extremely effective for removing salt-and-pepper noise and hot/dead pixels while preserving edges.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `radius` | `int` | `1` | Kernel half-size. `1` → 3×3, `2` → 5×5, `3` → 7×7 |

```csharp
new MedianKernel()           // 3×3 (default)
new MedianKernel(radius: 2)  // 5×5
new MedianKernel(radius: 3)  // 7×7
```

**Kernel size**: `(2 × radius + 1)²` pixels per neighborhood.

### GaussianKernel

Applies a Gaussian-weighted average to smooth the image. Reduces high-frequency noise while minimizing edge blurring compared to a simple box filter.

| Parameter | Type | Default | Description |
|---|---|---|---|
| `radius` | `int` | `1` | Kernel half-size |
| `sigma` | `double` | `radius / 2.0` | Standard deviation of the Gaussian. Larger = more smoothing |

```csharp
new GaussianKernel()                        // 3×3, σ=0.5
new GaussianKernel(radius: 2, sigma: 1.0)  // 5×5, σ=1.0
new GaussianKernel(radius: 3, sigma: 1.5)  // 7×7, σ=1.5
```

**Note**: At image edges where the full kernel doesn't fit, the Gaussian kernel falls back to a uniform average.

---

## Basic Usage

### Direct Extension Method

When `T` is known at compile time, use the extension method directly:

```csharp
using MxPlot.Core.Processing;

// Median 3×3
MatrixData<double> result = source.ApplyFilter(new MedianKernel());

// Gaussian 5×5
MatrixData<ushort> result2 = source.ApplyFilter(new GaussianKernel(radius: 2, sigma: 1.0));
```

### Operation Pipeline (Type-Erased)

When working with `IMatrixData` (T unknown, e.g. from UI code):

```csharp
using MxPlot.Core.Processing;

IMatrixData data = ...;  // T is unknown

// Median filter via Apply pipeline
IMatrixData result = data.Apply(new SpatialFilterOperation(new MedianKernel()));

// Gaussian filter with progress
IMatrixData result2 = data.Apply(new SpatialFilterOperation(
    new GaussianKernel(radius: 2, sigma: 1.5),
    Progress: progress,
    CancellationToken: cts.Token));
```

### Single-Frame / Specific-Frame Filtering

**Important**: `ApplyFilter` always processes **all frames**. To apply a filter to a single frame or a specific subset, extract the frame first using `SliceAt`.

```csharp
// Multi-frame data (e.g. 50-frame time-lapse)
var timelapse = new MatrixData<double>(256, 256, 50);

// ✅ Filter only a specific frame (frame 10)
var frame10 = timelapse.SliceAt(10);
var filtered = frame10.ApplyFilter(new MedianKernel(radius: 1));
// filtered.FrameCount == 1

// ✅ Single-frame data works directly
var singleFrame = new MatrixData<double>(512, 512);
var result = singleFrame.ApplyFilter(new GaussianKernel(radius: 1));
```

`SliceAt` is a lightweight operation (shallow copy of the data array), so the "extract → filter" pattern has minimal overhead and is the recommended approach.

---

## Practical Examples

### Hot Pixel Removal

A common use case in scientific imaging — removing isolated bright/dark pixels from sensor noise:

```csharp
// Single hot pixel at (50, 50) in a 512×512 image
var data = new MatrixData<double>(512, 512);
data.GetArray()[50 * 512 + 50] = 65535.0;  // Hot pixel

// Median 3×3 removes isolated outliers while preserving edges
var cleaned = data.ApplyFilter(new MedianKernel(radius: 1));
// The hot pixel is gone; surrounding pixels are unchanged
```

### Noise Reduction with Gaussian

Smoothing noisy data while preserving large-scale features:

```csharp
var noisy = LoadNoisyData();

// Light smoothing: 3×3, σ=1.0
var smooth = noisy.ApplyFilter(new GaussianKernel(radius: 1, sigma: 1.0));

// Stronger smoothing: 5×5, σ=2.0
var verySmooth = noisy.ApplyFilter(new GaussianKernel(radius: 2, sigma: 2.0));
```

### Multi-Frame Batch Processing

All frames are automatically processed. Frame-level parallelism is enabled by default when `FrameCount ≥ 2`:

```csharp
// 512×512 × 100 frames (e.g. time-lapse)
var timelapse = new MatrixData<float>(512, 512, 100);
// ... fill data ...

// All 100 frames are filtered in parallel
var filtered = timelapse.ApplyFilter(new MedianKernel(radius: 1));
// filtered.FrameCount == 100
```

### Extracting and Filtering Specific Axis Frames with Reorder

When working with multi-dimensional data (e.g. Z × Time), you can extract frames along a specific axis using `SelectBy` and then apply the filter. This enables dimension-aware frame selection:

```csharp
// 256×256, Z=10, Time=5 → 50 frames total
var data = new MatrixData<double>(256, 256, 50);
data.DefineDimensions(
    new Axis(10, 0, 100, "Z", "µm"),
    new Axis(5, 0, 10, "T", "s")
);

// Extract all Z slices at a specific time point (T=2)
var atTime2 = data.SelectBy("T", 2);
// atTime2: 256×256, 10 frames (Z slices only)

// Apply median filter to all Z slices (frame-parallel)
var filtered = atTime2.ApplyFilter(new MedianKernel(radius: 1));
// filtered: 256×256, 10 frames

// Filter a single specific slice: T=2, Z=5
var z5 = data.SelectBy("T", 2).SliceAt(5);
var filteredSlice = z5.ApplyFilter(new GaussianKernel(radius: 2, sigma: 1.0));
```

**Pattern**: `SelectBy` (narrow by axis) → `SliceAt` (extract single frame) → `ApplyFilter` — this chain lets you target any cross-section of multi-dimensional data.

### Progress Reporting and Cancellation

```csharp
var cts = new CancellationTokenSource();
var progress = new Progress<int>(value =>
{
    if (value < 0)
        Console.WriteLine($"Total frames: {-value}");
    else
        Console.WriteLine($"Completed frame {value}");
});

try
{
    var result = data.ApplyFilter(
        new MedianKernel(radius: 1),
        progress: progress,
        cancellationToken: cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("Filter cancelled.");
}
```

**Progress protocol**:
1. First report: negative value (`-FrameCount`) as a total count hint
2. Subsequent reports: `0, 1, 2, ...` as each frame completes

### Parallel vs Sequential Performance

```csharp
// Force sequential processing (useful for debugging or benchmarking)
var seqResult = data.ApplyFilter(new MedianKernel(), useParallel: false);

// Default: parallel when FrameCount ≥ 2
var parResult = data.ApplyFilter(new MedianKernel(), useParallel: true);
```

---

## Extending with Custom Kernels

### IFilterKernel Interface

```csharp
public interface IFilterKernel
{
    /// <summary>Kernel half-size. Radius=1 → 3×3, Radius=2 → 5×5.</summary>
    int Radius { get; }

    /// <summary>
    /// Computes the output value from the neighborhood.
    /// The implementation may freely mutate the buffer (e.g. for sorting).
    /// </summary>
    /// <param name="values">Scratch buffer. Only the first <paramref name="count"/> elements are valid.</param>
    /// <param name="count">Number of valid elements (may be less at edges).</param>
    double Apply(Span<double> values, int count);
}
```

**Key points**:
- `Radius` determines how many neighboring pixels are collected in each direction
- `Apply` receives the neighborhood as a `Span<double>` — you may sort, modify, or read it freely
- `count` may be less than `(2R+1)²` at image edges (clamped boundary)

### Example: Mean (Box) Filter

```csharp
public sealed class MeanKernel : IFilterKernel
{
    public int Radius { get; }

    public MeanKernel(int radius = 1) => Radius = radius;

    public double Apply(Span<double> values, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
            sum += values[i];
        return sum / count;
    }
}

// Usage
var smoothed = data.ApplyFilter(new MeanKernel(radius: 2));
```

### Example: Min/Max (Erosion/Dilation) Filter

```csharp
public sealed class MinKernel : IFilterKernel
{
    public int Radius { get; }
    public MinKernel(int radius = 1) => Radius = radius;

    public double Apply(Span<double> values, int count)
    {
        double min = values[0];
        for (int i = 1; i < count; i++)
            if (values[i] < min) min = values[i];
        return min;
    }
}

public sealed class MaxKernel : IFilterKernel
{
    public int Radius { get; }
    public MaxKernel(int radius = 1) => Radius = radius;

    public double Apply(Span<double> values, int count)
    {
        double max = values[0];
        for (int i = 1; i < count; i++)
            if (values[i] > max) max = values[i];
        return max;
    }
}
```

### Example: Weighted Custom Kernel

A kernel with arbitrary user-defined weights (e.g. Laplacian, Sobel-like):

```csharp
public sealed class WeightedKernel : IFilterKernel
{
    public int Radius { get; }
    private readonly double[] _weights;

    public WeightedKernel(int radius, double[] weights)
    {
        Radius = radius;
        int expected = (2 * radius + 1) * (2 * radius + 1);
        if (weights.Length != expected)
            throw new ArgumentException($"Expected {expected} weights, got {weights.Length}.");
        _weights = weights;
    }

    public double Apply(Span<double> values, int count)
    {
        int fullSize = _weights.Length;
        if (count == fullSize)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
                sum += values[i] * _weights[i];
            return sum;
        }
        // Edge fallback: simple average
        double avg = 0;
        for (int i = 0; i < count; i++) avg += values[i];
        return avg / count;
    }
}
```

---

## Design Notes

### Three-Layer Architecture

| Layer | Role | Why it exists |
|---|---|---|
| `IFilterKernel` | Pure computation strategy | Open-Closed: new filters = new class, no changes elsewhere |
| `FilterOperator` | Generic `<T>` algorithm | Needs `Span<T>`, `AsSpan()`, `T↔double` — only possible with `T` known |
| `SpatialFilterOperation` | `IMatrixDataOperation` bridge | UI layer works with `IMatrixData` (no `T`); this record resolves `T` via the Visitor pattern |

No layer is redundant — removing any one would either break the type-erased pipeline or lose kernel extensibility.

### Edge Handling (Clamp)

At image boundaries, the neighborhood is clamped to the valid pixel range:

```
For pixel (0, 0) with radius=1:
  Neighborhood X range: max(0, 0-1)..min(W-1, 0+1) = 0..1
  Neighborhood Y range: max(0, 0-1)..min(H-1, 0+1) = 0..1
  → 2×2 = 4 values instead of 3×3 = 9
```

The kernel's `count` parameter reflects the actual number of valid neighbors collected.

### T↔double Conversion

All kernel computations are performed in `double` space:
1. `T → double` via `MatrixData<T>.ToDoubleConverter` (static delegate per type)
2. Kernel computes in `double`
3. `double → T` via `MatrixData<T>.FromDoubleConverter`

This handles all supported types (`byte`, `ushort`, `int`, `float`, `double`, `Complex`, etc.) uniformly.

### CopyPropertiesFrom Helper

The result matrix inherits source properties via the shared helper:

```csharp
result.CopyPropertiesFrom(source);
// Copies: XY scale, XUnit, YUnit, Metadata, DimensionStructure
```

This utility is shared with `DimensionalOperator`, `MatrixArithmetic`, and other operators to avoid code duplication.

---

## File Map

```
MxPlot.Core/Processing/
├── FilterKernel.cs         ← IFilterKernel + MedianKernel + GaussianKernel
├── FilterOperator.cs       ← FilterOperator.ApplyFilter<T>() extension method
└── FilterOperations.cs     ← SpatialFilterOperation record (IMatrixDataOperation)
```

---

## API Reference Summary

### FilterOperator (Extension Method)

```csharp
public static MatrixData<T> ApplyFilter<T>(
    this MatrixData<T> source,
    IFilterKernel kernel,
    bool useParallel = true,
    IProgress<int>? progress = null,
    CancellationToken cancellationToken = default) where T : unmanaged
```

### SpatialFilterOperation (Record)

```csharp
public record SpatialFilterOperation(
    IFilterKernel Kernel,
    IProgress<int>? Progress = null,
    CancellationToken CancellationToken = default) : IMatrixDataOperation
```

### IFilterKernel (Interface)

```csharp
public interface IFilterKernel
{
    int Radius { get; }
    double Apply(Span<double> values, int count);
}
```

### Built-in Kernels

| Kernel | Parameters | Description |
|---|---|---|
| `MedianKernel(int radius = 1)` | `radius` | Sorts neighborhood → returns middle value |
| `GaussianKernel(int radius = 1, double sigma = 0)` | `radius`, `sigma` | Gaussian-weighted average (σ defaults to `radius / 2.0`) |
