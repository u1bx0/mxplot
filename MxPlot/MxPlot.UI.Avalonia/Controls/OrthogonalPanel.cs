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
        private const double InitialSideHeight = 180.0;
        private const double MinSideSize = 20.0;
        private const double MinMainSize = 80.0;

        private readonly Grid _grid;
        private Control? _topPanel;           // row 0: tracker panel (col 0 only)
        private readonly Border _hSplitter;           // separates main/bottom rows
        private readonly Border _vSplitter;           // separates main/right columns
        private readonly Border _cornerHandle;        // row 2 × col 1: diagonal resize corner
        private readonly Button _autoResizeBottomBtn; // XZ: auto-height overlay
        private readonly Button _autoResizeRightBtn;  // ZY: auto-width overlay
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

            // Auto-resize overlay buttons (drawn on top of side views)
            _autoResizeBottomBtn = MakeAutoResizeButton(
                "M 0,0 L 10,0 M 0,14 L 10,14 M 5,0 L 5,5 M 5,9 L 5,14 M 2,4 L 5,0 L 8,4 M 2,10 L 5,14 L 8,10 M 5,5 L 5,6.5 M 5,7.5 L 5,9",
                10, 14, "Expand");
            _autoResizeBottomBtn.Click += (_, _) => AutoResizeBottomRequested?.Invoke();
            Grid.SetRow(_autoResizeBottomBtn, 3); Grid.SetColumn(_autoResizeBottomBtn, 0);

            _autoResizeRightBtn = MakeAutoResizeButton(
                "M 0,0 L 0,10 M 14,0 L 14,10 M 0,5 L 5,5 M 9,5 L 14,5 M 4,2 L 0,5 L 4,8 M 10,2 L 14,5 L 10,8 M 5,5 L 6.5,5 M 7.5,5 L 9,5",
                14, 10, "Expand");
            _autoResizeRightBtn.Click += (_, _) => AutoResizeRightRequested?.Invoke();
            Grid.SetRow(_autoResizeRightBtn, 1); Grid.SetColumn(_autoResizeRightBtn, 2);

            _grid.Children.Add(_autoResizeBottomBtn);
            _grid.Children.Add(_autoResizeRightBtn);

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

            // Update button visibility whenever scroll state changes
            BottomView.ScrollStateChanged += (_, _) => UpdateAutoResizeButtonVisibility();
            RightView.ScrollStateChanged  += (_, _) => UpdateAutoResizeButtonVisibility();
            MainView.ScrollStateChanged   += (_, _) => UpdateAutoResizeButtonVisibility();
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

        private void UpdateAutoResizeButtonVisibility()
        {
            _autoResizeBottomBtn.IsVisible = _showBottom && BottomView.HasVerticalScrollbar  && !_maximizedMode;
            _autoResizeRightBtn.IsVisible  = _showRight  && RightView.HasHorizontalScrollbar && !_maximizedMode;
            _autoResizeMainBtn.IsVisible   = !_maximizedMode && !MainView.IsFitToView;
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
