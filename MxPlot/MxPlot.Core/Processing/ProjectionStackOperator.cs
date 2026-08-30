using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using MxPlot.Core.IO;

namespace MxPlot.Core.Processing
{
    /// <summary>
    /// Builds a stack of projections: the target axis is collapsed by projection while every
    /// other axis is preserved, producing one output frame per combination of the surviving axes.
    /// </summary>
    /// <remarks>
    /// This is the stack counterpart of <see cref="VolumeAccessorExtensions.CreateProjection{T}"/>,
    /// which collapses the target axis only at the current position of the other axes.
    /// Typical use: XYCZT with Z projected becomes XYCT, which can then be navigated, saved,
    /// or exported as a movie like any other dataset.
    /// </remarks>
    public static class ProjectionStackOperator
    {
        /// <summary>
        /// Projects along <paramref name="axisName"/> for every combination of the remaining axes.
        /// </summary>
        /// <param name="src">The source matrix data.</param>
        /// <param name="axis">Which direction to project along within each volume.</param>
        /// <param name="mode">Maximum (MIP), Minimum (MinIP) or Average (AIP).</param>
        /// <param name="axisName">
        /// The axis to collapse. Pass an empty string to use the only axis when the data has exactly one.
        /// </param>
        /// <param name="outputMode">
        /// Whether the result is held in memory or backed by a memory-mapped file.
        /// <see cref="LoadingMode.Auto"/> defers to <see cref="VirtualPolicy"/>.
        /// </param>
        /// <param name="progress">
        /// Optional progress reporter. Reports <c>-N</c> once to declare the number of output frames,
        /// then <c>0 ... N-1</c> as each output frame completes.
        /// </param>
        /// <param name="cancellationToken">Cancels the operation; checked once per output frame.</param>
        /// <returns>A new <see cref="MatrixData{T}"/> with the target axis removed.</returns>
        public static MatrixData<T> CreateProjectedStack<T>(
            this MatrixData<T> src,
            ViewFrom axis,
            ProjectionMode mode,
            string axisName = "",
            LoadingMode outputMode = LoadingMode.Auto,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            cancellationToken.ThrowIfCancellationRequested();

            var dims = src.Dimensions;
            if (dims.AxisCount == 0)
                throw new InvalidOperationException(
                    "Projection requires at least one axis to collapse, but the data is a single frame.");

            string targetName = string.IsNullOrEmpty(axisName) ? dims[0].Name : axisName;
            if (!dims.Contains(targetName))
                throw new ArgumentException($"Axis '{targetName}' not found.", nameof(axisName));
            int targetOrder = dims.GetAxisOrder(targetName);

            var groups = DimensionalOperator.EnumerateCollapseGroups(dims, targetOrder);
            progress?.Report(-groups.Count);

            // The first projection settles the output geometry as well: ViewFrom.X / ViewFrom.Y map
            // the collapsed axis onto the output Y axis, so neither the size nor the physical scale
            // can be derived from src alone. Let CreateProjection decide, then follow it.
            var first = Project(src, targetName, groups[0].Coords, axis, mode);
            int width = first.XCount;
            int height = first.YCount;

            long estimatedBytes = (long)width * height * groups.Count * Unsafe.SizeOf<T>();
            var resolved = VirtualPolicy.Resolve(outputMode, estimatedBytes, groups.Count);

            // GetArray rather than AsSpan below: these are not reads. The projection results are
            // throwaway wrappers this method owns, and their buffers are either handed to the
            // vessel or adopted outright by the result - both need the array reference itself.
            MatrixData<T> result;
            if (resolved == LoadingMode.Virtual)
            {
                var vessel = MatrixDataSerializer.CreateTempVessel<T>(width, height, groups.Count);
                vessel.WriteDirectly(0, first.GetArray(0));
                progress?.Report(0);

                for (int g = 1; g < groups.Count; g++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var proj = Project(src, targetName, groups[g].Coords, axis, mode);
                    vessel.WriteDirectly(g, proj.GetArray(0));
                    progress?.Report(g);
                }

                vessel.Flush();
                result = MatrixData<T>.CreateAsVirtualFrames(width, height, vessel);
            }
            else
            {
                // CreateProjection allocates a fresh frame buffer every call, so each one can be
                // adopted as-is rather than copied.
                var frames = new List<T[]>(groups.Count) { first.GetArray(0) };
                progress?.Report(0);

                for (int g = 1; g < groups.Count; g++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var proj = Project(src, targetName, groups[g].Coords, axis, mode);
                    frames.Add(proj.GetArray(0));
                    progress?.Report(g);
                }

                result = new MatrixData<T>(width, height, frames);
            }

            result.SetXYScale(first.XMin, first.XMax, first.YMin, first.YMax);
            result.XUnit = first.XUnit;
            result.YUnit = first.YUnit;
            // Scale is already set from the projection plane, and the collapsed axis is gone by design.
            result.CopyPropertiesFrom(src, copyScale: false, copyDimensions: false);

            var newAxes = dims.CreateAxesWithout(targetName);
            if (newAxes.Length > 0)
                result.DefineDimensions(newAxes);

            return result;
        }

        private static MatrixData<T> Project<T>(
            MatrixData<T> src, string axisName, int[] coords, ViewFrom axis, ProjectionMode mode)
            where T : unmanaged, INumber<T>, IMinMaxValue<T>
            => src.AsVolume(axisName, coords).CreateProjection(axis, mode);
    }
}
