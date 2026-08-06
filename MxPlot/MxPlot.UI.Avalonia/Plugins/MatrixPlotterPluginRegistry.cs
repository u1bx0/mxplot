using Avalonia.Threading;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MxPlot.UI.Avalonia.Plugins
{
    /// <summary>
    /// Central registry for <see cref="IMatrixPlotterPlugin"/> instances.
    /// Plugins can be added programmatically via <see cref="AddPlugin"/> or
    /// discovered from a directory of DLLs via <see cref="LoadFromDirectory"/>.
    /// </summary>
    public static class MatrixPlotterPluginRegistry
    {
        private static readonly List<IMatrixPlotterPlugin> _plugins = [];
        private static readonly List<IRenderExportPlugin> _exportPlugins = [];

        /// <summary>All currently registered plugins (read-only snapshot).</summary>
        public static IReadOnlyList<IMatrixPlotterPlugin> Plugins => _plugins.AsReadOnly();

        /// <summary>All registered render-export plugins (read-only snapshot).</summary>
        public static IReadOnlyList<IRenderExportPlugin> ExportPlugins => _exportPlugins.AsReadOnly();

        /// <summary>
        /// Fired on the UI thread whenever the plugin list changes.
        /// Subscribe in <see cref="Views.MatrixPlotter"/> to rebuild the Plugins menu tab.
        /// </summary>
        public static event Action? PluginsChanged;

        /// <summary>
        /// Fired on the UI thread when <see cref="ExportPlugins"/> changes.
        /// <see cref="Views.MatrixPlotter"/> subscribes to rebuild the "Export as…" menu.
        /// </summary>
        public static event Action? ExportPluginsChanged;

        /// <summary>
        /// Registers a plugin programmatically.
        /// Safe to call before the UI is initialised.
        /// </summary>
        public static void AddPlugin(IMatrixPlotterPlugin plugin)
        {
            _plugins.Add(plugin);
            if (Dispatcher.UIThread.CheckAccess())
                PluginsChanged?.Invoke();
            else
                Dispatcher.UIThread.Post(() => PluginsChanged?.Invoke());
        }

        /// <summary>
        /// Registers a render-export plugin programmatically.
        /// Safe to call before the UI is initialised.
        /// </summary>
        public static void AddExportPlugin(IRenderExportPlugin plugin)
        {
            _exportPlugins.Add(plugin);
            if (Dispatcher.UIThread.CheckAccess())
                ExportPluginsChanged?.Invoke();
            else
                Dispatcher.UIThread.Post(() => ExportPluginsChanged?.Invoke());
        }

        /// <summary>
        /// Scans <paramref name="pluginsDir"/> (including subdirectories) for DLLs,
        /// instantiates every exported class that implements <see cref="IMatrixPlotterPlugin"/>
        /// or <see cref="IRenderExportPlugin"/>, and registers it.
        /// Malformed or incompatible DLLs are silently skipped.
        /// </summary>
        public static void LoadFromDirectory(string pluginsDir)
        {
            if (!Directory.Exists(pluginsDir)) return;
            foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (!type.IsClass || type.IsAbstract) continue;
                        if (typeof(IMatrixPlotterPlugin).IsAssignableFrom(type)
                            && Activator.CreateInstance(type) is IMatrixPlotterPlugin plugin)
                        {
                            AddPlugin(plugin);
                        }
                        if (typeof(IRenderExportPlugin).IsAssignableFrom(type)
                            && Activator.CreateInstance(type) is IRenderExportPlugin ep)
                        {
                            AddExportPlugin(ep);
                        }
                    }
                }
                catch { /* skip unloadable / incompatible DLLs */ }
            }
        }

        // ── Default IPlotWindowService ────────────────────────────────────────

        /// <summary>
        /// The <see cref="IPlotWindowService"/> used when creating contexts inside
        /// <see cref="Views.MatrixPlotter"/>.
        /// Replace with a richer implementation (e.g. one that registers windows with a
        /// dashboard ViewModel) at application start-up if needed.
        /// </summary>
        public static IPlotWindowService WindowService { get; set; }
            = new DefaultPlotWindowService();

        private sealed class DefaultPlotWindowService : IPlotWindowService
        {
            public void ShowMatrixPlotter(IMatrixData data, string? title = null)
                => Dispatcher.UIThread.Post(()
                    => Views.MatrixPlotter.Create(data, title: title).Show());
        }
    }
}
