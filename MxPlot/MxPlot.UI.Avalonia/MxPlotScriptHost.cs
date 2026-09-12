using Avalonia;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Views;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia
{
    /// <summary>
    /// Runs MxPlot windows from a program that has no UI of its own — a C# script
    /// (<c>dotnet run app.cs</c>), a console tool, or a notebook cell.
    /// <para>
    /// Avalonia needs a message loop, and every window must be touched from the thread that owns
    /// it. This class owns both of those chores so that calling code can stay linear: open a
    /// window, keep computing, open another, adjust one that is already on screen.
    /// </para>
    /// <para>
    /// <b>Do not use this from a WinForms, WPF, or Avalonia application.</b> Those already have a
    /// message loop, and starting a second one corrupts Avalonia's thread affinity.
    /// <see cref="Run"/> and <see cref="Start"/> throw <see cref="InvalidOperationException"/> when
    /// they detect an application that is already initialized. For those hosts, configure Avalonia
    /// with <see cref="MxPlotHostApplication"/> directly — see the
    /// <c>WinForms / WPF Integration Guide</c>.
    /// </para>
    /// <para>
    /// <b>One session per process.</b> Avalonia can only be initialized once, so <see cref="Run"/>
    /// and <see cref="Start"/> may each be called once per process and not after one another. Open
    /// every window from inside a single session.
    /// </para>
    /// </summary>
    /// <example>
    /// The portable shape. The lambda runs off the UI thread, so it may block, compute, or sleep
    /// freely; <see cref="Run"/> returns once every window opened inside it has been closed.
    /// <code>
    /// // script.cs  —  dotnet run script.cs
    /// #:package MxPlot@0.3.0
    /// #:package Avalonia.Desktop@11.3.18
    ///
    /// using MxPlot.Core;
    /// using MxPlot.Core.Imaging;
    /// using MxPlot.UI.Avalonia;
    ///
    /// MxPlotScriptHost.Run(() =>
    /// {
    ///     var scan = new MatrixData&lt;double&gt;(256, 256);
    ///     scan.Set((ix, iy, x, y) => Math.Sin(ix * 0.1) * Math.Cos(iy * 0.1));
    ///     var main = MxPlotScriptHost.Show(scan, ColorThemes.Jet, "Scan");
    ///
    ///     var filtered = Analyze(scan);               // the window stays responsive
    ///     MxPlotScriptHost.Show(filtered, title: "Filtered");
    ///
    ///     MxPlotScriptHost.Invoke(() => main.RangeMode = ValueRangeMode.All);
    /// });
    /// </code>
    /// </example>
    /// <example>
    /// The non-blocking shape, for a REPL or notebook where the code cannot be wrapped in a
    /// lambda. Not available on macOS — see <see cref="Start"/>.
    /// <code>
    /// MxPlotScriptHost.Start();
    /// var plotter = MxPlotScriptHost.Show(data, title: "Live");
    /// // … later, in another cell …
    /// MxPlotScriptHost.Invoke(() => plotter.FixedRange = new RangeInfo(0, 4095));
    /// // … eventually …
    /// MxPlotScriptHost.Shutdown();
    /// </code>
    /// </example>
    public static class MxPlotScriptHost
    {
        private static readonly object _gate = new();
        private static Thread? _uiThread;
        private static CancellationTokenSource? _cts;
        private static ManualResetEventSlim? _ready;
        private static int _openWindows;
        private static bool _scriptFinished;
        private static Func<AppBuilder, AppBuilder>? _configure;
        private static bool _hasHosted;

        /// <summary>Whether a message loop started by this class is currently running.</summary>
        public static bool IsRunning => _uiThread is { IsAlive: true };

        /// <summary>
        /// The number of windows opened through <see cref="Show"/> that are still open.
        /// Windows created any other way are not counted.
        /// </summary>
        public static int OpenWindowCount => Volatile.Read(ref _openWindows);

        // ── Entry points ──────────────────────────────────────────────────────

        /// <summary>
        /// Starts a message loop, runs <paramref name="script"/> off the UI thread, and returns
        /// once every window opened through <see cref="Show"/> has closed. This is the portable
        /// entry point: it works on Windows, macOS and Linux alike.
        /// </summary>
        /// <param name="script">
        /// The calling code, which must not touch a window directly — use <see cref="Show"/> and
        /// <see cref="Invoke(Action)"/>. Exceptions are rethrown to the caller of <see cref="Run"/>
        /// after the loop has been shut down.
        /// </param>
        /// <param name="configure">
        /// Optional Avalonia backend setup. When omitted, <c>UsePlatformDetect()</c> is used, which
        /// requires the <c>Avalonia.Desktop</c> package to be referenced by the calling script or
        /// project. Supply this to pick a backend explicitly, e.g. <c>b =&gt; b.UseWin32().UseSkia()</c>.
        /// </param>
        /// <remarks>
        /// Which thread ends up running what differs per platform, and deliberately so: macOS
        /// requires the event loop to own the process's main thread, while Windows needs the loop
        /// on an STA thread that a top-level-statement program does not provide. Either way
        /// <paramref name="script"/> runs somewhere that is not the UI thread, so the contract seen
        /// by calling code is the same everywhere.
        /// <list type="table">
        ///   <listheader><term>Platform</term><description>Loop / script</description></listheader>
        ///   <item><term>Windows</term><description>loop on a new STA thread; script on the calling thread</description></item>
        ///   <item><term>macOS, Linux</term><description>loop on the calling thread; script on a worker thread</description></item>
        /// </list>
        /// If <paramref name="script"/> opens no windows, <see cref="Run"/> returns as soon as it
        /// finishes. Call <see cref="Shutdown"/> from inside the script to close everything early.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// An Avalonia application is already initialized in this process, or a loop started by
        /// this class is already running.
        /// </exception>
        public static void Run(Action script, Func<AppBuilder, AppBuilder>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(script);
            EnsureNotAlreadyHosted();
            _configure = configure;

            lock (_gate)
            {
                if (IsRunning)
                    throw new InvalidOperationException(
                        $"{nameof(MxPlotScriptHost)} is already running. {nameof(Run)} cannot be nested.");
                _cts = new CancellationTokenSource();
                _ready = new ManualResetEventSlim(false);
                _openWindows = 0;
                _scriptFinished = false;
            }

            ExceptionDispatchInfo? scriptError = null;
            void RunScript()
            {
                try { script(); }
                catch (Exception ex) { scriptError = ExceptionDispatchInfo.Capture(ex); }
                finally
                {
                    Volatile.Write(ref _scriptFinished, true);
                    // Nothing left on screen to wait for: stop the loop rather than hang.
                    if (OpenWindowCount == 0) RequestStop();
                }
            }

            if (OperatingSystem.IsWindows())
            {
                // The loop needs STA (OLE clipboard, drag & drop); a top-level-statement program
                // runs MTA, and a thread's apartment cannot be changed once it is started.
                _uiThread = StartLoopThread();
                _ready!.Wait();
                RunScript();
                _uiThread.Join();
            }
            else
            {
                // AppKit refuses to run its event loop anywhere but the process's main thread, so
                // here the caller's thread becomes the loop and the script is displaced instead.
                _uiThread = Thread.CurrentThread;
                ConfigureAvalonia();
                var worker = new Thread(RunScript) { Name = "MxPlot script", IsBackground = true };
                // Queued rather than started here, so the script cannot call Invoke before the
                // dispatcher is pumping and deadlock against a loop that has not begun.
                Dispatcher.UIThread.Post(worker.Start);
                Dispatcher.UIThread.MainLoop(_cts!.Token);
                worker.Join();
                _uiThread = null;
            }

            lock (_gate) { _uiThread = null; }
            scriptError?.Throw();
        }

        /// <summary>
        /// Starts a message loop on a background thread and returns immediately, leaving the
        /// calling thread free. Use this only where <see cref="Run"/> does not fit — a REPL or a
        /// notebook, where the calling code cannot be wrapped in a lambda.
        /// </summary>
        /// <param name="exitWhenAllWindowsClosed">
        /// When <see langword="true"/> (the default), closing the last window opened through
        /// <see cref="Show"/> also stops the loop. Pass <see langword="false"/> to keep the loop
        /// alive so that further windows can be opened later; call <see cref="Shutdown"/> to stop it.
        /// </param>
        /// <param name="configure">Optional Avalonia backend setup; see <see cref="Run"/>.</param>
        /// <remarks>
        /// <b>Not supported on macOS.</b> AppKit requires the event loop to own the process's main
        /// thread, which is exactly what this method declines to take. Use <see cref="Run"/> there —
        /// and prefer it everywhere if portability matters.
        /// </remarks>
        /// <exception cref="PlatformNotSupportedException">Called on macOS.</exception>
        /// <exception cref="InvalidOperationException">
        /// An Avalonia application is already initialized in this process.
        /// </exception>
        public static void Start(bool exitWhenAllWindowsClosed = true, Func<AppBuilder, AppBuilder>? configure = null)
        {
            if (OperatingSystem.IsMacOS())
                throw new PlatformNotSupportedException(
                    $"{nameof(Start)} cannot run the message loop off the main thread on macOS, " +
                    $"because AppKit requires it there. Use {nameof(MxPlotScriptHost)}.{nameof(Run)} instead.");

            EnsureNotAlreadyHosted();
            _configure = configure;

            lock (_gate)
            {
                if (IsRunning) return;
                _cts = new CancellationTokenSource();
                _ready = new ManualResetEventSlim(false);
                _openWindows = 0;
                // Start() has no script whose completion could end the wait, so the "last window
                // closed" rule is the only exit condition -- and only when asked for.
                _scriptFinished = exitWhenAllWindowsClosed;
                _uiThread = StartLoopThread();
            }
            _ready!.Wait();
        }

        /// <summary>Blocks the calling thread until the loop stops. No-op when nothing is running.</summary>
        public static void WaitForExit()
        {
            var t = _uiThread;
            if (t != null && t != Thread.CurrentThread) t.Join();
        }

        /// <summary>
        /// Closes every window opened through <see cref="Show"/> and stops the loop.
        /// Safe to call from any thread, and from inside a <see cref="Run"/> script.
        /// </summary>
        public static void Shutdown()
        {
            if (!IsRunning) return;
            Dispatcher.UIThread.Invoke(() =>
            {
                // Snapshot: Close raises Closed, which removes the entry from _tracked.
                foreach (var w in _tracked.ToArray()) w.Close();
            });
            RequestStop();
        }

        // ── Window and UI-thread access ───────────────────────────────────────

        /// <summary>
        /// Creates a <see cref="MatrixPlotter"/> on the UI thread, shows it, and returns it.
        /// Safe to call from the script thread.
        /// </summary>
        /// <param name="data">The data to display.</param>
        /// <param name="lut">Initial lookup table. Defaults to <see cref="ColorThemes.Grayscale"/>.</param>
        /// <param name="title">Window title. Defaults to a string derived from the data dimensions.</param>
        /// <param name="sourcePath">
        /// Optional path or sentinel controlling save/close behaviour — see
        /// <see cref="MatrixPlotter.Create"/>.
        /// </param>
        /// <param name="confirmOnClose">
        /// Whether closing the window asks to save first. Data handed to a plotter counts as
        /// unsaved until it is written somewhere, so the prompt appears even without a
        /// <paramref name="sourcePath"/>. Pass <see langword="false"/> for an unattended script
        /// that closes its own windows and would otherwise stall on the dialog.
        /// </param>
        /// <remarks>
        /// The returned plotter belongs to the UI thread. Reading <i>or</i> writing its properties
        /// from the script thread throws, because they reach Avalonia's thread-affine property
        /// system; wrap both in <see cref="Invoke(Action)"/> or <see cref="Invoke{T}(Func{T})"/>.
        /// The <see cref="IMatrixData"/> itself is not thread-affine and can be read directly.
        /// </remarks>
        /// <example>
        /// <code>
        /// var plotter = MxPlotScriptHost.Show(data, ColorThemes.Jet, "Scan 001");
        /// var lutName = MxPlotScriptHost.Invoke(() => plotter.Lut?.Name);   // read via Invoke
        /// int frames  = data.FrameCount;                                   // Core: read directly
        /// </code>
        /// </example>
        /// <exception cref="InvalidOperationException">No loop is running.</exception>
        public static MatrixPlotter Show(
            IMatrixData data, LookupTable? lut = null, string? title = null, string? sourcePath = null,
            bool confirmOnClose = true)
        {
            ArgumentNullException.ThrowIfNull(data);
            EnsureRunning();

            return Dispatcher.UIThread.Invoke(() =>
            {
                var plotter = MatrixPlotter.Create(data, lut, title, sourcePath);
                plotter.SuppressCloseConfirmation = !confirmOnClose;
                _tracked.Add(plotter);
                Interlocked.Increment(ref _openWindows);
                plotter.Closed += OnTrackedWindowClosed;
                plotter.Show();
                return plotter;
            });
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread and waits for it to finish.</summary>
        /// <exception cref="InvalidOperationException">No loop is running.</exception>
        public static void Invoke(Action action)
        {
            EnsureRunning();
            Dispatcher.UIThread.Invoke(action);
        }

        /// <summary>Runs <paramref name="func"/> on the UI thread and returns its result.</summary>
        /// <exception cref="InvalidOperationException">No loop is running.</exception>
        public static T Invoke<T>(Func<T> func)
        {
            EnsureRunning();
            return Dispatcher.UIThread.Invoke(func);
        }

        /// <summary>Queues <paramref name="action"/> on the UI thread without waiting for it.</summary>
        /// <exception cref="InvalidOperationException">No loop is running.</exception>
        public static Task InvokeAsync(Action action)
        {
            EnsureRunning();
            return Dispatcher.UIThread.InvokeAsync(action).GetTask();
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private static Thread StartLoopThread()
        {
            var thread = new Thread(() =>
            {
                ConfigureAvalonia();
                Dispatcher.UIThread.Post(() => _ready!.Set());
                Dispatcher.UIThread.MainLoop(_cts!.Token);
            })
            { Name = "MxPlot UI" };

            if (OperatingSystem.IsWindows())
                thread.SetApartmentState(ApartmentState.STA);

            thread.Start();
            return thread;
        }

        private static void ConfigureAvalonia()
        {
            _hasHosted = true;
            var builder = AppBuilder.Configure<MxPlotHostApplication>();
            builder = (_configure ?? UsePlatformDetectByReflection)(builder);
            builder.SetupWithoutStarting();
        }

        /// <summary>
        /// Calls <c>UsePlatformDetect()</c> without linking against it.
        /// </summary>
        /// <remarks>
        /// That extension lives in <c>Avalonia.Desktop</c>, which bundles the Win32, macOS and X11
        /// backends. This library deliberately does not reference it: a WinForms or WPF host wants
        /// <c>Avalonia.Win32</c> + <c>Avalonia.Skia</c> alone and should not be made to carry three
        /// backends (nor the transitive <c>Tmds.DBus.Protocol</c>) to embed a plotter. A script, on
        /// the other hand, wants the one line that works everywhere. Looking the method up at
        /// runtime serves both: reference <c>Avalonia.Desktop</c> and it is found, or pass an
        /// explicit <c>configure</c> callback and this is never reached.
        /// </remarks>
        private static AppBuilder UsePlatformDetectByReflection(AppBuilder builder)
        {
            var type = Type.GetType("Avalonia.AppBuilderDesktopExtensions, Avalonia.Desktop", throwOnError: false);
            var method = type?.GetMethod("UsePlatformDetect", BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                throw new InvalidOperationException(
                    "No Avalonia platform backend was configured. Add the Avalonia.Desktop package " +
                    "(which supplies UsePlatformDetect for Windows, macOS and Linux), or pass a " +
                    "configure callback, e.g. " +
                    $"{nameof(MxPlotScriptHost)}.{nameof(Run)}(script, b => b.UseWin32().UseSkia()).");

            return (AppBuilder)method.Invoke(null, new object[] { builder })!;
        }

        private static void OnTrackedWindowClosed(object? sender, EventArgs e)
        {
            if (sender is MatrixPlotter p) { p.Closed -= OnTrackedWindowClosed; _tracked.Remove(p); }
            if (Interlocked.Decrement(ref _openWindows) != 0) return;
            if (!Volatile.Read(ref _scriptFinished)) return;   // more windows may still be coming
            RequestStop();
        }

        /// <summary>
        /// Stops the loop, but only after the messages already queued have drained.
        /// </summary>
        /// <remarks>
        /// Cancelling inline races with Win32 tearing down the window's <c>HWND</c>: the
        /// destruction messages arrive after the dispatcher has gone, and Avalonia's thread check
        /// fails from inside <c>WndProc</c>. Posting at <see cref="DispatcherPriority.Background"/>
        /// lets that teardown finish first.
        /// </remarks>
        private static void RequestStop()
        {
            var cts = _cts;
            if (cts == null || cts.IsCancellationRequested) return;
            Dispatcher.UIThread.Post(() => cts.Cancel(), DispatcherPriority.Background);
        }

        /// <summary>
        /// The windows opened through <see cref="Show"/> that are still open. Only ever touched on
        /// the UI thread (<see cref="Show"/> runs there, and <see cref="Shutdown"/> reaches it
        /// through <c>Invoke</c>), so it needs no lock of its own.
        /// </summary>
        private static readonly List<MatrixPlotter> _tracked = new();

        private static void EnsureRunning()
        {
            if (!IsRunning)
                throw new InvalidOperationException(
                    $"No message loop is running. Call {nameof(MxPlotScriptHost)}.{nameof(Run)} " +
                    $"or {nameof(MxPlotScriptHost)}.{nameof(Start)} first.");
        }

        private static void EnsureNotAlreadyHosted()
        {
            // Avalonia is a process-wide singleton: SetupWithoutStarting can run once, and the
            // Dispatcher it creates stays bound to that first thread even after the loop ends.
            // A second session would therefore marshal onto a thread that no longer exists.
            if (_hasHosted)
                throw new InvalidOperationException(
                    $"{nameof(MxPlotScriptHost)} has already hosted a session in this process, and " +
                    $"Avalonia cannot be initialized twice. One {nameof(Run)} or {nameof(Start)} " +
                    $"per process is the limit — open every window you need from within a single " +
                    $"session, or run a second process.");

            if (Application.Current != null)
                throw new InvalidOperationException(
                    $"An Avalonia application is already initialized in this process, so " +
                    $"{nameof(MxPlotScriptHost)} would start a second message loop and break " +
                    $"thread affinity. This class is for scripts and console tools only — a " +
                    $"WinForms, WPF, or Avalonia host should configure " +
                    $"{nameof(MxPlotHostApplication)} itself and call " +
                    $"{nameof(MatrixPlotter)}.{nameof(MatrixPlotter.Create)} directly. See the " +
                    $"WinForms / WPF Integration Guide.");
        }
    }
}
