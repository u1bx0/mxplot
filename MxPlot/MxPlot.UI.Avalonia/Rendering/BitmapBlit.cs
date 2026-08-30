using Avalonia.Platform;
using System;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Copies a finished frame from a writer's scratch buffer into a locked bitmap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bitmap writers deliberately render into their own memory and blit afterwards rather
    /// than writing straight into the framebuffer. Their render loops use <see cref="System.Threading.Tasks.Parallel"/>,
    /// whose completion wait pumps Win32 messages on the UI thread; a pending WM_PAINT then
    /// re-enters, asks the compositor to commit, and the compositor's render thread blocks trying
    /// to take the very bitmap lock the render is holding — a deadlock. Confining the lock to this
    /// copy, which never blocks, removes that possibility entirely.
    /// </para>
    /// <para>
    /// Nothing here may allocate or wait, for the same reason.
    /// </para>
    /// </remarks>
    internal static class BitmapBlit
    {
        /// <summary>
        /// Copies <paramref name="height"/> rows of <paramref name="width"/> BGRA pixels from a
        /// tightly packed <paramref name="source"/> into <paramref name="target"/>.
        /// </summary>
        internal static unsafe void Rows(int* source, int width, int height, ILockedFramebuffer target)
        {
            long rowBytes = (long)width * 4;
            byte* dst = (byte*)target.Address;

            // No row padding: the whole frame is one contiguous run.
            if (target.RowBytes == rowBytes)
            {
                long total = rowBytes * height;
                Buffer.MemoryCopy(source, dst, total, total);
                return;
            }

            byte* src = (byte*)source;
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(src + y * rowBytes, dst + (long)y * target.RowBytes, rowBytes, rowBytes);
        }
    }
}
