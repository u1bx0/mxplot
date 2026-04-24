# Frame Sharing Model and Copy Semantics of `MatrixData<T>`

**Zero-cost frame reordering through reference sharing, with explicit control over data independence via deep copy.**

`MatrixData<T>` is a multi‑frame 2D data container designed for scientific visualization, numerical analysis, and time‑series matrix operations.  
Its core strength lies in a **precise and predictable data‑sharing model** that enables:

- **O(1) frame reordering**  
- **Explicit deep copying**  
- **Shared‑frame mutation semantics**  
- **Lazy min/max invalidation**  
- **High‑performance direct array access**

This document explains how frame references behave across operations such as `Reorder`, `Duplicate`, `GetArray`, and `SetValueAt`.

---

# 1. Overview

Each frame in `MatrixData<T>` internally stores a **2D array flattened into a 1D buffer**: 

```
e.g.
MatrixData<float>
 ├─ Frame 0 → float[] buffer0
 ├─ Frame 1 → float[] buffer1
 ├─ Frame 2 → float[] buffer2
 ...
```

Operations such as `Reorder` do **not** duplicate these buffers.  
Instead, they rearrange **references** to existing buffers.

This enables extremely fast operations while maintaining full data consistency.

---

# 2. Shallow Reordering (O(1))

`Reorder(int[] order)` creates a new `MatrixData<T>` whose frames **reference the same underlying arrays** as the original.

### Example

```csharp
var md = new MatrixData<float>(5, 5, 5);
md.ForEach((i, array) => array.AsSpan().Fill(i));

var md2 = md.Reorder([0, 0, 1, 1, 2]);
```

### Resulting reference structure

```
md (original)
 ├─ F0 → [0 0 0 ...]
 ├─ F1 → [1 1 1 ...]
 ├─ F2 → [2 2 2 ...]
 ├─ F3 → [3 3 3 ...]
 └─ F4 → [4 4 4 ...]

md2 (reordered)
 ├─ F0 → md.F0
 ├─ F1 → md.F0   (shared)
 ├─ F2 → md.F1
 ├─ F3 → md.F1   (shared)
 └─ F4 → md.F2
```

### Key property  
Reordering is **instant** and **memory‑efficient** because it only rearranges references.

---

# 3. Mutating Shared Frames

When two frames share the same underlying array, modifying one frame modifies all frames that reference that array.

### Example

```csharp
md2.GetArray(0).AsSpan().Fill(999);
```

### Resulting structure

```
md2
 ├─ F0 → [999 999 999 ...]
 └─ F1 → [999 999 999 ...]  (same array)

md
 └─ F0 → [999 999 999 ...]  (same array as md2.F0)
```

### Diagram

```
        +---------------------------+
md.F0 --|                           |
md2.F0 -|   float[] buffer0 (shared)| ← mutated
md2.F1 -|                           |
        +---------------------------+
```

This is **intentional** and forms the basis of the efficient aliasing model.

---

# 4. Lazy Min/Max Invalidation

Whenever a frame’s underlying array is modified (via `GetArray` or `SetValueAt`), its cached min/max values are **invalidated**.

They are recomputed **only when needed**, e.g.:

```csharp
var range = md2.GetValueRange(0); // triggers recalculation
```

This avoids unnecessary computation when performing bulk updates.

**Exception**: When using `ForEach(Action<int, T[]>)`, each frame’s min/max is forcibly recomputed immediately after the delegate finishes, so no manual invalidation is required in that case.


⚠️ **Important**: After calling `GetValueRange()`, any further manual modifications to the raw `T[]` returned by `GetArray()` will not be detected automatically; you must call `Invalidate()` again to refresh the cached min/max values.


## 4.1 Cross‑Instance ValueRange Synchronization

When `Reorder(deepCopy: false)` creates a shallow copy, not only are the `T[]` frame buffers shared — the **`List<double>` instances** inside each `ValueRange` entry are also shared by reference.

### Internal structure

```
_valueRangeMap (Dictionary<T[], ValueRange>)
  key = T[] reference
  value = ValueRange { MinValues: List<double>, MaxValues: List<double> }
```

### How sharing works

```
src:    _valueRangeMap[arrA] = ValueRange(listMin_A, listMax_A)
linked: _valueRangeMap[arrA] = ValueRange(listMin_A, listMax_A)  ← same List<double> instances
```

Because `Invalidate()` calls `List<double>.Clear()` on the shared list, the invalidation propagates to **all** `MatrixData` instances that hold the same `T[]` key:

