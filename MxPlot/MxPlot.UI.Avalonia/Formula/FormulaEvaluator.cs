using MxPlot.Core;
using MxPlot.Core.Processing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Formula
{
    /// <summary>
    /// Where a formula's axis variables come from when they are not simply the axes of the result: one
    /// entry per axis variable pair, in the order the formula was compiled with. An axis the result has
    /// (matched by name) takes the position of the frame being computed; any other axis keeps the fixed
    /// position given here.
    /// </summary>
    /// <param name="Axes">The name of each axis and its fixed index and coordinate.</param>
    internal sealed record FormulaPosition(IReadOnlyList<(string Name, int Index, double Value)> Axes)
    {
        /// <summary>The position of frame <paramref name="frameIndex"/> of <paramref name="source"/> along each of its axes.</summary>
        public static FormulaPosition Of(IMatrixData source, int frameIndex)
        {
            var axes = source.Axes;
            if (axes.Count == 0) return new FormulaPosition([]);

            if (frameIndex < 0 || frameIndex >= source.FrameCount) frameIndex = source.ActiveIndex;
            var indices = source.Dimensions.GetAxisIndices(frameIndex);
            var entries = new (string, int, double)[axes.Count];
            for (int i = 0; i < entries.Length; i++)
                entries[i] = (axes[i].Name, indices[i], axes[i].ValueAt(indices[i]));
            return new FormulaPosition(entries);
        }
    }

    /// <summary>A matrix a formula reads, and how.</summary>
    /// <param name="Data">The data.</param>
    /// <param name="ValueMode">The value mode structured data (Complex, ...) is read in.</param>
    /// <param name="FrameFor">
    /// The frame of <paramref name="Data"/> the reference reads, given the frame being computed, when a formula names
    /// none; -1 for none. <see langword="null"/> for the usual rule: the same frame when the data has as many
    /// frames as the result, otherwise its active frame.
    /// </param>
    internal sealed record FormulaReference(IMatrixData Data, int ValueMode = 0, Func<int, int>? FrameFor = null);

    /// <summary>
    /// The data a formula reads when the data it is applied to is only a part of it: one frame of a stack, or a
    /// Composite channel cube.
    /// </summary>
    /// <param name="Data">The whole data.</param>
    /// <param name="ValueMode">The value mode structured data is read in.</param>
    /// <param name="Frame">The frame being processed, from which the other axes keep their positions.</param>
    /// <param name="CompositeAxisName">The composited axis when the part is a channel cube; the frames of the result then run along it.</param>
    internal sealed record FormulaSource(IMatrixData Data, int ValueMode, int Frame, string? CompositeAxisName)
    {
        /// <summary>The reference a formula reads this data through.</summary>
        public FormulaReference ToReference() => new(Data, ValueMode,
            CompositeAxisName == null ? _ => Frame : f => FormulaEvaluator.FrameWithAxis(Data, Frame, CompositeAxisName, f));
    }

    /// <summary>Fills a new matrix by evaluating a formula for every pixel of every frame.</summary>
    /// <remarks>
    /// <para>
    /// The variables are, in order: <c>ix</c>, <c>iy</c> (pixel indices), <c>x</c>, <c>y</c> (physical
    /// coordinates of the output grid), <c>r</c> (the distance of (x, y) from the origin of those coordinates),
    /// then an index and a coordinate for each axis (see
    /// <see cref="VariableNames"/>). The axes are those of the result, or those of a
    /// <see cref="FormulaPosition"/> when one is given. A formula compiled with only the first few
    /// variables is valid too.
    /// </para>
    /// <para>
    /// A reference read without a frame reads the frame being computed when the reference has as many
    /// frames as the output, and its active frame otherwise.
    /// </para>
    /// </remarks>
    internal static class FormulaEvaluator
    {
        /// <summary>The variables every formula has: the pixel indices and the physical coordinates.</summary>
        public static IReadOnlyList<string> PixelVariables { get; } = ["ix", "iy", "x", "y", "r"];

        /// <summary>
        /// The variable names for <paramref name="axes"/>: <see cref="PixelVariables"/>, then for each axis
        /// its index and its coordinate - <c>iz</c>, <c>z</c> for Z, <c>ic</c>, <c>c</c> for Channel,
        /// <c>it</c>, <c>t</c> for Time, and <c>ia1</c>, <c>a1</c>, <c>ia2</c>, <c>a2</c>, ... for any
        /// other axis in order.
        /// </summary>
        public static string[] VariableNames(IEnumerable<Axis> axes)
        {
            var names = new List<string>(PixelVariables);
            int other = 0;
            foreach (var axis in axes)
            {
                var (index, value) = AxisVariable(axis.Name, ref other);
                names.Add(index);
                names.Add(value);
            }
            return names.ToArray();
        }

        /// <summary>
        /// The index variable of each of <paramref name="axes"/> - <c>iz</c>, <c>ic</c>, <c>it</c>, <c>ia1</c>, ... - in the
        /// order of the axes. A formula names an axis of a reference by it: <c>m1[ix, iy, iz:2]</c>.
        /// </summary>
        public static string[] AxisIndexLabels(IEnumerable<Axis> axes) => AxisIndexLabels(axes.Select(a => a.Name));

        /// <summary>The index variable of each axis called one of <paramref name="axisNames"/>, in their order.</summary>
        public static string[] AxisIndexLabels(IEnumerable<string> axisNames)
        {
            var labels = new List<string>();
            int other = 0;
            foreach (var name in axisNames) labels.Add(AxisVariable(name, ref other).Index);
            return labels.ToArray();
        }

        /// <summary>The index and coordinate variable of an axis called <paramref name="axisName"/>.</summary>
        /// <param name="otherOrdinal">The number of axes other than Z, Channel and Time seen so far; incremented for such an axis.</param>
        public static (string Index, string Value) AxisVariable(string axisName, ref int otherOrdinal)
        {
            switch (axisName)
            {
                case "Z": return ("iz", "z");
                case "Channel": return ("ic", "c");
                case "Time": return ("it", "t");
                default:
                    otherOrdinal++;
                    return ($"ia{otherOrdinal}", $"a{otherOrdinal}");
            }
        }

        /// <summary>
        /// The number of the frame of <paramref name="data"/> that is <paramref name="frame"/> moved to position
        /// <paramref name="position"/> along the axis called <paramref name="axisName"/>; -1 when there is no such axis or
        /// position.
        /// </summary>
        public static int FrameWithAxis(IMatrixData data, int frame, string axisName, int position)
        {
            var axes = data.Axes;
            for (int slot = 0; slot < axes.Count; slot++)
            {
                if (axes[slot].Name != axisName) continue;
                if ((uint)position >= (uint)axes[slot].Count || (uint)frame >= (uint)data.FrameCount) return -1;

                var indices = data.Dimensions.GetAxisIndices(frame);
                indices[slot] = position;
                return data.Dimensions.GetFrameIndexAt(indices);
            }
            return -1;
        }

        /// <summary>Evaluates <paramref name="formula"/> over a grid.</summary>
        /// <param name="scale">The X/Y grid of the result.</param>
        /// <param name="axes">The axes of the result beyond X and Y; they are cloned.</param>
        /// <param name="references">The data behind the formula's references, in their order, read in its first value mode.</param>
        /// <param name="progress">Receives the negated frame count first, then the index of each finished frame.</param>
        /// <param name="position">
        /// What the axis variables stand for, when the formula was compiled for other axes than those of
        /// the result; <see langword="null"/> when they are the axes of the result.
        /// </param>
        public static MatrixData<double> Evaluate(CompiledFormula formula, Scale2D scale, IReadOnlyList<Axis> axes,
            IReadOnlyList<IMatrixData> references, IProgress<int>? progress = null, CancellationToken ct = default,
            FormulaPosition? position = null)
            => EvaluateReferences(formula, scale, axes, references.Select(r => new FormulaReference(r)).ToArray(), progress, ct, position);

        /// <summary>
        /// Evaluates <paramref name="formula"/> over a grid, with each reference read as its <see cref="FormulaReference"/>
        /// says: in a value mode, and at a frame of its own choosing. See <c>Evaluate</c> for the other parameters.
        /// </summary>
        public static MatrixData<double> EvaluateReferences(CompiledFormula formula, Scale2D scale, IReadOnlyList<Axis> axes,
            IReadOnlyList<FormulaReference> references, IProgress<int>? progress = null, CancellationToken ct = default,
            FormulaPosition? position = null)
        {
            int frameCount = 1;
            foreach (var axis in axes) frameCount = checked(frameCount * axis.Count);

            var result = new MatrixData<double>(scale.XCount, scale.YCount, frameCount);
            result.SetXYScale(scale.XMin, scale.XMax, scale.YMin, scale.YMax);
            result.XUnit = scale.XUnit;
            result.YUnit = scale.YUnit;
            if (axes.Count > 0) result.DefineDimensions(axes.Select(a => a.Clone()).ToArray());

            FillReferences(result, formula, references, progress, ct, position);
            return result;
        }

        /// <summary>
        /// Evaluates <paramref name="formula"/> into the frames of <paramref name="result"/>, which keeps its grid and axes
        /// and its own frame buffers: a window showing it needs only to be redrawn. The values of the result are not
        /// marked as changed; the caller does that (<see cref="IMatrixData.Invalidate()"/>) before anyone reads its range.
        /// </summary>
        public static void FillReferences(MatrixData<double> result, CompiledFormula formula,
            IReadOnlyList<FormulaReference> references, IProgress<int>? progress = null, CancellationToken ct = default,
            FormulaPosition? position = null)
        {
            if (references.Count != formula.References.Count)
                throw new ArgumentException("The number of references does not match the formula.", nameof(references));

            var scale = result.GetScale();
            var axes = result.Axes;
            int xCount = scale.XCount;
            int yCount = scale.YCount;
            int frameCount = result.FrameCount;

            int axisVariableCount = position?.Axes.Count ?? axes.Count;
            int variableCount = Math.Max(formula.Variables.Count, PixelVariables.Count + 2 * axisVariableCount);
            // For each axis variable pair, the axis of the result it follows, or -1 for a fixed position.
            int[]? followed = position?.Axes.Select(p => IndexOfAxis(axes, p.Name)).ToArray();

            // The active frame of each reference is read once, so a reference being navigated while the
            // formula runs does not change frames part-way through.
            var activeFrames = references.Select(r => r.Data.ActiveIndex).ToArray();
            // One per reference for the whole evaluation: Max() and Min() are found once, not once per thread.
            var stats = references.Select(r => new FormulaStats(r.Data, r.ValueMode)).ToArray();
            var baseFrames = new int[references.Count];

            var axisIndices = new int[axes.Count];
            var axisVariables = new double[2 * axisVariableCount];
            var options = new ParallelOptions { CancellationToken = ct };
            progress?.Report(-frameCount);

            for (int frame = 0; frame < frameCount; frame++)
            {
                ct.ThrowIfCancellationRequested();
                if (axes.Count > 0) result.Dimensions.CopyAxisIndicesTo(axisIndices, frame);

                for (int k = 0; k < axisVariableCount; k++)
                {
                    int axis = followed == null ? k : followed[k];
                    if (axis >= 0)
                    {
                        axisVariables[2 * k] = axisIndices[axis];
                        axisVariables[2 * k + 1] = axes[axis].ValueAt(axisIndices[axis]);
                    }
                    else
                    {
                        axisVariables[2 * k] = position!.Axes[k].Index;
                        axisVariables[2 * k + 1] = position.Axes[k].Value;
                    }
                }

                for (int r = 0; r < baseFrames.Length; r++)
                {
                    var reference = references[r];
                    baseFrames[r] = reference.FrameFor != null
                        ? reference.FrameFor(frame)
                        : reference.Data.FrameCount == frameCount ? frame : activeFrames[r];
                }

                int f = frame;
                var target = result.GetArray(f);
                Parallel.For(0, yCount, options,
                    () => CreateContext(variableCount, axisVariables, references, stats, baseFrames),
                    (iy, _, context) =>
                    {
                        var v = context.V;
                        double y = scale.YValue(iy);
                        v[1] = iy;
                        v[3] = y;
                        int row = iy * xCount;
                        for (int ix = 0; ix < xCount; ix++)
                        {
                            double x = scale.XValue(ix);
                            v[0] = ix;
                            v[2] = x;
                            v[4] = Math.Sqrt(x * x + y * y);
                            target[row + ix] = formula.Evaluate(context);
                        }
                        return context;
                    },
                    _ => { });
                progress?.Report(f);
            }
        }

        private static int IndexOfAxis(IReadOnlyList<Axis> axes, string name)
        {
            for (int i = 0; i < axes.Count; i++)
                if (axes[i].Name == name) return i;
            return -1;
        }

        private static FormulaContext CreateContext(int variableCount, double[] axisVariables,
            IReadOnlyList<FormulaReference> references, FormulaStats[] stats, int[] baseFrames)
        {
            var v = new double[variableCount];
            Array.Copy(axisVariables, 0, v, PixelVariables.Count, axisVariables.Length);

            var m = new FormulaRef[references.Count];
            for (int r = 0; r < m.Length; r++)
                m[r] = new FormulaRef(references[r].Data, baseFrames[r], stats[r]);
            return new FormulaContext(v, m);
        }
    }

    /// <summary>
    /// Evaluates a formula over the grid and axes of the data it is applied to. The formula reads that data as
    /// its single reference, or <paramref name="Source"/> when the data is only a part of it. The result is double.
    /// </summary>
    /// <param name="Formula">A formula compiled with exactly one reference.</param>
    /// <param name="Position">
    /// What the formula's axis variables stand for, when the data it is applied to is a part of the data
    /// the formula was written for (one frame, or a channel cube); otherwise <see langword="null"/>.
    /// </param>
    /// <param name="Source">
    /// The whole data, when the data the operation is applied to is only a part of it: the formula then reads
    /// the whole data, so it can reach the other frames.
    /// </param>
    internal sealed record FormulaOperation(
        CompiledFormula Formula,
        IProgress<int>? Progress = null,
        CancellationToken CancellationToken = default,
        FormulaPosition? Position = null,
        FormulaSource? Source = null) : IMatrixDataOperation
    {
        public IMatrixData Execute<T>(MatrixData<T> data) where T : unmanaged
            => FormulaEvaluator.EvaluateReferences(Formula, data.GetScale(), data.Axes,
                [Source?.ToReference() ?? new FormulaReference(data)], Progress, CancellationToken, Position);
    }
}
