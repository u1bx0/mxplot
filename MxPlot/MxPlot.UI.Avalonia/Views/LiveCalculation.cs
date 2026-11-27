using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Formula;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// A result window kept up to date with the windows its formula reads: whenever one of them is redrawn,
    /// changes its frame, changes its value mode or has its data replaced, the formula is evaluated again into the
    /// result. The result is one frame: each input contributes the frame it shows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The result is written in place and redrawn, so its data instance stays the same for the life of the link. While
    /// the link lasts, "Replace data" is not offered for the result and closing it does not ask about unsaved changes,
    /// since it is recomputed from its inputs.
    /// </para>
    /// <para>
    /// The link ends when the result or any input closes, or when <see cref="End"/> is called. The result then
    /// stays as an ordinary window with the data it has. An input whose data is replaced by data of another size does
    /// not end it: the result keeps its grid, and what an input does not reach is NaN.
    /// </para>
    /// <para>
    /// A result may be an input of another link, but not of itself, directly or through other links.
    /// </para>
    /// </remarks>
    public sealed class LiveCalculation
    {
        // Every link in progress; used from the UI thread only.
        private static readonly List<LiveCalculation> Active = [];

        private sealed class InputLink
        {
            public required MatrixPlotter Plotter;
            public required EventHandler Refreshed;
            public required EventHandler<IMatrixData?> DataChanged;
            public required EventHandler ValueModeChanged;
            public required EventHandler Closed;
            public required EventHandler ActiveIndexChanged;

            /// <summary>The data <see cref="ActiveIndexChanged"/> is attached to.</summary>
            public IMatrixData? Data;
        }

        private readonly List<InputLink> _links = [];
        private readonly CompiledFormula _formula;
        private readonly EventHandler _resultClosed;
        private readonly bool _previousAllowDataReplace;
        private readonly bool _previousSecondary;
        private bool _running;
        private bool _dirty;
        private bool _ended;

        /// <summary>The window that shows the result.</summary>
        public MatrixPlotter Result { get; }

        /// <summary>The windows the formula reads, as <c>m1</c>, <c>m2</c>, ... in this order.</summary>
        public IReadOnlyList<MatrixPlotter> Inputs { get; }

        /// <summary>The formula, as written.</summary>
        public string Formula => _formula.Text;

        /// <summary>Whether the result is still kept up to date.</summary>
        public bool IsActive => !_ended;

        /// <summary>Raised once, when the link ends.</summary>
        public event EventHandler? Ended;

        private LiveCalculation(MatrixPlotter result, IReadOnlyList<MatrixPlotter> inputs, CompiledFormula formula)
        {
            Result = result;
            Inputs = inputs;
            _formula = formula;
            _previousAllowDataReplace = result.AllowDataReplace;
            _previousSecondary = result.IsSecondaryWindow;
            _resultClosed = (_, _) => End();
        }

        /// <summary>
        /// Starts keeping <paramref name="result"/> up to date. The result must already hold data of the grid the
        /// formula is to fill (<see cref="MatrixData{T}"/> of double, one frame).
        /// </summary>
        /// <exception cref="InvalidOperationException">An input is the result or depends on it.</exception>
        internal static LiveCalculation Start(MatrixPlotter result, IReadOnlyList<MatrixPlotter> inputs, CompiledFormula formula)
        {
            ArgumentNullException.ThrowIfNull(result);
            ArgumentNullException.ThrowIfNull(inputs);
            ArgumentNullException.ThrowIfNull(formula);
            if (formula.References.Count != inputs.Count)
                throw new ArgumentException("The number of inputs does not match the formula.", nameof(inputs));
            if (WouldCycle(result, inputs))
                throw new InvalidOperationException("An input depends on the result.");

            var calc = new LiveCalculation(result, inputs.ToArray(), formula);
            calc.Attach();
            return calc;
        }

        /// <summary>
        /// Whether keeping <paramref name="result"/> up to date from <paramref name="inputs"/> would make it
        /// depend on itself: an input is the result, or is itself kept up to date from it, directly or through others.
        /// </summary>
        internal static bool WouldCycle(MatrixPlotter result, IEnumerable<MatrixPlotter> inputs)
            => inputs.Any(input => DependsOn(input, result, []));

        private static bool DependsOn(MatrixPlotter plotter, MatrixPlotter target, HashSet<MatrixPlotter> seen)
        {
            if (ReferenceEquals(plotter, target)) return true;
            if (!seen.Add(plotter)) return false;

            foreach (var calc in Active.ToArray())
            {
                if (ReferenceEquals(calc.Result, plotter) && calc._links.Any(l => DependsOn(l.Plotter, target, seen)))
                    return true;
            }
            return false;
        }

        private void Attach()
        {
            Active.Add(this);
            Result.AllowDataReplace = false;
            Result.IsSecondaryWindow = true;
            Result.Closed += _resultClosed;

            foreach (var plotter in Inputs)
            {
                var link = new InputLink
                {
                    Plotter = plotter,
                    Refreshed = (_, _) => Trigger(),
                    DataChanged = null!,
                    ValueModeChanged = (_, _) => Trigger(),
                    Closed = (_, _) => End(),
                    ActiveIndexChanged = (_, _) => Trigger(),
                };
                // The data instance can be replaced (a window that follows its source gets a new one on every
                // update), and the frame is a property of the instance, so the subscription follows it.
                link.DataChanged = (_, data) =>
                {
                    Reattach(link, data);
                    Trigger();
                };
                _links.Add(link);

                plotter.Refreshed += link.Refreshed;
                plotter.MatrixDataChanged += link.DataChanged;
                plotter.ComplexValueModeChanged += link.ValueModeChanged;
                plotter.Closed += link.Closed;
                Reattach(link, plotter.MatrixData);
            }
        }

        private static void Reattach(InputLink link, IMatrixData? data)
        {
            if (link.Data != null) link.Data.ActiveIndexChanged -= link.ActiveIndexChanged;
            link.Data = data;
            if (data != null) data.ActiveIndexChanged += link.ActiveIndexChanged;
        }

        /// <summary>
        /// Ends the link: the result stays as an ordinary window with the data it has. Does nothing when it has
        /// already ended.
        /// </summary>
        public void End()
        {
            if (_ended) return;
            _ended = true;
            Active.Remove(this);

            foreach (var link in _links)
            {
                link.Plotter.Refreshed -= link.Refreshed;
                link.Plotter.MatrixDataChanged -= link.DataChanged;
                link.Plotter.ComplexValueModeChanged -= link.ValueModeChanged;
                link.Plotter.Closed -= link.Closed;
                if (link.Data != null) link.Data.ActiveIndexChanged -= link.ActiveIndexChanged;
                link.Data = null;
            }
            _links.Clear();

            Result.Closed -= _resultClosed;
            Result.AllowDataReplace = _previousAllowDataReplace;
            Result.IsSecondaryWindow = _previousSecondary;
            Ended?.Invoke(this, EventArgs.Empty);
        }

        // ── Recomputing ───────────────────────────────────────────────────────

        /// <summary>
        /// Asks for the result to be recomputed. While one evaluation runs, further requests are folded into one
        /// that runs after it with whatever the inputs show by then, so a fast source never queues work.
        /// </summary>
        private void Trigger()
        {
            if (_ended) return;
            // ActiveIndexChanged is raised by whichever thread changes the frame.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(Trigger);
                return;
            }
            if (_running)
            {
                _dirty = true;
                return;
            }
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            if (Result.MatrixData is not MatrixData<double> target)
            {
                End();
                return;
            }

            // Read on the UI thread: the data each input has and the value mode it shows at this moment.
            var references = new List<FormulaReference>(_links.Count);
            foreach (var link in _links)
            {
                if (link.Plotter.MatrixData is not { } data) return;
                references.Add(new FormulaReference(data, link.Plotter.DisplayedValueMode));
            }

            _running = true;
            _dirty = false;
            try
            {
                await Task.Run(() => FormulaEvaluator.FillReferences(target, _formula, references));
                if (_ended) return;

                // The values were written into the buffer behind the window's back: its range is stale.
                target.Invalidate(0);
                Result.Refresh();
            }
            catch (Exception)
            {
                // A failed evaluation leaves the last result in place; the next change tries again.
            }
            finally
            {
                _running = false;
            }

            if (_dirty && !_ended) Trigger();
        }
    }
}
