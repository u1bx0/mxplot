using Avalonia.Controls;
using System;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── Startup welcome animation ────────────────────────────────────────
        //
        // A one-time reveal on first launch: the empty-state hint (logo + "Drop files here")
        // fades in once the window is shown, rather than appearing instantly with everything
        // else. (An earlier version also grew the window's height on open, but by the time the
        // window actually painted on screen it was already mid-animation at some arbitrary
        // intermediate height -- confusing rather than a clean "reveal" -- so that part was
        // dropped; height stays fixed at its AXAML-declared value throughout.)

        /// <summary>Call once from the constructor, before the window is shown.</summary>
        private void SetupWelcomeAnimation()
        {
            Opened += OnWelcomeAnimationOpened;
        }

        private async void OnWelcomeAnimationOpened(object? sender, EventArgs e)
        {
            Opened -= OnWelcomeAnimationOpened;
            var hint = this.FindControl<StackPanel>("EmptyStateHint");
            if (hint != null)
                await FadeInAsync(hint, 750);
        }

        private async Task FadeInAsync(Control target, int durationMs)
        {
            const int steps = 20;
            for (int i = 1; i <= steps; i++)
            {
                target.Opacity = i / (double)steps;
                await Task.Delay(durationMs / steps);
            }
            target.Opacity = 1;
        }
    }
}
