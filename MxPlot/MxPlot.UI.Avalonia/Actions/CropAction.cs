using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Overlays.Shapes;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Linq;

namespace MxPlot.UI.Avalonia.Actions
{
    internal enum CropRole { Leader, Follower }

    /// <summary>ROI position and size in data pixel-index space (XIndex=0/YIndex=0 at bottom-left), shared between leader and follower windows.</summary>
    internal readonly record struct CropRoiBounds(int X, int Y, int Width, int Height, bool ReplaceData = false,
        bool ThisFrameOnly = false, int LeaderFrameIndex = 0);

    /// <summary>
    /// Interactive crop action.
    /// <list type="bullet">
    ///   <item>Creates an ROI overlay on the main view.</item>
    ///   <item>When orthogonal views are active, also creates synced ROIs on the XZ (Bottom) and ZY (Right) views.</item>
    ///   <item>Shows an Apply / Cancel panel anchored to the top-left corner of the main view.</item>
    ///   <item>On Apply: executes <see cref="CropOperation"/> and fires <see cref="Completed"/>.</item>
    ///   <item>On Cancel or forced <see cref="Dispose"/>: removes all overlays silently.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// ROI sync rules (all in pixel-edge space):<br/>
    /// <c>XY.X / XY.Width</c> ↔ <c>XZ.X / XZ.Width</c> (shared X range)<br/>
    /// <c>XY.Y / XY.Height</c> ↔ <c>ZY.X / ZY.Width</c> (shared Y range)<br/>
    /// <c>XZ.Y / XZ.Height</c> ↔ <c>ZY.Y / ZY.Height</c> (shared Z/depth range)<br/>
    /// <para>
    /// TODO: When orthogonal views are active, also substack along the depth axis on Apply
    /// (zStart = <c>(int)(_xzRoi.Y + 0.5)</c>, zCount = <c>(int)_xzRoi.Height</c>).
    /// </para>
    /// </remarks>
    public sealed class CropAction : IPlotterAction
    {
        private PlotterActionContext? _ctx;
        private RoiObject? _xyRoi;
        private RoiObject? _xzRoi;   // BottomView: X horizontal, Z vertical
        private RoiObject? _zyRoi;   // RightView data space: Y horizontal, Z vertical
        private Border? _panel;
        private EventHandler? _layoutHandler;
        private EventHandler? _themeHandler;
        private bool _syncing;
        private bool _disposed;
        private int _dataYCount;
        private int _dataZCount;
        private readonly CropRole _role;
        private double _offsetX;
        private double _offsetY;
        private double _lastLeaderX;
        private double _lastLeaderY;
        private CropRoiBounds? _finalBounds;
        private OverlayLayer? _overlay;
        private EventHandler? _sizeEditHandler;

        // Panel UI elements
        private TextBlock? _infoText;
        private CheckBox? _replaceDataChk;
        private CheckBox? _thisFrameOnlyChk;
        private TextBlock? _virtualWarning;

        public event EventHandler<IMatrixData?>? Completed;
        public event EventHandler? Cancelled;

        // Persisted across invocations for UX convenience.
        private static bool _lastReplaceData = false;
        private static bool _lastThisFrameOnly = false;

        /// <summary>Parameters computed on Apply click, read by the host after Completed fires.</summary>
        public CropParameters? Parameters { get; private set; }

        /// <summary>Describes the crop region and output options chosen by the user.</summary>
        /// <param name="FrameIndex">The frame index that was cropped. Meaningful only when <see cref="ThisFrameOnly"/> is <c>true</c>.</param>
        public sealed record CropParameters(int X, int Y, int Width, int Height, bool ReplaceData, bool ThisFrameOnly, int FrameIndex = 0);

        /// <summary>The role of this action instance (Leader or Follower).</summary>
        internal CropRole Role => _role;

        /// <summary>
        /// For <see cref="CropRole.Follower"/>: set the initial ROI bounds from the leader before calling <see cref="Invoke"/>.
        /// </summary>
        internal CropRoiBounds InitialBounds { get; set; }

        /// <summary>
        /// For <see cref="CropRole.Leader"/>: optional initial XY ROI position/size in data pixel-index space
        /// (origin bottom-left). When set, overrides the default 25%/50% placement in <see cref="BuildXyRoi"/>.
        /// </summary>
        internal CropRoiBounds? InitialLeaderBounds { get; set; }

        /// <summary>Current XY ROI bounds in data pixel-index space (origin bottom-left), or <c>null</c> before <see cref="Invoke"/>.</summary>
        internal CropRoiBounds? CurrentBounds =>
            _xyRoi == null || _ctx?.Data == null ? null : XyRoiToDataBounds();

