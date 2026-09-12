using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── ROI Live View ────────────────────────────────────────────────────
        //
        // "Open ROI View" on a Rectangle/Oval overlay opens a live linked window showing just the
        // data enclosed by that region, kept in sync via the same LinkedView machinery Log
        // Transform sync / Spatial Filter sync / orthogonal live extracts use. Unlike those, the
        // trigger set includes the overlay's own GeometryChanged (wired in MatrixPlotter.Overlays.cs)
        // so dragging/resizing the region re-crops too, not just a source data/frame change.
        //
        // Each recompute always swaps in a freshly cropped IMatrixData (LinkedViewCommit.UpdateView)
        // rather than writing in place: the crop's own shape changes whenever the region is resized
        // or slides across an edge, so there is no stable "one instance for the window's whole life"
        // to write into the way a fixed-shape live feed has.

        /// <summary>Overlay → (follower window, the LinkedView keeping it cropped and in sync, the
        /// host view the overlay actually lives on - Main/Bottom/Right - for badge redraws).</summary>
        private readonly Dictionary<IAnalyzableOverlay, (MatrixPlotter Window, LinkedView Link, MxView SourceView)> _roiViewLinks = [];

        /// <summary>Toggles the "Open ROI View" state for <paramref name="evaluable"/>.</summary>
        private void OnOpenRoiViewRequested(IAnalyzableOverlay evaluable)
        {
            if (evaluable is not BoundingBoxBase bbox) return;

            // Badge redraws must target whichever view (Main/Bottom/Right) evaluable actually
            // lives on, not always _view - resolved once here (and on creation, below) rather
            // than re-derived every time, since ResolveSourceView needs the overlay object itself.
            if (_roiViewLinks.Remove(evaluable, out var existing))
            {
                evaluable.HasLinkedRoiView = false;
                existing.SourceView.OverlayManager.InvalidateVisual();
                existing.Window.Close();
                return;
            }

            var initial = ComputeRoiViewUpdate(this, bbox);
            if (initial == null)
            {
                SetNotice("ROI has no overlap with the image.");
                return;
            }

            var sourceView = ResolveSourceView(bbox);
            var child = CreateLinked(initial.Value.Data, title: $"{Title} [ROI]", linkRefresh: false);
            if (initial.Value.CompositeChannelAxisName is { } axisName)
                SeedChildCompositeMode(child, initial.Value.Data, axisName);

            var link = new LinkedView(
                child, this,
                (source, ct) => Task.FromResult<LinkedViewUpdate?>(ComputeRoiViewUpdate(source, bbox)),
                LinkedViewCommit.UpdateView);

            // Closing the follower any other way (its own titlebar, Alt+F4, ...) must still clear
            // the badge/dictionary entry - OnOpenRoiViewRequested's own toggle-off already removed
            // the entry before calling Close(), so this is a no-op on that path.
            child.Closed += (_, _) =>
            {
                if (_roiViewLinks.Remove(evaluable))
                {
                    evaluable.HasLinkedRoiView = false;
                    sourceView.OverlayManager.InvalidateVisual();
                }
            };

            _roiViewLinks[evaluable] = (child, link, sourceView);
            evaluable.HasLinkedRoiView = true;
            sourceView.OverlayManager.InvalidateVisual();
            child.Show();
        }

        /// <summary>
        /// Crops <paramref name="source"/>'s current data to <paramref name="bbox"/>'s world-space
        /// bounds, clamped to the data's own extent (never zero-padded - see the design discussion:
        /// an overlay is not itself bounds-limited, so clamping is the only way to keep the result
        /// size bounded by the source data's own size). Composite-aware via
        /// <see cref="TryExtractCompositeFrameCube"/>: crops the whole composited channel cube at
        /// the current position of every other axis, not just the single active frame.
        /// </summary>
        /// <returns>
        /// <see langword="null"/> when the region has no overlap with the data at all (nothing to
        /// show this tick - the follower keeps showing its last valid crop, same as any other
        /// LinkedView recompute that returns null).
        /// </returns>
        private LinkedViewUpdate? ComputeRoiViewUpdate(MatrixPlotter source, BoundingBoxBase bbox)
        {
            // Resolve the view the overlay actually lives on - not source._currentData, which is
            // always the MAIN view's data regardless of where bbox was drawn. Without this, an ROI
            // View opened from a BottomView/RightView overlay cropped the wrong plane entirely
            // (main XY data, sliced with coordinates meant for the side view's own XZ/YZ data).
            var sourceView = source.ResolveSourceView(bbox);
            var data = sourceView.MatrixData;
            if (data == null) return null;

            // bbox.X/Y live in the pixel-CENTER-based world space (pixel i's centre at world i,
            // edges at i +/- 0.5 - see AvaloniaViewport/PixelSnapService), so an edge position e
            // is the edge of data index e+0.5 (pixel k's left edge sits at k-0.5, so k = e+0.5).
            // Round each of the four EDGE positions (already shifted by +0.5) independently to the
            // nearest integer *before* clamping or differencing - not (as an earlier version of
            // this method did) clamp as doubles and only round the derived width/height afterward,
            // and not (an even earlier version) round the raw edge with no +0.5 shift at all, which
            // gets the position right for positive edges but rounds the wrong way for a ROI that
            // overflows past the left/top edge (a negative edge position). Rounding a position and
            // a size that were derived from it separately can also disagree by a pixel right at a
            // clamped boundary, and (int) truncation on a value that should be a whole number but
            // carries floating-point noise from the Math.Max/Min subtraction (e.g. 3.999999994 from
            // what should be exactly 4.0) silently truncates to one less. Math.Round on each
            // shifted edge is immune to all of that: it snaps FP noise to the intended integer, and
            // every value from here on is a real int.
            int worldLeft = (int)Math.Round(bbox.X + 0.5, MidpointRounding.AwayFromZero);
            int worldTop = (int)Math.Round(bbox.Y + 0.5, MidpointRounding.AwayFromZero);
            int worldRight = (int)Math.Round(bbox.X + bbox.Width + 0.5, MidpointRounding.AwayFromZero);
            int worldBottom = (int)Math.Round(bbox.Y + bbox.Height + 0.5, MidpointRounding.AwayFromZero);

            int clampedLeft = Math.Max(0, worldLeft);
            int clampedRight = Math.Min(data.XCount, worldRight);
            int clampedTop = Math.Max(0, worldTop);
            int clampedBottom = Math.Min(data.YCount, worldBottom);

            int w = clampedRight - clampedLeft;
            int h = clampedBottom - clampedTop;
            if (w <= 0 || h <= 0) return null;

            // World space (top-left origin, Y-down) -> data-index space (bottom-left origin, Y-up).
            // sourceView.FlipY says whether the hosting view's bitmap reverses row order against
            // the data array (true - main view and RightView) or reads it directly (false -
            // BottomView, see OrthogonalPanel's constructor) - same split ComputeRegionStatistics
            // uses, and for the same reason: BottomView's XZ slice is laid out row-0-at-Zmin same
            // as any other data, but is displayed without RenderSurface's usual row reversal.
            int x = clampedLeft;
            int dataY = sourceView.FlipY ? data.YCount - clampedBottom : clampedTop;

            var compositeCube = source.TryExtractCompositeFrameCubeByName(data);
            if (compositeCube != null)
            {
                var cropped = compositeCube.Value.Cube.Apply(new CropOperation(x, dataY, w, h));
                return new LinkedViewUpdate(cropped, compositeCube.Value.ChannelAxisName);
            }
            else
            {
                var single = data.Apply(new SliceAtOperation(data.ActiveIndex));
                var cropped = single.Apply(new CropOperation(x, dataY, w, h));
                return new LinkedViewUpdate(cropped);
            }
        }

        /// <summary>
        /// Composite-aware counterpart to <see cref="TryExtractCompositeFrameCube"/> for data that
        /// may not share <see cref="_currentData"/>'s own axis ordering - a BottomView/RightView
        /// slice drops one axis, which can shift the composite axis's dimension *index* even though
        /// its *name* survives the slice (e.g. an XZ slice removes Y, shifting whatever came after
        /// it - including a Channel axis - down by one position). Resolves the channel axis by name
        /// (looked up once against <see cref="_currentData"/>, where <see cref="_compositeAxisDimIndex"/>
        /// is known-valid) instead of reusing that index positionally against <paramref name="data"/>.
        /// </summary>
        private (IMatrixData Cube, string ChannelAxisName)? TryExtractCompositeFrameCubeByName(IMatrixData data)
        {
            if (!_isCompositeMode || _compositeAxisDimIndex < 0 || _currentData == null) return null;
            if (_compositeAxisDimIndex >= _currentData.Dimensions.AxisCount) return null;
            string channelAxisName = _currentData.Dimensions[_compositeAxisDimIndex].Name;
            if (data.Axes.FindAxis(channelAxisName) == null) return null;
            int[] baseIndices = data.Axes.Select(a => a.Index).ToArray();
            var cube = data.Apply(new ExtractAlongOperation(channelAxisName, baseIndices));
            return (cube, channelAxisName);
        }

        /// <summary>
        /// Wired (see <see cref="WireViewEvents"/>) to <paramref name="view"/>'s own
        /// <see cref="MxView.MatrixDataChanged"/> - BottomView/RightView rebuild their sliced
        /// MatrixData asynchronously (<see cref="Controls.OrthogonalViewController.UpdateSlicesAsync"/>)
        /// whenever the main view's slice position moves or Composite mode toggles, and
        /// MatrixDataChanged is what actually fires once that new slice lands. The triggers that
        /// normally drive Statistics/ValueRange/ROI View (the leader's own
        /// <see cref="Refreshed"/>/<c>ActiveIndexChanged</c>) fire synchronously as soon as the
        /// change is requested - well before that async rebuild finishes - so a recompute reached
        /// only through those would read the side view's stale data. The main view needs no
        /// equivalent hook: its own MatrixData *is* <see cref="_currentData"/>, with no separate
        /// async rebuild step in between.
        /// </summary>
        private void OnSideViewMatrixDataChanged(MxView view)
        {
            foreach (var obj in view.OverlayManager.Objects)
                if (obj is BoundingBoxBase bbox)
                    RefreshCachedStatistics(bbox);

            RefreshRoiValueRange();

            foreach (var entry in _roiViewLinks.Values.ToList())
                if (ReferenceEquals(entry.SourceView, view))
                    entry.Link.RequestRecompute();
        }
    }
}
