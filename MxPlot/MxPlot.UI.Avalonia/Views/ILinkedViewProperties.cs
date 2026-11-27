using Avalonia.Controls;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// The editable settings of what a linked view computes. A window that follows its source through a
    /// link carrying one offers a menu item that opens them; once they are changed, the window is recomputed.
    /// </summary>
    internal interface ILinkedViewProperties
    {
        /// <summary>The text of the menu item that opens the settings, e.g. <c>Edit Formula…</c>.</summary>
        string MenuLabel { get; }

        /// <summary>
        /// Lets the user edit the settings in a dialog owned by <paramref name="owner"/>. Returns whether
        /// they changed. A change must leave the shape of the computed data as it was.
        /// </summary>
        Task<bool> EditAsync(Window owner);
    }
}
