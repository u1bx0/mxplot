# Calculator and Formula Guide

**MxPlot.UI.Avalonia / MxPlot.App**

> Last Updated: 2026-11-26 (v0.7.0)

A formula computes a new value for every pixel. It can use the pixel coordinates, the position along
the axes, and the data of one or more windows. Results are always `double`.

## Contents

1. [Calculator and Create Data](#calculator-and-create-data)
2. [Examples](#examples)
3. [Variables](#variables)
4. [Reading Data](#reading-data)
5. [Operators and Constants](#operators-and-constants)
6. [Functions](#functions)
7. [Values Outside the Data (NaN)](#values-outside-the-data-nan)
8. [Results That Follow Their Source](#results-that-follow-their-source)
9. [Using Create Data from a Host Application](#using-create-data-from-a-host-application)
10. [Notes and Limitations](#notes-and-limitations)

---

## Calculator and Create Data

| | Calculator | Create Data |
|---|---|---|
| Where | **Processing > Calculator…** in a `MatrixPlotter` | **Create Data…** in the MxPlot.App main menu |
| Data it reads | The window's own data, as `m1` | Any number of windows, as `m1`, `m2`, … (or none) |
| Grid and axes of the result | Those of the source | Those you define, or those of a reference window |
| Following the source | **Sync source data** | **Keep updated** |

In both dialogs:

- The **?** button opens a searchable list of operators and functions.
- The **Up / Down** keys (or the ▾ button) recall formulas applied earlier in the same session.
- An invalid formula tints the box and shows what is wrong.

### Calculator

Enter a formula and press **Apply**. For multi-frame data, the whole stack is processed unless
**This frame only** is checked. `m1` can read any frame of the source in either case. **Replace data**
overwrites the window's data instead of opening a new window.

### Create Data

1. Optionally add reference windows with **+ Add Window…**. They are named `m1`, `m2`, … in order.
   In MxPlot.App, selecting windows before opening the dialog adds them as references.
2. Set the output grid. With references, the grid and axes come from the window chosen in **From:**,
   unless **Custom size / scale** is checked. Axes are added with **+ Z**, **+ Channel**, **+ Time**
   and **+ Other**.
3. Enter a formula and press **Create**. The button then becomes **Update**, which makes the same
   window again from the current formula, references and grid.

---

## Examples

`m1` is the window's own data in the Calculator, and the first reference in Create Data.
Coordinates (`x`, `y`, `r`) are in the physical units of the data.

### Values and normalization

| Purpose | Formula |
|---|---|
| Gain and offset | `m1 * 2.5 - 100` |
| Normalize each frame by its maximum | `m1 / m1.Max()` |
| Stretch each frame to 0–1 | `(m1 - m1.Min()) / (m1.Max() - m1.Min())` |
| Normalize a stack by one common maximum | `m1 / m1.MaxAll()` |
| Square root (negative values become 0) | `sqrt(max(m1, 0))` |
| Decibels relative to the frame maximum | `10 * log10(m1 / m1.Max())` |

### Thresholds and masks

| Purpose | Formula |
|---|---|
| Set values below a threshold to 0 | `m1 > 500 ? m1 : 0` |
| Binary image | `m1 > 500` |
| Mark masked pixels as missing (NaN) | `m1 > 500 ? m1 : 0.0 / 0.0` |
| Keep a disc of radius 5 around the origin | `m1 * circ(r / 5)` |
| Keep a disc centered at (2, -1) | `m1 * circ((x - 2) / 5, (y + 1) / 5)` |
| Smooth Gaussian window | `m1 * gauss(r, 10)` |

### Neighbors, other frames and shifts

| Purpose | Formula |
|---|---|
| Difference to the pixel on the right | `m1[ix + 1, iy] - m1` |
| Gradient magnitude (central differences, per pixel) | `hypot((m1[ix + 1, iy] - m1[ix - 1, iy]) / 2, (m1[ix, iy + 1] - m1[ix, iy - 1]) / 2)` |
| Mirror left and right (grid symmetric about x = 0) | `m1(-x, y)` |
| Shift by 0.5 units in x, interpolated | `m1(x - 0.5, y)` |
| Subtract the first time point (needs a Time axis) | `m1 - m1[ix, iy, it:0]` |
| Difference from the previous time point | `m1 - m1[ix, iy, it: it - 1]` |
| The same, with 0 for the first time point | `num(m1 - m1[ix, iy, it: it - 1])` |
| Ratio of two channels (needs a Channel axis) | `m1[ix, iy, ic:0] / m1[ix, iy, ic:1]` |
| Subtract frame number 0 | `m1 - m1[ix, iy, 0]` |

### Combining windows (Create Data)

| Purpose | Formula |
|---|---|
| Difference | `m1 - m2` |
| Flat-field correction (`m1` signal, `m2` flat, `m3` dark) | `(m1 - m3) / (m2 - m3)` |
| Difference of data on different grids | `m1(x, y) - m2(x, y)` |
| Overlay shifted data, ignoring missing parts | `sum(m1(x - 0.5, y), m2(x, y))` |

### Generating data (Create Data without references)

| Purpose | Formula |
|---|---|
| Product of sine and cosine | `sin(x) * cos(y)` |
| Gaussian spot of standard deviation 0.2 | `gauss2d(x, y, 0.2)` |
| Ring of radius 0.5 and width 0.05 | `ring(r, 0.5, 0.05)` |
| Fringes of period 0.25 | `1 + cos(tau * x / 0.25)` |
| Spiral phase of charge 3, from 0 to 2π | `mod(3 * atan2(y, x), tau)` |
| Wave moving outward (Time axis, 20 frames per period) | `cos(tau * (r / 0.5 - it / 20))` |
| Focused beam through focus (Z axis, Rayleigh length 1) | `gauss2d(x, y, 0.2 * sqrt(1 + z * z)) / (1 + z * z)` |
| Noisy test image | `poisson(100 * gauss2d(x, y, 0.3)) + randn(10, 2)` |

---

## Variables

| Variable | Meaning |
|---|---|
| `ix`, `iy` | Pixel indices, from 0. `iy = 0` is the bottom row |
| `x`, `y` | Physical coordinates of the pixel |
| `r` | `sqrt(x*x + y*y)`, the distance from the origin of the coordinates |

Each axis of the data adds an index and a value:

| Axis name | Index | Value |
|---|---|---|
| `Z` | `iz` | `z` |
| `Channel` | `ic` | `c` |
| `Time` | `it` | `t` |
| Any other axis, in order | `ia1`, `ia2`, … | `a1`, `a2`, … |

The dialog lists the variables available for the current data. In the Calculator, they are the axes of
the window's data: when one frame is processed, they give its current position.

Variable and reference names are case-sensitive. Function names, constants and `Max` / `Min` are not.

---

## Reading Data

| Form | Gives |
|---|---|
| `m1` | The value at the pixel being computed (same as `m1[ix, iy]`) |
| `m1[ix, iy]` | The value at a pixel. Non-integer indices are rounded |
| `m1[ix, iy, frame]` | The value at a pixel of a frame, by frame number (from 0) |
| `m1[ix, iy, iz:2]` | The value at a pixel of the frame at position 2 of the Z axis |
| `m1(x, y)` | The value at a physical position, interpolated bilinearly |
| `m1(x, y, frame)` | The same, for a given frame. Axes can be named as above |
| `m1.Max()`, `m1.Min()` | Largest / smallest value of the frame being read |
| `m1.Max(frame)`, `m1.Min(frame)` | Largest / smallest value of a given frame |
| `m1.MaxAll()`, `m1.MinAll()` | Largest / smallest value over all frames |

- `m1` and `m1[ix, iy]` match pixels **by index**. Use `m1(x, y)` when the grids differ.
- Axes are named by their index variable (`iz`, `ic`, `it`, `ia1`, …), and several can be given:
  `m1[ix, iy, iz:2, it:0]`. Axes that are not named keep their position. The position is an expression,
  so `it: it - 1` is valid. A frame number and named axes cannot be combined.
- Which frame is read when none is given:
  - **Calculator**: the frame being processed.
  - **Create Data**: the frame being computed if the reference has as many frames as the result,
    otherwise the frame the reference window shows.
- Structured data such as `Complex` is read as a number, in the value mode the window shows
  (magnitude, phase, real part, and so on).

---

## Operators and Constants

From lowest to highest precedence:

| Operators | Note |
|---|---|
| `a ? b : c` | `b` when `a` is true, else `c` |
| <code>&#124;&#124;</code> | |
| `&&` | |
| `==` `!=` `<` `<=` `>` `>=` | Give 1 or 0 |
| `+` `-` | |
| `*` `/` `%` | `%` keeps the sign of the left operand; see `mod` |
| `-` `+` `!` (unary) | |

Every value is a number, and any non-zero value counts as true. Constants: `pi`, `tau` (2π), `e`,
`true`, `false`. For a power write `pow(a, b)`; `^` is not accepted. A formula is written in ASCII
characters only.

---

## Functions

| Group | Functions |
|---|---|
| Trigonometric | `sin` `cos` `tan` `asin` `acos` `atan` `atan2(y, x)` `sinh` `cosh` `tanh` |
| Exponential | `exp` `log` (natural) `log2` `log10` `sqrt` `pow(x, y)` `hypot(x, y)` |
| Rounding and range | `abs` `sign` `floor` `ceil` `round` (half away from zero) `min(a, b)` `max(a, b)` `clamp(x, min, max)` `fract` `mod(a, b)` |

### Shapes

| Function | Gives |
|---|---|
| `rect(x)` | 1 for \|x\| ≤ 0.5, otherwise 0 |
| `circ(r)` | 1 for \|r\| ≤ 1, otherwise 0 |
| `circ(x, y)` | 1 for x² + y² ≤ 1, otherwise 0 |
| `tri(x)` | 1 − \|x\| for \|x\| < 1, otherwise 0 |
| `sinc(x)` | sin(πx) / (πx), 1 at 0 |
| `ring(r, radius, width)` | 1 where `r` is within `width/2` of `radius`, otherwise 0 |
| `gauss(x, sigma)` | exp(−x² / 2σ²) |
| `gauss2d(x, y, sigma)` | exp(−(x² + y²) / 2σ²) |

### Utilities

| Function | Gives |
|---|---|
| `step(edge, x)` | 0 for `x < edge`, otherwise 1 |
| `smoothstep(edge0, edge1, x)` | A smooth rise from 0 to 1 between the edges |
| `lerp(a, b, t)` | `a + (b - a) * t` |
| `sum(a, b, ...)` | Sum of two or more values, ignoring NaN; NaN only when all are NaN |
| `num(v)` / `num(v, a)` | `v`, or 0 / `a` when `v` is NaN |

### Random

| Function | Gives |
|---|---|
| `rand()` / `rand(min, max)` | Uniform random number |
| `randn()` / `randn(mean, sigma)` | Normal random number |
| `poisson(lambda)` | Random count with mean `lambda` |
| `salt(p)` | 1 with probability `p`, otherwise 0 |
| `noise(x, y)` | Smooth noise from 0 to 1; the same position always gives the same value |

Random values other than `noise` are new at every evaluation, including every update of a result that
follows its source.

---

## Values Outside the Data (NaN)

A read outside the data, or of a frame that does not exist, gives NaN, and NaN passes through
arithmetic. Use `num(v)` or `num(v, a)` to replace it, or `sum(...)` to add only the values present.

```
num(m1[ix + 10, iy]) + m2
```

NaN is not equal to itself, so `v != v` tests for it. As a condition, NaN is non-zero and therefore
counts as true.

---

## Results That Follow Their Source

### Sync source data (Calculator)

The result window follows the current frame of its source and is recomputed when the source is
refreshed. For multi-frame data, check **This frame only** to enable it. The formula of such a window
can be changed later with **Data > View > Edit Formula…**.

### Keep updated (Create Data)

The result is one frame, computed from the frame each reference shows. It is recomputed when a
reference is redrawn, changes its frame or value mode, or has its data replaced.

- In MxPlot.App the result is marked **fx** in the window list, its inputs are tinted while it is
  selected, and **Edit Formula…** in its context menu changes it.
- The result stops following when it or a reference is closed, and stays as an ordinary window.
- A result can be a reference of another result, but cannot depend on itself.
- If a reference changes size, the result keeps its grid; pixels the reference does not reach are NaN.

---

## Using Create Data from a Host Application

`CreateDataDialog` is part of `MxPlot.UI.Avalonia`. The host supplies the windows that can be
referenced.

```csharp
using MxPlot.UI.Avalonia.Views;

var plotters = new List<MatrixPlotter>(); // the windows your application keeps track of

var dialog = CreateDataDialog.Show(owner: null, candidates: () => plotters);
dialog.LiveCalculationStarted += calc =>
{
    // calc.Result follows calc.Inputs until calc.Ended is raised.
};
```

Call it on the UI thread.

---

## Notes and Limitations

- Results are `double`. The size of a new result is its pixel count × 8 bytes.
- For Virtual data, `MaxAll()` and `MinAll()` count only the frames whose range is already known.
- `m1(x, y)` interpolates within one frame; there is no interpolation along other axes.
- The formula history is kept only while the application is running.
