# Summary of ShiftOption in FFT2D

## FFT/IFFT Fundamentals

In `Fft2D` and `InverseFft2D`, the index `[0]` of the input array `Complex[xnum * ynum]` is treated as the coordinate origin by default. Consequently, the basic behavior is as follows:

* **Fft2D (Forward):** Maps the Spatial Origin at `input[0]` to the **DC (Zero Frequency) at `output[0]`**.
* **InverseFft2D (Inverse):** Maps the DC at `input[0]` to the **Spatial Origin at `output[0]`**.

When implementing these, precise attention must be paid to the correspondence between these mathematical coordinates and the actual data stored in `MatrixData`.

## Origin Relocation via Shift Operations

The extension methods `MatrixData<T>.Fft2D` and `.InverseFft2D` require a `ShiftOption`. This allows the user to choose whether the coordinate origin (or DC) should be located at the **Corner** or the **Center** of the data array.

```CSharp
public ShiftOption
{
  None,
  Centered,
  BothCentered
}
```

For `Centered` and `BothCentered`, a shift process is applied. Mathematically, this involves multiplying the data by a checkerboard phase pattern ($(-1)^{x+y}$), which corresponds to applying a linear phase gradient in the reciprocal domain to shift the DC component to the center.

### **Fft2D (Forward: Space → Frequency)**

| Option | Input (Space) Origin | Output (Freq.) DC Position | Phase Tilt |
| :--- | :--- | :--- | :--- |
| **None** | Input[0] (Corner) | Output[0] (Corner) | None |
| **Centered** | Input[0] (Corner) | **Output[Center]** | **Present** |
| **BothCentered** | **Input[Center]** | **Output[Center]** | None<br>(Canceled) |

### **InverseFft2D (Backward: Frequency → Space)**

| Option | Input (Freq.) DC Position | Output (Space) Origin | Phase Tilt |
| :--- | :--- | :--- | :--- |
| **None** | Input[0] (Corner) | Output[0] (Corner) | None |
| **Centered** | **Input[Center]** | **Output[0] (Corner)** | **Present** |
| **BothCentered** | **Input[Center]** | **Output[Center]** | None<br>(Canceled) |

* **Centered:** Performs a pre-transform phase multiplication.
* **BothCentered:** Performs both pre-transform and post-transform phase multiplications to ensure the origin remains at the center in both domains without residual tilt.



---

## Defining the Spatial Origin

`MatrixData` defines spatial coordinates via `XMin`, `XMax`, `YMin`, and `YMax`. Since `FFT2D`/`IFFT2D` effectively treats the center of the array as the origin, the physical space is considered to span from $\pm \text{XRange}/2$ to $\pm \text{YRange}/2$, where $\text{XRange} = \text{XMax} - \text{XMin}$.

## Sequential Execution: `FFT2D` → `Action` → `IFFT2D`

When calling `Fft2D` and `InverseFft2D` sequentially using the **same option**, any superimposed phase tilt is mathematically cancelled out.

For methods like the **Angular Spectrum Method (ASM)**—where a phase distribution is multiplied in the frequency domain—the final complex amplitude will be identical regardless of the chosen `ShiftOption`, provided the kernel (the phase distribution) is prepared to match the data alignment. If the user prepares a kernel where the DC is at `index[0]`, the shift operations during FFT/IFFT become unnecessary (`ShiftOption.None`).

---

## When to use Centered vs. BothCentered?

* **Centered:** Ideal for filtering or processing where it is more intuitive to have the DC component located at the center of the array.
* **BothCentered:** While slightly redundant due to the additional post-processing phase multiplication, this is critical for calculating **Far-field (Fraunhofer) diffraction patterns**. For instance, performing a `BothCentered` FFT on a circular aperture defined at the array center will yield a mathematically rigorous Airy pattern with the correct phase center.

## Implementation of the Checkerboard Phase

For `Centered` and `BothCentered`, the phase pattern is defined such that the center of the data `[Nx/2, Ny/2]` results in a multiplier of $+1$.

## The Role of Quadrant Swapping (SwapQuadrants)

For the purpose of shifting the origin via phase multiplication, a physical quadrant swap is not required. However, when using `ShiftOption.None` for an `FFT -> Action -> IFFT` pipeline, the `Action` kernel must have its DC at `index[0]`. In this case, it is often easiest to generate the kernel with the DC at the center and then apply a **Quadrant Swap: `SwapQuadrants`** (equivalent to `fftshift`) once before entering the computation loop.