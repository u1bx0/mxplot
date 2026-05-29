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

    public enum CropMode { XY, Substack, Volume }

    /// <summary>ROI position and size in data pixel-index space (XIndex=0/YIndex=0 at bottom-left), shared between leader and follower windows.</summary>
    internal readonly record struct CropRoiBounds(
        int X, int Y, int Width, int Height,
        string? ZAxisName = null,
        int ZStart = 0, int ZCount = -1,
        CropMode Mode = CropMode.XY,
        bool ReplaceData = false,
        bool ThisFrameOnly = false,
        int LeaderFrameIndex = 0);

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
        // Follower: Z range received from the leader via SyncUpdateLeaderBounds
        private CropMode _followerMode = CropMode.XY;
        private string? _followerZAxisName;
        private int _followerZStart;
        private int _followerZCount = -1;
        private OverlayLayer? _overlay;
        private EventHandler? _sizeEditHandler;
        private EventHandler? _xzSizeEditHandler;
        private EventHandler? _zySizeEditHandler;

        // Panel UI elements
        private TextBlock? _infoText;
        private TextBlock? _zInfoText;
        private CheckBox? _replaceDataChk;
        private CheckBox? _thisFrameOnlyChk;
        private TextBlock? _virtualWarning;
        private Button? _applyBtn;
        private ComboBox? _modeCombo;
        private TextBlock? _cropLabel;
        private TextBlock? _hintIcon;
        private bool _refreshingModeCombo;
        private CropMode _mode = CropMode.XY;

        // XY / Z ROI cache — preserved across mode switches so the user's selection is not lost.
        private (double X, double Y, double W, double H)? _xyCache;
        private (double Y, double H)? _zCache;

        public event EventHandler<IMatrixData?>? Completed;
        public event EventHandler? Cancelled;

        // Panel collapse state — persisted across invocations.
        private StackPanel? _detailStack;
        private static bool _lastReplaceData = false;
        private static bool _lastThisFrameOnly = false;
        private static bool _panelExpanded = false;

        /// <summary>Parameters computed on Apply click, read by the host after Completed fires.</summary>
        public CropParameters? Parameters { get; private set; }

        /// <summary>Describes the crop region and output options chosen by the user.</summary>
        /// <param name="FrameIndex">The frame index that was cropped. Meaningful only when <see cref="ThisFrameOnly"/> is <c>true</c>.</param>
        public sealed record CropParameters(
            int X, int Y, int Width, int Height,
            bool ReplaceData, bool ThisFrameOnly, int FrameIndex = 0,
            CropMode Mode = CropMode.XY,
            string? ZAxisName = null,
            int ZStart = 0, int ZCount = -1);

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
                _followerMode = InitialBounds.Mode;
                _followerZAxisName = InitialBounds.ZAxisName;
                _followerZStart = InitialBounds.ZStart;
                _followerZCount = InitialBounds.ZCount;
                _xyRoi = BuildXyRoiFollower(ctx.Data);
                _xyRoi.BoundsChanged += OnFollowerXyBoundsChanged;
                ctx.MainView.OverlayManager.AddObject(_xyRoi);

                if (ctx.OrthoPanel != null && ctx.DepthAxisName != null && ctx.Data != null)
                {
                    int zCount = ctx.Data.Axes.FirstOrDefault(a => a.Name == ctx.DepthAxisName)?.Count ?? 1;
                    bool zAxisMatches = ctx.DepthAxisName == _followerZAxisName;
                    if (!zAxisMatches)
                    {
                        // Follower orth axis differs from leader crop axis: show full Z range.
                        _followerZStart = 0;
                        _followerZCount = zCount;
                    }
                    BuildSideRoisFollower(ctx.Data, zCount);
                    ctx.OrthoPanel.BottomView.OverlayManager.AddObject(_xzRoi!);
                    ctx.OrthoPanel.RightView.OverlayManager.AddObject(_zyRoi!);
                    WireFollowerSyncHandlers();
                }
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
            RefreshModeComboItems(newContext.DepthAxisName);

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

            if (_xzRoi == null && _zyRoi == null && _role == CropRole.Follower)
            {
                int zCount = data.Axes.FirstOrDefault(a => a.Name == newContext.DepthAxisName)?.Count ?? 1;
                bool zAxisMatches = newContext.DepthAxisName == _followerZAxisName;
                if (!zAxisMatches)
                {
                    // Follower orth axis differs from leader crop axis: show full Z range.
                    _followerZStart = 0;
                    _followerZCount = zCount;
                }
                BuildSideRoisFollower(data, zCount);
                newContext.OrthoPanel.BottomView.OverlayManager.AddObject(_xzRoi!);
                newContext.OrthoPanel.RightView.OverlayManager.AddObject(_zyRoi!);
                WireFollowerSyncHandlers();
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

            // XZ ROI (BottomView): X range inherits from XY, Z range covers full depth.
            _xzRoi = new RoiObject
            {
                DataBounds = new Rect(-0.5, -0.5, data.XCount, zCount),
                X = _xyRoi!.X,
                Y = -0.5,
                Width = _xyRoi.Width,
                Height = zCount,
                ShowSizeEditHint = true,
            };
            _xzSizeEditHandler = async (s, e) => await OnXzRoiSizeEditRequestedAsync();
            _xzRoi.SizeEditRequested += _xzSizeEditHandler;

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
                ShowSizeEditHint = true,
            };
            _zySizeEditHandler = async (s, e) => await OnXzRoiSizeEditRequestedAsync();
            _zyRoi.SizeEditRequested += _zySizeEditHandler;
        }

        private void BuildSideRoisFollower(IMatrixData data, int zCount)
        {
            _dataYCount = data.YCount;
            _dataZCount = zCount;

            var accent = Color.Parse("#E57373");

            // Initial Z range from leader bounds (clamp to local data depth).
            int initZStart = Math.Clamp(_followerZStart, 0, zCount - 1);
            int initZCount = _followerZCount > 0
                ? Math.Min(_followerZCount, zCount - initZStart)
                : zCount;

            _xzRoi = new RoiObject
            {
                AccentColor = accent,
                IsMoveOnly = true,
                DataBounds = new Rect(-0.5, -0.5, data.XCount, zCount),
                X = _xyRoi!.X,
                Y = initZStart - 0.5,
                Width = _xyRoi.Width,
                Height = initZCount,
            };

            _zyRoi = new RoiObject
            {
                AccentColor = accent,
                IsMoveOnly = true,
                DataBounds = new Rect(-0.5, -0.5, data.YCount, zCount),
                X = data.YCount - _xyRoi.Y - _xyRoi.Height - 1,
                Y = zCount - initZStart - initZCount - 0.5,
                Width = _xyRoi.Height,
                Height = initZCount,
            };
        }

        // ── ROI sync ──────────────────────────────────────────────────────────

        private void WireSyncHandlers()
        {
            _xyRoi!.BoundsChanged += OnXyBoundsChanged;
            _xzRoi!.BoundsChanged += OnXzBoundsChanged;
            _zyRoi!.BoundsChanged += OnZyBoundsChanged;
        }

        private void WireFollowerSyncHandlers()
        {
            _xzRoi!.BoundsChanged += OnFollowerXzBoundsChanged;
            _zyRoi!.BoundsChanged += OnFollowerZyBoundsChanged;
        }

        private void UnwireSyncHandlers()
        {
            if (_xyRoi != null) _xyRoi.BoundsChanged -= OnXyBoundsChanged;
            if (_xyRoi != null) _xyRoi.BoundsChanged -= OnFollowerXyBoundsChanged;
            if (_xzRoi != null) _xzRoi.BoundsChanged -= OnXzBoundsChanged;
            if (_xzRoi != null) _xzRoi.BoundsChanged -= OnFollowerXzBoundsChanged;
            if (_zyRoi != null) _zyRoi.BoundsChanged -= OnZyBoundsChanged;
            if (_zyRoi != null) _zyRoi.BoundsChanged -= OnFollowerZyBoundsChanged;
        }

        private void OnFollowerXyBoundsChanged(object? sender, EventArgs e)
        {
            if (_xyRoi != null && _ctx?.Data != null)
            {
                int actualX = (int)(_xyRoi.X + 0.5);
                int actualBitmapY = (int)(_xyRoi.Y + 0.5);
                int actualDataY = _ctx.Data.YCount - actualBitmapY - (int)_xyRoi.Height;
                _offsetX = actualX - _lastLeaderX;
                _offsetY = actualDataY - _lastLeaderY;
            }
            // Sync XZ/ZY X position from XY (Z range stays unchanged).
            if (_syncing) return;
            _syncing = true;
            if (_xzRoi != null) { _xzRoi.X = _xyRoi!.X; _xzRoi.Width = _xyRoi.Width; }
            if (_zyRoi != null) { _zyRoi.X = _dataYCount - _xyRoi!.Y - _xyRoi.Height - 1; _zyRoi.Width = _xyRoi.Height; }
            _syncing = false;
            _ctx?.OrthoPanel?.BottomView.OverlayManager.InvalidateVisual();
            _ctx?.OrthoPanel?.RightView.OverlayManager.InvalidateVisual();
            UpdateInfoText();
        }

        /// <summary>
        /// Follower: XZ ROI moved in X → update XY.X; XZ ROI moved in Z → sync ZY.Y.
        /// (XZ is IsMoveOnly so Width/Height do not change.)
        /// </summary>
        private void OnFollowerXzBoundsChanged(object? sender, EventArgs e)
        {
            if (_syncing || _xyRoi == null || _xzRoi == null) return;
            _syncing = true;
            // XZ.X → XY.X (shared X axis)
            _xyRoi.X = _xzRoi.X;
            _xyRoi.Width = _xzRoi.Width;
            // XZ.Y (Z start) → ZY.Y (inverted Z direction)
            if (_zyRoi != null)
                _zyRoi.Y = _dataZCount - _xzRoi.Y - _xzRoi.Height - 1;
            _syncing = false;

            // Recalculate X offset from updated XY position.
            if (_ctx?.Data != null)
            {
                int actualX = (int)(_xyRoi.X + 0.5);
                _offsetX = actualX - _lastLeaderX;
            }

            _ctx?.MainView.OverlayManager.InvalidateVisual();
            _ctx?.OrthoPanel?.RightView.OverlayManager.InvalidateVisual();
            UpdateInfoText();
        }

        /// <summary>
        /// Follower: ZY ROI moved in Y → update XY.Y; ZY ROI moved in Z → sync XZ.Y.
        /// (ZY is IsMoveOnly so Width/Height do not change.)
        /// </summary>
        private void OnFollowerZyBoundsChanged(object? sender, EventArgs e)
        {
            if (_syncing || _xyRoi == null || _zyRoi == null) return;
            _syncing = true;
            // ZY.X → XY.Y (shared Y axis, inverted)
            _xyRoi.Y = _dataYCount - _zyRoi.X - _zyRoi.Width - 1;
            _xyRoi.Height = _zyRoi.Width;
            // ZY.Y (Z start, inverted) → XZ.Y
            if (_xzRoi != null)
                _xzRoi.Y = _dataZCount - _zyRoi.Y - _zyRoi.Height - 1;
            _syncing = false;

            // Recalculate Y offset from updated XY position.
            if (_ctx?.Data != null)
            {
                int actualBitmapY = (int)(_xyRoi.Y + 0.5);
                int actualDataY = _ctx.Data.YCount - actualBitmapY - (int)_xyRoi.Height;
                _offsetY = actualDataY - _lastLeaderY;
            }

            _ctx?.MainView.OverlayManager.InvalidateVisual();
            _ctx?.OrthoPanel?.BottomView.OverlayManager.InvalidateVisual();
            UpdateInfoText();
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

            // Detect XY-size change while in Substack mode → auto-promote to Volume.
            if (_mode == CropMode.Substack && _xzRoi != null && _xyRoi?.DataBounds is { } db)
            {
                bool xChanged = Math.Abs(_xzRoi.X - db.X) > 0.5 || Math.Abs(_xzRoi.Width - db.Width) > 0.5;
                if (xChanged) PromoteSubstackToVolume();
            }

            // Detect Z-range change while in XY mode → auto-promote to Volume.
            if (_mode == CropMode.XY && _xzRoi != null)
            {
                bool zChanged = Math.Abs(_xzRoi.Y) > 0.5 || Math.Abs(_xzRoi.Height - _dataZCount) > 0.5;
                if (zChanged) PromoteXyToVolume();
            }

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

            // Detect XY-size change (Y/Height) while in Substack mode → auto-promote to Volume.
            if (_mode == CropMode.Substack && _zyRoi != null && _xyRoi?.DataBounds is { } db)
            {
                // ZY.X/Width correspond to XY.Y/Height (via Y-flip).
                int expectedZyX = _dataYCount - (int)(db.Y + 0.5) - (int)(db.Height + 0.5) - 1;
                bool yChanged = Math.Abs(_zyRoi.X - expectedZyX) > 0.5 || Math.Abs(_zyRoi.Width - db.Height) > 0.5;
                if (yChanged) PromoteSubstackToVolume();
            }

            // Detect Z-range change while in XY mode → auto-promote to Volume.
            if (_mode == CropMode.XY && _zyRoi != null)
            {
                bool zChanged = Math.Abs(_zyRoi.Y) > 0.5 || Math.Abs(_zyRoi.Height - _dataZCount) > 0.5;
                if (zChanged) PromoteXyToVolume();
            }

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
            bool hasOrtho = _ctx?.OrthoPanel != null;

            // ── Apply / Cancel action ─────────────────────────────────────────
            void DoApply()
            {
                Parameters = ComputeParameters();
                _finalBounds = _xyRoi == null || _ctx?.Data == null ? null : XyRoiToDataBounds();
                Cleanup();
                Completed?.Invoke(this, null);
            }
            void DoCancel()
            {
                Cleanup();
                Cancelled?.Invoke(this, EventArgs.Empty);
            }

            // ── Header row: [Crop label (+ ComboBox if multiframe) ── Star] [✓] [✗] [▼/▲] ──
            Control modeControl;
            if (isMultiFrame)
            {
                _modeCombo = new ComboBox
                {
                    FontSize = PanelFontSize,
                    Padding = new Thickness(4, 1, 4, 1),
                    MinHeight = 0,
                    Height = 20,
                    Width = 76,
                    Margin = new Thickness(4, 0, 0, 0),
                };
                _modeCombo.SelectionChanged += (_, _) =>
                {
                    if (_refreshingModeCombo) return;
                    if (_modeCombo.SelectedItem is ComboBoxItem { Tag: CropMode m })
                        OnModeChanged(m);
                };
                _cropLabel = new TextBlock
                {
                    Text = "Crop",
                    FontWeight = FontWeight.Bold,
                    FontSize = PanelFontSize,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                _hintIcon = new TextBlock
                {
                    Text = "\u2139",
                    FontSize = PanelFontSize,
                    Opacity = 0.75,
                    Margin = new Thickness(3, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsVisible = false,
                };
                RefreshModeComboItems(_ctx?.DepthAxisName);
                var labelComboRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                labelComboRow.Children.Add(_cropLabel);
                labelComboRow.Children.Add(_hintIcon);
                labelComboRow.Children.Add(_modeCombo);
                modeControl = labelComboRow;
            }
            else
            {
                modeControl = new TextBlock
                {
                    Text = "Crop",
                    FontWeight = FontWeight.Bold,
                    FontSize = PanelFontSize,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            // Compact ✓ / ✗ buttons shown in collapsed mode
            var compactApplyBtn = new Button
            {
                Content = "\u2713",
                FontSize = PanelFontSize,
                Padding = new Thickness(4, 1, 4, 1),
                MinWidth = 0,
                MinHeight = 0,
                Height = 20,
                Margin = new Thickness(4, 0, 0, 0),
            };
            ToolTip.SetTip(compactApplyBtn, "Apply");
            compactApplyBtn.Click += (_, _) => DoApply();

            var compactCancelBtn = new Button
            {
                Content = "\u2717",
                FontSize = PanelFontSize,
                Padding = new Thickness(4, 1, 4, 1),
                MinWidth = 0,
                MinHeight = 0,
                Height = 20,
                Margin = new Thickness(2, 0, 0, 0),
            };
            ToolTip.SetTip(compactCancelBtn, "Cancel");
            compactCancelBtn.Click += (_, _) => DoCancel();

            // ▼/▲ expand toggle button
            var expandBtn = new Button
            {
                Content = _panelExpanded ? "\u25b2" : "\u25bc",
                FontSize = 8,
                Padding = new Thickness(3, 1, 3, 1),
                MinWidth = 0,
                MinHeight = 0,
                Height = 20,
                Margin = new Thickness(2, 0, 0, 0),
            };
            ToolTip.SetTip(expandBtn, _panelExpanded ? "Collapse" : "Expand");

            var headerGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            Grid.SetColumn(modeControl, 0);
            Grid.SetColumn(compactApplyBtn, 1);
            Grid.SetColumn(compactCancelBtn, 2);
            Grid.SetColumn(expandBtn, 3);
            headerGrid.Children.Add(modeControl);
            headerGrid.Children.Add(compactApplyBtn);
            headerGrid.Children.Add(compactCancelBtn);
            headerGrid.Children.Add(expandBtn);

            // ── Detail rows (collapsible) ─────────────────────────────────────
            _infoText = new TextBlock
            {
                FontSize = InfoFontSize,
                Opacity = 0.85,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            _zInfoText = new TextBlock
            {
                FontSize = InfoFontSize,
                Opacity = 0.85,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsVisible = false,
            };

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

            _applyBtn = new Button
            {
                Content = "Apply",
                FontSize = PanelFontSize,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 3, 8, 3),
                MinWidth = 58,
            };
            var cancelBtn = new Button
            {
                Content = "Cancel",
                FontSize = PanelFontSize,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(8, 3, 8, 3),
                MinWidth = 58,
            };
            _applyBtn.Click += (_, _) => DoApply();
            cancelBtn.Click += (_, _) => DoCancel();

            // Right-aligned button row
            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 4,
                Margin = new Thickness(0, 6, 0, 0),
            };
            btnRow.Children.Add(_applyBtn);
            btnRow.Children.Add(cancelBtn);

            _detailStack = new StackPanel
            {
                Spacing = 0,
                IsVisible = _panelExpanded,
            };
            _detailStack.Children.Add(_infoText);
            _detailStack.Children.Add(_zInfoText);
            _detailStack.Children.Add(_replaceDataChk);
            if (isMultiFrame) _detailStack.Children.Add(_thisFrameOnlyChk);
            if (isVirtual) _detailStack.Children.Add(_virtualWarning);
            _detailStack.Children.Add(btnRow);

            // ── Compact ✓/✗ visibility: shown only when collapsed ─────────────
            compactApplyBtn.IsVisible = !_panelExpanded;
            compactCancelBtn.IsVisible = !_panelExpanded;

            // ── Expand toggle handler ─────────────────────────────────────────
            expandBtn.Click += (_, _) =>
            {
                _panelExpanded = !_panelExpanded;
                _detailStack.IsVisible = _panelExpanded;
                compactApplyBtn.IsVisible = !_panelExpanded;
                compactCancelBtn.IsVisible = !_panelExpanded;
                expandBtn.Content = _panelExpanded ? "\u25b2" : "\u25bc";
                ToolTip.SetTip(expandBtn, _panelExpanded ? "Collapse" : "Expand");
                PositionPanel();
            };

            // ── Outer stack ───────────────────────────────────────────────────
            var outerStack = new StackPanel
            {
                Spacing = 0,
                Margin = new Thickness(10, 8, 10, 10),
            };
            outerStack.Children.Add(headerGrid);
            outerStack.Children.Add(_detailStack);

            var panel = new Border
            {
                Child = outerStack,
                BorderBrush = new SolidColorBrush(Color.FromArgb(180, 110, 110, 110)),
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
            _zInfoText = new TextBlock
            {
                FontSize = InfoFontSize,
                Opacity = 0.85,
                IsVisible = false,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            stack.Children.Add(new TextBlock
            {
                Text = "\u21c4 Crop (Follower)",
                FontWeight = FontWeight.Bold,
                FontSize = PanelFontSize,
                Foreground = new SolidColorBrush(syncColor),
            });
            stack.Children.Add(_infoText);
            stack.Children.Add(_zInfoText);

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
            bool hasZ = _mode != CropMode.XY && _xzRoi != null;
            return new CropRoiBounds(x, dataY, w, h,
                ZAxisName: hasZ ? _ctx?.DepthAxisName : null,
                ZStart: hasZ ? (int)(_xzRoi!.Y + 0.5) : 0,
                ZCount: hasZ ? (int)_xzRoi!.Height : -1,
                Mode: _mode);
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

            CropMode effectiveMode = _role == CropRole.Follower ? _followerMode : _mode;
            bool hasZ = effectiveMode != CropMode.XY;
            return new CropParameters(x, y, w, h, replaceData, thisFrameOnly, frameIndex,
                Mode: effectiveMode,
                ZAxisName: hasZ ? (_role == CropRole.Follower ? _followerZAxisName : _ctx?.DepthAxisName) : null,
                ZStart: hasZ ? (_role == CropRole.Follower ? _followerZStart : (_xzRoi != null ? (int)(_xzRoi.Y + 0.5) : 0)) : 0,
                ZCount: hasZ ? (_role == CropRole.Follower ? _followerZCount : (_xzRoi != null ? (int)_xzRoi.Height : -1)) : -1);
        }

        /// <summary>
        /// Promotes the current mode from Substack to Volume without altering any ROI positions.
        /// Called when the user edits XY dimensions in the orthogonal views while in Substack mode.
        /// </summary>
        private void PromoteSubstackToVolume()
        {
            _mode = CropMode.Volume;

            // Unlock XY ROI so the user can freely resize it.
            if (_xyRoi != null) _xyRoi.IsMoveOnly = false;

            // Panel visibility: Volume shows both XY info and Z info.
            bool hasOrtho = _ctx?.OrthoPanel != null;
            if (_infoText != null) _infoText.IsVisible = true;
            if (_zInfoText != null) _zInfoText.IsVisible = hasOrtho;
            if (_thisFrameOnlyChk != null)
            {
                _thisFrameOnlyChk.IsEnabled = false;
                _thisFrameOnlyChk.IsChecked = false;
            }

            // Sync ComboBox selection to Volume without re-entering OnModeChanged.
            if (_modeCombo != null)
            {
                _refreshingModeCombo = true;
                try
                {
                    for (int i = 0; i < _modeCombo.Items.Count; i++)
                    {
                        if (_modeCombo.Items[i] is ComboBoxItem ci && ci.Tag is CropMode m && m == CropMode.Volume)
                        {
                            _modeCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                finally
                {
                    _refreshingModeCombo = false;
                }
            }

            string axisLbl = _ctx?.DepthAxisName ?? "Z";
            UpdateModeComboTooltip(axisLbl);
            UpdateZInfoText();
        }

        /// <summary>
        /// Promotes the current mode from XY to Volume without altering any ROI positions.
        /// Called when the user edits Z dimensions in the orthogonal views while in XY mode.
        /// </summary>
        private void PromoteXyToVolume()
        {
            _mode = CropMode.Volume;

            // Panel visibility: Volume shows both XY info and Z info.
            bool hasOrtho = _ctx?.OrthoPanel != null;
            if (_infoText != null) _infoText.IsVisible = true;
            if (_zInfoText != null) _zInfoText.IsVisible = hasOrtho;
            if (_thisFrameOnlyChk != null)
            {
                _thisFrameOnlyChk.IsEnabled = false;
                _thisFrameOnlyChk.IsChecked = false;
            }

            // Sync ComboBox selection to Volume without re-entering OnModeChanged.
            if (_modeCombo != null)
            {
                _refreshingModeCombo = true;
                try
                {
                    for (int i = 0; i < _modeCombo.Items.Count; i++)
                    {
                        if (_modeCombo.Items[i] is ComboBoxItem ci && ci.Tag is CropMode m && m == CropMode.Volume)
                        {
                            _modeCombo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                finally
                {
                    _refreshingModeCombo = false;
                }
            }

            string axisLbl = _ctx?.DepthAxisName ?? "Z";
            UpdateModeComboTooltip(axisLbl);
            UpdateZInfoText();
        }

        /// <summary>
        /// Called when the user changes the mode ComboBox selection.
        /// Saves the outgoing ROI state to cache, restores (or initialises) the incoming state,
        /// then updates lock states and panel visibility.
        /// </summary>
        private void OnModeChanged(CropMode newMode)
        {
            var oldMode = _mode;
            _mode = newMode;
            bool isSubstackOrVolume = newMode != CropMode.XY;
            bool hasOrtho = _ctx?.OrthoPanel != null;

            // ── Cache / restore ROI state based on transition ─────────────────
            if (_xyRoi != null && _xzRoi != null && _zyRoi != null)
            {
                bool xyWasFull = oldMode == CropMode.Substack;
                bool zWasFull = oldMode == CropMode.XY;

                // Save outgoing state (only when the ROI held meaningful user data).
                if (!xyWasFull) SaveXyCache();
                if (!zWasFull) SaveZCache();

                _syncing = true;
                try
                {
                    if (newMode == CropMode.Substack)
                    {
                        // XY → full data extent (locked); Z → restore or default.
                        ApplyXyFull();
                        RestoreZFromCacheOrDefault();
                    }
                    else if (newMode == CropMode.XY)
                    {
                        // Restore XY; Z → full extent.
                        RestoreXyFromCache();
                        ApplyZFull();
                    }
                    else // Volume
                    {
                        // XY stays as-is (or restore if coming from Substack).
                        if (oldMode == CropMode.Substack) RestoreXyFromCache();
                        // Z stays as-is (or restore if coming from XY).
                        if (oldMode == CropMode.XY) RestoreZFromCacheOrDefault();
                    }
                }
                finally
                {
                    _syncing = false;
                }

                _ctx?.MainView.OverlayManager.InvalidateVisual();
                _ctx?.OrthoPanel?.BottomView.OverlayManager.InvalidateVisual();
                _ctx?.OrthoPanel?.RightView.OverlayManager.InvalidateVisual();
            }

            // ── Lock state ────────────────────────────────────────────────────
            // Substack: XY ROI fully locked (same size as DataBounds → cannot be dragged out).
            // Z is always freely editable; editing Z while in XY mode auto-promotes to Volume.
            if (_xyRoi != null)
            {
                _xyRoi.IsMoveOnly = newMode == CropMode.Substack;
                _xyRoi.IsHeightLocked = false;
            }
            if (_xzRoi != null) _xzRoi.IsHeightLocked = false;
            if (_zyRoi != null) _zyRoi.IsHeightLocked = false;

            // ── Panel visibility ──────────────────────────────────────────────
            if (_thisFrameOnlyChk != null)
            {
                _thisFrameOnlyChk.IsEnabled = !isSubstackOrVolume;
                if (isSubstackOrVolume) _thisFrameOnlyChk.IsChecked = false;
            }
            if (_infoText != null)
                _infoText.IsVisible = newMode != CropMode.Substack;
            if (_zInfoText != null)
                _zInfoText.IsVisible = isSubstackOrVolume && hasOrtho;

            // Update ComboBox tooltip to reflect the new mode.
            string axisLbl = _ctx?.DepthAxisName ?? "Z";
            UpdateModeComboTooltip(axisLbl);

            UpdateZInfoText();

            // Notify followers so they can update Z/mode info via SyncUpdateLeaderBounds.
            if (_role == CropRole.Leader && _xyRoi != null && _ctx?.Data != null)
                RoiBoundsChanged?.Invoke(this, XyRoiToDataBounds());
        }

        // ── ROI cache helpers ─────────────────────────────────────────────────

        private void SaveXyCache()
        {
            if (_xyRoi == null) return;
            _xyCache = (_xyRoi.X, _xyRoi.Y, _xyRoi.Width, _xyRoi.Height);
        }

        private void SaveZCache()
        {
            if (_xzRoi == null) return;
            _zCache = (_xzRoi.Y, _xzRoi.Height);
        }

        /// <summary>Sets the XY ROI to cover the full data extent (used for Substack mode).</summary>
        private void ApplyXyFull()
        {
            if (_xyRoi?.DataBounds is not { } b) return;
            _xyRoi.X = b.X;
            _xyRoi.Y = b.Y;
            _xyRoi.Width = b.Width;
            _xyRoi.Height = b.Height;
            // Sync XZ X/Width from XY.
            if (_xzRoi != null) { _xzRoi.X = _xyRoi.X; _xzRoi.Width = _xyRoi.Width; }
            // Sync ZY X/Width from XY (ZY.X = YCount − XY.Y − XY.Height − 1).
            if (_zyRoi != null) { _zyRoi.X = _dataYCount - _xyRoi.Y - _xyRoi.Height - 1; _zyRoi.Width = _xyRoi.Height; }
        }

        /// <summary>Sets the XZ and ZY ROIs to cover the full Z extent (used for XY mode).</summary>
        private void ApplyZFull()
        {
            if (_xzRoi == null || _zyRoi == null) return;
            _xzRoi.Y = -0.5;
            _xzRoi.Height = _dataZCount;
            _zyRoi.Y = -0.5;
            _zyRoi.Height = _dataZCount;
        }

        /// <summary>Restores XY ROI from cache, or leaves it unchanged if no cache is available.</summary>
        private void RestoreXyFromCache()
        {
            if (_xyRoi == null || _xyCache is not { } c) return;
            _xyRoi.X = c.X;
            _xyRoi.Y = c.Y;
            _xyRoi.Width = c.W;
            _xyRoi.Height = c.H;
            ClampToDataBounds(_xyRoi);
            // Sync XZ X/Width and ZY X/Width from restored XY.
            if (_xzRoi != null) { _xzRoi.X = _xyRoi.X; _xzRoi.Width = _xyRoi.Width; }
            if (_zyRoi != null) { _zyRoi.X = _dataYCount - _xyRoi.Y - _xyRoi.Height - 1; _zyRoi.Width = _xyRoi.Height; }
        }

        /// <summary>
        /// Restores the Z ROI (XZ + ZY) from cache.
        /// Falls back to the middle 50% of the Z range when no cache is available.
        /// </summary>
        private void RestoreZFromCacheOrDefault()
        {
            if (_xzRoi == null || _zyRoi == null) return;

            double zY, zH;
            if (_zCache is { } zc)
            {
                zY = zc.Y;
                zH = Math.Min(zc.H, _dataZCount);
                // Clamp position so the range fits within [−0.5, ZCount−0.5].
                zY = Math.Clamp(zY, -0.5, _dataZCount - zH - 0.5);
            }
            else
            {
                // Default: middle 50% of the Z range.
                zH = Math.Max(1.0, Math.Floor(_dataZCount / 2.0));
                int zStart = _dataZCount / 4;
                zY = zStart - 0.5;
            }

            _xzRoi.Y = zY;
            _xzRoi.Height = zH;
            // ZY: opposite Z direction (FlipY=true on ZY, FlipY=false on XZ).
            _zyRoi.Y = _dataZCount - zY - zH - 1;
            _zyRoi.Height = zH;
        }

        /// <summary>
        /// Rebuilds the mode ComboBox items based on whether orthogonal views are currently active.
        /// When orthogonal views are not open, the ComboBox is hidden and only XY mode is active.
        /// Downgrades the current mode to XY if orthogonal views are no longer available.
        /// </summary>
        private void RefreshModeComboItems(string? depthAxisName)
        {
            if (_modeCombo == null || _refreshingModeCombo) return;
            _refreshingModeCombo = true;
            try
            {
                bool hasOrtho = _ctx?.OrthoPanel != null;
                string axisLabel = depthAxisName ?? "Z";

                // Show/hide the ComboBox depending on orthogonal availability.
                _modeCombo.IsVisible = hasOrtho;

                // Show/hide the hint icon and set its tooltip.
                if (_hintIcon != null)
                {
                    _hintIcon.IsVisible = !hasOrtho;
                    ToolTip.SetTip(_hintIcon,
                        hasOrtho ? null : "Open orthogonal view to enable Substack / 3D crop");
                }

                // When no ortho, remove hint from the crop label (icon carries it now).
                if (_cropLabel != null)
                    ToolTip.SetTip(_cropLabel, null);

                _modeCombo.Items.Clear();

                var xyItem = new ComboBoxItem { Content = "XY", Tag = CropMode.XY };
                ToolTip.SetTip(xyItem, "Crop XY region (all frames retained)");
                _modeCombo.Items.Add(xyItem);

                if (hasOrtho)
                {
                    // Use axis name as the item label; tooltip clarifies it is Substack.
                    var substackItem = new ComboBoxItem { Content = axisLabel, Tag = CropMode.Substack };
                    ToolTip.SetTip(substackItem, $"Substack along {axisLabel} axis (full XY retained)");
                    _modeCombo.Items.Add(substackItem);

                    var volItem = new ComboBoxItem { Content = "3D", Tag = CropMode.Volume };
                    ToolTip.SetTip(volItem, $"Crop XY region and {axisLabel} range simultaneously");
                    _modeCombo.Items.Add(volItem);
                }

                // Downgrade mode if the current mode is no longer available.
                if (!hasOrtho && _mode != CropMode.XY)
                {
                    _mode = CropMode.XY;
                    OnModeChanged(CropMode.XY);
                }

                // Restore selection to match current mode.
                int targetIdx = 0;
                for (int i = 0; i < _modeCombo.Items.Count; i++)
                {
                    if (_modeCombo.Items[i] is ComboBoxItem ci && ci.Tag is CropMode m && m == _mode)
                    {
                        targetIdx = i;
                        break;
                    }
                }
                _modeCombo.SelectedIndex = targetIdx;

                // Update the ComboBox-level tooltip to reflect the active mode.
                UpdateModeComboTooltip(axisLabel);
            }
            finally
            {
                _refreshingModeCombo = false;
            }
        }

        /// <summary>Sets a descriptive tooltip on the mode ComboBox for the current <see cref="_mode"/>.</summary>
        private void UpdateModeComboTooltip(string axisLabel)
        {
            if (_modeCombo == null) return;
            string tip = _mode switch
            {
                CropMode.Substack => $"Substack along {axisLabel} axis (full XY retained)",
                CropMode.Volume => $"Crop XY region and {axisLabel} range simultaneously",
                _ => "Crop XY region (all frames retained)",
            };
            ToolTip.SetTip(_modeCombo, tip);
        }

        /// <summary>Updates the Z range info text from the current XZ ROI position.</summary>
        private void UpdateZInfoText()
        {
            if (_zInfoText == null || _xzRoi == null || _ctx?.Data == null) return;
            if (!_zInfoText.IsVisible) return;

            var depthAxis = _ctx.DepthAxisName != null
                ? _ctx.Data.Dimensions[_ctx.DepthAxisName]
                : null;

            int zStart = (int)(_xzRoi.Y + 0.5);
            int zCount = (int)_xzRoi.Height;
            int zEnd = zStart + zCount - 1;

            if (depthAxis != null && depthAxis.Step != 0)
            {
                double phyStart = depthAxis.Min + zStart * depthAxis.Step;
                double phyEnd = depthAxis.Min + zEnd * depthAxis.Step;
                string unit = string.IsNullOrEmpty(depthAxis.Unit) ? "" : $" {depthAxis.Unit}";
                _zInfoText.Text = $"{depthAxis.Name}: {phyStart:G4} to {phyEnd:G4}{unit} [{zStart + 1}\u2013{zEnd + 1}: {zCount}]";
            }
            else
            {
                string name = depthAxis?.Name ?? "Z";
                _zInfoText.Text = $"{name}: {zStart + 1}\u2013{zEnd + 1} [{zCount}]";
            }
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
                SetInfoText($"W \u00d7 H = {scaledW:G4}\u00d7{scaledH:G4} [{wPxP}\u00d7{hPxP}]");
                _xyRoi.PositionLabel = $"({physX:G4}, {physY:G4})\n[{xIdx}, {yIdx}]";
            }
            else
            {
                SetInfoText($"W \u00d7 H = {wPxP} \u00d7 {hPxP} px");
                _xyRoi.PositionLabel = $"[{xIdx}, {yIdx}]";
            }

            UpdateZInfoText();
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

        private async System.Threading.Tasks.Task OnXzRoiSizeEditRequestedAsync()
        {
            if (_xzRoi == null || _ctx == null) return;
            var owner = TopLevel.GetTopLevel(_ctx.HostVisual) as Window;
            if (owner == null) return;

            var data = _ctx.Data;
            var depthAxis = _ctx.DepthAxisName != null ? data?.Dimensions[_ctx.DepthAxisName] : null;
            double step = depthAxis?.Step ?? 0;
            string unit = depthAxis?.Unit ?? "";
            string name = depthAxis?.Name ?? "Z";

            var dlg = new CropRoiSizeDialog(
                currentWidthPx: _xzRoi.Y + 0.5,   // start (Y in XZ space = Z start index)
                currentHeightPx: _xzRoi.Height,     // count
                widthLabel: $"{name} start:",
                heightLabel: $"{name} count:",
                widthScaleStep: step,
                heightScaleStep: step,
                widthScaleUnit: unit,
                heightScaleUnit: unit,
                maxWidthPx: _dataZCount - 1,        // start can be at most ZCount-1
                maxHeightPx: _dataZCount,            // count can be at most ZCount
                linkHeightMaxToWidth: true);         // count max = ZCount - start
            dlg.Title = "Z Range";

            var result = await dlg.ShowCenteredOnAsync(owner, _ctx.HostVisual);

            if (result is { } r && _xzRoi != null && _ctx != null)
            {
                // result.W = new Z start (pixel count), result.H = new Z count
                double newY = Math.Max(-0.5, r.W - 0.5);
                double newH = Math.Max(1.0, r.H);
                // Clamp to data Z extent
                if (newH > _dataZCount) newH = _dataZCount;
                double maxY = _dataZCount - newH;
                newY = Math.Clamp(newY, -0.5, maxY);

                _syncing = true;
                _xzRoi.Y = newY;
                _xzRoi.Height = newH;
                if (_zyRoi != null) { _zyRoi.Y = _dataZCount - _xzRoi.Y - _xzRoi.Height - 1; _zyRoi.Height = _xzRoi.Height; }
                _syncing = false;

                // Auto-promote XY mode to Volume if Z is no longer full extent.
                if (_mode == CropMode.XY)
                {
                    bool zChanged = Math.Abs(newY) > 0.5 || Math.Abs(newH - _dataZCount) > 0.5;
                    if (zChanged) PromoteXyToVolume();
                }

                _ctx.OrthoPanel?.BottomView.OverlayManager.InvalidateVisual();
                _ctx.OrthoPanel?.RightView.OverlayManager.InvalidateVisual();
                UpdateInfoText();
                if (_xyRoi != null) RoiBoundsChanged?.Invoke(this, XyRoiToDataBounds());
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
                if (_xzSizeEditHandler != null)
                {
                    _xzRoi.SizeEditRequested -= _xzSizeEditHandler;
                    _xzSizeEditHandler = null;
                }
                _ctx.OrthoPanel.BottomView.OverlayManager.RemoveObject(_xzRoi);
                _xzRoi = null;
            }
            if (_zyRoi != null && _ctx.OrthoPanel != null)
            {
                if (_zySizeEditHandler != null)
                {
                    _zyRoi.SizeEditRequested -= _zySizeEditHandler;
                    _zySizeEditHandler = null;
                }
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

            // Store leader Z info for use in ComputeParameters (only when axes match).
            _followerMode = leader.Mode;
            bool zAxisMatches = _ctx.DepthAxisName != null && _ctx.DepthAxisName == leader.ZAxisName;
            if (zAxisMatches)
            {
                _followerZAxisName = leader.ZAxisName;
                _followerZStart = leader.ZStart;
                _followerZCount = leader.ZCount;
            }
            // else: keep existing _followerZAxisName/_followerZStart/_followerZCount (full range for mismatched axis)

            // Update follower XZ/ZY ROIs to reflect the new XY position and, if axes match, Z range.
            if (_xzRoi != null && _zyRoi != null)
            {
                if (zAxisMatches)
                {
                    int zStart = leader.ZCount > 0
                        ? Math.Clamp(leader.ZStart, 0, _dataZCount - 1)
                        : 0;
                    int zCount = leader.ZCount > 0
                        ? Math.Min(leader.ZCount, _dataZCount - zStart)
                        : _dataZCount;

                    _xzRoi.X = _xyRoi.X;
                    _xzRoi.Width = _xyRoi.Width;
                    _xzRoi.Y = zStart - 0.5;
                    _xzRoi.Height = zCount;

                    _zyRoi.X = _dataYCount - _xyRoi.Y - _xyRoi.Height - 1;
                    _zyRoi.Width = _xyRoi.Height;
                    _zyRoi.Y = _dataZCount - zStart - zCount - 0.5;
                    _zyRoi.Height = zCount;
                }
                else
                {
                    // Mismatched axes: only sync XY-driven X/Width; leave Z at full range.
                    _xzRoi.X = _xyRoi.X;
                    _xzRoi.Width = _xyRoi.Width;
                    _zyRoi.X = _dataYCount - _xyRoi.Y - _xyRoi.Height - 1;
                    _zyRoi.Width = _xyRoi.Height;
                }

                _ctx.OrthoPanel?.BottomView.OverlayManager.InvalidateVisual();
                _ctx.OrthoPanel?.RightView.OverlayManager.InvalidateVisual();
            }

            // Update Z info display in the follower panel.
            if (_zInfoText != null)
            {
                if (leader.Mode != CropMode.XY && leader.ZCount > 0)
                {
                    if (zAxisMatches)
                    {
                        string axName = leader.ZAxisName ?? "Z";
                        int zEnd = leader.ZStart + leader.ZCount - 1;
                        _zInfoText.Text = $"{axName}: {leader.ZStart + 1}\u2013{zEnd + 1} ({leader.ZCount})";
                    }
                    else
                    {
                        // Axes differ: orth view shows full range, note it.
                        string axName = _ctx.DepthAxisName ?? "Z";
                        _zInfoText.Text = $"{axName}: full (axis mismatch)";
                    }
                    _zInfoText.IsVisible = true;
                }
                else
                {
                    _zInfoText.IsVisible = false;
                }
            }

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
