using Avalonia.Threading;
using MxPlot.UI.Avalonia.Plugins;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace MxPlot.App.Plugins
{
    /// <summary>
    /// Central registry for <see cref="IMxPlotPlugin"/> instances.
    /// </summary>
    public static class MxPlotAppPluginRegistry
    {
        private static readonly List<IMxPlotPlugin> _plugins = [];

        /// <summary>All currently registered plugins (read-only snapshot).</summary>
        public static IReadOnlyList<IMxPlotPlugin> Plugins => _plugins.AsReadOnly();

        /// <summary>
        /// Fired on the UI thread whenever the plugin list changes.
        /// <see cref="Views.MxPlotAppWindow"/> subscribes to rebuild the hamburger menu.
        /// </summary>
        public static event Action? PluginsChanged;

        /// <summary>Registers a plugin programmatically.</summary>
        public static void AddPlugin(IMxPlotPlugin plugin)
        {
            _plugins.Add(plugin);
            if (Dispatcher.UIThread.CheckAccess())
                PluginsChanged?.Invoke();
            else
                Dispatcher.UIThread.Post(() => PluginsChanged?.Invoke());
        }

        /// <summary>
        /// Scans <paramref name="pluginsDir"/> for DLLs and registers every exported
        /// class that implements <see cref="IMxPlotPlugin"/>.
        /// </summary>
        public static void LoadFromDirectory(string pluginsDir)
        {
            if (!Directory.Exists(pluginsDir)) return;
            foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (type.IsClass && !type.IsAbstract
                            && typeof(IMxPlotPlugin).IsAssignableFrom(type)
                            && Activator.CreateInstance(type) is IMxPlotPlugin plugin)
                        {
                            AddPlugin(plugin);
                        }
                    }
                }
                catch { }
            }
        }

        // ── Default IPlotWindowService ────────────────────────────────────────

        /// <summary>
        /// The <see cref="IPlotWindowService"/> used when building <see cref="IMxPlotContext"/>.
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
