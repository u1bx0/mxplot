using System;
using System.Threading;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>
    /// A running progress display and its cancellation. Disposing it ends the display and releases the
    /// cancellation source, so the caller needs no try/finally of its own.
    /// </summary>
    internal sealed class ProgressSession : IDisposable
    {
        private readonly CancellationTokenSource? _cts;
        private readonly Action _end;
        private bool _disposed;

        internal ProgressSession(IProgress<int> progress, CancellationTokenSource? cts, Action end)
        {
            Progress = progress;
            _cts = cts;
            _end = end;
        }

        /// <summary>Reports progress to the status bar.</summary>
        public IProgress<int> Progress { get; }

        /// <summary>Cancelled by the Cancel button; <see cref="CancellationToken.None"/> when the session is not cancellable.</summary>
        public CancellationToken Token => _cts?.Token ?? default;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _end();
            _cts?.Dispose();
        }
    }
}
