using Avalonia.Threading;
using MxPlot.App.ViewModels;
using MxPlot.App.Views;
using System.Threading.Tasks;

namespace MxPlot.App
{
    /// <summary>
    /// Single entry point for opening the MxPlot application window.
    /// Hides V/VM construction so callers need only one line.
    /// </summary>
    /// <remarks>
    /// <para><b>From an Avalonia app</b> (UI thread already exists):</para>
    /// <code>MxPlotApp.Launch();</code>
    ///
    /// <para><b>From a non-UI thread</b> (background task, async handler, WinForms button click):</para>
    /// <code>await MxPlotApp.LaunchAsync();</code>
    ///
    /// <para><b>From WinForms / Console</b> — Avalonia must be initialised first in Program.cs
    /// before any call to Launch / LaunchAsync:</para>
    /// <code>
    /// // Program.cs (WinForms):
    /// AppBuilder.Configure&lt;Application&gt;()
    ///     .UsePlatformDetect()
    ///     .SetupWithoutStarting();   // ← non-blocking init
    /// // Then anywhere:
    /// await MxPlotApp.LaunchAsync();
    ///
    /// // Program.cs (console — blocks until all windows close):
    /// AppBuilder.Configure&lt;Application&gt;()
    ///     .UsePlatformDetect()
    ///     .StartWithClassicDesktopLifetime(args);
    /// </code>
    /// </remarks>
    public static class MxPlotApp
    {
        /// <summary>
        /// Creates and shows the <see cref="MxPlotAppWindow"/>.
        /// <b>Must be called on the Avalonia UI thread.</b>
        /// Use <see cref="LaunchAsync"/> when calling from a background thread.
        /// </summary>
        /// <returns>The newly created window (already shown).</returns>
        public static MxPlotAppWindow Launch()
        {
            Dispatcher.UIThread.VerifyAccess();
            var vm  = new MxPlotAppViewModel();
            var win = new MxPlotAppWindow { DataContext = vm };
            win.Show();
            return win;
        }

        /// <summary>
        /// Schedules <see cref="Launch"/> on the Avalonia UI thread.
        /// Safe to await from any thread as long as Avalonia has been initialised.
        /// </summary>
        public static Task<MxPlotAppWindow> LaunchAsync()
            => Dispatcher.UIThread.InvokeAsync(Launch).GetTask();
    }
}
