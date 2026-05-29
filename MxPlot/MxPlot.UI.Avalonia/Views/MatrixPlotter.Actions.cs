using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.IO;
using MxPlot.UI.Avalonia.Actions;
using MxPlot.UI.Avalonia.Overlays;
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Window actions (File / Edit / About) ─────────────────────────────

        // ── Export framework ──────────────────────────────────────────────────

        /// <summary>
        /// Returns a <see cref="ExportFormatDescriptor"/> for the built-in single-frame PNG export.
        /// This is always present as the first item in the "Export as…" submenu.
        /// </summary>
        private ExportFormatDescriptor MakePngExportDescriptor()
        {
            return new ExportFormatDescriptor(
                Label: "PNG\u2026",
                Hint: "Exports the current frame as a PNG image.",
                FileTypeName: "PNG Image",
                FilePattern: "*.png",
                Exporter: async (path, _, _, _, _, _) =>
                {
                    await Task.CompletedTask; // SaveAsPng is synchronous
                    var (natW, natH) = _view.GetNaturalDims();
                    int w = Math.Max(1, (int)Math.Round(natW));
                    int h = Math.Max(1, (int)Math.Round(natH));
                    _view.SaveAsPng(path, w, h);
                },
                RequiresStack: false,
                DefaultFps: 0);
        }

        /// <summary>
        /// Opens a Save File dialog for <paramref name="desc"/> and runs the exporter.
        /// <para>
        /// For single-frame formats (<see cref="ExportFormatDescriptor.RequiresStack"/> is
        /// <c>false</c>) the <c>frameRenderer</c> delegate renders only the current frame.
        /// For stack formats it renders each frame in sequence at the natural view dimensions.
        /// </para>
        /// </summary>
        private async Task InvokeExportAsync(ExportFormatDescriptor desc)
        {
            var md = _view.MatrixData;
            if (md == null) return;

            int frameCount = desc.RequiresStack ? md.FrameCount : 1;
            int digits = md.FrameCount > 1 ? md.FrameCount.ToString().Length : 0;

            var baseName = Path.GetFileNameWithoutExtension(Title ?? "Image");
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Image";

            string suggestedName = !desc.RequiresStack && digits > 0
                ? baseName + $"_frame{_view.FrameIndex.ToString($"D{digits}")}"
                : baseName;

            var sp = StorageProvider;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Export as {desc.FileTypeName}",
                SuggestedFileName = suggestedName,
                FileTypeChoices = [new FilePickerFileType(desc.FileTypeName) { Patterns = [desc.FilePattern] }],
            });
            if (file == null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            var (natW, natH) = _view.GetNaturalDims();
            int w = Math.Max(1, (int)Math.Round(natW));
            int h = Math.Max(1, (int)Math.Round(natH));

            // frameRenderer: renders the given frame index to a BGRA32 byte array (via PNG round-trip).
            // Must be called on the UI thread. Used by injected stack exporters.
            byte[] FrameRenderer(int frameIndex)
            {
                _view.FrameIndex = frameIndex;
                var rtb = _view.RenderToBitmap(w, h);
                if (rtb == null) return [];
                using var ms = new MemoryStream();
                rtb.Save(ms);
                return ms.ToArray();
            }

            await desc.Exporter(path, FrameRenderer, frameCount, w, h, desc.DefaultFps);
        }

        /// <summary>
        /// Saves the current <see cref="IMatrixData"/> to a file chosen via SaveFilePickerAsync.
        /// File type choices are built dynamically from <see cref="FormatRegistry.WriterDescriptors"/>.
        /// The matching <see cref="IMatrixDataWriter"/> is created via <see cref="FormatRegistry.CreateWriter"/>.
        /// During save, a semi-transparent overlay with a progress bar covers the window.
        /// On completion, the window title is updated to the saved file name.
        /// </summary>
        private async Task SaveDataAsync() => await SaveAsAsync();

        /// <summary>
        /// Saves the current <see cref="IMatrixData"/> to <paramref name="path"/>.
        /// When <paramref name="path"/> is <c>null</c> (default), a Save File dialog is shown first.
        /// The format is inferred from the file extension via <see cref="FormatRegistry.CreateWriter"/>.
        /// During save, a progress bar overlay covers the window.
        /// On completion, the window title is updated to the saved file name (unless saving a copy
        /// of read-only virtual data).
        /// </summary>
        /// <param name="path">
        /// Destination file path, or <c>null</c> to open the Save File picker dialog.
        /// </param>
        /// <param name="ct">Cancellation token passed to the background save task.</param>
        public async Task SaveAsAsync(string? path = null, CancellationToken ct = default)
        {
            if (_currentData == null) return;

            if (path == null)
            {
                // ── Show Save File dialog to obtain the path ───────────────────────
                var descriptors = FormatRegistry.WriterDescriptors;
                var fileTypes = descriptors
                    .Select(d => new FilePickerFileType(d.FormatName) { Patterns = d.DialogPatterns.ToList() })
                    .ToList();
                var title = Title;
                string suggestedName = FormatRegistry.StripKnownExtension(title ?? "data");
                foreach (var c in Path.GetInvalidFileNameChars())
                    suggestedName = suggestedName.Replace(c, '_');

                // Pre-select the file type matching the original file extension.
                // Falls back to OME-TIFF if no match.
                string titleLower = (title ?? "").ToLowerInvariant();
                int bestIdx = -1;
                int bestLen = 0;
                for (int i = 0; i < descriptors.Count; i++)
                {
                    foreach (var ext in descriptors[i].Extensions)
                    {
                        if (titleLower.EndsWith(ext, StringComparison.Ordinal) && ext.Length > bestLen)
                        {
                            bestIdx = i;
                            bestLen = ext.Length;
                        }
                    }
                }
                if (bestIdx < 0)
                {
                    for (int i = 0; i < descriptors.Count; i++)
                    {
                        if (descriptors[i].FormatName.Contains("OME", StringComparison.OrdinalIgnoreCase))
                        { bestIdx = i; break; }
                    }
                }
                if (bestIdx > 0)
                {
                    var preferred = fileTypes[bestIdx];
                    fileTypes.RemoveAt(bestIdx);
                    fileTypes.Insert(0, preferred);
                }

                var sp = StorageProvider;
                var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Save As",
                    SuggestedFileName = suggestedName,
                    FileTypeChoices = fileTypes,
                });
                if (file == null) return;

                path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) return;
            }

            // ── Common save logic (dialog path or caller-supplied path) ────────────
            path = FormatRegistry.CleanCompoundExtension(path);

            var writer = FormatRegistry.CreateWriter(path);
            if (writer == null)
            {
                await ShowMessageDialogAsync("Unsupported Format",
                    $"No writer registered for '{Path.GetExtension(path)}'.");
                return;
            }

            // OME-TIFF uses ".ome" as a short dialog alias but the writer always
            // produces ".ome.tif[f]" on disk.
            string pathBeforeAlias = path;
            path = ResolveOmeTiffAlias(path);

            if (path != pathBeforeAlias && File.Exists(path))
            {
                if (!await ShowConfirmDialogAsync("Overwrite File?",
                        $"'{Path.GetFileName(path)}' already exists.\nDo you want to overwrite it?"))
                    return;
            }

            if (_currentData.IsVirtual && writer is ICompressible compressible)
                compressible.CompressionInWrite = false;

            var progress = BeginProgress($"Saving {Path.GetFileName(path)}…", blockInput: true);
            var savedTitle = Title;

            try
            {
                if (writer is IProgressReportable pr)
                    pr.ProgressReporter = progress;

                SaveViewSettings();

                await Task.Run(() => _currentData.SaveAs(path, writer), ct);

                if (_currentData.IsWritable)
                {
                    Title = Path.GetFileName(FormatRegistry.CleanCompoundExtension(path));
                    SourcePath = path;
                    CaptureLutVrSnapshot();
                    CaptureScaleSnapshot(_currentData);
                    ClearAllDirty();
                    if (_lutVrRevertBtn != null) _lutVrRevertBtn.IsVisible = false;
                    UpdateScaleRevertButton();
                }
            }
            catch (OperationCanceledException)
            {
                Title = savedTitle;
            }
            catch (Exception ex)
            {
                Title = savedTitle;
                await ShowMessageDialogAsync("Save Failed", ex.Message);
            }
            finally
            {
                EndProgress();
            }
        }

        /// <summary>
        /// Opens the copy-to-clipboard dialog on the main view and executes the user's choice
        /// (image at natural/custom size, or tab-separated text).
        /// </summary>
        private async Task CopyFrameToClipboardAsync()
        {
            await _view.ShowCopyDialogAsync();
        }

        /// <summary>
        /// Creates a new <see cref="MatrixPlotter"/> with a deep-copied <see cref="IMatrixData"/>.
        /// The duplicated data is fully independent — no <c>T[]</c> or <c>ValueRange</c>
        /// references are shared with the original, so mutations in either window are isolated.
        /// <para><see cref="IMatrixData.Duplicate"/> automatically selects the appropriate strategy:
        /// virtual (MMF-backed) data is cloned to a temporary .mxd file without OOM risk,
        /// while in-memory data is deep-copied conventionally.</para>
        /// </summary>
        /// <param name="show">
        /// When <c>true</c> (default), the new window is shown immediately.
        /// Pass <c>false</c> to receive the window without displaying it,
        /// allowing the caller to configure it before calling <see cref="Window.Show"/>.
        /// </param>
        /// <returns>
        /// The newly created <see cref="MatrixPlotter"/>, or <c>null</c> when no data is loaded.
        /// </returns>
        public async Task<MatrixPlotter?> DuplicateAsync(bool show = true)
        {
            if (_currentData == null) return null;

            // Duplicate() auto-dispatches: virtual → temp .mxd, in-memory → deep copy.
            // Virtual duplication writes all frames to a temp file and can take seconds for large
            // datasets, so show a blocking marquee progress overlay while it runs.
            IMatrixData copy;
            if (_currentData.IsVirtual)
            {
                var progress = BeginProgress("Duplicating…", blockInput: true);
                try
                {
                    copy = await Task.Run(() => _currentData.Duplicate());
                }
                finally
                {
                    EndProgress();
                }
            }
            else
            {
                copy = await Task.Run(() => _currentData.Duplicate());
            }

            AppendHistory(copy, "Duplicate", Title);
            var resultPlotter = MatrixPlotter.Create(copy, _view.Lut, $"Copy of {Title}");

            // Copy user overlays from the main view to the duplicate.
            // C# allows access to private fields of other instances of the same type.
            var overlayJson = _view.OverlayManager.SerializeOverlays();
            if (overlayJson != "[]")
            {
                resultPlotter._view.OverlayManager.LoadOverlays(overlayJson);

                // If any overlay was the active ROI, re-activate ROI mode in the new plotter.
                // LoadOverlays restores IsValueRangeRoi=true on the object but does not call
                // ActivateRoiMode, so _valueRangeOverlay, _roiRadio, and _rangeBar are not set.
                foreach (var obj in resultPlotter._view.OverlayManager.Objects)
                {
                    if (obj is IAnalyzableOverlay { IsValueRangeRoi: true } evaluable)
                    {
                        resultPlotter.ActivateRoiMode(evaluable);
                        break;
                    }
                }
            }

            if (show) resultPlotter.Show();
            return resultPlotter;
        }

        /// <summary>Menu command entry point: duplicates and shows the window immediately.</summary>
        private async Task DuplicateWindowAsync() => await DuplicateAsync(show: true);

        /// <summary>Shows a message dialog with an OK button.</summary>
        private async Task ShowMessageDialogAsync(string title, string message)
        {
            var ok = new Button
            {
                Content = "OK",
                Width = 70,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0),
            };
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            });
            stack.Children.Add(ok);
            var dlg = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = stack,
                FontSize = 11,
            };
            ok.Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(this);
        }

        /// <summary>Shows a Yes / No confirmation dialog and returns <c>true</c> if the user chose Yes.</summary>
        private async Task<bool> ShowConfirmDialogAsync(string title, string message)
        {
            bool result = false;
            var yes = new Button { Content = "Yes", Width = 70, Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
            var no = new Button { Content = "No", Width = 70, HorizontalContentAlignment = HorizontalAlignment.Center };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            buttons.Children.Add(yes);
            buttons.Children.Add(no);
            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 360,
            });
            stack.Children.Add(buttons);
            var dlg = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = stack,
                FontSize = 11,
            };
            yes.Click += (_, _) => { result = true; dlg.Close(); };
            no.Click += (_, _) => { result = false; dlg.Close(); };
            await dlg.ShowDialog(this);
            return result;
        }

        /// <summary>
        /// Dummy long-running operation for testing the status-bar progress reporter.
        /// Phase 1 (≈2 s): indeterminate spinner. Phase 2 (≈3 s): determinate progress bar.
        /// </summary>
        private async Task DummyProcessAsync()
        {
            var progress = BeginProgress("Processing…");
            try
            {
                // Phase 1: indeterminate spinner (~2 seconds of "thinking")
                await Task.Run(() => Thread.Sleep(2000));

                // Phase 2: determinate progress (30 steps over ~3 seconds)
                const int totalSteps = 30;
                progress.Report(-totalSteps);           // declare total → bar becomes determinate
                await Task.Run(() =>
                {
                    for (int i = 0; i < totalSteps; i++)
                    {
                        Thread.Sleep(100);               // ~100 ms per step
                        progress.Report(i);              // 0-based step index
                    }
                });
            }
            finally
            {
                EndProgress();
            }
        }

        // ── Action lifecycle ──────────────────────────────────────────────────

        /// <summary>
        /// Starts an <see cref="IPlotterAction"/>, disposing any currently active action first.
        /// Subscribes to <see cref="IPlotterAction.Completed"/> and <see cref="IPlotterAction.Cancelled"/>
        /// to update <see cref="_activeAction"/> and apply result data when the action finishes.
        /// </summary>
        private void InvokeAction(IPlotterAction action)
        {
            _activeAction?.Dispose();
            _activeAction = action;
            action.Completed += OnActionCompleted;
            action.Cancelled += OnActionCancelled;
            action.Invoke(CreateActionContext());
        }

        private void OnActionCompleted(object? sender, IMatrixData? result)
        {
            if (sender is IPlotterAction a)
            {
                a.Completed -= OnActionCompleted;
                a.Cancelled -= OnActionCancelled;
            }
            _activeAction = null;
            if (result != null) SetMatrixData(result);
        }

        private void OnActionCancelled(object? sender, EventArgs e)
        {
            if (sender is IPlotterAction a)
            {
                a.Completed -= OnActionCompleted;
                a.Cancelled -= OnActionCancelled;
            }
            _activeAction = null;
        }

        private PlotterActionContext CreateActionContext() => new()
        {
            MainView = _view,
            HostVisual = this,
            Data = _currentData,
            OrthoPanel = _orthoPanel.ShowRight ? _orthoPanel : null,
            DepthAxisName = _orthoController.ActiveAxisName,
        };

        private void ConvertValueTypeAsync()
        {
            if (_currentData is null) return;

            double lutMin = _rangeBar.DisplayedMinValue;
            double lutMax = _rangeBar.DisplayedMaxValue;
            if (double.IsNaN(lutMin) || double.IsNaN(lutMax))
            {
                var (min, max) = _view.ScanCurrentFrameRange();
                lutMin = min;
                lutMax = max;
            }

            var action = new ConvertValueTypeAction(lutMin, lutMax);
            _activeAction?.Dispose();
            _activeAction = action;

            action.ConvertingStarted += (_, _) =>
            {
                BeginProgress("Converting…", blockInput: true);
            };
            action.ConvertCompleted += (_, r) =>
            {
                EndProgress();
                _activeAction = null;
                string typeDesc = $"{_currentData?.ValueTypeName} \u2192 {r.Data.ValueTypeName}";
                string histDesc = r.DoScale
                    ? $"{typeDesc}; scale [{r.SrcMin:G6}, {r.SrcMax:G6}] \u2192 [{r.TgtMin:G6}, {r.TgtMax:G6}]"
                    : $"{typeDesc}; direct cast";
                if (r.ReplaceData)
                {
                    AppendHistory(r.Data, "Convert Type", Title, histDesc);
                    SetMatrixData(r.Data);
                }
                else
                {
                    AppendHistory(r.Data, "Convert Type", Title, histDesc);
                    MatrixPlotter.Create(r.Data, _view.Lut, $"Convert of {Title}").Show();
                }
            };
            action.Cancelled += (_, _) => { EndProgress(); _activeAction = null; };

            action.Invoke(CreateActionContext());
        }

        /// <summary>Shows a minimal About dialog.</summary>
        private async Task ShowAboutAsync()
        {
            var ok = new Button
            {
                Content = "OK",
                Width = 60,
                HorizontalAlignment = HorizontalAlignment.Right,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0),
            };
            var verFull = typeof(MatrixPlotter).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? string.Empty;
            var plusIdx = verFull.IndexOf('+');
            var ver = plusIdx >= 0 ? verFull[..plusIdx] : verFull;

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock { Text = "MxPlot", FontSize = 16, FontWeight = FontWeight.Bold });
            textStack.Children.Add(new TextBlock { Text = "—Multi-Axis Matrix Visualization", FontSize = 11, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.7 });
            if (!string.IsNullOrEmpty(ver))
                textStack.Children.Add(new TextBlock
                {
                    Text = $"Version {ver}",
                    FontSize = 11,
                    Margin = new Thickness(0, 2, 0, 0),
                    Opacity = 0.55,
                });

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
            try
            {
                var uri = new Uri("avares://MxPlot.UI.Avalonia/Assets/mxplot_logo_pre.png");
                var bmp = new Bitmap(AssetLoader.Open(uri));
                // Height is sized to match the text StackPanel (~2 lines at FontSize 16+11)
                headerRow.Children.Add(new Image
                {
                    Source = bmp,
                    Height = 52,
                    Width = 52,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                });
            }
            catch { }
            headerRow.Children.Add(textStack);

            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(headerRow);
            stack.Children.Add(ok);
            var dlg = new Window
            {
                Title = "About",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = stack,
            };
            ok.Click += (_, _) => dlg.Close();
            await dlg.ShowDialog(this);
        }

        /// <summary>
        /// OME-TIFF lists ".ome" as its first extension so the OS save dialog emits a
        /// single-token extension instead of the compound ".ome.tif", preventing
        /// compound-extension accumulation.  The writer normalises it internally, but the
        /// caller also needs the corrected path for the window title and <c>_filePath</c>.
        /// <para>
        /// When an existing file is being overwritten, the ".tiff" (double-f) variant is
        /// preferred over the canonical ".tif" so the original filename is preserved and
        /// the subsequent existence check can detect the overwrite correctly.
        /// </para>
        /// </summary>
        private static string ResolveOmeTiffAlias(string path)
        {
            if (!path.EndsWith(".ome", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ome.tif", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ome.tiff", StringComparison.OrdinalIgnoreCase))
                return path;

            // Prefer .tiff if that file already exists to avoid creating a sibling
            if (File.Exists(path + ".tiff")) return path + ".tiff";
            return path + ".tif";  // canonical
        }
    }
}
