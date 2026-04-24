using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using MxPlot.UI.Avalonia.Views;
using System.IO;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Integrates <see cref="RenderSurface"/> with horizontal/vertical <see cref="ScrollBar"/> controls.
    /// Layout is built programmatically (no AXAML dependency).
    /// </summary>
    public class MxView : UserControl
    {
        // ── Rendering options (forwarded to RenderSurface) ─────────────────

        public static readonly StyledProperty<bool> IsFixedRangeProperty =
            AvaloniaProperty.Register<MxView, bool>(nameof(IsFixedRange));
        public static readonly StyledProperty<double> FixedMinProperty =
            AvaloniaProperty.Register<MxView, double>(nameof(FixedMin));
        public static readonly StyledProperty<double> FixedMaxProperty =
            AvaloniaProperty.Register<MxView, double>(nameof(FixedMax), defaultValue: 1.0);
        public static readonly StyledProperty<bool> IsInvertedColorProperty =
            AvaloniaProperty.Register<MxView, bool>(nameof(IsInvertedColor));
        public static readonly StyledProperty<int> LutDepthProperty =
            AvaloniaProperty.Register<MxView, int>(nameof(LutDepth));

        public bool IsFixedRange { get => GetValue(IsFixedRangeProperty); set => SetValue(IsFixedRangeProperty, value); }
        public double FixedMin { get => GetValue(FixedMinProperty); set => SetValue(FixedMinProperty, value); }
        public double FixedMax { get => GetValue(FixedMaxProperty); set => SetValue(FixedMaxProperty, value); }
        public bool IsInvertedColor { get => GetValue(IsInvertedColorProperty); set => SetValue(IsInvertedColorProperty, value); }
        public int LutDepth { get => GetValue(LutDepthProperty); set => SetValue(LutDepthProperty, value); }

        /// <summary>Fired each time the auto value range is computed. Carries (Min, Max).</summary>
        public event EventHandler<(double Min, double Max)>? AutoRangeComputed;

        /// <summary>Raised after content is successfully copied to the clipboard via Ctrl+C.</summary>
        public event EventHandler<string>? CopiedToClipboard;

        /// <summary>Raised when the user chooses Crop from the surface context menu.</summary>
        public event EventHandler? CropRequested;

        /// <summary>Returns the computed value range for the current frame.</summary>
        public (double Min, double Max) ScanCurrentFrameRange() => _surface.ScanCurrentFrameRange();

        // ── Avalonia Styled Properties ────────────────────────────────────────

        public static readonly StyledProperty<IMatrixData?> MatrixDataProperty =
            AvaloniaProperty.Register<MxView, IMatrixData?>(nameof(MatrixData));

        public static readonly StyledProperty<LookupTable?> LutProperty =
            AvaloniaProperty.Register<MxView, LookupTable?>(nameof(Lut),
                defaultValue: ColorThemes.Grayscale);

        public static readonly StyledProperty<int> FrameIndexProperty =
            AvaloniaProperty.Register<MxView, int>(nameof(FrameIndex), defaultValue: 0);

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

        /// <summary>Current zoom factor (1.0 = 100%).</summary>
        public double Zoom => _surface.Zoom;
        /// <summary>True when the image is fitted to the viewport.</summary>
        public bool IsFitToView => _surface.IsFitToView;
        /// <summary>Raw horizontal translation before clamping (used for cross-view sync).</summary>
        public double RawTransX => _surface.RawTransX;
        /// <summary>Raw vertical translation before clamping (used for cross-view sync).</summary>
        public double RawTransY => _surface.RawTransY;
        /// <summary>Aspect scale factors derived from the current MatrixData step sizes.</summary>
        internal (double ax, double ay) GetAspectScales() => _surface.GetAspectScales();

        /// <summary>Returns the current overlay viewport for world-to-screen coordinate conversions.</summary>
        internal AvaloniaViewport GetOverlayViewport() => _surface.GetOverlayViewport();

        /// <summary>Sets zoom to an arbitrary factor anchored to the viewport centre. Exits fit-to-view mode.</summary>
        public void SetZoom(double zoom) => _surface.SetZoom(zoom);

        /// <summary>
        /// Padding (in DIPs) added around the bitmap when fitting to the viewport.
        /// The bitmap is scaled so that this many pixels remain on each side.
        /// Defaults to 3. Set to 0 to fit edge-to-edge.
        /// </summary>
        public double BitmapPadding
        {
            get => _surface.BitmapPadding;
            set => _surface.BitmapPadding = value;
        }

        /// <summary>
        /// Controls where the bitmap is pinned when smaller than the viewport.
        /// Setting this re-applies <see cref="FitToView"/> (if active) or snaps to the new position.
        /// </summary>
        public ContentAlignment ContentAlignment
        {
            get => _surface.ContentAlignment;
            set => _surface.ContentAlignment = value;
        }

        /// <summary>
        /// Scrolls so the axis indicator appears in the viewport.
        /// When <paramref name="forceCenter"/> is <c>true</c>, always centres the indicator
        /// even if it is already visible (used after zoom changes). Otherwise no-op if already visible.
        /// </summary>
        public void ScrollToAxisIndicator(bool forceCenter = false) => _surface.ScrollToAxisIndicator(forceCenter);

        /// <summary>
        /// Fired on the UI thread immediately after the rendered bitmap is updated.
        /// Forwarded from <see cref="RenderSurface.BitmapRefreshed"/> and re-exposed here
        /// so that <see cref="MxPlot.UI.Avalonia.Views.MatrixPlotter.ViewUpdated"/> can forward it
        /// further without depending on the <c>internal sealed</c> <see cref="RenderSurface"/> directly.
        /// </summary>
        internal event EventHandler? BitmapRefreshed
        {
            add => _surface.BitmapRefreshed += value;
            remove => _surface.BitmapRefreshed -= value;
        }

        /// <summary>
        /// Returns a reference to the internal rendered <see cref="WriteableBitmap"/>,
        /// or <c>null</c> when no data is loaded.
        /// The bitmap is valid for reading immediately after <see cref="BitmapRefreshed"/> fires.
        /// </summary>
        internal WriteableBitmap? GetRenderedBitmap() => _surface.Bitmap;

        /// <summary>Fired when zoom, translation, or bitmap size changes.</summary>
        public event EventHandler? ScrollStateChanged;

        // ── Busy indicator ────────────────────────────────────────────────────

        /// <summary>
        /// When <c>true</c>, shows a small spinning indicator in the top-left corner
        /// to signal that background work (e.g. projection computation) is in progress.
        /// </summary>
        public bool IsBusy
        {
            get => _busyIndicator.IsActive;
            set => _busyIndicator.IsActive = value;
        }

        // ── Crosshair API ─────────────────────────────────────────────────────

        /// <summary>When true, a draggable crosshair overlay is drawn on the surface.</summary>
        public bool ShowCrosshair
        {
            get => _surface.ShowCrosshair;
            set
            {
                _surface.ShowCrosshair = value;
                _surface.InvalidateVisual();
            }
        }

        /// <summary>When false, hides the horizontal crosshair line (Y-position indicator).</summary>
        public bool ShowCrosshairH
        {
            get => _surface.ShowCrosshairH;
            set
            {
                _surface.ShowCrosshairH = value;
                _surface.InvalidateVisual();
            }
        }

        /// <summary>When false, hides the vertical crosshair line (X-position indicator).</summary>
        public bool ShowCrosshairV
        {
            get => _surface.ShowCrosshairV;
            set
            {
                _surface.ShowCrosshairV = value;
                _surface.InvalidateVisual();
            }
        }

        /// <summary>When true, pixels are stretched proportionally to their physical step sizes.</summary>
        public bool IsAspectCorrectionEnabled
        {
            get => _surface.IsAspectCorrectionEnabled;
            set => _surface.IsAspectCorrectionEnabled = value;
        }

        /// <summary>Moves the crosshair to the given data-space position and redraws.</summary>
        public void SetCrosshairDataPosition(Point dataPos) => _surface.SetCrosshairDataPosition(dataPos);

        /// <summary>
        /// When set, the position overlay on this view shows the overlay's info text instead of
        /// the mouse coordinate/value. Set to <c>null</c> to restore normal mouse display.
        /// </summary>
        public string? OverlayInfoText
        {
            get => _surface.OverlayInfoText;
            set
            {
                if (_surface.OverlayInfoText == value) return;
                _surface.OverlayInfoText = value;
                _surface.InvalidateVisual();
            }
        }

        /// <summary>Fired when the user drags the crosshair to a new data-space position.</summary>
        public event EventHandler<Point>? CrosshairMoved
        {
            add => _surface.CrosshairMoved += value;
            remove => _surface.CrosshairMoved -= value;
        }

        // ── ViewTransform / AxisIndicator API ────────────────────────────────

        /// <summary>Visual transform applied to the rendered bitmap.</summary>
        public ViewTransform Transform
        {
            get => _surface.Transform;
            set => _surface.Transform = value;
        }

        /// <summary>
        /// When true, the bitmap Y-axis is flipped so YMax is at the screen top (scientific convention).
        /// Defaults to true. Set to false for orthogonal side views whose ViewTransform handles orientation.
        /// </summary>
        public bool FlipY
        {
            get => _surface.FlipY;
            set => _surface.FlipY = value;
        }

        /// <summary>
        /// When set, draws an indicator line at the given bitmap-pixel row.
        /// See <see cref="RenderSurface.AxisIndicatorPx"/> for orientation details.
        /// </summary>
        public int? AxisIndicatorPx
        {
            get => _surface.AxisIndicatorPx;
            set
            {
                _surface.AxisIndicatorPx = value;
                _surface.InvalidateVisual();
            }
        }

        /// <summary>Text label drawn near the axis indicator line (e.g. <c>"Z=1.250 mm"</c>). Set by <see cref="OrthogonalViewController"/>.</summary>
        public string? AxisIndicatorLabel
        {
            get => _surface.AxisIndicatorLabel;
            set
            {
                _surface.AxisIndicatorLabel = value;
                _surface.InvalidateVisual();
            }
        }

        /// <summary>Fired when the user drags the axis indicator to a new bitmap-row index.</summary>
        public event EventHandler<int>? AxisIndicatorDragged
        {
            add => _surface.AxisIndicatorDragged += value;
            remove => _surface.AxisIndicatorDragged -= value;
        }

        /// <summary>Fired when the user releases the axis indicator after dragging.</summary>
        public event EventHandler? AxisIndicatorDragEnded
        {
            add => _surface.AxisIndicatorDragEnded += value;
            remove => _surface.AxisIndicatorDragEnded -= value;
        }

        /// <summary>True while the user is actively dragging the axis indicator.</summary>
        public bool IsAxisIndicatorDragging => _surface.IsAxisIndicatorDragging;

        // ── Child controls ────────────────────────────────────────────────────

        private readonly RenderSurface _surface;
        private readonly ScrollBar _hScrollBar;
        private readonly ScrollBar _vScrollBar;
        private readonly BusyIndicator _busyIndicator;

        private bool _syncingScrollBars;

        /// <summary>
        /// Manages user overlay objects drawn on this view.
        /// Attach overlays via <see cref="OverlayManager.AddObject"/> or
        /// start interactive creation with <see cref="OverlayManager.StartCreating"/>.
        /// </summary>
        public OverlayManager OverlayManager { get; }

        /// <summary>
        /// Converts a point in <see cref="RenderSurface"/>-local coordinates to OS screen pixel coordinates.
        /// Use this to position floating windows near overlay objects.
        /// </summary>
        internal PixelPoint SurfacePointToScreen(Point surfaceLocalPos) =>
            _surface.PointToScreen(surfaceLocalPos);

        // ── Constructor (builds layout in code) ───────────────────────────────

        public MxView()
        {
            _surface = new RenderSurface { ClipToBounds = true };

            // Wire up overlay manager
            OverlayManager = new OverlayManager
            {
                InvalidateVisual = () => _surface.InvalidateVisual(),
                SetCursor = cursor => _surface.Cursor = cursor,
                GetViewport = () => _surface.GetOverlayViewport(),
                SetClipboardText = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask,
                GetClipboardText = () => TopLevel.GetTopLevel(this)?.Clipboard?.GetTextAsync() ?? Task.FromResult<string?>(null),
            };
            _surface.OverlayManager = OverlayManager;
            OverlayManager.Copied += (_, _) => CopiedToClipboard?.Invoke(this, "Overlay copied to clipboard");
            _hScrollBar = new ScrollBar
            {
                Orientation = Orientation.Horizontal,
                Minimum = 0,
                IsVisible = false,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Opacity = 0.75,
            };
            _vScrollBar = new ScrollBar
            {
                Orientation = Orientation.Vertical,
                Minimum = 0,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Opacity = 0.75,
            };

            _busyIndicator = new BusyIndicator();

            var panel = new Panel();
            panel.Children.Add(_surface);
            panel.Children.Add(_hScrollBar);
            panel.Children.Add(_vScrollBar);
            panel.Children.Add(_busyIndicator);

            Content = panel;

            // Surface → scrollbar sync + forward ScrollStateChanged
            _surface.ScrollStateChanged += (_, _) =>
            {
                SyncScrollBars();
                ScrollStateChanged?.Invoke(this, EventArgs.Empty);
            };
            _surface.AutoRangeComputed += (_, args) => AutoRangeComputed?.Invoke(this, args);

            // Scrollbar → surface
            _hScrollBar.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !_syncingScrollBars)
                    _surface.SetScrollX(_hScrollBar.Value);
            };
            _vScrollBar.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty && !_syncingScrollBars)
                    _surface.SetScrollY(_vScrollBar.Value);
            };

            // Right-click → dynamic context menu with overlay creation items
            _surface.ContextRequested += OnSurfaceContextRequested;

            // Ctrl+C: overlay copy when selected (handled by OverlayManager), else image copy dialog
            _surface.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control && !e.Handled)
                {
                    e.Handled = true;
                    await ShowCopyDialogAsync();
                }
            };
        }

        // ── Forward property changes to surface ───────────────────────────────

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == MatrixDataProperty)
            {
                _surface.Lut = Lut;        // sync LUT first so AllocateBitmap doesn't bail on null
                _surface.MatrixData = MatrixData;
            }
            else if (change.Property == LutProperty) _surface.Lut = Lut;
            else if (change.Property == FrameIndexProperty) _surface.FrameIndex = FrameIndex;
            else if (change.Property == IsFixedRangeProperty) _surface.IsFixedRange = IsFixedRange;
            else if (change.Property == FixedMinProperty) _surface.FixedMin = FixedMin;
            else if (change.Property == FixedMaxProperty) _surface.FixedMax = FixedMax;
            else if (change.Property == IsInvertedColorProperty) _surface.IsInvertedColor = IsInvertedColor;
            else if (change.Property == LutDepthProperty) _surface.LutLevel = LutDepth;
        }

        // ── Scrollbar synchronisation ─────────────────────────────────────────

        private void SyncScrollBars()
        {
            if (_surface.Bitmap == null) return;
            _syncingScrollBars = true;
            try
            {
                var (bmpW, bmpH) = _surface.GetEffectiveBmpDims();
                double vpW = _surface.Bounds.Width;
                double vpH = _surface.Bounds.Height;
                double m = _surface.PanMargin;

                bool showH = bmpW - vpW >= 1.0;
                bool showV = bmpH - vpH >= 1.0;

                _hScrollBar.IsVisible = showH;
                if (showH)
                {
                    double excessW = bmpW - vpW + 2 * m;
                    _hScrollBar.Maximum = excessW;
                    _hScrollBar.ViewportSize = vpW;
                    _hScrollBar.Value = Math.Clamp(m - _surface.TransX, 0, excessW);
                }

                _vScrollBar.IsVisible = showV;
                if (showV)
                {
                    double excessH = bmpH - vpH + 2 * m;
                    _vScrollBar.Maximum = excessH;
                    _vScrollBar.ViewportSize = vpH;
                    _vScrollBar.Value = Math.Clamp(m - _surface.TransY, 0, excessH);
                }
                // Keep right-aligned labels clear of the vertical scrollbar
                double sbW = _vScrollBar.Bounds.Width > 0 ? _vScrollBar.Bounds.Width : 14.0;
                _surface.RightInset = showV ? sbW : 0.0;
            }
            finally { _syncingScrollBars = false; }
        }

        // ── Public helpers ────────────────────────────────────────────────────

        /// <summary>Resets zoom and centers the image to fill the viewport.</summary>
        public void FitToView() => _surface.FitToView();

        /// <summary>
        /// Requests a redraw of the surface without rebuilding the bitmap.
        /// Used internally after axis-scale property edits where pixel values are unchanged.
        /// </summary>
        internal void InvalidateSurface() => _surface.InvalidateVisual();

        /// <summary>
        /// Rebuilds the bitmap from the current <see cref="MatrixData"/> pixel values and redraws.
        /// Call this after modifying data values in-place (e.g. after writing new samples into the array).
        /// </summary>
        public void Refresh() => _surface.RebuildAndInvalidate();

        /// <summary>
        /// Applies zoom and translation directly for cross-view sync.
        /// Exits fit-to-view mode and updates the scrollbars.
        /// </summary>
        public void ApplyZoomAndTrans(double zoom, double tx, double ty)
        {
            _surface.ApplyZoomAndTrans(zoom, tx, ty);
            SyncScrollBars();
        }

        /// <summary>True when the horizontal scrollbar is visible (content wider than the viewport).</summary>
        internal bool HasHorizontalScrollbar => _hScrollBar.IsVisible;

        /// <summary>True when the vertical scrollbar is visible (content taller than the viewport).</summary>
        internal bool HasVerticalScrollbar => _vScrollBar.IsVisible;

        /// <summary>
        /// Returns the post-transform, aspect-corrected bitmap dimensions in screen pixels
        /// at the current zoom level. Accounts for <see cref="ViewTransform"/> rotations.
        /// </summary>
        internal (double w, double h) GetEffectiveBmpDims() => _surface.GetEffectiveBmpDims();
        internal (double w, double h) GetNaturalDims() => _surface.GetNaturalDims();

        // ── Image rendering / export ─────────────────────────────────────────

        /// <summary>
        /// Renders the current frame at the specified pixel dimensions into a new bitmap.
        /// Applies <see cref="ViewTransform"/>, aspect correction, and optionally user overlays.
        /// Returns <c>null</c> when no data is loaded.
        /// </summary>
        public RenderTargetBitmap? RenderToBitmap(int width, int height, bool withOverlays = false)
        {
            if (_surface.Bitmap == null) return null;
            OverlayManager.BeginCapture(withOverlays);
            try
            {
                var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
                using (var ctx = rtb.CreateDrawingContext())
                    _surface.RenderClean(ctx, width, height, withOverlays);
                return rtb;
            }
            finally
            {
                OverlayManager.EndCapture();
                _surface.InvalidateVisual();
            }
        }

        /// <summary>
        /// Saves the current frame as a PNG file at <paramref name="path"/>.
        /// Overwrites existing files without confirmation.
        /// </summary>
        public void SaveAsPng(string path, int width, int height, bool withOverlays = false)
        {
            var rtb = RenderToBitmap(width, height, withOverlays);
            rtb?.Save(path);
        }

        // ── Clipboard copy ────────────────────────────────────────────────────

        /// <summary>
        /// Shows the copy dialog, then dispatches to
        /// <see cref="CopyImageAsync"/> or <see cref="CopyCsvAsync"/> based on the user's choice.
        /// </summary>
        internal async Task ShowCopyDialogAsync()
        {
            if (_surface.Bitmap == null) return;
            var owner = TopLevel.GetTopLevel(_surface) as Window;
            if (owner == null) return;

            var (natW, natH) = _surface.GetNaturalDims();
            int naturalW = Math.Max(1, (int)Math.Round(natW));
            int naturalH = Math.Max(1, (int)Math.Round(natH));

            var (effW, effH) = _surface.GetEffectiveBmpDims();
            int displayW = Math.Max(1, (int)Math.Round(effW));
            int displayH = Math.Max(1, (int)Math.Round(effH));

            // Build a correctly-transformed thumbnail (applies ViewTransform such as XZ/ZY flips)
            const double maxThumbW = 78, maxThumbH = 58;
            double ta = (double)naturalW / naturalH;
            int tw = ta >= maxThumbW / maxThumbH ? (int)maxThumbW : Math.Max(1, (int)Math.Round(maxThumbH * ta));
            int th = ta >= maxThumbW / maxThumbH ? Math.Max(1, (int)Math.Round(maxThumbW / ta)) : (int)maxThumbH;
            var thumb = RenderToBitmap(tw, th);

            var dlg = new CopyImageDialog(thumb, naturalW, naturalH, displayW, displayH,
                refreshPreview: ov => RenderToBitmap(tw, th, ov));
            await dlg.ShowDialog(owner);
            if (dlg.Result == null) return;

            var res = dlg.Result;
            if (res.Mode == CopyMode.Text)
            {
                await CopyCsvAsync(res.Separator);
                CopiedToClipboard?.Invoke(this, "Data copied to clipboard");
            }
            else
            {
                await CopyImageAsync(res.Width, res.Height, res.WithOverlays);
                CopiedToClipboard?.Invoke(this, "Image copied to clipboard");
            }
        }

        /// <summary>
        /// Renders the current frame at the specified pixel dimensions and copies it to the clipboard as a bitmap.
        /// </summary>
        public async Task CopyImageAsync(int width, int height, bool withOverlays = false)
        {
            var rtb = RenderToBitmap(width, height, withOverlays);
            if (rtb == null) return;
            var clipboard = TopLevel.GetTopLevel(_surface)?.Clipboard;
            if (clipboard == null) return;

            // Cache PNG bytes for same-process retrieval on macOS.
            // On macOS, Avalonia's GetDataAsync cannot read back native NSPasteboard image
            // data written by SetBitmapAsync, so MxPlotAppWindow.GetClipboardBitmapAsync
            // uses this cache when the in-process clipboard format is detected.
            using var ms = new MemoryStream();
            rtb.Save(ms);
            LastCopiedPng = ms.ToArray();

            await clipboard.SetBitmapAsync(rtb);
        }

        /// <summary>
        /// PNG bytes of the most recently copied image within this process.
        /// Read by <c>MxPlotAppWindow.GetClipboardBitmapAsync</c> on macOS where
        /// <c>GetDataAsync</c> cannot retrieve native NSPasteboard image data.
        /// </summary>
        public static byte[]? LastCopiedPng { get; private set; }

        /// <summary>
        /// Copies the current frame data to the clipboard as delimited text.
        /// </summary>
        public async Task CopyCsvAsync(string separator = "\t")
        {
            var data = MatrixData;
            if (data == null) return;

            int frame = FrameIndex;
            var sb = new StringBuilder();

            for (int y = data.YCount - 1; y >= 0; y--) // Iterate in Y-reversed order so that the first line corresponds to the top of the displayed image
            {
                for (int x = 0; x < data.XCount; x++)
                {
                    if (x > 0) sb.Append(separator);
                    sb.Append(data.GetValueAt(x, y, frame).ToString("G6", CultureInfo.InvariantCulture));
                }
                sb.AppendLine();
            }

            var clipboard = TopLevel.GetTopLevel(_surface)?.Clipboard;
            if (clipboard == null) return;
            await clipboard.SetTextAsync(sb.ToString());
        }

        // ── Overlay context menu ──────────────────────────────────────────────

        private void OnSurfaceContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            e.TryGetPosition(_surface, out var pos);

            Debug.WriteLine($"[MxView.OnSurfaceContextRequested] e.Handled = {e.Handled}, pos={pos}");

            var menu = new ContextMenu();


            // ① Object-specific items (click landed on an existing overlay)
            bool hasObjectItems = false;
            foreach (var oi in OverlayManager.GetContextMenuItems(pos))
            {
                menu.Items.Add(BuildMenuItem(oi));
                hasObjectItems = true;
            }
            if (hasObjectItems)
            {
                // Overlay has items: show only the overlay context menu, suppress the standard MxView menu.
                menu.Open(_surface);
                e.Handled = true;
                return;
            }

            // ② Copy image to clipboard
            var copyItem = new MenuItem { Header = "Copy Image\u2026", FontSize = 11 };
            copyItem.Icon = MakeIconSlot(MenuIcons.Image);
            copyItem.Click += async (_, _) => await ShowCopyDialogAsync();
            menu.Items.Add(copyItem);
            menu.Items.Add(new Separator());

            // ③ Crop shortcut
            menu.Items.Add(MakeItem("Crop", () => CropRequested?.Invoke(this, EventArgs.Empty), MenuIcons.AutoFix));
            menu.Items.Add(new Separator());

            // ④ Overlay creation submenu
            var overlayMenu = new MenuItem { Header = "Overlay", FontSize = 11 };
            overlayMenu.Icon = MakeIconSlot(MenuIcons.Layers);

            overlayMenu.Items.Add(MakeItem("Add Line", () => OverlayManager.StartCreating(new LineObject()), MenuIcons.Plus));
            overlayMenu.Items.Add(MakeItem("Add Rectangle", () => OverlayManager.StartCreating(new RectObject()), MenuIcons.Plus));
            overlayMenu.Items.Add(MakeItem("Add Oval", () => OverlayManager.StartCreating(new OvalObject()), MenuIcons.Plus));
            overlayMenu.Items.Add(MakeItem("Add Target", () => OverlayManager.StartCreating(new TargetingObject()), MenuIcons.Plus));
            overlayMenu.Items.Add(MakeItem("Add Text", () => OverlayManager.StartCreating(new TextObject()), MenuIcons.Plus));
            overlayMenu.Items.Add(MakeItem("Select Area", () => OverlayManager.StartCreating(new SelectionRect()), MenuIcons.SelectRect));
            overlayMenu.Items.Add(new Separator());
            overlayMenu.Items.Add(MakeItem("Paste", () => OverlayManager.PasteOverlays(), MenuIcons.Paste));
            overlayMenu.Items.Add(MakeItem("Clear All", () => OverlayManager.ClearAll(), MenuIcons.TrashCan));
            overlayMenu.Items.Add(MakeItem(
                OverlayManager.OverlaysVisible ? "Hide Overlays" : "Show Overlays",
                () => OverlayManager.OverlaysVisible = !OverlayManager.OverlaysVisible,
                OverlayManager.OverlaysVisible ? MenuIcons.EyeOff : MenuIcons.Eye));

            menu.Items.Add(overlayMenu);

            menu.Open(_surface);
            e.Handled = true;
        }

        private static Control BuildMenuItem(OverlayMenuItem oi)
        {
            if (oi.IsSeparator) return new Separator();

            var item = new MenuItem { Header = oi.Header, FontSize = 11 };
            if (oi.Icon != null)
                item.Icon = MakeIconSlot(oi.Icon);
            if (oi.Tooltip != null)
                ToolTip.SetTip(item, oi.Tooltip);

            if (oi.Children != null)
            {
                // Submenu: populate child items recursively
                foreach (var child in oi.Children)
                    item.Items.Add(BuildMenuItem(child));
            }
            else
            {
                if (oi.IsChecked)
                {
                    item.ToggleType = MenuItemToggleType.Radio;
                    item.IsChecked = true;
                }
                item.Click += (_, _) => oi.Click();
            }
            return item;
        }

        private static MenuItem MakeItem(string header, Action click, Geometry? icon = null)
        {
            var item = new MenuItem { Header = header, FontSize = 11 };
            if (icon != null)
                item.Icon = MakeIconSlot(icon);
            item.Click += (_, _) => click();
            return item;
        }

        private static PathIcon MakeIconSlot(Geometry icon)
        {
            var pi = new PathIcon { Data = icon, Width = 12, Height = 12 };
            var brush = MenuIcons.DefaultBrush(icon);
            if (brush != null) pi.Foreground = brush;
            return pi;
        }
    }
}
