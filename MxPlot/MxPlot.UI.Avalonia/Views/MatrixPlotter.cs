using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Plugins;
using MxPlot.UI.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Standalone window that displays <see cref="IMatrixData"/> via <see cref="MxView"/>.
    /// Usage: <c>new MatrixPlotter { DataContext = MatrixPlotterViewModel.Create(data, lut) }.Show();</c>
    /// </summary>
    public partial class MatrixPlotter : Window
    {
        private readonly MxView _view;
        private readonly LutSelector _lutSelector;
        private readonly StackPanel _trackerPanel;
        private readonly OrthogonalPanel _orthoPanel;
        private readonly OrthogonalViewController _orthoController;

        // For managing the ActiveIndexChanged subscription across MatrixData swaps
        private IMatrixData? _currentData;
        private EventHandler? _activeIndexHandler;
        private bool _settingMatrixData; // re-entrancy guard for MainView.MatrixDataChanged → SetMatrixData
        private readonly Dictionary<string, AxisTracker> _axisTrackers = [];

        // Status bar segments
        private readonly TextBlock _dirtyBadge;   // "●" shown when view settings are modified
        private readonly TextBlock _infoText;     // "[float]  11.9 GB"
        private readonly TextBlock _virtualBadge; // "(Virtual)" → clickable, visible only when virtual
        private readonly TextBlock _zoomText;     // "|  200% [Fit]"
        private readonly TextBlock _noticeText;   // transient info (overlay dimensions, etc.)
        private readonly TextBlock _progressSep;  // "|" separator before progress area
        private readonly TextBlock _progressText; // "Saving… 3/100"
        private readonly ProgressBar _progressBar;
        private Border? _inputBlocker;           // transparent hit-test blocker during blocking operations
        private Action? _inputBlockerCleanup;    // removes the OverlayLayer.PropertyChanged handler
        private CancellationTokenSource? _toastCts;
        private readonly Border _contentBorder;  // wraps window content for sync border highlight

        // One CacheMonitorWindow per MatrixPlotter
        private CacheMonitorWindow? _cacheMonitorWindow;

        // Value range bar + inline settings panel
        private readonly ValueRangeBar _rangeBar;
        private readonly Button _settingsBtn;
        private readonly Border _settingsPanel;
        private ToggleButton? _invertLutChk;
        private NumericUpDown? _levelNud;
        private RadioButton? _autoRadio;
        private RadioButton? _fixedRadio;
        private RadioButton? _allRadio;           // multi-frame only
        private RadioButton? _roiRadio;           // shown only when ROI overlay is designated
        private PathIcon? _allWarningIcon;     // ⚠ shown when All range is imperfect
        private bool _suppressModeSync;

        // Hamburger menu panel (OverlayLayer — renders inside the window's Skia surface
        // so alpha transparency correctly shows the underlying view content)
        private Border? _menuPanel;
        private Button? _hamburgerBtn;
        private StackPanel? _scaleTabBody;  // info tab content, rebuilt on SetMatrixData()
        private ListBox? _metaKeyList;
        private TextBox? _metaValueBox;
        private TextBox? _metaNewKeyBox;
        private Button? _metaCopyBtn;
        private Button? _metaSaveBtn;
        private CancellationTokenSource? _metaLoadCts;
        private string? _metaRawValue;
        private string? _metaDisplayedValue; // text currently shown; used to detect edits
        private bool _metaSwitchGuard;    // prevents re-entrant SelectionChanged during programmatic revert
        private string? _metaPreviousKey;    // last successfully loaded key; used for dirty-check on switch

        // Linked plotter support
        private bool _isRefreshing;
        private readonly List<MatrixPlotter> _linkedChildren = [];

        // File session state
        [Flags]
        private enum DirtyFlags
        {
            None    = 0,
            Lut     = 1 << 0,
            Vr      = 1 << 1,
            Scale   = 1 << 2,
            Overlay = 1 << 3,
            Data    = 1 << 4,
        }
        private DirtyFlags _dirty;
        private bool _suppressModified;  // true while RestoreViewSettings runs; prevents false dirty flags
        private bool _initializingData;  // true during SetMatrixData; suppresses LUT/VR dirty signals

        // SourcePath is owned by MatrixPlotterViewModel (MatrixData and SourcePath are a pair).
        // This property provides transparent read/write access without scattering null-checks.
        // When no ViewModel is present (VM-less usage), get returns null and set is a no-op —
        // which is correct: no VM means no file association.
        private string? SourcePath
        {
            get => (DataContext as MatrixPlotterViewModel)?.SourcePath;
            set
            {
                if (DataContext is MatrixPlotterViewModel vm)
                    vm.SourcePath = value;
                else
                    Debug.WriteLine("[MatrixPlotter.SourcePath] Warning: no ViewModel; ignoring set to " + value);
            }
        }

        // ── LUT/VR revert state ─────────────────────────────────────────────
        private Button? _lutVrRevertBtn;

        /// <summary>Immutable snapshot of LUT/VR settings captured when data is loaded or last saved.</summary>
        private sealed record LutVrSnapshot(
            string LutName, int LutLevel, bool Inverted,
            ValueRangeMode VrMode, double VrMin, double VrMax);
        private LutVrSnapshot? _lutVrSnapshot;

        // ── Scale/Axis revert state ─────────────────────────────────────────
        private Button? _scaleRevertBtn;

        /// <summary>Snapshot of X/Y scale and per-axis scale/unit captured when data is loaded or last saved.</summary>
        private sealed record AxisSnapshot(double Min, double Max, string Unit, string Name);
        private sealed record ScaleSnapshot(
            double XMin, double XMax, string XUnit,
            double YMin, double YMax, string YUnit,
            AxisSnapshot[] Axes);
        private ScaleSnapshot? _scaleSnapshot;

        // Crop undo state (Replace mode only; single-level)
        private IMatrixData? _cropUndoData;
        private DirtyFlags _cropUndoDirty;
        private string? _cropUndoTitle;
        private LutVrSnapshot? _cropUndoLutVrSnapshot;
        private ScaleSnapshot? _cropUndoScaleSnapshot;

        // Active interactive action (Crop, etc.)
        private IPlotterAction? _activeAction;

        // ── Resume settings state ────────────────────────────────────────────
        /// <summary>
        /// When <c>true</c>, swapping <see cref="Window.DataContext"/> (or otherwise replacing
        /// the displayed <see cref="IMatrixData"/>) will carry over the current view state to
        /// the new data: axis tracker indices are clamped to the new data's range and restored,
        /// the frozen (Volume) axis is re-applied when an axis with the same name exists, and
        /// the value-range mode/values are preserved instead of being reset to defaults.
        /// <para>Default is <c>false</c>.</para>
        /// </summary>
        public bool ResumeSettingsEnabled { get; set; }

        private sealed record ResumeSnapshot(
            System.Collections.Generic.Dictionary<string, int> AxisIndices,
            string? FrozenAxisName,
            ValueRangeMode VrMode,
            double VrMin,
            double VrMax);

        // ── Factory / typed ViewModel accessor

        /// <summary>
        /// <b>Recommended entry point.</b> Creates a <see cref="MatrixPlotter"/> fully configured
        /// for use as a standalone window: builds a <see cref="MatrixPlotterViewModel"/>, sets it
        /// as <see cref="Window.DataContext"/>, and registers the window with
        /// <see cref="PlotWindowNotifier"/> so it appears in the MxPlot.App window dashboard.
        /// <para>
        /// The caller is responsible for calling <see cref="Window.Show"/> or
        /// <see cref="Window.ShowDialog"/> to display the window.
        /// </para>
        /// <para>
        /// Use the default constructor instead when you need fine-grained control over the
        /// ViewModel lifecycle, want to set <see cref="Window.DataContext"/> yourself, or are
        /// embedding <see cref="MatrixPlotter"/> in a custom application shell that manages
        /// window registration independently.
        /// </para>
        /// </summary>
        /// <param name="data">The matrix data to display.</param>
        /// <param name="lut">Initial lookup table. Defaults to <see cref="ColorThemes.Grayscale"/>.</param>
        /// <param name="title">Window title. Defaults to a string derived from data dimensions.</param>
        /// <param name="sourcePath">
        /// Optional path or sentinel value that controls save/close behavior:
        /// <list type="bullet">
        ///   <item><c>null</c> (default) — no file association; close confirmation is suppressed
        ///         and Save always opens a file picker.</item>
        ///   <item>A real file path — enables direct overwrite on Save and triggers a
        ///         "Save changes?" dialog on close when the data has been modified.</item>
        ///   <item>A colon-prefixed sentinel such as <c>":measurement"</c> — marks the window
        ///         as having unsaveable-in-place data (e.g. live acquisition); close confirmation
        ///         is shown when modified (<see cref="ShouldConfirmClose"/> is <c>true</c>),
        ///         but Save always opens a file picker because <see cref="HasFile"/> is
        ///         <c>false</c> for any path starting with <c>':'</c>.</item>
        /// </list>
        /// </param>
        /// <example>
        /// <code>
        /// MatrixPlotter.Create(data, ColorThemes.Jet, "My result").Show();
        /// // Live measurement — close confirmation enabled, no overwrite path:
        /// MatrixPlotter.Create(measured, null, "Scan 001", sourcePath: ":measurement").Show();
        /// </code>
        /// </example>
        public static MatrixPlotter Create(
            IMatrixData data,
            LookupTable? lut = null,
            string? title = null,
            string? sourcePath = null)
        {
            var vm = MatrixPlotterViewModel.Create(data, lut, title, sourcePath);
            var plotter = new MatrixPlotter { DataContext = vm };
            PlotWindowNotifier.NotifyCreated(plotter, data);
            return plotter;
        }

        /// <summary>
        /// Typed accessor for the window's <see cref="DataContext"/>.
        /// Returns <c>null</c> when no <see cref="MatrixPlotterViewModel"/> has been set.
        /// </summary>
        public MatrixPlotterViewModel? ViewModel => DataContext as MatrixPlotterViewModel;

        /// <summary>
        /// The <see cref="IMatrixData"/> currently displayed.
        /// Returns <c>null</c> when no data has been set.
        /// </summary>
        public IMatrixData? MatrixData => _currentData;

        /// <summary>
        /// The primary (main) view component embedded in this window.
        /// Use this to subscribe to pointer/keyboard events, access
        /// <see cref="MxView.OverlayManager"/>, control zoom, and read display state.
        /// <para>
        /// To replace the displayed data, either assign <see cref="MxView.MatrixData"/> directly
        /// (recommended for code-behind scenarios) or set <c>ViewModel.MatrixData</c>
        /// (recommended for MVVM). Both paths keep axis trackers, orthogonal views,
        /// and value-range state in sync automatically.
        /// </para>
        /// </summary>
        public MxView MainView => _view;

        /// <summary>
        /// The bottom orthogonal projection view (XZ plane).
        /// The view component itself is never <c>null</c>, but <see cref="MxView.MatrixData"/>
        /// is <c>null</c> when orthogonal mode is inactive or the current data is single-frame.
        /// <see cref="MxView.IsVisible"/> reflects whether the pane is currently shown.
        /// <para>
        /// <b>Do not set <see cref="MxView.MatrixData"/> directly</b> — this view's data
        /// is managed by the internal <c>OrthogonalViewController</c>.
        /// </para>
        /// </summary>
        public MxView BottomView => _orthoPanel.BottomView;

        /// <summary>
        /// The right orthogonal projection view (YZ plane).
        /// The view component itself is never <c>null</c>, but <see cref="MxView.MatrixData"/>
        /// is <c>null</c> when orthogonal mode is inactive or the current data is single-frame.
        /// <see cref="MxView.IsVisible"/> reflects whether the pane is currently shown.
        /// <para>
        /// <b>Do not set <see cref="MxView.MatrixData"/> directly</b> — this view's data
        /// is managed by the internal <c>OrthogonalViewController</c>.
        /// </para>
        /// </summary>
        public MxView RightView => _orthoPanel.RightView;

        /// <summary>
        /// Raised on the UI thread whenever the plotter’s displayed content changes —
        /// both when <see cref="Refresh"/> is called explicitly and when
        /// <see cref="SetMatrixData"/> replaces the underlying data.
        /// Used by <see cref="LinkRefresh"/> to propagate refresh across linked plotters
        /// that share underlying <c>T[]</c> frame data, and by filter sync windows
        /// to trigger downstream re-computation.
        /// </summary>
        public event EventHandler? Refreshed;

        /// <summary>
        /// Fired on the UI thread whenever the display bitmap is updated —
        /// covers LUT changes, frame navigation, value-range changes, and explicit <see cref="Refresh"/> calls.
        /// Subscribe to this (rather than <see cref="Refreshed"/>) for thumbnail or preview updates.
        /// </summary>
        public event EventHandler? ViewUpdated
        {
            add => _view.BitmapRefreshed += value;
            remove => _view.BitmapRefreshed -= value;
        }

        /// <summary>
        /// Raised on the UI thread when the displayed <see cref="IMatrixData"/> is replaced
        /// (data swap, format conversion, action result, Virtual→InMemory, etc.).
        /// The event argument is the new data instance (may be <c>null</c>).
        /// </summary>
        public event EventHandler<IMatrixData?>? MatrixDataChanged;

        /// <summary>
        /// Descriptor for an export format shown under "Export as…" in the File menu.
        /// Built-in formats (PNG) are always present; additional formats are injected by the host app.
        /// </summary>
        /// <param name="Label">Menu item label, e.g. <c>"AVI\u2026"</c>.</param>
        /// <param name="Hint">Tooltip text.</param>
        /// <param name="FileTypeName">Save-dialog file type name, e.g. <c>"AVI Video"</c>.</param>
        /// <param name="FilePattern">Save-dialog glob pattern, e.g. <c>"*.avi"</c>.</param>
        /// <param name="Exporter">
        /// Async delegate that writes the file.
        /// Parameters: <c>(outputPath, frameRenderer, frameCount, width, height, fps)</c>.<br/>
        /// <c>frameRenderer(frameIndex)</c> must be called on the UI thread and returns a
        /// PNG-encoded <c>byte[]</c> for that frame (suitable for decoding with any standard
        /// image library; width and height are passed separately for convenience).
        /// </param>
        /// <param name="RequiresStack">
        /// When <c>true</c> the menu item is shown only for stack data (<see cref="IMatrixData.FrameCount"/> &gt; 1).
        /// </param>
        /// <param name="DefaultFps">Default frames-per-second passed to the exporter.</param>
        public sealed record ExportFormatDescriptor(
            string Label,
            string Hint,
            string FileTypeName,
            string FilePattern,
            Func<string, Func<int, byte[]>, int, int, int, int, Task> Exporter,
            bool RequiresStack = true,
            int DefaultFps = 10);

        /// <summary>
        /// Additional export formats injected by the host application and shown under
        /// "Export as…" in the File menu alongside the built-in PNG entry.
        /// Populate this list before the window is shown (or any time before the menu panel
        /// is first opened — the panel is rebuilt lazily on first open).
        /// </summary>
        public List<ExportFormatDescriptor> ExportFormats { get; } = [];

        /// <summary>
        /// Whether the current state differs from what was last opened or saved.
        /// </summary>
        public bool IsModified => _dirty != DirtyFlags.None;


        /// <summary>
        /// Raised on the UI thread when <see cref="IsModified"/> changes.
        /// </summary>
        public event EventHandler? IsModifiedChanged;

        /// <summary>
        /// Whether closing this window should prompt a “Save changes?” dialog.
        /// <c>true</c> when a source path is set and data has been modified since the last open/save.
        /// </summary>
        public bool ShouldConfirmClose => SourcePath != null && IsModified;

        /// <summary>
        /// When <c>true</c>, the close-confirmation dialog is skipped even if
        /// <see cref="ShouldConfirmClose"/> is <c>true</c>.
        /// Set this before triggering a programmatic close (e.g. app-level exit guard)
        /// to prevent double-prompting when the caller already handled confirmation.
        /// </summary>
        public bool SuppressCloseConfirmation { get; set; }

        /// <summary>
        /// Whether this window is backed by an actual file
        /// (as opposed to an in-memory or <see cref="Views.MatrixDataSource"/> sentinel source).
        /// </summary>
        public bool HasFile => SourcePath != null && !SourcePath.StartsWith(':');

        /// <summary>
        /// Clears all dirty flags without showing any dialog, as if the current state were
        /// freshly loaded or saved. Use this when the caller has already handled confirmation
        /// (e.g. app-level "Discard All" in a multi-window exit guard).
        /// <para>
        /// Also hides the LUT/VR and Scale revert buttons so the UI reflects the clean state.
        /// </para>
        /// </summary>
        public void DiscardChanges()
        {
            ClearAllDirty();
            if (_lutVrRevertBtn != null) _lutVrRevertBtn.IsVisible = false;
            UpdateScaleRevertButton();
        }

        private void SetDirty(DirtyFlags flags, bool dirty)
        {
            if (_suppressModified || _initializingData) return;
            var before = _dirty;
            _dirty = dirty ? (_dirty | flags) : (_dirty & ~flags);
            if (_dirty != before)
            {
                Debug.WriteLine($"[MatrixPlotter.SetDirty] {flags} → {dirty}  _dirty={_dirty}  Title={Title}");
                IsModifiedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void ClearAllDirty()
        {
            var before = _dirty;
            _dirty = DirtyFlags.None;
            if (_dirty != before)
                IsModifiedChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateDirtyBadge()
        {
            bool show = SourcePath != null && IsModified;
            _dirtyBadge.IsVisible = show;
            if (!show) return;
            var parts = new System.Text.StringBuilder("Unsaved changes:");
            if (_dirty.HasFlag(DirtyFlags.Lut))     parts.Append("\n  \u2022 LUT / Color");
            if (_dirty.HasFlag(DirtyFlags.Vr))      parts.Append("\n  \u2022 Value Range");
            if (_dirty.HasFlag(DirtyFlags.Scale))   parts.Append("\n  \u2022 Scale / Axes");
            if (_dirty.HasFlag(DirtyFlags.Overlay)) parts.Append("\n  \u2022 Overlays");
            if (_dirty.HasFlag(DirtyFlags.Data))    parts.Append("\n  \u2022 Data / Metadata");
            ToolTip.SetTip(_dirtyBadge, parts.ToString());
        }

        private void CaptureLutVrSnapshot()
        {
            if (_currentData == null)
            {
                _lutVrSnapshot = null;
                return;
            }
            _lutVrSnapshot = new LutVrSnapshot(
                LutName: _view.Lut?.Name ?? "",
                LutLevel: _view.LutDepth,
                Inverted: _view.IsInvertedColor,
                VrMode: _rangeBar.Mode,
                VrMin: _rangeBar.Mode == ValueRangeMode.Fixed ? _view.FixedMin : double.NaN,
                VrMax: _rangeBar.Mode == ValueRangeMode.Fixed ? _view.FixedMax : double.NaN);
        }

        private void CaptureScaleSnapshot(IMatrixData? data)
        {
            if (data == null)
            {
                _scaleSnapshot = null;
                return;
            }
            var axes = data.Axes;
            var axSnaps = new AxisSnapshot[axes.Count];
            for (int i = 0; i < axes.Count; i++)
                axSnaps[i] = new AxisSnapshot(axes[i].Min, axes[i].Max, axes[i].Unit ?? "", axes[i].Name ?? "");
            _scaleSnapshot = new ScaleSnapshot(
                data.XMin, data.XMax, data.XUnit ?? "",
                data.YMin, data.YMax, data.YUnit ?? "",
                axSnaps);
        }

        /// <summary>Marks LUT properties (name, level, invert) dirty/clean and updates the revert button.
        /// Checks full LUT consistency against the snapshot so partial restores are handled correctly.</summary>
        private void SetLutDirty(bool dirty)
        {
            SetDirty(DirtyFlags.Lut, dirty);
            UpdateLutVrRevertButton();
        }

        /// <summary>Marks VR properties (mode, fixed min/max) dirty/clean and updates the revert button.
        /// Checks full VR consistency against the snapshot so partial restores are handled correctly.</summary>
        private void SetVrDirty(bool dirty)
        {
            SetDirty(DirtyFlags.Vr, dirty);
            UpdateLutVrRevertButton();
        }

        private void UpdateLutVrRevertButton()
        {
            if (_suppressModified || _initializingData) return;
            if (_lutVrRevertBtn != null)
                _lutVrRevertBtn.IsVisible = (_dirty & (DirtyFlags.Lut | DirtyFlags.Vr)) != 0 && _lutVrSnapshot != null;
        }

        /// <summary>Shows or hides the Scale revert button in the Scale tab. Refreshes the tab to add/remove it.</summary>
        internal void SetScaleDirty(bool dirty)
        {
            SetDirty(DirtyFlags.Scale, dirty);
            if (!_suppressModified && !_initializingData)
                UpdateScaleRevertButton();
        }

        private void UpdateScaleRevertButton()
        {
            if (_scaleTabBody == null) return;

            // Remove any existing revert button first
            if (_scaleRevertBtn != null && _scaleTabBody.Children.Contains(_scaleRevertBtn))
                _scaleTabBody.Children.Remove(_scaleRevertBtn);

            if (!_dirty.HasFlag(DirtyFlags.Scale) || _scaleSnapshot == null) return;

            _scaleRevertBtn = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Children =
                    {
                        new PathIcon
                        {
                            Data = MenuIcons.Undo,
                            Width = 12,
                            Height = 12,
                            Foreground = MenuIcons.DefaultBrush(MenuIcons.Undo),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = "Revert Scale Settings",
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                },
                Height = 22,
                Padding = new Thickness(6, 1),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(4, 6, 4, 4),
            };
            ToolTip.SetTip(_scaleRevertBtn, "Revert Scale/Axis settings to the state when data was loaded or last saved");
            _scaleRevertBtn.Click += (_, _) => RevertScale();
            _scaleTabBody.Children.Add(_scaleRevertBtn);
        }

        private void RevertLutVr()
        {
            var snap = _lutVrSnapshot;
            if (snap == null || _currentData == null) return;

            _suppressModified = true;
            try
            {
                // LUT
                if (!string.IsNullOrEmpty(snap.LutName))
                {
                    try
                    {
                        var lut = ColorThemes.Get(snap.LutName);
                        _view.Lut = lut;
                        _lutSelector.SelectLut(lut);
                        Icon = _lutSelector.SelectedIcon;
                        if (DataContext is ViewModels.MatrixPlotterViewModel vm) vm.Lut = lut;
                    }
                    catch { }
                }
                if (_view.LutDepth != snap.LutLevel)
                {
                    _view.LutDepth = snap.LutLevel;
                    if (_levelNud != null) _levelNud.Value = snap.LutLevel;
                }
                if (_view.IsInvertedColor != snap.Inverted)
                {
                    _view.IsInvertedColor = snap.Inverted;
                    if (_invertLutChk != null) _invertLutChk.IsChecked = snap.Inverted;
                }

                // VR
                if (snap.VrMode == ValueRangeMode.Fixed && !double.IsNaN(snap.VrMin))
                {
                    _view.IsFixedRange = true;
                    _rangeBar.SetMode(true);
                    _rangeBar.SetRange(snap.VrMin, snap.VrMax);
                    // Re-assign after SetMode/SetRange to override any ModeChanged side-effects.
                    _view.FixedMin = snap.VrMin;
                    _view.FixedMax = snap.VrMax;
                    _suppressModeSync = true;
                    if (_autoRadio != null) _autoRadio.IsChecked = false;
                    if (_fixedRadio != null) _fixedRadio.IsChecked = true;
                    _suppressModeSync = false;
                }
                else
                {
                    _rangeBar.SetMode(snap.VrMode);
                }
                        _orthoController.SyncRenderSettings();
                        SaveViewSettings();
                    }
                    finally { _suppressModified = false; }

                    SetDirty(DirtyFlags.Lut, false);
                    SetDirty(DirtyFlags.Vr, false);
                    if (_lutVrRevertBtn != null) _lutVrRevertBtn.IsVisible = false;
                }

        private void RevertScale()
        {
            var snap = _scaleSnapshot;
            if (snap == null || _currentData == null) return;

            _suppressModified = true;
            try
            {
                _currentData.XMin = snap.XMin;
                _currentData.XMax = snap.XMax;
                _currentData.XUnit = snap.XUnit;
                _currentData.YMin = snap.YMin;
                _currentData.YMax = snap.YMax;
                _currentData.YUnit = snap.YUnit;

                var axes = _currentData.Axes;
                for (int i = 0; i < snap.Axes.Length && i < axes.Count; i++)
                {
                    axes[i].Min = snap.Axes[i].Min;
                    axes[i].Max = snap.Axes[i].Max;
                    axes[i].Unit = snap.Axes[i].Unit;
                    axes[i].Name = snap.Axes[i].Name;
                }

                if (_view.IsFitToView) _view.FitToView(); else _view.InvalidateSurface();
                _orthoController.RefreshCrosshairAndSlices();
                        SaveViewSettings();
                        RefreshInfoTab(); // rebuilds grid with restored values
                    }
                    finally { _suppressModified = false; }

                    SetDirty(DirtyFlags.Scale, false);
                    UpdateScaleRevertButton();
                }

        /// <summary>
        /// Creates a thumbnail-sized snapshot of the currently rendered view.
        /// Applies <see cref="Controls.ViewTransform"/> and aspect correction.
        /// Returns <c>null</c> when no data is loaded yet.
        /// Must be called on the UI thread.
        /// </summary>
        public Bitmap? CaptureThumbnail(int maxSize = 64)
        {
            var (natW, natH) = _view.GetNaturalDims();
            if (natW <= 0 || natH <= 0) return null;

            double scale = Math.Min((double)maxSize / natW, (double)maxSize / natH);
            scale = Math.Min(scale, 1.0); // don't upscale
            int dstW = Math.Max(1, (int)(natW * scale));
            int dstH = Math.Max(1, (int)(natH * scale));

            return _view.RenderToBitmap(dstW, dstH);
        }

        /// <summary>
        /// Exports the current view to <paramref name="filePath"/> as a full-resolution PNG.
        /// No-op when no data is loaded. Must be called on the UI thread.
        /// </summary>
        public void ExportAsPng(string filePath)
        {
            var (natW, natH) = _view.GetNaturalDims();
            if (natW <= 0 || natH <= 0) return;
            int w = Math.Max(1, (int)Math.Round(natW));
            int h = Math.Max(1, (int)Math.Round(natH));
            _view.SaveAsPng(filePath, w, h);
        }

        /// <summary>
        /// Rebuilds the display bitmap from the current <see cref="MatrixData"/> pixel values
        /// and redraws all views. Safe to call from any thread.
        /// Fires <see cref="Refreshed"/> after completion (with re-entrancy guard to prevent
        /// infinite loops in bidirectional link scenarios).
        /// </summary>
        public void Refresh()
        {
            if (_isRefreshing) return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                _isRefreshing = true;
                try
                {
                    _view.Refresh();
                    _orthoPanel.RightView.Refresh();
                    _orthoPanel.BottomView.Refresh();
                    RefreshAllOverlayAnalysis();
                    Refreshed?.Invoke(this, EventArgs.Empty);
                }
                finally { _isRefreshing = false; }
            }
            else
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_isRefreshing) return;
                    _isRefreshing = true;
                    try
                    {
                        _view.Refresh();
                        _orthoPanel.RightView.Refresh();
                        _orthoPanel.BottomView.Refresh();
                        RefreshAllOverlayAnalysis();
                        Refreshed?.Invoke(this, EventArgs.Empty);
                    }
                    finally { _isRefreshing = false; }
                });
            }
        }

        /// <summary>
        /// Establishes a bidirectional refresh link: when either plotter calls <see cref="Refresh"/>,
        /// the other is automatically refreshed. The <paramref name="child"/> is also closed when
        /// this (parent) plotter is closed, mirroring the XY-projection ownership model.
        /// </summary>
        /// <remarks>
        /// Typical usage: a Tool action creates a shallow <c>Reorder</c> (substack) and opens it
        /// in a linked window. Because both plotters share <c>T[]</c> frame data, a refresh on
        /// one must propagate to the other so that <c>ValueRange</c> invalidation is reflected.
        /// </remarks>
        public void LinkRefresh(MatrixPlotter child)
        {
            if (child == this || _linkedChildren.Contains(child)) return;

            _linkedChildren.Add(child);

            // Bidirectional refresh
            child.Refreshed += OnLinkedChildRefreshed;
            Refreshed += child.OnLinkedParentRefreshed;

            // Auto-unlink when child closes (user closes child independently)
            child.Closed += OnLinkedChildClosed;
        }

        /// <summary>
        /// Removes a previously established refresh link. Safe to call if not linked.
        /// </summary>
        public void UnlinkRefresh(MatrixPlotter child)
        {
            if (!_linkedChildren.Remove(child)) return;

            child.Refreshed -= OnLinkedChildRefreshed;
            Refreshed -= child.OnLinkedParentRefreshed;
            child.Closed -= OnLinkedChildClosed;
        }

        /// <summary>
        /// Creates a <see cref="MatrixPlotter"/> for <paramref name="data"/> and links it to this
        /// plotter via <see cref="LinkRefresh"/>. The caller is responsible for calling
        /// <see cref="Window.Show"/>.
        /// </summary>
        public MatrixPlotter CreateLinked(
            IMatrixData data,
            LookupTable? lut = null,
            string? title = null)
        {
            var child = Create(data, lut ?? _view.Lut, title);
            LinkRefresh(child);
            return child;
        }

        private void OnLinkedChildRefreshed(object? sender, EventArgs e) => Refresh();
        private void OnLinkedParentRefreshed(object? sender, EventArgs e) => Refresh();

        private void OnLinkedChildClosed(object? sender, EventArgs e)
        {
            if (sender is MatrixPlotter child)
                UnlinkRefresh(child);
        }

        // ── Constructor ───────────────────────────────────────────────────────

        /// <summary>
        /// Initializes a <see cref="MatrixPlotter"/> with no data loaded.
        /// <para>
        /// This constructor is the MVVM-friendly entry point: set <see cref="Window.DataContext"/>
        /// to a <see cref="MatrixPlotterViewModel"/> instance to load data and configure the view.
        /// </para>
        /// <para>
        /// For most use cases, prefer the static <see cref="Create"/> factory method, which
        /// constructs the ViewModel and registers the window with <see cref="PlotWindowNotifier"/>
        /// in one call. Use this constructor directly when:
        /// <list type="bullet">
        ///   <item>You manage the <see cref="MatrixPlotterViewModel"/> lifecycle externally
        ///         (e.g. DI container, XAML binding).</item>
        ///   <item>You are embedding <see cref="MatrixPlotter"/> in a custom application shell
        ///         that handles window registration independently.</item>
        ///   <item>You want to show the window before data is available and set
        ///         <see cref="Window.DataContext"/> later.</item>
        /// </list>
        /// </para>
        /// </summary>
        /// <example>
        /// <code>
        /// // MVVM / DI pattern
        /// var plotter = new MatrixPlotter { DataContext = myViewModel };
        /// plotter.Show();
        /// </code>
        /// </example>
        public MatrixPlotter()
        {
            Width = 420;
            Height = 480;

            _orthoPanel = new OrthogonalPanel();
            _view = _orthoPanel.MainView;
            _view.EnableBuiltInContextMenu = true;
            _view.MatrixDataChanged += (_, data) =>
            {
                if (_settingMatrixData) return; // already inside SetMatrixData — skip
                SetMatrixData(data);
            };
            _view.OverlayManager.ObjectAdded += OnOverlayObjectAdded;
            _view.OverlayManager.ObjectRemoved += OnOverlayObjectRemoved;
            _view.OverlayManager.GhostUpdated += (_, g) => _view.OverlayInfoText = g.GetInfo(_view.MatrixData);
            _view.OverlayManager.GhostCancelled += (_, _) => _view.OverlayInfoText = null;
            _orthoPanel.BottomView.EnableBuiltInContextMenu = true;
            _orthoPanel.BottomView.OverlayManager.ObjectAdded += OnOverlayObjectAdded;
            _orthoPanel.BottomView.OverlayManager.ObjectRemoved += OnOverlayObjectRemoved;
            _orthoPanel.BottomView.OverlayManager.GhostUpdated += (_, g) => _orthoPanel.BottomView.OverlayInfoText = g.GetInfo(_orthoPanel.BottomView.MatrixData);
            _orthoPanel.BottomView.OverlayManager.GhostCancelled += (_, _) => _orthoPanel.BottomView.OverlayInfoText = null;
            _orthoPanel.RightView.EnableBuiltInContextMenu = true;
            _orthoPanel.RightView.OverlayManager.ObjectAdded += OnOverlayObjectAdded;
            _orthoPanel.RightView.OverlayManager.ObjectRemoved += OnOverlayObjectRemoved;
            _orthoPanel.RightView.OverlayManager.GhostUpdated += (_, g) => _orthoPanel.RightView.OverlayInfoText = g.GetInfo(_orthoPanel.RightView.MatrixData);
            _orthoPanel.RightView.OverlayManager.GhostCancelled += (_, _) => _orthoPanel.RightView.OverlayInfoText = null;
            _view.CopiedToClipboard += (_, msg) => ShowToast(msg);
            _view.CropRequested += (_, _) => InvokeCropAction();
            _view.ExtractFrameRequested += (_, _) => InvokeExtractFrame(_view);
            _orthoPanel.BottomView.CopiedToClipboard += (_, msg) => ShowToast(msg);
            _orthoPanel.BottomView.CropRequested += (_, _) => InvokeCropAction();
            _orthoPanel.BottomView.ExtractFrameRequested += (_, _) => InvokeExtractFrame(_orthoPanel.BottomView);
            _orthoPanel.RightView.CopiedToClipboard += (_, msg) => ShowToast(msg);
            _orthoPanel.RightView.CropRequested += (_, _) => InvokeCropAction();
            _orthoPanel.RightView.ExtractFrameRequested += (_, _) => InvokeExtractFrame(_orthoPanel.RightView);
            _orthoController = new OrthogonalViewController(_orthoPanel);
            _orthoController.XYProjectionChanged += OnXYProjectionChanged;
            _lutSelector = new LutSelector
            {
                CompactMode = true,
                ComboWidth = 97,
            };

            _trackerPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                IsVisible = false,
                Margin = new Thickness(0, 1, 0, 1)
            };

            _lutSelector.SelectedLutChanged += (_, lut) =>
            {
                if (lut == null) return;
                _view.Lut = lut;
                _orthoController.SyncRenderSettings();
                if (DataContext is MatrixPlotterViewModel vm)
                    vm.Lut = lut;
                // Update window icon to match selected LUT gradient
                Icon = _lutSelector.SelectedIcon;
                SaveViewSettings();
                // If the selected LUT matches the snapshot, the state is clean — not dirty.
                // This also suppresses Avalonia's deferred SelectionChanged re-fire on
                // TemplateApplied (when the window is first shown), which would otherwise
                // produce a false-positive revert button for non-default LUTs like BSMod.
                // LUT name changed: check full LUT consistency (name + level + invert) against snapshot.
                bool lutMatchesSnapshot = _lutVrSnapshot != null
                    && string.Equals(lut.Name, _lutVrSnapshot.LutName, StringComparison.OrdinalIgnoreCase)
                    && _view.LutDepth == _lutVrSnapshot.LutLevel
                    && _view.IsInvertedColor == _lutVrSnapshot.Inverted;
                SetLutDirty(!lutMatchesSnapshot);
                if (!_syncApplying) SyncLutChanged?.Invoke(this, lut);
            };

            _dirtyBadge = new TextBlock
            {
                Text = "*",
                FontSize = 14,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 160, 60)),
                Margin = new Thickness(4, 0, 0, 0),
                IsVisible = false,
            };

            _infoText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
            };
            _virtualBadge = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DodgerBlue,
                Cursor = new Cursor(StandardCursorType.Hand),
                Margin = new Thickness(2, 0),
                IsVisible = false,
            };
            _virtualBadge.PointerPressed += (_, _) => OpenOrActivateCacheMonitor();
            _zoomText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            _zoomText.PointerPressed += async (_, e) =>
            {
                e.Handled = true;
                var dlg = new ZoomDialog(_view.Zoom);
                await dlg.ShowDialog(this);
                if (dlg.Result.HasValue)
                    _view.SetZoom(dlg.Result.Value);
            };
            _progressSep = new TextBlock
            {
                Text = "|",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                Margin = new Thickness(2, 0),
                IsVisible = false,
            };
            _noticeText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 2, 0),
                Opacity = 0.7,
                IsVisible = false,
            };
            _progressText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                IsVisible = false,
            };
            _progressBar = new ProgressBar
            {
                Width = 100,
                Height = 4,
                MinHeight = 0,
                Minimum = 0,
                Maximum = 100,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                IsVisible = false,
            };

            var statusPanel = new StackPanel { Orientation = Orientation.Horizontal };
            statusPanel.Children.Add(_dirtyBadge);
            statusPanel.Children.Add(_infoText);
            statusPanel.Children.Add(_virtualBadge);
            statusPanel.Children.Add(_zoomText);
            statusPanel.Children.Add(_noticeText);
            statusPanel.Children.Add(_progressSep);
            statusPanel.Children.Add(_progressBar);
            statusPanel.Children.Add(_progressText);

            var statusBar = new Border
            {
                Child = statusPanel,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 2),
            };

            _view.ScrollStateChanged += (_, _) => UpdateStatusBar();

            // ── Value range bar ────────────────────────────────────────────
            _rangeBar = new ValueRangeBar();
            _settingsBtn = new Button
            {
                Content = "▾",
                Width = 22,
                Height = 20,
                MinHeight = 20,
                FontSize = 18,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                Margin = new Thickness(4, 0),
            };

            // ── Inline settings panel (toggled by ? button) ──────────────────────
            _levelNud = new NumericUpDown
            {
                Minimum = 2,
                Maximum = 4096,
                Value = 256,
                Increment = 1,
                Width = 60,
                Height = 20,
                MinHeight = 0,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(4, 0),
            };
            _levelNud.Classes.Add("compact");
            _invertLutChk = new ToggleButton
            {
                // ◑ half-filled circle: circle outline (Stroke) + right semicircle (Fill).
                // PathIcon supports only Fill, so two overlaid Path elements are used.
                Content = new PathIcon
                {
                    Data = Geometry.Parse(
                        "F1 " + // FillRule = NonZero
                        "M 8,1 A 7,7 0 0,1 8,15 A 7,7 0 0,1 8,1 Z " + // 外側の完全な円 (時計回り)
                        "M 8,2 A 6,6 0 0,0 8,14 L 8,2 Z"),            // 内側の左半円をくり抜く (反時計回り)
                    Width = 14,
                    Height = 14,
                },
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                Margin = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(5, 0),
            };
            ToolTip.SetTip(_invertLutChk, "Invert LUT");

            _fixedRadio = new RadioButton
            {
                Content = "Fixed",
                GroupName = "VRMode",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
            };
            _fixedRadio.Classes.Add("compact");
            ToolTip.SetTip(_fixedRadio, "Fixed: user-specified numeric min/max");
            _autoRadio = new RadioButton
            {
                Content = "Auto",
                GroupName = "VRMode",
                IsChecked = true,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
            };
            _autoRadio.Classes.Add("compact");
            ToolTip.SetTip(_autoRadio, "Current frame min/max (automatic)");

            // ⚠ warning icon: filled triangle (CW) with ! cutouts (CCW) — NonZero winding cancels fill inside !
            _allWarningIcon = new PathIcon
            {
                Data = Geometry.Parse(
                    "M 7,0 L 14,12 L 0,12 Z " +
                    "M 6.3,3.5 L 6.3,8 L 7.7,8 L 7.7,3.5 Z " +
                    "M 6.3,9.5 L 6.3,11 L 7.7,11 L 7.7,9.5 Z"),
                Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 0)),
                Width = 10,
                Height = 10,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            var allLabel = new StackPanel { Orientation = Orientation.Horizontal };
            allLabel.Children.Add(new TextBlock
            {
                Text = "All",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            });
            allLabel.Children.Add(_allWarningIcon);
            _allRadio = new RadioButton
            {
                Content = allLabel,
                GroupName = "VRMode",
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                IsVisible = false, // shown only in multi-frame mode
            };
            _allRadio.Classes.Add("compact");
            ToolTip.SetTip(_allRadio, "Global min/max across all frames");

            _roiRadio = new RadioButton
            {
                Content = "ROI",
                GroupName = "VRMode",
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 0,
                Height = 20,
                IsVisible = false, // shown only when an ROI overlay is designated
            };
            _roiRadio.Classes.Add("compact");
            ToolTip.SetTip(_roiRadio, "Value range from designated ROI overlay");


            var settingsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(30, 2, 10, 2),
            };

            settingsRow.Children.Add(new TextBlock { Text = "Level:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            settingsRow.Children.Add(_levelNud);
            //settingsRow.Children.Add(new Border { Width = 1, Background = Brushes.Gray, Margin = new Thickness(5, 3) });
            settingsRow.Children.Add(_invertLutChk);
            settingsRow.Children.Add(new Border { Width = 1, Background = Brushes.Gray, Margin = new Thickness(5, 3) });
            settingsRow.Children.Add(new TextBlock { Text = "Value range:", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            settingsRow.Children.Add(_fixedRadio);
            settingsRow.Children.Add(_autoRadio);
            settingsRow.Children.Add(_allRadio);
            settingsRow.Children.Add(_roiRadio);

            _settingsPanel = new Border
            {
                Child = settingsRow,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(0, 0, 0, 1),
                IsVisible = false,
            };

            // ── Wire settings events ──────────────────────────────────────────────
            _settingsBtn.Click += (_, _) =>
            {
                bool opening = !_settingsPanel.IsVisible;
                double panelH = _settingsPanel.Bounds.Height; // capture before toggle (valid only while visible)

                _settingsPanel.IsVisible = opening;
                _settingsBtn.Content = opening ? "▴" : "▾";
                _settingsBtn.Background = opening ? Brushes.LightGray : Brushes.Transparent;

                if (WindowState == WindowState.Maximized) return;

                if (!opening && panelH > 0)
                {
                    // Closing: shrink immediately ? Bounds is still valid before hide
                    Height -= panelH;
                }
                else if (opening)
                {
                    // Opening: defer until after layout so Bounds is populated
                    Dispatcher.UIThread.Post(() =>
                    {
                        double h = _settingsPanel.Bounds.Height;
                        if (h > 0) Height += h;
                    }, DispatcherPriority.Background);
                }
            };

            _levelNud.ValueChanged += (_, _) =>
            {
                _view.LutDepth = (int)(_levelNud.Value ?? 256);
                _orthoController.SyncRenderSettings();
                SaveViewSettings();
                bool lutMatchesSnapshot = _lutVrSnapshot != null
                    && _view.LutDepth == _lutVrSnapshot.LutLevel
                    && string.Equals(_view.Lut?.Name, _lutVrSnapshot.LutName, StringComparison.OrdinalIgnoreCase)
                    && _view.IsInvertedColor == _lutVrSnapshot.Inverted;
                SetLutDirty(!lutMatchesSnapshot);
                if (!_syncApplying) SyncLutDepthChanged?.Invoke(this, _view.LutDepth);
            };

            _invertLutChk.IsCheckedChanged += (_, _) =>
            {
                _view.IsInvertedColor = _invertLutChk.IsChecked == true;
                _orthoController.SyncRenderSettings();
                SaveViewSettings();
                bool lutMatchesSnapshot = _lutVrSnapshot != null
                    && _view.IsInvertedColor == _lutVrSnapshot.Inverted
                    && string.Equals(_view.Lut?.Name, _lutVrSnapshot.LutName, StringComparison.OrdinalIgnoreCase)
                    && _view.LutDepth == _lutVrSnapshot.LutLevel;
                SetLutDirty(!lutMatchesSnapshot);
                if (!_syncApplying) SyncInvertedChanged?.Invoke(this, _view.IsInvertedColor);
            };

            _autoRadio.IsCheckedChanged += (_, _) =>
            {
                if (_suppressModeSync || _autoRadio.IsChecked != true) return;
                _suppressModeSync = true;
                _rangeBar.SetMode(ValueRangeMode.Current);
                _suppressModeSync = false;
            };
            _fixedRadio.IsCheckedChanged += (_, _) =>
            {
                if (_suppressModeSync || _fixedRadio.IsChecked != true) return;
                _suppressModeSync = true;
                _rangeBar.SetMode(ValueRangeMode.Fixed);
                _suppressModeSync = false;
            };
            _allRadio.IsCheckedChanged += (_, _) =>
            {
                if (_suppressModeSync || _allRadio.IsChecked != true) return;
                _suppressModeSync = true;
                _rangeBar.SetMode(ValueRangeMode.All);
                _suppressModeSync = false;
            };
            _roiRadio.IsCheckedChanged += (_, _) =>
            {
                if (_suppressModeSync || _roiRadio.IsChecked != true) return;
                _suppressModeSync = true;
                _rangeBar.SetMode(ValueRangeMode.Roi);
                _suppressModeSync = false;
            };

            // ── Wire range bar ⇔ view events ────────────────────────────────────
            _view.AutoRangeComputed += (_, args) =>
            {
                if (_rangeBar.Mode == ValueRangeMode.Current)
                    _rangeBar.SetRange(args.Min, args.Max);
            };

            _rangeBar.ModeChanged += (_, mode) =>
            {
                _view.IsFixedRange = mode != ValueRangeMode.Current;
                if (mode == ValueRangeMode.Fixed)
                {
                    // Use whatever the bar currently shows — works for both Current→Fixed and All→Fixed.
                    // Falls back to a live scan only if the bar has never received a range yet.
                    if (!double.IsNaN(_rangeBar.DisplayedMinValue))
                    {
                        _view.FixedMin = _rangeBar.DisplayedMinValue;
                        _view.FixedMax = _rangeBar.DisplayedMaxValue;
                    }
                    else
                    {
                        var (min, max) = _view.ScanCurrentFrameRange();
                        _rangeBar.SetRange(min, max);
                        _view.FixedMin = min;
                        _view.FixedMax = max;
                    }
                }
                else if (mode == ValueRangeMode.All)
                {
                    ApplyAllModeRange();
                }
                else if (mode == ValueRangeMode.Roi)
                {
                    // Range is applied by RefreshRoiValueRange(); just sync view state here.
                    _view.IsFixedRange = true;
                    RefreshRoiValueRange();
                }
                else // Current
                {
                    var (min, max) = _view.ScanCurrentFrameRange();
                    _rangeBar.SetRange(min, max);
                }
                if (!_suppressModeSync)
                {
                    _suppressModeSync = true;
                    if (_autoRadio != null) _autoRadio.IsChecked = mode == ValueRangeMode.Current;
                    if (_fixedRadio != null) _fixedRadio.IsChecked = mode == ValueRangeMode.Fixed;
                    if (_allRadio != null) _allRadio.IsChecked = mode == ValueRangeMode.All;
                    if (_roiRadio != null) _roiRadio.IsChecked = mode == ValueRangeMode.Roi;
                    _suppressModeSync = false;
                }
                _orthoController.SyncRenderSettings();
                SaveViewSettings();
                bool vrMatchesSnapshot = _lutVrSnapshot != null
                    && mode == _lutVrSnapshot.VrMode
                    && (mode != ValueRangeMode.Fixed
                        || (_view.FixedMin == _lutVrSnapshot.VrMin && _view.FixedMax == _lutVrSnapshot.VrMax));
                SetVrDirty(!vrMatchesSnapshot);
                if (!_syncApplying) SyncRangeModeChanged?.Invoke(this, mode);
                // When switching to Fixed the min/max are resolved inside the Fixed branch
                // above but RangeChanged is never fired (SetRange suppresses it via _updating).
                // Explicitly propagate the resolved values so sync targets receive them.
                if (!_syncApplying && mode == ValueRangeMode.Fixed)
                    SyncFixedRangeChanged?.Invoke(this, (_view.FixedMin, _view.FixedMax));
            };

            _rangeBar.RangeChanged += (_, args) =>
            {
                _view.FixedMin = args.Min;
                _view.FixedMax = args.Max;
                _orthoController.SyncRenderSettings();
                SaveViewSettings();
                bool vrMatchesSnapshot = _lutVrSnapshot != null
                    && _lutVrSnapshot.VrMode == ValueRangeMode.Fixed
                    && _view.FixedMin == _lutVrSnapshot.VrMin
                    && _view.FixedMax == _lutVrSnapshot.VrMax;
                SetVrDirty(!vrMatchesSnapshot);
                if (!_syncApplying) SyncFixedRangeChanged?.Invoke(this, (args.Min, args.Max));
            };

            _rangeBar.SearchMinRequested += (_, _) =>
            {
                var (min, _) = _view.ScanCurrentFrameRange();
                _view.FixedMin = min;
                _rangeBar.SetRange(min, _view.FixedMax);
                _orthoController.SyncRenderSettings();
            };

            _rangeBar.SearchMaxRequested += (_, _) =>
            {
                var (_, max) = _view.ScanCurrentFrameRange();
                _view.FixedMax = max;
                _rangeBar.SetRange(_view.FixedMin, max);
                _orthoController.SyncRenderSettings();
            };

            // Layout: [☰][LutSelector][ValueRangeBar ・・・・・・・・・・・][▾] on one compact top row
            _hamburgerBtn = new Button
            {
                Content = "☰",
                Width = 26,
                Height = 20,
                FontSize = 14,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(2, 0, 0, 0),
            };
            // Wire window-level light-dismiss once: closes the menu when the user clicks
            // anywhere inside MatrixPlotter that is outside the overlay panel.
            AddHandler(InputElement.PointerPressedEvent,
                       OnMenuLightDismiss, RoutingStrategies.Bubble, handledEventsToo: true);

            _hamburgerBtn.Click += (_, _) =>
            {
                if (_menuPanel == null)
                    _menuPanel = BuildMenuPanel();
                if (_menuPanel.IsVisible) HideMenuPanel();
                else ShowMenuPanel();
            };

            _lutVrRevertBtn = new Button
            {
                Content = new PathIcon
                {
                    Data = MenuIcons.Undo,
                    Width = 12,
                    Height = 12,
                    Foreground = MenuIcons.DefaultBrush(MenuIcons.Undo),
                },
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1, 0),
                IsVisible = false,
            };
            ToolTip.SetTip(_lutVrRevertBtn, "Revert LUT / Value Range to initial settings");
            _lutVrRevertBtn.Click += (_, _) => RevertLutVr();

            var topRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_settingsBtn, Dock.Right);
            DockPanel.SetDock(_lutVrRevertBtn, Dock.Right);
            DockPanel.SetDock(_hamburgerBtn, Dock.Left);
            DockPanel.SetDock(_lutSelector, Dock.Left);
            topRow.Children.Add(_settingsBtn);
            topRow.Children.Add(_lutVrRevertBtn);
            topRow.Children.Add(_hamburgerBtn);
            topRow.Children.Add(_lutSelector);
            topRow.Children.Add(_rangeBar);

            // Shrink LUT ComboBox first; only after it reaches 0 does ValueRangeBar compress.
            // fixedW  = hamburger(26) + settings(22) + small buffer
            // labelW  = "LUT:" label + spacing + panel margins ≈ 42 px
            // rangeMin = ValueRangeBar minimum (min/max boxes both at MinBoxWidth=36)
            topRow.SizeChanged += (_, e) =>
            {
                const double fixedW = 26 + 22 + 22 + 4; // hamburger + settingsBtn + lutVrRevertBtn
                const double labelW = 42;
                const double rangeMin = 240;
                double comboW = Math.Clamp(e.NewSize.Width - fixedW - labelW - rangeMin, 0, 97);
                _lutSelector.ComboWidth = comboW;
            };

            // Layout: TopRow → SettingsPanel → OrthoPanel (fill, trackers embedded in col 0) → StatusBar
            var dock = new DockPanel();
            DockPanel.SetDock(topRow, Dock.Top);
            DockPanel.SetDock(_settingsPanel, Dock.Top);
            DockPanel.SetDock(statusBar, Dock.Bottom);
            dock.Children.Add(topRow);
            dock.Children.Add(_settingsPanel);
            dock.Children.Add(statusBar);
            dock.Children.Add(_orthoPanel);

            // Embed tracker panel inside OrthogonalPanel column 0 so its width always matches MainView
            _orthoPanel.SetTopPanel(_trackerPanel);

            _contentBorder = new Border { Child = dock };
            Content = _contentBorder;

            IsModifiedChanged += (_, _) => UpdateDirtyBadge();

            // ── Splitter drag → window resize (WinForms MxViewForm parity) ──────
            _orthoPanel.VerticalSplitterDragged += delta =>
            {
                if (WindowState == WindowState.Normal) Width += delta;
            };
            _orthoPanel.HorizontalSplitterDragged += delta =>
            {
                if (WindowState == WindowState.Normal) Height += delta;
            };

            // Auto-resize: grow window so side view fits all data at the current zoom
            _orthoPanel.AutoResizeBottomRequested += () =>
            {
                if (WindowState != WindowState.Normal) return;
                var (_, bmpH) = _orthoPanel.BottomView.GetEffectiveBmpDims();
                int zCount = _orthoPanel.BottomView.MatrixData?.YCount ?? 1;
                double onePixelDip = zCount > 0 ? bmpH / zCount : 50.0;
                double margin = Math.Max(onePixelDip, 50.0);
                double delta = bmpH + margin - _orthoPanel.BottomView.Bounds.Height;
                if (delta <= 0.5) return;
                double newH = Height + delta;
                var screen = Screens?.ScreenFromWindow(this);
                if (screen != null)
                {
                    double maxH = (screen.WorkingArea.Bottom - Position.Y) / screen.Scaling;
                    newH = Math.Min(newH, maxH);
                }
                Height = newH;
            };
            _orthoPanel.AutoResizeRightRequested += () =>
            {
                if (WindowState != WindowState.Normal) return;
                var (bmpW, _) = _orthoPanel.RightView.GetEffectiveBmpDims();
                int zCount = _orthoPanel.RightView.MatrixData?.YCount ?? 1;
                double onePixelDip = zCount > 0 ? bmpW / zCount : 50.0;
                double margin = Math.Max(onePixelDip, 50.0);
                double delta = bmpW + margin - _orthoPanel.RightView.Bounds.Width;
                if (delta <= 0.5) return;
                double newW = Width + delta;
                var screen = Screens?.ScreenFromWindow(this);
                if (screen != null)
                {
                    double maxW = (screen.WorkingArea.Right - Position.X) / screen.Scaling;
                    newW = Math.Min(newW, maxW);
                }
                Width = newW;
            };
            _orthoPanel.AutoResizeMainRequested += () =>
            {
                if (WindowState != WindowState.Normal) return;
                if (_orthoPanel.MainView.MatrixData == null) return;

                var (bmpW, bmpH) = _orthoPanel.MainView.GetEffectiveBmpDims();
                double pad = _orthoPanel.MainView.BitmapPadding * 2;
                // Use a 1-DIP epsilon so the scrollbar condition (bmpW - vpW >= 1.0) is never met.
                double targetMainW = Math.Ceiling(bmpW) + pad + 1.0;
                double targetMainH = Math.Ceiling(bmpH) + pad + 1.0;

                var screen = Screens?.ScreenFromWindow(this);
                if (screen != null)
                {
                    double nonMainW = Width - _orthoPanel.MainView.Bounds.Width;
                    double nonMainH = Height - _orthoPanel.MainView.Bounds.Height;
                    double maxMainW = (screen.WorkingArea.Right - Position.X) / screen.Scaling - nonMainW;
                    double maxMainH = (screen.WorkingArea.Bottom - Position.Y) / screen.Scaling - nonMainH;
                    targetMainW = Math.Min(targetMainW, maxMainW);
                    targetMainH = Math.Min(targetMainH, maxMainH);
                }

                bool isOrtho = _orthoPanel.ShowRight || _orthoPanel.ShowBottom;
                if (isOrtho)
                {
                    // Ortho: move splitters to show the full bitmap at the current zoom,
                    // then defer FitToView until the grid layout and window resize have settled.
                    _orthoPanel.SetMainViewPixelSize(targetMainW, targetMainH);
                    Dispatcher.UIThread.Post(
                        () => _orthoPanel.MainView.FitToView(),
                        DispatcherPriority.Background);
                }
                else
                {
                    // Normal: resize the window, then defer FitToView so the bitmap fills
                    // the viewport exactly after the layout pass settles.
                    double deltaW = targetMainW - _orthoPanel.MainView.Bounds.Width;
                    double deltaH = targetMainH - _orthoPanel.MainView.Bounds.Height;
                    if (Math.Abs(deltaW) > 0.5) Width += deltaW;
                    if (Math.Abs(deltaH) > 0.5) Height += deltaH;
                    Dispatcher.UIThread.Post(
                        () => _orthoPanel.MainView.FitToView(),
                        DispatcherPriority.Background);
                }
            };

#if DEBUG
            this.SizeChanged += (_, _) =>
            {
                string line = $"Window size: W:{this.Bounds.Width:F0} H:{this.Bounds.Height:F0}";
                Debug.WriteLine(line);
            };
#endif

            // ── Maximised mode: swap column layout so main view (not side) grows ──
            // In maximised mode Column 0 = * and Column 2 = fixed so all extra
            // screen space goes to the main view; side views keep their size.
            // Reverts on restore so that normal-mode resize continues to grow side views.
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == WindowStateProperty)
                _orthoPanel.MaximizedMode = WindowState == WindowState.Maximized;
        }

        /// <summary>
        /// Applies a colored border around the window content to indicate sync group membership.
        /// Pass <c>null</c> to remove the highlight.
        /// </summary>
        public void SetSyncBorder(IBrush? brush)
        {
            _contentBorder.BorderBrush = brush;
            _contentBorder.BorderThickness = brush != null ? new Thickness(1) : default;
        }

        protected override async void OnClosing(WindowClosingEventArgs e)
        {
            if (ShouldConfirmClose && !SuppressCloseConfirmation)
            {
                e.Cancel = true;
                bool isReadOnly = _currentData != null && !_currentData.IsWritable;
                var result = await SaveOnCloseDialog.ShowAsync(this, Title, isReadOnly);
                switch (result)
                {
                    case SaveOnCloseResult.Save:
                        // Read-only virtual data cannot be overwritten; always open Save As picker.
                        // After a successful copy-save, clear dirty state and close even though
                        // IsWritable is false (the copy was written; it is safe to close the source).
                        string? savePath = (!isReadOnly && SourcePath is { Length: > 0 } && HasFile)
                            ? SourcePath : null;
                        await SaveAsAsync(savePath);
                        if (isReadOnly || !IsModified) { ClearAllDirty(); Close(); }
                        break;
                    case SaveOnCloseResult.Discard:
                        ClearAllDirty();
                        Close();
                        break;
                        // null (Cancel): do nothing, window stays open
                }
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            CloseXYProjectionWindow();
            CloseLinkedChildren();
            CloseAllLineProfiles();
            CloseCacheMonitor();
            (DataContext as IDisposable)?.Dispose();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is MatrixPlotterViewModel vm)
            {
                ClearAllDirty();
                Title = vm.Title;
                _view.Lut = vm.Lut;
                _lutSelector.SelectLut(vm.Lut);
                Icon = _lutSelector.SelectedIcon;
                SetMatrixData(vm.MatrixData);

                vm.PropertyChanged += (_, pe) =>
                {
                    switch (pe.PropertyName)
                    {
                        case nameof(vm.MatrixData):
                            SetMatrixData(vm.MatrixData);
                            SetDirty(DirtyFlags.Data, true);
                            break;
                        case nameof(vm.Lut):
                            _view.Lut = vm.Lut;
                            _lutSelector.SelectLut(vm.Lut);
                            break;
                        case nameof(vm.ActiveFrame):
                            _view.FrameIndex = vm.ActiveFrame;
                            break;
                        case nameof(vm.Title):
                            Title = vm.Title;
                            break;
                    }
                };
            }
        }

        /// <summary>
        /// Replaces the displayed <see cref="IMatrixData"/>, rebuilds <see cref="AxisTracker"/>s,
        /// and wires <c>ActiveIndexChanged</c> so tracker interactions update <see cref="MxView.FrameIndex"/>.
        /// Called internally whenever <see cref="MxView.MatrixData"/> or <c>ViewModel.MatrixData</c> changes.
        /// </summary>
        private void SetMatrixData(IMatrixData? data)
        {
            _initializingData = true;
            // Capture current state before replacing so we can decide whether to preserve view settings.
            // Fixed mode is sticky: data updates should not silently reset a deliberately chosen range.
            bool isFirstLoad = _currentData == null;
            var previousMode = _rangeBar.Mode;

            // Capture resume snapshot BEFORE tearing down the current state.
            ResumeSnapshot? resume = null;
            if (ResumeSettingsEnabled && _currentData != null)
            {
                var axisIndices = new System.Collections.Generic.Dictionary<string, int>();
                foreach (var kv in _axisTrackers)
                    axisIndices[kv.Key] = kv.Value.Axis.Index;
                string? frozenAxis = null;
                foreach (var kv in _axisTrackers)
                    if (kv.Value.FreezeButton.IsChecked == true) { frozenAxis = kv.Key; break; }
                resume = new ResumeSnapshot(axisIndices, frozenAxis, _rangeBar.Mode, _view.FixedMin, _view.FixedMax);
            }

            // Disconnect from the old dataset
            if (_currentData != null && _activeIndexHandler != null)
            {
                _currentData.ActiveIndexChanged -= _activeIndexHandler;
                _activeIndexHandler = null;
            }

            // Dispose any active action — its ROIs were created for the old data context.
            if (_activeAction != null)
            {
                _activeAction.Completed -= OnActionCompleted;
                _activeAction.Cancelled -= OnActionCancelled;
                _activeAction.Dispose();
                _activeAction = null;
            }

            _currentData = data;

            // Invalidate menu panel so it rebuilds with correct save label on next open
            if (_menuPanel?.Parent is Panel parent)
                parent.Children.Remove(_menuPanel);
            _menuPanel = null;

            // Clearing removes children from the visual tree → OnDetachedFromVisualTree
            // stops any running animations cleanly.
            _orthoController.Deactivate();
            _trackerPanel.Children.Clear();
            _axisTrackers.Clear();

            // Reset FrameIndex to 0 BEFORE swapping MatrixData.
            // If the new data has fewer frames than the current FrameIndex (e.g. single-frame
            // result of a "This frame only" crop replacing a multi-frame hyperstack),
            // the view would immediately attempt to render with the stale out-of-range index
            // and throw in BitmapWriter.CheckValidity before the line below corrects it.
            _settingMatrixData = true;
            try
            {
                _view.FrameIndex = 0;
                _view.MatrixData = data;
                _view.FrameIndex = data?.ActiveIndex ?? 0;
            }
            finally { _settingMatrixData = false; }
            MatrixDataChanged?.Invoke(this, data);

            // Sync multi-frame UI first so SetMode sees the correct _isMultiFrame state
            bool isMultiFrame = data is { FrameCount: > 1 };
            _rangeBar.SetMultiFrame(isMultiFrame);
            if (_autoRadio != null) _autoRadio.Content = isMultiFrame ? "Current" : "Auto";
            if (_allRadio != null) _allRadio.IsVisible = isMultiFrame;
            _view.ExtractFrameAllowed = isMultiFrame;
            _orthoPanel.BottomView.ExtractFrameAllowed = isMultiFrame;
            _orthoPanel.RightView.ExtractFrameAllowed = isMultiFrame;

            // Value range mode across data updates:
            //   Fixed  → always sticky; keep mode and range values as-is.
            //   Others → apply default logic (All for InMemory multi-frame, Current otherwise).
            // On first load RestoreViewSettings may further override via mxplot.vr.* metadata.
            bool restoreVR = isFirstLoad || previousMode != ValueRangeMode.Fixed;
            if (restoreVR)
            {
                var defaultMode = data is { FrameCount: > 1, IsVirtual: false }
                    ? ValueRangeMode.All
                    : ValueRangeMode.Current;
                _rangeBar.SetMode(defaultMode);
            }

            if (data == null || data.Axes.Count == 0) //no data or single-frame data
            {
                _trackerPanel.IsVisible = false;
                CloseCacheMonitor();
                if (data != null) RestoreViewSettings(data, restoreVR);
                _initializingData = false;
                CaptureLutVrSnapshot();
                CaptureScaleSnapshot(data);
                ClearAllDirty();
                if (_lutVrRevertBtn != null) _lutVrRevertBtn.IsVisible = false;
                UpdateScaleRevertButton();
                RefreshInfoTab();
                RaiseRefreshed();
                return;
            }

            // Forward MatrixData.ActiveIndex changes to the view's FrameIndex.
            // Axis → DimensionStructure → MatrixData.ActiveIndex → ActiveIndexChanged → here.
            _activeIndexHandler = (_, _) =>
            {
                _view.FrameIndex = data.ActiveIndex;
                _orthoController.UpdateFrameIndicator();
                _orthoController.RefreshSlices();
                RefreshAllOverlayAnalysis();
                // All mode: update the displayed global range whenever the active frame changes.
                // Virtual: pre-populate the cache for this frame via MMF — in All mode IsFixedRange=true
                //          so MxView's render never calls GetValueRange; we must trigger it manually.
                // InMemory: GetArray() invalidates a frame's min/max cache on write, so dirty frames
                //           are rescanned by GetGlobalValueRange() — forceRefresh only touches invalid
                //           entries, so this is O(dirty frames) and safe to call on every index change.
                if (_rangeBar.Mode == ValueRangeMode.All)
                {
                    if (data.IsVirtual)
                        data.GetValueRange(data.ActiveIndex); // caches this frame's range (MMF access, usually fast)
                    ApplyAllModeRange();
                }
            };
            data.ActiveIndexChanged += _activeIndexHandler;

            foreach (var axis in data.Axes)
            {
                if (false && axis is ColorChannel cc)
                {
                    var ctracker = new ColorAxisTracker(cc);
                    _trackerPanel.Children.Add(ctracker);
                }
                else
                {
                    var tracker = new AxisTracker(axis);
                    _trackerPanel.Children.Add(tracker);
                    _axisTrackers[axis.Name] = tracker;
                    WireFreezeButton(tracker, axis);
                    tracker.IndexChanged += (_, idx) =>
                    {
                        if (!_syncApplying) SyncAxisIndexChanged?.Invoke(this, (axis.Name, idx));
                    };
                }
            }

            _trackerPanel.IsVisible = true;
            CloseCacheMonitor();

            // Restore axis indices and frozen axis from resume snapshot (ResumeSettingsEnabled).
            if (resume != null)
            {
                foreach (var kv in _axisTrackers)
                {
                    if (resume.AxisIndices.TryGetValue(kv.Key, out int savedIdx))
                    {
                        int clamped = Math.Clamp(savedIdx, 0, kv.Value.Axis.Count - 1);
                        kv.Value.Axis.Index = clamped;
                    }
                }
                if (resume.FrozenAxisName != null && _axisTrackers.ContainsKey(resume.FrozenAxisName))
                    SetOrthogonalView(resume.FrozenAxisName);
            }

            RestoreViewSettings(data, restoreVR);

            // Apply resumed VR settings on top of RestoreViewSettings (resume takes priority
            // over metadata-stored defaults when ResumeSettingsEnabled is true).
            if (resume != null && restoreVR)
            {
                if (resume.VrMode == ValueRangeMode.Fixed && !double.IsNaN(resume.VrMin))
                {
                    _view.IsFixedRange = true;
                    _view.FixedMin = resume.VrMin;
                    _view.FixedMax = resume.VrMax;
                    _rangeBar.SetMode(ValueRangeMode.Fixed);
                    _rangeBar.SetRange(resume.VrMin, resume.VrMax);
                }
                else
                {
                    _rangeBar.SetMode(resume.VrMode);
                }
            }

            _initializingData = false;
            CaptureLutVrSnapshot();
            CaptureScaleSnapshot(data);
            ClearAllDirty();
            if (_lutVrRevertBtn != null) _lutVrRevertBtn.IsVisible = false;
            UpdateScaleRevertButton();
            UpdateStatusBar();
            RefreshInfoTab();
            RaiseRefreshed();
        }

        /// <summary>
        /// Fires <see cref="Refreshed"/> with the <see cref="_isRefreshing"/> re-entrancy guard.
        /// Called from both <see cref="Refresh"/> and <see cref="SetMatrixData"/>.
        /// </summary>
        private void RaiseRefreshed()
        {
            if (_isRefreshing) return;
            _isRefreshing = true;
            try { Refreshed?.Invoke(this, EventArgs.Empty); }
            finally { _isRefreshing = false; }
        }

        /// <summary>
        /// Clears and rebuilds the axis tracker panel from <paramref name="data"/>.
        /// Called after <see cref="IMatrixData.DefineDimensions"/> replaces axis objects
        /// (e.g. when a specialized axis is downgraded to a plain <see cref="Axis"/> on rename).
        /// </summary>
        private void RebuildTrackerPanel(IMatrixData data)
        {
            _orthoController.Deactivate();
            _trackerPanel.Children.Clear();
            _axisTrackers.Clear();
            foreach (var axis in data.Axes)
            {
                var tracker = new AxisTracker(axis);
                _trackerPanel.Children.Add(tracker);
                _axisTrackers[axis.Name] = tracker;
                WireFreezeButton(tracker, axis);
                var capturedAxis = axis;
                tracker.IndexChanged += (_, idx) =>
                {
                    if (!_syncApplying) SyncAxisIndexChanged?.Invoke(this, (capturedAxis.Name, idx));
                };
            }
            _trackerPanel.IsVisible = data.Axes.Count > 0;
        }

        /// <summary>
        /// Applies the global value range to the view and updates the imperfect state.
        /// For InMemory data: synchronous full scan (forceRefresh=true).
        /// For Virtual data: cached-only lookup (forceRefresh=false); shows imperfect warning when some frames are not yet scanned.
        /// </summary>
        private void ApplyAllModeRange()
        {
            var data = _currentData;
            if (data == null) return;

            double min, max;
            bool imperfect;

            int invalidCount = 0;
            if (data.IsVirtual)
            {
                (min, max) = data.GetGlobalValueRange(out var invalids, forceRefresh: false);
                invalidCount = invalids.Count;
                imperfect = invalidCount > 0;
            }
            else
            {
                // InMemory: synchronous full scan; subsequent calls are fast due to caching
                (min, max) = data.GetGlobalValueRange(out _, forceRefresh: true);
                imperfect = false;
            }

            if (!double.IsNaN(min))
            {
                _view.FixedMin = min;
                _view.FixedMax = max;
                _rangeBar.SetRange(min, max);
            }
            _rangeBar.SetImperfect(imperfect);

            string allTip = imperfect
                ? $"Global min/max \u2014 {invalidCount} frame{(invalidCount == 1 ? "" : "s")} not yet scanned"
                : "Global min/max across all frames";
            if (_allWarningIcon != null)
            {
                _allWarningIcon.IsVisible = imperfect;
                ToolTip.SetTip(_allWarningIcon, allTip);
            }
            if (_allRadio != null)
                ToolTip.SetTip(_allRadio, allTip);
        }

        // ── Notice (transient status info) ───────────────────────────────────

        /// <summary>
        /// Displays a transient message in the status bar notice area (e.g., overlay dimensions).
        /// Pass <c>null</c> or empty string to clear.
        /// Safe to call from any thread.
        /// </summary>
        public void SetNotice(string? text)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => SetNotice(text));
                return;
            }
            _toastCts?.Cancel();
            _noticeText.Classes.Remove("toast");
            _noticeText.Opacity = 1.0;
            _noticeText.Text = text ?? string.Empty;
            _noticeText.IsVisible = !string.IsNullOrEmpty(text) && !_progressBar.IsVisible;
        }

        private async void ShowToast(string message)
        {
            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var cts = _toastCts;
            _noticeText.Classes.Add("toast");
            _noticeText.Text = message;
            _noticeText.Opacity = 1.0;
            _noticeText.IsVisible = !_progressBar.IsVisible;
            try
            {
                await Task.Delay(1500, cts.Token);
                for (int step = 9; step >= 0; step--)
                {
                    if (cts.IsCancellationRequested) return;
                    _noticeText.Opacity = step / 10.0;
                    await Task.Delay(60, cts.Token);
                }
            }
            catch (OperationCanceledException) { return; }
            _noticeText.IsVisible = false;
            _noticeText.Classes.Remove("toast");
            _noticeText.Text = string.Empty;
            _noticeText.Opacity = 1.0;
        }

        private void UpdateStatusBar()
        {

            var data = _view.MatrixData;
            if (data == null)
            {
                _infoText.Text = string.Empty;
                _virtualBadge.IsVisible = false;
                _zoomText.Text = string.Empty;
                return;
            }

            string zoomStr = _view.IsFitToView
                ? $"|  {_view.Zoom * 100:0}% [Fit]"
                : $"|  {_view.Zoom * 100:0}%";

            long totalBytes = 1L * data.FrameCount * data.XCount * data.YCount * data.ElementSize;
            string sizeStr = totalBytes switch
            {
                >= 1L << 30 => $"{totalBytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
                >= 1L << 20 => $"{totalBytes / (1024.0 * 1024.0):F1} MB",
                >= 1L << 10 => $"{totalBytes / 1024.0:F1} KB",
                _ => $"{totalBytes} B"
            };

            _infoText.Text = $"[{data.ValueTypeName}]  {sizeStr}";
            string tooltip = $"{data.XCount}×{data.YCount}";
            if (data.FrameCount > 1) tooltip += $"  |  {data.FrameCount} frames";
            tooltip += $"  |  {data.ValueTypeName}";
            ToolTip.SetTip(_infoText, tooltip);
            _virtualBadge.IsVisible = data.IsVirtual;
            if (data.IsVirtual)
            {
                bool writable = data.IsWritable;
                _virtualBadge.Text = "(Virtual)";
                _virtualBadge.Foreground = writable
                    ? new SolidColorBrush(Color.FromRgb(220, 80, 80))   // red-ish for writable
                    : Brushes.DodgerBlue;                                // blue for read-only
                ToolTip.SetTip(_virtualBadge,
                    (writable ? "Virtual frames (Writable)" : "Virtual frames (Read-Only)")
                    + "\nClick to open back-end Cache Monitor");
            }
            _zoomText.Text = $"  {zoomStr}";
            ToolTip.SetTip(_zoomText, _view.IsFitToView
                ? "Zoom level: Fit to view"
                : "Double-click the image to fit to view");
        }

        // ── Status-bar progress reporter ──────────────────────────────────

        /// <summary>
        /// Shows an inline progress indicator in the status bar and returns an
        /// <see cref="IProgress{T}"/> compatible with <see cref="IProgressReportable"/>.
        /// <para>
        /// <b>Protocol:</b> report <c>-N</c> to declare <c>N</c> total steps
        /// (switches from indeterminate to determinate mode), then report
        /// <c>0, 1, …, N-1</c> for each completed step. The caller must invoke
        /// <see cref="EndProgress"/> when the operation finishes.
        /// </para>
        /// </summary>
        /// <example>
        /// <code>
        /// var progress = plotter.BeginProgress("Saving…");
        /// if (writer is IProgressReportable p) p.ProgressReporter = progress;
        /// await Task.Run(() => writer.Write(data, path));
        /// plotter.EndProgress();
        /// </code>
        /// </example>
        public IProgress<int> BeginProgress(string label = "Processing…", bool blockInput = false)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                IProgress<int>? result = null;
                Dispatcher.UIThread.Invoke(() => result = BeginProgress(label, blockInput));
                return result!;
            }

            _progressText.Text = label;
            _progressBar.IsIndeterminate = true;
            _progressBar.Value = 0;
            _infoText.IsVisible = false;
            _virtualBadge.IsVisible = false;
            _zoomText.IsVisible = false;
            _noticeText.IsVisible = false;
            _progressSep.IsVisible = false;
            _progressText.Margin = new Thickness(8, 0, 4, 0);
            _progressText.IsVisible = true;
            _progressBar.IsVisible = true;

            if (blockInput)
            {
                var layer = OverlayLayer.GetOverlayLayer(this);
                if (layer != null)
                {
                    _inputBlocker = new Border
                    {
                        IsHitTestVisible = true,
                        Background = Brushes.Transparent,
                        Cursor = new Cursor(StandardCursorType.Wait),
                        Width = layer.Bounds.Width,
                        Height = layer.Bounds.Height,
                    };
                    void OnBoundsChanged(object? s, AvaloniaPropertyChangedEventArgs e)
                    {
                        if (e.Property == BoundsProperty && s is Control c && _inputBlocker != null)
                        { _inputBlocker.Width = c.Bounds.Width; _inputBlocker.Height = c.Bounds.Height; }
                    }
                    layer.PropertyChanged += OnBoundsChanged;
                    _inputBlockerCleanup = () => layer.PropertyChanged -= OnBoundsChanged;
                    layer.Children.Add(_inputBlocker);
                }
            }

            return new StatusBarProgress(this, label);
        }

        /// <summary>
        /// Hides the status-bar progress indicator. Safe to call from any thread.
        /// </summary>
        public void EndProgress()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(EndProgress);
                return;
            }

            if (_inputBlocker != null)
            {
                _inputBlockerCleanup?.Invoke();
                _inputBlockerCleanup = null;
                (_inputBlocker.Parent as Panel)?.Children.Remove(_inputBlocker);
                _inputBlocker = null;
            }

            _progressSep.IsVisible = false;
            _progressText.IsVisible = false;
            _progressText.Margin = new Thickness(4, 0, 4, 0);
            _progressBar.IsVisible = false;
            _progressBar.Value = 0;

            _infoText.IsVisible = true;
            _zoomText.IsVisible = true;
            _noticeText.IsVisible = !string.IsNullOrEmpty(_noticeText.Text);
            UpdateStatusBar(); // restores text content and _virtualBadge
        }

        private void OnProgressReport(string label, int value)
        {
            if (value < 0)
            {
                int total = -value;
                _progressBar.IsIndeterminate = false;
                _progressBar.Maximum = total;
                _progressBar.Value = 0;
                _progressText.Text = $"{label} 0/{total}";
            }
            else
            {
                int total = (int)_progressBar.Maximum;
                if (total > 0 && value + 1 < total)
                {
                    _progressBar.Value = value + 1;
                    _progressText.Text = $"{label} {value + 1}/{total}";
                }
                else if (total > 0)
                {
                    _progressBar.Value = total;
                    _progressText.Text = $"{label} done";
                }
            }
        }

        private sealed class StatusBarProgress(MatrixPlotter owner, string label) : IProgress<int>
        {
            public void Report(int value)
            {
                if (Dispatcher.UIThread.CheckAccess())
                    owner.OnProgressReport(label, value);
                else
                    Dispatcher.UIThread.Post(() => owner.OnProgressReport(label, value));
            }
        }

        private void OpenOrActivateCacheMonitor()
        {
            if (_currentData == null || !_currentData.IsVirtual) return;

            if (_cacheMonitorWindow == null)
            {
                _cacheMonitorWindow = new CacheMonitorWindow(_currentData);
                _cacheMonitorWindow.Closed += (_, _) => _cacheMonitorWindow = null;
                // Register with the dashboard and declare this plotter as parent.
                // NotifyCreated posts RegisterWindow; SetParentLink is called after so it is
                // guaranteed to be set before the posted delegate executes (established pattern).
                PlotWindowNotifier.NotifyCreated(_cacheMonitorWindow);
                PlotWindowNotifier.SetParentLink(_cacheMonitorWindow, this);
                _cacheMonitorWindow.Show();
            }
            else
            {
                _cacheMonitorWindow.Activate();
            }
        }

        private void CloseCacheMonitor()
        {
            _cacheMonitorWindow?.Close();
            _cacheMonitorWindow = null;
        }

        /// <summary>
        /// Closes all linked child plotters and removes event subscriptions.
        /// Called from <see cref="OnClosed"/> so that children do not outlive the parent.
        /// </summary>
        private void CloseLinkedChildren()
        {
            // Iterate over a snapshot — Close handlers mutate _linkedChildren via UnlinkRefresh
            foreach (var child in _linkedChildren.ToArray())
            {
                child.Refreshed -= OnLinkedChildRefreshed;
                Refreshed -= child.OnLinkedParentRefreshed;
                child.Closed -= OnLinkedChildClosed;
                child.Close();
            }
            _linkedChildren.Clear();
        }

        // ── Plugin support ────────────────────────────────────────────────────

        /// <summary>Creates a context capturing the current plotter state for plugin execution.</summary>
        internal IMatrixPlotterContext CreatePluginContext()
            => new MatrixPlotterContextImpl(this);

        private sealed class MatrixPlotterContextImpl : IMatrixPlotterContext
        {
            private readonly MatrixPlotter _host;

            internal MatrixPlotterContextImpl(MatrixPlotter host) => _host = host;

            public IMatrixData Data
                => _host._currentData
                   ?? throw new InvalidOperationException("No data is loaded in this plotter.");

            public double DisplayMinValue
            {
                get => _host._view.FixedMin;
                set { _host._view.IsFixedRange = true; _host._view.FixedMin = value; }
            }

            public double DisplayMaxValue
            {
                get => _host._view.FixedMax;
                set { _host._view.IsFixedRange = true; _host._view.FixedMax = value; }
            }

            public TopLevel? Owner => _host;

            public IPlotWindowService WindowService => MatrixPlotterPluginRegistry.WindowService;

            public int ActiveFrameIndex => _host._view.FrameIndex;

            public LookupTable? CurrentLut => _host._view.Lut;

            public bool IsLutInverted => _host._view.IsInvertedColor;

            public WriteableBitmap RenderFrame(int frameIndex,
                                               double valueMin = double.NaN,
                                               double valueMax = double.NaN)
            {
                // Single-frame path only. When composite mode is active, use RenderFrameAsComposite
                // instead (planned for v0.2.0 — see design notes in IMatrixPlotterContext.cs).
                var data = Data;
                var lut = CurrentLut ?? ColorThemes.Grayscale;

                if (double.IsNaN(valueMin) || double.IsNaN(valueMax))
                {
                    if (_host._view.IsFixedRange)
                    {
                        valueMin = _host._view.FixedMin;
                        valueMax = _host._view.FixedMax;
                    }
                    else
                    {
                        (valueMin, valueMax) = data.GetValueRange(frameIndex);
                    }
                }

                return Rendering.BitmapWriter.CreateBitmap(data, frameIndex, lut, valueMin, valueMax);
            }
        }
    }
}
