# MxPlot.Extensions.Fft

**Preliminary 2D FFT Support for MatrixData (Powered by MathNet.Numerics)**

[![NuGet](https://img.shields.io/nuget/v/MxPlot.Extensions.Fft?include_prerelease&style=flat-square)](https://www.nuget.org/packages/MxPlot.Extensions.Fft)
[![.NET](https://img.shields.io/badge/.NET-10.0%20%7C%208.0-blue)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)


## 📦 About this Package
This package provides a thin wrapper around the 2D FFT functionality of **MathNet.Numerics**, enabling seamless integration with `MatrixData<T>`. 
It is designed to serve as a useful extension for users who require basic FFT capabilities within the MxPlot ecosystem.
**Although marked as preliminary, the design and API are already stable; remaining work may focus on performance tuning and potential bug fixes.**

## 🔍 Key Features
- Uses MathNet.Numerics with native MKL acceleration on x64 platforms for maximum performance.
- Automatically falls back to a managed 2D FFT implementation on non‑MKL environments (ARM, macOS, Linux).
- Implements a mathematically consistent `ShiftOption` model with correct handling of odd-sized transforms.
- Designed to preserve spatial origin semantics in `MatrixData<T>` during FFT/IFFT pipelines.
- Guarantees identical ShiftOption semantics across MKL and managed backends.


## 🛠️ Math Kernel Library (MKL) Support and Fallback

When running on x64 Windows and you want the maximum FFT performance, you can enable Intel's
Math Kernel Library (MKL) backend for MathNet.Numerics by installing the native runtime package:

```bash
dotnet add package MathNet.Numerics.MKL.Win-x64
```

This NuGet package provides the native MKL binaries for x64 Windows and lets MathNet.Numerics
use the optimized MKL FFT implementations. Using MKL typically yields significant speedups
for large 2D FFTs on x64 desktop/server CPUs.

If MKL is not installed, or when running on non-x64 architectures (ARM, macOS, Linux, etc.),
the extension automatically falls back to MathNet.Numerics' managed FFT implementation. In
that case the repository's own 2D FFT helper (managed implementation) is used transparently so
that `MatrixData<T>` users do not need to change their code.

Note: the MKL package is optional — the extension works out-of-the-box with the managed backend.


## 🚀 Quick Examples

### Forward / Inverse FFT

```csharp
using MxPlot.Core;
using MxPlot.Extensions.Fft;

var image = new MatrixData<double>(256, 256);
image.Set((ix, iy, x, y) => Math.Sin(x) * Math.Cos(y));

// Forward FFT — one call, returns MatrixData<Complex>
var spectrum = image.Fft2D(ShiftOption.Centered); // DC shifted to center

// Power spectrum statistics
var (min, max) = spectrum.GetMinMaxValues(0, ComplexValueMode.Power);

// Inverse FFT — back to spatial domain
var restored = spectrum.InverseFft2D(ShiftOption.Centered);
```

### FFT Pipeline with In-place Frequency-Domain Processing

`FftPipelineWithAction` performs **FFT → your action → IFFT** in a single optimized call,
minimizing intermediate allocations and swap operations.
This is the key feature of this package for frequency-domain filter design.

```csharp
// Example: Angular Spectrum Propagation (optical wave propagation by distance z)
double lambda = 0.5e-6;   // wavelength [m]
double z = 1e-3;          // propagation distance [m]

var field = new MatrixData<Complex>(512, 512);
// ... set field values ...
field.SetXYScale(-1e-3, 1e-3, -1e-3, 1e-3); // physical coords [m]

// FFT → apply transfer function → IFFT — all in one call
// BothCentered: input and output are both in center layout (origin at array center),
// matching the physical coordinate convention set by SetXYScale.
var propagated = field.FftPipelineWithAction(
    action: (buf, scale) =>
    {
        // buf is the frequency-domain array with DC at center
        // scale provides physical frequency coordinates (fx, fy)
        for (int iy = 0; iy < scale.YCount; iy++)
        {
            double fy = scale.YValue(iy);
            for (int ix = 0; ix < scale.XCount; ix++)
            {
                double fx = scale.XValue(ix);
                double fz2 = 1.0 / (lambda * lambda) - fx * fx - fy * fy;

                Complex h;
                if (fz2 >= 0)
                {
                    // Propagating wave: pure phase shift
                    double kz = 2 * Math.PI * Math.Sqrt(fz2);
                    h = Complex.Exp(new Complex(0, kz * z));
                }
                else
                {
                    // Evanescent wave: exponential decay (near-field only)
                    double decay = 2 * Math.PI * Math.Sqrt(-fz2) * z;
                    h = Math.Exp(-decay); // real-valued attenuation
                }

                buf[iy * scale.XCount + ix] *= h;
            }
        }
    },
    option: ShiftOption.BothCentered
);

// MatrixPlotter requires real-valued data; convert to intensity (|E|²) first
var intensity = propagated.ConvertTo<Complex, double>(z => z.Magnitude * z.Magnitude);
MatrixPlotter.Create(intensity, title: $"Intensity  z = {z * 1e3:F1} mm").Show();
```

The same pattern applies to any frequency-domain operation:
low-pass / high-pass filters, phase masks, aberration correction, and more.

### Multi-Frame FFT

For multi-frame data (e.g. time-series or Z-stacks), apply FFT to all frames at once.
Each frame is processed independently in parallel:

```csharp
// 3D data: 256×256, 100 time frames
var timeSeries = new MatrixData<double>(256, 256, 100);
// ... fill data ...
timeSeries.SetXYScale(-1e-3, 1e-3, -1e-3, 1e-3);

var spectra = timeSeries.Fft2DAllFrames(ShiftOption.Centered);
// spectra: MatrixData<Complex> with 100 frames, frequency-domain scale applied

var restored = spectra.InverseFft2DAllFrames(ShiftOption.Centered);
```

👉 **[GitHub Repository: u1bx0/MxPlot](https://github.com/u1bx0/mxplot)**

## 📊 Version History
For the complete changelog and version history, please visit the [Releases Page](https://github.com/u1bx0/mxplot/releases) on GitHub.