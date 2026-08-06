using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── View-settings persistence via IMatrixData.Metadata ───────────────
        //
        // These keys use the "mxplot." prefix to form a UI-layer namespace inside
        // the format-agnostic Metadata dictionary.  Any viewer that does not
        // understand MatrixPlotter simply ignores them.

        private const string KeyLutName = "mxplot.lut.name";
        private const string KeyLutLevel = "mxplot.lut.level";
        private const string KeyLutInverted = "mxplot.lut.inverted";
        private const string KeyVrMode = "mxplot.vr.mode";
        private const string KeyVrMin = "mxplot.vr.min";
        private const string KeyVrMax = "mxplot.vr.max";
        private const string KeyAxesIndices = "mxplot.axes.indices";
        private const string KeyOverlays = "mxplot.overlays";
        private const string KeyOrthoScaleModePrefix = "mxplot.ortho.";
        private const string KeyOrthoScaleModeSuffix = ".scaleMode";
        private const string KeyOrthoCustomRatioSuffix = ".custom";

        // Render mode discriminator
        // Values: "Lut" | "Composite" | "ColorCoded"
        // Absent = "Lut" (backward compatibility)
        private const string KeyRenderMode = "mxplot.render.mode";

        // Composite / ColorCoded (shared key group)
        // mxplot.composite.blend
        // mxplot.composite.{i}.visible
        // mxplot.composite.{i}.color
        // mxplot.composite.{i}.min
        // mxplot.composite.{i}.max
        // mxplot.composite.{i}.gain
        // mxplot.composite.{i}.gamma
        private const string KeyCompositeBlend = "mxplot.composite.blend";
        private const string KeyCompositePrefix = "mxplot.composite.";
        private const string KeyChVisible = ".visible";
        private const string KeyChColor = ".color";
        private const string KeyChMin = ".min";
        private const string KeyChMax = ".max";
        private const string KeyChGain = ".gain";
        private const string KeyChGamma = ".gamma";

        /// <summary>
        /// Serializes current overlay objects into <c>_currentData.Metadata</c>
        /// (key <c>mxplot.overlays</c>). Call this before taking a data snapshot
        /// (<see cref="IMatrixData.Clone"/>) when overlay state must survive the clone.
        /// If there are no overlays the key is removed from Metadata.
        /// No-op when no data is loaded.
        /// </summary>
        public void StoreOverlaysToMetadata()
        {
            if (_currentData == null) return;
            var overlayJson = _view.OverlayManager.SerializeOverlays();
            if (string.IsNullOrWhiteSpace(overlayJson) || overlayJson == "[]")
                _currentData.Metadata.Remove(KeyOverlays);
            else
                _currentData.Metadata[KeyOverlays] = overlayJson;
        }

        /// <summary>
        /// Writes the current view settings (LUT, value-range, axis positions)
        /// into <c>_currentData.Metadata</c> so that they are persisted by any
        /// format writer that round-trips the Metadata dictionary.
        /// Call this immediately before <see cref="IMatrixData.SaveAs"/>.
        /// </summary>
        private void SaveViewSettings()
        {
            if (_currentData == null) return;
            if (_reentrancy.IsActive(GuardContext.Initializing)) return;
            var meta = _currentData.Metadata;

            // LUT
            meta[KeyLutName] = _view.Lut?.Name ?? "";
            meta[KeyLutLevel] = _view.LutDepth.ToString(CultureInfo.InvariantCulture);
            meta[KeyLutInverted] = _view.IsInvertedColor.ToString();

            // Value range
            meta[KeyVrMode] = _rangeBar.Mode switch
            {
                ValueRangeMode.Fixed => "Fixed",
                ValueRangeMode.All => "All",
                ValueRangeMode.Roi => "ROI",
                _ => "Current",
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

            // Orthogonal scale (per axis)
            foreach (var kv in _orthoController.OrthoViewScalePerAxis)
            {
                string axisName = kv.Key;
                meta[KeyOrthoScaleModePrefix + axisName + KeyOrthoScaleModeSuffix] = kv.Value.Mode.ToString();
                if (kv.Value.Mode == OrthoScaleMode.Custom)
                {
                    string ratioStr = kv.Value.Ratio.ToString("R", CultureInfo.InvariantCulture);
                    meta[KeyOrthoScaleModePrefix + axisName + KeyOrthoCustomRatioSuffix] = ratioStr;
                    Debug.WriteLine($"[OrthoScale] Save: axis={axisName} mode=Custom ratio={ratioStr}");
                }
                else
                {
                    meta.Remove(KeyOrthoScaleModePrefix + axisName + KeyOrthoCustomRatioSuffix);
                    Debug.WriteLine($"[OrthoScale] Save: axis={axisName} mode={kv.Value.Mode}");
                }
            }

            // Render mode (Composite / ColorCoded)
            SaveRenderModeSettings(meta);
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
            //_suppressModified = true;
            using var _init = _reentrancy.Begin(GuardContext.Initializing);
            var meta = data.Metadata;
            Debug.WriteLine($"[MatrixPlotter.RestoreViewSettings] Read Metadat, Count = {meta.Count}");
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
                    _rangeBar.SetMode(true);
                    _rangeBar.SetRange(vrMin, vrMax);
                    // ModeChanged may overwrite FixedMin/Max via DisplayedMinValue or ScanCurrentFrameRange.
                    // Re-assign after SetMode/SetRange to guarantee the correct persisted values.
                    _view.FixedMin = vrMin;
                    _view.FixedMax = vrMax;
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
            // Orthogonal scale (per axis) — inject into controller before any Activate call
            if (data.Axes.Count > 0)
            {
                foreach (var axis in data.Axes)
                {
                    string key = KeyOrthoScaleModePrefix + axis.Name + KeyOrthoScaleModeSuffix;
                    if (meta.TryGetValue(key, out string? modeStr)
                        && System.Enum.TryParse<OrthoScaleMode>(modeStr, out var orthoMode))
                    {
                        double ratio = 1.0;
                        if (orthoMode == OrthoScaleMode.Custom)
                        {
                            string ratioKey = KeyOrthoScaleModePrefix + axis.Name + KeyOrthoCustomRatioSuffix;
                            if (meta.TryGetValue(ratioKey, out string? ratioStr))
                                double.TryParse(ratioStr, System.Globalization.NumberStyles.Any,
                                    CultureInfo.InvariantCulture, out ratio);
                            Debug.WriteLine($"[OrthoScale] Restore: axis={axis.Name} mode=Custom ratio={ratio}");
                        }
                        else
                        {
                            Debug.WriteLine($"[OrthoScale] Restore: axis={axis.Name} mode={orthoMode}");
                        }
                        _orthoController.SetSavedOrthoViewScale(axis.Name, orthoMode, ratio);
                    }
                }
            }

            // Render mode (Composite / ColorCoded) — must come after axes restoration
            RestoreRenderModeSettings(meta, data);
        }

        /// <summary>
        /// Saves the current rendering mode and composite settings to metadata.
        /// </summary>
        private void SaveRenderModeSettings(System.Collections.Generic.IDictionary<string, string> meta)
        {
            meta[KeyRenderMode] = _view.RenderingMode.ToString();

            if (_view.RenderingMode is RenderingMode.Composite or RenderingMode.ColorCoded
                && _view.CompositeRecipes != null)
            {
                meta[KeyCompositeBlend] = _view.CompositeBlendMode.ToString();
                SaveCompositeRecipes(meta, _view.CompositeRecipes);
            }
            else
            {
                RemoveCompositeKeys(meta);
            }
        }

        /// <summary>
        /// Saves composite recipes to metadata.
        /// </summary>
        private void SaveCompositeRecipes(
            System.Collections.Generic.IDictionary<string, string> meta,
            System.Collections.Generic.IReadOnlyList<BlendRecipe> recipes)
        {
            RemoveCompositeKeys(meta);
            for (int i = 0; i < recipes.Count; i++)
            {
                var r = recipes[i];
                string pfx = $"{KeyCompositePrefix}{i}";
                meta[$"{pfx}{KeyChVisible}"] = r.IsVisible.ToString();
                meta[$"{pfx}{KeyChColor}"] = r.ColorArgb.ToString(CultureInfo.InvariantCulture);
                meta[$"{pfx}{KeyChMin}"] = r.ValueMin.ToString("R", CultureInfo.InvariantCulture);
                meta[$"{pfx}{KeyChMax}"] = r.ValueMax.ToString("R", CultureInfo.InvariantCulture);
                meta[$"{pfx}{KeyChGain}"] = r.Gain.ToString("R", CultureInfo.InvariantCulture);
                meta[$"{pfx}{KeyChGamma}"] = r.Gamma.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Removes all composite-related keys from metadata.
        /// </summary>
        private void RemoveCompositeKeys(System.Collections.Generic.IDictionary<string, string> meta)
        {
            meta.Remove(KeyCompositeBlend);
            int i = 0;
            while (true)
            {
                string pfx = $"{KeyCompositePrefix}{i}";
                if (!meta.ContainsKey($"{pfx}{KeyChColor}")) break;
                foreach (var s in new[]
                    { KeyChVisible, KeyChColor, KeyChMin, KeyChMax, KeyChGain, KeyChGamma })
                    meta.Remove($"{pfx}{s}");
                i++;
            }
        }

        /// <summary>
        /// Restores rendering mode and composite settings from metadata.
        /// </summary>
        private void RestoreRenderModeSettings(
            System.Collections.Generic.IDictionary<string, string> meta, IMatrixData data)
        {
            if (!meta.TryGetValue(KeyRenderMode, out string? modeStr)) return;
            if (string.IsNullOrEmpty(modeStr)) return;
            if (!System.Enum.TryParse<RenderingMode>(modeStr, out var mode)) return;
            if (mode == RenderingMode.Lut) return; // デフォルトのため処理不要

            var recipes = LoadCompositeRecipes(meta);
            if (recipes.Count == 0) return;

            var blendMode = BlendMode.Additive;
            if (meta.TryGetValue(KeyCompositeBlend, out string? blendStr))
            {
                System.Enum.TryParse(blendStr, out blendMode);
            }

            int[] indices = System.Linq.Enumerable.Range(0, recipes.Count).ToArray();

            _view.CompositeFrameIndices = indices;
            _view.CompositeRecipes = recipes;
            _view.CompositeBlendMode = blendMode;
            _view.RenderingMode = mode;

            Debug.WriteLine($"[MatrixPlotter.RestoreRenderModeSettings] Restored {mode} mode with {recipes.Count} recipes");
        }

        /// <summary>
        /// Loads composite recipes from metadata.
        /// </summary>
        private System.Collections.Generic.List<BlendRecipe> LoadCompositeRecipes(
            System.Collections.Generic.IDictionary<string, string> meta)
        {
            var recipes = new System.Collections.Generic.List<BlendRecipe>();
            int i = 0;
            while (true)
            {
                string pfx = $"{KeyCompositePrefix}{i}";
                if (!meta.ContainsKey($"{pfx}{KeyChColor}")) break;

                bool visible = !meta.TryGetValue($"{pfx}{KeyChVisible}", out string? vs)
                            || !bool.TryParse(vs, out bool v) || v;
                int color = meta.TryGetValue($"{pfx}{KeyChColor}", out string? cs)
                         && int.TryParse(cs, out int c)
                          ? c : unchecked((int)0xFFFFFFFF);
                double min = meta.TryGetValue($"{pfx}{KeyChMin}", out string? mns)
                          && double.TryParse(mns, CultureInfo.InvariantCulture, out double mn)
                           ? mn : 0.0;
                double max = meta.TryGetValue($"{pfx}{KeyChMax}", out string? mxs)
                          && double.TryParse(mxs, CultureInfo.InvariantCulture, out double mx)
                           ? mx : 1.0;
                double gain = meta.TryGetValue($"{pfx}{KeyChGain}", out string? gs)
                           && double.TryParse(gs, CultureInfo.InvariantCulture, out double g)
                            ? g : 1.0;
                double gamma = meta.TryGetValue($"{pfx}{KeyChGamma}", out string? gms)
                            && double.TryParse(gms, CultureInfo.InvariantCulture, out double gm)
                             ? gm : 1.0;

                recipes.Add(new BlendRecipe(visible, color, min, max, gain, gamma));
                i++;
            }
            return recipes;
        }
    }
}