        /// <summary>
        /// The XY ROI bounds captured immediately before <see cref="Completed"/> was fired.
        /// Valid after Apply/ForceApply; <c>null</c> before that.
        /// </summary>
        internal CropRoiBounds? FinalBounds => _finalBounds;

        /// <summary>
        /// For <see cref="CropRole.Follower"/>: set before <see cref="ForceApply"/> to inherit
        /// the leader's Replace-data setting (follower panel has no checkbox).
        /// </summary>
        internal bool ReplaceData { get; set; }

        /// <summary>
        /// For <see cref="CropRole.Follower"/>: set before <see cref="ForceApply"/> to inherit
        /// the leader's This-frame-only setting.
        /// </summary>
        internal bool ThisFrameOnly { get; set; }

        /// <summary>
        /// For <see cref="CropRole.Follower"/>: the leader's frame index when <see cref="ThisFrameOnly"/> is <c>true</c>.
        /// The follower will use this as a hint; if the follower's frame count is smaller,
        /// it falls back to its own <see cref="IMatrixData.ActiveIndex"/>.
        /// </summary>
        internal int LeaderFrameIndex { get; set; }

        /// <summary>
        /// Raised when the XY ROI position or size changes in <see cref="CropRole.Leader"/> mode.
        /// </summary>
        internal event EventHandler<CropRoiBounds>? RoiBoundsChanged;

        // ── Constructor ───────────────────────────────────────────────────────

        public CropAction() { }

        internal CropAction(CropRole role)
        {
            _role = role;
        }

        // ── IPlotterAction ────────────────────────────────────────────────────

        public void Invoke(PlotterActionContext ctx)
        {
            _ctx = ctx;

            if (_role == CropRole.Follower)
            {
                _xyRoi = BuildXyRoiFollower(ctx.Data);
                _xyRoi.BoundsChanged += (_, _) =>
                {
                    if (_xyRoi != null && _ctx?.Data != null)
                    {
                        int actualX = (int)(_xyRoi.X + 0.5);
                        int actualBitmapY = (int)(_xyRoi.Y + 0.5);
                        int actualDataY = _ctx.Data.YCount - actualBitmapY - (int)_xyRoi.Height;
                        _offsetX = actualX - _lastLeaderX;
                        _offsetY = actualDataY - _lastLeaderY;
                    }
                    UpdateInfoText();
                };
                ctx.MainView.OverlayManager.AddObject(_xyRoi);
            }
            else
            {
                _xyRoi = BuildXyRoi(ctx.Data, InitialLeaderBounds);
                _xyRoi.BoundsChanged += (_, _) =>
                {
                    UpdateInfoText();
                    PositionPanel();
                    if (!_syncing && _xyRoi != null)
                        RoiBoundsChanged?.Invoke(this, XyRoiToDataBounds());
                };
                _sizeEditHandler = async (s, e) => await OnXyRoiSizeEditRequestedAsync();
                _xyRoi.SizeEditRequested += _sizeEditHandler;
                _xyRoi.ShowSizeEditHint = true;
                ctx.MainView.OverlayManager.AddObject(_xyRoi);

                if (ctx.OrthoPanel != null && ctx.DepthAxisName != null && ctx.Data != null)
                {
                    int zCount = ctx.Data.Axes.FirstOrDefault(a => a.Name == ctx.DepthAxisName)?.Count ?? 1;
                    BuildSideRois(ctx.Data, zCount);
                    ctx.OrthoPanel.BottomView.OverlayManager.AddObject(_xzRoi!);
                    ctx.OrthoPanel.RightView.OverlayManager.AddObject(_zyRoi!);
                    WireSyncHandlers();
                }
            }

            var overlay = OverlayLayer.GetOverlayLayer(ctx.HostVisual);
            if (overlay == null) return;
            _overlay = overlay;

            _panel = BuildPanel();
            overlay.Children.Add(_panel);
            PositionPanel(overlay);

            if (ctx.HostVisual is Control hostControl)
            {
                _layoutHandler = (_, _) => PositionPanel();
                hostControl.LayoutUpdated += _layoutHandler;
            }

            UpdateInfoText();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Cleanup();
        }

