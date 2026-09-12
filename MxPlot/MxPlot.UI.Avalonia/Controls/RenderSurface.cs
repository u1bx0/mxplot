using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Rendering;
using MxPlot.UI.Avalonia.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Anchor corner for <see cref="RenderSurface.OverlayInfoText"/> display.
    /// </summary>
    public enum OverlayInfoTextAnchor
    {
        /// <summary>Bottom-left corner (default, same position as the mouse coordinate overlay).</summary>
        BottomLeft,
        /// <summary>Top-right corner.</summary>
        TopRight,
    }

    /// <summary>
    /// Core rendering surface for <see cref="IMatrixData"/> display.
    /// Handles bitmap generation via <see cref="LutBitmapWriter"/> or 
    /// <see cref="CompositeBitmapWriter"/> implemeting <see cref="IBitmapWriter"/>,  and manages
    /// pan (left-button drag) and zoom (mouse wheel) interactions.
    /// </summary>
    /// <remarks>
    /// Coordinate model:
    ///   screen = data * Zoom + (TransX, TransY)
    ///   data   = (screen - (TransX, TransY)) / Zoom
    /// </remarks>
    internal sealed class RenderSurface : Control
    {
        public RenderSurface()
        {
            Focusable = true;
        }

        // ── Zoom level table (same as WinForms MxView) ───────────────────────
        private static readonly double[] ZoomLevels =
        {
            0.01, 0.05, 0.1, 0.15, 0.25, 0.35, 0.5, 0.65, 0.75, 0.9,
            1.0,
            1.25, 1.5, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0, 16.0, 24.0, 32.0, 64.0
        };

        /// <summary>
        /// Minimum screen pixels between the image edge and the viewport edge while panning.
        /// When the image fits inside the viewport it is centered regardless of this value.
        /// </summary>
        public double PanMargin { get; set; } = 20.0;

        /// <summary>
        /// Padding (in DIPs) added around the bitmap when fitting to the viewport.
        /// The bitmap is scaled so that this many pixels remain on each side.
        /// Defaults to 3. Set to 0 to fit edge-to-edge.
        /// </summary>
        public double BitmapPadding { get; set; } = 3.0;

        // ── State ────────────────────────────────────────────────────────────
        private WriteableBitmap? _bitmap;

        // Memoized palette-depth resample - see ResolveEffectiveLut.
        private LookupTable? _resampleSource;
        private LookupTable? _resampledLut;
        private int _resampleDepth;
        private IBitmapWriter? _writer;
#if DEBUG
        // Debug-only render timing - see RefreshBitmap's own comment and DrawRenderDiagnosticsOverlay.
        private readonly System.Diagnostics.Stopwatch _writerRenderStopwatch = new();
        private double _lastWriterRenderMs;
#endif
        private bool _refreshing;         // a render is in flight; see RefreshBitmap
        private bool _refreshRequested;   // a nested request arrived while it was
        private EventHandler? _scaleChangedHandler;
        private double _zoom = 1.0;
        private double _transX = 0.0;
        private double _transY = 0.0;
        private bool _isFitToView = true;   // auto-scale to viewport; cleared on explicit zoom
        private bool _isPanning;
        private Point _panStart;
        private bool _pendingFitToView;
        private const double DragLimitRatio = 0.15;
        private DispatcherTimer? _snapTimer;
        private bool _isPointerInside;
        private Point _lastScreenPos;
        private KeyModifiers _lastPointerModifiers;
        private double _zoomDeltaAccum;

        // ── Crosshair ────────────────────────────────────────────────────────
        private enum CrosshairHit { None, HLine, VLine, Cross }
        private bool _isDraggingCrosshair;
        private bool _isDraggingH;   // horizontal line (Y) is being moved
        private bool _isDraggingV;   // vertical line (X) is being moved
        private Point? _crosshairDataPos;
        private const double CrosshairHitTolerance = 8.0;

        // ── ComplexValueMode ─────────────────────────────────────────────────
        private ComplexValueMode _complexValueMode = ComplexValueMode.Magnitude;

        // ── Axis indicator drag ──────────────────────────────────────────────
        private bool _isDraggingAxisIndicator;
        private const double AxisIndicatorHitTolerance = 6.0;

        /// <summary>Fired when the user drags the axis indicator to a new bitmap-row index.</summary>
        public event EventHandler<int>? AxisIndicatorDragged;

        /// <summary>Fired when the user releases the axis indicator after dragging.</summary>
        public event EventHandler? AxisIndicatorDragEnded;

        /// <summary>True while the user is actively dragging the axis indicator.</summary>
        public bool IsAxisIndicatorDragging => _isDraggingAxisIndicator;

        /// <summary>When true, draws a draggable crosshair overlay.</summary>
        public bool ShowCrosshair { get; set; }

        /// <summary>When false, hides the horizontal crosshair line (Y-position indicator).</summary>
        public bool ShowCrosshairH { get; set; } = true;

        /// <summary>When false, hides the vertical crosshair line (X-position indicator).</summary>
        public bool ShowCrosshairV { get; set; } = true;

        // ── Aspect ratio correction ───────────────────────────────────────────
        private bool _isAspectCorrectionEnabled = true;

        /// <summary>
        /// When true, pixels are stretched proportionally to their physical step sizes
        /// (XStep / YStep) so the display matches real-world aspect ratios.
        /// </summary>
        public bool IsAspectCorrectionEnabled
        {
            get => _isAspectCorrectionEnabled;
            set
            {
                if (_isAspectCorrectionEnabled == value) return;
                _isAspectCorrectionEnabled = value;
                if (_isFitToView && _bitmap != null) FitToView();
                else { ClampTranslation(); InvalidateVisual(); }
            }
        }

        /// <summary>Fired when the user drags the crosshair to a new data-space position.</summary>
        public event EventHandler<Point>? CrosshairMoved;

        /// <summary>Moves the crosshair to <paramref name="dataPos"/> (data-coordinate space) and redraws.</summary>
        public void SetCrosshairDataPosition(Point dataPos)
        {
            _crosshairDataPos = new Point(SnapDataX(dataPos.X), SnapDataY(dataPos.Y));
            InvalidateVisual();
        }

        /// <summary>
        /// When true, the bitmap is rendered with Y-axis flipped (YMax at screen top, YMin at bottom).
        /// Defaults to true. Set to false for orthogonal side views whose ViewTransform handles orientation.
        /// </summary>
        public bool FlipY { get; set; } = true;

        /// <summary>
        /// How much of the machine the bitmap writer's pixel loop may use.
        /// <see cref="ParallelismAuto"/> (the default) leaves the writer to decide from the frame
        /// size; <c>0</c> keeps the loop on one thread; <c>-1</c> is unrestricted; a positive
        /// value caps the degree of parallelism.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Shaped after <c>MatrixDataPlotter.Parallelism</c> in the MatrixDataPlot library, whose
        /// 0 / -1 / positive encoding consumers already know, with Auto added for the size-based
        /// default this library has and that one did not.
        /// </para>
        /// <para>
        /// A positive cap still goes through the writer's size threshold, so capping how many
        /// cores a large frame may use does not also force a tiny frame to go parallel. Measured
        /// on a 4096x4096 LUT frame (32 logical processors): 8 threads reach 77% of the best time
        /// while leaving three quarters of the machine to whatever else the host is doing - an
        /// acquisition or processing thread that matters more than redraw latency.
        /// </para>
        /// <para>
        /// Handed to the writer alongside <see cref="FlipY"/> on every allocation pass, so a
        /// change takes effect from the next render.
        /// </para>
        /// </remarks>
        public int Parallelism
        {
            get => _parallelism;
            set
            {
                if (value < ParallelismAuto)
                    throw new ArgumentOutOfRangeException(
                        nameof(value), value, "Parallelism must be -2 (Auto), -1, 0, or positive.");
                if (_parallelism == value) return;
                _parallelism = value;
                // Rebuilt here rather than per render: the options object is immutable in practice
                // and handing the same instance to the writer every pass keeps it allocation-free.
                _parallelismOptions = value > 0
                    ? new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = value }
                    : null;
            }
        }

        /// <summary>Sentinel for <see cref="Parallelism"/>: let the writer decide from frame size.</summary>
        public const int ParallelismAuto = -2;

        private int _parallelism = ParallelismAuto;
        private System.Threading.Tasks.ParallelOptions? _parallelismOptions;

        private MxPlot.UI.Avalonia.Rendering.ParallelRenderPolicy ParallelPolicyFromParallelism => _parallelism switch
        {
            0 => MxPlot.UI.Avalonia.Rendering.ParallelRenderPolicy.Never,
            -1 => MxPlot.UI.Avalonia.Rendering.ParallelRenderPolicy.Always,
            _ => MxPlot.UI.Avalonia.Rendering.ParallelRenderPolicy.Auto,   // Auto, and any positive cap
        };

        /// <summary>When true, draws the current data-pixel coordinates at the top-left corner.</summary>
        public bool ShowDataPosition { get; set; } = true;

        /// <summary>
        /// When set, <see cref="DrawPositionOverlay"/> shows this text instead of the mouse coordinate/value.
        /// Set by <see cref="MxView"/> when an overlay object is selected.
        /// </summary>
        public string? OverlayInfoText { get; set; }

        /// <summary>
        /// Corner where <see cref="OverlayInfoText"/> is anchored when it is non-<c>null</c>.
        /// Has no effect on the normal mouse-coordinate display.
        /// </summary>
        public OverlayInfoTextAnchor OverlayInfoTextAnchor { get; set; } = OverlayInfoTextAnchor.BottomLeft;

        /// <summary>Visual transform applied to the rendered bitmap.</summary>
        public ViewTransform Transform { get; set; } = ViewTransform.None;

        /// <summary>
        /// Controls where the bitmap is pinned when it is smaller than the viewport.
        /// Defaults to <see cref="ContentAlignment.Center"/>.
        /// Triggers <see cref="FitToView"/> (if active) or <see cref="ClampTranslation"/> on change.
        /// </summary>
        private ContentAlignment _contentAlignment = ContentAlignment.Center;
        public ContentAlignment ContentAlignment
        {
            get => _contentAlignment;
            set
            {
                if (_contentAlignment == value) return;
                _contentAlignment = value;
                if (_isFitToView && _bitmap != null) FitToView();
                else { ClampTranslation(); InvalidateVisual(); }
            }
        }

        // Returns the screen-space X origin for a fitting image given the current ContentAlignment.
        private double AlignedTransX(double bmpW, double vpW) => _contentAlignment switch
        {
            ContentAlignment.TopLeft or ContentAlignment.Left or ContentAlignment.BottomLeft => PanMargin,
            ContentAlignment.TopRight or ContentAlignment.Right or ContentAlignment.BottomRight => Math.Max(0.0, vpW - bmpW - PanMargin),
            _ => (vpW - bmpW) / 2.0,
        };
        // Returns the screen-space Y origin for a fitting image given the current ContentAlignment.
        private double AlignedTransY(double bmpH, double vpH) => _contentAlignment switch
        {
            ContentAlignment.TopLeft or ContentAlignment.Top or ContentAlignment.TopRight => PanMargin,
            ContentAlignment.BottomLeft or ContentAlignment.Bottom or ContentAlignment.BottomRight => Math.Max(0.0, vpH - bmpH - PanMargin),
            _ => (vpH - bmpH) / 2.0,
        };

        /// <summary>
        /// When set, draws a horizontal indicator line (black 3 px + white 1 px) across the
        /// full effective display width at the given bitmap-pixel row.
        /// Subject to <see cref="Transform"/>: e.g. after <see cref="ViewTransform.Transpose"/>
        /// the row index maps to the column axis, so the line appears vertical on screen.
        /// </summary>
        public int? AxisIndicatorPx { get; set; }

        /// <summary>
        /// When set, draws a text label near the axis indicator line.
        /// Typically provides the axis name and physical value, e.g. <c>"Z=1.250 mm"</c>.
        /// </summary>
        public string? AxisIndicatorLabel { get; set; }

        /// <summary>Fired when zoom, translation, or bitmap size changes, so <see cref="MxView"/> can sync scrollbars.</summary>
        public event EventHandler? ScrollStateChanged;

        /// <summary>
        /// Fired on the UI thread immediately after the rendered bitmap pixels are updated.
        /// Acts as a single chokepoint for all display changes (LUT, frame, value range, Refresh).
        /// Forwarded via <see cref="MxView.BitmapRefreshed"/> → <see cref="MatrixPlotter.ViewUpdated"/>
        /// to allow external consumers (e.g. dashboard thumbnails) to react without
        /// enumerating every individual trigger point in <see cref="MatrixPlotter"/>.
        /// </summary>
        internal event EventHandler? BitmapRefreshed;

        // ── Avalonia Styled Properties ────────────────────────────────────────

        public static readonly StyledProperty<IMatrixData?> MatrixDataProperty =
            AvaloniaProperty.Register<RenderSurface, IMatrixData?>(nameof(MatrixData));

        public static readonly StyledProperty<LookupTable?> LutProperty =
            AvaloniaProperty.Register<RenderSurface, LookupTable?>(nameof(Lut));

        public static readonly StyledProperty<int> FrameIndexProperty =
            AvaloniaProperty.Register<RenderSurface, int>(nameof(FrameIndex), defaultValue: 0);

        public static readonly StyledProperty<IBrush> BackgroundBrushProperty =
            AvaloniaProperty.Register<RenderSurface, IBrush>(nameof(BackgroundBrush), defaultValue: (IBrush)Brushes.DimGray);

        public static readonly StyledProperty<bool> IsFixedRangeProperty =
            AvaloniaProperty.Register<RenderSurface, bool>(nameof(IsFixedRange));
        public static readonly StyledProperty<double> FixedMinProperty =
            AvaloniaProperty.Register<RenderSurface, double>(nameof(FixedMin));
        public static readonly StyledProperty<double> FixedMaxProperty =
            AvaloniaProperty.Register<RenderSurface, double>(nameof(FixedMax), defaultValue: 1.0);
        public static readonly StyledProperty<bool> IsInvertedColorProperty =
            AvaloniaProperty.Register<RenderSurface, bool>(nameof(IsInvertedColor));
        public static readonly StyledProperty<int> LutLevelProperty =
            // 0 = use the LUT's own level count. MxView pushes its own LutDepth default in at
            // construction, so in practice the surface starts at 256, not at this sentinel.
            AvaloniaProperty.Register<RenderSurface, int>(nameof(LutLevel));

        public static readonly StyledProperty<RenderingMode> RenderingModeProperty =
            AvaloniaProperty.Register<RenderSurface, RenderingMode>(
                nameof(RenderingMode), defaultValue: RenderingMode.Lut);

        public static readonly StyledProperty<IReadOnlyList<BlendRecipe>?> CompositeRecipesProperty =
            AvaloniaProperty.Register<RenderSurface, IReadOnlyList<BlendRecipe>?>(
                nameof(CompositeRecipes));

        public static readonly StyledProperty<BlendMode> CompositeBlendModeProperty =
            AvaloniaProperty.Register<RenderSurface, BlendMode>(
                nameof(CompositeBlendMode), defaultValue: BlendMode.Additive);

        public static readonly StyledProperty<int[]?> CompositeFrameIndicesProperty =
            AvaloniaProperty.Register<RenderSurface, int[]?>(
                nameof(CompositeFrameIndices));

        public static readonly StyledProperty<ColorCodedRenderInfo?> ColorCodedInfoProperty =
            AvaloniaProperty.Register<RenderSurface, ColorCodedRenderInfo?>(
                nameof(ColorCodedInfo));

        public IMatrixData? MatrixData
        {
            get => GetValue(MatrixDataProperty);
            set => SetValue(MatrixDataProperty, value);
        }

        public LookupTable? Lut
        {
            get => GetValue(LutProperty);
            set => SetValue(LutProperty, value);
        }

        public int FrameIndex
        {
            get => GetValue(FrameIndexProperty);
            set => SetValue(FrameIndexProperty, value);
        }

        public IBrush BackgroundBrush
        {
            get => GetValue(BackgroundBrushProperty);
            set => SetValue(BackgroundBrushProperty, value);
        }

        public bool IsFixedRange { get => GetValue(IsFixedRangeProperty); set => SetValue(IsFixedRangeProperty, value); }
        public double FixedMin { get => GetValue(FixedMinProperty); set => SetValue(FixedMinProperty, value); }
        public double FixedMax { get => GetValue(FixedMaxProperty); set => SetValue(FixedMaxProperty, value); }
        public bool IsInvertedColor { get => GetValue(IsInvertedColorProperty); set => SetValue(IsInvertedColorProperty, value); }
        public int LutLevel { get => GetValue(LutLevelProperty); set => SetValue(LutLevelProperty, value); }

        public RenderingMode RenderingMode
        {
            get => GetValue(RenderingModeProperty);
            set => SetValue(RenderingModeProperty, value);
        }
        public IReadOnlyList<BlendRecipe>? CompositeRecipes
        {
            get => GetValue(CompositeRecipesProperty);
            set => SetValue(CompositeRecipesProperty, value);
        }
        public BlendMode CompositeBlendMode
        {
            get => GetValue(CompositeBlendModeProperty);
            set => SetValue(CompositeBlendModeProperty, value);
        }
        public int[]? CompositeFrameIndices
        {
            get => GetValue(CompositeFrameIndicesProperty);
            set => SetValue(CompositeFrameIndicesProperty, value);
        }
        public ColorCodedRenderInfo? ColorCodedInfo
        {
            get => GetValue(ColorCodedInfoProperty);
            set => SetValue(ColorCodedInfoProperty, value);
        }

        /// <summary>
        /// Gets or sets the display mode for Complex-valued matrix data.
        /// Changing this property triggers a full bitmap rebuild when
        /// the current MatrixData is of type MatrixData&lt;Complex&gt;.
        /// Has no effect on non-Complex data types.
        /// </summary>
        public ComplexValueMode ComplexValueMode
        {
            get => _complexValueMode;
            set
            {
                if (_complexValueMode == value) return;
                _complexValueMode = value;
                if (MatrixData?.ValueType == typeof(Complex))
                {
                    if (_writer != null)
                        _writer.StructValueConverter = GetComplexConverter();
                    RebuildAndInvalidate();
                }
            }
        }

        /// <summary>Fired (on the UI thread) each time the auto value range is computed. Carries (Min, Max).</summary>
        public event EventHandler<(double Min, double Max)>? AutoRangeComputed;

        /// <summary>Returns the computed value range for the current frame (seed for Fixed mode or ?? buttons).</summary>
        public (double Min, double Max) ScanCurrentFrameRange() =>
            MatrixData != null ? MatrixData.GetValueRange(FrameIndex) : (0, 1);

        /// <summary>
        /// Returns the computed value range for the current frame using the specified value mode.
        /// For Complex data, use the appropriate mode index; for primitives, valueMode is ignored.
        /// </summary>
        internal (double Min, double Max) ScanCurrentFrameRange(int valueMode) =>
            MatrixData != null ? MatrixData.GetValueRange(FrameIndex, valueMode) : (0, 1);


        // ── Overlay support ───────────────────────────────────────────────────

        /// <summary>
        /// When set, overlay objects are drawn in <see cref="Render"/> and pointer events
        /// are forwarded before the built-in AxisIndicator / Crosshair / Pan handling.
        /// </summary>
        internal OverlayManager? OverlayManager { get; set; }

        /// <summary>
        /// Builds an <see cref="AvaloniaViewport"/> snapshot from the current display state,
        /// using the elastic-corrected render translation so overlays align with the bitmap.
        /// </summary>
        internal AvaloniaViewport GetOverlayViewport()
        {
            var (ax, ay) = GetAspectScales();
            var (effW, effH) = GetEffectiveBmpDims();
            double rx = GetRenderTrans(_transX, effW, Bounds.Width, true);
            double ry = GetRenderTrans(_transY, effH, Bounds.Height, false);
            return new AvaloniaViewport
            {
                Zoom = _zoom,
                TransX = rx,
                TransY = ry,
                Ax = ax,
                Ay = ay,
                EffW = effW,
                EffH = effH,
                Transform = Transform,
            };
        }

        // ── Exposed for MxView scrollbar sync ────────────────────────────────
        public WriteableBitmap? Bitmap => _bitmap;

        /// <summary>
        /// Right-edge inset in pixels to avoid overlapping the vertical scrollbar when it is visible.
        /// Set by <see cref="MxView"/> whenever the vertical scrollbar visibility changes.
        /// Affects the clamped X position of right-aligned labels (y= crosshair, Z= axis indicator).
        /// </summary>
        internal double RightInset { get; set; }
        public double Zoom => _zoom;
        public bool IsFitToView => _isFitToView;
        public double TransX => ClampedTrans(_transX, GetEffectiveBmpDims().w, Bounds.Width, true);
        public double TransY => ClampedTrans(_transY, GetEffectiveBmpDims().h, Bounds.Height, false);
        /// <summary>Raw horizontal translation before clamping (used for cross-view sync).</summary>
        internal double RawTransX => _transX;
        /// <summary>Raw vertical translation before clamping (used for cross-view sync).</summary>
        internal double RawTransY => _transY;

        /// <summary>
        /// Applies zoom and translation directly for cross-view sync.
        /// Exits fit-to-view mode. Does NOT fire <see cref="ScrollStateChanged"/> to avoid loops.
        /// </summary>
        internal void ApplyZoomAndTrans(double zoom, double tx, double ty)
        {
            _isFitToView = false;
            _zoom = zoom;
            _transX = tx;
            _transY = ty;
            ClampTranslation();
            InvalidateVisual();
        }

        // ── Static constructor: class-level property observers ────────────────
        static RenderSurface()
        {
            MatrixDataProperty.Changed.AddClassHandler<RenderSurface>(
                (s, e) => s.OnMatrixDataChanged(e.OldValue as IMatrixData));
            LutProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            FrameIndexProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            IsFixedRangeProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            FixedMinProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            FixedMaxProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            IsInvertedColorProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            LutLevelProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            RenderingModeProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            CompositeRecipesProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            CompositeBlendModeProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            CompositeFrameIndicesProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            ColorCodedInfoProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.RebuildAndInvalidate());
            // Redraw debug overlay when focus changes (IsFocused is a built-in AvaloniaObject property)
            IsFocusedProperty.Changed.AddClassHandler<RenderSurface>(
                (s, _) => s.InvalidateVisual());
        }

        // ── Property change handlers ──────────────────────────────────────────

        private void OnMatrixDataChanged(IMatrixData? oldData)
        {
            // Unsubscribe from the previous dataset's scale changes
            if (oldData != null && _scaleChangedHandler != null)
            {
                oldData.ScaleChanged -= _scaleChangedHandler;
                _scaleChangedHandler = null;
            }

            // Subscribe to the new dataset's scale changes
            var newData = MatrixData;
            if (newData != null)
            {
                _scaleChangedHandler = (_, _) => OnScaleChanged();
                newData.ScaleChanged += _scaleChangedHandler;
            }

            // Remember previous bitmap dimensions before reallocation.
            var prevSize = _bitmap?.PixelSize;
            AllocateBitmap();
            RefreshBitmap();

            // Only reset the view when the bitmap dimensions actually changed or this is the
            // first load (_bitmap was null before). When the same-sized data is swapped in
            // (e.g. during an export loop where each frame replaces the slice MatrixData with
            // the same XCount×YCount), the user's zoom/pan state is preserved.
            bool dimsChanged = _bitmap == null
                ? prevSize != null
                : prevSize == null || prevSize.Value != _bitmap.PixelSize;

            if (dimsChanged)
            {
                if (Bounds.Width > 0 && Bounds.Height > 0)
                    FitToView();
                else
                    _pendingFitToView = true;
            }
            else if (_isFitToView && _bitmap != null)
            {
                // Dimensions are the same but aspect scales may differ; re-fit to stay accurate.
                if (Bounds.Width > 0 && Bounds.Height > 0)
                    FitToView();
            }
        }

        /// <summary>
        /// Called when <see cref="IMatrixData.ScaleChanged"/> fires.
        /// Pixel content is unchanged; only the aspect-correction geometry changes.
        /// Re-fits or clamps the view and notifies subscribers (e.g. thumbnail updaters)
        /// without rebuilding the bitmap.
        /// </summary>
        private void OnScaleChanged()
        {
            if (_isFitToView && _bitmap != null) FitToView();
            else { ClampTranslation(); InvalidateVisual(); }
            BitmapRefreshed?.Invoke(this, EventArgs.Empty);
        }

        internal void RebuildAndInvalidate()
        {
            // Guarded for the same reason as RefreshBitmap, and for one more: AllocateBitmap may
            // dispose and replace _bitmap, which must not happen underneath a render that is
            // already writing for it. The deferred request is picked up by the loop below.
            if (_refreshing)
            {
                _refreshRequested = true;
                InvalidateVisual();
                return;
            }

            AllocateBitmap();
            RefreshBitmap();
            InvalidateVisual();
        }

        /// <summary>
        /// Ensures <see cref="_bitmap"/> and <see cref="_writer"/> are allocated and
        /// sized correctly for the current <see cref="MatrixData"/>.
        /// Only reallocates when dimensions or value type have changed.
        /// </summary>
        private void AllocateBitmap()
        {
            var data = MatrixData;
            var mode = RenderingMode;

            if (data == null || (Lut == null && mode == RenderingMode.Lut))
            {
                _bitmap = null;
                _writer = null;
                return;
            }

            // Reallocate the pixel buffer only when size changes
            if (_bitmap == null ||
                _bitmap.PixelSize.Width != data.XCount ||
                _bitmap.PixelSize.Height != data.YCount)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(data.XCount, data.YCount),
                    new global::Avalonia.Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);
            }

            // Recreate the writer when type or mode changes
            bool needsNewWriter = _writer == null
                || _writer.ValueType != data.ValueType
                || !IsWriterCompatible(_writer, mode);

            if (needsNewWriter)
            {
                _writer = mode switch
                {
                    RenderingMode.Lut => new LutBitmapWriter(data.ValueType),
                    RenderingMode.Composite => new CompositeBitmapWriter(data.ValueType),
                    // The ColorCoded live child window's data is the winner *value* matrix itself --
                    // an ordinary projection-shaped result, same type as the source data -- combined
                    // at render time with the winner-index/depth-palette data in ColorCodedInfo.
                    // See ColorCoded_View_InitialDesign.md section 3.3.
                    RenderingMode.ColorCoded => new ColorCodedBitmapWriter(data.ValueType),
                    _ => throw new NotSupportedException($"RenderingMode {mode} is not supported.")
                };
            }

            _writer.FlipY = FlipY;
            _writer.ParallelPolicy = ParallelPolicyFromParallelism;
            _writer.ParallelOptions = _parallelismOptions;

            // Set Complex projection function when data is Complex
            if (data.ValueType == typeof(Complex))
                _writer.StructValueConverter = GetComplexConverter();
        }

        private static bool IsWriterCompatible(IBitmapWriter writer, RenderingMode mode)
            => mode switch
            {
                RenderingMode.Lut => writer is LutBitmapWriter,
                RenderingMode.Composite => writer is CompositeBitmapWriter,
                RenderingMode.ColorCoded => writer is ColorCodedBitmapWriter,
                _ => false
            };

        /// <summary>
        /// Writes the current frame into the existing <see cref="_bitmap"/>.
        /// Updates LUT and value range as needed without reallocating.
        /// </summary>
        private void RefreshBitmap()
        {
            // The writers keep per-instance state (colour maps, scratch buffer), so re-entering
            // one mid-render corrupts the frame being produced. That is not hypothetical: their
            // parallel loops block on a wait that pumps Win32 messages, and the pumped loop runs
            // dispatcher jobs — any of which may write a styled property and land back here.
            //
            // Serialising alone would not do: the nested request carries newer state, so dropping
            // it would leave a stale frame on screen. Remember it and render again instead.
            if (_refreshing)
            {
                _refreshRequested = true;
                return;
            }

            _refreshing = true;
            try
            {
                do
                {
                    _refreshRequested = false;

                    // Re-read every input — a nested request may have changed any of them.
                    var data = MatrixData;
                    if (data == null || _bitmap == null || _writer == null) return;

                    var context = BuildContext(data);
                    if (context == null) return;

#if DEBUG
                    // Debug-only, relative signal (not a Release-mode perf number - JIT/tiering
                    // differences make the absolute value meaningless there): "did this change
                    // make the writer call faster or slower, roughly", visible at a glance via
                    // DrawRenderDiagnosticsOverlay instead of reading log spam. Same reasoning as
                    // LastRenderUsedRgbFastPath - a quick sanity check, not a shipped API.
                    _writerRenderStopwatch.Restart();
                    _writer.Render(data, _bitmap, context);
                    _lastWriterRenderMs = _writerRenderStopwatch.Elapsed.TotalMilliseconds;
#else
                    _writer.Render(data, _bitmap, context);
#endif
                }
                while (_refreshRequested);
            }
            finally
            {
                _refreshing = false;
            }

            // Fired once, for the settled state — nested passes are an implementation detail.
            BitmapRefreshed?.Invoke(this, EventArgs.Empty);
        }

        private IRenderingContext? BuildContext(IMatrixData data)
            => RenderingMode switch
            {
                RenderingMode.Lut => BuildLutContext(data, Lut),
                RenderingMode.Composite => BuildCompositeContext(data),
                RenderingMode.ColorCoded => BuildColorCodedContext(data),
                _ => null
            };

        /// <summary>
        /// Applies the requested palette depth, resampling the LUT when it does not already have
        /// that many levels. The result is memoized: <see cref="LutBitmapWriter"/> keys its color
        /// map cache on the LUT instance, so handing it a freshly resampled table on every render
        /// would rebuild that map every frame - and for a custom .mlut whose level count differs
        /// from the default depth, that is every frame.
        /// </summary>
        private LookupTable ResolveEffectiveLut(LookupTable lut, int depth)
        {
            // depth 0 is the "use the LUT's own level count" sentinel; 1 would collapse the LUT.
            if (depth <= 1 || depth == lut.Levels) return lut;

            if (!ReferenceEquals(_resampleSource, lut) || _resampleDepth != depth)
            {
                _resampledLut = lut.Resample(depth);
                _resampleSource = lut;
                _resampleDepth = depth;
            }

            return _resampledLut!;
        }

        private LutRenderingContext? BuildLutContext(IMatrixData data, LookupTable? lut)
        {
            if (lut == null) return null;

            var effectiveLut = ResolveEffectiveLut(lut, LutLevel);

            double min, max;
            if (IsFixedRange)
            {
                min = FixedMin;
                max = FixedMax;
            }
            else
            {
                int valueMode = data.ValueType == typeof(Complex) ? (int)_complexValueMode : 0;
                (min, max) = data.GetValueRange(FrameIndex, valueMode);
                AutoRangeComputed?.Invoke(this, (min, max));
            }

            return new LutRenderingContext(FrameIndex, effectiveLut, min, max, IsInvertedColor);
        }

        /// <summary>
        /// Builds the ColorCoded context, or <c>null</c> when <see cref="ColorCodedInfo"/> has not
        /// been set yet, or its WinnerIndex's dimensions do not (yet) match <paramref name="data"/> --
        /// MatrixData and ColorCodedInfo are separate styled properties updated one after another by
        /// the parent, so a render triggered in between briefly disagrees. Skipping that frame is
        /// harmless, mirroring <see cref="BuildCompositeContext"/>'s identical guard.
        /// </summary>
        private ColorCodedRenderingContext? BuildColorCodedContext(IMatrixData data)
        {
            var info = ColorCodedInfo;
            if (info == null) return null;
            if (info.WinnerIndex.XCount != data.XCount || info.WinnerIndex.YCount != data.YCount) return null;
            return new ColorCodedRenderingContext(FrameIndex, info);
        }

        /// <summary>
        /// Builds the composite context, or <c>null</c> when the current property set is not yet
        /// self-consistent.
        /// <para>
        /// MatrixData, CompositeRecipes and CompositeFrameIndices are separate styled properties,
        /// and each assignment re-renders synchronously, so a caller updating all of them passes
        /// through intermediate states where they disagree - N frame indices against the previous
        /// single-frame slice, or an index list that is temporarily longer than the recipe list.
        /// Rendering those would throw out of <see cref="CompositeBitmapWriter"/>. Skipping the
        /// frame instead is harmless: the assignment that completes the set renders immediately.
        /// </para>
        /// </summary>
        private CompositeRenderingContext? BuildCompositeContext(IMatrixData data)
        {
            var recipes = CompositeRecipes;
            var indices = CompositeFrameIndices;
            if (recipes is not { Count: > 0 } || indices is not { Length: > 0 }) return null;
            if (recipes.Count != indices.Length) return null;
            foreach (int frameIndex in indices)
                if (frameIndex < 0 || frameIndex >= data.FrameCount) return null;
            return new CompositeRenderingContext(indices, recipes, CompositeBlendMode);
        }

        // Detect Bounds changes (size changed → clamp translation / deferred FitToView)
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != BoundsProperty) return;

            var newBounds = change.GetNewValue<Rect>();
            if (newBounds.Width <= 0 || newBounds.Height <= 0) return;

            if (_pendingFitToView)
            {
                _pendingFitToView = false;
                FitToView();
            }
            else if (_isFitToView)
            {
                // Keep image fitted when the viewport is resized in fit mode
                FitToView();
            }
            else
            {
                ClampTranslation();
                ScrollStateChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }

        // ── Rendering ────────────────────────────────────────────────────────

        public override void Render(DrawingContext ctx)
        {
            ctx.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

            if (_bitmap == null) return;

            var (effW, effH) = GetEffectiveBmpDims();
            double rx = GetRenderTrans(_transX, effW, Bounds.Width, true);
            double ry = GetRenderTrans(_transY, effH, Bounds.Height, false);
            var (ax, ay) = GetAspectScales();
            double origW = _bitmap.PixelSize.Width * _zoom * ax;
            double origH = _bitmap.PixelSize.Height * _zoom * ay;

            // Nearest-neighbor when magnifying (preserves sharp pixels); bilinear when minifying.
            var interpolation = ChooseInterpolation(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height, origW, origH);

            using (ctx.PushClip(new Rect(Bounds.Size)))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
            {
                if (Transform == ViewTransform.None)
                {
                    ctx.DrawImage(_bitmap, new Rect(rx, ry, origW, origH));
                }
                else
                {
                    double cx = rx + effW / 2.0;
                    double cy = ry + effH / 2.0;
                    var mat = Matrix.CreateTranslation(-cx, -cy)
                            * GetTransformMatrix()
                            * Matrix.CreateTranslation(cx, cy);
                    using (ctx.PushTransform(mat))
                        ctx.DrawImage(_bitmap, new Rect(cx - origW / 2.0, cy - origH / 2.0, origW, origH));
                }
                // Axis indicator is drawn in screen space so it works correctly for all transforms
                DrawAxisIndicator(ctx, rx, ry, effW, effH);
            }

            // User overlays drawn above the image but below the system overlays
            OverlayManager?.Draw(ctx);

            DrawCrosshair(ctx);
            DrawPositionOverlay(ctx);
#if DEBUG
            DrawModifierDebugOverlay(ctx);
            DrawRenderDiagnosticsOverlay(ctx);
#endif
        }

        /// <summary>
        /// Renders the bitmap with the current <see cref="ViewTransform"/> to fill exactly
        /// (0, 0, <paramref name="width"/>, <paramref name="height"/>) with no pan offset,
        /// no background fill, and no crosshair/position overlay.
        /// Used by <see cref="MxView.CopyImageAsync"/> for clean clipboard output.
        /// </summary>
        internal void RenderClean(DrawingContext ctx, int width, int height, bool withOverlays)
        {
            if (_bitmap == null) return;
            var (ax, ay) = GetAspectScales();
            var (natW, _) = GetNaturalDims();
            double origW = _bitmap.PixelSize.Width * ax;
            double origH = _bitmap.PixelSize.Height * ay;
            double scale = natW > 0 ? width / natW : 1.0;
            double scaledW = origW * scale;
            double scaledH = origH * scale;

            var interpolation = ChooseInterpolation(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height, width, height);
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
            {
                if (Transform == ViewTransform.None)
                {
                    ctx.DrawImage(_bitmap, new Rect(0, 0, width, height));
                }
                else
                {
                    double cx = width / 2.0;
                    double cy = height / 2.0;
                    var mat = Matrix.CreateTranslation(-cx, -cy)
                            * GetTransformMatrix()
                            * Matrix.CreateTranslation(cx, cy);
                    using (ctx.PushTransform(mat))
                        ctx.DrawImage(_bitmap, new Rect(cx - scaledW / 2.0, cy - scaledH / 2.0, scaledW, scaledH));
                }
            }

            if (OverlayManager != null)
            {
                var vp = new AvaloniaViewport
                {
                    Zoom = scale,
                    TransX = 0,
                    TransY = 0,
                    Ax = ax,
                    Ay = ay,
                    EffW = width,
                    EffH = height,
                    Transform = Transform,
                };
                OverlayManager.BeginCapture(withOverlays);
                OverlayManager.DrawWithViewport(ctx, vp);
                OverlayManager.EndCapture();
            }
        }

        // ── FitToView / CenterImage ───────────────────────────────────────────

        /// <summary>
        /// Scales and centers the image to fill the viewport (maintains aspect ratio)
        /// and enables <see cref="IsFitToView"/> mode so the image re-fits on resize.
        /// </summary>
        public void FitToView()
        {
            if (_bitmap == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

            _isFitToView = true;
            var (ax, ay) = GetAspectScales();
            var (epw, eph) = GetEffectiveBmpPixelDims();
            bool swapped = Transform is ViewTransform.Rotate90CW
                                      or ViewTransform.Rotate90CCW
                                      or ViewTransform.Transpose;
            double contentW = epw * (swapped ? ay : ax);
            double contentH = eph * (swapped ? ax : ay);

            // When ContentAlignment pins an edge, the image is offset by PanMargin in that
            // direction, so the available space for fitting is reduced accordingly.
            // Left/Right pin the X edge; Top/Bottom pin the Y edge; Center has no pin.
            double mX = _contentAlignment is ContentAlignment.Left or ContentAlignment.TopLeft or ContentAlignment.BottomLeft
                                          or ContentAlignment.Right or ContentAlignment.TopRight or ContentAlignment.BottomRight
                        ? PanMargin : 0.0;
            double mY = _contentAlignment is ContentAlignment.Top or ContentAlignment.TopLeft or ContentAlignment.TopRight
                                          or ContentAlignment.Bottom or ContentAlignment.BottomLeft or ContentAlignment.BottomRight
                        ? PanMargin : 0.0;
            // Add BitmapPadding on each side (×2 for both edges)
            double availW = Math.Max(1.0, Bounds.Width - mX - BitmapPadding * 2);
            double availH = Math.Max(1.0, Bounds.Height - mY - BitmapPadding * 2);

            _zoom = Math.Min(availW / contentW, availH / contentH);
            _transX = AlignedTransX(contentW * _zoom, Bounds.Width);
            _transY = AlignedTransY(contentH * _zoom, Bounds.Height);
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        /// <summary>
        /// Sets zoom to an arbitrary factor, anchored to the viewport centre.
        /// Clamps to the supported range [<c>1 %</c> … <c>6400 %</c>] and exits fit-to-view mode.
        /// </summary>
        public void SetZoom(double zoom)
        {
            if (_bitmap == null) return;
            zoom = Math.Clamp(zoom, ZoomLevels[0], ZoomLevels[^1]);
            _isFitToView = false;

            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                var (ax, ay) = GetAspectScales();
                double cx = Bounds.Width / 2.0;
                double cy = Bounds.Height / 2.0;
                double dataX = (cx - _transX) / (_zoom * ax);
                double dataY = (cy - _transY) / (_zoom * ay);
                _zoom = zoom;
                _transX = cx - dataX * zoom * ax;
                _transY = cy - dataY * zoom * ay;
            }
            else
            {
                _zoom = zoom;
            }
            ClampTranslation();
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        /// <summary>
        /// If the axis indicator line is currently outside the visible viewport, scrolls so
        /// that it appears at the centre of the viewport (clamped to the valid pan range when
        /// the indicator is near the data boundary).
        /// No-op when the indicator is already visible, when <see cref="AxisIndicatorPx"/> is
        /// <c>null</c>, or when the bitmap / bounds are not yet ready.
        /// </summary>
        /// <param name="forceCenter">
        /// When <c>true</c>, always scrolls so the indicator is centred in the viewport,
        /// even if it is already visible. Used by zoom-sync to keep the indicator centred
        /// after the zoom level changes.
        /// </param>
        public void ScrollToAxisIndicator(bool forceCenter = false)
        {
            // Suppress auto-scroll while the user is directly dragging the indicator.
            // Scrolling during drag creates a feedback loop that causes the view to
            // scroll continuously (the shifted viewport makes the indicator appear
            // outside bounds on the next call, perpetuating the cycle).
            if (_isDraggingAxisIndicator) return;
            if (!AxisIndicatorPx.HasValue || _bitmap == null) return;
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

            var (_, ay) = GetAspectScales();
            var (effW, effH) = GetEffectiveBmpDims();
            int idx = AxisIndicatorPx.Value;
            int bmpRow = FlipY ? _bitmap.PixelSize.Height - 1 - idx : idx;
            bool changed = false;

            switch (Transform)
            {
                case ViewTransform.None:
                case ViewTransform.FlipH:
                    {
                        double ry = ClampedTrans(_transY, effH, Bounds.Height, false);
                        double y = ry + (bmpRow + 0.5) * _zoom * ay;
                        if (forceCenter || y < 0 || y > Bounds.Height)
                        {
                            _transY = Bounds.Height / 2.0 - (bmpRow + 0.5) * _zoom * ay;
                            changed = true;
                        }
                        break;
                    }
                case ViewTransform.FlipV:
                case ViewTransform.Rotate180:
                    {
                        double ry = ClampedTrans(_transY, effH, Bounds.Height, false);
                        double y = ry + effH - (bmpRow + 0.5) * _zoom * ay;
                        if (forceCenter || y < 0 || y > Bounds.Height)
                        {
                            _transY = Bounds.Height / 2.0 - effH + (bmpRow + 0.5) * _zoom * ay;
                            changed = true;
                        }
                        break;
                    }
                case ViewTransform.Transpose:
                case ViewTransform.Rotate90CCW:
                    {
                        double rx = ClampedTrans(_transX, effW, Bounds.Width, true);
                        double x = rx + (bmpRow + 0.5) * _zoom * ay;
                        if (forceCenter || x < 0 || x > Bounds.Width)
                        {
                            _transX = Bounds.Width / 2.0 - (bmpRow + 0.5) * _zoom * ay;
                            changed = true;
                        }
                        break;
                    }
                case ViewTransform.Rotate90CW:
                    {
                        double rx = ClampedTrans(_transX, effW, Bounds.Width, true);
                        double x = rx + effW - (bmpRow + 0.5) * _zoom * ay;
                        if (forceCenter || x < 0 || x > Bounds.Width)
                        {
                            _transX = Bounds.Width / 2.0 - effW + (bmpRow + 0.5) * _zoom * ay;
                            changed = true;
                        }
                        break;
                    }
            }

            if (changed)
            {
                _isFitToView = false;
                ClampTranslation();
                ScrollStateChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
        }

        // ── Pan ──────────────────────────────────────────────────────────────

        protected override void OnKeyDown(KeyEventArgs e)
        {
            /*
#if DEBUG
            Debug.WriteLine("[OnKeyDown] Key: {0}, Modifiers: {1}", e.Key, e.KeyModifiers);
#endif
            */
            if (OverlayManager != null && OverlayManager.OnKeyDown(e.Key, e.KeyModifiers))
            {
                e.Handled = true;
                return;
            }
            if (OverlayManager != null &&
                e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt)
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            bool isModifier = e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;
            /*
#if DEBUG
            Debug.WriteLine("[OnKeyUp] Key: {0}, Modifiers: {1}, IsModifier: {2}",
                e.Key, e.KeyModifiers, isModifier);
#endif
            */
            OverlayManager?.OnKeyUp(e.Key, e.KeyModifiers);
            if (OverlayManager != null && isModifier)
            {
                e.Handled = true;
                InvalidateVisual();
                return;
            }
            base.OnKeyUp(e);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            _isPointerInside = true;
            OverlayManager?.SyncModifiers(e.KeyModifiers);
            base.OnPointerEntered(e);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _isPointerInside = false;
            Cursor = Cursor.Default;
            InvalidateVisual();
            base.OnPointerExited(e);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                base.OnPointerPressed(e);
                return;
            }
            if (_bitmap == null) return;

            // Take keyboard focus so arrow / Delete / Escape keys work
            Focus();

            var pos = e.GetPosition(this);
            _snapTimer?.Stop();

            // Overlay manager has first priority (including double-click to open editors)
            if (OverlayManager != null)
            {
                bool isLeft = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
                if (OverlayManager.OnPointerPressed(pos, isLeft, e.KeyModifiers, e.ClickCount))
                {
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
            }

            if (e.ClickCount == 2)
            {
                // Double-click with no overlay hit: cancel any pan and re-enter fit mode
                _isPanning = false;
                e.Pointer.Capture(null);
                FitToView();
                e.Handled = true;
                return;
            }

            if (AxisIndicatorPx.HasValue && IsOnAxisIndicator(pos))
            {
                _isDraggingAxisIndicator = true;
                var newIdx = ScreenToAxisIndicatorIndex(pos);
                AxisIndicatorPx = newIdx;
                AxisIndicatorDragged?.Invoke(this, newIdx);
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            var chHit = GetCrosshairHit(pos);
            if (chHit != CrosshairHit.None)
            {
                _isDraggingCrosshair = true;
                _isDraggingH = chHit == CrosshairHit.HLine || chHit == CrosshairHit.Cross;
                _isDraggingV = chHit == CrosshairHit.VLine || chHit == CrosshairHit.Cross;
                ApplyCrosshairDrag(pos);
                CrosshairMoved?.Invoke(this, _crosshairDataPos!.Value);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            _isPanning = true;
            _panStart = pos;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {

            _lastPointerModifiers = e.KeyModifiers;
            var pos = e.GetPosition(this);
            _lastScreenPos = pos;
            if (_isDraggingAxisIndicator)
            {
                var newIdx = ScreenToAxisIndicatorIndex(pos);
                if (AxisIndicatorPx != newIdx)
                {
                    AxisIndicatorPx = newIdx;
                    AxisIndicatorDragged?.Invoke(this, newIdx);
                }
            }
            else if (_isDraggingCrosshair)
            {
                ApplyCrosshairDrag(pos);
                CrosshairMoved?.Invoke(this, _crosshairDataPos!.Value);
            }
            else if (OverlayManager != null && OverlayManager.OnPointerMoved(pos, _lastPointerModifiers))
            {
                // Overlay consumed the move: it set the cursor itself via SetCursor delegate.
                // Skip system-overlay cursor updates below.
                InvalidateVisual();
                return;
            }
            else if (_isPanning)
            {
                _transX += pos.X - _panStart.X;
                _transY += pos.Y - _panStart.Y;
                _panStart = pos;
                ScrollStateChanged?.Invoke(this, EventArgs.Empty);
            }

            // Update cursor for AxisIndicator / Crosshair when no overlay or pan is active.
            if (!_isDraggingAxisIndicator && !_isDraggingCrosshair && !_isPanning)
            {
                if (AxisIndicatorPx.HasValue && IsOnAxisIndicator(pos))
                {
                    Cursor = Transform is ViewTransform.Transpose
                                      or ViewTransform.Rotate90CW
                                      or ViewTransform.Rotate90CCW
                        ? new Cursor(StandardCursorType.SizeWestEast)
                        : new Cursor(StandardCursorType.SizeNorthSouth);
                }
                else if (ShowCrosshair && _crosshairDataPos.HasValue)
                {
                    Cursor = GetCrosshairHit(pos) switch
                    {
                        CrosshairHit.Cross => new Cursor(StandardCursorType.SizeAll),
                        CrosshairHit.HLine => new Cursor(StandardCursorType.SizeNorthSouth),
                        CrosshairHit.VLine => new Cursor(StandardCursorType.SizeWestEast),
                        _ => Cursor.Default,
                    };
                }
                else
                {
                    Cursor = Cursor.Default;
                }
            }
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (_isDraggingAxisIndicator)
            {
                _isDraggingAxisIndicator = false;
                Cursor = Cursor.Default;
                e.Pointer.Capture(null);
                AxisIndicatorDragEnded?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (_isDraggingCrosshair)
            {
                _isDraggingCrosshair = false;
                _isDraggingH = false;
                _isDraggingV = false;
                Cursor = Cursor.Default;
                e.Pointer.Capture(null);
                return;
            }
            if (OverlayManager != null)
            {
                var pos = e.GetPosition(this);
                if (OverlayManager.OnPointerReleased(pos, e.KeyModifiers))
                {
                    e.Pointer.Capture(null);
                    return;
                }
            }
            if (!_isPanning)
            {
                // No left-button operation in progress.
                // Call base so Avalonia can raise ContextRequested on right-click.
                base.OnPointerReleased(e);
                return;
            }
            _isPanning = false;
            e.Pointer.Capture(null);
            double tx = SnapTargetX(), ty = SnapTargetY();
            if (Math.Abs(_transX - tx) > 0.5 || Math.Abs(_transY - ty) > 0.5)
                StartSnapBackAnimation();
        }

        // ── Zoom ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Threshold for accumulated wheel delta before a discrete zoom step is applied.
        /// Standard mouse wheels send ±1.0 per notch; trackpads send much smaller increments.
        /// </summary>
        private const double ZoomDeltaThreshold = 0.5;

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            _zoomDeltaAccum += e.Delta.Y;

            // Wait until the accumulated delta exceeds the threshold
            if (Math.Abs(_zoomDeltaAccum) < ZoomDeltaThreshold)
            {
                e.Handled = true;
                return;
            }

            var mouse = e.GetPosition(this);
            double newZoom = _zoomDeltaAccum > 0 ? ZoomUp(_zoom) : ZoomDown(_zoom);
            _zoomDeltaAccum = 0;

            // Explicit zoom → exit fit mode
            _isFitToView = false;

            // Anchor: keep the data point under the cursor fixed
            var (ax, ay) = GetAspectScales();
            double dataX = (mouse.X - _transX) / (_zoom * ax);
            double dataY = (mouse.Y - _transY) / (_zoom * ay);
            _zoom = newZoom;
            _transX = mouse.X - dataX * _zoom * ax;
            _transY = mouse.Y - dataY * _zoom * ay;

            ClampTranslation();
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            e.Handled = true;
        }

        // ── Scrollbar integration (called by MxView) ─────────────────────────

        // barValue = PanMargin - _transX  (barValue=0 → left edge at PanMargin)
        internal void SetScrollX(double barValue)
        {
            if (_bitmap == null || Bounds.Width <= 0) return;
            if (GetEffectiveBmpDims().w <= Bounds.Width) return;
            _transX = PanMargin - barValue;
            InvalidateVisual();
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void SetScrollY(double barValue)
        {
            if (_bitmap == null || Bounds.Height <= 0) return;
            if (GetEffectiveBmpDims().h <= Bounds.Height) return;
            _transY = PanMargin - barValue;
            InvalidateVisual();
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a projection function from Complex to double
        /// corresponding to the current ComplexValueMode.
        /// </summary>
        private Func<Complex, double> GetComplexConverter() =>
            ComplexValueModeConverter.GetConverter(_complexValueMode);

        private void ClampTranslation()
        {
            _snapTimer?.Stop();
            if (_bitmap == null) return;
            var (effW, effH) = GetEffectiveBmpDims();
            double vpW = Bounds.Width;
            double vpH = Bounds.Height;
            double m = PanMargin;

            _transX = effW <= vpW
                ? AlignedTransX(effW, vpW)
                : Math.Clamp(_transX, vpW - m - effW, m);

            _transY = effH <= vpH
                ? AlignedTransY(effH, vpH)
                : Math.Clamp(_transY, vpH - m - effH, m);
        }

        // ── Coordinate conversion ───────────────────────────────────────────────

        /// <summary>
        /// Converts a position in this control's screen coordinates to
        /// data coordinates using MxPlot's bottom-left origin convention.
        /// Returns scaled data values (XMin..XMax, YMin..YMax) when
        /// <see cref="MatrixData"/> is set; otherwise returns fractional pixel indices.
        /// Y increases upward: screen row 0 (top) maps to <c>YMax</c>,
        /// screen row <c>YCount-1</c> (bottom) maps to <c>YMin</c>.
        /// Accounts for <see cref="Transform"/>, zoom, and elastic pan offset.
        /// </summary>
        public Point ScreenToData(Point screenPos)
        {
            if (_bitmap == null || _zoom == 0) return screenPos;
            var (bx, by) = ScreenToBitmapPixel(screenPos);
            var md = MatrixData;
            if (md != null && md.XStep != 0 && md.YStep != 0)
                return new Point(md.XMin + (bx - 0.5) * md.XStep, md.YMax - (by - 0.5) * md.YStep);
            return new Point(bx, by);
        }

        /// <summary>
        /// Converts data coordinates (bottom-left origin, scaled) to a position
        /// in this control's screen coordinate space.
        /// Accepts scaled data values (XMin..XMax, YMin..YMax) when
        /// <see cref="MatrixData"/> is set; otherwise accepts fractional pixel indices.
        /// Y increases upward: <c>YMax</c> maps to screen row 0 (top),
        /// <c>YMin</c> maps to screen row <c>YCount-1</c> (bottom).
        /// Accounts for <see cref="Transform"/>, zoom, and elastic pan offset.
        /// </summary>
        public Point DataToScreen(Point dataPos)
        {
            if (_bitmap == null) return dataPos;
            var (ax, ay) = GetAspectScales();
            var (effW, effH) = GetEffectiveBmpDims();
            double rx = GetRenderTrans(_transX, effW, Bounds.Width, true);
            double ry = GetRenderTrans(_transY, effH, Bounds.Height, false);
            var md = MatrixData;
            double bx, by;
            if (md != null && md.XStep != 0 && md.YStep != 0)
            {
                bx = (dataPos.X - md.XMin) / md.XStep + 0.5;
                by = (md.YMax - dataPos.Y) / md.YStep + 0.5;
            }
            else
            {
                bx = dataPos.X;
                by = dataPos.Y;
            }
            // Inverse of ScreenToBitmapPixel — one case per ViewTransform.
            // effW/effH are already swapped for Rotate90CW/CCW/Transpose by GetEffectiveBmpDims.
            return Transform switch
            {
                ViewTransform.FlipH => new Point(rx + effW - bx * _zoom * ax, by * _zoom * ay + ry),
                ViewTransform.FlipV => new Point(bx * _zoom * ax + rx, ry + effH - by * _zoom * ay),
                ViewTransform.Rotate180 => new Point(rx + effW - bx * _zoom * ax, ry + effH - by * _zoom * ay),
                ViewTransform.Rotate90CW => new Point(rx + effW - by * _zoom * ay, bx * _zoom * ax + ry),
                ViewTransform.Rotate90CCW => new Point(by * _zoom * ay + rx, ry + effH - bx * _zoom * ax),
                ViewTransform.Transpose => new Point(by * _zoom * ay + rx, bx * _zoom * ax + ry),
                _ => new Point(bx * _zoom * ax + rx, by * _zoom * ay + ry),
            };
        }

        // ── Modifier debug overlay ────────────────────────────────────────────

        private void DrawModifierDebugOverlay(DrawingContext ctx)
        {
            if (OverlayManager == null) return;

            var trk = OverlayManager.TrackedModifiers;
            bool match = _lastPointerModifiers == trk;
            bool sOn = trk.HasFlag(KeyModifiers.Shift);
            bool cOn = trk.HasFlag(KeyModifiers.Control);
            bool focused = IsFocused;

            string line1 = $"({_lastScreenPos.X:F0}, {_lastScreenPos.Y:F0})";
            string line2 = (sOn, cOn) switch
            {
                (true, true) => "Shift  Ctrl",
                (true, false) => "Shift",
                (false, true) => "Ctrl",
                _ => "\u2014",
            };
            string line3 = focused ? "Focus \u2713" : "Focus \u2013";

            const double fontSize = 11.0;
            const double pad = 4.0;
            const double lineGap = 2.0;
            var tf = new Typeface("Consolas");
            var ft1 = new FormattedText(line1, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, fontSize, Brushes.White);
            var ft2 = new FormattedText(line2, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, fontSize, Brushes.White);
            var ft3 = new FormattedText(line3, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, fontSize,
                focused ? new SolidColorBrush(Color.FromRgb(120, 220, 120)) : new SolidColorBrush(Color.FromRgb(180, 180, 180)));

            double bw = Math.Max(Math.Max(ft1.Width, ft2.Width), ft3.Width) + pad * 2;
            double bh = ft1.Height + lineGap + ft2.Height + lineGap + ft3.Height + pad * 2;
            double ox = Bounds.Width - bw - 4.0;
            double oy = 4.0;

            var bg = match
                ? new SolidColorBrush(Color.FromArgb(200, 20, 100, 20))
                : new SolidColorBrush(Color.FromArgb(200, 160, 80, 0));

            ctx.FillRectangle(bg, new Rect(ox, oy, bw, bh), 3f);
            ctx.DrawText(ft1, new Point(ox + pad, oy + pad));
            ctx.DrawText(ft2, new Point(ox + pad, oy + pad + ft1.Height + lineGap));
            ctx.DrawText(ft3, new Point(ox + pad, oy + pad + ft1.Height + lineGap + ft2.Height + lineGap));
        }

#if DEBUG
        /// <summary>
        /// Debug-only render diagnostics: the last writer.Render call's own elapsed time (any
        /// mode - LUT, Composite, ColorCoded), plus, in Composite mode specifically, whether that
        /// render actually took <see cref="CompositeBitmapWriter"/>'s pure-RGB Fast Path (see its
        /// own <c>IsRgbFastPathEligible</c>) instead of the general per-pixel LUT/blend path. Grew
        /// out of a DSImageCapture performance investigation where this was exactly the kind of
        /// thing that needed an at-a-glance Debug-time sanity check, without adding logging noise.
        /// </summary>
        /// <remarks>
        /// The timing is a relative signal for spotting regressions or confirming a change helped
        /// within the same Debug session - not a Release-mode perf number. Debug builds run
        /// un-tiered/un-optimized, so the absolute millisecond value here does not transfer to
        /// Release; that is also why this stays a private, Debug-only overlay rather than a public
        /// MxView property - a "did this get faster" glance does not need a committed public API,
        /// and a real Release-mode number would need its own separate design (this method's own
        /// Stopwatch already only exists under #if DEBUG - see the field declarations above).
        /// Bottom-right, away from <see cref="DrawModifierDebugOverlay"/> (top-right) and any
        /// app-level overlay that might sit at the view's top-left (e.g. DSImageCapture's own
        /// Stack controls).
        /// </remarks>
        private void DrawRenderDiagnosticsOverlay(DrawingContext ctx)
        {
            bool isComposite = _writer is CompositeBitmapWriter;
            bool fast = _writer is CompositeBitmapWriter cbw && cbw.LastRenderUsedRgbFastPath;
            string? rejectReason = _writer is CompositeBitmapWriter cbw2 ? cbw2.LastRgbFastPathRejectReason : null;

            const double fontSize = 11.0;
            const double pad = 4.0;
            const double lineGap = 2.0;
            var tf = new Typeface("Consolas");
            var neutralBrush = new SolidColorBrush(Color.FromRgb(220, 220, 220));
            var fastBrush = fast
                ? new SolidColorBrush(Color.FromRgb(120, 220, 120))
                : new SolidColorBrush(Color.FromRgb(180, 180, 180));
            var reasonBrush = new SolidColorBrush(Color.FromRgb(230, 180, 90));

            var lines = new List<FormattedText>
            {
                new($"Render {_lastWriterRenderMs:F2} ms", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, fontSize, neutralBrush),
            };
            if (isComposite)
                lines.Add(new(fast ? "FastPath ✓" : "FastPath –",
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, fontSize, fastBrush));
            // Diagnostic-only, per-condition detail so a stale/unexpectedly-non-eligible session
            // does not need a debugger session to explain itself - see CompositeBitmapWriter's own
            // LastRgbFastPathRejectReason doc comment.
            if (isComposite && !fast && rejectReason != null)
                lines.Add(new(rejectReason,
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, fontSize, reasonBrush));

            double bw = 0;
            double bh = pad * 2;
            for (int i = 0; i < lines.Count; i++)
            {
                bw = Math.Max(bw, lines[i].Width);
                bh += lines[i].Height + (i > 0 ? lineGap : 0);
            }
            bw += pad * 2;
            double ox = Bounds.Width - bw - 4.0;
            double oy = Bounds.Height - bh - 4.0;

            var bg = isComposite && fast
                ? new SolidColorBrush(Color.FromArgb(200, 20, 100, 20))
                : new SolidColorBrush(Color.FromArgb(200, 60, 60, 60));

            ctx.FillRectangle(bg, new Rect(ox, oy, bw, bh), 3f);
            double y = oy + pad;
            foreach (var line in lines)
            {
                ctx.DrawText(line, new Point(ox + pad, y));
                y += line.Height + lineGap;
            }
        }
#endif

        // ── Position overlay ──────────────────────────────────────────────────

        /// <summary>
        /// Converts a screen position to bitmap pixel coordinates, accounting for
        /// the current <see cref="Transform"/>. Returns fractional pixel indices.
        /// </summary>
        private (double bx, double by) ScreenToBitmapPixel(Point s)
        {
            var (ax, ay) = GetAspectScales();
            var (effW, effH) = GetEffectiveBmpDims();
            double rx = GetRenderTrans(_transX, effW, Bounds.Width, true);
            double ry = GetRenderTrans(_transY, effH, Bounds.Height, false);
            return Transform switch
            {
                ViewTransform.FlipH => ((rx + effW - s.X) / (_zoom * ax), (s.Y - ry) / (_zoom * ay)),
                ViewTransform.FlipV => ((s.X - rx) / (_zoom * ax), (ry + effH - s.Y) / (_zoom * ay)),
                ViewTransform.Rotate180 => ((rx + effW - s.X) / (_zoom * ax), (ry + effH - s.Y) / (_zoom * ay)),
                ViewTransform.Rotate90CW => ((s.Y - ry) / (_zoom * ax), (rx + effW - s.X) / (_zoom * ay)),
                ViewTransform.Rotate90CCW => ((ry + effH - s.Y) / (_zoom * ax), (s.X - rx) / (_zoom * ay)),
                ViewTransform.Transpose => ((s.Y - ry) / (_zoom * ax), (s.X - rx) / (_zoom * ay)),
                _ => ((s.X - rx) / (_zoom * ax), (s.Y - ry) / (_zoom * ay)),
            };
        }

        /// <summary>
        /// Draws the cursor read-out box.
        /// <para>
        /// The text is built as a list of coloured runs rather than one string, because in
        /// <see cref="RenderingMode.Composite"/> every visible channel contributes a value and each
        /// is tinted with that channel's own colour. That keeps the read-out compact and tied to the
        /// blended image, instead of spelling out channel names that would push the box off-screen
        /// once the names are real ("DAPI", "mCherry", ...). Outside Composite mode the list holds a
        /// single white run, exactly as before.
        /// </para>
        /// </summary>
        private void DrawPositionOverlay(DrawingContext ctx)
        {
            if (!ShowDataPosition) return;

            var runs = new List<(string Text, IBrush Brush)>();

            if (OverlayInfoText is { } overlayInfo)
            {
                runs.Add((overlayInfo, Brushes.White));
            }
            else
            {
                if (_bitmap == null || !_isPointerInside) return;

                // Hide when the cursor is outside the effective image rect
                var (effW, effH) = GetEffectiveBmpDims();
                double rx = GetRenderTrans(_transX, effW, Bounds.Width, true);
                double ry = GetRenderTrans(_transY, effH, Bounds.Height, false);
                if (_lastScreenPos.X < rx || _lastScreenPos.X >= rx + effW ||
                    _lastScreenPos.Y < ry || _lastScreenPos.Y >= ry + effH) return;

                var (fbx, fby) = ScreenToBitmapPixel(_lastScreenPos);

                int rawIx = Math.Clamp((int)Math.Floor(fbx), 0, _bitmap.PixelSize.Width - 1);
                int rawIy = FlipY
                    ? Math.Clamp(_bitmap.PixelSize.Height - 1 - (int)Math.Floor(fby), 0, _bitmap.PixelSize.Height - 1)
                    : Math.Clamp((int)Math.Floor(fby), 0, _bitmap.PixelSize.Height - 1);

                var md = MatrixData;
                if (md != null && md.XStep != 0 && md.YStep != 0)
                {
                    double dx = md.XMin + (fbx - 0.5) * md.XStep;
                    double dy = FlipY
                        ? md.YMax - (fby - 0.5) * md.YStep
                        : md.YMin + (fby - 0.5) * md.YStep;
                    string xu = md.XUnit.Length > 0 ? $" {md.XUnit}" : "";
                    string yu = md.YUnit.Length > 0 ? $" {md.YUnit}" : "";
                    string prefix = $"({dx:F2}{xu}, {dy:F2}{yu}) [{rawIx},{rawIy}] = ";

                    if (RenderingMode == RenderingMode.ColorCoded && ColorCodedInfo is { } info)
                    {
                        // md is now the winner *value* matrix itself (an ordinary projection-shaped
                        // result -- see ColorCodedBitmapWriter's doc comment), so its GetValueAt below
                        // is already the real value. Only the depth position needs the auxiliary
                        // winner-index matrix and Axis that ColorCodedInfo carries.
                        int depthIndex = (int)Math.Round(info.WinnerIndex.GetValueAt(rawIx, rawIy, 0));
                        double depthValue = md.GetValueAt(rawIx, rawIy, FrameIndex);
                        string axisUnit = info.Axis.Unit.Length > 0 ? $" {info.Axis.Unit}" : "";
                        string depthLabel = $"{info.Axis.Name}={info.Axis.ValueAt(depthIndex):G4}{axisUnit}";
                        runs.Add((prefix + $"{depthLabel}, value={FormatCursorValue(md.ValueType, depthValue)}",
                            Brushes.White));
                    }
                    else if (!TryAddCompositeValueRuns(runs, md, rawIx, rawIy, prefix))
                    {
                        double value;
                        string cmplxValue = "";
                        if (md.ValueType == typeof(Complex)
                            && _writer?.StructValueConverter is Func<Complex, double> conv)
                        {
                            var typedData = (MatrixData<Complex>)md;
                            var span = typedData.AsSpan(FrameIndex);

                            var z = span[rawIy * md.XCount + rawIx];
                            value = conv(z);
                            cmplxValue = $" ({z.Real:G5}{(z.Imaginary >= 0 ? "+" : "-")}{Math.Abs(z.Imaginary):G5}i)";
                        }
                        else
                        {
                            value = md.GetValueAt(rawIx, rawIy, FrameIndex);
                        }
                        runs.Add((prefix + FormatCursorValue(md.ValueType, value) + cmplxValue, Brushes.White));
                    }
                }
                else
                {
                    runs.Add(($"[{rawIx},{rawIy}]", Brushes.White));
                }
            }

            if (runs.Count == 0) return;

            var typeface = new Typeface("Consolas");
            var texts = new List<FormattedText>(runs.Count);
            double textWidth = 0, textHeight = 0;
            foreach (var (text, brush) in runs)
            {
                var run = new FormattedText(
                    text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12.0, brush);
                texts.Add(run);
                textWidth += run.Width;
                textHeight = Math.Max(textHeight, run.Height);
            }

            const double pad = 4.0;
            const double margin = 0.0;
            double bw = textWidth + pad * 2;
            double bh = textHeight + pad * 2;

            double ox, oy;
            if (OverlayInfoText != null && OverlayInfoTextAnchor == OverlayInfoTextAnchor.TopRight)
            {
                ox = Bounds.Width - margin - bw;
                oy = margin;
            }
            else
            {
                ox = margin;
                double oyBL = Bounds.Height - margin - bh;
                oy = (_lastScreenPos.X >= margin && _lastScreenPos.X < margin + bw &&
                      _lastScreenPos.Y >= oyBL && _lastScreenPos.Y < oyBL + bh)
                     ? margin
                     : oyBL;
            }

            ctx.FillRectangle(
                new SolidColorBrush(Color.FromArgb(160, 48, 48, 48)),
                new Rect(ox, oy, bw, bh),
                3.0f);

            double penX = ox + pad;
            foreach (var run in texts)
            {
                ctx.DrawText(run, new Point(penX, oy + pad));
                penX += run.Width;
            }
        }

        /// <summary>
        /// Appends one colour-tinted run per visible composite channel to <paramref name="runs"/>.
        /// Returns <c>false</c> when Composite rendering is not active (or carries no usable recipe
        /// set), leaving <paramref name="runs"/> untouched so the caller can fall back to the
        /// single-value read-out.
        /// </summary>
        private bool TryAddCompositeValueRuns(
            List<(string Text, IBrush Brush)> runs, IMatrixData md, int rawIx, int rawIy, string prefix)
        {
            if (RenderingMode is not (RenderingMode.Composite or RenderingMode.ColorCoded)) return false;

            var recipes = CompositeRecipes;
            var indices = CompositeFrameIndices;
            if (recipes is not { Count: > 0 } || indices is not { Length: > 0 }) return false;

            // MatrixData, CompositeRecipes and CompositeFrameIndices are separate styled properties
            // that can briefly disagree mid-transition (see BuildCompositeContext's identical guard) --
            // e.g. entering Composite mode pushes N frame indices before the view's MatrixData has
            // been swapped for the merged N-frame set. md.GetValueAt below would throw for an index
            // past md.FrameCount; skip the read-out for this frame instead, same as the bitmap.
            foreach (int frameIndex in indices)
                if (frameIndex < 0 || frameIndex >= md.FrameCount) return false;

            int count = Math.Min(recipes.Count, indices.Length);
            var separator = new SolidColorBrush(Color.FromRgb(150, 150, 150));
            bool any = false;

            runs.Add((prefix, Brushes.White));
            for (int i = 0; i < count; i++)
            {
                if (!recipes[i].IsVisible) continue;   // hidden channels contribute nothing to the image
                if (any) runs.Add((" / ", separator));

                double value = md.GetValueAt(rawIx, rawIy, indices[i]);
                runs.Add((FormatCursorValue(md.ValueType, value),
                          new SolidColorBrush(BrightenForText(recipes[i].ColorArgb))));
                any = true;
            }

            if (!any) runs.Clear();   // every channel hidden: fall back rather than show a bare "="
            return any;
        }

        private static string FormatCursorValue(Type valueType, double value) =>
            valueType == typeof(float) ? value.ToString("G5", CultureInfo.InvariantCulture)
            : (valueType == typeof(double) || valueType == typeof(Complex))
                ? value.ToString("G6", CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Lifts a channel colour to a minimum luminance so the value stays legible on the dark
        /// read-out background. Pure blue in particular is close to unreadable at 12 px otherwise.
        /// The hue is preserved; only the overall level is raised.
        /// </summary>
        private static Color BrightenForText(int argb)
        {
            var c = Color.FromUInt32(unchecked((uint)argb));
            double luma = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
            const double minLuma = 0.55;
            if (luma >= minLuma || luma <= 0.0) return Color.FromRgb(c.R, c.G, c.B);

            double gain = minLuma / luma;
            byte Lift(byte v) => (byte)Math.Clamp(v * gain, 0, 255);
            return Color.FromRgb(Lift(c.R), Lift(c.G), Lift(c.B));
        }

        // ── Elastic pan ──────────────────────────────────────────────────────

        /// <summary>
        /// Returns the render position for one axis, applying rubber-band resistance
        /// when <paramref name="trans"/> is outside the valid pan range.
        /// For a fitting axis (image smaller than viewport) the elastic is centred.
        /// </summary>
        private double GetRenderTrans(double trans, double bmpSize, double viewSize, bool isX)
        {
            double limit = viewSize * DragLimitRatio;
            if (bmpSize <= viewSize)
            {
                // Elastic pull around the alignment-defined home position
                double home = isX ? AlignedTransX(bmpSize, viewSize) : AlignedTransY(bmpSize, viewSize);
                double raw = trans - home;
                return raw == 0 ? home : home + raw * limit / (limit + Math.Abs(raw));
            }
            double maxT = PanMargin;
            double minT = viewSize - PanMargin - bmpSize;
            if (trans > maxT)
            {
                double raw = trans - maxT;  // positive
                return maxT + raw * limit / (limit + raw);
            }
            if (trans < minT)
            {
                double raw = trans - minT;  // negative
                return minT + raw * limit / (limit - raw);  // divisor = limit + |raw|
            }
            return trans;
        }

        /// <summary>Clamps trans to the valid pan range (used by scrollbar sync).</summary>
        private double ClampedTrans(double trans, double bmpSize, double viewSize, bool isX)
        {
            if (bmpSize <= viewSize)
                return isX ? AlignedTransX(bmpSize, viewSize) : AlignedTransY(bmpSize, viewSize);
            double maxT = PanMargin;
            double minT = viewSize - PanMargin - bmpSize;
            return Math.Clamp(trans, minT, maxT);
        }

        private double SnapTargetX()
        {
            if (_bitmap == null) return _transX;
            var (effW, _) = GetEffectiveBmpDims();
            return effW <= Bounds.Width
                ? AlignedTransX(effW, Bounds.Width)
                : Math.Clamp(_transX, Bounds.Width - PanMargin - effW, PanMargin);
        }

        private double SnapTargetY()
        {
            if (_bitmap == null) return _transY;
            var (_, effH) = GetEffectiveBmpDims();
            return effH <= Bounds.Height
                ? AlignedTransY(effH, Bounds.Height)
                : Math.Clamp(_transY, Bounds.Height - PanMargin - effH, PanMargin);
        }

        private void StartSnapBackAnimation()
        {
            if (_snapTimer == null)
            {
                _snapTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
                _snapTimer.Tick += SnapTimer_Tick;
            }
            _snapTimer.Start();
        }

        private void SnapTimer_Tick(object? sender, EventArgs e)
        {
            double targetX = SnapTargetX();
            double targetY = SnapTargetY();
            _transX += (targetX - _transX) * 0.5;
            _transY += (targetY - _transY) * 0.5;
            if (Math.Abs(_transX - targetX) < 0.5 && Math.Abs(_transY - targetY) < 0.5)
            {
                _transX = targetX;
                _transY = targetY;
                _snapTimer!.Stop();
            }
            ScrollStateChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }

        private static double ZoomUp(double current)
        {
            foreach (var z in ZoomLevels)
                if (z > current + 1e-9) return z;
            return ZoomLevels[^1];
        }

        private static double ZoomDown(double current)
        {
            for (int i = ZoomLevels.Length - 1; i >= 0; i--)
                if (ZoomLevels[i] < current - 1e-9) return ZoomLevels[i];
            return ZoomLevels[0];
        }

        // ── ViewTransform helpers ─────────────────────────────────────────────

        /// <summary>Returns the effective (post-transform, aspect-corrected) bitmap dimensions in screen pixels.</summary>
        internal (double w, double h) GetEffectiveBmpDims()
        {
            if (_bitmap == null) return (0, 0);
            var (ax, ay) = GetAspectScales();
            double w = _bitmap.PixelSize.Width * _zoom * ax;
            double h = _bitmap.PixelSize.Height * _zoom * ay;
            return Transform is ViewTransform.Rotate90CW
                             or ViewTransform.Rotate90CCW
                             or ViewTransform.Transpose
                ? (h, w) : (w, h);
        }

        /// <summary>Returns the zoom-independent, aspect-corrected bitmap dimensions (i.e. at zoom = 1).</summary>
        internal (double w, double h) GetNaturalDims()
        {
            if (_bitmap == null) return (0, 0);
            var (ax, ay) = GetAspectScales();
            double w = _bitmap.PixelSize.Width * ax;
            double h = _bitmap.PixelSize.Height * ay;
            return Transform is ViewTransform.Rotate90CW
                             or ViewTransform.Rotate90CCW
                             or ViewTransform.Transpose
                ? (h, w) : (w, h);
        }

        /// <summary>
        /// Returns the appropriate <see cref="BitmapInterpolationMode"/> for rendering a bitmap
        /// at the given destination size.
        /// Magnification (destination larger than source): nearest-neighbor for pixel-exact output.
        /// Minification (destination smaller than source): low-quality (bilinear) for smoother downscale.
        /// </summary>
        private static BitmapInterpolationMode ChooseInterpolation(int srcW, int srcH, double dstW, double dstH)
            => (dstW >= srcW || dstH >= srcH) ? BitmapInterpolationMode.None
                                              : BitmapInterpolationMode.LowQuality;

        /// <summary>
        /// When set, replaces <c>MatrixData.YStep</c> in <see cref="GetAspectScales"/>.
        /// Used by <see cref="OrthogonalViewController"/> in Custom scale mode to adjust the
        /// orthogonal axis display size without mutating the underlying matrix data geometry.
        /// Set to <c>null</c> to use the actual <c>YStep</c> (default).
        /// </summary>
        internal double? OrthoAxisStepOverride { get; set; }

        /// <summary>
        /// Returns the aspect scaling factors derived from <see cref="MatrixData"/> step sizes:
        /// (ax, ay) = (XStep/minStep, YStep/minStep).
        /// When <see cref="OrthoAxisStepOverride"/> is set, it replaces <c>YStep</c> in the calculation.
        /// Returns (1, 1) when <see cref="IsAspectCorrectionEnabled"/> is false or data is unavailable.
        /// </summary>
        internal (double ax, double ay) GetAspectScales()
        {
            if (!_isAspectCorrectionEnabled) return (1.0, 1.0);
            var md = MatrixData;
            if (md == null || md.XStep <= 0 || md.YStep <= 0) return (1.0, 1.0);
            double yStep = OrthoAxisStepOverride > 0 ? OrthoAxisStepOverride.Value : md.YStep;
            double minStep = Math.Min(md.XStep, yStep);
            return (md.XStep / minStep, yStep / minStep);
        }

        /// <summary>Returns the effective pixel dimensions (without zoom).</summary>
        private (int w, int h) GetEffectiveBmpPixelDims()
        {
            if (_bitmap == null) return (1, 1);
            int w = _bitmap.PixelSize.Width;
            int h = _bitmap.PixelSize.Height;
            return Transform is ViewTransform.Rotate90CW
                             or ViewTransform.Rotate90CCW
                             or ViewTransform.Transpose
                ? (h, w) : (w, h);
        }

        private Matrix GetTransformMatrix() => Transform switch
        {
            ViewTransform.Rotate90CW => new Matrix(0, 1, -1, 0, 0, 0),
            ViewTransform.Rotate90CCW => new Matrix(0, -1, 1, 0, 0, 0),
            ViewTransform.Rotate180 => new Matrix(-1, 0, 0, -1, 0, 0),
            ViewTransform.FlipH => new Matrix(-1, 0, 0, 1, 0, 0),
            ViewTransform.FlipV => new Matrix(1, 0, 0, -1, 0, 0),
            ViewTransform.Transpose => new Matrix(0, 1, 1, 0, 0, 0),
            _ => Matrix.Identity,
        };

        // ── Axis indicator ────────────────────────────────────────────────────

        private static readonly Pen AxisIndicatorBlack = new Pen(Brushes.Black, 1.6);
        private static readonly Pen AxisIndicatorWhite = new Pen(Brushes.White, 1);
        private static readonly Typeface LabelTypeface = new Typeface("Consolas");
        private const double LabelFontSize = 11.0;

        /// <summary>Returns the rendered size of <paramref name="text"/> in the label typeface.</summary>
        private static (double w, double h) MeasureLabel(string text)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, Brushes.White);
            return (ft.Width, ft.Height);
        }

        /// <summary>Draws <paramref name="text"/> with a 1-px black shadow (white on black, WinForms CrossLine style).</summary>
        private static void DrawShadowLabel(DrawingContext ctx, string text, double x, double y)
        {
            var shadow = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, Brushes.Black);
            ctx.DrawText(shadow, new Point(x + 1, y + 1));
            var label = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, LabelTypeface, LabelFontSize, Brushes.White);
            ctx.DrawText(label, new Point(x, y));
        }

        /// <summary>
        /// Draws a label just to the right of the data area on a horizontal indicator line,
        /// above the line when there is room, otherwise below.
        /// X and Y are always clamped within the viewport (matching WinForms CrossLine.ShowPosition).
        /// </summary>
        private void DrawHLineLabel(DrawingContext ctx, string text,
            double lineY, double rx, double ry, double effW, double effH)
        {
            var (tw, th) = MeasureLabel(text);
            double vpW = Bounds.Width, vpH = Bounds.Height;
            // X: just right of data edge, clamped within viewport (inset when vertical scrollbar is active)
            double xMin = 2.0, xMax = Math.Max(xMin, vpW - tw - 4.0 - RightInset);
            double x = Math.Clamp(rx + effW + 4.0, xMin, xMax);
            // Y: above the line if it fits in the viewport; fall back to below, clamped within viewport
            double yMin = 2.0, yMax = Math.Max(yMin, vpH - th - 2.0);
            double above = lineY - th - 2.0;
            double y = (above >= yMin && above <= yMax) ? above : Math.Clamp(lineY + 2.0, yMin, yMax);
            DrawShadowLabel(ctx, text, x, y);
        }

        /// <summary>
        /// Draws a label just above the data area on a vertical indicator line.
        /// Horizontally: right of the line, falling back to left when there is not enough room.
        /// X and Y are always clamped within the viewport (matching WinForms CrossLine.ShowPosition).
        /// </summary>
        private void DrawVLineLabel(DrawingContext ctx, string text,
            double lineX, double rx, double ry, double effW, double effH)
        {
            var (tw, th) = MeasureLabel(text);
            double vpW = Bounds.Width, vpH = Bounds.Height;
            // Y: just above data top edge; fall back to inside viewport top when no room
            double yMax = Math.Max(2.0, vpH - th - 4.0);
            double above = ry - th - 2.0;
            double y = above >= 2.0 ? above : Math.Clamp(ry + 2.0, 2.0, yMax);
            // X: right of line if it fits; otherwise left; clamped within viewport
            double xMin = 2.0, xMax = Math.Max(xMin, vpW - tw - 4.0);
            double x = (lineX + 4.0 <= xMax) ? lineX + 4.0 : Math.Max(lineX - tw - 4.0, xMin);
            DrawShadowLabel(ctx, text, x, y);
        }

        /// <summary>
        /// Draws the Y data-coordinate label for the main-view crosshair horizontal line.
        /// Placed just to the right of the bitmap edge (outside) when it fits in the viewport;
        /// clamped inside the viewport otherwise (matching WinForms CrossLine.ShowPosition).
        /// Vertically: above the line, falling back to below when there is not enough room.
        /// </summary>
        private void DrawCrosshairHLabel(DrawingContext ctx, string text, double lineY, double bmpRightX)
        {
            var (tw, th) = MeasureLabel(text);
            double vpW = Bounds.Width, vpH = Bounds.Height;
            // X: just right of bitmap edge, clamped within viewport (inset when vertical scrollbar is active)
            double xMax = Math.Max(2.0, vpW - tw - 4.0 - RightInset);
            double x = Math.Clamp(bmpRightX + 4.0, 2.0, xMax);
            // Y: above the line; fall back to below when no room, clamped within viewport
            double yMax = Math.Max(2.0, vpH - th - 2.0);
            double above = lineY - th - 2.0;
            double y = above >= 2.0 ? Math.Min(above, yMax) : Math.Clamp(lineY + 2.0, 2.0, yMax);
            DrawShadowLabel(ctx, text, x, y);
        }

        /// <summary>
        /// Draws the X data-coordinate label for the main-view crosshair vertical line.
        /// Placed just above the bitmap top edge (outside) when it fits in the viewport;
        /// clamped inside the viewport otherwise.
        /// Horizontally: right of the line, falling back to left when there is not enough room.
        /// </summary>
        private void DrawCrosshairVLabel(DrawingContext ctx, string text, double lineX, double bmpTopY)
        {
            var (tw, th) = MeasureLabel(text);
            double vpW = Bounds.Width, vpH = Bounds.Height;
            // Y: just above bitmap top; fall back to inside viewport top when no room
            double yMax = Math.Max(2.0, vpH - th - 4.0);
            double above = bmpTopY - th - 2.0;
            double y = above >= 2.0 ? above : Math.Clamp(bmpTopY + 2.0, 2.0, yMax);
            // X: right of line if it fits; otherwise left; clamped within viewport
            double xMax = Math.Max(2.0, vpW - tw - 4.0);
            double x = (lineX + 4.0 <= xMax)
                ? lineX + 4.0
                : Math.Max(lineX - tw - 4.0, 2.0);
            DrawShadowLabel(ctx, text, x, y);
        }

        /// <summary>
        /// Draws an indicator line (black 3 px + white 1 px) at the bitmap row given by
        /// <see cref="AxisIndicatorPx"/>. The line is drawn in screen space (outside
        /// PushTransform) and its orientation adapts to <see cref="Transform"/>:
        /// <list type="bullet">
        ///   <item><see cref="ViewTransform.None"/> / <see cref="ViewTransform.FlipH"/>: horizontal line at <c>ry + (idx+0.5)*zoom*ay</c></item>
        ///   <item><see cref="ViewTransform.FlipV"/> / <see cref="ViewTransform.Rotate180"/>: horizontal line at <c>ry + effH - (idx+0.5)*zoom*ay</c></item>
        ///   <item><see cref="ViewTransform.Transpose"/> / <see cref="ViewTransform.Rotate90CCW"/>: vertical line at <c>rx + (idx+0.5)*zoom*ay</c></item>
        ///   <item><see cref="ViewTransform.Rotate90CW"/>: vertical line at <c>rx + effW - (idx+0.5)*zoom*ay</c></item>
        /// </list>
        /// </summary>
        private void DrawAxisIndicator(DrawingContext ctx, double rx, double ry, double effW, double effH)
        {
            if (!AxisIndicatorPx.HasValue || _bitmap == null) return;
            var (_, ay) = GetAspectScales();
            int idx = AxisIndicatorPx.Value;
            int bmpRow = FlipY ? _bitmap.PixelSize.Height - 1 - idx : idx;
            string? lbl = AxisIndicatorLabel?.Length > 0 ? AxisIndicatorLabel : null;
            switch (Transform)
            {
                case ViewTransform.None:
                case ViewTransform.FlipH:
                    {
                        double y = ry + (bmpRow + 0.5) * _zoom * ay;
                        if (y < ry || y > ry + effH) return;
                        ctx.DrawLine(AxisIndicatorBlack, new Point(rx, y), new Point(rx + effW, y));
                        ctx.DrawLine(AxisIndicatorWhite, new Point(rx, y), new Point(rx + effW, y));
                        if (lbl != null) DrawHLineLabel(ctx, lbl, y, rx, ry, effW, effH);
                        break;
                    }
                case ViewTransform.FlipV:
                case ViewTransform.Rotate180:
                    {
                        double y = ry + effH - (bmpRow + 0.5) * _zoom * ay;
                        if (y < ry || y > ry + effH) return;
                        ctx.DrawLine(AxisIndicatorBlack, new Point(rx, y), new Point(rx + effW, y));
                        ctx.DrawLine(AxisIndicatorWhite, new Point(rx, y), new Point(rx + effW, y));
                        if (lbl != null) DrawHLineLabel(ctx, lbl, y, rx, ry, effW, effH);
                        break;
                    }
                case ViewTransform.Transpose:
                case ViewTransform.Rotate90CCW:
                    {
                        double x = rx + (bmpRow + 0.5) * _zoom * ay;
                        if (x < rx || x > rx + effW) return;
                        ctx.DrawLine(AxisIndicatorBlack, new Point(x, ry), new Point(x, ry + effH));
                        ctx.DrawLine(AxisIndicatorWhite, new Point(x, ry), new Point(x, ry + effH));
                        if (lbl != null) DrawVLineLabel(ctx, lbl, x, rx, ry, effW, effH);
                        break;
                    }
                case ViewTransform.Rotate90CW:
                    {
                        double x = rx + effW - (bmpRow + 0.5) * _zoom * ay;
                        if (x < rx || x > rx + effW) return;
                        ctx.DrawLine(AxisIndicatorBlack, new Point(x, ry), new Point(x, ry + effH));
                        ctx.DrawLine(AxisIndicatorWhite, new Point(x, ry), new Point(x, ry + effH));
                        if (lbl != null) DrawVLineLabel(ctx, lbl, x, rx, ry, effW, effH);
                        break;
                    }
            }
        }

        // ── Axis indicator helpers ─────────────────────────────────────────────

        /// <summary>Returns true if <paramref name="screenPos"/> is within hit-test distance of the axis indicator line.</summary>
        private bool IsOnAxisIndicator(Point screenPos)
        {
            if (!AxisIndicatorPx.HasValue || _bitmap == null) return false;
            var (_, ay) = GetAspectScales();
            var (effW, effH) = GetEffectiveBmpDims();
            double rx = GetRenderTrans(_transX, effW, Bounds.Width, true);
            double ry = GetRenderTrans(_transY, effH, Bounds.Height, false);
            int idx = AxisIndicatorPx.Value;
            int bmpRow = FlipY ? _bitmap.PixelSize.Height - 1 - idx : idx;
            switch (Transform)
            {
                case ViewTransform.None:
                case ViewTransform.FlipH:
                    {
                        double y = ry + (bmpRow + 0.5) * _zoom * ay;
                        return Math.Abs(screenPos.Y - y) < AxisIndicatorHitTolerance
                            && screenPos.X >= rx && screenPos.X <= rx + effW;
                    }
                case ViewTransform.FlipV:
                case ViewTransform.Rotate180:
                    {
                        double y = ry + effH - (bmpRow + 0.5) * _zoom * ay;
                        return Math.Abs(screenPos.Y - y) < AxisIndicatorHitTolerance
                            && screenPos.X >= rx && screenPos.X <= rx + effW;
                    }
                case ViewTransform.Transpose:
                case ViewTransform.Rotate90CCW:
                    {
                        double x = rx + (bmpRow + 0.5) * _zoom * ay;
                        return Math.Abs(screenPos.X - x) < AxisIndicatorHitTolerance
                            && screenPos.Y >= ry && screenPos.Y <= ry + effH;
                    }
                case ViewTransform.Rotate90CW:
                    {
                        double x = rx + effW - (bmpRow + 0.5) * _zoom * ay;
                        return Math.Abs(screenPos.X - x) < AxisIndicatorHitTolerance
                            && screenPos.Y >= ry && screenPos.Y <= ry + effH;
                    }
            }
            return false;
        }

        /// <summary>Converts a screen position to the nearest bitmap-row index for the axis indicator.</summary>
        private int ScreenToAxisIndicatorIndex(Point screenPos)
        {
            if (_bitmap == null || _zoom == 0) return 0;
            var (_, ay) = GetAspectScales();
            var (effW, effH) = GetEffectiveBmpDims();
            double rx = GetRenderTrans(_transX, effW, Bounds.Width, true);
            double ry = GetRenderTrans(_transY, effH, Bounds.Height, false);
            int maxIdx = _bitmap.PixelSize.Height - 1;
            int idx;
            switch (Transform)
            {
                case ViewTransform.None:
                case ViewTransform.FlipH:
                    idx = (int)Math.Floor((screenPos.Y - ry) / (_zoom * ay));
                    break;
                case ViewTransform.FlipV:
                case ViewTransform.Rotate180:
                    idx = (int)Math.Floor((ry + effH - screenPos.Y) / (_zoom * ay));
                    break;
                case ViewTransform.Transpose:
                case ViewTransform.Rotate90CCW:
                    idx = (int)Math.Floor((screenPos.X - rx) / (_zoom * ay));
                    break;
                case ViewTransform.Rotate90CW:
                    idx = (int)Math.Floor((rx + effW - screenPos.X) / (_zoom * ay));
                    break;
                default:
                    return 0;
            }
            return FlipY ? Math.Clamp(maxIdx - idx, 0, maxIdx) : Math.Clamp(idx, 0, maxIdx);
        }

        // ── Crosshair helpers

        private CrosshairHit GetCrosshairHit(Point screenPos)
        {
            if (!ShowCrosshair || !_crosshairDataPos.HasValue || _bitmap == null) return CrosshairHit.None;
            var sp = DataToScreen(_crosshairDataPos.Value);
            bool nearV = ShowCrosshairV && Math.Abs(screenPos.X - sp.X) < CrosshairHitTolerance;
            bool nearH = ShowCrosshairH && Math.Abs(screenPos.Y - sp.Y) < CrosshairHitTolerance;
            if (nearH && nearV) return CrosshairHit.Cross;
            if (nearH) return CrosshairHit.HLine;
            if (nearV) return CrosshairHit.VLine;
            return CrosshairHit.None;
        }

        /// <summary>
        /// Updates <see cref="_crosshairDataPos"/> from a screen position,
        /// moving only the axes enabled by <see cref="_isDraggingH"/>/<see cref="_isDraggingV"/>.
        /// Each moving axis snaps to the nearest pixel center.
        /// </summary>
        private void ApplyCrosshairDrag(Point screenPos)
        {
            var raw = ScreenToData(screenPos);
            var current = _crosshairDataPos ?? raw;
            double newX = _isDraggingV ? SnapDataX(raw.X) : current.X;
            double newY = _isDraggingH ? SnapDataY(raw.Y) : current.Y;
            _crosshairDataPos = new Point(newX, newY);
        }

        /// <summary>Snaps a data-space X value to the nearest pixel centre.</summary>
        private double SnapDataX(double dataX)
        {
            var md = MatrixData;
            if (md == null || md.XStep == 0) return dataX;
            double px = (dataX - md.XMin) / md.XStep;
            int idx = Math.Clamp((int)Math.Round(px), 0, md.XCount - 1);
            return md.XMin + idx * md.XStep;
        }

        /// <summary>Snaps a data-space Y value to the nearest pixel centre (upward-positive convention).</summary>
        private double SnapDataY(double dataY)
        {
            var md = MatrixData;
            if (md == null || md.YStep == 0) return dataY;
            double py = (md.YMax - dataY) / md.YStep;
            int idx = Math.Clamp((int)Math.Round(py), 0, md.YCount - 1);
            return md.YMax - idx * md.YStep;
        }

        private void DrawCrosshair(DrawingContext ctx)
        {
            if (!ShowCrosshair || !_crosshairDataPos.HasValue || _bitmap == null) return;
            var (bmpW, bmpH) = GetEffectiveBmpDims();
            double rx = GetRenderTrans(_transX, bmpW, Bounds.Width, true);
            double ry = GetRenderTrans(_transY, bmpH, Bounds.Height, false);
            var sp = DataToScreen(_crosshairDataPos.Value);

            using (ctx.PushClip(new Rect(rx, ry, bmpW, bmpH)))
            {
                if (ShowCrosshairH)
                {
                    // Horizontal line: black 3 px + white 1 px (same style as AxisIndicator)
                    ctx.DrawLine(AxisIndicatorBlack, new Point(rx, sp.Y), new Point(rx + bmpW, sp.Y));
                    ctx.DrawLine(AxisIndicatorWhite, new Point(rx, sp.Y), new Point(rx + bmpW, sp.Y));
                }
                if (ShowCrosshairV)
                {
                    // Vertical line: black 3 px + white 1 px
                    ctx.DrawLine(AxisIndicatorBlack, new Point(sp.X, ry), new Point(sp.X, ry + bmpH));
                    ctx.DrawLine(AxisIndicatorWhite, new Point(sp.X, ry), new Point(sp.X, ry + bmpH));
                }
            }

            // Labels show data coordinates.
            // _crosshairDataPos already stores physical data values; if YStep < 0 the Y value
            // increases upward naturally ? no additional inversion is needed.
            var md = MatrixData;
            if (md != null && md.XStep != 0 && md.YStep != 0)
            {
                using (ctx.PushClip(new Rect(Bounds.Size)))
                {
                    if (ShowCrosshairH)
                    {
                        string yu = md.YUnit.Length > 0 ? $" {md.YUnit}" : "";
                        DrawCrosshairHLabel(ctx, $"y={_crosshairDataPos.Value.Y:G4}{yu}", sp.Y, rx + bmpW);
                    }
                    if (ShowCrosshairV)
                    {
                        string xu = md.XUnit.Length > 0 ? $" {md.XUnit}" : "";
                        DrawCrosshairVLabel(ctx, $"x={_crosshairDataPos.Value.X:G4}{xu}", sp.X, ry);
                    }
                }
            }
        }
    }

}
