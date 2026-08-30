// OrthogonalViewController.Composite.cs
//
// Per-channel extraction + zero-copy merge used to render Composite mode (Channel-axis
// blending) in the XZ/YZ side views and their MIP/MinIP/AIP projections. See
// Tests.Documents/Working/Composite/CompositeMode_Step4-5_ImplementationPlan.md (Step F).
using MxPlot.Core;
using MxPlot.Core.Processing;
using System;
using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Controls
{
    public sealed partial class OrthogonalViewController
    {
        /// <summary>
        /// Extracts one <see cref="IMatrixData"/> per Channel-axis index (via
        /// <paramref name="extractOne"/>, called once per channel with a <c>BaseIndices</c>
        /// array pinning every other axis at its current position and the Channel axis at
        /// that channel's index) and merges the results into a single zero-copy multi-frame
        /// <see cref="IMatrixData"/> suitable for <see cref="Rendering.RenderingMode.Composite"/>.
        /// Internal (not private) so <c>MatrixPlotter.InvokeExtractFrame</c> can reuse the same
        /// per-channel-extract-and-merge logic for Extract on the XZ/YZ orthogonal side views.
        /// </summary>
        internal static IMatrixData BuildChannelComposite(
            IMatrixData source, int channelAxisDimIndex, int channelCount, Func<int[], IMatrixData> extractOne)
        {
            var baseTemplate = source.Dimensions.GetAxisIndices();
            var perChannel = new IMatrixData[channelCount];
            for (int c = 0; c < channelCount; c++)
            {
                var bi = (int[])baseTemplate.Clone();
                bi[channelAxisDimIndex] = c;
                perChannel[c] = extractOne(bi);
            }
            return MergeChannelComposite(source, channelAxisDimIndex, perChannel);
        }

        /// <summary>
        /// Merges per-channel results the caller has already extracted into a single Composite
        /// payload. Split out of <see cref="BuildChannelComposite"/> for callers that hold their own
        /// per-channel sources instead of re-deriving them from the live axis position each time -
        /// the orthogonal Extract windows, which freeze their axis coordinates at extraction.
        /// </summary>
        internal static IMatrixData MergeChannelComposite(
            IMatrixData source, int channelAxisDimIndex, IReadOnlyList<IMatrixData> perChannel)
        {
            int channelCount = perChannel.Count;
            var merged = MergeChannelFrames(source.ValueType, perChannel);

            // The extracted slices/projections already carry the correct physical scale for their
            // plane (see VolumeAccessor.SliceY / CreateProjection). The merged wrapper is a fresh
            // MatrixData, so that scale - and the Channel axis - have to be re-applied, otherwise
            // the side views fall back to pixel indices and, because Dimensions no longer contains
            // "Channel", line profiles silently collapse to a single series.
            var template = perChannel[0];
            merged.SetXYScale(template.XMin, template.XMax, template.YMin, template.YMax);
            merged.XUnit = template.XUnit;
            merged.YUnit = template.YUnit;
            merged.DefineDimensions(CloneChannelAxis(source.Dimensions[channelAxisDimIndex], channelCount));
            return merged;
        }

        /// <summary>
        /// Returns a detached copy of the Channel axis for the merged slice data, preserving tags
        /// (and colours) so profile series stay labelled with the real channel names.
        /// A copy rather than the original: sharing one Axis instance between two MatrixData
        /// objects would wire the merged slice to the main view's channel navigation.
        /// </summary>
        private static Axis CloneChannelAxis(Axis channelAxis, int channelCount)
        {
            if (channelAxis is TaggedAxis tagged && tagged.Tags.Count == channelCount)
                return tagged.Clone();
            return Axis.Channel(channelCount);
        }

        private static IMatrixData MergeChannelFrames(Type valueType, IReadOnlyList<IMatrixData> perChannel)
        {
            return valueType switch
            {
                Type t when t == typeof(byte) => MergeTyped<byte>(perChannel),
                Type t when t == typeof(ushort) => MergeTyped<ushort>(perChannel),
                Type t when t == typeof(short) => MergeTyped<short>(perChannel),
                Type t when t == typeof(int) => MergeTyped<int>(perChannel),
                Type t when t == typeof(float) => MergeTyped<float>(perChannel),
                Type t when t == typeof(double) => MergeTyped<double>(perChannel),
                Type t when t == typeof(System.Numerics.Complex) => MergeTyped<System.Numerics.Complex>(perChannel),
                _ => throw new NotSupportedException(
                    $"Composite orthogonal/projection rendering does not support value type '{valueType}'."),
            };
        }

        private static IMatrixData MergeTyped<T>(IReadOnlyList<IMatrixData> perChannel) where T : unmanaged
        {
            var first = (MatrixData<T>)perChannel[0];
            var arrays = new List<T[]>(perChannel.Count);
            foreach (var p in perChannel)
                arrays.Add(((MatrixData<T>)p).GetArray(0));
            return new MatrixData<T>(first.XCount, first.YCount, arrays);
        }

        /// <summary>
        /// Applies (or clears) Composite rendering state on a side/projection <see cref="MxView"/>.
        /// Takes the same captured (not live-field) state used to build the merged frame set,
        /// so a concurrent <see cref="SetCompositeState"/> call can't apply mismatched state to
        /// a view whose <c>MatrixData</c> was built from the previous state — frame indices are
        /// always <c>0..channelCount-1</c> because <see cref="BuildChannelComposite"/> always
        /// produces a freshly merged 0-based frame set.
        /// </summary>
        private static void ApplyCompositeViewState(
            MxView view, bool active, int channelCount,
            System.Collections.Generic.IReadOnlyList<Rendering.BlendRecipe>? recipes, Rendering.BlendMode blendMode)
        {
            if (active)
            {
                var indices = new int[channelCount];
                for (int i = 0; i < indices.Length; i++) indices[i] = i;
                view.CompositeFrameIndices = indices;
                view.CompositeRecipes = recipes;
                view.CompositeBlendMode = blendMode;
                view.RenderingMode = Rendering.RenderingMode.Composite;
            }
            else
            {
                view.RenderingMode = Rendering.RenderingMode.Lut;
            }
        }
    }
}
