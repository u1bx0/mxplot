# MxView Coordinate Systems Guide

**Created**: 2026-04-30  
**Updated**: 2026-04-30  
**Target**: `MxPlot.UI.Avalonia` — `MxView`, `RenderSurface`, `OverlayManager`

---

## Overview

`MxView` operates across **three distinct coordinate layers**. Understanding how they relate is
essential when writing pointer-event handlers, placing overlay objects, or consuming the
`ScreenToData` / `DataToScreen` API.

| Layer | Name | Origin | Unit | Who uses it |
|-------|------|---------|------|-------------|
| **Screen** | Screen / Control | Top-left of `MxView`, Y-down | Device-independent pixels | `PointerMoved`, `GetPosition` |
| **World** | World / Bitmap-pixel | Top-left of bitmap, Y-down | Bitmap pixel index (integer = centre) | All overlay objects |
| **Data** | Data / Physical | Bottom-left of data, Y-up | `XMin`…`XMax`, `YMin`…`YMax` | `MatrixData` scale, `ScreenToData` |

---

## Layer 1 — Screen Coordinates

The raw position reported by Avalonia pointer events, **relative to the `MxView` control**.

```csharp
_mview.PointerMoved += (s, e) =>
{
    Point screen = e.GetPosition(_mview);
    // screen.X ∈ [0, MxView.Bounds.Width)
    // screen.Y ∈ [0, MxView.Bounds.Height), Y increases downward
};
```

- Affected by control size, zoom, pan, and `ViewTransform`.
- Not useful for data comparison without conversion.

---

## Layer 2 — World Coordinates (Bitmap-pixel Space)

The coordinate system of the raw bitmap produced by `BitmapWriter<T>`.

- **Origin**: top-left corner of the bitmap, `(0, 0)`.
- **Axis direction**: X right, Y **down** (screen convention).
- **Integer value = pixel centre**: world `(3, 7)` refers to the centre of the pixel at column 3, row 7.
- This is the native coordinate of **all overlay objects** (`LineObject.P1/P2`, `RectObject.Origin`, etc.).

> **Why World and not Data?**  
> Overlay objects use bitmap-pixel (World) coordinates because bitmap/screen space is
> the universal standard for image-editing tools (pixel-perfect hit-testing, sub-pixel
> rendering, rotation/flip handling). Data coordinates are dataset-specific and require
> a scale definition (`XStep`, `YMin`, etc.) that may not always be present.
> World coordinates are always valid regardless of whether `MatrixData.SetXYScale` has been called.

### Accessing World coordinates

`AvaloniaViewport` (obtained internally via `RenderSurface.GetOverlayViewport()`) provides
`ScreenToWorld` / `WorldToScreen` for the overlay drawing system. External consumers should
use the `MxView` API below rather than reaching into the viewport directly.

---

## Layer 3 — Data Coordinates (Physical Scale)

The physical-unit space defined by `IMatrixData.SetXYScale(xMin, xMax, yMin, yMax)`.

- **Origin**: bottom-left of the data grid.
- **Axis direction**: X right, Y **up** (mathematical / scientific convention).
- `(XMin, YMin)` is the bottom-left corner; `(XMax, YMax)` is the top-right corner.
- Row 0 of the bitmap corresponds to `YMax` (top of data); the last row corresponds to `YMin`.

The conversion between World `(bx, by)` and Data `(dx, dy)`:

```
dx = XMin + (bx - 0.5) * XStep      // XStep = (XMax - XMin) / (XCount - 1)
dy = YMax - (by - 0.5) * YStep      // YStep = (YMax - YMin) / (YCount - 1)
```

When no `Scale2D` is passed to the `MatrixData` constructor, a default scale of
`XMin = 0, XMax = XCount - 1` (and equivalently for Y) is applied automatically,
so `XStep == 1` in that case — pixel indices and Data coordinate values coincide.
`ScreenToData` therefore **always** returns Data coordinates.

---

## Conversion API on `MxView`

These two public methods handle all three layers and all `ViewTransform` orientations:

```csharp
// Screen → Data coordinates
Point dataPos = _mview.ScreenToData(e.GetPosition(_mview));

// Data → Screen
Point screenPos = _mview.DataToScreen(new Point(dx, dy));
```

Both methods internally route through `RenderSurface.ScreenToBitmapPixel`, which is the
single Transform-aware path shared with the overlay drawing system.
The full pipeline is:

```
Screen  ──[ScreenToBitmapPixel]──▶  World (bx, by)  ──[XMin/YMax/XStep/YStep]──▶  Data (dx, dy)
                                                                                           ▲
DataToScreen reverses this pipeline ───────────────────────────────────────────────────────┘
```

### Typical usage patterns

**Pointer tracking with physical units:**
```csharp
_mview.PointerMoved += (s, e) =>
{
    var d = _mview.ScreenToData(e.GetPosition(_mview));
    StatusLabel.Text = $"X={d.X:F3}  Y={d.Y:F3}";
};
```

**Placing an Avalonia element at a data position:**
```csharp
var screen = _mview.DataToScreen(new Point(targetX, targetY));
Canvas.SetLeft(marker, screen.X);
Canvas.SetTop(marker, screen.Y);
```

**Working with overlay objects** — overlay `P1` / `P2` / `Origin` are in World space.
Convert a Data coordinate to World before assigning:

```csharp
// Data → World
double bx = (dataX - md.XMin) / md.XStep + 0.5;
double by = (md.YMax - dataY) / md.YStep + 0.5;
var line = new LineObject { P1 = new Point(bx, by), ... };
```

---

## ViewTransform and Coordinate Conversion

`MxView.Transform` (`ViewTransform` enum) applies a geometric transformation to the
displayed bitmap: `FlipH`, `FlipV`, `Rotate90CW`, `Rotate90CCW`, `Rotate180`, `Transpose`.

**`ScreenToData` and `DataToScreen` are fully Transform-aware** — they produce the correct
Data coordinates regardless of which `ViewTransform` is active.
This means the same `ScreenToData` call works correctly on `MainView` (typically `None`),
`BottomView` (`FlipV`), and `RightView` (`Rotate90CCW`) without any additional handling.

Overlay objects always store coordinates in the **untransformed World space**.
The overlay rendering system applies the transform when drawing, so assigning a World
position to an overlay object is independent of the current `ViewTransform`.

---

## Summary Table

| Question | Answer |
|----------|--------|
| What does `e.GetPosition(_mview)` return? | Screen coordinates |
| What coordinate does `LineObject.P1` use? | World (bitmap-pixel, Y-down, top-left origin) |
| What does `MxView.ScreenToData` return? | Data coordinates (Y-up, bottom-left origin); with default scale, values equal pixel indices |
| Does `ScreenToData` handle `ViewTransform`? | Yes — all six transform cases are handled |
| Y-axis direction in Data space? | **Upward** — `YMax` is at the top of the view |
| Y-axis direction in World / Screen space? | **Downward** — row 0 is at the top |
| Integer World value meaning? | Pixel centre (`(0,0)` = centre of top-left pixel) |
