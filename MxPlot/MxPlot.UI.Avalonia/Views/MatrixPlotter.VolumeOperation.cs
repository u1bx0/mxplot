using Avalonia.Controls;
using MxPlot.Core;
using MxPlot.Core.IO.CacheStrategies;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Controls;
using System;
using System.Diagnostics;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Orthogonal side panel ──────────────────────────────────────────────

        //private bool _suppressOrthoResize;

        /// <summary>
        /// Wires the <see cref="AxisTracker.FreezeButton"/> toggle to activate/deactivate
        /// orthogonal side views and adjust the window size accordingly.
        /// </summary>
        private void WireFreezeButton(AxisTracker tracker, Axis axis)
        {
            tracker.FreezeButton.IsCheckedChanged += (_, _) =>
            {
                if (tracker.FreezeButton.IsChecked == true)
                {
                    // When switching axes, suppress the resize fired by deactivating the old button
                    bool wasShowing = _orthoPanel.ShowRight;
                    using var _ = _reentrancy.Begin(GuardContext.Operating);
                    foreach (var child in _trackerPanel.Children)
                        if (child is AxisTracker t && t != tracker)
                            t.FreezeButton.IsChecked = false;

                    if (_currentData != null)
                    {
                        Debug.WriteLine($"[OrthoScale] Activate: axis={axis.Name}");
                        ApplyCacheStrategy(_currentData, axis);
                        _orthoController.Activate(_currentData, axis.Name);
                        // Guard: clamp any active action's ROIs to the new axis dimensions.
                        _activeAction?.NotifyContextChanged(CreateActionContext());
                        if (!wasShowing && WindowState != WindowState.Maximized)
                        {
                            Width += _orthoPanel.SavedSideDeltaWidth;
                            Height += _orthoPanel.SavedSideDeltaHeight;
                        }
                    }
                }
                else
                {
                    // Capture sizes BEFORE deactivating (panels are hidden after Deactivate)
                    var (deltaW, deltaH) = _orthoPanel.GetCurrentSideSizes();
                    Debug.WriteLine($"[OrthoScale] Deactivate: axis={_orthoController.ActiveAxisName ?? "(none)"}");
                    _orthoController.Deactivate();
                    // Notify any active action that orthogonal views are no longer available.
                    _activeAction?.NotifyContextChanged(CreateActionContext());

                    // Reset to neighbour strategy only when no axis is frozen
                    bool anyFrozen = false;
                    foreach (var child in _trackerPanel.Children)
                    {
                        if (child is AxisTracker t && t.FreezeButton.IsChecked == true)
                        {
                            anyFrozen = true; break;
                        }
                    }

                    if (!anyFrozen && _currentData != null)
                        ApplyCacheStrategy(_currentData, null);

                    if (!_reentrancy.IsActive(GuardContext.Operating) && deltaW > 0 && WindowState != WindowState.Maximized)
                    {
                        Width -= deltaW;
                        Height -= deltaH;
                    }
                }
            };
        }

        /// <summary>
        /// Sets <see cref="MxView.OverlayInfoText"/> to the current axis position (and global frame
        /// when there are multiple axes) while the user is dragging an <see cref="AxisTracker"/> slider.
        /// Called on <see cref="AxisTracker.SliderDragStarted"/> and on every <see cref="AxisTracker.IndexChanged"/>
        /// while <c>_isDraggingAxisTracker</c> is <c>true</c>.
        /// </summary>
        private void UpdateAxisDragOverlay(AxisTracker tracker)
        {
            _isDraggingAxisTracker = true;
            string text;
            int axisCount = _currentData?.Axes.Count ?? 0;
            if (axisCount > 1)
            {
                int globalFrame = _currentData?.ActiveIndex ?? 0;
                int totalFrames = _currentData?.FrameCount ?? 1;
                text = $"{tracker.BuildPositionText()}  [{globalFrame + 1}/{totalFrames}]";
            }
            else
            {
                text = tracker.BuildPositionText();
            }
            _view.OverlayInfoTextAnchor = OverlayInfoTextAnchor.TopRight;
            _view.OverlayInfoText = text;
        }

        /// <summary>
        /// Mirrors the WinForms <c>SelectedAxis</c> setter: when <paramref name="targetAxis"/> is set
        /// and the data is virtual, installs a <see cref="DimensionStrategy"/> in Volume mode so that
        /// the on-demand cache pre-fetches the full Z-stack for the selected axis.
        /// When <paramref name="targetAxis"/> is <c>null</c> (or data is in-memory), reverts to
        /// <see cref="NeighborStrategy"/> for normal frame-by-frame pre-fetching.
        /// </summary>
        private static void ApplyCacheStrategy(IMatrixData data, Axis? targetAxis)
        {
            if (!data.IsVirtual) return;

            if (targetAxis != null)
            {
                if (data.CacheStrategy is DimensionStrategy ds)
                {
                    ds.TargetAxis = targetAxis;
                    ds.Mode = DimensionStrategy.CacheMode.Volume;
                    ds.SetTargetChannels([]);
                }
                else
                {
                    var st = new DimensionStrategy(data.Dimensions, targetAxis)
                    {
                        Mode = DimensionStrategy.CacheMode.Volume
                    };
                    data.CacheStrategy = st;
                }
            }
            else
            {
                data.CacheStrategy = new NeighborStrategy();
            }
        }

        // ── Public orthogonal-view control ────────────────────────────────────────

        /// <summary>
        /// Returns the <see cref="AxisTracker"/> that controls the specified axis,
        /// or <c>null</c> if no tracker with that name exists.
        /// </summary>
        /// <param name="axisName">The <see cref="Axis.Name"/> of the target axis.</param>
        public AxisTracker? GetAxisTracker(string axisName)
            => _axisTrackers.TryGetValue(axisName, out var t) ? t : null;

        /// <summary>
        /// Activates the orthogonal side views for the specified axis, or deactivates them
        /// when <paramref name="axisName"/> is <c>null</c>.
        /// Equivalent to toggling the 🧊 freeze button on the corresponding <see cref="AxisTracker"/>.
        /// </summary>
        /// <param name="axisName">
        /// The <see cref="Axis.Name"/> of the axis to freeze, or <c>null</c> to deactivate.
        /// </param>
        public void SetOrthogonalView(string? axisName)
        {
            if (axisName == null)
            {
                foreach (var t in _axisTrackers.Values)
                    t.FreezeButton.IsChecked = false;
                return;
            }
            if (_axisTrackers.TryGetValue(axisName, out var tracker))
                tracker.FreezeButton.IsChecked = true;
        }

        /// <summary>
        /// Gets the name of the axis currently shown in orthogonal side views,
        /// or <c>null</c> when no orthogonal view is active.
        /// </summary>
        public string? OrthogonalViewAxisName => _orthoController.ActiveAxisName;

        // ── XY Projection (Z-direction) ─ opens a separate MatrixPlotter window ──

        // XY (Z-direction) projection window opened via ProjectionSelector
        private MatrixPlotter? _xyProjectionWindow;

        private void OnXYProjectionChanged(object? sender, IMatrixData? projectionData)
        {
            if (projectionData == null)
            {
                CloseXYProjectionWindow();
                return;
            }

            string modeName = _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY) switch
            {
                ProjectionMode.Minimum => "Min",
                ProjectionMode.Average => "Avg",
                _ => "Max",
            };
            string orthoAxisName = _orthoController.ActiveAxisName ?? "Z";

            if (_xyProjectionWindow == null || !_xyProjectionWindow.IsVisible)
            {
                _xyProjectionWindow = MatrixPlotter.Create(projectionData, _view.Lut,
                    $"{modeName} Z-Projection — {Title}");
                _xyProjectionWindow.IsSecondaryWindow = true;
                // Establish LinkedSource so the child delegates Export / token resolution to this plotter.
                _xyProjectionWindow.LinkedSource = this;
                _xyProjectionWindow.LinkedSourceExcludedAxes = [orthoAxisName];
                _xyProjectionWindow.Closed += OnXYProjectionWindowClosed;
                PlotWindowNotifier.SetParentLink(_xyProjectionWindow, this);
                _xyProjectionWindow.Show();
            }
            else
            {
                _xyProjectionWindow.Title = $"{modeName} Z-Projection — {Title}";
                // Keep LinkedSourceExcludedAxes in sync if the ortho axis changed.
                _xyProjectionWindow.LinkedSourceExcludedAxes = [orthoAxisName];
                // Use UpdateProjectionData instead of ViewModel.MatrixData= to avoid a full
                // SetMatrixData re-initialization that would clear overlay state (ROI mode,
                // line profiles, region statistics) on every parent frame change.
                _xyProjectionWindow.UpdateProjectionData(projectionData);
            }
        }

        private void OnXYProjectionWindowClosed(object? sender, EventArgs e)
        {
            if (_xyProjectionWindow != null)
            {
                _xyProjectionWindow.LinkedSource = null;
                _xyProjectionWindow.LinkedSourceExcludedAxes = null;
            }
            _xyProjectionWindow = null;
            _orthoPanel.ProjectionSelector.SetState(ProjectionPlane.XY, false,
                _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY));
            _orthoController.ClearXYProjection();
        }

        private void CloseXYProjectionWindow()
        {
            if (_xyProjectionWindow != null)
            {
                _xyProjectionWindow.Closed -= OnXYProjectionWindowClosed;
                _xyProjectionWindow.LinkedSource = null;
                _xyProjectionWindow.LinkedSourceExcludedAxes = null;
                _xyProjectionWindow.Close();
                _xyProjectionWindow = null;
                _orthoPanel.ProjectionSelector.SetState(ProjectionPlane.XY, false,
                    _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY));
                _orthoController.ClearXYProjection();
            }
        }

        // ── Axis Scale context menu for side views ────────────────────────────

        private System.Collections.Generic.IEnumerable<MenuItem> BuildSideViewContextMenuItems()
        {
            var scaleItem = new MenuItem { Header = "Axis View Scale\u2026", FontSize = 11 };
            scaleItem.Icon = new global::Avalonia.Controls.PathIcon
            {
                Data = MxPlot.UI.Avalonia.Helpers.MenuIcons.Ruler,
                Width = 12,
                Height = 12,
            };
            scaleItem.Click += async (_, _) => await ShowAxisScaleDialogAsync();
            yield return scaleItem;
        }

        private async System.Threading.Tasks.Task ShowAxisScaleDialogAsync()
        {
            var data = _currentData;
            string axisName = _orthoController.ActiveAxisName ?? "";
            double xStep = data?.XStep ?? 0;
            string xUnit = data?.XUnit ?? "";
            double yStep = data?.YStep ?? 0;
            string yUnit = data?.YUnit ?? "";

            // Convert current CustomAxisStep (d, physical units) → display ratio.
            // ratio = ZCount × d / (XCount × XStep)
            // When ScaleMode is Physical, Custom has never been configured for this axis,
            // so default the ratio to 1.0 instead of back-calculating from the placeholder value.
            int xCount = data?.XCount ?? 1;
            int zCount = _orthoPanel.BottomView.MatrixData?.YCount ?? 1;
            double axisStep = _orthoPanel.BottomView.MatrixData?.YStep ?? 0;
            var (xyDisplayW, xyDisplayH) = _orthoPanel.MainView.GetNaturalDims();
            int physicalAxisPx = (xStep > 0 && axisStep > 0 && xyDisplayW > 0)
                ? (int)System.Math.Round(xyDisplayW * axisStep / xStep)
                : zCount;
            double currentRatio;
            if (_orthoController.ScaleMode == OrthoScaleMode.Custom)
            {
                double currentD = _orthoController.CustomAxisStep;
                currentRatio = (xCount > 0 && xStep > 0 && zCount > 0)
                    ? zCount * currentD / (xCount * xStep)
                    : currentD;
            }
            else
            {
                currentRatio = 1.0;
            }

            var dlg = new OrthogonalScaleDialog(
                _orthoController.ScaleMode,
                currentRatio,
                axisName,
                xStep, xUnit,
                yStep, yUnit,
                xyDisplayW, xyDisplayH, physicalAxisPx);
            await dlg.ShowDialog(this);
            if (dlg.ResultMode.HasValue)
            {
                _orthoController.ApplyOrthoViewScale(dlg.ResultMode.Value, dlg.ResultCustomRatio);
                SetScaleDirty(true);
            }
        }
    }
}