        public void NotifyContextChanged(PlotterActionContext newContext)
        {
            if (_disposed || _xyRoi == null) return;
            _ctx = newContext;

            var data = newContext.Data;
            if (data == null) return;

            // ── XY ROI: clamp to (possibly new) data dimensions ───────────────
            _dataYCount = data.YCount;
            _xyRoi.DataBounds = new Rect(-0.5, -0.5, data.XCount, data.YCount);
            ClampToDataBounds(_xyRoi);

            // ── No ortho views: nothing more to clamp ─────────────────────────
            if (newContext.OrthoPanel == null || newContext.DepthAxisName == null) return;

            // OrthoPanel is now available but side ROIs don't exist yet — create them.
            if (_xzRoi == null && _zyRoi == null && _role == CropRole.Leader)
            {
                int zCount = data.Axes.FirstOrDefault(a => a.Name == newContext.DepthAxisName)?.Count ?? 1;
                BuildSideRois(data, zCount);
                newContext.OrthoPanel.BottomView.OverlayManager.AddObject(_xzRoi!);
                newContext.OrthoPanel.RightView.OverlayManager.AddObject(_zyRoi!);
                WireSyncHandlers();
                newContext.OrthoPanel.BottomView.OverlayManager.InvalidateVisual();
                newContext.OrthoPanel.RightView.OverlayManager.InvalidateVisual();
                return;
            }

            if (_xzRoi == null || _zyRoi == null) return;

            int newZCount = data.Axes.FirstOrDefault(a => a.Name == newContext.DepthAxisName)?.Count ?? 1;
            _dataZCount = newZCount;

            // ── XZ ROI: re-sync X from clamped XY, then clamp Z to new range ─
            _xzRoi.DataBounds = new Rect(-0.5, -0.5, data.XCount, newZCount);
            _xzRoi.X = _xyRoi.X;
            _xzRoi.Width = _xyRoi.Width;
            ClampToDataBounds(_xzRoi);

            // ── ZY ROI: re-derive entirely from clamped XY + XZ ──────────────
            // (maintains coordinate-inversion invariants; no separate clamp needed)
            _zyRoi.DataBounds = new Rect(-0.5, -0.5, data.YCount, newZCount);
            _zyRoi.X = _dataYCount - _xyRoi.Y - _xyRoi.Height - 1;
            _zyRoi.Y = _dataZCount - _xzRoi.Y - _xzRoi.Height - 1;
            _zyRoi.Width = _xyRoi.Height;
            _zyRoi.Height = _xzRoi.Height;

            // ── Invalidate all three views ────────────────────────────────────
            newContext.MainView.OverlayManager.InvalidateVisual();
            newContext.OrthoPanel.BottomView.OverlayManager.InvalidateVisual();
            newContext.OrthoPanel.RightView.OverlayManager.InvalidateVisual();
        }

        // ── ROI construction ──────────────────────────────────────────────────

        private static RoiObject BuildXyRoi(IMatrixData? data, CropRoiBounds? initial = null)
        {
            var roi = new RoiObject();
            if (data == null) return roi;

            roi.DataBounds = new Rect(-0.5, -0.5, data.XCount, data.YCount);
            double qx = Math.Floor(data.XCount / 4.0);
            double qy = Math.Floor(data.YCount / 4.0);
            roi.X = qx - 0.5;
            roi.Y = qy - 0.5;
            roi.Width = Math.Floor(data.XCount / 2.0);
            roi.Height = Math.Floor(data.YCount / 2.0);
            if (initial is { } ib)
            {
                // Convert data-index Y (bottom-left origin) to bitmap Y (top-left origin)
                double bitmapY = data.YCount - ib.Y - ib.Height;
                roi.X = Math.Max(-0.5, Math.Min(ib.X, data.XCount - 1));
                roi.Y = Math.Max(-0.5, Math.Min(bitmapY, data.YCount - 1));
                roi.Width = Math.Max(1, Math.Min(ib.Width, data.XCount));
                roi.Height = Math.Max(1, Math.Min(ib.Height, data.YCount));
            }
            return roi;
        }

        private void BuildSideRois(IMatrixData data, int zCount)
        {
            _dataYCount = data.YCount;
            _dataZCount = zCount;

            // XZ ROI (BottomView): X range inherits from XY, Z range covers full depth (locked).
            _xzRoi = new RoiObject
            {
                DataBounds = new Rect(-0.5, -0.5, data.XCount, zCount),
                X = _xyRoi!.X,
                Y = -0.5,
                Width = _xyRoi.Width,
                Height = zCount,
                IsHeightLocked = true,
            };

            // ZY ROI (RightView bitmap space, FlipY=true):
            //   X axis = data Y (direct): col c = data Y=c
            //   Y axis = data Z (inverted by FlipY): row r = data Z=(ZCount-1-r)
            //
            // XY.Y→ZY.X (Y axis, XY has FlipY=true):
            //   ZY.X = YCount - XY.Y - XY.Height - 1
            //
            // XZ.Y→ZY.Y (Z axis, XZ FlipY=false vs ZY FlipY=true → opposite directions):
            //   ZY.Y = ZCount - XZ.Y - XZ.Height - 1
            _zyRoi = new RoiObject
            {
                DataBounds = new Rect(-0.5, -0.5, data.YCount, zCount),
                X = data.YCount - _xyRoi.Y - _xyRoi.Height - 1,
                Y = -0.5,
                Width = _xyRoi.Height,
                Height = zCount,
                IsHeightLocked = true,
            };
        }

