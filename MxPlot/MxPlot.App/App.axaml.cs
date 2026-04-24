using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MxPlot.App.Plugins;
using MxPlot.App.ViewModels;
using MxPlot.App.Views;
using MxPlot.UI.Avalonia.Plugins;
using System.IO;
using System.Linq;

namespace MxPlot.App
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Apply theme from command-line args (--dark / --light)
                var args = desktop.Args ?? [];
                if (args.Contains("--dark"))
                    RequestedThemeVariant = ThemeVariant.Dark;
                else if (args.Contains("--light"))
                    RequestedThemeVariant = ThemeVariant.Light;

                // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
                // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
                DisableAvaloniaDataAnnotationValidation();

                // ── Plugin auto-scan ───────────────────────────────────────────────
                // Scans the "plugins" subdirectory next to the executable.
                // FormatRegistry (IMatrixDataReader/Writer) is handled separately via
                // its own MxPlot.Extensions.*.dll convention in AppContext.BaseDirectory.
                var pluginsDir = Path.Combine(System.AppContext.BaseDirectory, "plugins");
                MatrixPlotterPluginRegistry.LoadFromDirectory(pluginsDir);
                MxPlotAppPluginRegistry.LoadFromDirectory(pluginsDir);

                var vm = new MxPlotAppViewModel();
                var win = new MxPlotAppWindow { DataContext = vm };

                // Close all managed windows when the dashboard closes → full app exit
                win.Closing += (_, _) =>
                {
                    foreach (var item in vm.ManagedWindows.ToArray())
                        item.Window.Close();
                };

                desktop.MainWindow = win;
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static void DisableAvaloniaDataAnnotationValidation()
        {
            var toRemove = BindingPlugins.DataValidators
                .OfType<DataAnnotationsValidationPlugin>().ToArray();
            foreach (var plugin in toRemove)
                BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}