using MxPlot.Core.IO.Formats;
using MxPlot.UI.Avalonia.AddOns;
using MxPlot.UI.Avalonia.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MxPlot.App.Plugins
{
    /// <summary>
    /// Loads add-ons from every root of <see cref="AddOnFolders"/>: file-format extensions named
    /// <c>MxPlot.Extensions.*.dll</c> that sit directly in a root, and every DLL in its <c>plugins</c>
    /// folder, where a single DLL may hold any mix of file formats, MatrixPlotter plugins and
    /// MxPlot.App plugins.
    /// </summary>
    internal static class AddOnLoader
    {
        /// <summary>Loads the add-ons of the roots that exist, nearest root first.</summary>
        public static void LoadAll() => LoadAll(AddOnFolders.GetRoots(), AppContext.BaseDirectory);

        /// <summary>
        /// A plugin assembly is loaded once: if its name has already been seen, a later copy is
        /// skipped. Two kinds of names are seen before any add-on folder is read - the assemblies
        /// already loaded and the DLLs the application itself ships - so a plugin folder that also
        /// holds a copy of a shared library (for example a plugin's build output) can never replace
        /// the application's own.
        /// </summary>
        /// <param name="roots">The add-on roots, nearest first.</param>
        /// <param name="baseDirectory">The folder of the running assembly.</param>
        internal static void LoadAll(IReadOnlyList<string> roots, string baseDirectory)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (loaded.GetName().Name is { } loadedName) seen.Add(loadedName);
            }
            if (Directory.Exists(baseDirectory))
            {
                foreach (var dll in Directory.EnumerateFiles(baseDirectory, "*.dll"))
                    seen.Add(Path.GetFileNameWithoutExtension(dll));
            }

            foreach (var root in roots)
            {
                // FormatRegistry has already scanned the application's own folder.
                if (!IsSameDirectory(root, baseDirectory))
                {
                    try { FormatRegistry.ScanAndRegister(root); }
                    catch { /* an unreadable root is skipped */ }
                }

                string? pluginsDir = AddOnFolders.ResolveSubfolder(root, AddOnFolders.PluginsFolderName);
                if (pluginsDir == null) continue;

                IEnumerable<string> dlls;
                try { dlls = Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var dll in dlls)
                {
                    try
                    {
                        string? name = AssemblyName.GetAssemblyName(dll).Name;
                        if (name == null || !seen.Add(name)) continue;

                        // Each registry takes only the types that implement its own interfaces.
                        var assembly = Assembly.LoadFrom(dll);
                        MatrixPlotterPluginRegistry.LoadFromAssembly(assembly);
                        MxPlotAppPluginRegistry.LoadFromAssembly(assembly);
                        FormatRegistry.RegisterFromAssembly(assembly);
                    }
                    catch { /* not a managed assembly, or incompatible */ }
                }
            }
        }

        private static bool IsSameDirectory(string a, string b)
            => string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }
}
