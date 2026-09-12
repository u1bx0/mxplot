using MxPlot.Core;
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
        // mxplot.composite.axis
        // mxplot.composite.blend
        // mxplot.composite.{i}.visible
        // mxplot.composite.{i}.color
        // mxplot.composite.{i}.min
        // mxplot.composite.{i}.max
        // mxplot.composite.{i}.gain
        // mxplot.composite.{i}.gamma
        // Which axis is composited -- Composite is no longer restricted to one named "Channel"
        // (see Tests.Documents/Working/ColorCoded/ColorCoded_View_InitialDesign.md section 3.3.7).
        private const string KeyCompositeAxis = "mxplot.composite.axis";
        private const string KeyCompositeBlend = "mxplot.composite.blend";
        private const string KeyCompositePrefix = "mxplot.composite.";
        private const string KeyChVisible = ".visible";
        private const string KeyChColor = ".color";
        private const string KeyChMin = ".min";
        private const string KeyChMax = ".max";
        private const string KeyChGain = ".gain";
        private const string KeyChGamma = ".gamma";
        // Composite value-range scope/mode (added with Global vs Channel-wise support).
        // Absent = Global + Current, matching the defaults, so older files still load.
        private const string KeyCompositeScope  = "mxplot.composite.scope";
        private const string KeyCompositeVrMode = "mxplot.composite.vrmode";
        private const string KeyChVrMode = ".vrmode";

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
        /// <param name="isFirstLoad">
        /// <c>true</c> only when this window had no data before. Gates the "open an RGB colour image
        /// in Composite" default so that a later in-place data swap (Crop, Reverse Stack, …) never
        /// pushes the user back into a mode they deliberately left.
        /// </param>
        private void RestoreViewSettings(IMatrixData data, bool restoreVR = true, bool isFirstLoad = false)
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
                    UpdateWindowIcon();
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

            // Render mode (Composite / ColorCoded) — must come after axes restoration.
            // RestoreRenderModeSettings only rehydrates the Composite *fields*; entering the mode
            // (which builds the header, the channel rows and the orthogonal composite state) is
            // SyncCompositeUiAfterRestore's job. See MatrixPlotter.Composite.cs.
            var restoredMode = RestoreRenderModeSettings(meta, data);

            // A file that carries mxplot.render.mode has already had its display mode decided, so
            // that always wins. Only when the key is absent does the data itself get a say, and an
            // RGB colour image is the one case where LUT mode is the wrong default.
            if (restoredMode == RenderingMode.Lut && isFirstLoad && !meta.ContainsKey(KeyRenderMode)
                && TryAutoEnterRgbComposite(data))
                return;

            SyncCompositeUiAfterRestore(data, restoredMode);
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
                // SaveCompositeRecipes calls RemoveCompositeKeys internally to clear stale
                // per-channel entries before rewriting them -- that also wipes KeyCompositeAxis
                // and KeyCompositeBlend, so those two must be (re)written *after* it runs, not
                // before. Writing them first meant every save silently discarded the axis name,
                // which made a saved Composite file reopen in LUT mode (RestoreRenderModeSettings
                // never found mxplot.composite.axis to resolve).
                SaveCompositeRecipes(meta, _view.CompositeRecipes);
                if (_currentData != null && _compositeAxisDimIndex >= 0
                    && _compositeAxisDimIndex < _currentData.Dimensions.AxisCount)
                    meta[KeyCompositeAxis] = _currentData.Dimensions[_compositeAxisDimIndex].Name;
                meta[KeyCompositeBlend] = _view.CompositeBlendMode.ToString();
                meta[KeyCompositeScope] = _compositeScope.ToString();
                meta[KeyCompositeVrMode] = _compositeGlobalMode.ToString();
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
                if (_compositeBars != null && i < _compositeBars.Length)
                    meta[$"{pfx}{KeyChVrMode}"] = _compositeBars[i].RangeMode.ToString();
            }
        }

        /// <summary>
        /// Removes all composite-related keys from metadata.
        /// </summary>
        private void RemoveCompositeKeys(System.Collections.Generic.IDictionary<string, string> meta)
        {
            meta.Remove(KeyCompositeAxis);
            meta.Remove(KeyCompositeBlend);
            meta.Remove(KeyCompositeScope);
            meta.Remove(KeyCompositeVrMode);
            int i = 0;
            while (true)
            {
                string pfx = $"{KeyCompositePrefix}{i}";
                if (!meta.ContainsKey($"{pfx}{KeyChColor}")) break;
                foreach (var s in new[]
                    { KeyChVisible, KeyChColor, KeyChMin, KeyChMax, KeyChGain, KeyChGamma, KeyChVrMode })
                    meta.Remove($"{pfx}{s}");
                i++;
            }
        }

        /// <summary>
        /// Reads the persisted rendering mode and rehydrates the Composite fields from metadata,
        /// and returns the mode the caller should switch to.
        /// <para>
        /// <c>mxplot.render.mode</c> is the "which mode does this file open in?" flag, and it is
        /// deliberately honoured <b>on its own</b>: a file carrying only that key (no
        /// <c>mxplot.composite.*</c> recipes) still opens in Composite, because
        /// <see cref="EnterCompositeMode"/> generates default recipes whenever the recipe count does
        /// not match the channel count. The composite keys are an optional refinement of the flag,
        /// not a precondition for it.
        /// </para>
        /// <para>
        /// This method deliberately does not touch <see cref="MxView.RenderingMode"/> or the
        /// composite properties on <see cref="_view"/>; <see cref="EnterCompositeMode"/> sets all of
        /// them, and computes the frame indices from the real <c>DimensionStructure</c> rather than
        /// assuming the Channel axis is the fastest-varying one.
        /// </para>
        /// </summary>
        private RenderingMode RestoreRenderModeSettings(
            System.Collections.Generic.IDictionary<string, string> meta, IMatrixData data)
        {
            if (!meta.TryGetValue(KeyRenderMode, out string? modeStr)) return RenderingMode.Lut;
            if (string.IsNullOrEmpty(modeStr)) return RenderingMode.Lut;
            if (!System.Enum.TryParse<RenderingMode>(modeStr, out var mode)) return RenderingMode.Lut;
            if (mode == RenderingMode.Lut) return RenderingMode.Lut; // default: nothing to restore

            var recipes = LoadCompositeRecipes(meta);
            _compositeRecipes = recipes;

            _compositeBlendMode = BlendMode.Additive;
            if (meta.TryGetValue(KeyCompositeBlend, out string? blendStr)
                && System.Enum.TryParse<BlendMode>(blendStr, out var blendMode))
                _compositeBlendMode = blendMode;

            // Range scope / modes. Absent keys keep the defaults (Global + Current), so files
            // written before this feature existed still restore cleanly.
            if (meta.TryGetValue(KeyCompositeScope, out string? scopeStr)
                && System.Enum.TryParse<CompositeRangeScope>(scopeStr, out var scope))
                _compositeScope = scope;
            if (meta.TryGetValue(KeyCompositeVrMode, out string? vrStr)
                && System.Enum.TryParse<ValueRangeMode>(vrStr, out var vrMode))
                _compositeGlobalMode = vrMode;
            _compositeRestoredModes = recipes.Count > 0
                ? LoadCompositeChannelModes(meta, recipes.Count)
                : null;

            Debug.WriteLine($"[MatrixPlotter.RestoreRenderModeSettings] Restored {mode} mode with {recipes.Count} recipes");
            return mode;
        }

        /// <summary>
        /// Loads the per-channel value-range modes. Any channel whose key is missing falls back to
        /// <see cref="ValueRangeMode.Current"/>, which is the default for a freshly entered session.
        /// </summary>
        private static ValueRangeMode[] LoadCompositeChannelModes(
            System.Collections.Generic.IDictionary<string, string> meta, int count)
        {
            var modes = new ValueRangeMode[count];
            for (int i = 0; i < count; i++)
            {
                modes[i] = meta.TryGetValue($"{KeyCompositePrefix}{i}{KeyChVrMode}", out string? ms)
                        && System.Enum.TryParse<ValueRangeMode>(ms, out var m)
                    ? m : ValueRangeMode.Current;
            }
            return modes;
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