```csharp
var md = new MatrixData<float>(5, 5, 3);
// ... fill data ...
var range = md.GetValueRange(0);     // populate cache: min=0, max=0

var linked = md.Reorder([0, 1, 2]); // shallow copy — List<double> refs shared

linked.SetValueAt(0, 0, 0, -99f);   // modifies arrA → linked.Invalidate(0)
                                      // → listMin_A.Clear() — affects BOTH instances

md.GetValueRange(0);                 // listMin_A.Count == 0 → IsValid = false
                                      // → RefreshValueRange → scans arrA → min = -99  ✅
```

### Why this matters

Without reference sharing (e.g., if `new List<double>(vmins)` were used to create value copies), `md.GetValueRange(0)` would return the **stale** cached value `(0, 0)` instead of the correct `(-99, 0)`.

### Deep copy behavior

`Reorder(deepCopy: true)` creates new empty `List<double>` instances for each frame. No cache state is shared with the original.

### Virtual (MMF‑backed) data

For `MatrixData` backed by `VirtualFrames<T>`, the same sharing model applies:
- `RoutedFrames<T>` routes logical indices to physical frames in the underlying `IFrameKeyProvider<T>`
- `GetFrameKey()` returns the same `T[]` reference for the same physical frame
- `ValueRange` entries are keyed by this shared `T[]`, ensuring cross‑instance synchronization


---

# 5. Deep Copy via `Duplicate()` / `Clone()`

`Duplicate()` (an extension method that calls `Clone()`) creates a **full deep copy**:

- All frame arrays are duplicated — no references are shared with the original
- Subsequent mutations are completely isolated
- `Clone()` automatically selects the appropriate copy strategy based on `IsVirtual`:

| Source | Clone strategy |
|---|---|
| In-memory | Conventional in-memory deep copy |
| Virtual (MMF-backed) | Frames are streamed one at a time to a temporary `.mxd` file — peak memory is proportional to a single frame, avoiding OOM for large datasets |

### Example

```csharp
var md3 = md2.Duplicate();
```

### Diagram

```
md2
 ├─ F0 → bufferA
 ├─ F1 → bufferA (shared)
 ├─ F2 → bufferB
 ├─ F3 → bufferB (shared)
 └─ F4 → bufferC

md3 (deep copy)
 ├─ F0 → bufferA' (copy of A)
 ├─ F1 → bufferA'' (copy of A)
 ├─ F2 → bufferB' (copy of B)
 ├─ F3 → bufferB'' (copy of B)
 └─ F4 → bufferC' (copy of C)
```

### Key property  
After duplication, **no mutation in md3 affects md or md2**.

---

# 6. Complete Example: Reference Evolution

Below is a full evolution diagram corresponding to your test code.

### Step 1 — Original

```
md
 ├─ F0 → A
 ├─ F1 → B
 ├─ F2 → C
 ├─ F3 → D
 └─ F4 → E
```

### Step 2 — Reorder

```
md2 = md.Reorder([0,0,1,1,2])

md2
 ├─ F0 → A
 ├─ F1 → A
 ├─ F2 → B
 ├─ F3 → B
 └─ F4 → C
```

### Step 3 — Mutate md2.F0

```
A = [999 ...]
```

Affects:

- md.F0
- md2.F0
- md2.F1

### Step 4 — Duplicate

```
md3 = md2.Duplicate()

md3
 ├─ F0 → A' (copy)
 ├─ F1 → A'' (copy)
 ├─ F2 → B' (copy)
 ├─ F3 → B'' (copy)
 └─ F4 → C' (copy)
```

### Step 5 — Mutate md3.F0

Only md3.F0 changes; all others remain intact.

---

# 7. Summary Table

| Operation | Copies Arrays? | Shares References? | Shares ValueRange? | Mutations Propagate? | Complexity |
|----------|-----------------|---------------------|---------------------|----------------------|------------|
| `Reorder()` | No | Yes | Yes | Yes | **O(1)** |
| `GetArray()` | No | Yes | — | Yes | O(1) |
| `SetValueAt()` | No | Yes | — | Yes | O(1) |
| `Duplicate()` | **Yes** | No | No | No | **O(N)** |
| `Clone()` | Yes | No | No | No | O(N) |

---

# 8. Design Philosophy

The `MatrixData<T>` model is built on three principles:

### 1. **Explicit Mutability**
Users always know when they are modifying shared data.

### 2. **Zero‑Cost Reordering**
Reordering frames should be instantaneous and allocation‑free.

### 3. **User‑Controlled Copying**
Deep copying is explicit, never implicit.

This mirrors the design of high‑performance scientific libraries such as:

- NumPy’s view vs. copy semantics  
- MATLAB’s copy‑on‑write arrays  
- xarray’s indexing and slicing model  

---