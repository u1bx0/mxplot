using Avalonia.Threading;
using MxPlot.UI.Avalonia.Plugins;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MxPlot.App.Plugins
{
    /// <summary>
    /// Central registry for <see cref="IMxPlotAppPlugin"/> instances.
    /// </summary>
    public static class MxPlotAppPluginRegistry
    {
        private static readonly List<IMxPlotAppPlugin> _plugins = [];

        /// <summary>All currently registered plugins (read-only snapshot).</summary>
        public static IReadOnlyList<IMxPlotAppPlugin> Plugins => _plugins.AsReadOnly();

        /// <summary>
        /// Fired on the UI thread whenever the plugin list changes.
        /// <see cref="Views.MxPlotAppWindow"/> subscribes to rebuild the hamburger menu.
        /// </summary>
        public static event Action? PluginsChanged;

        /// <summary>Registers a plugin programmatically.</summary>
        public static void AddPlugin(IMxPlotAppPlugin plugin)
        {
            _plugins.Add(plugin);
            if (Dispatcher.UIThread.CheckAccess())
                PluginsChanged?.Invoke();
            else
                Dispatcher.UIThread.Post(() => PluginsChanged?.Invoke());
        }

        /// <summary>
        /// Scans <paramref name="pluginsDir"/> for DLLs and registers every exported
        /// class that implements <see cref="IMxPlotAppPlugin"/>.
        /// </summary>
        public static void LoadFromDirectory(string pluginsDir)
        {
            if (!Directory.Exists(pluginsDir)) return;
            foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
            {
                try { LoadFromAssembly(Assembly.LoadFrom(dll)); }
                catch { /* skip unloadable / incompatible DLLs */ }
            }
        }

        /// <summary>
        /// Instantiates every exported class of <paramref name="assembly"/> that implements
        /// <see cref="IMxPlotAppPlugin"/>, and registers it. A type that is already registered is
        /// not registered again, so the same assembly can be offered more than once. An
        /// incompatible assembly is silently skipped.
        /// </summary>
        /// <returns>The number of plugins registered by this call.</returns>
        public static int LoadFromAssembly(Assembly assembly)
        {
            int count = 0;
            try
            {
                foreach (var type in assembly.GetExportedTypes())
                {
                    if (type.IsClass && !type.IsAbstract
                        && typeof(IMxPlotAppPlugin).IsAssignableFrom(type)
                        && !_plugins.Any(p => p.GetType() == type)
                        && Activator.CreateInstance(type) is IMxPlotAppPlugin plugin)
                    {
                        AddPlugin(plugin);
                        count++;
                    }
                }
            }
            catch { /* skip an unloadable / incompatible assembly */ }
            return count;
        }

        // ── Default IPlotWindowService ────────────────────────────────────────

        /// <summary>
        /// The <see cref="IPlotWindowService"/> used when building <see cref="IMxPlotAppContext"/>.
        /// Replace with a richer implementation at application start-up if needed.
        /// </summary>
        public static IPlotWindowService WindowService { get; set; }
            = new DefaultPlotWindowService();

        private sealed class DefaultPlotWindowService : IPlotWindowService
        {
            public void ShowMatrixPlotter(MxPlot.Core.IMatrixData data, string? title = null)
                => Dispatcher.UIThread.Post(()
                    => MatrixPlotter.Create(data, title: title).Show());
        }
    }
}
