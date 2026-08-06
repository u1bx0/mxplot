using System;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        /// <summary>
        /// Defines the logical execution contexts used by <see cref="ReentrancyGuard"/>
        /// to suppress re-entrant event handling within <see cref="MatrixPlotter"/>.
        /// <para>
        /// Before adding a new flag, verify that an existing category does not already
        /// cover the intended use case. Do <b>not</b> introduce bare <c>bool</c> fields
        /// for suppression purposes — use <see cref="ReentrancyGuard.Begin"/> instead.
        /// </para>
        /// </summary>
        [Flags]
        private enum GuardContext
        {
            /// <summary>No context is active.</summary>
            None = 0,

            /// <summary>
            /// A programmatic UI update is in progress.
            /// Prevents feedback loops caused by two-way bindings between UI components
            /// (e.g. <c>RadioButton</c> ↔ <c>ValueRangeBar</c>, metadata <c>ListBox</c> selection).
            /// </summary>
            UiSync = 1 << 0,

            /// <summary>
            /// <see cref="SetMatrixData"/> or a related initialization sequence is running.
            /// Suppresses dirty-flag propagation, LUT/VR snapshot updates, and other
            /// side-effects that must not fire while the view is being built from scratch.
            /// </summary>
            Initializing = 1 << 1,

            /// <summary>
            /// An inbound synchronization from an external <see cref="MatrixPlotterSyncGroup"/>
            /// is being applied. Prevents the resulting local state changes from being
            /// re-broadcast back to the group and causing a synchronization loop.
            /// </summary>
            SyncApply = 1 << 2,

            /// <summary>
            /// A continuous or asynchronous operation is in progress
            /// (e.g. <see cref="Refresh"/> execution, axis-tracker drag).
            /// Guards against overlapping invocations of the same operation.
            /// </summary>
            Operating = 1 << 3,
        }

        /// <summary>
        /// A lightweight, scope-based reentrancy guard for <see cref="MatrixPlotter"/>.
        /// <para>
        /// Consolidates all suppression flags that were previously scattered as individual
        /// <c>bool</c> fields (e.g. <c>_suppressModeSync</c>, <c>_initializingData</c>)
        /// into a single object keyed by <see cref="GuardContext"/>.
        /// </para>
        /// <para>
        /// Acquire a guard scope with <see cref="Begin"/>; the scope is released automatically
        /// when the returned <see cref="IDisposable"/> is disposed — even if an exception occurs.
        /// Nested acquisitions of the same context are safe: the context remains active until
        /// the outermost scope is disposed.
        /// </para>
        /// <para>
        /// All accesses are assumed to occur on the UI thread. This class is
        /// <b>not</b> thread-safe and does not need to be.
        /// </para>
        /// </summary>
        /// <example>
        /// <code>
        /// // Suppress UI feedback loop while programmatically updating radio buttons.
        /// using var _ = _reentrancy.Begin(GuardContext.UiSync);
        /// _autoRadio.IsChecked = false;
        /// _fixedRadio.IsChecked = true;
        ///
        /// // In the event handler:
        /// if (_reentrancy.IsActive(GuardContext.UiSync)) return;
        /// </code>
        /// </example>
        private sealed class ReentrancyGuard
        {
            private GuardContext _active;

            /// <summary>
            /// Returns <c>true</c> if any of the specified contexts are currently active.
            /// </summary>
            /// <param name="ctx">
            /// One or more <see cref="GuardContext"/> values (combinable with <c>|</c>).
            /// </param>
            public bool IsActive(GuardContext ctx)
                => (_active & ctx) != 0;

            /// <summary>
            /// Returns <c>true</c> if any context is currently active.
            /// </summary>
            public bool IsAnyActive()
                => _active != GuardContext.None;

            /// <summary>
            /// Activates the specified <paramref name="ctx"/> and returns a scope handle
            /// that deactivates it when disposed.
            /// <para>
            /// If the context is already active (nested call), the existing activation is
            /// preserved and the returned scope is a no-op on dispose.
            /// </para>
            /// </summary>
            /// <param name="ctx">The context to activate.</param>
            /// <returns>
            /// An <see cref="IDisposable"/> that deactivates <paramref name="ctx"/>
            /// when disposed, unless it was already active before this call.
            /// </returns>
            public IDisposable Begin(GuardContext ctx)
            {
                bool wasActive = IsActive(ctx);
                _active |= ctx;
                return new GuardScope(() =>
                {
                    if (!wasActive)
                        _active &= ~ctx;
                });
            }

            /// <summary>
            /// Scope handle returned by <see cref="Begin"/>.
            /// Calls the provided cleanup delegate exactly once on <see cref="Dispose"/>.
            /// </summary>
            private sealed class GuardScope(Action onDispose) : IDisposable
            {
                private bool _disposed;

                /// <inheritdoc/>
                public void Dispose()
                {
                    if (_disposed) return;
                    _disposed = true;
                    onDispose();
                }
            }
        }

        /// <summary>
        /// The single reentrancy guard instance for this <see cref="MatrixPlotter"/>.
        /// Replaces all individual suppression flags such as <c>_suppressModeSync</c>,
        /// <c>_initializingData</c>, <c>_syncApplying</c>, and <c>_isRefreshing</c>.
        /// <para>
        /// Always access via <see cref="ReentrancyGuard.Begin"/> with a <c>using</c> statement
        /// to ensure the scope is released even when exceptions occur.
        /// </para>
        /// </summary>
        private readonly ReentrancyGuard _reentrancy = new();
    }
}