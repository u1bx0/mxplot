# Data Access Strategies for `MatrixData<T>`

`MatrixData<T>` provides multiple ways to iterate over and manipulate multi-dimensional data. Choosing the right approach depends on your specific use case: Performance, Memory Access Patterns, or Initialization.

Here is a 5D example dataset (`X, Y, Channel, Z, Time`) used in the examples below:

```csharp
var xyczt = new MatrixData<float>(
    Scale2D.Centered(32, 32, 2, 2),  // -1 to 1 for x/y axes
    Axis.Channel(2),             // C=2
    Axis.Z(3, -1, 2, "µm"),      // Z=3, -1 to 1 µm
    Axis.Time(4, 0, 2, "s"),     // T=4, 0 to 2 s
);// Total: 2x3x4 = 24 frames

```

---

## 1. Frame-Based Processing (The `ForEach` Method)

**Best for:** High-performance per-pixel processing, parallel execution, and applying 2D image filters.

This is the most efficient way to process data when you want to treat each frame as an independent 2D image. It provides direct access to the underlying 1D array of each frame, bypassing indexer overhead.

```csharp
// useParallel: true (default) enables concurrent processing of multiple frames.
xyczt.ForEach((i, array) =>
{ 
    // i = frameIndex (index of the internal List<T[]>)
    var (ic, iz, it) = xyczt.Dimensions.GetAxisIndicesStruct(i);
    var (c, z, t) = xyczt.Dimensions.GetAxisValuesStruct(i);

    for (int iy = 0; iy < xyczt.YCount; iy++)
    {
        double y = xyczt.YValue(iy);
        int offset = iy * xyczt.XCount; // Pre-calculate row offset

        for (int ix = 0; ix < xyczt.XCount; ix++)
        {
            double x = xyczt.XValue(ix);
            var v = array[offset + ix]; // Extremely fast 1D array access
            
            // Do something with v, x, y, c, z, t
            array[offset + ix] = v;     // Write back if needed
        }
    }
}, useParallel: true); 

```

* **Pros:** Absolute maximum performance. Trivial to parallelize across frames.
* **Cons:** Hard to access pixels in adjacent frames (e.g., temporal smoothing across `T` or 3D convolution across `Z`).

---

## 2. Spatial-Temporal Random Access (The Multi-dimensional Indexer)

**Best for:** Cross-frame operations (e.g., 3D/4D convolutions, temporal tracking), or algorithms that naturally require nested loops.

This approach uses the explicit `this[ix, iy, ic, iz, it]` indexer. It is highly flexible but requires strict adherence to loop ordering for cache efficiency.

```csharp
int tnum = xyczt["Time"]!.Count;
int znum = xyczt["Z"]!.Count;
int cnum = xyczt["Channel"]!.Count;

// ⚠️ CRITICAL: Nest loops from right-to-left of the [ix, iy, c, z, t] signature
for (int it = 0; it < tnum; it++) // Outermost: The last defined axis
{
    for (int iz = 0; iz < znum; iz++)
    {
        for (int ic = 0; ic < cnum; ic++)
        {
            for (int iy = 0; iy < xyczt.YCount; iy++)
            {
                for (int ix = 0; ix < xyczt.XCount; ix++) // Innermost: X (contiguous memory)
                {
                    var v = xyczt[ix, iy, ic, iz, it];
                    
                    // Note: If physical values (c, z, t) are necessary, 
                    // get them via xyczt["Channel"].ValueAt(ic), etc.
                    
                    xyczt[ix, iy, ic, iz, it] = v; // Write back
                }
            }
        }
    }
}

```

* **Pros:** Intuitive for spatial algorithms. Easy to read neighbor pixels like `[ix+1, iy, ic, iz-1, it]`.
* **Cons:** Requires strict loop ordering. If you invert the loops, performance will degrade due to CPU cache misses.

---

## 3. Functional Generation (The `Set` Method with LINQ)

**Best for:** Initializing data based on mathematical formulas, physical coordinates, or procedural generation.

By combining PLINQ (`AsParallel().ForAll`) and the `Set` method, you can write highly readable, declarative code to generate pixel values based on their physical coordinates.

```csharp
// Iterate over all frames in parallel
Enumerable.Range(0, xyczt.FrameCount).AsParallel().ForAll(i =>
{
    var (c, z, t) = xyczt.Dimensions.GetAxisValuesStruct(i);
    
    // Set sequentially populates the current frame
    xyczt.Set(i, (ix, iy, x, y) => CalculatePixelValue(x, y, c, z, t));
});

// A separate method (or an inline lambda) to define the math
float CalculatePixelValue(double x, double y, double c, double z, double t)
{
    // Example: Generating a value using physical coordinates
    return (float)(x * y + c * z * t);
}

```

* **Pros:** Very clean, declarative syntax. Excellent for generators and test-data creation.
* **Cons:** The overhead of the delegate/lambda call per pixel makes it slightly slower than the raw `ForEach` array access for simple operations.