        // ── ROI sync ──────────────────────────────────────────────────────────

        private void WireSyncHandlers()
        {
            _xyRoi!.BoundsChanged += OnXyBoundsChanged;
            _xzRoi!.BoundsChanged += OnXzBoundsChanged;
            _zyRoi!.BoundsChanged += OnZyBoundsChanged;
        }

        private void UnwireSyncHandlers()
        {
            if (_xyRoi != null) _xyRoi.BoundsChanged -= OnXyBoundsChanged;
            if (_xzRoi != null) _xzRoi.BoundsChanged -= OnXzBoundsChanged;
            if (_zyRoi != null) _zyRoi.BoundsChanged -= OnZyBoundsChanged;
        }

        private void OnXyBoundsChanged(object? sender, EventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            if (_xzRoi != null) { _xzRoi.X = _xyRoi!.X; _xzRoi.Width = _xyRoi.Width; }
            if (_zyRoi != null) { _zyRoi.X = _dataYCount - _xyRoi!.Y - _xyRoi.Height - 1; _zyRoi.Width = _xyRoi.Height; }
            _syncing = false;
            _ctx?.OrthoPanel?.BottomView.OverlayManager.InvalidateVisual();
            _ctx?.OrthoPanel?.RightView.OverlayManager.InvalidateVisual();
        }

        private void OnXzBoundsChanged(object? sender, EventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            if (_xyRoi != null) { _xyRoi.X = _xzRoi!.X; _xyRoi.Width = _xzRoi.Width; }
            // ZY has FlipY=true; XZ has FlipY=false → Z directions are opposite → invert
            if (_zyRoi != null) { _zyRoi.Y = _dataZCount - _xzRoi!.Y - _xzRoi.Height - 1; _zyRoi.Height = _xzRoi.Height; }
            _syncing = false;
            _ctx?.MainView.OverlayManager.InvalidateVisual();
            _ctx?.OrthoPanel?.RightView.OverlayManager.InvalidateVisual();
            UpdateInfoText();
            if (_xyRoi != null)
                RoiBoundsChanged?.Invoke(this, XyRoiToDataBounds());
        }

        private void OnZyBoundsChanged(object? sender, EventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            if (_xyRoi != null) { _xyRoi.Y = _dataYCount - _zyRoi!.X - _zyRoi.Width - 1; _xyRoi.Height = _zyRoi.Width; }
            // ZY has FlipY=true; XZ has FlipY=false → Z directions are opposite → invert
            if (_xzRoi != null) { _xzRoi.Y = _dataZCount - _zyRoi!.Y - _zyRoi.Height - 1; _xzRoi.Height = _zyRoi.Height; }
            _syncing = false;
            _ctx?.MainView.OverlayManager.InvalidateVisual();
            _ctx?.OrthoPanel?.BottomView.OverlayManager.InvalidateVisual();
            UpdateInfoText();
            if (_xyRoi != null)
                RoiBoundsChanged?.Invoke(this, XyRoiToDataBounds());
        }

        // ── Panel construction ────────────────────────────────────────────────

        private Border BuildPanel() =>
            _role == CropRole.Follower ? BuildFollowerPanel() : BuildLeaderPanel();

        private Border BuildLeaderPanel()
        {
            const double PanelFontSize = 11;
            const double InfoFontSize = 10;

            var data = _ctx?.Data;
            bool isMultiFrame = data is { FrameCount: > 1 };
            bool isVirtual = data?.IsVirtual == true;

            // ROI info text (updated dynamically via UpdateInfoText)
            _infoText = new TextBlock
            {
                FontSize = InfoFontSize,
                Opacity = 0.85,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // Options
            _replaceDataChk = ControlFactory.MakeCheckBox("Replace data", fontSize: PanelFontSize);
            _replaceDataChk.Margin = new Thickness(0, 0, 0, -10);
            _replaceDataChk.IsChecked = _lastReplaceData;

            _thisFrameOnlyChk = ControlFactory.MakeCheckBox("This frame only", fontSize: PanelFontSize);
            _thisFrameOnlyChk.Margin = new Thickness(0, 0, 0, -7);
            _thisFrameOnlyChk.IsVisible = isMultiFrame;
            _thisFrameOnlyChk.IsChecked = isMultiFrame && _lastThisFrameOnly;

            _virtualWarning = new TextBlock
            {
                Text = "\u26a0 Virtual frames \u2014 crop may take time",
                FontSize = InfoFontSize,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 0)),
                IsVisible = isVirtual,
                Margin = new Thickness(0, 2, 0, 0),
            };

