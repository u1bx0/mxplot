# Summary of ShiftOption in FFT2D (Revised for Swap Implementation)

## FFT/IFFT Fundamentals

In `Fft2D` and `InverseFft2D`, the index `[0]` of the input array `Complex[xnum * ynum]` is treated as the coordinate origin by default. Consequently:

* **Fft2D (Forward):** Maps the Spatial Origin at `input[0]` to the **DC (Zero Frequency) at `output[0]`**.
* **InverseFft2D (Inverse):** Maps the DC at `input[0]` to the **Spatial Origin at `output[0]`**.

To handle "Centered" data (where the origin/DC is at `[N/2, N/2]`), we utilize **Quadrant Swapping (fftshift)**. This ensures robust phase handling even for odd-sized arrays, unlike the Checkerboard method.

## Origin Relocation via Swap Operations

The extension methods `MatrixData<T>.Fft2D` and `.InverseFft2D` use `ShiftOption` to determine when to perform memory swaps (`fftshift`).

```CSharp
public enum ShiftOption
{
  None, // No Swap. Raw FFT.
  Centered, // Visually centered output (Magnitude only).
  BothCentered // Physically centered input & output (Phase correct).
}
```

### **Fft2D (Forward: Space → Frequency)**

| Option | Pipeline | Input Origin | Output DC Position | Phase Status |
| :--- | :--- | :--- | :--- | :--- |
| **None** | `FFT` | `[0]` | `[0]` | **Tilted** (if object is at center) |
| **Centered** | `FFT` → `Swap` | `[0]` | **`[Center]`** | **Tilted** (Input was not centered) |
| **BothCentered** | `Swap` → `FFT` → `Swap` | **`[Center]`** | **`[Center]`** | **Flat / Correct** |

* **Centered:** Useful for visualizing the Power Spectrum. The phase contains a linear tilt because the input object was at `[N/2, N/2]` (shifted from calculation origin).
* **BothCentered:** Performs a pre-FFT swap to move the object to `[0]`, ensuring the phase is calculated without tilt. Then, performs a post-FFT swap to center the DC for display.

### **InverseFft2D (Backward: Frequency → Space)**

| Option | Pipeline | Input DC Position | Output Origin | Phase/Image Status |
| :--- | :--- | :--- | :--- | :--- |
| **None** | `IFFT` | `[0]` | `[0]` | Standard IFFT |
| **Centered** | `Swap` → `IFFT` | **`[Center]`** | `[0]` | Image at Corner `[0]` |
| **BothCentered** | `Swap` → `IFFT` → `Swap` | **`[Center]`** | **`[Center]`** | Image at **`[Center]`** |

* **Centered:** Standard for image restoration. Takes a centered spectrum and restores the image to the standard top-left origin.
* **BothCentered:** Returns the image to the center of the array.

---

## Defining the Spatial Origin

`MatrixData` defines spatial coordinates via `XMin`...`YMax`. In `BothCentered` mode, the coordinate system is physically consistent: the array center corresponds to $(0,0)$ in both Spatial and Frequency domains.

## Sequential Execution: The "4-Swap" Pipeline

To maintain intuitive "Centered" data handling throughout an `FFT -> Action -> IFFT` pipeline, we use `BothCentered` for both steps:

`Swap` → `FFT` → **`Swap`** → `[Action]` → **`Swap`** → `IFFT` → `Swap`

* **Action Logic:**
    * **For `Centered` / `BothCentered`:** The DC component is strictly aligned to the array center `[N/2, N/2]`. Users can intuitively define filters or transfer functions based on the distance from the center.
    * **For `None`:** The DC component resides at index `[0]` (Corner). Any filter or propagator applied here must be designed or pre-shifted to match this corner-based layout.

* **Performance Note:**
    * **`BothCentered`** is designed for **correctness and ease of use** in wave optics, providing physically flat phase data at the center. However, this convenience comes with the overhead of **4 swap operations** per cycle (Input Swap + Output Swap for both FFT and IFFT).
    * **For High-Performance Scenarios:** **`ShiftOption.None` is the definitive choice.** It involves **zero data movement**. In this mode, users should manually pre-shift their Action kernels (propagators) to match the corner-based DC layout once during initialization, achieving the same physical result with maximum speed.

## When to use Centered vs. BothCentered?

* **Centered:**
    * **Usage:** Spectrum visualization, simple filtering where phase is irrelevant.
    * **Behavior:** Magnitude is correct. Phase is tilted.
* **BothCentered:**
    * **Usage:** **Wave optics, Holography, Convolution**, or any operation requiring precise phase manipulation.
    * **Behavior:** Magnitude is correct. Phase is correct (Flat).

## Implementation Detail: Why Swap?

We use **Quadrant Swapping (Memory Copy)** instead of Checkerboard Multiplication because:

1.  **Odd-Size Compatibility:** Checkerboard fails on odd sizes (e.g., 513x513), causing a half-pixel shift and a $\pi$ phase error. Swap handles odd sizes by strictly moving the `[0,0]` element to the integer center `[N/2, N/2]`.
2.  **Consistency:** It guarantees that the "Mathematical Origin" and the "Array Center" are perfectly aligned in `BothCentered` mode.