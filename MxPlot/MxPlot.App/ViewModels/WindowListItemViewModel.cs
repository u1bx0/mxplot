using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.App.ViewModels
{
    /// <summary>
    /// Base ViewModel for a single managed window card in the dashboard list.
    /// Handles all common logic: title sync, minimize intercept, thumbnail display,
    /// rename, visibility toggle, and <see cref="IPlotTreeNode"/> leaf implementation.
    /// <para/>
    /// Specialised subclasses (e.g. <see cref="MatrixPlotterListItemViewModel"/>) override
    /// the virtual data-related properties to expose type-specific information.
    /// For window types that only differ in fallback icon, pass a <c>fallbackBitmapUri</c>
    /// to the constructor and use this class directly.
    /// </summary>
    public partial class WindowListItemViewModel : ViewModelBase, IPlotTreeNode
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ToolTipTitle))]
        private string _fileName = string.Empty;

        [ObservableProperty]
        private string _metaData = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRealThumbnail))]
        [NotifyPropertyChangedFor(nameof(ShowFallbackBitmap))]
        [NotifyPropertyChangedFor(nameof(ShowFallbackIcon))]
        private Bitmap? _thumbnail;

        private const string GenericWindowIconPath =
            "M0,0 H14 V2 H0 Z  M0,2 H2 V14 H0 Z  M12,2 H14 V14 H12 Z  M0,12 H14 V14 H0 Z";

        /// <summary>
        /// PNG-asset fallback used for window types that supply a <c>fallbackBitmapUri</c>.
        /// <c>null</c> when the generic window-frame <see cref="PathIcon"/> is used instead.
        /// </summary>
        public Bitmap? FallbackBitmap { get; }

        /// <summary>True when a PNG asset is used as the fallback; false when PathIcon geometry is used.</summary>
        public bool HasFallbackBitmap => FallbackBitmap != null;

        /// <summary>
        /// Filled-path geometry fallback for generic windows (window-frame icon).
        /// <c>null</c> when <see cref="FallbackBitmap"/> is set instead.
        /// </summary>
        public Geometry? FallbackIconData { get; }

        /// <summary>True once a real bitmap thumbnail has been captured.</summary>
        public bool HasRealThumbnail => Thumbnail != null;

        /// <summary>True when no real thumbnail is available and a PNG-asset fallback exists.</summary>
        public bool ShowFallbackBitmap => !HasRealThumbnail && HasFallbackBitmap;

        /// <summary>True when no real thumbnail is available and no PNG-asset fallback exists.</summary>
        public bool ShowFallbackIcon => !HasRealThumbnail && !HasFallbackBitmap;

        [ObservableProperty]
        private bool _isSelected;

        /// <summary>
        /// <c>false</c> when the window is explicitly hidden by the user.
        /// The window remains loaded in memory.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CardOpacity))]
        private bool _isWindowVisible = true;

        /// <summary>Opacity for the list card: full when visible, dimmed when hidden.</summary>
        public double CardOpacity => IsWindowVisible ? 1.0 : 0.4;

        /// <summary>True while the filename TextBox is active for inline editing.</summary>
        [ObservableProperty]
        private bool _isRenaming;

        /// <summary>Scratch buffer for the in-progress rename text.</summary>
        [ObservableProperty]
        private string _editingName = string.Empty;

        /// <summary>The managed <see cref="Window"/> instance.</summary>
        public Window Window { get; }

        // ── Virtual properties (overridden by MatrixPlotterListItemViewModel) ────────

        /// <summary>True when data is fully in-memory (no backing file). Always <c>false</c> for generic windows.</summary>
        public virtual bool IsInMemory => false;

        /// <summary>True when data is virtual and the backing file is read-only. Always <c>false</c> for generic windows.</summary>
        public virtual bool IsVirtualReadOnly => false;

        /// <summary>True when data is virtual and the backing file is writable. Always <c>false</c> for generic windows.</summary>
        public virtual bool IsVirtualWritable => false;

        /// <summary>True when the window has unsaved changes. Always <c>false</c> for generic windows.</summary>
        public virtual bool HasUnsavedChanges => false;

        /// <summary>Raises <see cref="System.ComponentModel.INotifyPropertyChanged"/> for <see cref="HasUnsavedChanges"/>.</summary>
        protected void NotifyUnsavedChangesChanged() => OnPropertyChanged(nameof(HasUnsavedChanges));

        /// <summary>Short dimension string for icon-view (e.g. "2048×2048×…"). Empty for generic windows.</summary>
        public virtual string DimensionsText => string.Empty;

        /// <summary>Full axis-dimension string for tooltip. Empty for generic windows.</summary>
        public virtual string DimensionLine => string.Empty;

        // ── Constructor ───────────────────────────────────────────────────────────

        /// <param name="window">The managed window.</param>
        /// <param name="fallbackBitmapUri">
        /// Optional URI for a PNG asset used as the fallback thumbnail icon (e.g. ProfilePlotter).
        /// When <c>null</c>, the generic window-frame <see cref="PathIcon"/> geometry is used instead.
        /// </param>
        public WindowListItemViewModel(Window window, Uri? fallbackBitmapUri = null)
        {
            Window = window;
            FileName = window.Title ?? "(untitled)";

            if (fallbackBitmapUri != null)
            {
                using var stream = AssetLoader.Open(fallbackBitmapUri);
                FallbackBitmap = new Bitmap(stream);
            }
            else
            {
                FallbackIconData = Geometry.Parse(GenericWindowIconPath);
            }

            RefreshMetaData();

            // Track title changes to keep FileName in sync.
            EventHandler<Avalonia.AvaloniaPropertyChangedEventArgs> onTitleChanged =
                (_, e) => { if (e.Property == Window.TitleProperty) FileName = window.Title ?? "(untitled)"; };
            window.PropertyChanged += onTitleChanged;
            window.Closed += (_, _) => window.PropertyChanged -= onTitleChanged;

            // Intercept minimize: hide the window instead (no taskbar entry to restore from).
            EventHandler<Avalonia.AvaloniaPropertyChangedEventArgs> onWindowStateChanged =
                (_, e) =>
                {
                    if (e.Property == Window.WindowStateProperty
                        && window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                        window.Hide();
                        IsWindowVisible = false;
                    }
                };
            window.PropertyChanged += onWindowStateChanged;
            window.Closed += (_, _) => window.PropertyChanged -= onWindowStateChanged;
        }

        /// <summary>Recomputes <see cref="MetaData"/>. Override in subclasses for type-specific text.</summary>
        protected virtual void RefreshMetaData()
        {
            MetaData = Window.GetType().Name;
        }

        /// <summary>Toggles the window between visible and hidden.</summary>
        public void ToggleVisibility()
        {
            if (IsWindowVisible)
            {
                Window.Hide();
                IsWindowVisible = false;
            }
            else
            {
                Window.Show();
                Window.Activate();
                IsWindowVisible = true;
            }
        }

        // ── Rename commands ───────────────────────────────────────────────────────

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void Rename()
        {
            EditingName = FileName;
            IsRenaming = true;
        }

        /// <summary>Commits the current edit: updates the window title and exits rename mode.</summary>
        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void CommitRename()
        {
            var name = EditingName.Trim();
            if (!string.IsNullOrEmpty(name))
                Window.Title = name; // onTitleChanged handler keeps FileName in sync
            IsRenaming = false;
        }

        /// <summary>Discards the current edit and exits rename mode.</summary>
        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void CancelRename() => IsRenaming = false;

        /// <summary>
        /// Fired when Tab or Shift+Tab is pressed during an inline rename.
        /// <c>true</c> = forward (Tab), <c>false</c> = backward (Shift+Tab).
        /// Subscribers should start renaming the adjacent item.
        /// </summary>
        internal event EventHandler<bool>? RenameNavigationRequested;

        /// <summary>Commits the current name and signals the parent to move rename focus.</summary>
        internal void RequestRenameNavigation(bool forward)
        {
            CommitRename();
            RenameNavigationRequested?.Invoke(this, forward);
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void Duplicate()
        {
            // TODO: open a linked duplicate window
        }

        [CommunityToolkit.Mvvm.Input.RelayCommand]
        private void CloseWindow() => Window.Close();

        // ── Parent-child relationship ─────────────────────────────────────────────

        private WindowListItemViewModel? _parentItem;

        /// <summary>
        /// The parent <see cref="WindowListItemViewModel"/>, or <c>null</c> for root-level windows.
        /// Setting this raises <see cref="System.ComponentModel.INotifyPropertyChanged"/> for <see cref="Depth"/>.
        /// </summary>
        public WindowListItemViewModel? ParentItem
        {
            get => _parentItem;
            internal set
            {
                _parentItem = value;
                OnPropertyChanged(nameof(Depth));
                OnPropertyChanged(nameof(IsChild));
                OnPropertyChanged(nameof(ToolTipTitle));
            }
        }

        /// <summary>
        /// <c>true</c> when this window is a linked child of another window.
        /// Used to show the 🔗 indicator in the list view.
        /// </summary>
        public bool IsChild => _parentItem is not null;

        /// <summary>
        /// Title string for the hover tooltip. Prefixed with "🔗[Linked] " for child windows.
        /// </summary>
        public string ToolTipTitle => IsChild ? $"🔗[Linked] {FileName}" : FileName;

        /// <summary>Direct children of this window in the managed-window hierarchy.</summary>
        internal List<WindowListItemViewModel> ChildItems { get; } = [];

        // ── IPlotTreeNode (leaf-node implementation) ──────────────────────────────

        /// <inheritdoc/>
        string IPlotTreeNode.DisplayName => FileName;

        /// <summary>
        /// Nesting depth computed from the <see cref="ParentItem"/> chain.
        /// Root-level windows return <c>0</c>; direct children return <c>1</c>, and so on.
        /// </summary>
        public int Depth => ParentItem is null ? 0 : ParentItem.Depth + 1;

        /// <inheritdoc/>
        bool IPlotTreeNode.CanHaveChildren => false;

        /// <inheritdoc/>
        bool IPlotTreeNode.IsExpanded { get => false; set { } }

        /// <inheritdoc/>
        IReadOnlyList<IPlotTreeNode>? IPlotTreeNode.Children => null;

        /// <inheritdoc/>
        Task<IReadOnlyList<IPlotTreeNode>> IPlotTreeNode.LoadChildrenAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IPlotTreeNode>>(Array.Empty<IPlotTreeNode>());

        /// <inheritdoc/>
        Window? IPlotTreeNode.AssociatedWindow => Window;
    }
}
