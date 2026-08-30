using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using MxPlot.UI.Avalonia.Rendering;
using MxPlot.UI.Avalonia.Utils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Complex = System.Numerics.Complex;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Line Profile integration ───────────────────────────────────────────────

        // Line profile: LineObject → (live ProfilePlotter, source MxView)
        private readonly Dictionary<LineObject, (ProfilePlotter Window, MxView SourceView, string? GroupAxisName)> _lineProfileWindows = [];

        // Text edit: TextObject → open TextEditDialog
        private readonly Dictionary<TextObject, TextEditDialog> _textEditDialogs = [];

        // Pen/geometry edit: any OverlayObjectBase → open OverlayPropertyDialog
        private readonly Dictionary<OverlayObjectBase, OverlayPropertyDialog> _propertyDialogs = [];

        // The overlay currently designated as the ROI for value-range computation.
        // Exclusive: at most one overlay may hold this role at a time.
        private IAnalyzableOverlay? _valueRangeOverlay;

        private void OnOverlayObjectAdded(object? sender, OverlayObjectBase obj)
        {
            if (obj is LineObject line)
            {
                line.PlotProfile.Handler = () => OnLinePlotProfileRequested(line);
                line.CalibrateScale.Handler = () => OnLineCalibrateScaleRequested(line);
                line.GeometryChanged += OnLineGeometryChanged;
            }
            if (obj is TextObject text)
            {
                text.Edit.Handler = () => OnTextEditRequested(text);
                // Resolve {N:p}/{N:i} against a data instance that actually has axes: XZ/YZ slice
                // views fall back to this window's own volume, and an XY-projection window - whose
                // projected data has no axes at all - borrows its owner's via OverlayAxisSource.
                text.DataContext = OverlayAxisSource?._currentData
                    ?? _currentData
                    ?? ResolveSourceView(text).MatrixData;
            }
            if (obj is RectObject rect)
                rect.GeometryChanged += OnRectGeometryChanged;
            if (obj is OvalObject oval)
                oval.GeometryChanged += OnBBoxGeometryChanged;
            if (obj is TargetingObject target)
                target.GeometryChanged += OnBBoxGeometryChanged;
            if (obj is IAnalyzableOverlay evaluable)
            {
                evaluable.FindMinMax.Handler = () => OnFindMinMaxRequested(evaluable);
                evaluable.ToggleShowStatistics.Handler = () => OnToggleShowStatisticsRequested(evaluable);
                evaluable.UseRoiForValueRange.Handler = () => OnUseRoiForValueRangeRequested(evaluable);
                evaluable.CopyData.Handler = () => _ = OnCopyDataRequestedAsync(evaluable);
            }
            obj.SelectionChanged += OnOverlaySelectionChanged;
            obj.PenEdit.Handler = () => OnPenEditRequested(obj);
            if (obj is not ISystemOverlay)
                SetDirty(DirtyFlags.Overlay, true);
        }

        

        private void OnOverlayObjectRemoved(object? sender, OverlayObjectBase obj)
        {
            if (obj is LineObject line)
            {
                line.PlotProfile.Handler = null;
                line.CalibrateScale.Handler = null;
                line.GeometryChanged -= OnLineGeometryChanged;
                if (_lineProfileWindows.Remove(line, out var entry))
                    entry.Window.Close();
            }
            if (obj is TextObject text)
            {
                text.DataContext = null;
                text.Edit.Handler = null;
                if (_textEditDialogs.Remove(text, out var dlg))
                    dlg.Close();
            }
            if (obj is RectObject rect)
                rect.GeometryChanged -= OnRectGeometryChanged;
            if (obj is OvalObject oval)
                oval.GeometryChanged -= OnBBoxGeometryChanged;
            if (obj is TargetingObject target)
                target.GeometryChanged -= OnBBoxGeometryChanged;
            if (obj is IAnalyzableOverlay evaluable)
            {
                evaluable.FindMinMax.Handler = null;
                evaluable.ToggleShowStatistics.Handler = null;
                evaluable.UseRoiForValueRange.Handler = null;
                evaluable.CopyData.Handler = null;
            }
            obj.SelectionChanged -= OnOverlaySelectionChanged;
            obj.PenEdit.Handler = null;
            if (_propertyDialogs.Remove(obj, out var propDlg))
                propDlg.Close();
            if (obj.IsSelected)
                ClearOverlayInfo();
            if (obj is IAnalyzableOverlay removed && ReferenceEquals(removed, _valueRangeOverlay))
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] ROI overlay removed — falling back to Current mode.");
                DeactivateRoiMode();
            }
            SetDirty(DirtyFlags.Overlay, true);
        }

        /// <summary>Resolves which <see cref="MxView"/> owns the given overlay object.</summary>
        private MxView ResolveSourceView(OverlayObjectBase obj)
        {
            if (_orthoPanel.BottomView.OverlayManager.Objects.Contains(obj)) return _orthoPanel.BottomView;
            if (_orthoPanel.RightView.OverlayManager.Objects.Contains(obj))  return _orthoPanel.RightView;
            return _view;
        }

        // ── Notice bar (overlay geometry info) ────────────────────────────────────

        private void OnOverlaySelectionChanged(object? sender, bool selected)
        {
            if (sender is not OverlayObjectBase obj) return;
            if (selected)
                UpdateNoticeFromOverlay(obj);
            else
                ResolveSourceView(obj).OverlayInfoText = null;

            // A composite statistics label's per-channel cap depends on IsSelected (see
            // ComputeCompositeStatisticsLabel) -- both the object losing selection (collapses back
            // down) and the one gaining it (expands) need their cached label recomputed, and this
            // fires once per object per transition, covering both sides of a selection change.
            if (obj is BoundingBoxBase bbox) RefreshCachedStatistics(bbox);
        }

        private void OnLineGeometryChanged(object? sender, (global::Avalonia.Point P1, global::Avalonia.Point P2) _)
        {
            if (sender is LineObject line && line.IsSelected)
                UpdateNoticeFromOverlay(line);
            SetDirty(DirtyFlags.Overlay, true);
        }

        private void OnRectGeometryChanged(object? sender, (global::Avalonia.Point Origin, double Width, double Height) _)
        {
            if (sender is not RectObject rect) return;
            if (rect.IsSelected) UpdateNoticeFromOverlay(rect);
            RefreshCachedStatistics(rect);
            if (rect is IAnalyzableOverlay a && ReferenceEquals(a, _valueRangeOverlay))
                RefreshRoiValueRange();
            SetDirty(DirtyFlags.Overlay, true);
        }

        private void OnBBoxGeometryChanged(object? sender, (global::Avalonia.Point Origin, double Width, double Height) _)
        {
            if (sender is not OverlayObjectBase obj) return;
            if (obj.IsSelected) UpdateNoticeFromOverlay(obj);
            if (obj is BoundingBoxBase bbox) RefreshCachedStatistics(bbox);
            if (obj is IAnalyzableOverlay a2 && ReferenceEquals(a2, _valueRangeOverlay))
                RefreshRoiValueRange();
            SetDirty(DirtyFlags.Overlay, true);
        }

        private void UpdateNoticeFromOverlay(OverlayObjectBase obj)
        {
            var sourceView = ResolveSourceView(obj);
            sourceView.OverlayInfoText = obj.GetInfo(sourceView.MatrixData);
        }

        private void ClearOverlayInfo()
        {
            _view.OverlayInfoText = null;
            _orthoPanel.BottomView.OverlayInfoText = null;
            _orthoPanel.RightView.OverlayInfoText = null;
        }

        // ── Region statistics (FindMinMax / ShowStatistics) ─────────────────────

        private void OnFindMinMaxRequested(IAnalyzableOverlay evaluable)
        {
            if (evaluable is not BoundingBoxBase bbox) return;
            var sourceView = ResolveSourceView(bbox);
            var md = sourceView.MatrixData;
            if (md == null) return;

            var stats = ComputeRegionStatistics(evaluable, bbox, md, sourceView.FrameIndex);
            if (stats.NumPoints == 0) return;

            // Always target the main _rangeBar; ModeChanged handler propagates to ortho views
            // via _orthoController.SyncRenderSettings() in the existing wiring.
            _rangeBar.SetRange(stats.Min, stats.Max);
            _rangeBar.SetMode(ValueRangeMode.Fixed);
        }

        private void OnToggleShowStatisticsRequested(IAnalyzableOverlay evaluable)
        {
            if (evaluable is not BoundingBoxBase bbox) return;
            evaluable.ShowStatistics = !evaluable.ShowStatistics;

            if (evaluable.ShowStatistics)
                RecomputeCachedStatistics(evaluable, bbox, ResolveSourceView(bbox));
            else
            {
                evaluable.CachedStatistics = null;
                evaluable.CachedStatisticsLabel = null;
            }
            ResolveSourceView(bbox).OverlayManager.InvalidateVisual();
        }

        // ── ROI value range ───────────────────────────────────────────────────────

        private void OnUseRoiForValueRangeRequested(IAnalyzableOverlay evaluable)
        {

            if (evaluable.IsValueRangeRoi)
            {
                // User toggled off
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] ROI value range deactivated by user.");
                DeactivateRoiMode();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] ROI value range activated by user.");
                ActivateRoiMode(evaluable);
            }
        }

        /// <summary>
        /// Designates <paramref name="evaluable"/> as the ROI for value-range computation,
        /// switches the range bar to ROI mode, and performs an initial range refresh.
        /// Any previously designated ROI overlay is cleared first.
        /// </summary>
        internal void ActivateRoiMode(IAnalyzableOverlay evaluable)
        {
            // Clear the previous ROI if any
            if (_valueRangeOverlay != null && !ReferenceEquals(_valueRangeOverlay, evaluable))
            {
                _valueRangeOverlay.IsValueRangeRoi = false;
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] Previous ROI overlay cleared.");
            }

            _valueRangeOverlay = evaluable;
            evaluable.IsValueRangeRoi = true;
            _rangeBar.SetRoiAvailable(true);
            _rangeBar.SetMode(ValueRangeMode.Roi);
            RefreshRoiValueRange();
            // Redraw all views to show the ROI label
            _view.OverlayManager.InvalidateVisual();
        }

        /// <summary>
        /// Clears the current ROI designation and falls back to Current mode.
        /// Safe to call when no ROI is active.
        /// </summary>
        private void DeactivateRoiMode()
        {
            if (_valueRangeOverlay != null)
            {
                _valueRangeOverlay.IsValueRangeRoi = false;
                _view.OverlayManager.InvalidateVisual();
            }
            _valueRangeOverlay = null;
            _rangeBar.SetRoiAvailable(false);
            // SetRoiAvailable(false) while in Roi mode automatically calls SetMode(Current)
        }

        /// <summary>
        /// Recomputes the value range from the current ROI overlay and updates the bar.
        /// No-op when no ROI is designated or when the bar is not in ROI mode.
        /// </summary>
        internal void RefreshRoiValueRange()
        {
            if (_valueRangeOverlay == null || _rangeBar.Mode != ValueRangeMode.Roi) return;
            if (_valueRangeOverlay is not BoundingBoxBase bbox)
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] RefreshRoiValueRange: ROI overlay is not a BoundingBoxBase — skipping.");
                return;
            }

            var sourceView = ResolveSourceView(bbox);
            var md = sourceView.MatrixData;
            if (md == null)
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] RefreshRoiValueRange: no MatrixData — skipping.");
                return;
            }

            var stats = ComputeRegionStatistics(_valueRangeOverlay, bbox, md, sourceView.FrameIndex);
            if (stats.NumPoints == 0)
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] RefreshRoiValueRange: ROI region is empty — no update.");
                return;
            }

            _view.IsFixedRange = true;
            _view.FixedMin = stats.Min;
            _view.FixedMax = stats.Max;
            _rangeBar.SetRange(stats.Min, stats.Max);
            _orthoController.SyncRenderSettings();

            // SetRange() does not fire RangeChanged event, so manually update histogram if settings panel is open
            // Call UpdateHistogram() instead of SetViewValueRange() to ensure proper async cancellation
            if (_lutModeDetails?.IsVisible == true && _histogramPlot != null)
            {
                UpdateHistogram();
            }
        }

        /// <summary>
        /// Re-computes and caches statistics for <paramref name="bbox"/> if
        /// <see cref="IAnalyzableOverlay.ShowStatistics"/> is active.
        /// No-op when the object does not implement <see cref="IAnalyzableOverlay"/>
        /// or when ShowStatistics is false.
        /// </summary>
        private void RefreshCachedStatistics(BoundingBoxBase bbox)
        {
            if (bbox is not IAnalyzableOverlay evaluable || !evaluable.ShowStatistics) return;
            var sourceView = ResolveSourceView(bbox);
            RecomputeCachedStatistics(evaluable, bbox, sourceView);
            sourceView.OverlayManager.InvalidateVisual();
        }

        /// <summary>
        /// Recomputes and stores <see cref="IAnalyzableOverlay.CachedStatistics"/> or
        /// <see cref="IAnalyzableOverlay.CachedStatisticsLabel"/> (whichever applies) for
        /// <paramref name="bbox"/>. No-op when <paramref name="sourceView"/> has no data.
        /// </summary>
        private void RecomputeCachedStatistics(IAnalyzableOverlay evaluable, BoundingBoxBase bbox, MxView sourceView)
        {
            var md = sourceView.MatrixData;
            if (md == null) return;

            var compositeLabel = ComputeCompositeStatisticsLabel(evaluable, bbox, md, sourceView);
            if (compositeLabel != null)
            {
                evaluable.CachedStatisticsLabel = compositeLabel;
                evaluable.CachedStatistics = null;
            }
            else
            {
                evaluable.CachedStatisticsLabel = null;
                evaluable.CachedStatistics = ComputeRegionStatistics(evaluable, bbox, md, sourceView.FrameIndex);
            }
        }

        /// <summary>
        /// Maximum number of channels shown individually in an <b>unselected</b> overlay's composite
        /// statistics label before the rest are collapsed into a trailing "... (+N more)" line --
        /// several ROIs each showing many composite-axis positions (all visible by default, see
        /// <c>BuildChannelComposite</c>) at once would otherwise clutter the view with tall blocks
        /// for overlays the user isn't currently focused on. The <b>selected</b> overlay (the one the
        /// user just toggled statistics on, or clicked to inspect) shows every visible channel with
        /// no cap -- see <see cref="OnOverlaySelectionChanged"/>, which recomputes this on selection
        /// change so the cap actually updates as focus moves between overlays.
        /// </summary>
        private const int MaxCompositeStatisticsChannelsUnselected = 4;

        /// <summary>
        /// Builds a per-channel statistics label when Composite/ColorCoded rendering is active, one
        /// line per visible channel ("{tag}: Min …, Max …, Avg …") -- the region-statistics
        /// counterpart to <c>RenderSurface.TryAddCompositeValueRuns</c>'s per-channel cursor
        /// read-out, which this mirrors (same <see cref="RenderingMode"/> check, same visible-only
        /// filtering over <see cref="MxView.CompositeRecipes"/>/<see cref="MxView.CompositeFrameIndices"/>).
        /// Each line is labelled via <see cref="GetChannelDisplayName"/> -- the same channel tag
        /// ("GFP", "Red", ...) shown everywhere else in Composite mode, falling back to "Ch0"/"Ch1"
        /// only when the axis carries no tags. A single ordinary <c>RegionStatistics</c> for "the
        /// current frame" would silently reflect only one channel of what the composite actually
        /// displays. Returns <see langword="null"/> when not in Composite/ColorCoded mode (or no
        /// recipes/indices are available), so the caller falls back to the ordinary single-frame
        /// statistics.
        /// </summary>
        private string? ComputeCompositeStatisticsLabel(
            IAnalyzableOverlay evaluable, BoundingBoxBase bbox, IMatrixData md, MxView sourceView)
        {
            if (sourceView.RenderingMode is not (RenderingMode.Composite or RenderingMode.ColorCoded))
                return null;

            var recipes = sourceView.CompositeRecipes;
            var indices = sourceView.CompositeFrameIndices;
            if (recipes is not { Count: > 0 } || indices is not { Length: > 0 })
                return null;

            int count = Math.Min(recipes.Count, indices.Length);
            int maxShown = bbox.IsSelected ? int.MaxValue : MaxCompositeStatisticsChannelsUnselected;
            var lines = new List<string>();
            int shown = 0, visibleTotal = 0;
            int numPoints = 0;
            for (int i = 0; i < count; i++)
            {
                if (!recipes[i].IsVisible) continue;
                visibleTotal++;
                if (shown >= maxShown) continue;
                var stats = ComputeRegionStatistics(evaluable, bbox, md, indices[i]);
                // Every channel shares the same ROI and the same ContainsWorldPoint filter, so the
                // point count is the same for all of them (barring a channel-specific NaN inside the
                // ROI, an edge case not worth a per-channel count here) -- take it once from the
                // first channel actually computed rather than repeating "(n=...)" on every line.
                if (shown == 0) numPoints = stats.NumPoints;
                lines.Add(stats.ToCompactLabel(GetChannelDisplayName(i)));
                shown++;
            }

            if (lines.Count == 0) return null;
            if (visibleTotal > shown)
                lines.Add($"... (+{visibleTotal - shown} more)");
            lines.Add($"(n={numPoints})");
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Iterates over all integer pixel positions within <paramref name="bbox"/>'s bounding box,
        /// filters by <see cref="IAnalyzableOverlay.ContainsWorldPoint"/>,
        /// applies FlipY (<c>dataY = YCount - 1 - worldY</c>), and accumulates statistics.
        /// </summary>
        private static RegionStatistics ComputeRegionStatistics(
            IAnalyzableOverlay evaluable, BoundingBoxBase bbox,
            IMatrixData md, int frameIndex)
        {
            int xMin = Math.Max(0, (int)Math.Ceiling(bbox.X - 0.5 - 1e-10));
            int xMax = Math.Min(md.XCount - 1, (int)Math.Floor(bbox.X + bbox.Width - 0.5 + 1e-10));
            int yMin = Math.Max(0, (int)Math.Ceiling(bbox.Y - 0.5 - 1e-10));
            int yMax = Math.Min(md.YCount - 1, (int)Math.Floor(bbox.Y + bbox.Height - 0.5 + 1e-10));

            // Acquire entire frame once (single lock / zero-alloc for double data)
            var frame = md.GetFrameAsDoubleSpan(frameIndex);

            double min = double.MaxValue, max = double.MinValue, sum = 0;
            int count = 0;

            for (int wy = yMin; wy <= yMax; wy++)
            {
                int dataY = (md.YCount - 1) - wy;   // FlipY: world Y-down → data Y-up
                int rowStart = dataY * md.XCount;
                for (int wx = xMin; wx <= xMax; wx++)
                {
                    if (!evaluable.ContainsWorldPoint(new Point(wx + 0.5, wy + 0.5))) continue;
                    double v = frame[rowStart + wx];
                    if (double.IsNaN(v)) continue;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    sum += v;
                    count++;
                }
            }

            return count == 0
                ? new RegionStatistics(0, 0, 0, 0, 0)
                : new RegionStatistics(min, max, sum / count, sum, count);
        }

        private void OnLinePlotProfileRequested(LineObject line)
        {
            var sourceView = ResolveSourceView(line);
            var md = sourceView.MatrixData;
            if (md == null || md.XStep == 0 || md.YStep == 0) return;

            // Close previous window for this line (re-open / refresh)
            if (_lineProfileWindows.TryGetValue(line, out var existing))
            {
                existing.Window.Close();
                _lineProfileWindows.Remove(line);
            }

            // The composite axis is not always named "Channel" (see ColorCoded_View_InitialDesign.md
            // section 3.3.7) -- look up this window's actual composite axis name instead of assuming
            // it. Axis names survive the per-channel extract+merge that builds orthogonal-view
            // Composite data (see OrthogonalViewController.Composite.cs), so the name found on
            // _currentData still applies to sourceView's (possibly derived) md.
            string? compositeAxisName = _isCompositeMode && _currentData != null
                && _compositeAxisDimIndex >= 0 && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount
                ? _currentData.Dimensions[_compositeAxisDimIndex].Name
                : null;
            string? groupAxisName = (sourceView.RenderingMode == RenderingMode.Composite
                && compositeAxisName != null && md.Dimensions.Contains(compositeAxisName))
                ? compositeAxisName : null;
            var series = BuildProfileSeries(line, md, sourceView, groupAxisName);
            if (series.Count == 0) return;

            var win = new ProfilePlotter(
                //[new PlotSeries(profile, "Profile", PlotStyle.Line)],
                series,
                xAxisLabel: $"Distance ({md.XUnit})",
                yAxisLabel: "Value",
                title: "Line Profile (Bilinear)");
            _lineProfileWindows[line] = (win, sourceView, groupAxisName);
            // Default behaviour: initialize the Y axis to whatever value range is currently
            // displayed for the image -- the header ValueRangeBar (LUT mode) or the shared
            // Composite range (Global scope). This must NOT be gated on IsFixedRange: that flag
            // only governs whether the *rendered image* rescales per frame (false in Current
            // mode), it is not "what range is currently shown" -- DisplayedMinValue/Max already
            // tracks that correctly across Fixed/Current/All/Roi.
            var (vrMin, vrMax) = GetCurrentDisplayedValueRange();
            if (!double.IsNaN(vrMin) && !double.IsNaN(vrMax))
            {
                win.Plot.YAxisFixed = true;
                win.Plot.YFixedMin = vrMin;
                win.Plot.YFixedMax = vrMax;
            }
            else
            {
                win.Plot.YAxisFixed = false;
            }
            
            // Dynamic tracking: update on geometry change
            line.GeometryChanged += (_, _) => UpdateLineProfile(line);

            // Untrack if user closes the profile window directly
            win.Closed += (_, _) => _lineProfileWindows.Remove(line);

            // Highlight the corresponding line while the profile window is active
            win.Activated += (_, _) =>
            {
                foreach (var o in sourceView.OverlayManager.Objects)
                    o.IsSelected = o == line;
                sourceView.OverlayManager.InvalidateVisual.Invoke();
            };
            win.Deactivated += (_, _) =>
            {
                line.IsSelected = false;
                sourceView.OverlayManager.InvalidateVisual.Invoke();
            };
            PlotWindowNotifier.SetParentLink(win, this);
            win.Show();
        }

        /// <summary>
        /// Builds a list of <see cref="PlotSeries"/> by extracting line profiles for all values
        /// of the specified axis, while keeping all other axes fixed at their current positions.
        /// </summary>
        /// <remarks>
        /// If the specified axis is a <see cref="TaggedAxis"/>, the tag string is used as the series label.
        /// Otherwise, the label is formatted as "{AxisName}0", "{AxisName}1", etc.
        /// If the axis does not exist in the dimension structure, a single profile at the current
        /// frame index is returned with the label "Profile".
        /// <code>
        /// // Example: Channel axis with tags "R", "G", "B"
        /// var series = BuildProfileSeries(line, md, sourceView, "Channel");
        /// // → PlotSeries("R", ...), PlotSeries("G", ...), PlotSeries("B", ...)
        ///
        /// // Example: Z axis without tags
        /// var series = BuildProfileSeries(line, md, sourceView, "Z");
        /// // → PlotSeries("Z0", ...), PlotSeries("Z1", ...), ...
        /// </code>
        /// </remarks>
        /// <param name="line">The line object defining the profile path.</param>
        /// <param name="md">The matrix data source.</param>
        /// <param name="sourceView">The source view providing FrameIndex and FlipY.</param>
        /// <param name="groupAxisName">The axis name to iterate over. If null or not found, falls back to a single profile.</param>
        private List<PlotSeries> BuildProfileSeries(LineObject line, IMatrixData md, MxView sourceView, string? groupAxisName = "Channel")
        {
            var result = new List<PlotSeries>();
            var dims = md.Dimensions;

            // Complex data (including Composite) has no natural double value — reduce it using
            // the same ComplexValueMode-based projection currently shown in sourceView's bitmap,
            // so the profile matches what the user sees on screen.
            Func<Complex, double>? complexConverter = md.ValueType == typeof(Complex)
                ? ComplexValueModeConverter.GetConverter(sourceView.ComplexValueMode)
                : null;

            if (groupAxisName != null && dims.Contains(groupAxisName))
            {
                var axis = dims[groupAxisName]!;
                var taggedAxis = axis as TaggedAxis;
                int[] frameIndices = dims.GetFrameIndicesFor(groupAxisName);

                for (int i = 0; i < frameIndices.Length; i++)
                {
                    // Skip channels the user has hidden via BlendRecipe.IsVisible in the Composite
                    // settings panel, so the profile plot only shows what is actually blended into
                    // the displayed image. Independent of value type T.
                    if (i < _compositeRecipes.Count && !_compositeRecipes[i].IsVisible)
                        continue;

                    var profile = ExtractLineProfile(line, md, frameIndices[i], sourceView.FlipY, complexConverter);
                    if (profile.Count == 0) continue;

                    string label = taggedAxis != null ? taggedAxis[i] : $"{axis.Name}{i}";
                    result.Add(new PlotSeries(profile, label, PlotStyle.Line));
                }
            }
            else
            {
                var profile = ExtractLineProfile(line, md, sourceView.FrameIndex, sourceView.FlipY, complexConverter);
                if (profile.Count > 0)
                    result.Add(new PlotSeries(profile, "Profile", PlotStyle.Line));
            }

            return result;
        }

        /// <summary>
        /// Returns the value range currently displayed for the image: the header
        /// <see cref="ValueRangeBar"/> in LUT mode (correct across Fixed/Current/All/Roi, since
        /// <see cref="ValueRangeBar.DisplayedMinValue"/>/<see cref="ValueRangeBar.DisplayedMaxValue"/>
        /// always track what is shown, unlike <c>IsFixedRange</c> which only says whether the
        /// rendered image rescales per frame), or the shared Composite range in Global scope.
        /// Returns <c>NaN</c> when no single range applies (Composite Channel-wise scope, where
        /// each channel owns its own range) -- callers should fall back to auto-fit in that case.
        /// </summary>
        private (double Min, double Max) GetCurrentDisplayedValueRange()
        {
            if (_isCompositeMode)
            {
                return _compositeScope == CompositeRangeScope.Global && _compositeRecipes.Count > 0
                    ? (_compositeRecipes[0].ValueMin, _compositeRecipes[0].ValueMax)
                    : (double.NaN, double.NaN);
            }
            return (_rangeBar.DisplayedMinValue, _rangeBar.DisplayedMaxValue);
        }

        /// <summary>
        /// Opens <see cref="CalibrateScaleDialog"/> and applies the resulting XStep/YStep
        /// to the source <see cref="IMatrixData"/> (XMin/YMin are kept fixed).
        /// Mirrors the Scale tab Wire() pattern: XMax = XMin + step × (N−1).
        /// </summary>
        private async void OnLineCalibrateScaleRequested(LineObject line)
        {
            var sourceView = ResolveSourceView(line);
            var md = sourceView.MatrixData;
            if (md == null) return;

            var dlg = new CalibrateScaleDialog(line, md);

            var worldCenter = line.GetApproxWorldCenter();
            if (worldCenter.HasValue)
            {
                var vp = sourceView.OverlayManager.GetViewport?.Invoke() ?? default;
                var surfacePos = vp.WorldToScreen(worldCenter.Value);
                var osPos = sourceView.SurfacePointToScreen(surfacePos);
                dlg.Position = new PixelPoint(osPos.X + 24, osPos.Y + 24);
            }

            await dlg.ShowDialog(this);
            if (dlg.DialogResult is not { } result) return;

            double dx = line.P2.X - line.P1.X;
            double dy = line.P2.Y - line.P1.Y;
            double lpx = Math.Sqrt(dx * dx + dy * dy);
            if (lpx < 0.5) return;

            double d = result.RealLength;
            string unit = result.Unit;

            // Square-pixel assumption: 1 pixel = D / Lpx regardless of line direction.
            double step = d / lpx;

            switch (result.ApplyTo)
            {
                case CalibrateScaleDialog.ApplyTo.XOnly:
                    md.XMax = md.XMin + step * (md.XCount - 1);
                    md.XUnit = unit;
                    break;
                case CalibrateScaleDialog.ApplyTo.YOnly:
                    md.YMax = md.YMin + step * (md.YCount - 1);
                    md.YUnit = unit;
                    break;
                case CalibrateScaleDialog.ApplyTo.Both:
                    md.XMax = md.XMin + step * (md.XCount - 1);
                    md.YMax = md.YMin + step * (md.YCount - 1);
                    md.XUnit = unit;
                    md.YUnit = unit;
                    break;
            }

            // Refresh view — mirrors Scale tab Wire() pattern
            if (_view.IsFitToView) _view.FitToView(); else _view.InvalidateSurface();
            _orthoController.RefreshCrosshairAndSlices();
            RefreshInfoTab();
            UpdateAllLineProfiles();
        }

        private async void OnTextEditRequested(TextObject text)
        {

            if (_textEditDialogs.ContainsKey(text)) return;

            var sourceView = ResolveSourceView(text);
            var dlg = new TextEditDialog(text,
                () => sourceView.OverlayManager.InvalidateVisual.Invoke());
            _textEditDialogs[text] = dlg;

            // Position the dialog near the overlay object
            var worldCenter = text.GetApproxWorldCenter();
            if (worldCenter.HasValue)
            {
                var vp = sourceView.OverlayManager.GetViewport?.Invoke() ?? default;
                var surfacePos = vp.WorldToScreen(worldCenter.Value);
                var osPos = sourceView.SurfacePointToScreen(surfacePos);
                dlg.Position = new PixelPoint(osPos.X + 24, osPos.Y + 24);
            }

            await dlg.ShowDialog(this);
            _textEditDialogs.Remove(text);
        }

        private async void OnPenEditRequested(OverlayObjectBase obj)
        {

            if (_propertyDialogs.ContainsKey(obj)) return;

            var sourceView = ResolveSourceView(obj);
            var dlg = new OverlayPropertyDialog(obj, sourceView.MatrixData,
                () => sourceView.OverlayManager.InvalidateVisual.Invoke());
            _propertyDialogs[obj] = dlg;

            // Position the dialog near the overlay object
            var worldCenter = obj.GetApproxWorldCenter();
            if (worldCenter.HasValue)
            {
                var vp = sourceView.OverlayManager.GetViewport?.Invoke() ?? default;
                var surfacePos = vp.WorldToScreen(worldCenter.Value);
                var osPos = sourceView.SurfacePointToScreen(surfacePos);
                dlg.Position = new PixelPoint(osPos.X + 24, osPos.Y + 24);
            }

            await dlg.ShowDialog(this);
            _propertyDialogs.Remove(obj);
        }

        private void UpdateLineProfile(LineObject line)
        {
            if (!_lineProfileWindows.TryGetValue(line, out var entry)) return;
            var md = entry.SourceView.MatrixData;
            if (md == null) return;

            var series = BuildProfileSeries(line, md, entry.SourceView, entry.GroupAxisName);
            for (int i = 0; i < series.Count; i++)
                entry.Window.Plot.UpdatePointsAndFit(i, series[i].Points);
        }

        private void UpdateAllLineProfiles()
        {
            foreach (var line in _lineProfileWindows.Keys.ToArray())
                UpdateLineProfile(line);
        }

        /// <summary>
        /// Fully rebuilds every Composite-grouped Line Profile plot (one series per visible channel,
        /// grouped over whichever axis is the current composite axis), including which series exist —
        /// not just their point values. Needed whenever a channel's
        /// <see cref="BlendRecipe.IsVisible"/> is toggled in the Composite settings panel, since that
        /// changes how many series <see cref="BuildProfileSeries"/> returns.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="UpdateLineProfile"/> (index-based <see cref="ProfilePlotControl.UpdatePointsAndFit"/>,
        /// used for lightweight per-frame updates while dragging the line), this calls
        /// <see cref="ProfilePlotControl.SetData"/>, which resets per-series colors and the
        /// legend's manual show/hide toggles — acceptable here because the channel set itself changed.
        /// </remarks>
        private void RebuildChannelGroupedLineProfiles()
        {
            foreach (var (line, entry) in _lineProfileWindows.ToArray())
            {
                if (entry.GroupAxisName == null) continue;
                var md = entry.SourceView.MatrixData;
                if (md == null) continue;

                var series = BuildProfileSeries(line, md, entry.SourceView, entry.GroupAxisName);
                var plot = entry.Window.Plot;
                plot.SetData(series, plot.XLabel, plot.YLabel, plot.PlotTitle);
            }
        }

        private void RefreshAllRegionStatistics()
        {
            foreach (var view in new[] { _view, _orthoPanel.BottomView, _orthoPanel.RightView })
            {
                foreach (var obj in view.OverlayManager.Objects)
                {
                    if (obj is BoundingBoxBase bbox)
                        RefreshCachedStatistics(bbox);
                }
            }
        }

        /// <summary>
        /// Refreshes all overlay analysis results in one call:
        /// line profiles, region statistics (ShowStatistics), and ROI value range.
        /// Call this whenever the underlying data content or active frame changes.
        /// </summary>
        private void RefreshAllOverlayAnalysis()
        {
            UpdateAllLineProfiles();
            RefreshAllRegionStatistics();
            if (_rangeBar.Mode == ValueRangeMode.Roi) RefreshRoiValueRange();
        }

        private static List<(double X, double Y)> ExtractLineProfile(
            LineObject line, IMatrixData md, int frameIndex, bool flipY = true,
            Func<Complex, double>? complexValueConverter = null)
        {
            // ── 1. SAT: return empty when the segment lies entirely outside
            //          the bitmap rectangle [0, XCount-1] × [0, YCount-1] ─────────────
            // (both endpoints share the same out-of-range half-plane on any axis)
            if ((line.P1.X < 0             && line.P2.X < 0)             ||
                (line.P1.X > md.XCount - 1 && line.P2.X > md.XCount - 1) ||
                (line.P1.Y < 0             && line.P2.Y < 0)             ||
                (line.P1.Y > md.YCount - 1 && line.P2.Y > md.YCount - 1))
                return [];

            // ── 2. Clamp endpoints to pixel centres [0, Count-1] ─────────────────────
            static double Clamp(double v, int n) => Math.Clamp(v, 0.0, n - 1.0);
            double wx1 = Clamp(line.P1.X, md.XCount), wy1 = Clamp(line.P1.Y, md.YCount);
            double wx2 = Clamp(line.P2.X, md.XCount), wy2 = Clamp(line.P2.Y, md.YCount);

            // ── 3. World → physical coords ─────────────────────────────────────────────
            // flipY = true  (main / YZ): BitmapWriter flipped → row 0 = YMax → Y = YMax − wy × YStep
            // flipY = false (XZ bottom): no BitmapWriter flip  → row 0 = YMin → Y = YMin + wy × YStep
            double px1 = md.XMin + wx1 * md.XStep;
            double py1 = flipY ? md.YMax - wy1 * md.YStep : md.YMin + wy1 * md.YStep;
            double px2 = md.XMin + wx2 * md.XStep;
            double py2 = flipY ? md.YMax - wy2 * md.YStep : md.YMin + wy2 * md.YStep;

            // ── 4. Order p1→p2 from visual bottom-left toward top-right ───────────────
            // Primary  : smaller physical X  = left  (no X-inversion in any view).
            // Secondary: smaller physical Y  = visual bottom
            //            (YMin is the visual bottom in BOTH flipY modes, so this is uniform).
            if (px1 > px2 || (px1 == px2 && py1 > py2))
                (px1, py1, px2, py2) = (px2, py2, px1, py1);

            (double[] pos, double[] values) = md.ValueType == typeof(Complex)
                ? ((MatrixData<Complex>)md).GetLineProfile(
                    (X: px1, Y: py1), (X: px2, Y: py2),
                    frameIndex: frameIndex, option: LineProfileOption.Bilinear,
                    valueConverter: complexValueConverter ?? (c => c.Magnitude))
                : md.GetLineProfile(
                    (X: px1, Y: py1), (X: px2, Y: py2),
                    frameIndex: frameIndex, option: LineProfileOption.Bilinear);
            var points = new List<(double X, double Y)>(pos.Length);
            for (int i = 0; i < pos.Length; i++)
                points.Add((pos[i], values[i]));
            return points;
        }

        /// <summary>
        /// Closes only the profile windows that were opened as a Composite-grouped plot.
        /// Called when leaving Composite mode, where those multi-series plots no longer match what
        /// the view shows. Profiles opened in LUT mode are single-series and stay untouched, so the
        /// user does not lose unrelated work.
        /// </summary>
        private void CloseChannelGroupedLineProfiles()
        {
            foreach (var (line, entry) in _lineProfileWindows.ToArray())
            {
                if (entry.GroupAxisName == null) continue;
                entry.Window.Close();          // Closed handler also removes the entry
                _lineProfileWindows.Remove(line);
            }
        }

        private void CloseAllLineProfiles()
        {
            foreach (var (win, _, _) in _lineProfileWindows.Values.ToArray())
                win.Close();
            _lineProfileWindows.Clear();
        }

        // ── Copy Data (rect region → clipboard) ────────────────────────────────────

        private async Task OnCopyDataRequestedAsync(IAnalyzableOverlay evaluable)
        {
            if (evaluable is not BoundingBoxBase bbox) return;
            var sourceView = ResolveSourceView(bbox);
            var md = sourceView.MatrixData;
            if (md == null) return;

            var region = ExtractRectRegionData(evaluable, bbox, md, sourceView.FrameIndex);
            if (region == null)
            {
                await ShowMessageDialogAsync("Copy Data", "The selected rectangle has no data points.");
                return;
            }

            double vmin = sourceView.IsFixedRange ? sourceView.FixedMin : double.NaN;
            double vmax = sourceView.IsFixedRange ? sourceView.FixedMax : double.NaN;

            // Physical aspect correction: if XStep != YStep, naturalH is adjusted so that
            // 1 pixel = 1 data point on the longer physical axis.
            int naturalW = region.XCount;
            int naturalH = region.YCount;
            double xStep = Math.Abs(md.XStep > 0 ? md.XStep : 1.0);
            double yStep = Math.Abs(md.YStep > 0 ? md.YStep : 1.0);
            if (Math.Abs(xStep - yStep) > 1e-12)
            {
                if (xStep < yStep)
                    naturalH = Math.Max(1, (int)Math.Round(region.YCount * yStep / xStep));
                else
                    naturalW = Math.Max(1, (int)Math.Round(region.XCount * xStep / yStep));
            }

            // While Composite is active, the ROI image should show the same blended colours as
            // the screen rather than a single (Channel-0) LUT frame. Mirrors the Composite check
            // OnLinePlotProfileRequested already uses for the same sourceView.
            var compositeRecipes = sourceView.CompositeRecipes;
            var compositeFrameIndices = sourceView.CompositeFrameIndices;
            bool useComposite = sourceView.RenderingMode == RenderingMode.Composite
                && compositeRecipes is { Count: > 0 }
                && compositeFrameIndices is { Length: > 0 }
                && compositeRecipes.Count == compositeFrameIndices.Length;

            // Render the region at natural (aspect-corrected) size for the preview + image copy.
            WriteableBitmap RenderRegion()
            {
                if (useComposite)
                {
                    // The "no data in the rectangle" check above already ran on frame 0 — that is
                    // a purely geometric question (ContainsWorldPoint), so it holds for every
                    // channel. A per-channel extraction still failing here would mean the channel
                    // frame index is out of range; fall back to an all-NaN layer rather than throw.
                    var channelArrays = compositeFrameIndices!
                        .Select(fi => ExtractRectRegionPixels(evaluable, bbox, md, fi)?.Values
                                   ?? new double[region.XCount * region.YCount])
                        .ToList();
                    var compositeRegion = new MatrixData<double>(region.XCount, region.YCount, channelArrays);
                    return CompositeBitmapWriter.CreateBitmap(
                        compositeRegion, Enumerable.Range(0, compositeFrameIndices.Length).ToArray(),
                        compositeRecipes!, sourceView.CompositeBlendMode);
                }
                return LutBitmapWriter.CreateBitmap(region, 0, sourceView.Lut, vmin, vmax);
            }

            Bitmap RenderScaled(int w, int h)
            {
                var src = RenderRegion();
                if (w == src.PixelSize.Width && h == src.PixelSize.Height) return src;
                var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
                var mode = (w >= src.PixelSize.Width || h >= src.PixelSize.Height)
                    ? BitmapInterpolationMode.None
                    : BitmapInterpolationMode.LowQuality;
                using (var ctx = rtb.CreateDrawingContext())
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
                    ctx.DrawImage(src, new Rect(0, 0, w, h));
                return rtb;
            }

            // Thumbnail for the dialog preview
            const double maxThumbW = 78, maxThumbH = 58;
            double ta = (double)naturalW / naturalH;
            int tw = ta >= maxThumbW / maxThumbH ? (int)maxThumbW : Math.Max(1, (int)Math.Round(maxThumbH * ta));
            int th = ta >= maxThumbW / maxThumbH ? Math.Max(1, (int)Math.Round(maxThumbW / ta)) : (int)maxThumbH;
            var thumb = RenderScaled(tw, th);

            var dlg = new CopyImageDialog(thumb, naturalW, naturalH, naturalW, naturalH,
                refreshPreview: null, showOverlaysOption: false, showCustomSizeOption: false);
            await dlg.ShowDialog(this);
            if (dlg.Result == null) return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;

            var res = dlg.Result;
            if (res.Mode == CopyMode.Text)
            {
                var sb = new StringBuilder();
                for (int row = region.YCount - 1; row >= 0; row--)
                {
                    for (int col = 0; col < region.XCount; col++)
                    {
                        if (col > 0) sb.Append(res.Separator);
                        sb.Append(region.GetValueAt(col, row, 0).ToString("G6", CultureInfo.InvariantCulture));
                    }
                    sb.AppendLine();
                }
                await clipboard.SetTextAsync(sb.ToString());
            }
            else
            {
                // ActualPixelSize: render at physical-aspect-corrected size
                var bmp = RenderScaled(naturalW, naturalH);
                using var ms = new System.IO.MemoryStream();
                if (bmp is RenderTargetBitmap rtb2) rtb2.Save(ms);
                else bmp.Save(ms);
                MxView.SetLastCopiedPng(ms.ToArray());
                await clipboard.SetBitmapAsync(bmp);
            }
        }

        /// <summary>
        /// Extracts pixels within <paramref name="bbox"/> at <paramref name="frameIndex"/> into a
        /// dense row-major array, in the same bottom-origin row order every <see cref="MatrixData{T}"/>
        /// in this codebase uses (row 0 = the bottom of the display) — the convention
        /// <see cref="LutBitmapWriter"/>/<see cref="CompositeBitmapWriter"/> already expect via their
        /// own <c>FlipY</c> handling, so callers never need to flip the result themselves.
        /// Pixels outside <see cref="IAnalyzableOverlay.ContainsWorldPoint"/> are stored as NaN.
        /// Returns <c>null</c> when the rectangle contains no in-bounds pixels — a purely geometric
        /// question, independent of <paramref name="frameIndex"/>, so a caller extracting several
        /// channels of the same rectangle only needs to check this once.
        /// </summary>
        private static (double[] Values, int Width, int Height)? ExtractRectRegionPixels(
            IAnalyzableOverlay evaluable, BoundingBoxBase bbox,
            IMatrixData md, int frameIndex)
        {
            // PixelSnapMode.Corner snaps to pixel edges  (bbox coords are half-integers: 2.5, 9.5…)
            // PixelSnapMode.Center snaps to pixel centres (bbox coords are integers:       3,  10…)
            // Pixel wx is inside when:  bbox.X ≤ wx+0.5 < bbox.X+bbox.Width
            //   → xMin = ⌈ bbox.X − 0.5 ⌉    (first wx whose centre is ≥ bbox.X)
            //   → xMax = ⌊ bbox.X+W − 0.5 − ε ⌋  (last  wx whose centre is <  bbox.X+W)
            int xMin = Math.Max(0, (int)Math.Ceiling(bbox.X - 0.5 - 1e-10));
            int xMax = Math.Min(md.XCount - 1, (int)Math.Floor(bbox.X + bbox.Width - 0.5 - 1e-10));
            int yMin = Math.Max(0, (int)Math.Ceiling(bbox.Y - 0.5 - 1e-10));
            int yMax = Math.Min(md.YCount - 1, (int)Math.Floor(bbox.Y + bbox.Height - 0.5 - 1e-10));
            if (xMax < xMin || yMax < yMin) return null;

            int w = xMax - xMin + 1;
            int h = yMax - yMin + 1;
            var frame = md.GetFrameAsDoubleSpan(frameIndex);
            var arr = new double[w * h];

            for (int wy = yMin; wy <= yMax; wy++)
            {
                int dataY = (md.YCount - 1) - wy;
                // wy is screen/world space (Y-down: yMin is the top of the rect on screen).
                // destRow must land in the opposite (bottom-origin) convention, so the row at the
                // screen bottom of the rect (wy = yMax) becomes row 0.
                int destRow = yMax - wy;
                for (int wx = xMin; wx <= xMax; wx++)
                {
                    double v = evaluable.ContainsWorldPoint(new Point(wx + 0.5, wy + 0.5))
                        ? frame[dataY * md.XCount + wx]
                        : double.NaN;
                    arr[destRow * w + (wx - xMin)] = v;
                }
            }

            if (arr.All(double.IsNaN)) return null;
            return (arr, w, h);
        }

        /// <summary>
        /// Extracts pixels within <paramref name="bbox"/> into a new single-frame
        /// <see cref="MatrixData{T}"/> of type <c>double</c>. See
        /// <see cref="ExtractRectRegionPixels"/> for the row convention and NaN handling.
        /// </summary>
        private static MatrixData<double>? ExtractRectRegionData(
            IAnalyzableOverlay evaluable, BoundingBoxBase bbox,
            IMatrixData md, int frameIndex)
        {
            var pixels = ExtractRectRegionPixels(evaluable, bbox, md, frameIndex);
            if (pixels == null) return null;
            var (arr, w, h) = pixels.Value;
            return new MatrixData<double>(w, h, arr);
        }
    }
}
