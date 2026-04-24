# MatrixPlotter Metadata Format Guide

**MxPlot.Core / MxPlot.UI.Avalonia — Metadata Conventions**

> Last Updated: 2026-04-24

*Note: This document is largely based on AI-generated content and requires further review for accuracy.*

## 📚 Table of Contents

1. [Overview](#overview)
2. [Metadata Dictionary Basics](#metadata-dictionary-basics)
3. [Key Namespaces](#key-namespaces)
4. [Format Header Metadata (Core API)](#format-header-metadata-core-api)
   - [API Reference](#api-reference)
   - [Storage Mechanism](#storage-mechanism)
   - [Guide for IO Handler Authors](#guide-for-io-handler-authors)
   - [Derivation and Round-Trip Behaviour](#derivation-and-round-trip-behaviour)
5. [Processing History (UI Layer)](#processing-history-ui-layer)
   - [History Entry Format](#history-entry-format)
   - [How History is Recorded](#how-history-is-recorded)
   - [VisibleSystemKeys Mechanism](#visiblesystemkeys-mechanism)
6. [UI Behaviour Summary](#ui-behaviour-summary)
7. [File Map](#file-map)

---

## Overview

`IMatrixData` (backed by `MatrixData<T>`) exposes a `Metadata` property - an `IDictionary<string, string>` that can freely hold any information accompanying the pixel data. Keys and values are both plain strings, used to store format-specific information, measurement parameters, comments, and so on.

The **MatrixPlotter** UI layer (`MxPlot.UI.Avalonia`) makes extended use of this same dictionary, storing three distinct categories of information:

| Purpose | Examples | Owner |
|---|---|---|
| **UI settings** | LUT min/max, display mode | MatrixPlotter system |
| **Format-specific data** | OME-XML header, FITS header cards | IO handlers (OmeTiffHandler, etc.) |
| **Processing history** | Crop, Duplicate operation log | MatrixPlotter system |

Some of these keys should **not** be freely edited or deleted by the user - for example, manually rewriting the OME-XML would break format integrity. However, `Metadata` is an `IDictionary` and the Core library provides no built-in mechanism to prevent writes.

This document therefore describes the **convention for recording format-header metadata** and explains how the Core library and UI layer cooperate:

- **Format headers** — metadata entries that store the raw header blob of the originating file format (e.g. FITS header text, OME-XML). Automatically treated as read-only and excluded from derivation
- **UI editing restrictions** — format-header keys are displayed with 🔒 and blocked from user edits (the UI layer's responsibility)
- **System-managed keys** — keys in the `mxplot.*` namespace reserved for internal use
- **Processing history** — automatic recording of operations applied to the data

> **Important**: The Core library (`MxPlot.Core`) does **not** physically prevent writes to the Metadata dictionary. `MarkAsFormatHeader` records the intent that a key is a format-specific header blob. This has two effects: (1) UI consumers treat the key as read-only, and (2) `CopyPropertiesFrom` skips it during derivation. The Core library does not enforce editing restrictions — that is the UI layer's responsibility. All other conventions described here (History, VisibleSystemKeys, UI display rules) are likewise **MatrixPlotter (UI layer) specific**.

---

## Metadata Dictionary Basics

```csharp
public interface IMatrixData
{
    IDictionary<string, string> Metadata { get; }
    // ...
}
```

- Keys are **case-insensitive** (`StringComparer.OrdinalIgnoreCase`)
- Values are always `string` — complex data should be serialized (JSON, CSV, XML, etc.)
- `CopyPropertiesFrom()` copies all metadata entries **except** format-header keys and their tracking key (see [Format Header Metadata](#format-header-metadata-core-api))

---

## Key Namespaces

| Key pattern | Owner | UI visibility | Editable | Copied by `CopyPropertiesFrom` | Examples |
|---|---|---|---|---|---|
| *(user-defined)* | User / IO handler | ✅ Shown | ✅ Yes | ✅ Yes | `user_note`, `experiment_id` |
| `mxplot.*` (general) | MatrixPlotter system | ❌ Hidden | ❌ No | ✅ Yes | `mxplot.lut.min`, `mxplot.metadata.format_header` |
| `mxplot.*` + `VisibleSystemKeys` | MatrixPlotter system | ✅ Shown (display name) | ❌ No | ✅ Yes | `mxplot.data.history` → "History" |
| *(any key)* + `MarkAsFormatHeader` | IO handler | ✅ Shown with 🔒 ¹ | ❌ No ¹ | ❌ **No** ² | `OME_XML`, `FITS_HEADER` |

> ¹ The UI layer reads the hint via `IsFormatHeader()` and enforces the restriction. The Core library itself does not prevent writes.
>
> ² Format-header entries describe the source file and become invalid after derivation (Crop, Filter, Slice, etc.). They are fully preserved by `Clone()` (exact duplicate of the same data).

The `mxplot.` prefix is defined in `PlotterConfigKeys.Prefix`. Keys matching this prefix are hidden from the Metadata tab by default, unless explicitly registered in `PlotterConfigKeys.VisibleSystemKeys`.

---

## Format Header Metadata (Core API)

Format-header keys are metadata entries that store the raw header blob of the originating file format (e.g. FITS header text, OME-XML). Marking a key as a format header has two effects:

1. **Read-only in UI** — consumers treat the key as read-only (editing disabled)
2. **Excluded from derivation** — `CopyPropertiesFrom` skips format-header entries because they describe the source file and become invalid after operations like Crop, Filter, or Slice

> `Clone()` (exact duplicate) copies **all** metadata including format headers. Only `CopyPropertiesFrom` (derivation) excludes them.

### API Reference

Three extension methods in `MatrixData.Static.cs` (`MxPlot.Core` namespace):

```csharp
// Mark key(s) as format headers
data.MarkAsFormatHeader("OME_XML", "FITS_HEADER");

// Check if a key is a format header
bool isFH = data.IsFormatHeader("OME_XML"); // true

// Get all format-header keys
IReadOnlySet<string> fhKeys = data.GetFormatHeaderKeys();
```

### Storage Mechanism

Format-header markers are stored as a **comma-separated list** inside the Metadata dictionary itself:

```
Metadata["mxplot.metadata.format_header"] = "OME_XML,FITS_HEADER"
```

This design was chosen deliberately:

| Benefit | Explanation |
|---|---|
| **Zero-cost round-trip** | Any format writer that persists `Metadata` (MXD, OME-TIFF, FITS) automatically preserves the format-header markers |
| **Self-describing** | The data itself records which keys are format headers — no static registry or DLL-load timing issues |
| **No Core model changes** | No additional fields or interfaces needed on `IMatrixData` |

The key `mxplot.metadata.format_header` is itself hidden from the UI by the `mxplot.*` reserved namespace rule.

### Guide for IO Handler Authors

When your file format reader loads metadata that is **authoritative to the format** (e.g. OME-XML embedded in TIFF, FITS header cards), mark it as a format header immediately after setting the value:

```csharp
// ✅ Recommended pattern for IO handlers
public static void ApplyMetadataToMatrixData(IMatrixData md, MyFormatMetadata meta)
{
    // Set the metadata value
    md.Metadata["MY_FORMAT_HEADER"] = meta.RawHeaderText;

    // Mark as format header
    // → UI treats as read-only; CopyPropertiesFrom skips on derivation
    md.MarkAsFormatHeader("MY_FORMAT_HEADER");
}
```

**Built-in examples:**

| Handler | Key | Content |
|---|---|---|
| `OmeTiffHandler` | `OME_XML` | Original OME-XML string from IMAGEDESCRIPTION tag |
| `FitsHandler` | `FITS_HEADER` | Raw 80-char FITS header cards |

**Guidelines:**

- Call `MarkAsFormatHeader` in the **read path** (not the write path)
- Choose a descriptive, uppercase key name that reflects the format
- The value can be large (full XML, all header cards) — the UI renders it with monospace font and copy button
- Do **not** use the `mxplot.*` prefix for your keys — that namespace is reserved for MatrixPlotter internals

### Derivation and Round-Trip Behaviour

**Direct load → save (same data):**

```
File Load (IO layer)
  │  md.Metadata["OME_XML"] = xml;
  │  md.MarkAsFormatHeader("OME_XML");
  ▼
Metadata dictionary:
  "OME_XML"                        = "<OME>..."
  "mxplot.metadata.format_header"  = "OME_XML"
  │
  │  Save to MXD / OME-TIFF / FITS → both entries persisted
  │  Re-load → both entries restored
  ▼
UI: IsFormatHeader("OME_XML") → true → read-only display
```

**Derivation (Crop, Filter, etc.):**

```
Source: OME-TIFF with OME_XML format header
  │
  │  CopyPropertiesFrom(source)
  │    → skips "OME_XML" (format header)
  │    → skips "mxplot.metadata.format_header" (format-header registry key)
  │    → copies user metadata, History, etc.
  ▼
Derived data: clean — no stale format headers
  │
  │  Save as FITS → no cross-format contamination
  ▼
Re-load FITS → FITS_HEADER is the only format header
```

This prevents the **cross-format contamination** problem where OME-XML would leak into FITS files (or vice versa) after format conversion.

---

## Processing History (UI Layer)

> **Scope**: This feature is entirely within `MxPlot.UI.Avalonia` (MatrixPlotter). The Core library has no knowledge of processing history.

### History Entry Format

History is stored as a JSON array under the system key `mxplot.data.history`:

```json
[
  {
    "op": "Crop",
    "at": "2026-04-12T15:30:00+09:00",
    "from": "Sample.ome.tif",
    "detail": "X=10 Y=20 W=100 H=100"
  },
  {
    "op": "Duplicate",
    "at": "2026-04-12T15:31:00+09:00",
    "from": "Crop of Sample.ome.tif"
  }
]
```

| Field | Required | Description |
|---|---|---|
| `op` | ✅ | Operation name (short identifier) |
| `at` | ✅ | ISO 8601 timestamp with timezone |
| `from` | ⚪ | Source MatrixPlotter window title or file name. `null` for newly-created data |
| `detail` | ⚪ | Human-readable parameter summary. `null` for parameter-less operations |

### How History is Recorded

`MatrixPlotter.AppendHistory()` is called at each processing completion point:

```csharp
// Signature
internal static void AppendHistory(
    IMatrixData data,
    string operation,
    string? from,
    string? detail = null)
```

Current call sites:

| Call site | `op` | `from` | `detail` |
|---|---|---|---|
| `ApplyCropResult` | `"Crop"` | Window title | `"X=10 Y=20 W=100 H=100"` (appends `" (frame N)"` for single-frame crop) |
| `DuplicateWindowAsync` | `"Duplicate"` | Window title | *(none)* |
| `ConvertValueTypeAsync` | `"Convert Type"` | Window title | `"float → double; scale [0, 255] → [0, 1]"` or `"float → int; direct cast"` |
| `ReverseStackAsync` | `"Reverse Stack"` | Window title | `"all frames"` or `"axis: Z"` |

The method:
1. Parses the existing JSON array (if any, e.g. inherited via `CopyPropertiesFrom`)
2. Appends a new entry
3. Serializes back to the metadata dictionary

### VisibleSystemKeys Mechanism

The History key uses the `mxplot.*` namespace for protection, but needs to be **visible** in the Metadata tab. This is achieved through `PlotterConfigKeys.VisibleSystemKeys`:

```csharp
internal static class PlotterConfigKeys
{
    public const string Prefix = "mxplot.";

    // Internal key → Display name
    public static readonly Dictionary<string, string> VisibleSystemKeys = new()
    {
        ["mxplot.data.history"] = "History",
    };

    // mxplot.* keys are reserved, EXCEPT those in VisibleSystemKeys
    public static bool IsReserved(string key) =>
        key.StartsWith(Prefix) && !VisibleSystemKeys.ContainsKey(key);
}
```

In the Metadata tab, the key appears as `🔒 History` (with the display name from the mapping, not the internal key).

**Why not use `MarkAsFormatHeader`?**

| | `MarkAsFormatHeader` | `mxplot.*` namespace |
|---|---|---|
| User can create a key with the same name | ⚠️ Yes (collision risk) | ❌ Impossible |
| Protection mechanism | CSV in metadata | Key prefix rule |
| Survives derivation (`CopyPropertiesFrom`) | ❌ No (excluded) | ✅ Yes (copied) |
| Suitable for | IO handler format blobs (file-specific) | System-managed internal data |

---

## UI Behaviour Summary

The Metadata tab in MatrixPlotter handles three categories of keys:

```
MatrixData.Metadata
│
├── User keys: "user_note" = "..."
│   → Displayed as-is, fully editable
│
├── Format-header keys: "OME_XML" = "<OME>..."
│   → Displayed as "🔒 OME_XML", view-only (TextBox.IsReadOnly)
│   → Delete / Save buttons disabled
│   → Managed by: mxplot.metadata.format_header CSV
│   → NOT copied by CopyPropertiesFrom (excluded on derivation)
│
├── Hidden system keys: "mxplot.lut.min" = "0"
│   → Not shown in the list (PlotterConfigKeys.IsReserved = true)
│
└── Visible system keys: "mxplot.data.history" = "[...]"
    → Displayed as "🔒 History" (display name from VisibleSystemKeys)
    → View-only, user cannot create/delete
    → Managed by: mxplot.* namespace + VisibleSystemKeys exception
```

Helper methods in `MatrixPlotter.InfoTab.cs`:

| Method | Purpose |
|---|---|
| `ResolveMetaKey(rawDisplayKey)` | `"🔒 History"` → `"mxplot.data.history"` (reverse-lookup) |
| `IsDisplayKeyReadOnly(rawDisplayKey, data)` | Returns `true` for any 🔒-prefixed key or format-header key |

---

## File Map

```
MxPlot.Core/
├── MatrixData.Static.cs         ← MarkAsFormatHeader / IsFormatHeader / GetFormatHeaderKeys
│                                   (extension methods on IMatrixData)
│                                   CopyPropertiesFrom — skips format-header keys
├── IO/
│   └── FitsHandler.cs           ← MarkAsFormatHeader("FITS_HEADER")
│
MxPlot.Extensions.Tiff/
│   └── OmeTiffHandler.cs        ← MarkAsFormatHeader("OME_XML")
│
MxPlot.UI.Avalonia/Views/
├── PlotterConfigKeys.cs         ← Prefix, VisibleSystemKeys, IsReserved()
├── MatrixPlotter.History.cs     ← HistoryMetaKey, AppendHistory()
└── MatrixPlotter.InfoTab.cs     ← ResolveMetaKey(), IsDisplayKeyReadOnly(), RefreshMetaTab()
```
