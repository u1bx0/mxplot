using Avalonia.Controls;
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.App.ViewModels
{
    /// <summary>
    /// Represents a node in the managed plot window list / flat-tree hierarchy.
    /// </summary>
    /// <remarks>
    /// <b>Current implementations:</b><br/>
    /// <see cref="WindowListItemViewModel"/> — base class for window list items; may act as a parent
    /// with linked child windows accessible via <c>ChildItems</c>.<br/>
    /// <see cref="MatrixPlotterListItemViewModel"/> — concrete leaf representing one open <see cref="Window"/>.
    /// <para/>
    /// <b>Planned implementations:</b>
    /// <list type="bullet">
    ///   <item>
    ///     <term>FolderNode</term>
    ///     <description>A directory entry whose children are opened plot windows.</description>
    ///   </item>
    ///   <item>
    ///     <term>Hdf5FileNode</term>
    ///     <description>
    ///       An HDF5 file whose internal datasets / groups appear as child nodes.
    ///       Children are loaded lazily via <see cref="LoadChildrenAsync"/>.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para/>
    /// <b>Flat-tree model:</b><br/>
    /// <c>ManagedWindows</c> in <c>MxPlotAppViewModel</c> holds only the
    /// <em>currently visible</em> nodes as a flat <c>ObservableCollection</c>.
    /// Container nodes insert / remove their children in-place when expanded or collapsed.
    /// </remarks>
    public interface IPlotTreeNode
    {
        /// <summary>Short display name shown in the list row.</summary>
        string DisplayName { get; }

        /// <summary>Preview thumbnail, or <c>null</c> while not yet generated.</summary>
        Bitmap? Thumbnail { get; }

        /// <summary>
        /// Nesting depth in the hierarchy. Root-level nodes are <c>0</c>;
        /// children of a root are <c>1</c>, and so on.
        /// Used by the UI to calculate left-indent and draw hierarchy guide lines.
        /// </summary>
        int Depth { get; }

        /// <summary>
        /// <c>true</c> if this node can contain children (folder, HDF5 file).
        /// <c>false</c> if this is a leaf node (single plot window, dataset).
        /// </summary>
        bool CanHaveChildren { get; }

        /// <summary>
        /// Whether the node is currently expanded in the flat list.
        /// Toggling this causes the owning <c>MxPlotAppViewModel</c>
        /// to insert or remove child nodes from <c>ManagedWindows</c>.
        /// Leaf nodes always return <c>false</c>; the setter is a no-op.
        /// </summary>
        bool IsExpanded { get; set; }

        /// <summary>
        /// Eagerly-available children, or <c>null</c> when the node is a leaf or
        /// children have not been loaded yet.
        /// Use <see cref="LoadChildrenAsync"/> when enumeration is expensive.
        /// </summary>
        IReadOnlyList<IPlotTreeNode>? Children { get; }

        /// <summary>
        /// Loads and returns the children of this node.
        /// Returns an empty list for leaf nodes; implementations may cache the result.
        /// </summary>
        Task<IReadOnlyList<IPlotTreeNode>> LoadChildrenAsync(CancellationToken ct = default);

        /// <summary>
        /// The Avalonia <see cref="Window"/> directly managed by this node,
        /// or <c>null</c> for container nodes that hold children without
        /// an associated window themselves.
        /// </summary>
        Window? AssociatedWindow { get; }
    }
}

