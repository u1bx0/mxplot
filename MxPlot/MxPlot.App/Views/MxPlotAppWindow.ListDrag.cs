using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MxPlot.App.ViewModels;
using System.Linq;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── Drag-to-reorder for the window list ───────────────────────────
        //
        // The gesture itself lives in ListDragReorder; this file only supplies the dashboard's
        // answers to the questions it asks.

        private ListDragReorder _listDrag = null!;

        private void InitializeListDrag()
        {
            var overlay = this.FindControl<Canvas>("ListDragOverlay")!;

            _listDrag = new ListDragReorder(
                list: _windowList,
                overlay: overlay,
                canStart: () =>
                    // Sync freezes the list, in step with its other input handlers, and a drag
                    // during an inline rename would pull the row out from under the edit box.
                    !_isSyncActive && !ViewModel.ManagedWindows.Any(m => m.IsRenaming),
                getParent: item => (item as WindowListItemViewModel)?.ParentItem,
                isWrapped: () => _viewMode == ViewMode.Icons,
                applyDrop: (item, slot) =>
                {
                    if (item is WindowListItemViewModel vm)
                        ReorderList(() => ViewModel.MoveSiblingToSlot(vm, slot));
                });

            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (!_listDrag.IsDragging || e.Key != Key.Escape) return;
                _listDrag.Cancel();
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
        }
    }
}
