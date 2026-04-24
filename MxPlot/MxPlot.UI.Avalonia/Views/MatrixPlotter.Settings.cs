using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Overlays;
using System;
using System.Diagnostics;
using System.Globalization;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── View-settings persistence via IMatrixData.Metadata ───────────────
        //
        // These keys use the "mxplot." prefix to form a UI-layer namespace inside
        // the format-agnostic Metadata dictionary.  Any viewer that does not
        // understand MatrixPlotter simply ignores them.

        private const string KeyLutName      = "mxplot.lut.name";
        private const string KeyLutLevel     = "mxplot.lut.level";
        private const string KeyLutInverted  = "mxplot.lut.inverted";
        private const string KeyVrMode       = "mxplot.vr.mode";
        private const string KeyVrMin        = "mxplot.vr.min";
        private const string KeyVrMax        = "mxplot.vr.max";
        private const string KeyAxesIndices  = "mxplot.axes.indices";
        private const string KeyOverlays     = "mxplot.overlays";

        /// <summary>
        /// Writes the current view settings (LUT, value-range, axis positions)
        /// into <c>_currentData.Metadata</c> so that they are persisted by any
        /// format writer that round-trips the Metadata dictionary.
        /// Call this immediately before <see cref="IMatrixData.SaveAs"/>.
        /// </summary>
        private void SaveViewSettings()
        {
            if (_currentData == null) return;
            var meta = _currentData.Metadata;

            // LUT
            meta[KeyLutName] = _view.Lut?.Name ?? "";
            meta[KeyLutLevel] = _view.LutDepth.ToString(CultureInfo.InvariantCulture);
            meta[KeyLutInverted] = _view.IsInvertedColor.ToString();

            // Value range
            meta[KeyVrMode] = _rangeBar.Mode switch
            {
                ValueRangeMode.Fixed => "Fixed",
                ValueRangeMode.All   => "All",
                ValueRangeMode.Roi   => "ROI",
                _                    => "Current",
            };
            if (_rangeBar.Mode == ValueRangeMode.Fixed)
            {
                meta[KeyVrMin] = _view.FixedMin.ToString("R", CultureInfo.InvariantCulture);
                meta[KeyVrMax] = _view.FixedMax.ToString("R", CultureInfo.InvariantCulture);
            }
            else
            {
                meta.Remove(KeyVrMin);
                meta.Remove(KeyVrMax);
            }

            // Axis positions (CSV of each Axis.Index)
            var axes = _currentData.Axes;
            if (axes.Count > 0)
            {
                var indices = new int[axes.Count];
                for (int i = 0; i < axes.Count; i++)
                    indices[i] = axes[i].Index;
                meta[KeyAxesIndices] = string.Join(",", indices);
            }
            else
            {
                meta.Remove(KeyAxesIndices);
            }

            // Overlays
            var overlayJson = _view.OverlayManager.SerializeOverlays();
            if (overlayJson == "[]" || string.IsNullOrWhiteSpace(overlayJson))
                meta.Remove(KeyOverlays);
            else
                meta[KeyOverlays] = overlayJson;
        }

        /// <summary>
        /// Reads <c>mxplot.*</c> keys from <paramref name="data"/>.Metadata and
        /// restores the corresponding view state (LUT selector, depth, inversion,
        /// value-range mode, and axis positions).
        /// Call this at the end of <see cref="SetMatrixData"/> after trackers are built.
        /// </summary>
        /// <param name="data">The new matrix data whose metadata is read.</param>
        /// <param name="restoreVR">
        /// When <c>true</c> (default), the value-range mode and Fixed min/max are restored
        /// from metadata. Pass <c>false</c> when the current Fixed range must be preserved
        /// across a data update (e.g. linked filter refresh).
        /// </param>
        private void RestoreViewSettings(IMatrixData data, bool restoreVR = true)
        {
            var meta = data.Metadata;
            Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Read Metada, Count = {meta.Count}");
            // LUT
            if (meta.TryGetValue(KeyLutName, out string? lutName) && !string.IsNullOrEmpty(lutName))
            {
                try
                {
                    Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Found LUT name: {lutName}");
                    var lut = ColorThemes.Get(lutName);
                    _view.Lut = lut;
                    _lutSelector.SelectLut(lut);
                    Icon = _lutSelector.SelectedIcon;
                    if (DataContext is ViewModels.MatrixPlotterViewModel vm)
                    {
                        vm.Lut = lut;
                    }
                }
                catch { /* unknown LUT name — keep current */ }
            }

            if (meta.TryGetValue(KeyLutLevel, out string? levelStr)
                && int.TryParse(levelStr, CultureInfo.InvariantCulture, out int level)
                && level >= 2 && level <= 4096)
            {
                Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Found LUT level: {level}");
                _view.LutDepth = level;
                if (_levelNud != null) _levelNud.Value = level;
                _orthoController.SyncRenderSettings();
            }

            if (meta.TryGetValue(KeyLutInverted, out string? invertedStr)
                && bool.TryParse(invertedStr, out bool inverted))
            {
                Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Found LUT inverted: {inverted}");
                _view.IsInvertedColor = inverted;
                if (_invertLutChk != null) _invertLutChk.IsChecked = inverted;
                _orthoController.SyncRenderSettings();
            }

            // Value range — skipped when preserving an existing Fixed range across live data updates
            if (restoreVR && meta.TryGetValue(KeyVrMode, out string? vrMode))
            {
                Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Found VR mode: {vrMode}");
                if (string.Equals(vrMode, "Fixed", StringComparison.OrdinalIgnoreCase)
                    && meta.TryGetValue(KeyVrMin, out string? minStr)
                    && meta.TryGetValue(KeyVrMax, out string? maxStr)
                    && double.TryParse(minStr, CultureInfo.InvariantCulture, out double vrMin)
                    && double.TryParse(maxStr, CultureInfo.InvariantCulture, out double vrMax))
                {
                    _view.IsFixedRange = true;
                    _view.FixedMin = vrMin;
                    _view.FixedMax = vrMax;
                    _rangeBar.SetMode(true);
                    _rangeBar.SetRange(vrMin, vrMax);
                    _suppressModeSync = true;
                    if (_autoRadio != null) _autoRadio.IsChecked = false;
                    if (_fixedRadio != null) _fixedRadio.IsChecked = true;
                    _suppressModeSync = false;
                    _orthoController.SyncRenderSettings();
                }
                else if (string.Equals(vrMode, "All", StringComparison.OrdinalIgnoreCase)
                         && data.FrameCount > 1)
                {
                    // fires ModeChanged → ApplyAllModeRange()
                    _rangeBar.SetMode(ValueRangeMode.All);
                }
                else if (string.Equals(vrMode, "Current", StringComparison.OrdinalIgnoreCase))
                {
                    // Overrides InMemory default of All when user had explicitly chosen Current
                    _rangeBar.SetMode(ValueRangeMode.Current);
                }
                // else unknown/invalid mode — keep unchanged
            }

            // Axis positions
            if (meta.TryGetValue(KeyAxesIndices, out string? indicesStr)
                && !string.IsNullOrEmpty(indicesStr))
            {
                Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Found axis indices: {indicesStr}");
                var parts = indicesStr.Split(',');
                var axes = data.Axes;
                for (int i = 0; i < parts.Length && i < axes.Count; i++)
                {
                    if (int.TryParse(parts[i].Trim(), CultureInfo.InvariantCulture, out int idx)
                        && idx >= 0 && idx < axes[i].Count)
                    {
                        axes[i].Index = idx;
                    }
                }
            }

            // Overlays
            if (meta.TryGetValue(KeyOverlays, out string? overlayJson)
                && !string.IsNullOrWhiteSpace(overlayJson))
            {
                Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Restoring overlays.");
                _view.OverlayManager.LoadOverlays(overlayJson, clearExisting: true);
            }

            // ROI value range — must be applied after overlays are restored so the
            // overlay reference is available. If no ROI overlay is found, silently ignore.
            if (restoreVR
                && meta.TryGetValue(KeyVrMode, out string? vrModeForRoi)
                && string.Equals(vrModeForRoi, "ROI", StringComparison.OrdinalIgnoreCase))
            {
                IAnalyzableOverlay? roiOverlay = null;
                foreach (var obj in _view.OverlayManager.Objects)
                {
                    if (obj is IAnalyzableOverlay a && a.IsValueRangeRoi)
                    {
                        roiOverlay = a;
                        break;
                    }
                }
                if (roiOverlay != null)
                {
                    Debug.WriteLine("[MatrixPlotter.RestoreViewSettings] Restoring ROI value range mode.");
                    ActivateRoiMode(roiOverlay);
                }
                else
                {
                    Debug.WriteLine("[MatrixPlotter.RestoreViewSettings] ROI mode in metadata but no ROI overlay found — staying in current mode.");
                }
            }
        }
    }
}
