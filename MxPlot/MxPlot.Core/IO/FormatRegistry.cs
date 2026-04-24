using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MxPlot.Core.IO
{
    /// <summary>
    /// Central registry for discoverable file format handlers.
    /// Stores <see cref="Type"/> references and creates fresh instances on demand
    /// so that per-operation state (e.g. <see cref="IProgressReportable.ProgressReporter"/>)
    /// is never shared across concurrent callers.
    /// </summary>
    /// <remarks>
    /// <para><b>Built-in formats</b> (<see cref="MxBinaryFormat"/>, <see cref="CsvFormat"/>)
    /// are registered automatically when the class is first accessed.</para>
    /// <para><b>Extension formats</b> (OME-TIFF, HDF5, etc.) are auto-discovered from
    /// <c>MxPlot.Extensions.*.dll</c> in <see cref="AppContext.BaseDirectory"/> during
    /// static initialization. No explicit startup call is required for the default pattern.</para>
    /// <para>For additional plugin patterns (e.g. third-party DLLs with non-standard names),
    /// call <see cref="ScanAndRegister(string, string)"/> explicitly.</para>
    /// <para>
    /// When multiple formats match a file path, the one with the <b>longest matching extension</b>
    /// wins (e.g. <c>.ome.tif</c> beats <c>.tif</c> for <c>stack.ome.tif</c>).
    /// Registration order is used only as a tiebreaker for equal-length extensions.
    /// </para>
    /// </remarks>
    public static class FormatRegistry
    {
        private static readonly List<Type> _readerTypes = [];
        private static readonly List<Type> _writerTypes = [];

        // Prototype instances cached for descriptor queries (FormatName, Extensions, DialogFilter).
        // Never used for actual I/O — CreateReader/CreateWriter return fresh instances.
        private static readonly List<IMatrixDataReader> _readerDescriptors = [];
        private static readonly List<IMatrixDataWriter> _writerDescriptors = [];

        private static readonly object _lock = new();

        static FormatRegistry()
        {
            // Built-in formats (always available from Core)
            RegisterReader<MxBinaryFormat>();
            RegisterWriter<MxBinaryFormat>();
            RegisterReader<CsvFormat>();
            RegisterWriter<CsvFormat>();
            Register<FitsFormat>();

            // Auto-discover extension formats from MxPlot.Extensions.*.dll
            ScanAndRegister();
        }

        // ── Registration ─────────────────────────────────────────────────

        /// <summary>Registers a reader type. The type must have a parameterless constructor.</summary>
        public static void RegisterReader<T>() where T : IMatrixDataReader, new()
        {
            lock (_lock)
            {
                if (_readerTypes.Contains(typeof(T))) return;
                _readerTypes.Add(typeof(T));
                _readerDescriptors.Add(new T());
            }
        }

        /// <summary>Registers a writer type. The type must have a parameterless constructor.</summary>
        public static void RegisterWriter<T>() where T : IMatrixDataWriter, new()
        {
            lock (_lock)
            {
                if (_writerTypes.Contains(typeof(T))) return;
                _writerTypes.Add(typeof(T));
                _writerDescriptors.Add(new T());
            }
        }

        /// <summary>Registers a type as both reader and writer if it implements both interfaces.</summary>
        public static void Register<T>() where T : IMatrixDataReader, IMatrixDataWriter, new()
        {
            RegisterReader<T>();
            RegisterWriter<T>();
        }

        // ── Descriptors (for dialog filters) ─────────────────────────────

        /// <summary>Gets read-only descriptors for all registered readers (for dialog filter generation).</summary>
        public static IReadOnlyList<IFileFormatDescriptor> ReaderDescriptors
        {
            get { lock (_lock) return _readerDescriptors.ToArray(); }
        }

        /// <summary>Gets read-only descriptors for all registered writers (for dialog filter generation).</summary>
        public static IReadOnlyList<IFileFormatDescriptor> WriterDescriptors
        {
            get { lock (_lock) return _writerDescriptors.ToArray(); }
        }

        // ── Factory (fresh instance per call) ────────────────────────────

        /// <summary>
        /// Creates a new <see cref="IMatrixDataReader"/> instance whose extensions match <paramref name="filePath"/>.
        /// When multiple readers match, the one with the longest matching extension wins
        /// (e.g. <c>.ome.tif</c> beats <c>.tif</c> for <c>stack.ome.tif</c>).
        /// Returns <c>null</c> if no registered reader matches.
        /// </summary>
        public static IMatrixDataReader? CreateReader(string filePath)
        {
            lock (_lock)
            {
                int bestIndex = FindBestMatch(_readerDescriptors, filePath);
                return bestIndex >= 0
                    ? (IMatrixDataReader)Activator.CreateInstance(_readerTypes[bestIndex])!
                    : null;
            }
        }

        /// <summary>
        /// Creates a new <see cref="IMatrixDataWriter"/> instance whose extensions match <paramref name="filePath"/>.
        /// When multiple writers match, the one with the longest matching extension wins.
        /// Returns <c>null</c> if no registered writer matches.
        /// </summary>
        public static IMatrixDataWriter? CreateWriter(string filePath)
        {
            lock (_lock)
            {
                int bestIndex = FindBestMatch(_writerDescriptors, filePath);
                return bestIndex >= 0
                    ? (IMatrixDataWriter)Activator.CreateInstance(_writerTypes[bestIndex])!
                    : null;
            }
        }

        // ── DLL scanning ─────────────────────────────────────────────────

        /// <summary>
        /// Default glob pattern for format plugin DLLs.
        /// Third-party plugins should follow this naming convention:
        /// <c>MxPlot.Extensions.{Name}.dll</c> (e.g. <c>MxPlot.Extensions.Zarr.dll</c>).
        /// </summary>
        public const string DefaultFormatExtensionPattern = "MxPlot.Extensions.*.dll";

        /// <summary>
        /// Scans DLLs matching <paramref name="pattern"/> in <paramref name="directory"/>,
        /// discovers types implementing <see cref="IMatrixDataReader"/> or <see cref="IMatrixDataWriter"/>,
        /// and registers them.
        /// </summary>
        /// <param name="directory">
        /// The directory to scan. Defaults to <see cref="AppContext.BaseDirectory"/>.
        /// </param>
        /// <param name="pattern">
        /// Glob pattern for format extension DLLs. Defaults to <see cref="DefaultFormatExtensionPattern"/>.
        /// </param>
        /// <returns>The number of newly registered format types.</returns>
        public static int ScanAndRegister(string? directory = null, string pattern = DefaultFormatExtensionPattern)
        {
            directory ??= AppContext.BaseDirectory;
            int count = 0;

            foreach (var dll in Directory.EnumerateFiles(directory, pattern))
            {
                Assembly asm;
                try { asm = Assembly.LoadFrom(dll); }
                catch { continue; }

                count += RegisterFromAssembly(asm);
            }
            return count;
        }

        /// <summary>
        /// Registers all <see cref="IMatrixDataReader"/> and <see cref="IMatrixDataWriter"/>
        /// implementations found in the specified assembly.
        /// </summary>
        /// <returns>The number of newly registered format types.</returns>
        public static int RegisterFromAssembly(Assembly assembly)
        {
            int count = 0;
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (type.GetConstructor(Type.EmptyTypes) == null) continue;

                bool isReader = typeof(IMatrixDataReader).IsAssignableFrom(type);
                bool isWriter = typeof(IMatrixDataWriter).IsAssignableFrom(type);
                if (!isReader && !isWriter) continue;

                lock (_lock)
                {
                    if (isReader && !_readerTypes.Contains(type))
                    {
                        _readerTypes.Add(type);
                        _readerDescriptors.Add((IMatrixDataReader)Activator.CreateInstance(type)!);
                        count++;
                    }
                    if (isWriter && !_writerTypes.Contains(type))
                    {
                        _writerTypes.Add(type);
                        _writerDescriptors.Add((IMatrixDataWriter)Activator.CreateInstance(type)!);
                        count++;
                    }
                }
            }
            return count;
        }

        // ── Dialog filter helpers ────────────────────────────────────────

        /// <summary>
        /// Builds a combined WinForms/WPF filter string for all registered readers.
        /// Example: <c>"OME-TIFF (*.ome.tiff;*.ome.tif)|*.ome.tiff;*.ome.tif|MxPlot Binary (*.mxd)|*.mxd"</c>
        /// </summary>
        public static string GetOpenDialogFilter(bool includeAllFiles = true)
        {
            var filters = ReaderDescriptors.Select(d => d.DialogFilter).ToList();
            if (includeAllFiles) filters.Add("All Files (*.*)|*.*");
            return string.Join("|", filters);
        }

        /// <summary>
        /// Builds a combined WinForms/WPF filter string for all registered writers.
        /// </summary>
        public static string GetSaveDialogFilter(bool includeAllFiles = false)
        {
            var filters = WriterDescriptors.Select(d => d.DialogFilter).ToList();
            if (includeAllFiles) filters.Add("All Files (*.*)|*.*");
            return string.Join("|", filters);
        }

        /// <summary>
        /// Strips any registered compound extension from <paramref name="fileName"/>,
        /// returning the base name without format-specific suffixes.
        /// Handles compound extensions like <c>.ome.tif</c> that
        /// <see cref="Path.GetFileNameWithoutExtension"/> cannot strip correctly.
        /// </summary>
        /// <example>
        /// <code>
        /// FormatRegistry.StripKnownExtension("data.ome.tif") → "data"
        /// FormatRegistry.StripKnownExtension("result.mxd")   → "result"
        /// FormatRegistry.StripKnownExtension("noext")         → "noext"
        /// </code>
        /// </example>
        public static string StripKnownExtension(string fileName)
        {
            // Try all registered extensions (readers + writers), longest first,
            // so ".ome.tif" is checked before ".tif".
            lock (_lock)
            {
                var allDescriptors = _readerDescriptors.Cast<IFileFormatDescriptor>()
                    .Concat(_writerDescriptors);
                int bestLength = 0;
                foreach (var d in allDescriptors)
                    foreach (var ext in d.Extensions)
                        if (ext.Length > bestLength
                            && fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                            bestLength = ext.Length;

                return bestLength > 0
                    ? fileName[..^bestLength]
                    : Path.GetFileNameWithoutExtension(fileName);
            }
        }

        /// <summary>
        /// Repairs a full file path that may contain accumulated compound extensions
        /// caused by OS native save dialogs not understanding multi-dot extensions.
        /// Repeatedly strips known extensions from the base name until only one remains,
        /// then re-appends the final (outermost) extension.
        /// </summary>
        /// <example>
        /// <code>
        /// // "data.ome.ome.tif" → "data.ome.tif"  (one .ome was spurious)
        /// // "data.ome.h5"      → "data.h5"        (.ome was left over from OME-TIFF)
        /// </code>
        /// </example>
        public static string CleanCompoundExtension(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var fileName = Path.GetFileName(filePath);

            // Find the actual extension the OS dialog chose (the final registered extension)
            string finalExt = "";
            lock (_lock)
            {
                int bestLength = 0;
                var allDescriptors = _readerDescriptors.Cast<IFileFormatDescriptor>()
                    .Concat(_writerDescriptors);
                foreach (var d in allDescriptors)
                    foreach (var ext in d.Extensions)
                        if (ext.Length > bestLength
                            && fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                        {
                            bestLength = ext.Length;
                            finalExt = ext;
                        }
            }
            if (finalExt.Length == 0) return filePath; // unknown extension, leave as-is

            // Strip the final extension, then repeatedly strip any leftover known extensions
            var baseName = fileName[..^finalExt.Length];
            string prev;
            do
            {
                prev = baseName;
                baseName = StripKnownExtension(baseName);
            }
            while (baseName != prev && baseName.Length > 0);

            // Guard: if stripping ate everything, fall back to original
            if (baseName.Length == 0) return filePath;

            var cleaned = Path.Combine(dir, baseName + finalExt);
            return cleaned;
        }

        // ── Internal helpers ─────────────────────────────────────────────

        /// <summary>
        /// Finds the descriptor index whose extension is the longest match for <paramref name="filePath"/>.
        /// Returns -1 if no match is found.
        /// </summary>
        private static int FindBestMatch<T>(List<T> descriptors, string filePath) where T : IFileFormatDescriptor
        {
            int bestIndex = -1;
            int bestLength = 0;
            for (int i = 0; i < descriptors.Count; i++)
            {
                foreach (var ext in descriptors[i].Extensions)
                {
                    if (ext.Length > bestLength
                        && filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        bestIndex = i;
                        bestLength = ext.Length;
                    }
                }
            }
            return bestIndex;
        }
    }
}
