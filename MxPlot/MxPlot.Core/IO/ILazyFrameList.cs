namespace MxPlot.Core.IO
{
    /// <summary>
    /// Marker interface for frame lists that supply data lazily or on-demand,
    /// rather than holding all frames in memory at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Current implementors:</strong>
    /// <list type="bullet">
    ///   <item><see cref="VirtualFrames{T}"/> — local memory-mapped file (MMF) backed lazy access.</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Planned implementors (reserved for future extensions):</strong>
    /// <list type="bullet">
    ///   <item>
    ///     <c>RemoteFrames&lt;T&gt;</c> (MxPlot.Extensions.Zarr or MxPlot.Core.Remote) —
    ///     HTTP/S3/cloud-backed chunk loading for OME-Zarr and similar formats.
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Purpose — what this marker is used for:</strong><br/>
    /// This interface is the common base for any frame list that holds external resources
    /// and loads frame data on demand. It is used to generalize two cross-cutting concerns:
    /// <list type="bullet">
    ///   <item>
    ///     <term><c>RequiresDisposal</c></term>
    ///     <description>
    ///       Both MMF-backed (<see cref="VirtualFrames{T}"/>) and future remote-backed lists
    ///       hold external resources (file handles, HTTP connections, etc.) that must be
    ///       explicitly released. Checking <c>_arrayList is ILazyFrameList</c> provides a
    ///       single, forward-compatible predicate for this.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term><c>CacheStrategy</c></term>
    ///     <description>
    ///       Both local and remote lazy lists benefit from a pluggable prefetch/eviction
    ///       strategy. This interface serves as a discriminator for enabling cache-related
    ///       operations in <c>MatrixData&lt;T&gt;</c>.
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>What this interface is NOT used for:</strong><br/>
    /// <see cref="IMatrixData.IsVirtual"/> remains tied to <see cref="IVirtualFrameList"/>
    /// (MMF only). Remote data is detected via <c>is IRemoteMatrixData</c> at the UI layer.
    /// Merging both under a single <c>IsVirtual</c> flag would cause incorrect "Virtual"
    /// badges to appear for remote data and conflates two distinct concepts
    /// (OS-level virtual memory vs. network-lazy loading).
    /// </para>
    /// <para>
    /// See the design memo at
    /// <c>Tests.Documents/Working/MxPlot.Remote/RemoteFrames_Design.md</c> §12
    /// for the full rationale.
    /// </para>
    /// </remarks>
    public interface ILazyFrameList
    {
    }
}