            var applyBtn = new Button
            {
                Content = "Apply",
                FontSize = PanelFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                MinWidth = 58,
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                FontSize = PanelFontSize,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                MinWidth = 58,
            };

            applyBtn.Click += (_, _) =>
            {
                Parameters = ComputeParameters();
                _finalBounds = _xyRoi == null || _ctx?.Data == null ? null : XyRoiToDataBounds();
                Cleanup();
                Completed?.Invoke(this, null);
            };
            cancelBtn.Click += (_, _) =>
            {
                Cleanup();
                Cancelled?.Invoke(this, EventArgs.Empty);
            };

            var btnRow = new Grid
            {
                Height = 28,
                Width = 150,
                Margin = new Thickness(0, 4, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            btnRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            btnRow.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(applyBtn, 0);
            Grid.SetColumn(cancelBtn, 1);
            btnRow.Children.Add(applyBtn);
            btnRow.Children.Add(cancelBtn);

            var stack = new StackPanel
            {
                Spacing = 0,
                Margin = new Thickness(10, 8, 10, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            stack.Children.Add(new TextBlock { Text = "Crop", FontWeight = FontWeight.Bold, FontSize = PanelFontSize });
            stack.Children.Add(_infoText);
            stack.Children.Add(_replaceDataChk);
            if (isMultiFrame) stack.Children.Add(_thisFrameOnlyChk);
            if (isVirtual) stack.Children.Add(_virtualWarning);
            stack.Children.Add(btnRow);

            var panel = new Border
            {
                Child = stack,
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 110, 110, 110)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            // Theme-aware background — same resource as the hamburger menu panel
            static IBrush? GetMenuBg() =>
                Application.Current?.TryGetResource("MenuPopupBg",
                    Application.Current.ActualThemeVariant, out var r) == true
                    ? r as IBrush
                    : new SolidColorBrush(Color.FromArgb(220, 36, 36, 36));

            panel.Background = GetMenuBg();
            if (Application.Current is { } app)
            {
                _themeHandler = (_, _) => panel.Background = GetMenuBg();
                app.ActualThemeVariantChanged += _themeHandler;
            }

            return panel;
        }

        private Border BuildFollowerPanel()
        {
            const double PanelFontSize = 11;
            const double InfoFontSize = 10;
            var syncColor = Color.Parse("#E57373");

            _infoText = new TextBlock
            {
                FontSize = InfoFontSize,
                Opacity = 0.85,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var stack = new StackPanel
            {
                Spacing = 0,
                Margin = new Thickness(10, 8, 10, 10),
            };
            stack.Children.Add(new TextBlock
            {
                Text = "\u2b21 Crop (Follower)",
                FontWeight = FontWeight.Bold,
                FontSize = PanelFontSize,
                Foreground = new SolidColorBrush(syncColor),
            });
            stack.Children.Add(_infoText);

            var panel = new Border
            {
                Child = stack,
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 229, 115, 115)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            static IBrush? GetMenuBg() =>
                Application.Current?.TryGetResource("MenuPopupBg",
                    Application.Current.ActualThemeVariant, out var r) == true
                    ? r as IBrush
                    : new SolidColorBrush(Color.FromArgb(220, 36, 36, 36));

            panel.Background = GetMenuBg();
            if (Application.Current is { } app)
            {
                _themeHandler = (_, _) => panel.Background = GetMenuBg();
                app.ActualThemeVariantChanged += _themeHandler;
            }

            return panel;
        }

        // ── ROI construction (Follower) ───────────────────────────────────────

        private RoiObject BuildXyRoiFollower(IMatrixData? data)
        {
            var roi = new RoiObject
            {
                AccentColor = Color.Parse("#E57373"),
                IsMoveOnly = true,
            };
            if (data == null) return roi;

            roi.DataBounds = new Rect(-0.5, -0.5, data.XCount, data.YCount);
            int initBitmapRow = data.YCount - InitialBounds.Y - InitialBounds.Height;
            roi.X = InitialBounds.X - 0.5;
            roi.Y = initBitmapRow - 0.5;
            roi.Width = InitialBounds.Width;
            roi.Height = InitialBounds.Height;
            ClampToDataBounds(roi);

            int actualX = (int)(roi.X + 0.5);
            int actualBitmapY = (int)(roi.Y + 0.5);
            int actualDataY = data.YCount - actualBitmapY - (int)roi.Height;
            _lastLeaderX = InitialBounds.X;
            _lastLeaderY = InitialBounds.Y;
            _offsetX = actualX - _lastLeaderX;
            _offsetY = actualDataY - _lastLeaderY;

            return roi;
        }

        // ── Execution ─────────────────────────────────────────────────────────

        /// <summary>
        /// Converts the current XY ROI from bitmap pixel-edge space to data pixel-index space
        /// (origin bottom-left). Call only when <see cref="_xyRoi"/> and <see cref="_ctx"/> are non-null.
        /// </summary>
        private CropRoiBounds XyRoiToDataBounds()
        {
            int x = (int)(_xyRoi!.X + 0.5);
            int w = (int)_xyRoi.Width;
            int h = (int)_xyRoi.Height;
            int bitmapY = (int)(_xyRoi.Y + 0.5);
            int dataY = _ctx!.Data!.YCount - bitmapY - h;
            return new CropRoiBounds(x, dataY, w, h);
        }

        /// <summary>
        /// Clamps <paramref name="roi"/> so that its X/Y position and Width/Height fit within
        /// <see cref="RoiObject.DataBounds"/>. The size is preserved as much as possible
        /// (shrunk only if the ROI is wider/taller than the bounds), then the position is
        /// slid inward if the right/bottom edge would exceed the bounds.
        /// </summary>
        private static void ClampToDataBounds(RoiObject roi)
        {
            if (roi.DataBounds is not { } b) return;
            const double minSize = 1.0;

            double w = Math.Max(minSize, Math.Min(roi.Width, b.Width));
            double h = Math.Max(minSize, Math.Min(roi.Height, b.Height));
            double x = Math.Clamp(roi.X, b.X, b.Right - w);
            double y = Math.Clamp(roi.Y, b.Y, b.Bottom - h);

            roi.X = x; roi.Y = y; roi.Width = w; roi.Height = h;
        }

        /// <summary>
        /// Converts the XY ROI (pixel-edge space) to pixel indices and packages them
        /// together with the user's output options into a <see cref="CropParameters"/>.
        /// The actual crop execution is handled by the host (<c>MatrixPlotter.Processings</c>).
        /// </summary>
        private CropParameters? ComputeParameters()
        {
            if (_ctx?.Data == null || _xyRoi == null) return null;

            // Pixel-edge coordinate: edge at (n − 0.5) is the left/top of pixel n.
            int x = (int)(_xyRoi.X + 0.5);
            int w = (int)_xyRoi.Width;
            int h = (int)_xyRoi.Height;
            if (w <= 0 || h <= 0) return null;

            // XY view uses FlipY=true: bitmap row 0 = data row YCount-1.
            // Convert bitmap-row edge → data row index for CropOperation.
            int bitmapY = (int)(_xyRoi.Y + 0.5);
            int y = _ctx.Data.YCount - bitmapY - h;

            bool replaceData = _replaceDataChk?.IsChecked == true || ReplaceData;
            bool thisFrameOnly = _role == CropRole.Follower
                ? ThisFrameOnly
                : _thisFrameOnlyChk?.IsChecked == true;

            // Persist for next invocation (leader only).
            if (_role != CropRole.Follower)
            {
                _lastReplaceData = replaceData;
                _lastThisFrameOnly = thisFrameOnly;
            }

            // For follower: encode LeaderFrameIndex as a hint in FrameIndex.
            // -1 means "resolve to local ActiveIndex" (leader path or ThisFrameOnly=false).
            // Follower encodes the leader's frame index so ExecuteCropAsync can clamp it
            // to the follower's own frame count and fall back to ActiveIndex when out of range.
            int frameIndex = _role == CropRole.Follower && thisFrameOnly ? LeaderFrameIndex : -1;

            return new CropParameters(x, y, w, h, replaceData, thisFrameOnly, frameIndex);
        }

        /// <summary>
        /// Updates the ROI info text with the current origin and size.
        /// In Follower mode shows the pixel offset from the leader window instead.
        /// </summary>
        private void UpdateInfoText()
        {
            if (_infoText == null || _xyRoi == null || _ctx?.Data == null) return;

            if (_role == CropRole.Follower)
            {
                int wPx = (int)_xyRoi.Width;
                int hPx = (int)_xyRoi.Height;
                string dxStr = _offsetX >= 0 ? $"+{(int)_offsetX}" : $"{(int)_offsetX}";
                string dyStr = _offsetY >= 0 ? $"+{(int)_offsetY}" : $"{(int)_offsetY}";
                SetInfoText($"Offset = ({dxStr}, {dyStr}) px\nSize = {wPx} \u00d7 {hPx} px");
                return;
            }

            var data = _ctx.Data;

            int xIdx = (int)(_xyRoi.X + 0.5);
            int wPxP = (int)_xyRoi.Width;
            int hPxP = (int)_xyRoi.Height;
            int bitmapY = (int)(_xyRoi.Y + 0.5);
            int yIdx = data.YCount - bitmapY - hPxP;

            bool hasScale = data.XCount > 1 && data.YCount > 1
                         && !double.IsNaN(data.XStep) && !double.IsNaN(data.YStep);
            if (hasScale)
            {
                double physX = data.XMin + xIdx * data.XStep;
                double physY = data.YMin + yIdx * data.YStep;
                double scaledW = wPxP * Math.Abs(data.XStep);
                double scaledH = hPxP * Math.Abs(data.YStep);
                SetInfoText($"(x0, y0) = ({physX:G4}, {physY:G4}) [{xIdx}, {yIdx}]\n"
                          + $"W \u00d7 H = {scaledW:G4}\u00d7{scaledH:G4} [{wPxP} \u00d7 {hPxP}]");
            }
            else
            {
                SetInfoText($"(x0, y0) = [{xIdx}, {yIdx}]\n"
                          + $"W \u00d7 H = {wPxP} \u00d7 {hPxP} px");
            }
        }

        private void SetInfoText(string text)
        {
            _infoText!.Text = text;
            ToolTip.SetTip(_infoText, text);
        }

        // ── Positioning ───────────────────────────────────────────────────────

        private void PositionPanel() { if (_overlay != null) PositionPanel(_overlay); }

        private void PositionPanel(OverlayLayer overlay)
        {
            if (_panel == null || _ctx == null) return;
            _overlay = overlay;

            var mainView = _ctx.MainView;
            var origin = mainView.TranslatePoint(new Point(0, 0), overlay);
            if (!origin.HasValue) return;
            var pt = origin.Value;

            double viewW = mainView.Bounds.Width;
            double viewH = mainView.Bounds.Height;
            double panelW = _panel.Bounds.Width > 0 ? _panel.Bounds.Width : 200;
            double panelH = _panel.Bounds.Height > 0 ? _panel.Bounds.Height : 120;
            const double Margin = 4.0;

            if (_xyRoi == null || _role == CropRole.Follower)
            {
                Canvas.SetLeft(_panel, pt.X + Margin);
                Canvas.SetTop(_panel, pt.Y + Margin);
                return;
            }

            var vp = mainView.GetOverlayViewport();
            var rTL = vp.WorldToScreen(new Point(_xyRoi.X, _xyRoi.Y));
            var rTR = vp.WorldToScreen(new Point(_xyRoi.X + _xyRoi.Width, _xyRoi.Y));
            var rBL = vp.WorldToScreen(new Point(_xyRoi.X, _xyRoi.Y + _xyRoi.Height));
            var rBR = vp.WorldToScreen(new Point(_xyRoi.X + _xyRoi.Width, _xyRoi.Y + _xyRoi.Height));

            double roiL = pt.X + Math.Min(Math.Min(rTL.X, rTR.X), Math.Min(rBL.X, rBR.X));
            double roiT = pt.Y + Math.Min(Math.Min(rTL.Y, rTR.Y), Math.Min(rBL.Y, rBR.Y));
            double roiR = pt.X + Math.Max(Math.Max(rTL.X, rTR.X), Math.Max(rBL.X, rBR.X));
            double roiB = pt.Y + Math.Max(Math.Max(rTL.Y, rTR.Y), Math.Max(rBL.Y, rBR.Y));
            var roiRect = new Rect(roiL, roiT, roiR - roiL, roiB - roiT);

            Point[] candidates =
            [
                new(pt.X + Margin,                  pt.Y + Margin),
                new(pt.X + viewW - panelW - Margin, pt.Y + Margin),
                new(pt.X + Margin,                  pt.Y + viewH - panelH - Margin),
                new(pt.X + viewW - panelW - Margin, pt.Y + viewH - panelH - Margin),
            ];

            var chosen = candidates[0];
            foreach (var c in candidates)
            {
                if (!new Rect(c.X, c.Y, panelW, panelH).Intersects(roiRect))
                { chosen = c; break; }
            }

            Canvas.SetLeft(_panel, chosen.X);
            Canvas.SetTop(_panel, chosen.Y);
        }

        // ── Size-edit dialog (Leader only) ────────────────────────────────────

        private async System.Threading.Tasks.Task OnXyRoiSizeEditRequestedAsync()
        {
            if (_xyRoi == null || _ctx == null) return;
            var owner = TopLevel.GetTopLevel(_ctx.HostVisual) as Window;
            if (owner == null) return;

            var dlg = new CropRoiSizeDialog(_xyRoi.Width, _xyRoi.Height, _ctx.Data);
            var result = await dlg.ShowCenteredOnAsync(owner, _ctx.HostVisual);

            if (result is { } r && _xyRoi != null && _ctx != null)
            {
                _xyRoi.ApplySizeFromDialog(r.W, r.H);
                _ctx.MainView.OverlayManager.InvalidateVisual();
            }
        }

        // ── Cleanup ───────────────────────────────────────────────────────────

        private void Cleanup()
        {
            if (_ctx == null) return;
            UnwireSyncHandlers();

            if (_xyRoi != null)
            {
                if (_sizeEditHandler != null)
                {
                    _xyRoi.SizeEditRequested -= _sizeEditHandler;
                    _sizeEditHandler = null;
                }
                _ctx.MainView.OverlayManager.RemoveObject(_xyRoi);
                _xyRoi = null;
            }
            if (_xzRoi != null && _ctx.OrthoPanel != null)
            {
                _ctx.OrthoPanel.BottomView.OverlayManager.RemoveObject(_xzRoi);
                _xzRoi = null;
            }
            if (_zyRoi != null && _ctx.OrthoPanel != null)
            {
                _ctx.OrthoPanel.RightView.OverlayManager.RemoveObject(_zyRoi);
                _zyRoi = null;
            }
            if (_panel != null)
            {
                if (_panel.Parent is Panel p) p.Children.Remove(_panel);
                _panel = null;
            }
            if (_layoutHandler != null && _ctx.HostVisual is Control hostControl)
            {
                hostControl.LayoutUpdated -= _layoutHandler;
                _layoutHandler = null;
            }
            if (_themeHandler != null && Application.Current is { } app)
            {
                app.ActualThemeVariantChanged -= _themeHandler;
                _themeHandler = null;
            }
            _ctx = null;
        }

        // ── Sync Crop helpers (called by host for Synced role) ────────────────

        /// <summary>
        /// Updates the follower ROI to follow the leader window's new bounds.
        /// Maintains the user's pixel offset; clamps to this window's data bounds.
        /// Call only when <see cref="Role"/> is <see cref="CropRole.Follower"/>.
        /// </summary>
        internal void SyncUpdateLeaderBounds(CropRoiBounds leader)
        {
            if (_xyRoi == null || _ctx?.Data == null) return;

            int targetX = leader.X + (int)_offsetX;
            int targetDataY = leader.Y + (int)_offsetY;
            int bitmapRow = _ctx.Data.YCount - targetDataY - leader.Height;

            _xyRoi.Width = leader.Width;
            _xyRoi.Height = leader.Height;
            _xyRoi.X = targetX - 0.5;
            _xyRoi.Y = bitmapRow - 0.5;
            ClampToDataBounds(_xyRoi);

            int actualX = (int)(_xyRoi.X + 0.5);
            int actualBitmapY = (int)(_xyRoi.Y + 0.5);
            int actualDataY = _ctx.Data.YCount - actualBitmapY - (int)_xyRoi.Height;
            _lastLeaderX = leader.X;
            _lastLeaderY = leader.Y;
            _offsetX = actualX - leader.X;
            _offsetY = actualDataY - leader.Y;

            UpdateInfoText();
            _ctx.MainView.OverlayManager.InvalidateVisual();
        }

        /// <summary>
        /// Programmatically confirms the crop (equivalent to the user clicking Apply).
        /// <see cref="Parameters"/> is set; <see cref="Completed"/> is fired.
        /// If the ROI has zero width or height after clamping, <see cref="Parameters"/> is
        /// left <c>null</c> and execution is skipped by the host.
        /// </summary>
        internal void ForceApply()
        {
            _finalBounds = _xyRoi == null || _ctx?.Data == null ? null : XyRoiToDataBounds();
            Parameters = ComputeParameters();
            Cleanup();
            Completed?.Invoke(this, null);
        }

        /// <summary>Programmatically cancels the crop (equivalent to the user clicking Cancel).</summary>
        internal void ForceCancel()
        {
            Cleanup();
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }
}
