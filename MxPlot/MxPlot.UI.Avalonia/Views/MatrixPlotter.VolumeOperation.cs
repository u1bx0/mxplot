using Avalonia.Controls;
using MxPlot.Core;
using MxPlot.Core.IO.CacheStrategies;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Controls;
using System;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Orthogonal side panel ──────────────────────────────────────────────

        private bool _suppressOrthoResize;

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
                    _suppressOrthoResize = true;
                    foreach (var child in _trackerPanel.Children)
                        if (child is AxisTracker t && t != tracker)
                            t.FreezeButton.IsChecked = false;
                    _suppressOrthoResize = false;

                    if (_currentData != null)
                    {
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
                    _orthoController.Deactivate();

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

                    if (!_suppressOrthoResize && deltaW > 0 && WindowState != WindowState.Maximized)
                    {
                        Width -= deltaW;
                        Height -= deltaH;
                    }
                }
            };
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

            if (_xyProjectionWindow == null || !_xyProjectionWindow.IsVisible)
            {
                string modeName = _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY) switch
                {
                    ProjectionMode.Minimum => "Min",
                    ProjectionMode.Average => "Avg",
                    _ => "Max",
                };
                _xyProjectionWindow = MatrixPlotter.Create(projectionData, _view.Lut,
                    $"{modeName} Z-Projection — {Title}");
                _xyProjectionWindow.Closed += OnXYProjectionWindowClosed;
                PlotWindowNotifier.SetParentLink(_xyProjectionWindow, this);
                _xyProjectionWindow.Show();
            }
            else
            {
                string modeName = _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY) switch
                {
                    ProjectionMode.Minimum => "Min",
                    ProjectionMode.Average => "Avg",
                    _ => "Max",
                };
                _xyProjectionWindow.Title = $"{modeName} Z-Projection — {Title}";
                if (_xyProjectionWindow.ViewModel != null)
                    _xyProjectionWindow.ViewModel.MatrixData = projectionData;
            }
        }

        private void OnXYProjectionWindowClosed(object? sender, EventArgs e)
        {
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
                _xyProjectionWindow.Close();
                _xyProjectionWindow = null;
                _orthoPanel.ProjectionSelector.SetState(ProjectionPlane.XY, false,
                    _orthoPanel.ProjectionSelector.GetMode(ProjectionPlane.XY));
                _orthoController.ClearXYProjection();
            }
        }
    }
}
