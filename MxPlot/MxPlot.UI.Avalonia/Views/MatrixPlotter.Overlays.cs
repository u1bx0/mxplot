using Avalonia;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Line Profile integration ───────────────────────────────────────────────

        // Line profile: LineObject → (live ProfilePlotter, source MxView)
        private readonly Dictionary<LineObject, (ProfilePlotter Window, MxView SourceView)> _lineProfileWindows = [];

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
                line.PlotProfileRequested += OnLinePlotProfileRequested;
                line.CalibrateScaleRequested += OnLineCalibrateScaleRequested;
                line.GeometryChanged += OnLineGeometryChanged;
            }
            if (obj is TextObject text)
            {
                text.EditRequested += OnTextEditRequested;
                text.DataContext = ResolveSourceView(text).MatrixData;
            }
            if (obj is RectObject rect)
                rect.GeometryChanged += OnRectGeometryChanged;
            if (obj is OvalObject oval)
                oval.GeometryChanged += OnBBoxGeometryChanged;
            if (obj is TargetingObject target)
                target.GeometryChanged += OnBBoxGeometryChanged;
            if (obj is IAnalyzableOverlay evaluable)
            {
                evaluable.FindMinMaxRequested += OnFindMinMaxRequested;
                evaluable.ToggleShowStatisticsRequested += OnToggleShowStatisticsRequested;
                evaluable.UseRoiForValueRangeRequested += OnUseRoiForValueRangeRequested;
            }
            obj.SelectionChanged += OnOverlaySelectionChanged;
            obj.PenEditRequested += OnPenEditRequested;
        }

        

        private void OnOverlayObjectRemoved(object? sender, OverlayObjectBase obj)
        {
            if (obj is LineObject line)
            {
                line.PlotProfileRequested -= OnLinePlotProfileRequested;
                line.CalibrateScaleRequested -= OnLineCalibrateScaleRequested;
                line.GeometryChanged -= OnLineGeometryChanged;
                if (_lineProfileWindows.Remove(line, out var entry))
                    entry.Window.Close();
            }
            if (obj is TextObject text)
            {
                text.DataContext = null;
                text.EditRequested -= OnTextEditRequested;
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
                evaluable.FindMinMaxRequested -= OnFindMinMaxRequested;
                evaluable.ToggleShowStatisticsRequested -= OnToggleShowStatisticsRequested;
                evaluable.UseRoiForValueRangeRequested -= OnUseRoiForValueRangeRequested;
            }
            obj.SelectionChanged -= OnOverlaySelectionChanged;
            obj.PenEditRequested -= OnPenEditRequested;
            if (_propertyDialogs.Remove(obj, out var propDlg))
                propDlg.Close();
            if (obj.IsSelected)
                ClearOverlayInfo();
            // If the removed object is the active ROI overlay, fall back gracefully
            if (obj is IAnalyzableOverlay removed && ReferenceEquals(removed, _valueRangeOverlay))
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] ROI overlay removed — falling back to Current mode.");
                DeactivateRoiMode();
            }
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
        }

        private void OnLineGeometryChanged(object? sender, (global::Avalonia.Point P1, global::Avalonia.Point P2) _)
        {
            if (sender is LineObject line && line.IsSelected)
                UpdateNoticeFromOverlay(line);
        }

        private void OnRectGeometryChanged(object? sender, (global::Avalonia.Point Origin, double Width, double Height) _)
        {
            if (sender is not RectObject rect) return;
            if (rect.IsSelected) UpdateNoticeFromOverlay(rect);
            RefreshCachedStatistics(rect);
            if (rect is IAnalyzableOverlay a && ReferenceEquals(a, _valueRangeOverlay))
                RefreshRoiValueRange();
        }

        private void OnBBoxGeometryChanged(object? sender, (global::Avalonia.Point Origin, double Width, double Height) _)
        {
            if (sender is not OverlayObjectBase obj) return;
            if (obj.IsSelected) UpdateNoticeFromOverlay(obj);
            if (obj is BoundingBoxBase bbox) RefreshCachedStatistics(bbox);
            if (obj is IAnalyzableOverlay a2 && ReferenceEquals(a2, _valueRangeOverlay))
                RefreshRoiValueRange();
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

        private void OnFindMinMaxRequested(object? sender, EventArgs e)
        {
            if (sender is not (BoundingBoxBase bbox and IAnalyzableOverlay evaluable)) return;
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

        private void OnToggleShowStatisticsRequested(object? sender, EventArgs e)
        {
            if (sender is not (BoundingBoxBase bbox and IAnalyzableOverlay evaluable)) return;
            evaluable.ShowStatistics = !evaluable.ShowStatistics;

            if (evaluable.ShowStatistics)
            {
                var sourceView = ResolveSourceView(bbox);
                if (sourceView.MatrixData != null)
                    evaluable.CachedStatistics = ComputeRegionStatistics(
                        evaluable, bbox, sourceView.MatrixData, sourceView.FrameIndex);
            }
            else
            {
                evaluable.CachedStatistics = null;
            }
            ResolveSourceView(bbox).OverlayManager.InvalidateVisual();
        }

        // ── ROI value range ───────────────────────────────────────────────────────

        private void OnUseRoiForValueRangeRequested(object? sender, EventArgs e)
        {
            if (sender is not IAnalyzableOverlay evaluable) return;

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
            if (_roiRadio != null) _roiRadio.IsVisible = true;
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
            if (_roiRadio != null) _roiRadio.IsVisible = false;
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
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] RefreshRoiValueRange: ROI overlay is not a BoundingBoxBase \u2014 skipping.");
                return;
            }

            var sourceView = ResolveSourceView(bbox);
            var md = sourceView.MatrixData;
            if (md == null)
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] RefreshRoiValueRange: no MatrixData \u2014 skipping.");
                return;
            }

            var stats = ComputeRegionStatistics(_valueRangeOverlay, bbox, md, sourceView.FrameIndex);
            if (stats.NumPoints == 0)
            {
                System.Diagnostics.Debug.WriteLine("[MatrixPlotter] RefreshRoiValueRange: ROI region is empty \u2014 no update.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[MatrixPlotter] RefreshRoiValueRange: min={stats.Min}, max={stats.Max}");
            _view.IsFixedRange = true;
            _view.FixedMin = stats.Min;
            _view.FixedMax = stats.Max;
            _rangeBar.SetRange(stats.Min, stats.Max);
            _orthoController.SyncRenderSettings();
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
            if (sourceView.MatrixData == null) return;
            evaluable.CachedStatistics = ComputeRegionStatistics(
                evaluable, bbox, sourceView.MatrixData, sourceView.FrameIndex);
            sourceView.OverlayManager.InvalidateVisual();
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
            int xMin = Math.Max(0, (int)Math.Floor(bbox.X));
            int xMax = Math.Min(md.XCount - 1, (int)Math.Ceiling(bbox.X + bbox.Width));
            int yMin = Math.Max(0, (int)Math.Floor(bbox.Y));
            int yMax = Math.Min(md.YCount - 1, (int)Math.Ceiling(bbox.Y + bbox.Height));

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

        private void OnLinePlotProfileRequested(object? sender, EventArgs e)
        {
            if (sender is not LineObject line) return;
            var sourceView = ResolveSourceView(line);
            var md = sourceView.MatrixData;
            if (md == null || md.XStep == 0 || md.YStep == 0) return;

            // Close previous window for this line (re-open / refresh)
            if (_lineProfileWindows.TryGetValue(line, out var existing))
            {
                existing.Window.Close();
                _lineProfileWindows.Remove(line);
            }

            var profile = ExtractLineProfile(line, md, sourceView.FrameIndex, sourceView.FlipY);
            if (profile.Count == 0) return;

            var win = new ProfilePlotter(
                [new PlotSeries(profile, "Profile", PlotStyle.Line)],
                xAxisLabel: $"Distance ({md.XUnit})",
                yAxisLabel: "Value",
                title: "Line Profile (Bilinear)");
            _lineProfileWindows[line] = (win, sourceView);
            //Default behaviour: Y axis is fixed:
            if (_view.IsFixedRange)
            {
                var min = _view.FixedMin;
                var max = _view.FixedMax;
                win.Plot.YAxisFixed = true;
                win.Plot.YFixedMin = min;
                win.Plot.YFixedMax = max;
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
        /// Opens <see cref="CalibrateScaleDialog"/> and applies the resulting XStep/YStep
        /// to the source <see cref="IMatrixData"/> (XMin/YMin are kept fixed).
        /// Mirrors the Scale tab Wire() pattern: XMax = XMin + step × (N−1).
        /// </summary>
        private async void OnLineCalibrateScaleRequested(object? sender, EventArgs e)
        {
            if (sender is not LineObject line) return;
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

        private async void OnTextEditRequested(object? sender, EventArgs e)
        {
            if (sender is not TextObject text) return;

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

        private async void OnPenEditRequested(object? sender, EventArgs e)
        {
            if (sender is not OverlayObjectBase obj) return;

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

            var profile = ExtractLineProfile(line, md, entry.SourceView.FrameIndex, entry.SourceView.FlipY);
            entry.Window.Plot.UpdatePointsAndFit(0, profile); //if profile.Count == 0, no plots are shown.
            
        }

        private void UpdateAllLineProfiles()
        {
            foreach (var line in _lineProfileWindows.Keys.ToArray())
                UpdateLineProfile(line);
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
            LineObject line, IMatrixData md, int frameIndex, bool flipY = true)
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

            var (pos, values) = md.GetLineProfile(
                (X: px1, Y: py1), (X: px2, Y: py2),
                frameIndex: frameIndex, option: LineProfileOption.Bilinear);
            var points = new List<(double X, double Y)>(pos.Length);
            for (int i = 0; i < pos.Length; i++)
                points.Add((pos[i], values[i]));
            return points;
        }

        private void CloseAllLineProfiles()
        {
            foreach (var (win, _) in _lineProfileWindows.Values.ToArray())
                win.Close();
            _lineProfileWindows.Clear();
        }
    }
}
