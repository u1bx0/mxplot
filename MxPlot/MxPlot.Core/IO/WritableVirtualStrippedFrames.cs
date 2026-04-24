using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Represents a writable collection of virtual stripped frames, allowing for modification and efficient write-back
    /// of changes to disk. It also manages the lifecycle of temporary backing files.
    /// </summary>
    /// <remarks>
    /// This class tracks modified (dirty) frames and ensures that changes are written back to disk
    /// either on demand or during disposal. It is designed for scenarios where direct, in-place editing of large
    /// frame-based datasets is required, such as memory-mapped image or data processing. 
    /// If instantiated as a temporary store, it will automatically delete the backing physical file upon disposal 
    /// unless <see cref="Retain"/> is called. Frames marked as dirty are protected from eviction until they 
    /// are flushed to disk, ensuring data integrity. Thread safety is maintained for cache and dirty frame management.
    /// </remarks>
    /// <typeparam name="T">The type of the elements in the frames. Must be an unmanaged type.</typeparam>
    public class WritableVirtualStrippedFrames<T> : VirtualStrippedFrames<T>, IWritableFrameProvider<T> where T : unmanaged
    {
        // Manages indices of dirty (modified) frames
        private readonly HashSet<int> _dirtyIndices = new HashSet<int>();

        /// <summary>
        /// Tracks whether any writes (WriteDirectly or WriteBackToDisk via dirty eviction)
        /// have occurred since the last <see cref="Flush"/>.
        /// When false, <c>_accessor.Flush()</c> (FlushViewOfFile) is skipped to avoid
        /// scanning the entire MMF view for dirty pages — significant for multi-GB files.
        /// </summary>
        private volatile bool _hasPendingWrites;

        public WritableVirtualStrippedFrames(string filePath,
            int w, int h, long[][] offsets, long[][] bytesCounts, bool isYFlipped, bool isTemporary)
            : base(filePath, w, h, offsets, bytesCounts, isYFlipped, MemoryMappedFileAccess.ReadWrite)
        {
            IsTemporary = isTemporary;
        }

        /// <summary>
        /// Returns false because this is a writable list.
        /// </summary>
        public override bool IsReadOnly => false;

        /// <summary>
        /// Gets a value indicating whether the backing memory-mapped file is temporary.
        /// If <c>true</c>, the underlying physical file will be automatically deleted when this instance is disposed.
        /// </summary>
        /// <remarks>
        /// This flag is typically set to <c>true</c> when the framework automatically generates a backing file 
        /// in the system's temporary folder. To prevent deletion, call the <see cref="Retain"/> method.
        /// </remarks>
        public bool IsTemporary { get; private set; } = true;

        /// <summary>
        /// Prevents the backing temporary file from being deleted on disposal.
        /// After calling this method, <see cref="IsTemporary"/> becomes <c>false</c>
        /// and the file will persist until explicitly deleted or overwritten via <see cref="SaveAs"/>.
        /// </summary>
        public void Retain() => IsTemporary = false;

        /// <summary>
        /// Prevents eviction of dirty frames until <see cref="Flush"/> is called.
        /// Called while holding <c>_cacheLock</c>.
        /// </summary>
        protected override bool CanEvict(int frameIndex)
        {
            return !_dirtyIndices.Contains(frameIndex);
        }

        /// <summary>
        /// Called from <c>UpdateCache</c> outside of <c>_cacheLock</c>.
        /// Access to <c>_dirtyIndices</c> is protected by <c>_cacheLock</c>.
        /// </summary>
        /// <remarks>
        /// Normally not invoked because <c>CanEvict</c> protects dirty frames.
        /// Kept as a safety net for when the cache is entirely filled with dirty frames.
        /// </remarks>
        protected override void OnFrameEvicted(int frameIndex, T[] frameData)
        {
            bool isDirty;
            lock (_cacheLock) // OnFrameEvicted is called outside _cacheLock, so we acquire it here
            {
                isDirty = _dirtyIndices.Remove(frameIndex);
            }
            if (isDirty)
            {
                WriteBackToDisk(frameIndex, frameData);
                _hasPendingWrites = true;
                // Per-eviction flush is skipped for cost reasons — handled collectively by Flush()
            }
        }

        /// <summary>
        /// Writes all dirty frames back to disk.
        /// </summary>
        public void Flush()
        {
            if (IsDisposed) return;

            // Snapshot the dirty indices inside the lock and clear them atomically.
            // Any new dirty frames added via GetWritableArray after the clear will be handled in the next Flush.
            int[] dirtySnapshot;
            lock (_cacheLock)
            {
                dirtySnapshot = [.. _dirtyIndices];
                _dirtyIndices.Clear();
            }

            // Perform I/O outside the lock to avoid holding _cacheLock for an extended period.
            foreach (int index in dirtySnapshot)
            {
                T[]? data;
                lock (_cacheLock) { _cache.TryGetValue(index, out data); }

                if (data != null)
                    WriteBackToDisk(index, data);
            }

            // Commit write-backs to the OS only when actual writes have occurred.
            // Skipping FlushViewOfFile when _hasPendingWrites is false avoids
            // scanning the entire MMF view (significant for multi-GB files).
            bool needsOsFlush = _hasPendingWrites || dirtySnapshot.Length > 0;
            if (needsOsFlush)
            {
                _accessor.Flush();
                _hasPendingWrites = false;
            }

            Debug.WriteLine($"[WritableVirtualStrippedFrams.Flush] Called Flush(). dirtySnapshot count={dirtySnapshot.Length}, osFlush={needsOsFlush}");
        }

        /// <summary>
        /// Writes <paramref name="data"/> directly to the MMF at <paramref name="frameIndex"/>,
        /// bypassing the read cache entirely.
        /// Any cached copy of the frame is evicted before writing to maintain coherency.
        /// Call <see cref="Flush"/> afterwards to commit MMF pages to disk.
        /// </summary>
        /// <remarks>
        /// <b>Not thread-safe for concurrent access to the same frame index.</b>
        /// Two threads writing to the same frameIndex simultaneously produce undefined results.
        /// This method does NOT call <c>_accessor.Flush()</c> internally;
        /// invoke <see cref="Flush"/> (or rely on <see cref="Dispose"/>) to persist.
        /// </remarks>
        /// <exception cref="ArgumentException">data.Length ≠ Width × Height.</exception>
        public void WriteDirectly(int frameIndex, T[] data)
        {
            if (data.Length != _width * _height)
                throw new ArgumentException(
                    $"Array length mismatch: expected {_width * _height} ({_width}×{_height}), got {data.Length}.",
                    nameof(data));

            // Evict cached copy (if any) so subsequent GetArray reads fresh MMF data.
            lock (_cacheLock)
            {
                _cache.Remove(frameIndex);
                _dirtyIndices.Remove(frameIndex);
            }

            WriteBackToDisk(frameIndex, data);
            _hasPendingWrites = true;
            // _accessor.Flush() is intentionally deferred — caller drives commit timing.
        }

        /// <summary>
        /// Retrieves the array for the specified frame for writing.
        /// Atomically marks the frame as dirty and ensures it is stored in the cache.
        /// </summary>
        public T[] GetWritableArray(int index)
        {
            // Atomically guarantees that the frame is cached and registered as dirty.
            // Retry in the extremely rare case where eviction occurs right after this[index] and before the lock is acquired.
            // Once CanEvict(index) returns false, eviction is prevented, so this normally completes in one iteration.
            while (true)
            {
                var data = this[index]; // I/O outside the lock (on cache miss)

                lock (_cacheLock)
                {
                    if (!_cache.ContainsKey(index))
                        continue; // Extremely rare race: eviction occurred right after this[index] — retry

                    // Register as dirty only after confirming the frame is present in the cache.
                    // From this point, CanEvict(index) returns false and the frame is protected from eviction.
                    _dirtyIndices.Add(index);
                    return data;
                }
            }
        }

        /// <summary>
        /// Saves the virtual frames to the specified path by moving or copying the underlying physical file.
        /// Once completed, this instance binds to the new file and the temporary flag is cleared.
        /// </summary>
        /// <param name="newPath">The destination file path.</param>
        /// <param name="beforeRemount">
        /// An optional callback invoked after the file has been moved/copied to <paramref name="newPath"/>
        /// but before the MMF is re-mounted. The callback receives the resolved destination file path.
        /// This allows format-specific finalization (e.g., appending a metadata trailer) while the file
        /// is exclusively accessible via normal file I/O — not locked by the MMF.
        /// </param>
        /// <param name="progress">An optional progress reporter (primarily for cross-volume copy operations).</param>
        /// <exception cref="ObjectDisposedException">Thrown if the instance is already disposed.</exception>
        public void SaveAs(string newPath, Action<string>? beforeRemount = null, IProgress<int>? progress = null)
        {
            if (IsDisposed) 
                throw new ObjectDisposedException(nameof(WritableVirtualStrippedFrames<T>));

            lock (_cacheLock)
            {
                progress?.Report(-this.Count); // Report start of SaveAs operation

                string currentPath = Path.GetFullPath(this.FilePath);
                string targetPath = Path.GetFullPath(newPath);

                // 1. If the path is exactly the same, flush and optionally run the hook for in-place finalization.
                if (string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    IsTemporary = false;
                    if (beforeRemount != null)
                    {
                        Unmount();
                        try
                        {
                            beforeRemount(targetPath);
                        }
                        finally
                        {
                            Mount(MemoryMappedFileAccess.ReadWrite);
                        }
                    }
                    progress?.Report(this.Count);
                    return;
                }

                // 2. Ensure all dirty frames are written to the current physical file.
                Flush();

                // 3. Release OS-level file locks (MemoryMappedFile and FileStream) so we can move/copy it.
                Unmount();

                try
                {
                    bool wasTemporary = IsTemporary;
                    string srcRoot = Path.GetPathRoot(currentPath) ?? string.Empty;
                    string dstRoot = Path.GetPathRoot(targetPath) ?? string.Empty;

                    // 4. Physical file operation
                    if (string.Equals(srcRoot, dstRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        // Fast path: Same volume, OS-level move (O(1) time)
                        File.Move(currentPath, targetPath, overwrite: true);
                        this.FilePath = targetPath;
                        this.IsTemporary = false;
                    }
                    else
                    {
                        // Slow path: Cross-volume, physical bit-by-bit copy
                        File.Copy(currentPath, targetPath, overwrite: true);
                        this.FilePath = targetPath;
                        this.IsTemporary = false;
                        // Delete old temp file after copying to ensure we don't lose data if the copy fails
                        if (wasTemporary)
                        {
                            try { File.Delete(currentPath); }
                            catch { }
                        }
                    }

                    // 5. Format-specific finalization (e.g., append .mxd trailer) while file is not MMF-locked.
                    beforeRemount?.Invoke(targetPath);
                }
                finally
                {
                    // 6. Re-acquire OS locks on the new path and resume normal operations.
                    Mount(MemoryMappedFileAccess.ReadWrite);
                    progress?.Report(this.Count);
                }
            }

        }

        /// <summary>
        /// Writes the specified data to disk at the given frame index using a memory-mapped file, ensuring that the
        /// frame's pixel data is updated accordingly.
        /// </summary>
        /// <remarks>This method performs direct memory operations for performance and may use unsafe
        /// code. It handles both Y-flipped and non-flipped data layouts based on the internal state. Callers should
        /// ensure that the data array matches the expected frame size and format to avoid data corruption.</remarks>
        /// <param name="frameIndex">The zero-based index of the frame to which the data will be written. Must correspond to a valid frame within
        /// the internal storage.</param>
        /// <param name="data">An array containing the pixel data of type T to be written to the specified frame. The length and structure
        /// of the array must match the expected frame dimensions.</param>
        /// <exception cref="IOException">Thrown when a critical failure occurs during the write-back operation, such as an error accessing the
        /// memory-mapped file or writing data to disk.</exception>
        private unsafe void WriteBackToDisk(int frameIndex, T[] data)
        {
            try
            {
                long[] stripOffsets = _offsets[frameIndex];
                long[] stripByteCounts = _byteCounts[frameIndex];
                int sizeOfT = Unsafe.SizeOf<T>();

                var handle = _accessor.SafeMemoryMappedViewHandle;
                byte* pBase = null;

                try
                {
                    handle.AcquirePointer(ref pBase);
                    int currentFrameRow = 0;

                    fixed (T* pSrcBase = data)
                    {
                        for (int i = 0; i < stripOffsets.Length; i++)
                        {
                            long offset = stripOffsets[i];
                            long byteCount = stripByteCounts[i];
                            if (offset == 0 || byteCount == 0) continue;

                            int elementCount = (int)(byteCount / sizeOfT);
                            int rowsInStrip = elementCount / _width;
                            long rowPitch = (long)_width * sizeOfT;

                            if (_isYFlipped)
                            {
                                // --- Write each row in reversed order directly to MMF ---
                                for (int y = 0; y < rowsInStrip; y++)
                                {
                                    // Write destination in MMF: top-down TIFF order (currentFrameRow + y)
                                    void* pDestRow = pBase + offset + (y * rowPitch);

                                    // Source in memory: row counted from the bottom (MxPlot convention)
                                    int sourceY = (_height - 1) - (currentFrameRow + y);
                                    T* pSrcRow = pSrcBase + (sourceY * _width);

                                    Buffer.MemoryCopy(pSrcRow, pDestRow, rowPitch, rowPitch);
                                }
                            }
                            else
                            {
                                // No flip needed — bulk copy the entire strip at once
                                void* pDestStrip = pBase + offset;
                                T* pSrcStrip = pSrcBase + (currentFrameRow * _width);
                                Buffer.MemoryCopy(pSrcStrip, pDestStrip, byteCount, byteCount);
                            }

                            currentFrameRow += rowsInStrip;
                        }
                    }
                }
                finally
                {
                    if (pBase != null) handle.ReleasePointer();
                }
            }
            catch (Exception ex)
            {
                throw new IOException(
                    $"Critical failure during MMF write-back (unsafe). FrameIndex: {frameIndex}, " +
                    $"OffsetCount: {_offsets[frameIndex].Length}. Internal Message: {ex.Message}", ex);
            }
        }

        public override void Dispose()
        {
            if (IsDisposed) return;

            try
            {
                Flush(); // Always write back pending changes to the OS cache before disposal
            }
            catch (Exception ex)
            {
                // Ensure that resources are still released even if the final flush fails
                Debug.WriteLine($"[WritableVirtualFrameList] Warning: Flush failed during Dispose: {ex.Message}");
            }
            finally
            {
                // 1. Always call base.Dispose() first to ensure the MemoryMappedFile and FileStream 
                // release their OS-level locks on the physical file.
                base.Dispose();

                // 2. Once the locks are released, perform cleanup for temporary files.
                if (IsTemporary && !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
                {
                    try
                    {
                        File.Delete(FilePath);
                        Debug.WriteLine($"[WritableVirtualFrameList] Successfully deleted temporary file: {FilePath}");
                    }
                    catch (Exception ex)
                    {
                        // Swallow the exception to prevent the application from crashing during disposal,
                        // but log the failure for debugging purposes.
                        Debug.WriteLine($"[WritableVirtualFrameList] Error: Failed to delete temporary file '{FilePath}': {ex.Message}");
                    }
                }
            }
        }
    }

}
