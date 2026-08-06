using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── View mode types and data ──────────────────────────────────────

        private enum ViewMode { Details, Icons }

        private static readonly (double Outer, double Thumb, double Icon)[] CardSizeSteps =
        [
            (54,  42, 22),
            (66,  52, 28),
            (76,  62, 34),
            (96,  80, 44),
            (116, 96, 54),
            (140, 120, 66),
            (160, 146, 80),
        ];

        // ── View mode switching ───────────────────────────────────────────

        /// <summary>
        /// Switches between Details (list) and Icons (grid) view modes for the window list.
        /// Updates UI templates, button states, and ViewModel accordingly.
        /// </summary>
        private void ApplyViewMode(ViewMode mode)
        {
            _viewMode = mode;
            if (mode == ViewMode.Icons)
            {
                _windowList.ItemTemplate = (IDataTemplate)Resources["IconTemplate"]!;
                _windowList.ItemsPanel = (ITemplate<Panel>)Resources["IconsPanel"]!;
                if (!_windowList.Classes.Contains("IconView"))
                    _windowList.Classes.Add("IconView");
                _viewDetailsBtn.Opacity = 0.35;
                _viewIconsBtn.Opacity = 1.0;
                ViewModel.IsIconView = true;
            }
            else
            {
                _windowList.ItemTemplate = (IDataTemplate)Resources["DetailsTemplate"]!;
                _windowList.ItemsPanel = (ITemplate<Panel>)Resources["DetailsPanel"]!;
                _windowList.Classes.Remove("IconView");
                _viewDetailsBtn.Opacity = 1.0;
                _viewIconsBtn.Opacity = 0.35;
                ViewModel.IsIconView = false;
            }
        }

        /// <summary>
        /// Applies a card size step to the icon view, updating resource dictionary values
        /// for GridCardOuter, GridCardThumb, and GridCardIcon dimensions.
        /// </summary>
        private void ApplyCardSize(int step)
        {
            if (_applyingCardSize) return;
            _applyingCardSize = true;
            _cardSizeStep = step;
            var (outer, thumb, icon) = CardSizeSteps[step];
            Resources["GridCardOuter"] = outer;
            Resources["GridCardThumb"] = thumb;
            Resources["GridCardIcon"] = icon;
            if ((int)_cardSizeSlider.Value != step)
                _cardSizeSlider.Value = step;
            _applyingCardSize = false;
        }

        /// <summary>
        /// Wires up view mode toggle buttons and card size controls in the constructor.
        /// Should be called during window initialization.
        /// </summary>
        private void InitializeViewModeControls()
        {
            _viewDetailsBtn = this.FindControl<Button>("ViewDetailsBtn")!;
            _viewIconsBtn = this.FindControl<Button>("ViewIconsBtn")!;
            _viewDetailsBtn.Click += (_, _) => ApplyViewMode(ViewMode.Details);
            _viewIconsBtn.Click += (_, _) => ApplyViewMode(ViewMode.Icons);

            _cardSizeSlider = this.FindControl<Slider>("CardSizeSlider")!;
            _cardSizeSlider.ValueChanged += (_, e) => ApplyCardSize((int)e.NewValue);

            _windowList.PointerWheelChanged += (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _viewMode == ViewMode.Icons)
                {
                    e.Handled = true;
                    var delta = e.Delta.Y > 0 ? 1 : -1;
                    ApplyCardSize(Math.Clamp(_cardSizeStep + delta, 0, CardSizeSteps.Length - 1));
                }
            };
        }
    }
}
