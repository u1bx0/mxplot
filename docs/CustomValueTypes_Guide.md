# Custom Value Types Guide

**MxPlot.Core Advanced Topics**

> Last Updated: 2026-02-08  
> Version: 0.0.2-alpha  
> Status: **Preliminary** (Design under active development)

## 📚 Table of Contents

1. [Introduction](#introduction)
2. [The Unmanaged Constraint](#the-unmanaged-constraint)
3. [Complex Number Support](#complex-number-support)
4. [Custom Struct Registration](#custom-struct-registration)
5. [Value Mode Management](#value-mode-management)
6. [Potential Applications](#potential-applications)
7. [Implementation Notes](#implementation-notes)

---

## Introduction

`MatrixData<T>` is designed to handle **any unmanaged type**, extending beyond primitive numerics to custom structs including `Complex`. This enables advanced scientific computing scenarios such as vector fields, spectral data, and multi-component measurements.

**⚠️ Important**: While the core infrastructure supports custom structs, the design and best practices are still evolving. This guide presents the current capabilities and potential future directions.

### Key Design Philosophy

MxPlot.Core provides the **flexibility** to work with arbitrary unmanaged types while maintaining:
- Zero-copy performance
- Type safety
- Extensibility for domain-specific needs

---

## The Unmanaged Constraint

### What is Unmanaged?

The `unmanaged` constraint ensures types can be stored in contiguous memory and manipulated via pointers:

```csharp
// ✅ Valid: All fields are unmanaged
public struct Vector3D
{
    public double X;
    public double Y;
    public double Z;
}

// ❌ Invalid: Contains managed reference
public struct InvalidStruct
{
    public double Value;
    public string Label;  // ← Managed type not allowed
}
```

---

## Complex Number Support

MxPlot.Core provides built-in support for `System.Numerics.Complex` as a reference implementation.

### Built-in Min/Max Finder

`Complex` has a pre-registered finder that calculates min/max for 5 different modes:

```csharp
public enum ComplexValueMode
{
    Magnitude = 0,  // |z| (default)
    Real = 1,       // Re(z)
    Imaginary = 2,  // Im(z)
    Phase = 3,      // arg(z)
    Power = 4       // |z|²
}
```

### Usage Example

```csharp
var data = new MatrixData<Complex>(256, 256);
data.Set((ix, iy, x, y) => new Complex(Math.Cos(x), Math.Sin(y)));

// Access different modes
var (magMin, magMax) = data.GetMinMaxValues(0, ComplexValueMode.Magnitude);
var (phaseMin, phaseMax) = data.GetMinMaxValues(0, ComplexValueMode.Phase);
var (powerMin, powerMax) = data.GetMinMaxValues(0, ComplexValueMode.Power);

// Or using integer mode index
var (min, max) = data.GetMinMaxValues(frameIndex: 0, valueMode: 0);  // Magnitude
```

**Implementation Detail**: The built-in `MinMaxFinder` (delegate) for `Complex` returns a `double[5]` array containing min/max for all modes simultaneously, enabling efficient single-pass computation.

**Key Insight**: Custom structs can follow the same pattern—return multiple modes in a single array for efficiency.

---

## Custom Struct Registration

### Critical Requirement: Min/Max Finder

To support statistics and visualization, custom structs need a **registered finder** for min/max operations.

#### Actual API

```csharp
// Register once per type (typically in application startup)
MatrixData<Vector3D>.RegisterDefaultMinMaxFinder(array =>
{
    // Calculate min/max for the entire array
    double minMag = double.PositiveInfinity;
    double maxMag = double.NegativeInfinity;
    
    foreach (var vec in array)
    {
        double mag = Math.Sqrt(vec.X * vec.X + vec.Y * vec.Y + vec.Z * vec.Z);
        if (mag < minMag) minMag = mag;
        if (mag > maxMag) maxMag = mag;
    }
    
    // Return arrays: single mode (magnitude)
    return (new[] { minMag }, new[] { maxMag });
});

// Now all new MatrixData<Vector3D> instances can calculate min/max
var data = new MatrixData<Vector3D>(512, 512, 100);
var (min, max) = data.GetMinMaxValues();  // Uses registered finder
```

#### Multi-Mode Example

For types with multiple meaningful representations (like `Complex`):

```csharp
public struct ElectricField
{
    public Complex Ex, Ey, Ez;
}

MatrixData<ElectricField>.RegisterDefaultMinMaxFinder(array =>
{
    // Mode 0: Total magnitude
    // Mode 1: Ex magnitude
    // Mode 2: Ey magnitude
    // Mode 3: Ez magnitude
    
    double minTotal = double.PositiveInfinity;
    double maxTotal = double.NegativeInfinity;
    double minEx = double.PositiveInfinity;
    double maxEx = double.NegativeInfinity;
    double minEy = double.PositiveInfinity;
    double maxEy = double.NegativeInfinity;
    double minEz = double.PositiveInfinity;
    double maxEz = double.NegativeInfinity;
    
    foreach (var field in array)
    {
        // Total magnitude
        double total = Math.Sqrt(
            field.Ex.Magnitude * field.Ex.Magnitude +
            field.Ey.Magnitude * field.Ey.Magnitude +
            field.Ez.Magnitude * field.Ez.Magnitude
        );
        if (total < minTotal) minTotal = total;
        if (total > maxTotal) maxTotal = total;
        
        // Ex component
        double ex = field.Ex.Magnitude;
        if (ex < minEx) minEx = ex;
        if (ex > maxEx) maxEx = ex;
        
        // Ey component
        double ey = field.Ey.Magnitude;
        if (ey < minEy) minEy = ey;
        if (ey > maxEy) maxEy = ey;
        
        // Ez component
        double ez = field.Ez.Magnitude;
        if (ez < minEz) minEz = ez;
        if (ez > maxEz) maxEz = ez;
    }
    
    return (
        new[] { minTotal, minEx, minEy, minEz },
        new[] { maxTotal, maxEx, maxEy, maxEz }
    );
});

// Usage with mode selection
var data = new MatrixData<ElectricField>(256, 256, 50);
var (min0, max0) = data.GetMinMaxValues(frameIndex: 0, valueMode: 0);  // Total
var (min1, max1) = data.GetMinMaxValues(frameIndex: 0, valueMode: 1);  // Ex
```

### Key Characteristics

**Type-Global Registration**:
- Register **once per type** at application startup
- All instances created **after registration** will use the finder
- `MatrixData<int>` and `MatrixData<float>` have separate registrations

**Timing Matters**:
```csharp
// ❌ Wrong: Create before registration
var data1 = new MatrixData<Vector3D>(512, 512);
MatrixData<Vector3D>.RegisterDefaultMinMaxFinder(...);
// data1 will throw exception on RefreshValueRange()

// ✅ Correct: Register first
MatrixData<Vector3D>.RegisterDefaultMinMaxFinder(...);
var data2 = new MatrixData<Vector3D>(512, 512);
// data2 works fine
```

**Recommended Pattern**:
```csharp
// In your application startup (Program.cs, etc.)
public static class CustomTypeInitializer
{
    public static void RegisterCustomFinders()
    {
        MatrixData<Vector3D>.RegisterDefaultMinMaxFinder(...);
        MatrixData<ElectricField>.RegisterDefaultMinMaxFinder(...);
        MatrixData<SpectrumPixel>.RegisterDefaultMinMaxFinder(...);
    }
}

// Call once at startup
CustomTypeInitializer.RegisterCustomFinders();
```

---

## Value Mode Management

### The Value Mode Problem

For complex data types, a single "value" can have multiple meaningful interpretations. The min/max finder must calculate statistics for **all modes** you want to support.

### Current Approach: Integer Index

The current design uses integer indices to select modes:

```csharp
// Mode 0, 1, 2, ... correspond to different interpretations
var (min, max) = data.GetMinMaxValues(frameIndex: 0, valueMode: 0);
```

**Complex Example**:
```csharp
public enum ComplexValueMode
{
    Magnitude = 0,  // Default
    Real = 1,
    Imaginary = 2,
    Phase = 3,
    Power = 4
}

// Extension method for type-safe access
var (min, max) = complexData.GetMinMaxValues(0, ComplexValueMode.Phase);
```

### Extending to Custom Types

**Option 1: Define Custom Enum**

```csharp
public enum ElectricFieldMode
{
    TotalMagnitude = 0,
    ComponentEx = 1,
    ComponentEy = 2,
    ComponentEz = 3
}

// Type-safe extension method
public static (double Min, double Max) GetMinMaxValues(
    this MatrixData<ElectricField> data,
    int frameIndex,
    ElectricFieldMode mode)
{
    return data.GetMinMaxValues(frameIndex, (int)mode);
}

// Usage
var (min, max) = data.GetMinMaxValues(0, ElectricFieldMode.ComponentEx);
```

**Option 2: Constants Class**

```csharp
public static class ElectricFieldModes
{
    public const int TotalMagnitude = 0;
    public const int ComponentEx = 1;
    public const int ComponentEy = 2;
    public const int ComponentEz = 3;
}

// Usage
var (min, max) = data.GetMinMaxValues(0, ElectricFieldModes.ComponentEx);
```

### Design Tradeoffs

**Current System**:
- ✅ Simple and efficient (integer indexing)
- ✅ No additional API surface
- ❌ Mode semantics not self-documenting
- ❌ Requires user documentation

**Future Possibilities**:
- Generic metadata system for mode descriptions
- Runtime mode enumeration/discovery
- Integration with visualization layer for auto-labeling

**Recommendation**: Define enums or constants for your custom types to maintain type safety and code clarity.

---

## Potential Applications

### 1. Vector Fields

```csharp
public struct VectorField3D
{
    public double Vx;
    public double Vy;
    public double Vz;
    
    public double Magnitude => Math.Sqrt(Vx*Vx + Vy*Vy + Vz*Vz);
}

var flowField = new MatrixData<VectorField3D>(512, 512, 100);
// Store velocity field from fluid simulation
```

**Visualization Modes**:
- Magnitude map
- Direction (quiver plot)
- Component maps (Vx, Vy, Vz)
- Divergence/curl (requires spatial derivatives)

### 2. Spectral Data

```csharp
public struct SpectrumPixel
{
    public fixed float Intensities[32];  // 32 wavelength channels
    
    // Fixed-size buffers are unmanaged
}

var hyperSpectral = new MatrixData<SpectrumPixel>(1024, 1024);
```

**Visualization Modes**:
- RGB composite (select 3 channels)
- Specific wavelength
- Spectral index (NDVI, etc.)

### 3. Multi-Modal Sensor Data

```csharp
public struct MultiSensorPixel
{
    public ushort Visible;
    public ushort Infrared;
    public ushort Thermal;
    public byte Quality;
}
```

**Visualization Modes**:
- Individual sensor channels
- Sensor ratios
- Composite indices

---

## Implementation Notes

### Current Status

- ✅ Core infrastructure supports any `unmanaged` type
- ✅ `Complex` fully supported with 5 value modes
- ✅ Registration API is stable: `RegisterDefaultMinMaxFinder()`
- ⚠️ Mode enumeration/discovery not yet standardized
- ⚠️ Documentation conventions for custom modes need guidance

### API Surface

```csharp
// Static registration (once per type)
MatrixData<T>.RegisterDefaultMinMaxFinder(MinMaxFinder finder);

// Delegate signature
public delegate (double[] minValues, double[] maxValues) MinMaxFinder(T[] array);

// Instance-level access
var (min, max) = data.GetMinMaxValues(frameIndex, valueMode);
var (minArr, maxArr) = data.GetMinMaxArrays(frameIndex);

// Per-instance override (if needed)
data.SetMinMaxFinder(customFinder);
```

### Design Considerations

1. **Type-Global vs. Instance-Specific**
   - **Current**: Type-global registration with optional instance override
   - **Rationale**: Most applications have consistent interpretation per type
   - **Flexibility**: `SetMinMaxFinder()` allows special cases

2. **Mode Enumeration**
   - **Current**: Integer indices (0, 1, 2, ...)
   - **Best Practice**: Define enums or constants for clarity
   - **Future**: Potential metadata system for mode descriptions

3. **Performance**
   - Min/max finder runs once per frame (cached)
   - Multi-mode calculation in single pass is efficient
   - Parallel processing possible (not yet implemented)

4. **Serialization**
   - Custom structs serialize as raw binary (works automatically)
   - Min/max arrays stored separately in `.mxd` format
   - Mode metadata must be documented externally (no standard yet)

### Best Practices Summary

**For Library Authors**:
1. Register finders at module initialization
2. Document mode indices clearly
3. Provide extension methods for type-safe access
4. Consider multi-mode single-pass calculation

**For Application Developers**:
1. Call registration in `Program.cs` or equivalent
2. Define enums for your custom modes
3. Test min/max calculation before large-scale use
4. Handle missing registration gracefully (catch exceptions)

### Example: Complete Integration

**Note**: This example demonstrates all features for educational purposes. In practice, most applications only need 1-2 modes (e.g., Magnitude only), not all possible interpretations.

```csharp
// 1. Define struct
public struct Vector3D
{
    public double X, Y, Z;
    public double Magnitude => Math.Sqrt(X*X + Y*Y + Z*Z);
}

// 2. Define modes (for documentation, even if not all are implemented)
public enum Vector3DMode
{
    Magnitude = 0,
    ComponentX = 1,
    ComponentY = 2,
    ComponentZ = 3
}

// 3. Register finder (startup)
public static class Vector3DSupport
{
    public static void Register()
    {
        // ⚠️ EDUCATIONAL EXAMPLE: This calculates ALL modes for demonstration
        // In production, consider registering only the modes you actually need
        MatrixData<Vector3D>.RegisterDefaultMinMaxFinder(array =>
        {
            double minMag = double.PositiveInfinity;
            double maxMag = double.NegativeInfinity;
            double minX = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity;
            double maxY = double.NegativeInfinity;
            double minZ = double.PositiveInfinity;
            double maxZ = double.NegativeInfinity;
            
            foreach (var v in array)
            {
                double mag = v.Magnitude;
                if (mag < minMag) minMag = mag;
                if (mag > maxMag) maxMag = mag;
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
                if (v.Z < minZ) minZ = v.Z;
                if (v.Z > maxZ) maxZ = v.Z;
            }
            
            return (
                new[] { minMag, minX, minY, minZ },
                new[] { maxMag, maxX, maxY, maxZ }
            );
        });
    }
    
    // 🎯 REALISTIC ALTERNATIVE: Register only what you need
    public static void RegisterMagnitudeOnly()
    {
        MatrixData<Vector3D>.RegisterDefaultMinMaxFinder(array =>
        {
            double minMag = double.PositiveInfinity;
            double maxMag = double.NegativeInfinity;
            
            foreach (var v in array)
            {
                double mag = v.Magnitude;
                if (mag < minMag) minMag = mag;
                if (mag > maxMag) maxMag = mag;
            }
            
            // Single mode: [0] = Magnitude
            return (new[] { minMag }, new[] { maxMag });
        });
    }
}

// 4. Extension methods (optional)
public static class Vector3DExtensions
{
    public static (double Min, double Max) GetMinMaxValues(
        this MatrixData<Vector3D> data,
        int frameIndex,
        Vector3DMode mode)
    {
        return data.GetMinMaxValues(frameIndex, (int)mode);
    }
}

// 5. Usage
Vector3DSupport.RegisterMagnitudeOnly();  // Recommended for most cases

var data = new MatrixData<Vector3D>(512, 512, 100);
var (min, max) = data.GetMinMaxValues();  // Gets magnitude min/max
```

### Practical Recommendations

**When to use single-mode registration**:
- ✅ Most visualization needs (magnitude/intensity)
- ✅ Simple data types with obvious primary metric
- ✅ Performance-critical applications

**When to use multi-mode registration**:
- Use when you genuinely need multiple interpretations displayed simultaneously
- Consider calculating additional modes on-demand instead
- Example: Complex numbers inherently need multiple modes (magnitude, phase, etc.)

**Design philosophy**: **Register only what you need**. You can always provide additional analysis methods separately without burdening the min/max finder.

---

## Summary

MxPlot.Core provides **production-ready support** for custom unmanaged types through a well-defined registration API. While `Complex` serves as the reference implementation, the framework readily extends to:

- Multi-component vector fields
- Spectral/hyperspectral data
- Multi-sensor fusion
- Custom scientific measurements

### Integration Checklist

To add support for your custom type:

1. ✅ **Define struct** (ensure all fields are `unmanaged`)
2. ✅ **Register min/max finder** (`RegisterDefaultMinMaxFinder()`)
3. ✅ **Define mode enum** (for type-safe access)
4. ✅ **Document modes** (what each index means)
5. ✅ **Test** (create MatrixData instance and verify min/max)

### Key Points

- **Type-global registration**: One-time setup per type
- **Multi-mode support**: Return `double[]` for multiple interpretations
- **Performance**: Single-pass calculation, cached results
- **Flexibility**: Override per-instance if needed

### Current Limitations

- Mode metadata not stored in `.mxd` files (document externally)
- No automatic mode discovery API (use conventions)
- Parallel min/max finding not yet implemented (future optimization)

This design balances **simplicity** (minimal API surface), **performance** (efficient caching), and **flexibility** (extensible to arbitrary types), making MxPlot.Core a robust foundation for diverse scientific computing applications.

---

## Related Topics

- **[MatrixData Operations Guide](./MatrixData_Operations_Guide.md)** - Core operations applicable to all types
- **[VolumeAccessor Guide](./VolumeAccessor_Guide.md)** - 3D operations (works with custom types)
- [Main API Reference](../README.md) - Type constraints and requirements

---

**Status**: Stable API, evolving best practices  
**Last Updated**: 2026-02-08  
**Feedback**: Implementation suggestions welcome via GitHub issues
