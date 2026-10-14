using MxPlot.Core.IO.Formats;

namespace MxPlot.App.Services
{
    /// <summary>
    /// Decides how the app opens a file: InMemory, Virtual, or by asking the user. The threshold is
    /// process-wide and meant to be set once at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Below <see cref="LargeFileThresholdBytes"/> every file loads InMemory. The TIFF readers decode
    /// compressed frames on all cores, so a file of that size is ready in about a second, and plain
    /// in-memory data has no intermediate states.
    /// </para>
    /// <para>
    /// From the threshold up, the choice depends on what a Virtual load of the file would use:
    /// </para>
    /// <list type="bullet">
    ///   <item><see cref="VirtualBackendKind.Decoded"/> (e.g. compressed TIFF) opens Virtual without
    ///   asking. It opens at once and fills in the background; once it has every frame it holds them
    ///   for good and releases the file, which is then equivalent to an in-memory load. A file too
    ///   large for the memory budget stays on demand instead, which is where InMemory would have been
    ///   a risk.</item>
    ///   <item><see cref="VirtualBackendKind.Mapped"/> (memory-mapped) never becomes resident, so
    ///   InMemory and Virtual are a real trade-off and the user is asked. So is a file whose reader
    ///   does not report its backend.</item>
    /// </list>
    /// </remarks>
    internal static class LoadingModePolicy
    {
        /// <summary>
        /// File size from which a file is treated as large: a Decoded file then opens Virtual, and any
        /// other file asks the user. Smaller files load InMemory.
        /// </summary>
        public static long LargeFileThresholdBytes { get; set; } = 500L * 1024 * 1024;

        /// <summary>
        /// Returns the loading mode to use, or <see langword="null"/> when the user should be asked.
        /// </summary>
        /// <param name="probe">What the reader reports about the file.</param>
        /// <param name="fileBytes">File size in bytes.</param>
        /// <param name="forcePrompt">Ask regardless of the file (Shift held on drop).</param>
        public static LoadingMode? Decide(VirtualLoadProbe probe, long fileBytes, bool forcePrompt)
        {
            if (probe.Backend == VirtualBackendKind.None)
                return LoadingMode.InMemory;
            if (forcePrompt)
                return null;
            if (fileBytes < LargeFileThresholdBytes)
                return LoadingMode.InMemory;

            return probe.Backend == VirtualBackendKind.Decoded ? LoadingMode.Virtual : null;
        }
    }
}
