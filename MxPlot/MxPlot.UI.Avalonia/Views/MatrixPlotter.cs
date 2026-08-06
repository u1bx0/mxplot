using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.Core.Utils;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Controls;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Plugins;
using MxPlot.UI.Avalonia.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Standalone window that displays <see cref="IMatrixData"/> via <see cref="MxView"/>.
    /// Usage: <c>new MatrixPlotter { DataContext = MatrixPlotterViewModel.Create(data, lut) }.Show();</c>
    /// </summary>
    public partial class MatrixPlotter : Window
    {
        private MxView _view;
        private LutSelector _lutSelector;
        private StackPanel _trackerPanel;
        private OrthogonalPanel _orthoPanel;
        private OrthogonalViewController _orthoController;

        // For managing the ActiveIndexChanged subscription across MatrixData swaps
        private IMatrixData? _currentData;
        private EventHandler? _activeIndexHandler;

        // ── LinkedSource: delegate UI features to a parent plotter ───────────

        private MatrixPlotter? _linkedSource;

        /// <summary>
        /// When non-null, this plotter is a derived view (e.g. XY projection) of the source.
        /// Export menus, overlay token resolution, and FrameCount checks delegate to the source
        /// rather than to this plotter's own <see cref="_currentData"/>.
        /// Set by the parent on window creation; cleared on parent or child close.
        /// Subscribes to the parent's <see cref="Refreshed"/> event so that overlay analysis
        /// (line profiles, region statistics, ROI value range) updates when the parent changes frame.
        /// </summary>
        public MatrixPlotter? LinkedSource
        {
            get => _linkedSource;
            internal set
            {
                if (_linkedSource != null)
                    _linkedSource.Refreshed -= OnLinkedSourceRefreshed;
                _linkedSource = value;
                if (_linkedSource != null)
                    _linkedSource.Refreshed += OnLinkedSourceRefreshed;
            }
        }

        private void OnLinkedSourceRefreshed(object? sender, EventArgs e)
        {
            // Skip: XYProjection windows receive their data update via UpdateProjectionData
            // (called from OnXYProjectionChanged after the new projection is computed).
            // Calling RefreshAllOverlayAnalysis here would run against the stale MatrixData
            // instance that has already been replaced by the time this event fires.
        }

        /// <summary>
        /// Updates the projection data displayed by this window without triggering a full
        /// <see cref="SetMatrixData"/> re-initialization.  Intended for XY-projection child
        /// windows whose <see cref="IMatrixData"/> instance changes on every parent frame
        /// navigation but whose overlay state (line profiles, ROI, statistics) must survive.
        /// Only updates <see cref="_view"/> and then refreshes all overlay analysis.
        /// </summary>
        internal void UpdateProjectionData(IMatrixData data)
        {
            _view.SetMatrixDataInternal(data);
            RefreshAllOverlayAnalysis();
        }

        /// <summary>
        /// Names of axes in <see cref="LinkedSource"/> that are "consumed" by this derived view
        /// and therefore excluded from delegation (e.g. <c>"Z"</c> for XY projection).
        /// Used by export hosts to name the excluded axis in <see cref="Plugins.IRenderHost.ExcludedAxisName"/>.
        /// </summary>
        public IReadOnlyList<string>? LinkedSourceExcludedAxes { get; internal set; }

        /// <summary>
        /// The effective data for UI decisions (Export menus, FrameCount, overlay tokens).
        /// When <see cref="LinkedSource"/> is set, returns its current data instead of this plotter's own.
        /// </summary>
        private IMatrixData? EffectiveData => LinkedSource?._currentData ?? _currentData;
        private Dictionary<string, AxisTracker> _axisTrackers = [];

        /// <summary>
        /// Closes the hamburger menu panel if it is currently open.
        /// Used by sync groups to hide stale Scale tab displays when another window
        /// changes scale settings.
        /// </summary>
        internal void CloseMenuPanelIfOpen()
        {
            if (_menuPanel?.IsVisible == true)
                HideMenuPanel();
        }

        /// <summary>
        /// <c>true</c> while the user is dragging an <see cref="AxisTracker"/> slider.
        /// Used to update the drag overlay on index changes during the drag gesture.
        /// </summary>
        private bool _isDraggingAxisTracker;
            

        // Status bar segments
        private TextBlock _dirtyBadge;   // "●" shown when view settings are modified
        private TextBlock _infoText;     // "[float]  11.9 GB"
        private TextBlock _virtualBadge; // "(Virtual)" → clickable, visible only when virtual
        private TextBlock _zoomText;     // "|  200% [Fit]"
        private TextBlock _noticeText;   // transient info (overlay dimensions, etc.)
        private TextBlock _progressSep;  // "|" separator before progress area
        private TextBlock _progressText; // "Saving… 3/100"
        private ProgressBar _progressBar;
        private Border? _inputBlocker;           // transparent hit-test blocker during blocking operations
        private Action? _inputBlockerCleanup;    // removes the OverlayLayer.PropertyChanged handler
        private CancellationTokenSource? _toastCts;
        private Border _toastPanel;   // overlay toast container
        private TextBlock _toastText; // overlay toast message
        private Border _contentBorder;  // wraps window content for sync border highlight

        // One CacheMonitorWindow per MatrixPlotter
        private CacheMonitorWindow? _cacheMonitorWindow;

        // Value range bar + inline settings panel
        private ValueRangeBar _rangeBar;
        private Button _settingsBtn;
        private Border _settingsPanel;
        private ToggleButton? _invertLutChk;
        private NumericUpDown? _levelNud;
        private HistogramPlotControl? _histogramPlot;
        private CancellationTokenSource? _histogramCts;  // Cancel pending histogram calculation

        // Hamburger menu panel (OverlayLayer — renders inside the window's Skia surface
        // so alpha transparency correctly shows the underlying view content)
        private Border? _menuPanel;
        private Border? _zoomFlyout;
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
        private string? _metaPreviousKey;    // last successfully loaded key; used for dirty-check on switch
        //private bool _metaSwitchGuard;    // prevents re-entrant SelectionChanged during programmatic revert -> Changed to _reentrancy

        // Linked plotter support
        private readonly List<MatrixPlotter> _linkedChildren = [];

        /// <summary>
        /// Re-entrancy guard for <see cref="Refresh"/> and <see cref="RaiseRefreshed"/>.
        /// Set to <c>true</c> for the duration of a refresh cycle to prevent overlapping
        /// invocations from the UI thread (e.g. bidirectional link callbacks).
        /// <para>
        /// Declared <c>volatile</c> for cross-thread visibility. Background callers always
        /// marshal via <see cref="Avalonia.Threading.Dispatcher.Post"/>, so no lock is needed.
        /// In the unlikely event of a race, the worst outcome is a redundant redraw.
        /// </para>
        /// </summary>
        private volatile bool _isRefreshing;

        // File session state
        [Flags]
        private enum DirtyFlags
        {
            None = 0,
            Lut = 1 << 0,
            Vr = 1 << 1,
            Scale = 1 << 2,
            Overlay = 1 << 3,
            Data = 1 << 4,
        }
        private DirtyFlags _dirty;
        private bool _neverSaved;        // true when data has no SourcePath and has never been saved
        private bool _isSecondaryWindow; // true for linked derivative views (MIP, projection, linked filter) — no independent save needed
        //private bool _initializingData;  // true during SetMatrixData; suppresses LUT/VR dirty signals

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

        // ── Scale precision (for Info tab display and edit comparison) ──────
        private int _scaleValuePrecision = 5;
        private int _scaleStepPrecision = 4;

        /// <summary>
        /// Precision (significant digits) for displaying and comparing Min/Max/Range values
        /// in the Scale/Info tab. Default is 5 (equivalent to "G5").
        /// Valid range is 1–15; out-of-range values are clamped automatically.
        /// </summary>
        public int ScaleValuePrecision
        {
            get => _scaleValuePrecision;
            set => _scaleValuePrecision = Math.Clamp(value, 1, 15);
        }

        /// <summary>
        /// Precision (significant digits) for displaying and comparing Step values
        /// in the Scale/Info tab. Default is 4 (equivalent to "G4").
        /// Valid range is 1–15; out-of-range values are clamped automatically.
        /// </summary>
        public int ScaleStepPrecision
        {
            get => _scaleStepPrecision;
            set => _scaleStepPrecision = Math.Clamp(value, 1, 15);
        }

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
        /// Parameters: <c>(outputPath, parentWindow, renderHost, progress, cancellationToken)</c>.<br/>
        /// The delegate is responsible for showing any settings dialog and iterating frames via
        /// <see cref="Plugins.IRenderHost.RenderFrameAsync"/>.
        /// </param>
        /// <param name="RequiresStack">
        /// When <c>true</c> the menu item is shown only for stack data (<see cref="IMatrixData.FrameCount"/> &gt; 1).
        /// </param>
        public sealed record ExportFormatDescriptor(
            string Label,
            string Hint,
            string FileTypeName,
            string FilePattern,
            Func<string, Window, Plugins.IRenderHost, IProgress<int>, CancellationToken, Task> Exporter,
            bool RequiresStack = true);

        /// <summary>
        /// Additional export formats injected by the host application and shown under
        /// "Export as…" in the File menu alongside the built-in PNG entry.
        /// Populate this list before the window is shown (or any time before the menu panel
        /// is first opened — the panel is rebuilt lazily on first open).
        /// </summary>
        public List<ExportFormatDescriptor> ExportFormats { get; } = [];

        /// <summary>
        /// Synchronizes render settings (LUT, depth, invert, value range, ComplexValueMode)
        /// from the main view to orthogonal side views.
        /// Called internally when a mode is changed via context menu on a side view.
        /// </summary>
        internal void SyncOrthoRenderSettings() => _orthoController.SyncRenderSettings();

        /// <summary>
        /// Whether the current state differs from what was last opened or saved,
        /// or the data has no associated file path yet (never saved).
        /// </summary>
        public bool IsModified => _dirty != DirtyFlags.None || (_neverSaved && !_isSecondaryWindow);


        /// <summary>
        /// Raised on the UI thread when <see cref="IsModified"/> changes.
        /// </summary>
        public event EventHandler? IsModifiedChanged;

        /// <summary>
        /// Whether closing this window should prompt a “Save changes?” dialog.
        /// <c>true</c> whenever <see cref="IsModified"/> is <c>true</c> (includes unsaved new data).
        /// </summary>
        public bool IsSecondaryWindow
        {
            get => _isSecondaryWindow;
            set
            {
                if (_isSecondaryWindow == value) return;
                _isSecondaryWindow = value;
                IsModifiedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Whether closing this window should prompt a "Save changes?" dialog.
        /// Always <c>false</c> for secondary windows (MIP projections, orthogonal views, linked filters).
        /// </summary>
        public bool ShouldConfirmClose => !_isSecondaryWindow && IsModified;

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
            if (_reentrancy.IsActive(GuardContext.Initializing)) return;
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
            var neverSavedBefore = _neverSaved;
            _dirty = DirtyFlags.None;
            _neverSaved = false;
            if (_dirty != before || _neverSaved != neverSavedBefore)
                IsModifiedChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateDirtyBadge()
        {
            bool show = IsModified;
            _dirtyBadge.IsVisible = show;
            if (!show) return;
            if (SourcePath == null)
            {
                ToolTip.SetTip(_dirtyBadge, "Unsaved: not yet saved to a file.");
                return;
            }
            var parts = new System.Text.StringBuilder("Unsaved changes:");
            if (_dirty.HasFlag(DirtyFlags.Lut)) parts.Append("\n  \u2022 LUT / Color");
            if (_dirty.HasFlag(DirtyFlags.Vr)) parts.Append("\n  \u2022 Value Range");
            if (_dirty.HasFlag(DirtyFlags.Scale)) parts.Append("\n  \u2022 Scale / Axes");
            if (_dirty.HasFlag(DirtyFlags.Overlay)) parts.Append("\n  \u2022 Overlays");
            if (_dirty.HasFlag(DirtyFlags.Data)) parts.Append("\n  \u2022 Data / Metadata");
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
            if (_reentrancy.IsActive(GuardContext.Initializing)) return;
            if (_lutVrRevertBtn != null)
                _lutVrRevertBtn.IsVisible = (_dirty & (DirtyFlags.Lut | DirtyFlags.Vr)) != 0 && _lutVrSnapshot != null;
        }

        /// <summary>Shows or hides the Scale revert button in the Scale tab. Refreshes the tab to add/remove it.</summary>
        internal void SetScaleDirty(bool dirty)
        {
            SetDirty(DirtyFlags.Scale, dirty);
            if (!_reentrancy.IsActive(GuardContext.Initializing))
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

            using var _ini  = _reentrancy.Begin(GuardContext.Initializing);
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
            }
            else
            {
                _rangeBar.SetMode(snap.VrMode);
            }
            _orthoController.SyncRenderSettings();
            SaveViewSettings();

            SetDirty(DirtyFlags.Lut, false);
            SetDirty(DirtyFlags.Vr, false);
            if (_lutVrRevertBtn != null) _lutVrRevertBtn.IsVisible = false;
        }

        private void RevertScale()
        {
            var snap = _scaleSnapshot;
            if (snap == null || _currentData == null) return;

            using var _ = _reentrancy.Begin(GuardContext.Initializing);
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
        /// <para>
        /// When <paramref name="rebuildOrthogonalData"/> is <c>true</c>, forces orthogonal
        /// views to rebuild their slice/projection data from the current MainView data
        /// before refreshing bitmaps. Use this after directly modifying frame pixel values
        /// (e.g., via <c>GetArray()</c>) to ensure side views reflect the updated data.
        /// </para>
        /// <para>
        /// <b>Note:</b> <see cref="RefreshSlices"/> already updates side view bitmaps
        /// internally via <see cref="MxView.SetMatrixDataInternal"/>, so we only need
        /// to call <see cref="MxView.InvalidateSurface"/> (redraw) instead of
        /// <see cref="MxView.Refresh"/> (rebuild + redraw) to avoid redundant work.
        /// </para>
        /// </summary>
        /// <param name="rebuildOrthogonalData">
        /// Whether to rebuild orthogonal slice/projection data before refreshing bitmaps.
        /// Default is <c>false</c> (only redraws existing bitmaps using current LUT/VR).
        /// </param>
        public void Refresh(bool rebuildOrthogonalData = false)
        {
            if (_isRefreshing) return;

            void DoRefresh()
            {
                if (_isRefreshing) return;
                _isRefreshing = true;
                try
                {
                    _view.Refresh();

                    if (rebuildOrthogonalData && (_orthoPanel.ShowBottom || _orthoPanel.ShowRight))
                    {
                        // RefreshSlices internally calls SetMatrixDataInternal which triggers
                        // bitmap rebuild. Subsequent InvalidateSurface() only redraws.
                        _orthoController.RefreshSlices();
                    }

                    if (_orthoPanel.ShowBottom)
                    {
                        if (rebuildOrthogonalData)
                            _orthoPanel.BottomView.InvalidateSurface();
                        else
                            _orthoPanel.BottomView.Refresh();
                    }
                    if (_orthoPanel.ShowRight)
                    {
                        if (rebuildOrthogonalData)
                            _orthoPanel.RightView.InvalidateSurface();
                        else
                            _orthoPanel.RightView.Refresh();
                    }

                    RefreshAllOverlayAnalysis();
                    Refreshed?.Invoke(this, EventArgs.Empty);
                }
                finally { _isRefreshing = false; }
            }

            if (Dispatcher.UIThread.CheckAccess())
                DoRefresh();
            else
                Dispatcher.UIThread.Post(DoRefresh);
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
        /// Creates a secondary <see cref="MatrixPlotter"/> for <paramref name="data"/>, registers it
        /// as a dashboard child of this plotter, and marks it as secondary (no save confirmation on close).
        /// When <paramref name="linkRefresh"/> is <c>true</c> (default), establishes a bidirectional
        /// <see cref="Refresh"/> link so that refreshing either window propagates to the other —
        /// use this when both windows share the same underlying <c>T[]</c> frame data.
        /// Set <paramref name="linkRefresh"/> to <c>false</c> when the child manages its own update
        /// logic (e.g. filter sync windows that recompute on demand).
        /// The caller is responsible for calling <see cref="Window.Show"/>.
        /// </summary>
        public MatrixPlotter CreateLinked(
            IMatrixData data,
            LookupTable? lut = null,
            string? title = null,
            bool linkRefresh = true)
        {
            var child = Create(data, lut ?? _view.Lut, title);
            child.IsSecondaryWindow = true;
            PlotWindowNotifier.SetParentLink(child, this);
            if (linkRefresh) LinkRefresh(child);
            return child;
        }

        private void OnLinkedChildRefreshed(object? sender, EventArgs e) => Refresh();
        private void OnLinkedParentRefreshed(object? sender, EventArgs e) => Refresh();

        private void OnLinkedChildClosed(object? sender, EventArgs e)
        {
            if (sender is MatrixPlotter child)
                UnlinkRefresh(child);
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
            // Clear LinkedSource back-reference so children don't hold stale parent refs
            foreach (var child in _linkedChildren.ToArray())
            {
                if (child.LinkedSource == this)
                {
                    child.LinkedSource = null;
                    child.LinkedSourceExcludedAxes = null;
                }
            }
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
                        case nameof(vm.SourcePath):
                            if (vm.SourcePath != null) { _neverSaved = false; }
                            IsModifiedChanged?.Invoke(this, EventArgs.Empty);
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
            using var _ = _reentrancy.Begin(GuardContext.Initializing);

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

            _view.FrameIndex = 0;
            _view.MatrixData = data;
            _view.FrameIndex = data?.ActiveIndex ?? 0;

            MatrixDataChanged?.Invoke(this, data);

            // Sync multi-frame UI first so SetMode sees the correct _isMultiFrame state
            bool isMultiFrame = data is { FrameCount: > 1 };
            _rangeBar.SetMultiFrame(isMultiFrame);
            _view.ExtractFrameAllowed = isMultiFrame;
            _orthoPanel.BottomView.ExtractFrameAllowed = isMultiFrame;
            _orthoPanel.RightView.ExtractFrameAllowed = isMultiFrame;
            bool isHyperstack = data is { Axes.Count: > 1 };
            _view.ExtractDimensionAllowed = isHyperstack;

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
                CaptureLutVrSnapshot();
                CaptureScaleSnapshot(data);
                ClearAllDirty();
                if (data != null && SourcePath == null) { _neverSaved = true; IsModifiedChanged?.Invoke(this, EventArgs.Empty); }
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
                // Current mode: update range bar to reflect the new frame's range
                else if (_rangeBar.Mode == ValueRangeMode.Current)
                {
                    var (min, max) = data.GetValueRange(data.ActiveIndex);
                    using (_reentrancy.Begin(GuardContext.SyncApply))
                        _rangeBar.SetRange(min, max);
                }

                // Update histogram when frame changes (if settings panel is open)
                if (_settingsPanel?.IsVisible == true && _histogramPlot != null)
                    UpdateHistogram();
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
                    if (axis.Name.Equals("Time", StringComparison.OrdinalIgnoreCase) && axis.Step > 0)
                    {
                        //Special case for time axis: convert step to milliseconds and set AnimationInterval
                        string unit = axis.Unit.ToLowerInvariant();
                        int ms = unit switch
                        {
                            "s" => (int)Math.Round(axis.Step * 1000.0),
                            "ms" => (int)Math.Round(axis.Step),
                            _ => 0
                        };
                        if (ms > 0)
                            tracker.AnimationInterval = ms;
                    }


                    _trackerPanel.Children.Add(tracker);
                    _axisTrackers[axis.Name] = tracker;
                    WireFreezeButton(tracker, axis);
                    var capturedAxis = axis;
                    tracker.IndexChanged += (_, idx) =>
                    {
                        if (!_reentrancy.IsActive(GuardContext.SyncApply)) SyncAxisIndexChanged?.Invoke(this, (capturedAxis.Name, idx));
                    };
                    tracker.SliderDragStarted += (_, _) => UpdateAxisDragOverlay(tracker);
                    tracker.IndexChanged += (_, _) =>
                    {
                        if (_isDraggingAxisTracker) UpdateAxisDragOverlay(tracker);
                    };
                    tracker.SliderDragEnded += (_, _) =>
                    {
                        _isDraggingAxisTracker = false;
                        _view.OverlayInfoText = null;
                        _view.OverlayInfoTextAnchor = OverlayInfoTextAnchor.BottomLeft;
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

            CaptureLutVrSnapshot();
            CaptureScaleSnapshot(data);
            ClearAllDirty();
            if (SourcePath == null) { _neverSaved = true; IsModifiedChanged?.Invoke(this, EventArgs.Empty); }
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
                    if (!_reentrancy.IsActive(GuardContext.SyncApply)) SyncAxisIndexChanged?.Invoke(this, (capturedAxis.Name, idx));
                };
            }
            _trackerPanel.IsVisible = data.Axes.Count > 0;
        }


        /// <summary>
        /// Updates the animation interval for the Time axis based on its Step and Unit.
        /// </summary>
        /// <param name="axis">The axis to update. If null, the Time axis from the current data is used.</param>
        /// <param name="showNotice">Whether to show a notice with the updated interval.</param>
        /// <returns>True if the interval was updated; otherwise, false.</returns>
        private bool TryUpdateTimeAxisAnimationInterval(Axis? axis = null, bool showNotice = false)
        {
            axis ??= _currentData?.Axes.FirstOrDefault(a =>
                a.Name.Equals("Time", StringComparison.OrdinalIgnoreCase));

            if (axis == null || axis.Step <= 0)
                return false;

            if (!axis.Name.Equals("Time", StringComparison.OrdinalIgnoreCase))
                return false;

            bool isSeconds = axis.Unit.Equals("s", StringComparison.OrdinalIgnoreCase);
            bool isMilliseconds = axis.Unit.Equals("ms", StringComparison.OrdinalIgnoreCase);
            if (!isSeconds && !isMilliseconds)
                return false;

            int intervalMs = isSeconds
                ? (int)Math.Round(axis.Step * 1000.0)
                : (int)Math.Round(axis.Step);

            if (intervalMs <= 0)
                return false;

            if (!_axisTrackers.TryGetValue(axis.Name, out var tracker))
                return false;

            if (tracker.AnimationInterval == intervalMs)
                return false;

            tracker.AnimationInterval = intervalMs;
            if (showNotice)
                ShowToast($"Animation interval: {intervalMs} ms");
            return true;
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

            // For Complex data: use the current display mode; for primitives: always mode 0
            int valueMode = data.ValueType == typeof(System.Numerics.Complex)
                ? (int)_view.ComplexValueMode : 0;

            int invalidCount = 0;
            if (data.IsVirtual)
            {
                if (valueMode == 0)
                {
                    // Non-Complex or Magnitude mode: use interface method
                    (min, max) = data.GetGlobalValueRange(out var invalids, false);
                    invalidCount = invalids.Count;
                }
                else
                {
                    // Complex with non-Magnitude mode: use typed extension
                    var complexData = (MatrixData<System.Numerics.Complex>)data;
                    (min, max) = complexData.GetGlobalValueRange(valueMode, out var invalids, false);
                    invalidCount = invalids.Count;
                }
                imperfect = invalidCount > 0;
            }
            else
            {
                // InMemory: synchronous full scan; subsequent calls are fast due to caching
                if (valueMode == 0)
                {
                    (min, max) = data.GetGlobalValueRange(out _, true);
                }
                else
                {
                    var complexData = (MatrixData<System.Numerics.Complex>)data;
                    (min, max) = complexData.GetGlobalValueRange(valueMode, out _, true);
                }
                imperfect = false;
            }

            if (!double.IsNaN(min))
            {
                _view.FixedMin = min;
                _view.FixedMax = max;
                _rangeBar.SetRange(min, max);
            }
            _rangeBar.SetImperfect(imperfect, invalidCount);
        }

        /// <summary>
        /// Updates the histogram control with the current frame's data and LUT.
        /// Called when the settings panel is opened or when LUT/level changes while the panel is visible.
        /// Runs histogram calculation on a background thread to avoid blocking the UI.
        /// Mode-specific behavior:
        /// - Fixed: preserves plot zoom, uses valueRange for badge base (1x = plotRange matches valueRange).
        ///   Range bar changes only update viewRange (red lines), not plotRange.
        /// - Roi: preserves plot zoom, uses valueRange for badge base. viewRange (red lines) follows ROI statistics.
        ///   ROI movement only updates viewRange, not plotRange. Behaves like Fixed mode for histogram.
        /// - Current/Auto: resets plot zoom to 1:1 with valueRange, uses valueRange for badge base.
        /// - All: resets plot zoom to 1:1 with viewRange, uses viewRange for badge base (1x = plotRange matches viewRange from range bar).
        /// </summary>
        private async void UpdateHistogram()
        {
            if (_histogramPlot == null || _currentData == null) return;

            // Cancel any pending histogram calculation
            _histogramCts?.Cancel();
            _histogramCts = new CancellationTokenSource();
            var cts = _histogramCts;

            var lut = _view.Lut;
            int level = _view.LutDepth;
            bool isInverted = _view.IsInvertedColor;

            // Guard against invalid LUT depth
            if (level <= 0)
            {
                Debug.WriteLine($"[UpdateHistogram] Invalid LutDepth: {level}");
                return;
            }

            // Resample LUT if necessary, then get ARGB array
            var effectiveLut = (lut != null && lut.Levels != level) ? lut.Resample(level) : lut;
            var aRgbs = effectiveLut?.AsSpan().ToArray() ?? new int[level];

            // Apply inversion to the LUT array for histogram display
            if (isInverted)
                Array.Reverse(aRgbs);

            try
            {
                // Determine the view range (red lines / LUT application range)
                // Read from _rangeBar which is the authoritative source for all modes
                double viewMin, viewMax;
                if (_rangeBar.Mode == ValueRangeMode.Fixed)
                {
                    viewMin = _view.FixedMin;
                    viewMax = _view.FixedMax;
                }
                else
                {
                    // For All/Current/Roi: _rangeBar has already been updated with the correct range
                    // Avoid redundant ScanCurrentFrameRange() calls
                    viewMin = _rangeBar.DisplayedMinValue;
                    viewMax = _rangeBar.DisplayedMaxValue;
                    Debug.WriteLine($"[UpdateHistogram] {_rangeBar.Mode} mode: viewMin={viewMin:F3}, viewMax={viewMax:F3} (from _rangeBar)");
                }

                int frameIndex = _currentData.ActiveIndex;
                var data = _currentData;  // Capture for background thread
                var complexValueMode = _view.ComplexValueMode;
                int valueMode = (int)complexValueMode;

                // Calculate bin count parameters on UI thread
                double viewRange = Math.Max(1e-9, viewMax - viewMin);

                // Run heavy computation on background thread
                var (valueMin, valueMax, histogram) = await Task.Run(() =>
                {
                    if (cts.Token.IsCancellationRequested) 
                        return (double.NaN, double.NaN, Array.Empty<int>());

                    // Determine the histogram calculation range (actual data range)
                    double vMin, vMax;
                    if (data.ValueType == typeof(System.Numerics.Complex))
                        (vMin, vMax) = data.GetValueRange(frameIndex, valueMode);
                    else
                        (vMin, vMax) = data.GetValueRange(frameIndex);

                    if (cts.Token.IsCancellationRequested) 
                        return (double.NaN, double.NaN, Array.Empty<int>());

                    // Calculate bin count: scale bins proportionally to the range ratio.
                    double dataRange = Math.Max(1e-9, vMax - vMin);
                    int bins = (int)Math.Ceiling(level * (dataRange / viewRange));
                    bins = Math.Clamp(bins, 3, 4096);

                    // Create histogram
                    int[] hist;
                    if (data.ValueType == typeof(System.Numerics.Complex))
                    {
                        var complexData = (MatrixData<System.Numerics.Complex>)data;

                        Func<System.Numerics.Complex, double> converter = complexValueMode switch
                        {
                            ComplexValueMode.Magnitude => c => c.Magnitude,
                            ComplexValueMode.Real => c => c.Real,
                            ComplexValueMode.Imaginary => c => c.Imaginary,
                            ComplexValueMode.Phase => c => Math.Atan2(c.Imaginary, c.Real),
                            ComplexValueMode.Power => c => c.Real * c.Real + c.Imaginary * c.Imaginary,
                            _ => c => c.Magnitude
                        };

                        hist = complexData.CreateHistogram(frameIndex, bins, valueMode, converter, vMin, vMax);
                    }
                    else
                    {
                        hist = data.CreateHistogram(frameIndex, bins, vMin, vMax);
                    }

                    return (vMin, vMax, hist);
                }, cts.Token);

                // Check if cancelled
                if (cts.Token.IsCancellationRequested || double.IsNaN(valueMin))
                    return;

                // Update UI on UI thread
                // Fixed/Roi mode: preserves plot zoom (user/ROI-controlled window), viewRange follows LUT range
                // Current/All: reset plot to 1:1 with valueRange/viewRange (data-driven window)
                bool preservePlot = _rangeBar.Mode == ValueRangeMode.Fixed || _rangeBar.Mode == ValueRangeMode.Roi;
                bool useViewRangeAsBase = _rangeBar.Mode == ValueRangeMode.All;
                Debug.WriteLine($"[UpdateHistogram] Before SetHistogram: " +
                    $"valueMin={valueMin:F3}, valueMax={valueMax:F3}, " +
                    $"viewMin={viewMin:F3}, viewMax={viewMax:F3}, " +
                    $"preservePlot={preservePlot}, useViewRangeAsBase={useViewRangeAsBase}");
                _histogramPlot.SetHistogram(histogram, valueMin, valueMax, viewMin, viewMax, aRgbs, preservePlot, useViewRangeAsBase);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateHistogram] Error: {ex.Message}");
                Debug.WriteLine($"  LutDepth={level}, FrameIndex={_currentData.ActiveIndex}, DataType={_currentData.ValueType.Name}");
            }
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

        /// <summary>
        /// Displays a transient toast message in the status bar notice area.
        /// </summary>
        /// <param name="message"></param>
        private async void ShowToast(string message)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ShowToast(message));
                return;
            }

            _toastCts?.Cancel();
            _toastCts = new CancellationTokenSource();
            var token = _toastCts.Token;

            if (_progressBar.IsVisible) return;

            _toastText.Text = message;
            _toastPanel.IsVisible = true;
            _toastPanel.Opacity = 0;

            var tt = _toastPanel.RenderTransform as TranslateTransform ?? new TranslateTransform(0, 12);
            _toastPanel.RenderTransform = tt;
            tt.Y = 12;

            // slide-in + fade-in (180ms)
            for (int i = 1; i <= 12; i++)
            {
                if (token.IsCancellationRequested) return;
                double t = i / 12.0;
                _toastPanel.Opacity = t;
                tt.Y = (1.0 - t) * 12.0;
                await Task.Delay(15, CancellationToken.None);
            }

            // hold
            try { await Task.Delay(2800, token); }
            catch (OperationCanceledException) { return; }

            // slide-out + fade-out (240ms)
            for (int i = 8; i >= 0; i--)
            {
                if (token.IsCancellationRequested) return;
                double t = i / 8.0;
                _toastPanel.Opacity = t;
                tt.Y = (1.0 - t) * 8.0;
                await Task.Delay(30, CancellationToken.None);
            }

            _toastPanel.IsVisible = false;
            _toastPanel.Opacity = 0;
            tt.Y = 12;
        }

        /*
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
        */



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

            string typeLabel = data.ValueTypeName;
            if (data.ValueType == typeof(System.Numerics.Complex))
            {
                string modeLabel = _view.ComplexValueMode switch
                {
                    ComplexValueMode.Magnitude => "Mag",
                    ComplexValueMode.Real => "Real",
                    ComplexValueMode.Imaginary => "Imag",
                    ComplexValueMode.Phase => "Phase",
                    ComplexValueMode.Power => "Power",
                    _ => _view.ComplexValueMode.ToString(),
                };
                typeLabel = $"Complex ({modeLabel})";
            }

            _infoText.Text = $"[{typeLabel}]  {sizeStr}";
            string tooltip = $"{data.XCount}×{data.YCount}";
            if (data.FrameCount > 1) tooltip += $"  |  {data.FrameCount} frames";
            tooltip += $"  |  {data.ValueTypeName}";
            if (data.ValueType == typeof(System.Numerics.Complex))
                tooltip += $"  |  Mode: {_view.ComplexValueMode}";
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
            string zoomTip = _view.IsFitToView
                ? "Fit to view (double-click image to toggle)"
                : $"{_view.Zoom * 100:0.##}% (double-click image to fit)";
            ToolTip.SetTip(_zoomText, zoomTip + "\nClick here to set zoom / view size");
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
