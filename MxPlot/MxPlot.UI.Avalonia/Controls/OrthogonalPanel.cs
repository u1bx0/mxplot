using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using System;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Three-pane layout: main view (top-left), right slice (top-right), bottom slice (bottom-left).
    /// <para>
    /// <b>Resize behaviour (WinForms MxViewForm parity):</b><br/>
    /// • Splitter drag in normal mode → main view grows <em>and</em> window grows by the same delta
    ///   so the side view stays fixed.  Use <see cref="VerticalSplitterDragged"/> /
    ///   <see cref="HorizontalSplitterDragged"/> events to resize the host window.<br/>
    /// • Window resize in normal mode → side views absorb the delta; main view stays fixed.<br/>
    /// • Maximised mode → window cannot grow; splitter drag redistributes between main and side view.
    /// </para>
    /// </summary>
    public class OrthogonalPanel : UserControl
    {
        private const double SplitterThickness = 4.0;
        private const double InitialSideWidth = 200.0;
        private const double InitialSideHeight = 200.0;
        private const double MinSideSize = 20.0;
        private const double MinMainSize = 80.0;

        private readonly Grid _grid;
        private Control? _topPanel;           // row 0: tracker panel (col 0 only)
        private readonly Border _hSplitter;           // separates main/bottom rows
        private readonly Border _vSplitter;           // separates main/right columns
        private readonly Border _cornerHandle;        // row 2 × col 1: diagonal resize corner
        private readonly Button _autoResizeBottomBtn; // XZ: auto-height overlay
        private readonly Button _autoResizeRightBtn;  // ZY: auto-width overlay
        private readonly Button _autoShrinkBottomBtn; // XZ: shrink-to-fit overlay (view taller than bitmap)
        private readonly Button _autoShrinkRightBtn;  // ZY: shrink-to-fit overlay (view wider than bitmap)
        private readonly Button _autoResizeMainBtn;   // XY: fit main view / window

        public readonly MxView MainView;
        public readonly MxView RightView;
        public readonly MxView BottomView;
        public readonly ProjectionSelector ProjectionSelector;

        private bool _showRight;
        private bool _showBottom;
        private double _savedSideWidth = InitialSideWidth;
        private double _savedSideHeight = InitialSideHeight;

        // Fixed pixel sizes for main view when side views are visible.
        // Column 0 / Row 1 are switched to fixed-px so that window resize goes to the side views.
        private double _mainViewWidth;
        private double _mainViewHeight;

        // Splitter drag state
        private bool _vDragging;
        private double _vDragOrigin;
        private bool _hDragging;
        private double _hDragOrigin;
        private bool _cDragging;   // corner
        private double _cDragX;
        private double _cDragY;

        // Maximised-mode layout state
        private bool _maximizedMode;
        private double _savedMainViewWidthBeforeMax;
        private double _savedMainViewHeightBeforeMax;

        // ── Events ───────────────────────────────────────────────────────────

        /// <summary>
        /// Fired while the user drags the vertical splitter.
        /// <c>delta</c> &gt; 0 means the main view grew (window should grow right).
        /// The host window should call <c>Width += delta</c> when not maximised.
        /// </summary>
        public event Action<double>? VerticalSplitterDragged;

        /// <summary>
        /// Fired while the user drags the horizontal splitter.
        /// <c>delta</c> &gt; 0 means the main view grew (window should grow down).
        /// The host window should call <c>Height += delta</c> when not maximised.
        /// </summary>
        public event Action<double>? HorizontalSplitterDragged;

        /// <summary>
        /// Fired when the XZ auto-height button is clicked.
        /// The host window should grow its height until the XZ view has no vertical scrollbar.
        /// </summary>
        public event Action? AutoResizeBottomRequested;

        /// <summary>
        /// Fired when the ZY auto-width button is clicked.
        /// The host window should grow its width until the ZY view has no horizontal scrollbar.
        /// </summary>
        public event Action? AutoResizeRightRequested;

        /// <summary>
        /// Fired when the XY auto-resize button is clicked.
        /// In normal mode the host should resize the window to fit the main bitmap.
        /// In ortho mode the host should call <see cref="SetMainViewPixelSize"/> to adjust the main column/row.
        /// </summary>
        public event Action? AutoResizeMainRequested;

        /// <summary>Raised when the user asks to shrink the bottom (XZ) view to fit its bitmap.</summary>
        public event Action? AutoShrinkBottomRequested;

        /// <summary>Raised when the user asks to shrink the right (ZY) view to fit its bitmap.</summary>
        public event Action? AutoShrinkRightRequested;

        // ── ShowRight / ShowBottom ────────────────────────────────────────────

        public bool ShowRight
        {
            get => _showRight;
            set
            {
                if (_showRight && !value)
                {
                    double w = RightView.Bounds.Width;
                    if (w >= MinSideSize) _savedSideWidth = w;
                }
                _showRight = value;
                if (value)
                {
                    if (_maximizedMode)
                    {
                        // Maximised: Column 0 = * (main grows), Column 2 = fixed (side stays)
                        _grid.ColumnDefinitions[0].Width = GridLength.Star;
                        _grid.ColumnDefinitions[1].Width = new GridLength(SplitterThickness);
                        _grid.ColumnDefinitions[2].Width = new GridLength(_savedSideWidth);
                        _grid.ColumnDefinitions[2].MinWidth = MinSideSize;
                    }
                    else
                    {
                        // Normal: Column 0 = fixed (main stays), Column 2 = * (side grows with window).
                        // Use MainView.Bounds.Width (main-view-only width) instead of the total panel
                        // width so that axis switches (ShowRight false→true without window resize) don't
                        // accidentally inflate _mainViewWidth to include the side-view area.
                        _mainViewWidth = MainView.Bounds.Width > MinMainSize ? MainView.Bounds.Width : 400.0;
                        _grid.ColumnDefinitions[0].Width = new GridLength(_mainViewWidth);
                        _grid.ColumnDefinitions[1].Width = new GridLength(SplitterThickness);
                        _grid.ColumnDefinitions[2].Width = GridLength.Star;
                        _grid.ColumnDefinitions[2].MinWidth = MinSideSize;
                    }
                }
                else
                {
                    _grid.ColumnDefinitions[0].Width = GridLength.Star;
                    _grid.ColumnDefinitions[1].Width = new GridLength(0);
                    _grid.ColumnDefinitions[2].Width = new GridLength(0);
                    _grid.ColumnDefinitions[2].MinWidth = 0;
                }
                        _vSplitter.IsVisible = value;
                        RightView.IsVisible = value;
                        _cornerHandle.IsVisible = _showRight && _showBottom;
                        ProjectionSelector.IsVisible = _showRight && _showBottom;
                        UpdateAutoResizeButtonVisibility();
                    }
                }

        public bool ShowBottom
        {
            get => _showBottom;
            set
            {
                if (_showBottom && !value)
                {
                    double h = BottomView.Bounds.Height;
                    if (h >= MinSideSize) _savedSideHeight = h;
                }
                _showBottom = value;
                if (value)
                {
                    if (_maximizedMode)
                    {
                        // Maximised: Row 1 = * (main grows), Row 3 = fixed (side stays)
                        _grid.RowDefinitions[1].Height = GridLength.Star;
                        _grid.RowDefinitions[2].Height = new GridLength(SplitterThickness);
                        _grid.RowDefinitions[3].Height = new GridLength(_savedSideHeight);
                        _grid.RowDefinitions[3].MinHeight = MinSideSize;
                    }
                    else
                    {
                        // Normal: Row 1 = fixed (main stays), Row 3 = * (side grows with window)
                        _mainViewHeight = MainView.Bounds.Height > MinMainSize
                            ? MainView.Bounds.Height
                            : 300.0;
                        _grid.RowDefinitions[1].Height = new GridLength(_mainViewHeight);
                        _grid.RowDefinitions[2].Height = new GridLength(SplitterThickness);
                        _grid.RowDefinitions[3].Height = GridLength.Star;
                        _grid.RowDefinitions[3].MinHeight = MinSideSize;
                    }
                }
                else
                {
                    _grid.RowDefinitions[1].Height = GridLength.Star;
                    _grid.RowDefinitions[2].Height = new GridLength(0);
                    _grid.RowDefinitions[3].Height = new GridLength(0);
                    _grid.RowDefinitions[3].MinHeight = 0;
                }
                        _hSplitter.IsVisible = value;
                        BottomView.IsVisible = value;
                        _cornerHandle.IsVisible = _showRight && _showBottom;
                        ProjectionSelector.IsVisible = _showRight && _showBottom;
                        UpdateAutoResizeButtonVisibility();
                    }
                }

        // ── Constructor ───────────────────────────────────────────────────────

        public OrthogonalPanel()
        {
            MainView = new MxView { ClipToBounds = true };
            BottomView = new MxView
            {
                ClipToBounds = true,
                IsVisible = false,
                Transform = ViewTransform.FlipV,
                ContentAlignment = ContentAlignment.Top,
                FlipY = false,
            };
            BottomView.SetAsDependentView();
            RightView = new MxView
            {
                ClipToBounds = true,
                IsVisible = false,
                Transform = ViewTransform.Rotate90CCW,
                ContentAlignment = ContentAlignment.Left,
            };
            RightView.SetAsDependentView();

            _vSplitter = new Border
            {
                Width = SplitterThickness,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.DarkGray,
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
                IsVisible = false,
            };
            _hSplitter = new Border
            {
                Height = SplitterThickness,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = Brushes.DarkGray,
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
                IsVisible = false,
            };
            _cornerHandle = new Border
            {
                Width = SplitterThickness,
                Height = SplitterThickness,
                Background = Brushes.Gray,
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                IsVisible = false,
            };

            WireVSplitter();
            WireHSplitter();
            WireCornerHandle();

            _grid = new Grid();
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(0)));
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // row 0: tracker
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Star));   // row 1: MainView + RightView
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(0))); // row 2: hSplitter
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(0))); // row 3: BottomView

            ProjectionSelector = new ProjectionSelector { IsVisible = false };

            Grid.SetRow(MainView, 1); Grid.SetColumn(MainView, 0);
            Grid.SetRow(RightView, 1); Grid.SetColumn(RightView, 2);
            Grid.SetRow(BottomView, 3); Grid.SetColumn(BottomView, 0);
            Grid.SetRow(ProjectionSelector, 3); Grid.SetColumn(ProjectionSelector, 2);
            Grid.SetRow(_vSplitter, 1); Grid.SetColumn(_vSplitter, 1); Grid.SetRowSpan(_vSplitter, 3);
            Grid.SetRow(_hSplitter, 2); Grid.SetColumn(_hSplitter, 0); Grid.SetColumnSpan(_hSplitter, 3);
            Grid.SetRow(_cornerHandle, 2); Grid.SetColumn(_cornerHandle, 1); // on top of both splitters

            _grid.Children.Add(MainView);
            _grid.Children.Add(RightView);
            _grid.Children.Add(BottomView);
            _grid.Children.Add(ProjectionSelector);
            _grid.Children.Add(_vSplitter);
            _grid.Children.Add(_hSplitter);
            _grid.Children.Add(_cornerHandle); // last → drawn on top

            // Auto-resize overlay buttons (drawn on top of side views). Each view's own busy
            // spinner (MxView.IsBusy) always sits at that view's top-left corner, so the two
            // buttons are pinned to different corners per view to avoid overlapping it.
            // Expand: arrow toward the edge being moved, bar = that edge. Shrink: arrow pulling the
            // same edge back. Each shrink button sits in a different corner from its Expand button.
            // Ctrl+click on any of the four fits both side views instead of the one the button belongs to.
            const string FitBothHint = "\nCtrl+click: fit both side views";
            _autoResizeBottomBtn = MakeAutoResizeButton(
                "M 5,0 L 5,11 M 1,7 L 5,11 L 9,7 M 0,14 L 10,14", 10, 14, "Expand to fit" + FitBothHint);
            _autoResizeBottomBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _autoResizeBottomBtn.VerticalAlignment = VerticalAlignment.Bottom;
            WireSideFitButton(_autoResizeBottomBtn, () => AutoResizeBottomRequested?.Invoke());
            Grid.SetRow(_autoResizeBottomBtn, 3); Grid.SetColumn(_autoResizeBottomBtn, 0);

            _autoShrinkBottomBtn = MakeAutoResizeButton(
                "M 5,2 L 5,11 M 1,6 L 5,2 L 9,6 M 0,14 L 10,14", 10, 14, "Shrink to fit" + FitBothHint);
            _autoShrinkBottomBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _autoShrinkBottomBtn.VerticalAlignment = VerticalAlignment.Top;
            WireSideFitButton(_autoShrinkBottomBtn, () => AutoShrinkBottomRequested?.Invoke());
            Grid.SetRow(_autoShrinkBottomBtn, 3); Grid.SetColumn(_autoShrinkBottomBtn, 0);

            _autoResizeRightBtn = MakeAutoResizeButton(
                "M 0,5 L 11,5 M 7,1 L 11,5 L 7,9 M 14,0 L 14,10", 14, 10, "Expand to fit" + FitBothHint);
            _autoResizeRightBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _autoResizeRightBtn.VerticalAlignment = VerticalAlignment.Top;
            WireSideFitButton(_autoResizeRightBtn, () => AutoResizeRightRequested?.Invoke());
            Grid.SetRow(_autoResizeRightBtn, 1); Grid.SetColumn(_autoResizeRightBtn, 2);

            _autoShrinkRightBtn = MakeAutoResizeButton(
                "M 3,5 L 14,5 M 7,1 L 3,5 L 7,9 M 14,0 L 14,10", 14, 10, "Shrink to fit" + FitBothHint);
            _autoShrinkRightBtn.HorizontalAlignment = HorizontalAlignment.Left;
            _autoShrinkRightBtn.VerticalAlignment = VerticalAlignment.Top;
            WireSideFitButton(_autoShrinkRightBtn, () => AutoShrinkRightRequested?.Invoke());
            Grid.SetRow(_autoShrinkRightBtn, 1); Grid.SetColumn(_autoShrinkRightBtn, 2);

            _grid.Children.Add(_autoResizeBottomBtn);
            _grid.Children.Add(_autoResizeRightBtn);
            _grid.Children.Add(_autoShrinkBottomBtn);
            _grid.Children.Add(_autoShrinkRightBtn);

            // Auto-resize main: diagonal ↖↘ icon (top-left and bottom-right corners with arrow tips)
            // 16×16 canvas: two L-shaped corner brackets pointing NW and SE
            _autoResizeMainBtn = MakeAutoResizeButton(
                "M 6,1 L 1,1 L 1,6 M 1,1 L 7,7  M 9,9 L 15,15 M 10,15 L 15,15 L 15,10",
                16, 16, "Fit window/view to bitmap");
            _autoResizeMainBtn.HorizontalAlignment = HorizontalAlignment.Right;
            _autoResizeMainBtn.VerticalAlignment = VerticalAlignment.Bottom;
            _autoResizeMainBtn.Click += (_, _) => AutoResizeMainRequested?.Invoke();
            Grid.SetRow(_autoResizeMainBtn, 1); Grid.SetColumn(_autoResizeMainBtn, 0);
            _grid.Children.Add(_autoResizeMainBtn);

            Content = _grid;

            // Update button visibility whenever view display state changes (zoom/pan/resize,
            // bitmap rebuild, or scrollbar visibility change after cross-view sync).
            BottomView.ViewportChanged += (_, _) => UpdateAutoResizeButtonVisibility();
            RightView.ViewportChanged  += (_, _) => UpdateAutoResizeButtonVisibility();
            MainView.ViewportChanged   += (_, _) => UpdateAutoResizeButtonVisibility();

            // The shrink buttons depend on the side views' laid-out size, which settles after a
            // window resize (including the one a fit button itself causes) without a viewport change.
            BottomView.SizeChanged += (_, _) => UpdateAutoResizeButtonVisibility();
            RightView.SizeChanged  += (_, _) => UpdateAutoResizeButtonVisibility();
        }

        // ── Splitter drag wiring ──────────────────────────────────────────────

        private void WireVSplitter()
        {
            _vSplitter.PointerPressed += (_, e) =>
            {
                _vDragging = true;
                _vDragOrigin = e.GetPosition(this).X;
                e.Pointer.Capture(_vSplitter);
                e.Handled = true;
            };
            _vSplitter.PointerMoved += (_, e) =>
            {
                if (!_vDragging) return;
                double x = e.GetPosition(this).X;
                double delta = x - _vDragOrigin;
                if (Math.Abs(delta) < 0.5) return;
                _vDragOrigin = x;

                if (_maximizedMode)
                {
                    // Maximised: Column 2 is fixed; drag right shrinks side view, main (*) grows
                    double maxSide = Bounds.Width - SplitterThickness - MinMainSize;
                    double newSide = Math.Clamp(_savedSideWidth - delta, MinSideSize, maxSide);
                    _savedSideWidth = newSide;
                    _grid.ColumnDefinitions[2].Width = new GridLength(_savedSideWidth);
                }
                else
                {
                    // Normal: Column 0 is fixed; drag right grows main view and window
                    double maxW = Math.Max(MinMainSize, Bounds.Width - SplitterThickness - MinSideSize);
                    double newW = Math.Clamp(_mainViewWidth + delta, MinMainSize, maxW);
                    double actual = newW - _mainViewWidth;
                    _mainViewWidth = newW;
                    _grid.ColumnDefinitions[0].Width = new GridLength(_mainViewWidth);
                    if (Math.Abs(actual) > 0.1) VerticalSplitterDragged?.Invoke(actual);
                }
                e.Handled = true;
            };
            _vSplitter.PointerReleased += (_, e) =>
            {
                _vDragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
        }

        private void WireHSplitter()
        {
            _hSplitter.PointerPressed += (_, e) =>
            {
                _hDragging = true;
                _hDragOrigin = e.GetPosition(this).Y;
                e.Pointer.Capture(_hSplitter);
                e.Handled = true;
            };
            _hSplitter.PointerMoved += (_, e) =>
            {
                if (!_hDragging) return;
                double y = e.GetPosition(this).Y;
                double delta = y - _hDragOrigin;
                if (Math.Abs(delta) < 0.5) return;
                _hDragOrigin = y;

                if (_maximizedMode)
                {
                    // Maximised: Row 3 is fixed; drag down shrinks bottom view, main (*) grows
                    double topH = _topPanel?.Bounds.Height ?? 0;
                    double maxSide = Bounds.Height - topH - SplitterThickness - MinMainSize;
                    double newSide = Math.Clamp(_savedSideHeight - delta, MinSideSize, maxSide);
                    _savedSideHeight = newSide;
                    _grid.RowDefinitions[3].Height = new GridLength(_savedSideHeight);
                }
                else
                {
                    // Normal: Row 1 is fixed; drag down grows main view and window
                    double topH = _topPanel?.Bounds.Height ?? 0;
                    double maxH = Math.Max(MinMainSize, Bounds.Height - topH - SplitterThickness - MinSideSize);
                    double newH = Math.Clamp(_mainViewHeight + delta, MinMainSize, maxH);
                    double actual = newH - _mainViewHeight;
                    _mainViewHeight = newH;
                    _grid.RowDefinitions[1].Height = new GridLength(_mainViewHeight);
                    if (Math.Abs(actual) > 0.1) HorizontalSplitterDragged?.Invoke(actual);
                }
                e.Handled = true;
            };
            _hSplitter.PointerReleased += (_, e) =>
            {
                _hDragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
        }

        private void WireCornerHandle()
        {
            _cornerHandle.PointerPressed += (_, e) =>
            {
                _cDragging = true;
                var p = e.GetPosition(this);
                _cDragX = p.X;
                _cDragY = p.Y;
                e.Pointer.Capture(_cornerHandle);
                e.Handled = true;
            };
            _cornerHandle.PointerMoved += (_, e) =>
            {
                if (!_cDragging) return;
                var p = e.GetPosition(this);
                double dx = p.X - _cDragX;
                double dy = p.Y - _cDragY;
                _cDragX = p.X;
                _cDragY = p.Y;

                // ── Horizontal ──────────────────────────────────────────────
                if (Math.Abs(dx) >= 0.5)
                {
                    if (_maximizedMode)
                    {
                        double maxSide = Bounds.Width - SplitterThickness - MinMainSize;
                        double newSide = Math.Clamp(_savedSideWidth - dx, MinSideSize, maxSide);
                        _savedSideWidth = newSide;
                        _grid.ColumnDefinitions[2].Width = new GridLength(_savedSideWidth);
                    }
                    else
                    {
                        double maxW = Math.Max(MinMainSize, Bounds.Width - SplitterThickness - MinSideSize);
                        double newW = Math.Clamp(_mainViewWidth + dx, MinMainSize, maxW);
                        double actualX = newW - _mainViewWidth;
                        _mainViewWidth = newW;
                        _grid.ColumnDefinitions[0].Width = new GridLength(_mainViewWidth);
                        if (Math.Abs(actualX) > 0.1) VerticalSplitterDragged?.Invoke(actualX);
                    }
                }

                // ── Vertical ────────────────────────────────────────────────
                if (Math.Abs(dy) >= 0.5)
                {
                    if (_maximizedMode)
                    {
                        double topH = _topPanel?.Bounds.Height ?? 0;
                        double maxSide = Bounds.Height - topH - SplitterThickness - MinMainSize;
                        double newSide = Math.Clamp(_savedSideHeight - dy, MinSideSize, maxSide);
                        _savedSideHeight = newSide;
                        _grid.RowDefinitions[3].Height = new GridLength(_savedSideHeight);
                    }
                    else
                    {
                        double topH = _topPanel?.Bounds.Height ?? 0;
                        double maxH = Math.Max(MinMainSize, Bounds.Height - topH - SplitterThickness - MinSideSize);
                        double newH = Math.Clamp(_mainViewHeight + dy, MinMainSize, maxH);
                        double actualY = newH - _mainViewHeight;
                        _mainViewHeight = newH;
                        _grid.RowDefinitions[1].Height = new GridLength(_mainViewHeight);
                        if (Math.Abs(actualY) > 0.1) HorizontalSplitterDragged?.Invoke(actualY);
                    }
                }

                e.Handled = true;
            };
            _cornerHandle.PointerReleased += (_, e) =>
            {
                _cDragging = false;
                e.Pointer.Capture(null);
                e.Handled = true;
            };
        }

        // ── Bounds change: enforce minimum side-view sizes ────────────────────

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property != BoundsProperty) return;
            var b = change.GetNewValue<Rect>();

            if (_showRight && b.Width > 0 && !_maximizedMode)
            {
                double maxW = Math.Max(MinMainSize, b.Width - SplitterThickness - MinSideSize);
                if (_mainViewWidth > maxW)
                {
                    _mainViewWidth = maxW;
                    _grid.ColumnDefinitions[0].Width = new GridLength(_mainViewWidth);
                }
            }
            if (_showBottom && b.Height > 0 && !_maximizedMode)
            {
                double topH = _topPanel?.Bounds.Height ?? 0;
                double maxH = Math.Max(MinMainSize, b.Height - topH - SplitterThickness - MinSideSize);
                if (_mainViewHeight > maxH)
                {
                    _mainViewHeight = maxH;
                    _grid.RowDefinitions[1].Height = new GridLength(_mainViewHeight);
                }
            }
        }

        // ── Maximised-mode layout switch ──────────────────────────────────────

        /// <summary>
        /// Set to <c>true</c> when the host window is maximised.<br/>
        /// In maximised mode the column/row layout is inverted so that the main view
        /// absorbs the extra screen space (<c>Column 0 = *</c>, <c>Column 2 = fixed</c>)
        /// while the side views retain their user-defined size.
        /// </summary>
        public bool MaximizedMode
        {
            get => _maximizedMode;
            set
            {
                if (_maximizedMode == value) return;
                _maximizedMode = value; // set first so bounds/splitter handlers see the correct mode
                if (value) SwitchToMaximizedLayout();
                else SwitchToNormalLayout();
            }
        }

        private void SwitchToMaximizedLayout()
        {
            // Save pre-maximise main-view sizes so they can be restored later.
            _savedMainViewWidthBeforeMax = _mainViewWidth;
            _savedMainViewHeightBeforeMax = _mainViewHeight;

            if (_showRight)
            {
                // Use _savedSideWidth (from user actions: ShowRight toggle / maximised
                // splitter drag) instead of RightView.Bounds.Width which may already be
                // inflated by the Star column absorbing the maximised window size before
                // this method runs.
                _grid.ColumnDefinitions[0].Width = GridLength.Star;
                _grid.ColumnDefinitions[2].Width = new GridLength(_savedSideWidth);
                _grid.ColumnDefinitions[2].MinWidth = MinSideSize;
            }
            if (_showBottom)
            {
                _grid.RowDefinitions[1].Height = GridLength.Star;
                _grid.RowDefinitions[3].Height = new GridLength(_savedSideHeight);
                _grid.RowDefinitions[3].MinHeight = MinSideSize;
            }
            UpdateAutoResizeButtonVisibility();
        }

        private void SwitchToNormalLayout()
        {
            if (_showRight)
            {
                // _savedSideWidth is already up-to-date from maximised-mode splitter drags.
                // Column 2 becomes Star; no need to re-capture from (possibly stale) Bounds.
                _mainViewWidth = _savedMainViewWidthBeforeMax > MinMainSize
                    ? _savedMainViewWidthBeforeMax
                    : Math.Max(MinMainSize, Bounds.Width - SplitterThickness - _savedSideWidth);
                _grid.ColumnDefinitions[0].Width = new GridLength(_mainViewWidth);
                _grid.ColumnDefinitions[2].Width = GridLength.Star;
                _grid.ColumnDefinitions[2].MinWidth = MinSideSize;
            }
            if (_showBottom)
            {
                // _savedSideHeight is already up-to-date from maximised-mode splitter drags.
                double topH = _topPanel?.Bounds.Height ?? 0;
                _mainViewHeight = _savedMainViewHeightBeforeMax > MinMainSize
                    ? _savedMainViewHeightBeforeMax
                    : Math.Max(MinMainSize, Bounds.Height - topH - SplitterThickness - _savedSideHeight);
                _grid.RowDefinitions[1].Height = new GridLength(_mainViewHeight);
                _grid.RowDefinitions[3].Height = GridLength.Star;
                _grid.RowDefinitions[3].MinHeight = MinSideSize;
            }
            UpdateAutoResizeButtonVisibility();
        }

        // ── Window-resize helpers ─────────────────────────────────────────────

        /// <summary>Total width delta (right pane + splitter) when side views are first shown.</summary>
        public static double InitialSideDeltaWidth => InitialSideWidth + SplitterThickness;

        /// <summary>Total height delta (bottom pane + splitter) when side views are first shown.</summary>
        public static double InitialSideDeltaHeight => InitialSideHeight + SplitterThickness;

        /// <summary>
        /// <c>true</c> while neither side view has been resized from its initial size, i.e. there is
        /// no user-chosen size that a reopen should preserve.
        /// </summary>
        public bool IsSideSizeInitial =>
            _savedSideWidth == InitialSideWidth && _savedSideHeight == InitialSideHeight;

        /// <summary>
        /// Raises the right-side, bottom-side and main-view auto-resize requests in that order,
        /// so each side view ends at its fit size (grown where the screen allows, or shrunk when
        /// the stack is shorter than the initial size) and the main view is fitted last.
        /// Each side request is a no-op in the direction it does not apply to.
        /// </summary>
        public void RequestAutoResizeAll()
        {
            AutoShrinkRightRequested?.Invoke();
            AutoResizeRightRequested?.Invoke();
            AutoShrinkBottomRequested?.Invoke();
            AutoResizeBottomRequested?.Invoke();
            AutoResizeMainRequested?.Invoke();
        }

        /// <summary>
        /// Raises the shrink and expand requests of both side views, so each ends at its fit size
        /// whichever direction that is. The main view is left as it is.
        /// </summary>
        public void RequestAutoResizeSides()
        {
            AutoShrinkRightRequested?.Invoke();
            AutoResizeRightRequested?.Invoke();
            AutoShrinkBottomRequested?.Invoke();
            AutoResizeBottomRequested?.Invoke();
        }

        /// <summary>
        /// Makes <paramref name="button"/> raise its own request on a plain click and
        /// <see cref="RequestAutoResizeSides"/> on a click with Ctrl (or the Command key) held.
        /// </summary>
        private void WireSideFitButton(Button button, Action raiseOwnRequest)
        {
            // Click carries no key state, so it is read from the pointer release that raises the click.
            bool fitBoth = false;
            button.AddHandler(PointerReleasedEvent,
                (_, e) => fitBoth = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0,
                global::Avalonia.Interactivity.RoutingStrategies.Tunnel);
            button.Click += (_, _) =>
            {
                bool both = fitBoth;
                fitBoth = false;
                if (both) RequestAutoResizeSides();
                else raiseOwnRequest();
            };
        }

        /// <summary>Width delta to add to the window when reopening the right side view.</summary>
        public double SavedSideDeltaWidth => _savedSideWidth + SplitterThickness;

        /// <summary>Height delta to add to the window when reopening the bottom side view.</summary>
        public double SavedSideDeltaHeight => _savedSideHeight + SplitterThickness;

        // ── Column-0 top panel ────────────────────────────────────────────────

        public void SetTopPanel(Control panel)
        {
            if (_topPanel != null)
                _grid.Children.Remove(_topPanel);
            _topPanel = panel;
            Grid.SetRow(panel, 0);
            Grid.SetColumn(panel, 0);
            // Row 0 holds nothing else, so the panel may reach over the side view's column; the host
            // decides how wide it actually is.
            Grid.SetColumnSpan(panel, 3);
            _grid.Children.Add(panel);
        }

        /// <summary>
        /// Returns the current pixel deltas occupied by the visible side panels (pane + splitter).
        /// Call BEFORE deactivating to capture the sizes to subtract from the window.
        /// </summary>
        public (double RightWidth, double BottomHeight) GetCurrentSideSizes()
        {
            double rw = _showRight ? RightView.Bounds.Width + SplitterThickness : 0;
            double bh = _showBottom ? BottomView.Bounds.Height + SplitterThickness : 0;
            return (rw, bh);
        }

        // ── Main view pixel size ──────────────────────────────────────────────

        /// <summary>
        /// Programmatically sets the main view pixel size (equivalent to dragging both splitters to the target).
        /// Fires <see cref="VerticalSplitterDragged"/> and/or <see cref="HorizontalSplitterDragged"/> with the
        /// applied delta so the host window grows or shrinks to accommodate.
        /// Only effective in non-maximised mode with the respective side panel visible.
        /// </summary>
        public void SetMainViewPixelSize(double targetW, double targetH)
        {
            if (_maximizedMode) return;
            if (_showRight && targetW >= MinMainSize)
            {
                double actual = targetW - _mainViewWidth;
                _mainViewWidth = targetW;
                _grid.ColumnDefinitions[0].Width = new GridLength(_mainViewWidth);
                if (Math.Abs(actual) > 0.1) VerticalSplitterDragged?.Invoke(actual);
            }
            if (_showBottom && targetH >= MinMainSize)
            {
                double actual = targetH - _mainViewHeight;
                _mainViewHeight = targetH;
                _grid.RowDefinitions[1].Height = new GridLength(_mainViewHeight);
                if (Math.Abs(actual) > 0.1) HorizontalSplitterDragged?.Invoke(actual);
            }
        }

        // ── Auto-resize overlay buttons ───────────────────────────────────────

        // A view may exceed its fit size by this much before the shrink button appears, so a view
        // that was just fitted (or sits within rounding of it) does not offer to shrink.
        private const double ShrinkTolerance = 4.0;

        /// <summary>
        /// Natural size of the projection selector, which occupies the corner shared by the bottom
        /// and right views while both are shown: its height is the least the bottom view can be
        /// fitted to, its width the least for the right view. Falls back to
        /// <see cref="MinSideSize"/> when the selector is not shown.
        /// </summary>
        private Size ProjectionSelectorSize
        {
            get
            {
                if (!ProjectionSelector.IsVisible) return new Size(MinSideSize, MinSideSize);
                // Measured unconstrained: DesiredSize under the current cell would be clipped to it.
                ProjectionSelector.Measure(Size.Infinity);
                var d = ProjectionSelector.DesiredSize;
                return new Size(Math.Max(d.Width, MinSideSize), Math.Max(d.Height, MinSideSize));
            }
        }

        /// <summary>
        /// Height the bottom (XZ) view needs to show its whole bitmap plus a margin of one data
        /// step (at least 50 DIP). Target of both expand and shrink.
        /// </summary>
        public double BottomFitHeight
        {
            get
            {
                var (_, bmpH) = BottomView.GetEffectiveBmpDims();
                int count = BottomView.MatrixData?.YCount ?? 1;
                double oneDip = count > 0 ? bmpH / count : 50.0;
                return Math.Max(bmpH + Math.Max(oneDip, 50.0), ProjectionSelectorSize.Height);
            }
        }

        /// <summary>Width counterpart of <see cref="BottomFitHeight"/> for the right (ZY) view.</summary>
        public double RightFitWidth
        {
            get
            {
                var (bmpW, _) = RightView.GetEffectiveBmpDims();
                int count = RightView.MatrixData?.YCount ?? 1;
                double oneDip = count > 0 ? bmpW / count : 50.0;
                return Math.Max(bmpW + Math.Max(oneDip, 50.0), ProjectionSelectorSize.Width);
            }
        }

        private void UpdateAutoResizeButtonVisibility()
        {
            _autoResizeBottomBtn.IsVisible = _showBottom && BottomView.HasVerticalScrollbar  && !_maximizedMode;
            _autoResizeRightBtn.IsVisible  = _showRight  && RightView.HasHorizontalScrollbar && !_maximizedMode;
            _autoResizeMainBtn.IsVisible   = !_maximizedMode && !MainView.IsFitToView;
            _autoShrinkBottomBtn.IsVisible = _showBottom && !_maximizedMode
                && BottomView.Bounds.Height > BottomFitHeight + ShrinkTolerance;
            _autoShrinkRightBtn.IsVisible  = _showRight && !_maximizedMode
                && RightView.Bounds.Width > RightFitWidth + ShrinkTolerance;
        }

        private static Button MakeAutoResizeButton(string iconPath, double iconW, double iconH, string tooltip)
        {
            var btn = new Button
            {
                Padding = new Thickness(4, 3),
                Background = new SolidColorBrush(Color.FromArgb(160, 30, 30, 30)),
                Foreground = Brushes.White,
                IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4),
                Content = new Path
                {
                    Width = iconW,
                    Height = iconH,
                    Data = Geometry.Parse(iconPath),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.5,
                    StrokeLineCap = PenLineCap.Square,
                    Stretch = Stretch.Fill,
                },
            };
            ToolTip.SetTip(btn, tooltip);
            return btn;
        }
    }
}
