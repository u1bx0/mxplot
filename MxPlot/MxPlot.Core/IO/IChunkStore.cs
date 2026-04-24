using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Abstraction for a key-addressable store that returns raw (possibly compressed)
    /// chunk bytes on demand. Designed as the I/O back-end for
    /// <c>RemoteFrames&lt;T&gt;</c> and future chunk-based format readers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Key format convention (Zarr):</strong><br/>
    /// For OME-Zarr v2, a chunk key is a slash-delimited coordinate string such as
    /// <c>"0/0/5/0/0"</c> representing <c>t/c/z/y_chunk/x_chunk</c>.
    /// Each <see cref="IChunkStore"/> implementation translates this key to
    /// its native addressing scheme (filesystem path, S3 object key, Azure blob name, etc.).
    /// </para>
    /// <para>
    /// <strong>Planned implementations (all in MxPlot.Extensions.Zarr or higher layers):</strong>
    /// <list type="bullet">
    ///   <item><c>LocalChunkStore</c>  — <c>File.ReadAllBytes</c>, used for offline tests.</item>
    ///   <item><c>HttpChunkStore</c>   — <c>HttpClient</c> with HTTP Range Request support.</item>
    ///   <item><c>S3ChunkStore</c>     — AWS SDK / presigned URL.</item>
    ///   <item><c>AzureChunkStore</c>  — Azure.Storage.Blobs.</item>
    /// </list>
    /// The interface lives in <c>MxPlot.Core.IO</c> so that
    /// <c>RemoteFrames&lt;T&gt;</c> (also in Core) can depend on it without
    /// pulling in any cloud SDK references.
    /// </para>
    /// <para>
    /// See <c>Tests.Documents/Working/MxPlot.Remote/RemoteFrames_Design.md</c> §6
    /// for the full design and URI routing convention.
    /// </para>
    /// </remarks>
    public interface IChunkStore
    {
        /// <summary>
        /// Asynchronously retrieves the raw bytes for the chunk identified by
        /// <paramref name="key"/>. The bytes may be compressed; decompression is
        /// the caller's responsibility.
        /// </summary>
        /// <param name="key">
        /// Store-relative chunk identifier (e.g., <c>"0/0/5/0/0"</c> for Zarr).
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Raw (possibly compressed) chunk bytes.</returns>
        Task<byte[]> GetChunkAsync(string key, CancellationToken ct = default);

        /// <summary>
        /// Returns <see langword="true"/> if the chunk identified by
        /// <paramref name="key"/> exists in this store.
        /// </summary>
        bool Exists(string key);
    }
}
