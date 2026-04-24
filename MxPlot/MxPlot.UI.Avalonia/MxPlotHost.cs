using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using System;

namespace MxPlot.UI.Avalonia
{
    /// <summary>
    /// Minimal Avalonia <see cref="Application"/> for hosting MxPlot windows inside a
    /// non-Avalonia application (WinForms, console, etc.).
    /// Loads the Fluent theme and the MxPlot UI styles; no main window is created.
    /// </summary>
    /// <remarks>
    /// Pass this type to <c>AppBuilder.Configure</c> in the host application's startup code.
    /// The host is responsible for choosing the platform backend:
    /// <code>
    /// // Program.cs (WinForms) — requires Avalonia.Desktop NuGet in the host project:
    /// AppBuilder.Configure&lt;MxPlotHostApplication&gt;()
    ///     .UsePlatformDetect()
    ///     .SetupWithoutStarting();
    /// </code>
    /// </remarks>
    public sealed class MxPlotHostApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());

            var uri = new Uri("avares://MxPlot.UI.Avalonia/Themes/Default.axaml");
            Styles.Add(new StyleInclude(uri) { Source = uri });
        }

        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
        }
    }
}
