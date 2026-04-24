using System;
using System.Collections.Generic;
using System.Text;

namespace MxPlot.Core.IO
{
    public interface IWritableFrameProvider<T>
    {
        /// <summary>
        /// Returns a writable array for the specified frame index. 
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        T[] GetWritableArray(int index);

        /// <summary>
        /// Writes <paramref name="data"/> directly to the backing store at <paramref name="frameIndex"/>,
        /// bypassing the read cache entirely.
        /// Any cached copy of the frame is evicted to maintain coherency.
        /// Call <see cref="Flush"/> afterwards to commit the MMF pages to disk.
        /// </summary>
        /// <remarks>
        /// <b>Not thread-safe for concurrent access to the same frame index.</b>
        /// Two threads writing to the same frameIndex simultaneously will produce undefined results.
        /// </remarks>
        /// <exception cref="ArgumentException">Thrown when <paramref name="data"/>.Length ≠ Width × Height.</exception>
        void WriteDirectly(int frameIndex, T[] data);

        /// <summary>
        /// Writes all dirty (modified) frames back to disk immediately.
        /// </summary>
        void Flush();

        void SaveAs(string path, Action<string>? beforeRemount = null, IProgress<int>? progress = null);
    }
}
