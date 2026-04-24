using System;
using System.Threading;

namespace MxPlot.Core.Processing
{
    // =========================================================================================
    // Log Transform
    // =========================================================================================

    /// <summary>The logarithm base used by <see cref="LogTransformOperation"/>.</summary>
    public enum LogBase { Natural, Log10, Log2 }

    /// <summary>
    /// Specifies how non-positive pixel values are handled before the logarithm is applied.
    /// </summary>
    public enum NegativeHandling
    {
        /// <summary>
        /// Add <c>|frameMin| + ε</c> to every value before taking the log.
        /// Each frame is shifted independently so the minimum maps to log(ε) ≈ −23.
        /// Preserves relative magnitudes within a frame.
        /// </summary>
        Shift,

        /// <summary>Clamp values below ε to ε before taking the log. Negative values map to log(ε).</summary>
        Clamp,
    }

    /// <summary>
    /// Applies a per-element logarithm transform, always producing a <see cref="MatrixData{T}"/>
    /// of type <c>double</c> regardless of the source element type.
    /// </summary>
    public record LogTransformOperation(
        LogBase Base = LogBase.Natural,
        NegativeHandling Handling = NegativeHandling.Shift,
        int SingleFrameIndex = -1,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.LogTransform(Base, Handling, SingleFrameIndex, Progress, CancellationToken);
    }

    // =========================================================================================
    // Normalize
    // =========================================================================================

    /// <summary>
    /// Specifies whether normalization is performed independently per frame or globally
    /// across all frames.
    /// </summary>
    public enum NormalizeScope
    {
        /// <summary>Each frame is normalized independently using its own maximum value.</summary>
        PerFrame,

        /// <summary>
        /// All frames are normalized using the single maximum value found across the entire dataset.
        /// For virtual (MMF-backed) data this requires a full scan of all frames, which may be slow.
        /// </summary>
        Global,
    }

    /// <summary>
    /// Normalizes pixel values so that the maximum maps to <see cref="Target"/>.
    /// The minimum is preserved proportionally (origin stays at 0).
    /// <para>
    /// When <see cref="SingleFrameIndex"/> is ≥ 0, only that one frame is processed and
    /// the result is a single-frame <see cref="IMatrixData"/>. Otherwise all frames are
    /// processed according to <see cref="Scope"/>.
    /// </para>
    /// </summary>
    public record NormalizeOperation(
        double Target,
        NormalizeScope Scope,
        int SingleFrameIndex = -1,
        double PrecomputedGlobalMax = double.NaN,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> src) where T : unmanaged
            => src.Normalize(Target, Scope, SingleFrameIndex, PrecomputedGlobalMax, Progress, CancellationToken);
    }
}
