using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MxPlot.App.ViewModels;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── Window focus helper ───────────────────────────────────────────

        /// <summary>
        /// Called when a managed window gains focus.
        /// Updates the dashboard list selection to match the focused window,
        /// preserving multi-selection after operations like Tile.
        /// </summary>
        private void OnManagedWindowFocused(Window window)
        {
            if (_isSyncActive) return;
            // Ignore Activated fired by our own programmatic Show()/Activate() batch (dashboard
            // minimize/restore, or the list-driven activation below) rather than a real user click --
            // see _activatingFromList's remarks for why this guard has to cover the whole batch.
            if (_activatingFromList) return;
            var item = ViewModel.ManagedWindows.FirstOrDefault(m => m.Window == window);
            if (item == null) return;

            // If the focused window is already part of a multi-selection, keep the
            // selection as-is (e.g. after Tile repositions and activates windows).
            if (_windowList.SelectedItems?.Count > 1 &&
                _windowList.SelectedItems.Contains(item))
                return;

            _windowList.SelectedItem = item;
        }

        /// <summary>
        /// Called when a managed window's own content is actually clicked (as opposed to being
        /// activated via Alt+Tab, taskbar, or the dashboard list itself). Ctrl held toggles the
        /// window into/out of the current multi-selection; a plain click collapses the selection
        /// down to just this window (no-op if it is already the sole selection, so that ordinary
        /// interaction inside an already-solo-selected window doesn't keep re-touching selection).
        /// </summary>
        private void OnManagedWindowSelectionClicked(Window window, bool ctrlHeld)
        {
            if (_isSyncActive) return;
            var item = ViewModel.ManagedWindows.FirstOrDefault(m => m.Window == window);
            if (item == null) return;

            Debug.WriteLine($"[WinSelectTest] Click on '{window.Title}', Ctrl={ctrlHeld}, wasSelected={item.IsSelected}");

            if (ctrlHeld)
            {
                if (_windowList.SelectedItems!.Contains(item))
                    _windowList.SelectedItems.Remove(item);
                else
                    _windowList.SelectedItems.Add(item);
            }
            else if (!(_windowList.SelectedItems?.Count == 1 && ReferenceEquals(_windowList.SelectedItem, item)))
            {
                _windowList.SelectedItem = item;
            }
        }

        // ── Reordering the window list ────────────────────────────────────

        /// <summary>
        /// Slides the list's rows whenever their order changes. Created on first use, since the list
        /// it animates is resolved during window construction.
        /// </summary>
        private ListReorderAnimator? _listReorder;

        /// <summary>
        /// Applies a reordering of the window list: slides the rows to their new places and keeps
        /// the selection on the same items.
        /// </summary>
        /// <remarks>
        /// The selection has to be restored explicitly because a ListBox selects by position, not by
        /// item: after <c>ObservableCollection.Move</c> the selection is left sitting on whatever now
        /// occupies the old index, so a moved row comes out of a reorder unselected even though its
        /// window is still the active one.
        /// </remarks>
        internal void ReorderList(Action reorder)
        {
            var selected = _windowList.SelectedItems?.Cast<object>().ToList() ?? [];
            (_listReorder ??= new ListReorderAnimator(_windowList)).Run(reorder);
            RestoreListSelection(selected);
        }

        private void RestoreListSelection(List<object> wanted)
        {
            if (_windowList.SelectedItems is not { } current) return;

            bool unchanged = current.Count == wanted.Count;
            if (unchanged)
            {
                foreach (var item in wanted)
                {
                    if (current.Contains(item)) continue;
                    unchanged = false;
                    break;
                }
            }
            if (unchanged) return;

            // The SelectionChanged handler is suppressed for the repair itself: it re-activates the
            // single selected window and nudges it clear of the dashboard, which is not something
            // reordering a list should do.
            _processingSelectionChange = true;
            try
            {
                current.Clear();
                foreach (var item in wanted) current.Add(item);
            }
            finally
            {
                _processingSelectionChange = false;
            }

            foreach (var item in ViewModel.ManagedWindows)
                item.IsSelected = wanted.Contains(item);
            ViewModel.RefreshSelectionState();
        }

        // ── Window list context menu ──────────────────────────────────────

        /// <summary>
        /// Dynamically builds a context menu for the window list with Rename, Hide/Show, and Close options.
        /// Menu content adapts based on single vs. multi-selection and visibility states.
        /// </summary>
        private void OnWindowListContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            // No context menu during Sync mode, as selection is locked to the sync snapshot.
            if (_isSyncActive) return;

            // This ensures that the context menu actions apply to the clicked item even if it wasn't previously selected.
            var target = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (target?.DataContext is not WindowListItemViewModel clicked) return;

            if (!clicked.IsSelected)
            {
                _windowList.SelectedItem = clicked;
            }

            var selected = _windowList.SelectedItems?
                .OfType<WindowListItemViewModel>()
                .ToList() ?? [];
            if (selected.Count == 0) return;

            bool multi = selected.Count > 1;
            bool anyVisible = selected.Any(m => m.IsWindowVisible);
            bool anyHidden = selected.Any(m => !m.IsWindowVisible);

            var menu = new ContextMenu();

            // ── Rename (single selection only, visible window only) ────────────────
            if (!multi && clicked.IsWindowVisible)
            {
                var renameItem = new MenuItem { Header = "Rename" };
                renameItem.Click += (_, _) => clicked.RenameCommand.Execute(null);
                menu.Items.Add(renameItem);
            }

            // ── Sort by Name (scoped to the clicked item's sibling group only) ─────
            if (ViewModel.CanSortSiblings(clicked))
            {
                bool ascending = ViewModel.NextSortIsAscending(clicked);
                var sortItem = new MenuItem { Header = $"Sort by Name {(ascending ? "↑" : "↓")}" };
                sortItem.Click += (_, _) => ReorderList(() => ViewModel.SortSiblingsByName(clicked));
                menu.Items.Add(sortItem);
                menu.Items.Add(new Separator());
            }

            // ── Hide / Show ───────────────────────────────────────────────
            if (!multi)
            {
                // Single selection: Toggle based on current state
                var toggleItem = new MenuItem { Header = clicked.IsWindowVisible ? "Hide" : "Show" };
                toggleItem.Click += (_, _) => clicked.ToggleVisibility();
                menu.Items.Add(toggleItem);
            }
            else if (anyVisible && !anyHidden)
            {
                // All visible → Hide Selected
                var hideItem = new MenuItem { Header = "Hide Selected" };
                hideItem.Click += (_, _) =>
                {
                    foreach (var vm in selected.Where(m => m.IsWindowVisible))
                        vm.ToggleVisibility();
                };
                menu.Items.Add(hideItem);
            }
            else if (!anyVisible && anyHidden)
            {
                // All hidden → Show Selected
                var showItem = new MenuItem { Header = "Show Selected" };
                showItem.Click += (_, _) =>
                {
                    foreach (var vm in selected.Where(m => !m.IsWindowVisible))
                        vm.ToggleVisibility();
                };
                menu.Items.Add(showItem);
            }
            else
            {
                // Mixed → Hide Selected and Show Selected
                var hideItem = new MenuItem { Header = "Hide Selected" };
                hideItem.Click += (_, _) =>
                {
                    foreach (var vm in selected.Where(m => m.IsWindowVisible))
                        vm.ToggleVisibility();
                };
                var showItem = new MenuItem { Header = "Show Selected" };
                showItem.Click += (_, _) =>
                {
                    foreach (var vm in selected.Where(m => !m.IsWindowVisible))
                        vm.ToggleVisibility();
                };
                menu.Items.Add(hideItem);
                menu.Items.Add(showItem);
            }

            // ── Hide Others (visible window only, other visible windows exist) ────
            if (clicked.IsWindowVisible && ViewModel.ManagedWindows.Any(m => m != clicked && m.IsWindowVisible))
            {
                var hideOthersItem = new MenuItem { Header = "Hide Others" };
                hideOthersItem.Click += (_, _) =>
                {
                    foreach (var vm in ViewModel.ManagedWindows.Where(m => m != clicked && m.IsWindowVisible))
                        vm.ToggleVisibility();
                };
                menu.Items.Add(hideOthersItem);
            }

            // ── Close ─────────────────────────────────────────────────────
            menu.Items.Add(new Separator());
            var closeItem = new MenuItem { Header = multi ? "Close Selected" : "Close" };
            closeItem.Click += (_, _) =>
            {
                foreach (var vm in selected.ToList())
                    vm.Window.Close();
            };
            menu.Items.Add(closeItem);

            menu.Open(_windowList);
            e.Handled = true;
        }

        // ── Selection helpers ─────────────────────────────────────────────

        /// <summary>
        /// Removes hidden (non-visible) items from the ListBox selection and
        /// refreshes <see cref="MxPlotAppViewModel.RefreshSelectionState"/>.
        /// Should be called before Tile or Sync so that only visible windows participate.
        /// </summary>
        private void DeselectHiddenItems()
        {
            var toDeselect = _windowList.SelectedItems?
                .OfType<WindowListItemViewModel>()
                .Where(vm => !vm.IsWindowVisible)
                .ToList();
            if (toDeselect is not { Count: > 0 }) return;
            _processingSelectionChange = true;
            try
            {
                foreach (var vm in toDeselect)
                {
                    _windowList.SelectedItems!.Remove(vm);
                    vm.IsSelected = false;
                }
                ViewModel.RefreshSelectionState();
            }
            finally { _processingSelectionChange = false; }
        }
    }
}
