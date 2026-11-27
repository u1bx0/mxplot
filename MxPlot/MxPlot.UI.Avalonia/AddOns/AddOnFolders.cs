using System;
using System.Collections.Generic;
using System.IO;

namespace MxPlot.UI.Avalonia.AddOns
{
    /// <summary>
    /// Where MxPlot looks for add-ons: plugins, file-format extensions and custom LUTs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every root has the same layout:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>MxPlot.Extensions.*.dll</c> directly in the root - file-format extensions.</item>
    ///   <item>A <see cref="PluginsFolderName"/> folder - DLLs of any name; a DLL there may hold any
    ///         mix of file formats, MatrixPlotter plugins and MxPlot.App plugins.</item>
    ///   <item>A <see cref="LutsFolderName"/> folder - custom palettes.</item>
    /// </list>
    /// <para>
    /// The roots, nearest first:
    /// </para>
    /// <list type="number">
    ///   <item>The folder of the running assembly - a Windows release folder, or the
    ///         <c>Contents/MacOS</c> folder inside a macOS bundle.</item>
    ///   <item>macOS only: <c>~/Library/Application Support/MxPlot</c>. This is where add-ons belong
    ///         on macOS: the app bundle is treated as read-only (changing it breaks its code
    ///         signature, it is hidden from Finder and it is replaced on every update), and the
    ///         location does not depend on where the user keeps the app.</item>
    ///   <item>macOS only: the folder that holds the <c>.app</c> bundle. A convenience that is not
    ///         recommended: the folder differs from user to user, and an app that was never moved
    ///         out of its download location may run from a translocated, read-only copy where the
    ///         neighboring folder is not visible.</item>
    /// </list>
    /// <para>
    /// When the same file is found under more than one root, the nearest root wins; loaders skip a
    /// later copy of an assembly that has already been loaded.
    /// </para>
    /// </remarks>
    public static class AddOnFolders
    {
        /// <summary>The documented spelling of the plugins folder. Matched case-insensitively.</summary>
        public const string PluginsFolderName = "plugins";

        /// <summary>The documented spelling of the custom palettes folder. Matched case-insensitively.</summary>
        public const string LutsFolderName = "LUTs";

        private const string UserFolderName = "MxPlot";

        /// <summary>
        /// The folder a user is expected to put add-ons in, and that an "open the add-ons folder"
        /// command should show: the per-user folder on macOS, the application folder elsewhere.
        /// The folder may not exist yet; nothing is created here.
        /// </summary>
        public static string UserFolder
            => OperatingSystem.IsMacOS() ? MacUserFolder() : AppContext.BaseDirectory;

        /// <summary>
        /// The roots that currently exist, nearest first. See the type remarks for the order.
        /// </summary>
        public static IReadOnlyList<string> GetRoots()
            => [.. EnumerateRoots(AppContext.BaseDirectory, OperatingSystem.IsMacOS(), MacUserFolder())];

        /// <summary>
        /// The roots for an application that runs from <paramref name="baseDirectory"/>. Takes
        /// everything it depends on as arguments so the order and the bundle handling can be
        /// exercised on any OS; <see cref="GetRoots"/> supplies the real values.
        /// </summary>
        /// <param name="baseDirectory">The folder of the running assembly.</param>
        /// <param name="isMacOS">Whether the macOS roots apply.</param>
        /// <param name="macUserFolder">The per-user folder used on macOS.</param>
        internal static IEnumerable<string> EnumerateRoots(string baseDirectory, bool isMacOS, string? macUserFolder)
        {
            // Linux is the only one of the three platforms where two paths that differ only in case
            // are two different directories.
            var seen = new HashSet<string>(OperatingSystem.IsLinux()
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase);

            foreach (string? candidate in new[]
            {
                baseDirectory,
                isMacOS ? macUserFolder : null,
                isMacOS ? ResolveAppBundleParent(baseDirectory) : null,
            })
            {
                if (string.IsNullOrWhiteSpace(candidate) || !Directory.Exists(candidate)) continue;

                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
                if (seen.Add(root)) yield return root;
            }
        }

        /// <summary>
        /// Finds the subfolder of <paramref name="root"/> named <paramref name="folderName"/>,
        /// whatever its casing, or <c>null</c>. The exact spelling is tried first - it is what the
        /// file system resolves for free on Windows and on a default (case-insensitive) APFS volume.
        /// The sweep after it is for Linux and case-sensitive APFS, where <c>luts</c> would
        /// otherwise be silently ignored.
        /// </summary>
        public static string? ResolveSubfolder(string root, string folderName)
        {
            if (!Directory.Exists(root)) return null;

            string exact = Path.Combine(root, folderName);
            if (Directory.Exists(exact)) return exact;

            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                if (Path.GetFileName(dir).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            return null;
        }

        /// <summary>
        /// Given the directory an app bundle runs from (<c>Foo.app/Contents/MacOS</c>), returns the
        /// directory the bundle itself sits in. Returns <c>null</c> for anything that is not that
        /// structure.
        /// <para>
        /// The <c>Info.plist</c> check is what distinguishes a real bundle from a directory that
        /// merely happens to be three levels deep. Takes the path rather than reading
        /// <see cref="AppContext.BaseDirectory"/> itself so the structure test is exercisable off
        /// macOS; the caller applies the platform guard.
        /// </para>
        /// </summary>
        internal static string? ResolveAppBundleParent(string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return null;

            var macOsDir = new DirectoryInfo(baseDirectory);
            var contents = macOsDir.Parent;
            var bundle = contents?.Parent;
            if (contents == null || bundle == null) return null;

            if (!macOsDir.Name.Equals("MacOS", StringComparison.Ordinal)) return null;
            if (!contents.Name.Equals("Contents", StringComparison.Ordinal)) return null;
            if (!bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(Path.Combine(contents.FullName, "Info.plist"))) return null;

            return bundle.Parent?.FullName;
        }

        private static string MacUserFolder()
            => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support", UserFolderName);
    }
}
