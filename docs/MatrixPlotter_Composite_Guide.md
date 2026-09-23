# MatrixPlotter — Composite Rendering Guide

**Created**: 2026-08-20  
**Updated**: 2026-09-23

Composite mode renders several frames along a chosen axis at once, each tinted with its own colour
and contrast, and blends them into a single image — the standard way multi-channel fluorescence
data is displayed, and also how MxPlot shows ordinary RGB colour images.

Composite was introduced as a rendering-only base in 0.2.0 and became a complete workflow in
0.3.0 (settings panel, persistence, orthogonal views, Extract integration, grayscale conversion).
0.3.0 also generalized it from a hardcoded **Channel**-only axis to **any axis** (see
[Requirements and Entry Points](#requirements-and-entry-points)).

---

## Contents

1. [Requirements and Entry Points](#requirements-and-entry-points)
2. [Per-Channel Settings — `BlendRecipe`](#per-channel-settings--blendrecipe)
3. [Blend Modes](#blend-modes)
4. [Value Range: Global vs Channel-wise](#value-range-global-vs-channel-wise)
5. [Default Channel Colours](#default-channel-colours)
6. [RGB Colour Images](#rgb-colour-images)
7. [Interaction with Other Features](#interaction-with-other-features)
8. [Persistence](#persistence)
9. [Controlling Composite from Code](#controlling-composite-from-code)
10. [Limitations](#limitations)

---

## Requirements and Entry Points

Composite mode can operate on **any axis** — Z, Time, FOV, a custom axis, not just one literally
named `Channel`. Only one axis can be composited at a time; the others remain ordinary
LUT-rendered navigation axes. (Before 0.3.0's generalization, Composite was hardcoded to an axis
named `Channel`; that restriction is gone, but the common case — a genuine multi-channel or RGB
dataset — still just works the same way it always did, since that axis is still usually named
`Channel`.)

There are four ways a window enters Composite mode:

| Trigger | Behaviour |
|---|---|
| **Axis context menu** | Right-click any axis tracker → **Switch to Composite Mode**. Offered for every axis, not just one named `Channel` |
| **RGB auto-open** | A `byte` dataset whose Channel axis is tagged R/G/B opens directly in Composite mode with the original colours reproduced. See [RGB Colour Images](#rgb-colour-images) |
| **Metadata restore** | A file carrying `mxplot.render.mode = Composite` reopens in Composite mode on the axis named by `mxplot.composite.axis`, with its saved recipes. See [Persistence](#persistence) |
| **Host code** | `plotter.EnterCompositeMode(axis)`, a public Facade a host can call programmatically. See [Controlling Composite from Code](#controlling-composite-from-code) |

While Composite mode is active:

- The titlebar icon changes from the LUT gradient to the composite (Venn) icon.
- The LUT toolbar is replaced by a Composite header row with the same shape — hamburger, mode
  selector, value-range bar, revert — so the two modes feel identical to operate. Its tooltip
  names the composited axis (`"Composite axis: Z"`, etc.), since that is no longer implicitly
  "Channel".
- The composited axis's own `Axis.Index` is pinned to `0` for the whole session — not
  `MatrixData.ActiveIndex` itself, which is a single flat frame index over *all* axes, not a
  per-axis coordinate. Pinning the axis coordinate means `ActiveIndex` always resolves to
  "channel 0 at the current position of every other axis" (e.g. current Z, current T) — 0 only
  when every other axis also happens to be at 0. That axis is no longer a navigation axis; every
  position along it is on screen at once. This matters for any operation that would otherwise
  restrict itself to the active frame (see
  [Interaction with Other Features](#interaction-with-other-features)).

Entering Composite mode also **promotes the target axis to a `ColorAxis`** (a `TaggedAxis`
subtype, renamed from `ColorChannel` in 0.3.0 — the on-disk discriminator string is `"colored"`
either way, so old files still load), so that per-channel names and colours exist and can
round-trip to formats that carry them (OME-TIFF).

### Promoting a scaled axis loses its physical scale

`ColorAxis`/`TaggedAxis` are always index-based (`0 .. Count-1`), so promoting an axis that has a
real physical scale — Z in µm, Time in seconds — discards that scale: after promotion the axis is
just channel positions `0, 1, 2, ...`, with no way to recover the original min/max/unit from the
promoted axis itself. Because this is a real, sometimes-surprising loss, entering Composite on a
**non-index-based** axis shows a confirmation dialog first; an axis that is already index-based
(a genuine Channel axis, or one already tagged) skips it, since nothing is actually lost there.

**Revert restores the scale, but only within the same session.** The header's revert button
restores from a snapshot taken when the window was set up (`_scaleSnapshot`), which does include
the pre-promotion Min/Max/Unit — so Promote → tweak recipes → Revert correctly gets the original
scale back. Closing the window (or the file) and reopening it does **not**: the physical scale
is not written to file metadata, only the fact that some axis is now index-based/composited is.
A round-trip through save-and-reload therefore leaves the promoted axis permanently index-based,
even if you never touched Revert. This is a known, deliberate limitation, not a bug — see
[Limitations](#limitations).

---

## Per-Channel Settings — `BlendRecipe`

Each channel is described by one `BlendRecipe` (`MxPlot.UI.Avalonia.Rendering`):

```csharp
public record BlendRecipe(
    bool   IsVisible,
    int    ColorArgb,
    double ValueMin,
    double ValueMax,
    double Gain  = 1.0,
    double Gamma = 1.0);
```

| Field | Meaning |
|---|---|
| `IsVisible` | Whether the channel contributes to the blend. Toggled per channel in the settings panel |
| `ColorArgb` | The channel's tint |
| `ValueMin` / `ValueMax` | The channel's display range — the same idea as the LUT-mode value range, applied per channel |
| `Gain` | Linear multiplier applied after range normalisation |
| `Gamma` | Gamma correction applied after gain |

The settings panel shows one row per channel (`BlendRecipeBar`) with a colour swatch, a visibility
toggle, a histogram, and the range/gain/gamma controls.

---

## Blend Modes

| Mode | Behaviour | Used for |
|---|---|---|
| `Additive` | Channel contributions are summed | Multi-colour fluorescence, and RGB reconstruction |
| `Maximum` | The brightest channel wins per pixel | Depth / time colour coding |

Channel composites are **always `Additive`**, and there is deliberately no UI to change it: adding
pure primaries is exactly what reconstructs a normal colour image. `Maximum` exists in the
renderer and in the persistence format for backward compatibility with files written by older
builds, and because "brightest wins" is conceptually close to depth/time colour coding. The
ColorCoded feature 0.3.0 actually shipped (see [Limitations](#limitations)) does **not** render through it,
though: it has its own independent `ColorCodedBitmapWriter` — Composite's `Maximum` blend still
picks a *value* per pixel, one full frame at a time, where ColorCoded needs a per-pixel *winning
axis index* out of a whole scanned range, which is a different computation entirely
(`ExtremumIndexOperation` in Core). (Its `Color (RGB-Max)`/`Color (RGB-Add)` picks do reuse Composite's per-pixel `Maximum`/`Additive` blend, but apply it to the depth-tinted slices of the swept range rather than to channels.)

---

## Value Range: Global vs Channel-wise

Composite mode offers two range scopes, switched from the settings panel:

| Scope | Behaviour |
|---|---|
| **Global** (default) | One Min/Max, owned by the header range bar, applied to every channel |
| **Channel-wise** | Each channel evaluates and owns its own Min/Max; the header bar shows a read-only union of them |

The header range bar is the very same `ValueRangeBar` control the LUT toolbar uses, so Composite
mode gets the same **Fixed / Current / All / ROI** menu and the same out-of-range badge. In
Channel-wise scope each channel row carries its own mode.

When a session starts, each channel's default Min/Max is that channel's **current frame** range —
the same definition as LUT mode's `Current`, not a full Z/T-stack scan. That keeps the meaning
unambiguous when more than one non-Channel axis is present.

---

## Default Channel Colours

Colours are chosen in this priority order:

1. `ColorAxis.AssignedColors`, when the axis already carries per-channel colours (e.g. restored
   from OME-TIFF metadata) and the count matches.
2. A `byte` dataset with exactly 3 channels — the conventional **red / green / blue** triplet,
   since that combination reads as a colour image far more often than as a three-colour
   fluorescence composite.
3. Otherwise a fluorescence palette, extended with evenly spaced HSV hues when there are more
   channels than palette entries.

Colours can be changed per channel from the settings panel; the choice is written back to the
axis, so it survives a save/reload.

---

## RGB Colour Images

An ordinary colour image loaded through `MxPlot.Extensions.Images` becomes a `byte` matrix with a
three-tag Channel axis. Two behaviours follow from that:

**Automatic composite on open.** When the Channel axis is a genuine R/G/B triplet
(`ColorAxis.IsRgbTriplet` — tags `R`/`G`/`B` or `Red`/`Green`/`Blue`, in that order), the
window opens directly in Composite mode using pure primaries and a **Fixed** range of `0–255`.
The fixed full-byte range matters: using each channel's measured range instead would stretch the
channels independently, so an image with no bright blue would come out yellow.

The test is deliberately on the *tags*, not on the channel count — a three-channel fluorescence
stack is not an RGB image and opens in LUT mode as before.

**Convert to Grayscale.** The complementary operation collapses the Channel axis back to a single
channel, via `GrayscaleOperation` in `MxPlot.Core`:

```csharp
using MxPlot.Core.Processing;

var gray = data.Apply(new GrayscaleOperation(
    AxisName: "Channel",
    Method: GrayscaleMethod.Auto));
```

| `GrayscaleMethod` | Behaviour |
|---|---|
| `Auto` | Rec.709 luma for an R/G/B triplet, plain mean for any other channel axis |
| `Mean` | Arithmetic mean of all channels; works for any channel count |
| `LumaRec709` | `0.2126 R + 0.7152 G + 0.0722 B`; requires exactly 3 channels |

The UI exposes this through a dedicated grayscale dialog.

---

## Interaction with Other Features

Because the composited axis's coordinate is pinned to `0` (see above), `ActiveIndex` always
resolves to channel 0 at the current position of every other axis. Operations that would normally
act on "the current frame" are redirected to act on **the whole composited axis at the current
position of every other axis** instead — the channel cube.

| Feature | Behaviour in Composite mode |
|---|---|
| **Extract Frame** (main and orthogonal views) | Extracts every channel at the current position, not just channel 0. The new window opens already in Composite mode with the parent's recipes and a Fixed range |
| **This Frame Only** (Log Transform, Normalize, Spatial Filter) | Processes the channel cube rather than a single channel, so no channel is silently dropped |
| **Orthogonal views** (XZ / YZ) | Composited the same way as the main view |
| **XY projection window** | Seeded with the parent's composite state |
| **Derived / linked windows** | Live-derived followers keep their own display settings while following the source's content |

If the composited axis is no longer present after an operation, the result simply stays in LUT
mode rather than forcing Composite back on.

The **revert** button in the Composite header restores the render state captured when the file was
opened — the twin of the LUT toolbar's revert button, sharing the same mechanism. This is the same
mechanism that restores a promoted axis's physical scale within a session; see
[Promoting a scaled axis loses its physical scale](#promoting-a-scaled-axis-loses-its-physical-scale)
for why that specific part of it does not survive a save/reload.

---

## Persistence

Composite state round-trips through `IMatrixData.Metadata` under the `mxplot.composite.*` keys,
with `mxplot.render.mode` acting as the "which mode does this file open in?" discriminator.
The key is **absent** for LUT mode, which is what keeps pre-0.3.0 files opening unchanged.
`mxplot.composite.axis` records *which* axis is composited — introduced alongside 0.3.0's
generalization away from a hardcoded "Channel"; there is deliberately no fallback to "Channel"
when the key is absent, since no file written before this existed could have meant anything else,
and every file written by 0.3.0+ carries it whenever Composite is the saved mode.

See the [Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md#system-key-reference-ui-layer)
for the full key table.

---

## Controlling Composite from Code

`MatrixPlotter` exposes a public Facade for driving Composite mode programmatically (since 0.4.0) —
a host (e.g. a live camera preview pushing an RGB channel cube) does not need to reach into `MainView`:

```csharp
plotter.EnterCompositeMode(plotter.MatrixData.Axes.FindAxis("Channel")); // must be the data's own axis instance
plotter.CompositeRecipes = recipes; // IReadOnlyList<BlendRecipe>, one per channel
```

`EnterCompositeMode(Axis channelAxis)` performs the same orchestration `MatrixPlotter` runs when the
user switches modes from the axis context menu — promoting the axis to a `ColorAxis`, building
default recipes, pinning the composited axis's coordinate to `0` (see
[Requirements and Entry Points](#requirements-and-entry-points) for why that isn't the same as
pinning `ActiveIndex` itself), rebuilding the header and settings panel, wiring the orthogonal
views. It no-ops silently if `MatrixData` is `null`; `channelAxis` must be one of the data's own
`Axis` instances (e.g. `MatrixData.Axes.FindAxis("Channel")`), not a freshly constructed one.
`CompositeRecipes` can only be set once already in Composite mode, and its count must match the
number of channels `EnterCompositeMode` was called with.

Underneath the Facade, these are the `MxView` properties that actually get driven —
`RenderingMode`, `CompositeRecipes`, `CompositeBlendMode`, `CompositeFrameIndices` — available
directly should a host need finer control than the Facade gives (e.g. `CompositeFrameIndices[i]`,
the real source-frame index channel `i` currently maps to):

```csharp
plotter.MainView.RenderingMode = RenderingMode.Composite;
plotter.MainView.CompositeRecipes = recipes;          // IReadOnlyList<BlendRecipe>
plotter.MainView.CompositeBlendMode = BlendMode.Additive;
plotter.MainView.CompositeFrameIndices = frameIndices; // source frame per channel
```

At this level the caller is responsible for keeping `CompositeFrameIndices` consistent with the
data on screen — none of the orchestration `EnterCompositeMode` does happens automatically.

In practice, most external code should either use the Facade above, or let the user (or the saved
metadata) enter Composite mode, and limit itself to supplying data whose Channel axis is shaped and
tagged the way it wants. For an RGB image, tagging the axis `R`/`G`/`B` is enough to get a correct
composite automatically:

```csharp
using MxPlot.Core;

// A byte matrix whose Channel axis is a tagged RGB triplet opens in Composite mode.
var rgbAxis = ColorAxis.CreateRgb();   // tags R/G/B + pure primary colours
```

---

## Limitations

- **One composited axis at a time.** Compositing two axes simultaneously (e.g. Channel *and*
  Z) is not supported — entering Composite on a second axis exits it on the first.
- **A promoted axis's physical scale does not survive save/reload.** Revert restores it within
  the same session (see
  [Promoting a scaled axis loses its physical scale](#promoting-a-scaled-axis-loses-its-physical-scale)),
  but nothing writes the pre-promotion Min/Max/Unit to file metadata, so a save-and-reopen leaves
  the axis permanently index-based even if Revert was never used.
- **`RenderingMode.ColorCoded` is a separate, independent feature, not a Composite mode.** It
  ships in 0.3.0 alongside this generalization (a live depth/time colour-coded projection,
  entered via the orthogonal-view projection selector's `Color (Max)`/`Color (Min)`/`Color (RGB-Max)`/`Color (RGB-Add)` options, not
  through the Composite axis-context-menu path this guide describes) and is deliberately mutually
  exclusive with Composite — see [Blend Modes](#blend-modes) for why it does not actually share
  `CompositeBitmapWriter` despite the conceptual similarity to `Maximum` blending.
- **`BlendMode` is fixed to `Additive`** for channel composites.

---

## Related Documents

- [MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md)
- [MxPlot.UI.Avalonia Overview](./MxPlotUIAvalonia_Overview.md)
- [MatrixPlotter Metadata Format Guide](./MatrixPlotter_MetadataFormat_Guide.md)
