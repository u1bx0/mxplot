using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MxPlot.Core.IO;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    public partial class MxPlotAppWindow
    {
        // ── File open helpers ────────────────────────────────────────────

        /// <summary>
        /// Temporarily suspends <see cref="Window.Topmost"/> while awaiting a dialog so that
        /// the dialog is not obscured by the always-on-top main window.
        /// </summary>
        private async Task<T> WithTopmostSuspended<T>(
            System.Func<Task<T>> action)
        {
            bool wasTopmost = Topmost;
            Topmost = false;
            try { return await action(); }
            finally { Topmost = wasTopmost; }
        }

        /// <summary>
        /// Places a newly created plot window in the wider side of the screen relative to
        /// this dashboard window, with a cascade offset so multiple windows don't stack exactly.
        /// </summary>
        private void PositionNewWindow(Window window, int windowIndex)
        {
            const double Gap = 16.0;
            const double CascadeStep = 28.0;

            // Find the screen that contains this dashboard window.
            var screen = Screens.All.FirstOrDefault(s =>
                Position.X >= s.Bounds.X && Position.X < s.Bounds.X + s.Bounds.Width &&
                Position.Y >= s.Bounds.Y && Position.Y < s.Bounds.Y + s.Bounds.Height)
                ?? Screens.Primary;
            if (screen == null) return;

            double sc = screen.Scaling;
            var wb = screen.WorkingArea;

            // Convert everything to DIPs for easy arithmetic.
            var screenRect = new Rect(wb.X / sc, wb.Y / sc, wb.Width / sc, wb.Height / sc);
            var dashRect = new Rect(Position.X / sc, Position.Y / sc, Bounds.Width, Bounds.Height);

            // Determine the available side: use whichever side of the dashboard is wider.
            var available = screenRect;
            if (dashRect.Intersects(screenRect))
            {
                double rightSpace = screenRect.Right - dashRect.Right - Gap;
                double leftSpace = dashRect.Left - screenRect.X - Gap;
                if (rightSpace >= leftSpace)
                    available = new Rect(dashRect.Right + Gap, screenRect.Y, rightSpace, screenRect.Height);
                else
                    available = new Rect(screenRect.X, screenRect.Y, leftSpace, screenRect.Height);
            }

            // Fallback: use full screen if the computed side is too narrow.
            if (available.Width < 200 || available.Height < 200)
                available = screenRect;

            // Cascade: wrap within the available region so windows don't drift off screen.
            double maxStep = System.Math.Max(0, System.Math.Min(available.Width, available.Height) - 200);
            double step = maxStep > 0 ? CascadeStep * windowIndex % maxStep : 0;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(
                (int)((available.X + Gap / 2 + step) * sc),
                (int)((available.Y + Gap / 2 + step) * sc));
        }

        /// <summary>Shows the file picker dialog and loads selected files.</summary>
        private async Task OpenFileViaDialogAsync()
        {
            var descriptors = FormatRegistry.ReaderDescriptors;
            var perFormat = descriptors
                .Select(d => new FilePickerFileType(d.FormatName) { Patterns = d.DialogPatterns.ToList() })
                .ToList();

            var allPatterns = descriptors.SelectMany(d => d.DialogPatterns).Distinct().ToList();
            var allSupported = new FilePickerFileType("All Supported Files") { Patterns = allPatterns };

            var fileTypes = new List<FilePickerFileType> { allSupported };
            fileTypes.AddRange(perFormat);

            var files = await WithTopmostSuspended(() =>
                StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Open File",
                    AllowMultiple = true,
                    FileTypeFilter = fileTypes,
                }));

            foreach (var file in files)
            {
                var path = file.TryGetLocalPath();
                if (path != null)
                    await ViewModel.LoadAndOpenFileAsync(path, this);
            }
        }

        /// <summary>
        /// For large files, asks the user to choose between InMemory and Virtual loading.
        /// Returns null if the user cancels.
        /// </summary>
        internal async Task<LoadingMode?> ResolveLoadingModeAsync(string path)
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length < LargeFileThresholdBytes)
                return LoadingMode.Auto;

            var reader = FormatRegistry.CreateReader(path);
            if (reader is not IVirtualLoadable)
                return LoadingMode.InMemory;

            return await WithTopmostSuspended(() =>
                LoadingModeDialog.ShowAsync(this, Path.GetFileName(path), fileInfo.Length));
        }
    }
}
