using System.Collections.Generic;

namespace MxPlot.UI.Avalonia.Formula
{
    /// <summary>
    /// The formulas used most recently, newest first, for the life of the application. A formula used
    /// again moves to the front instead of being listed twice. Used from the UI thread only.
    /// </summary>
    internal sealed class FormulaHistory
    {
        private readonly List<string> _items = [];
        private readonly int _capacity;

        /// <summary>The formulas applied by the Calculator command, in any window.</summary>
        public static FormulaHistory Calculator { get; } = new();

        /// <summary>The formulas used in the Create Data dialog.</summary>
        public static FormulaHistory CreateData { get; } = new();

        public FormulaHistory(int capacity = 20) => _capacity = capacity;

        /// <summary>The formulas, newest first.</summary>
        public IReadOnlyList<string> Items => _items;

        /// <summary>Puts <paramref name="formula"/> at the front. A blank formula is ignored.</summary>
        public void Add(string? formula)
        {
            formula = formula?.Trim();
            if (string.IsNullOrEmpty(formula)) return;

            _items.Remove(formula);
            _items.Insert(0, formula);
            if (_items.Count > _capacity) _items.RemoveRange(_capacity, _items.Count - _capacity);
        }

        /// <summary>Forgets every formula.</summary>
        public void Clear() => _items.Clear();
    }
}
