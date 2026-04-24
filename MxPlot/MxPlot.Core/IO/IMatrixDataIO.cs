using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Describes a file format that can read or write matrix data.
    /// Provides the metadata required for file-dialog integration and plugin discovery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations should declare a human-readable <see cref="FormatName"/> and one or more
    /// <see cref="Extensions"/> (including the leading dot, e.g. <c>".ome.tiff"</c>).
    /// Helper properties <see cref="DialogFilter"/> and <see cref="DialogPatterns"/> generate
    /// platform-specific filter strings automatically.
    /// </para>
    /// <para>
    /// At application startup, UI layers can scan loaded assemblies for types that implement
    /// <see cref="IFileFormatDescriptor"/> (or its derived interfaces <see cref="IMatrixDataReader"/>
    /// / <see cref="IMatrixDataWriter"/>) to build file-dialog filters dynamically.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // WinForms
    /// string filter = string.Join("|", formats.Select(f => f.DialogFilter));
    ///
    /// // Avalonia
    /// var types = formats.Select(f =>
    ///     new FilePickerFileType(f.FormatName) { Patterns = f.DialogPatterns });
    /// </code>
    /// </example>
    public interface IFileFormatDescriptor
    {
        /// <summary>
        /// Gets the human-readable name of the format (e.g. <c>"OME-TIFF"</c>, <c>"MxPlot Binary"</c>).
        /// </summary>
        string FormatName { get; }

        /// <summary>
        /// Gets the file extensions associated with this format, including the leading dot
        /// (e.g. <c>[".ome.tiff", ".ome.tif"]</c>).
        /// </summary>
        IReadOnlyList<string> Extensions { get; }

        /// <summary>
        /// Gets a WinForms/WPF-style dialog filter string.
        /// Example: <c>"OME-TIFF (*.ome.tiff;*.ome.tif)|*.ome.tiff;*.ome.tif"</c>
        /// </summary>
        string DialogFilter
        {
            get
            {
                var patterns = string.Join(";", Extensions.Select(e => $"*{e}"));
                return $"{FormatName} ({patterns})|{patterns}";
            }
        }

        /// <summary>
        /// Gets glob patterns for Avalonia <c>FilePickerFileType</c>.
        /// Example: <c>["*.ome.tiff", "*.ome.tif"]</c>
        /// </summary>
        IReadOnlyList<string> DialogPatterns
            => Extensions.Select(e => $"*{e}").ToArray();
    }

    /// <summary>
    /// Indicates that a file format handler supports progress reporting during I/O operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not all formats require progress reporting (e.g., lightweight formats like CSV or bitmap).
    /// Implementations that handle potentially large files (OME-TIFF, .mxd, FITS, etc.) should implement
    /// this interface so that UI layers can attach a progress reporter generically:
    /// </para>
    /// <code>
    /// var reader = FormatRegistry.CreateReader(filePath);
    /// if (reader is IProgressReportable p) p.ProgressReporter = myProgress;
    /// var data = reader.Read(filePath);
    /// </code>
    /// <para>
    /// <b>Progress reporting convention (frame-based I/O):</b><br/>
    /// This interface intentionally omits a separate <c>TotalFrames</c> property.
    /// Instead, the total is communicated through the reporter itself using a sign convention:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <term>Initial signal</term>
    ///     <description>
    ///       Call <c>Report(-totalFrames)</c> once before the loop begins.
    ///       The negative value tells the UI the total frame count.
    ///       Example: 200 frames → <c>Report(-200)</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Per-frame progress</term>
    ///     <description>
    ///       After completing frame <c>i</c> (0-based), call <c>Report(i)</c>.
    ///       The UI displays <c>i + 1</c> / <c>totalFrames</c>.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Completion</term>
    ///     <description>
    ///       Optionally call <c>Report(totalFrames)</c> at the end.
    ///       The MxPlot UI ignores this value; the <c>finally</c> block clears the progress bar.
    ///     </description>
    ///   </item>
    /// </list>
    /// <code>
    /// // Correct implementation pattern
    /// progress?.Report(-frameCount);       // ① signal total (negative)
    /// for (int i = 0; i &lt; frameCount; i++)
    /// {
    ///     // ... load frame i ...
    ///     progress?.Report(i);             // ② 0-based index after each frame
    /// }
    /// // ③ optional: progress?.Report(frameCount);
    /// </code>
    /// <para>
    /// <b>Rationale for the sign convention:</b>
    /// Adding a <c>TotalFrames</c> property to the interface would require format handlers to
    /// pre-scan the file before reading, which is expensive for some formats. By encoding the
    /// total as a negative first report, the handler can emit it at the moment it becomes known
    /// (e.g., right after parsing the header) without any interface change.
    /// </para>
    /// </remarks>
    public interface IProgressReportable
    {
        /// <summary>
        /// Gets or sets the progress reporter for tracking I/O operations.
        /// See the interface remarks for the required sign convention.
        /// </summary>
        IProgress<int>? ProgressReporter { get; set; }
    }

    /// <summary>
    /// Indicates that a file format reader supports virtual (MMF-backed) loading
    /// in addition to standard in-memory loading.
    /// </summary>
    /// <remarks>
    /// Readers that implement this interface allow the caller to choose between
    /// <see cref="LoadingMode.InMemory"/> and <see cref="LoadingMode.Virtual"/> loading.
    /// When <see cref="LoadingMode.Auto"/> is set (the default), the reader uses
    /// <see cref="VirtualPolicy.Resolve"/> to decide based on file size.
    /// <code>
    /// var reader = FormatRegistry.CreateReader(filePath);
    /// if (reader is IVirtualLoadable vl) vl.LoadingMode = LoadingMode.Virtual;
    /// var data = reader.Read(filePath);
    /// </code>
    /// </remarks>
    public interface IVirtualLoadable
    {
        /// <summary>
        /// Gets or sets the loading strategy for this reader.
        /// </summary>
        LoadingMode LoadingMode { get; set; }
    }

    /// <summary>
    /// Indicates that a file format writer supports configurable compression.
    /// </summary>
    /// <remarks>
    /// UI layers can use this to disable compression generically when saving virtual data,
    /// avoiding the costly re-compression of already-uncompressed MMF-backed data:
    /// <code>
    /// var writer = FormatRegistry.CreateWriter(path);
    /// if (data.IsVirtual &amp;&amp; writer is ICompressible c) c.CompressionInWrite = false;
    /// </code>
    /// </remarks>
    public interface ICompressible
    {
        /// <summary>
        /// Gets or sets whether compression is applied when writing.
        /// Defaults to <c>true</c> for most formats.
        /// </summary>
        bool CompressionInWrite { get; set; }
    }

    /// <summary>
    /// Defines a method for reading matrix data from a file into a strongly typed structure.
    /// </summary>
    /// <remarks>Implementations of this interface are responsible for parsing files containing matrix data
    /// and returning the data in a type-safe manner. The generic type parameter allows reading matrices of various
    /// unmanaged numeric types, such as integers or floating-point values.</remarks>
    public interface IMatrixDataReader : IFileFormatDescriptor
    {
        /// <summary>
        /// Gets or sets the cancellation token used to cancel a read operation in progress.
        /// The default value is <see cref="CancellationToken.None"/> (uncancellable).
        /// </summary>
        CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// Gets whether this reader actually checks <see cref="CancellationToken"/> during reading.
        /// Returns <c>false</c> by default; readers that implement cancellation override this to <c>true</c>.
        /// </summary>
        bool IsCancellable => false;

        /// <summary>
        /// Reads matrix data from the specified file and returns it as a MatrixData<T> instance.
        /// </summary>
        /// <typeparam name="T">The unmanaged value type of the matrix elements to read from the file.</typeparam>
        /// <param name="filePath">The path to the file containing the matrix data to read. Cannot be null or empty.</param>
        /// <returns>A MatrixData<T> object containing the matrix data read from the specified file.</returns>
        MatrixData<T> Read<T>(string filePath) where T : unmanaged;

        /// <summary>
        /// Reads matrix data from the specified file path.
        /// </summary>
        /// <param name="path">The path to the file containing the matrix data to read. Cannot be null or empty.</param>
        /// <returns>An <see cref="IMatrixData"/> instance containing the data read from the file.</returns>
        IMatrixData Read(string path);
    }

    /// <summary>
    /// Defines a method for writing matrix data to a file in a specific format.
    /// </summary>
    /// <remarks>Implementations of this interface are responsible for persisting matrix data to disk. The
    /// format and structure of the output file depend on the concrete implementation. This interface is typically used
    /// to abstract file output for different matrix storage formats.</remarks>
    public interface IMatrixDataWriter : IFileFormatDescriptor
    {
        /// <summary>
        /// Writes the specified matrix data to a file.
        /// </summary>
        /// <typeparam name="T">The unmanaged value type of the matrix elements.</typeparam>
        /// <param name="filePath">The destination path where the matrix data will be saved. Cannot be null or empty.</param>
        /// <param name="data">The <see cref="MatrixData{T}"/> instance containing the data to write.</param>
        /// <param name="accessor">An <see cref="IBackendAccessor"/> providing access to the underlying backing store of the matrix data, if needed by the writer.</param>
        void Write<T>(string filePath, MatrixData<T> data, IBackendAccessor accessor) where T : unmanaged;
    }

    /// <summary>
    /// Defines a builder responsible for allocating virtual storage (such as memory-mapped files) 
    /// and constructing a new, writable <see cref="MatrixData{T}"/> instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This interface acts as a blueprint or factory. It encapsulates format-specific constraints 
    /// (e.g., allowed dimension orders or metadata structures) and ensures that the resulting 
    /// <see cref="MatrixData{T}"/> is properly bound to a compatible underlying virtual storage.
    /// </para>
    /// <para>
    /// Implementations (such as an OME-TIFF builder) define <i>how</i> the physical container is structured, 
    /// while leaving the actual pixel manipulation to the domain logic of <see cref="MatrixData{T}"/>.
    /// </para>
    /// </remarks>
    public interface IVirtualFrameBuilder
    {
        /// <summary>
        /// Creates a new, writable <see cref="MatrixData{T}"/> instance backed by virtual storage.
        /// </summary>
        /// <typeparam name="T">The unmanaged value type of the matrix elements.</typeparam>
        /// <param name="backingFilePath">
        /// The explicit path where the underlying virtual storage file should be created. 
        /// If <see langword="null"/>, the implementation is responsible for generating and managing a temporary file.
        /// </param>
        /// <returns>A newly constructed, writable <see cref="MatrixData{T}"/> instance bound to the virtual storage.</returns>
        MatrixData<T> CreateWritable<T>(string? backingFilePath) where T : unmanaged;
    }

    /// <summary>
    /// Specifies the loading strategy for matrix data, determining how and when image data is loaded into memory.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Auto"/> to let the system select the most appropriate strategy based on file size.
    /// <see cref="InMemory"/> loads all data at once (fast access, high memory).
    /// <see cref="Virtual"/> uses memory-mapped files for on-demand access (low memory, may be slower on repeated access).
    /// </remarks>
    public enum LoadingMode
    {
        /// <summary>
        /// Automatically selects the loading mode based on the data size.
        /// </summary>
        Auto,

        /// <summary>
        /// Loads all frame data into memory at once.
        /// </summary>
        InMemory,

        /// <summary>
        /// Loads frame data on demand via memory-mapped virtual frames.
        /// </summary>
        Virtual
    }

    /// <summary>
    /// Provides global policy settings for virtual (MMF-backed) data loading.
    /// </summary>
    /// <remarks>
    /// Format handlers use <see cref="ThresholdBytes"/> when <see cref="LoadingMode.Auto"/> is requested
    /// to decide whether to load data in-memory or as virtual frames.
    /// The threshold can be changed at runtime to suit the application's memory constraints.
    /// </remarks>
    public static class VirtualPolicy
    {
        /// <summary>
        /// Gets or sets the file-size threshold (in bytes) above which <see cref="LoadingMode.Auto"/>
        /// resolves to <see cref="LoadingMode.Virtual"/>. Default is 2 GB.
        /// </summary>
        public static long ThresholdBytes { get; set; } = 2L * 1024 * 1024 * 1024;

        /// <summary>
        /// Gets or sets the frame-count threshold above which <see cref="LoadingMode.Auto"/>
        /// resolves to <see cref="LoadingMode.Virtual"/>, regardless of file size.
        /// Targets uncompressed formats where per-frame IFD overhead makes InMemory loading
        /// slow even for relatively small files. Default is 1000.
        /// </summary>
        public static int ThresholdFrames { get; set; } = 1000;

        /// <summary>
        /// Resolves <see cref="LoadingMode.Auto"/> based on file size and frame count.
        /// Returns <paramref name="mode"/> unchanged if it is not <see cref="LoadingMode.Auto"/>.
        /// Virtual mode is chosen when <em>either</em> condition is met:
        /// <list type="bullet">
        ///   <item><paramref name="fileBytes"/> exceeds <see cref="ThresholdBytes"/></item>
        ///   <item><paramref name="frameCount"/> exceeds <see cref="ThresholdFrames"/> (when &gt; 0)</item>
        /// </list>
        /// </summary>
        /// <param name="mode">The requested loading mode.</param>
        /// <param name="fileBytes">The size of the file in bytes.</param>
        /// <param name="frameCount">
        /// Total number of frames in the file. Pass <c>0</c> (default) to skip the frame-count check.
        /// Primarily useful for uncompressed formats where per-frame IFD traversal dominates load time.
        /// </param>
        /// <param name="canVirtual">
        /// Whether the format handler supports virtual loading for this file
        /// (e.g., <c>false</c> for compressed .mxd files).
        /// </param>
        /// <returns>The resolved <see cref="LoadingMode"/>.</returns>
        public static LoadingMode Resolve(
            LoadingMode mode, long fileBytes, int frameCount = 0, bool canVirtual = true)
        {
            if (mode != LoadingMode.Auto) return mode;
            if (!canVirtual) return LoadingMode.InMemory;
            bool largeFile  = fileBytes   > ThresholdBytes;
            bool manyFrames = frameCount  > 0 && frameCount > ThresholdFrames;
            return (largeFile || manyFrames)
                ? LoadingMode.Virtual
                : LoadingMode.InMemory;
        }
    }
}
