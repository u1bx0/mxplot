using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.UI.Avalonia.Overlays;
using MxPlot.UI.Avalonia.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MxPlot.Core.IO.Formats;

namespace MxPlot.UI.Avalonia.Views
{
    public partial class MatrixPlotter
    {
        // ── Window actions (File / Edit / About) ─────────────────────────────

        // ── Export framework ──────────────────────────────────────────────────

        /// <summary>
        /// Converts an <see cref="IRenderExportPlugin"/> to an <see cref="ExportFormatDescriptor"/>
        /// so plugin-supplied exporters can be invoked via the same path as built-in formats.
        /// </summary>
        private static ExportFormatDescriptor ToDescriptor(Plugins.IRenderExportPlugin ep)
            => new(
                Label: ep.Label,
                Hint: ep.Hint,
                FileTypeName: ep.FileTypeName,
                FilePattern: ep.FilePattern,
                Exporter: (path, parent, host, progress, ct)
                    => ep.ExportAsync(path, parent, host, progress, ct),
                RequiresStack: ep.RequiresStack);

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
                Exporter: async (path, _, _, _, _) =>
                {
                    await Task.CompletedTask; // SaveAsPng is synchronous
                    var (effW, effH) = _view.GetEffectiveBmpDims();
                    int w = Math.Max(1, (int)Math.Round(effW));
                    int h = Math.Max(1, (int)Math.Round(effH));
                    _view.SaveAsPng(path, w, h, withOverlays: _view.OverlayManager.OverlaysVisible);
                },
                RequiresStack: false);
        }

        /// <summary>
        /// Opens a Save File dialog for <paramref name="desc"/> and runs the exporter.
        /// <para>
        /// UI input is blocked for the duration of the export and the current frame index is
        /// restored to its original value when the export completes or is cancelled.
        /// </para>
        /// </summary>
        private async Task InvokeExportAsync(ExportFormatDescriptor desc)
        {
            var md = _view.MatrixData;
            if (md == null) return;

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

            var (effW, effH) = _view.GetEffectiveBmpDims();
            int w = Math.Max(1, (int)Math.Round(effW));
            int h = Math.Max(1, (int)Math.Round(effH));

            var host = new RenderHostImpl(_view, new global::Avalonia.Size(w, h), BuildExcludedAxisNames());

            int savedFrameIndex = _view.FrameIndex;
            using var cts = new CancellationTokenSource();
            IProgress<int> progress = BeginProgress("Exporting\u2026", blockInput: true);
            try
            {
                await desc.Exporter(path, this, host, progress, cts.Token);
            }
            finally
            {
                _view.FrameIndex = savedFrameIndex;
                EndProgress();
            }
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
                    CaptureRenderSnapshot();
                    CaptureScaleSnapshot(_currentData);
                    ClearAllDirty();
                    UpdateRenderRevertButtons();
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

        private CancellationTokenSource? _duplicateCts;

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
        /// The newly created <see cref="MatrixPlotter"/>, or <c>null</c> when no data is loaded, or
        /// when the user cancels via the status-bar Cancel button.
        /// </returns>
        public async Task<MatrixPlotter?> DuplicateAsync(bool show = true)
        {
            if (_currentData == null) return null;

            // Duplicate() auto-dispatches: Virtual data with more than one frame stays Virtual
            // (streamed to a temp .mxd); everything else is forced in-memory. Either path copies
            // frame by frame and can take a noticeable amount of time for large datasets, so always
            // show progress with a working Cancel button -- not just for the Virtual case, as before.
            bool forceInMemory = !(_currentData.IsVirtual && _currentData.FrameCount > 1);

            _duplicateCts?.Dispose();
            _duplicateCts = new CancellationTokenSource();
            var ct = _duplicateCts.Token;
            var progress = BeginProgress("Duplicating…", blockInput: true, _duplicateCts);

            IMatrixData copy;
            try
            {
                copy = await Task.Run(() => _currentData.Duplicate(forceInMemory, progress, ct));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                _duplicateCts?.Dispose();
                _duplicateCts = null;
                EndProgress();
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

        private Task ConvertValueTypeAsync()
        {
            if (_currentData is null) return Task.CompletedTask;

            // Complex data: show Convert Complex dialog
            if (_currentData.ValueType == typeof(System.Numerics.Complex))
            {
                ConvertComplexValueAsync();
                return Task.CompletedTask;
            }

            // Primitive types: show Convert Value Type dialog
            return new ConvertValueTypeCommand().RunAsync(this);
        }

        private async void ConvertComplexValueAsync()
        {
            if (_currentData is not MatrixData<System.Numerics.Complex> complexData) return;

            var dialog = new ConvertComplexDialog(_currentData, IsReplaceDataBlocked);
            var result = await dialog.ShowCenteredOnAsync(this, this);
            if (result == null) return;

            var (mode, applyLog10, replaceData) = result.Value;

            BeginProgress("Converting complex data…", blockInput: true);
            try
            {
                await Task.Run(() =>
                {
                    var converted = complexData.ToDoubleAllFrames(mode, applyLog10);
                    Dispatcher.UIThread.Post(() =>
                    {
                        EndProgress();
                        string modeDesc = mode.ToString();
                        string histDesc = $"Complex \u2192 double ({modeDesc})" + (applyLog10 ? " + log₁₀" : "");
                        if (replaceData)
                        {
                            AppendHistory(converted, "Convert Complex", Title, histDesc);
                            SetMatrixData(converted, closeDerivedWindows: true);
                        }
                        else
                        {
                            AppendHistory(converted, "Convert Complex", Title, histDesc);
                            MatrixPlotter.Create(converted, _view.Lut, $"Convert of {Title}").Show();
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                EndProgress();
                // Simple error display (future: ShowNotification)
                System.Diagnostics.Debug.WriteLine($"Conversion failed: {ex.Message}");
            }
        }

        /// <summary>Shows a minimal About dialog.</summary>
        /// <summary>
        /// Optional replacement for the content shown in the About dialog (Menu → About),
        /// in place of <see cref="BuildDefaultAboutContent"/>. Set once at startup by a host
        /// embedding MatrixPlotter (e.g. a camera-viewer app built on top of it) that wants its
        /// own About screen instead of MxPlot's -- typically by building its own branding and
        /// calling <see cref="BuildDefaultAboutContent"/> itself to still credit MxPlot underneath.
        /// Receives the <see cref="MatrixPlotter"/> instance About was invoked from (rarely needed).
        /// Left <see langword="null"/> (the default), the built-in MxPlot About is shown unchanged.
        /// </summary>
        public static Func<MatrixPlotter, Control>? AboutContentOverride { get; set; }

        /// <summary>
        /// Builds the default About content (MxPlot logo, name, and version) as a standalone
        /// control. Public and static so a host can reuse it verbatim -- e.g. nested inside its
        /// own <see cref="AboutContentOverride"/> content, to credit MxPlot alongside its own
        /// branding -- without reimplementing the version-reading/layout logic.
        /// </summary>
        public static Control BuildDefaultAboutContent()
        {
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
            return headerRow;
        }

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

            var stack = new StackPanel { Margin = new Thickness(20) };
            stack.Children.Add(AboutContentOverride?.Invoke(this) ?? BuildDefaultAboutContent());
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

        // ── IRenderHost implementation ────────────────────────────────────────

        /// <summary>
        /// Returns export format entries for <paramref name="view"/>'s context menu.
        /// For the main view this mirrors the hamburger-menu export formats.
        /// For side views only formats relevant to the orthogonal slice sequence are included.
        /// </summary>
        private System.Collections.Generic.IEnumerable<(string Label, string Hint, bool RequiresStack, Func<Task> Action)>
            GetExportFormatsForView(Controls.MxView view)
        {
            bool isMain = ReferenceEquals(view, _view);
            if (isMain)
            {
                bool mainIsStack = HasAnimatableAxis(BuildExcludedAxisNames());
                yield return ("PNG\u2026", "Exports the current frame as a PNG image.", false,
                    () => InvokeExportAsync(MakePngExportDescriptor()));
                foreach (var desc in ExportFormats)
                {
                    var d = desc;
                    if (d.RequiresStack && !mainIsStack) continue;
                    yield return (d.Label, d.Hint, d.RequiresStack, () => InvokeExportAsync(d));
                }
                foreach (var ep in MatrixPlotterPluginRegistry.ExportPlugins)
                {
                    if (ep.RequiresStack && !mainIsStack) continue;
                    var d = ToDescriptor(ep);
                    yield return (d.Label, d.Hint, d.RequiresStack, () => InvokeExportAsync(d));
                }
            }
            else
            {
                if (view.MatrixData == null) yield break;
                // Side views consume the ortho axis as their slice dimension, so it cannot be
                // animated either - the same "is anything left to step through" rule applies.
                bool isHyperstack = HasAnimatableAxis(BuildExcludedAxisNames(_orthoController.ActiveAxisName));
                var cv = view;
                yield return ("PNG\u2026", "Exports the current side-view frame as a PNG image.", false,
                    () => InvokeExportForViewAsync(
                        new ExportFormatDescriptor(
                            "PNG\u2026", "Exports the current side-view frame as a PNG image.",
                            "PNG Image", "*.png",
                            async (path, _, _, _, _) =>
                            {
                                await Task.CompletedTask;
                                var (ew, eh) = cv.GetEffectiveBmpDims();
                                cv.SaveAsPng(path, Math.Max(1, (int)Math.Round(ew)),
                                    Math.Max(1, (int)Math.Round(eh)),
                                    withOverlays: cv.OverlayManager.OverlaysVisible);
                            },
                            RequiresStack: false),
                        cv));
                if (isHyperstack)
                {
                    foreach (var desc in ExportFormats)
                    {
                        if (!desc.RequiresStack) continue;
                        var d = desc;
                        yield return (d.Label, d.Hint, d.RequiresStack,
                            () => InvokeExportForViewAsync(d, cv));
                    }
                    foreach (var ep in MatrixPlotterPluginRegistry.ExportPlugins)
                    {
                        if (!ep.RequiresStack) continue;
                        var d = ToDescriptor(ep);
                        yield return (d.Label, d.Hint, d.RequiresStack,
                            () => InvokeExportForViewAsync(d, cv));
                    }
                }
            }
        }

        /// <summary>
        /// Opens a Save File dialog for <paramref name="desc"/> and exports the given
        /// <paramref name="sideView"/> using a <see cref="SideViewRenderHostImpl"/> that
        /// drives frame iteration through the main <see cref="_currentData"/> and waits for
        /// each orthogonal slice rebuild before rendering the side view bitmap.
        /// </summary>
        private async Task InvokeExportForViewAsync(ExportFormatDescriptor desc, Controls.MxView sideView)
        {
            var md = _currentData;
            if (md == null || sideView.MatrixData == null) return;

            int digits = md.FrameCount > 1 ? md.FrameCount.ToString().Length : 0;
            var baseName = Path.GetFileNameWithoutExtension(Title ?? "Image");
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "Image";

            // Determine view label, excluded axis, and projection suffix for side views
            bool isBottom = ReferenceEquals(sideView, _orthoPanel.BottomView);
            bool isRight = ReferenceEquals(sideView, _orthoPanel.RightView);
            string? viewLabel = isBottom ? "X-Z View" : isRight ? "Z-Y View" : null;
            string? excludedAxisName = _orthoController.ActiveAxisName;

            string viewTag = isBottom ? "X-Z" : isRight ? "Z-Y" : "";
            ProjectionMode? projMode = isBottom ? _orthoController.XzProjectionMode
                : isRight ? _orthoController.YzProjectionMode : null;
            string projSuffix = projMode switch
            {
                MxPlot.Core.Processing.ProjectionMode.Maximum => " MIP",
                MxPlot.Core.Processing.ProjectionMode.Minimum => " MinIP",
                MxPlot.Core.Processing.ProjectionMode.Average => " AvIP",
                _ => ""
            };
            string viewSuffix = viewTag.Length > 0 ? $" [{viewTag}{projSuffix}]" : "";

            string suggestedName = desc.RequiresStack
                ? baseName + viewSuffix
                : baseName + viewSuffix + $"_frame{_view.FrameIndex.ToString($"D{Math.Max(digits, 1)}")}";

            var sp = StorageProvider;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = viewLabel != null ? $"Export {viewLabel} as {desc.FileTypeName}" : $"Export as {desc.FileTypeName}",
                SuggestedFileName = suggestedName,
                FileTypeChoices = [new FilePickerFileType(desc.FileTypeName) { Patterns = [desc.FilePattern] }],
            });
            if (file == null) return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            var (effW, effH) = sideView.GetEffectiveBmpDims();
            int w = Math.Max(1, (int)Math.Round(effW));
            int h = Math.Max(1, (int)Math.Round(effH));

            using var host = new SideViewRenderHostImpl(sideView, md, new global::Avalonia.Size(w, h),
                viewLabel, BuildExcludedAxisNames(excludedAxisName), _orthoController);

            int savedActiveIndex = md.ActiveIndex;
            using var cts = new CancellationTokenSource();
            IProgress<int> progress = BeginProgress("Exporting\u2026", blockInput: true);
            try
            {
                await desc.Exporter(path, this, host, progress, cts.Token);
            }
            finally
            {
                md.ActiveIndex = savedActiveIndex;
                EndProgress();
            }
        }

        /// <summary>
        /// Whether any axis is left to animate once <paramref name="excludedAxisNames"/> is taken
        /// out. Frame-stepping exporters (AVI, image sequences) are pointless without one - e.g.
        /// Composite data whose only axis is Channel renders a single blended image.
        /// </summary>
        private bool HasAnimatableAxis(IReadOnlyList<string>? excludedAxisNames)
        {
            if (_currentData is not { FrameCount: > 1 } data) return false;
            if (excludedAxisNames == null || excludedAxisNames.Count == 0) return true;

            foreach (var axis in data.Axes)
            {
                if (axis.Count <= 1) continue;
                if (!excludedAxisNames.Contains(axis.Name, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Axis names that must not be offered as an export animation axis, because the view
        /// already consumes them: the Channel axis while Composite blends every channel into each
        /// frame, plus <paramref name="orthoAxisName"/> for a side view's slice dimension.
        /// </summary>
        private IReadOnlyList<string>? BuildExcludedAxisNames(string? orthoAxisName = null)
        {
            var names = new List<string>(2);
            if (!string.IsNullOrEmpty(orthoAxisName)) names.Add(orthoAxisName!);
            if (_isCompositeMode && _compositeAxisDimIndex >= 0 && _currentData != null)
            {
                string channelAxisName = _currentData.Dimensions[_compositeAxisDimIndex].Name;
                if (!names.Contains(channelAxisName, StringComparer.OrdinalIgnoreCase))
                    names.Add(channelAxisName);
            }
            return names.Count > 0 ? names : null;
        }

        /// <summary>
        /// Concrete <see cref="Plugins.IRenderHost"/> that wraps the <see cref="Controls.MxView"/>
        /// held by this <see cref="MatrixPlotter"/>. Created per export invocation.
        /// </summary>
        private sealed class RenderHostImpl : Plugins.IRenderHost
        {
            private readonly Controls.MxView _view;
            private readonly global::Avalonia.Size _size;

            internal RenderHostImpl(Controls.MxView view, global::Avalonia.Size size,
                IReadOnlyList<string>? excludedAxisNames = null)
            {
                _view = view;
                _size = size;
                ExcludedAxisNames = excludedAxisNames;
            }

            public MxPlot.Core.IMatrixData Data => _view.MatrixData!;

            public IReadOnlyList<string>? ExcludedAxisNames { get; }

            public global::Avalonia.Size CurrentRenderSize => _size;

            public bool IsOverlayVisible => _view.OverlayManager.OverlaysVisible;

            public Task<byte[]> RenderFrameAsync(int frameIndex, global::Avalonia.Size size, bool withOverlay)
            {
                return Dispatcher.UIThread.InvokeAsync<byte[]>(() =>
                {
                    _view.FrameIndex = frameIndex;
                    // Sync Axis.Index so TextOverlay tokens like {1:p} reflect the current frame.

                    /* The following is commented out because the index of each axis is automatically 
                     * updated when FrameIndex is set. These lines are redundant and can be removed to avoid confusion.
                    var dims = _view.MatrixData?.Dimensions;
                    if (dims != null)
                        dims.SetActiveIndices(dims.GetAxisIndices(frameIndex));
                    */
                    int w = Math.Max(1, (int)Math.Round(size.Width));
                    int h = Math.Max(1, (int)Math.Round(size.Height));
                    var rtb = _view.RenderToBitmap(w, h, withOverlay);
                    if (rtb == null) return Array.Empty<byte>();
                    using var wb = new WriteableBitmap(
                        new PixelSize(w, h), new Vector(96, 96),
                        PixelFormat.Bgra8888,
                        AlphaFormat.Premul);
                    using var lck = wb.Lock();
                    rtb.CopyPixels(lck, AlphaFormat.Premul);
                    var buf = new byte[lck.RowBytes * h];
                    System.Runtime.InteropServices.Marshal.Copy(lck.Address, buf, 0, buf.Length);
                    return buf;
                }).GetTask();
            }
        }

        /// <summary>
        /// <see cref="Plugins.IRenderHost"/> for side views (XZ/YZ orthogonal slices).
        /// <para>
        /// <see cref="RenderFrameAsync"/> internally calls
        /// <see cref="Controls.OrthogonalViewController.RebuildSlicesForExportAsync"/> to
        /// recompute the slice data for each frame, then captures the side view bitmap.
        /// All UI-thread marshalling is handled internally; callers may call
        /// <see cref="RenderFrameAsync"/> from any thread.
        /// </para>
        /// </summary>
        private sealed class SideViewRenderHostImpl : Plugins.IRenderHost, IDisposable
        {
            private readonly Controls.MxView _sideView;
            private readonly MxPlot.Core.IMatrixData _mainData;
            private readonly global::Avalonia.Size _size;
            private readonly Controls.OrthogonalViewController _orthoController;

            internal SideViewRenderHostImpl(Controls.MxView sideView,
                MxPlot.Core.IMatrixData mainData,
                global::Avalonia.Size size,
                string? viewLabel,
                IReadOnlyList<string>? excludedAxisNames,
                Controls.OrthogonalViewController orthoController)
            {
                _sideView = sideView;
                _mainData = mainData;
                _size = size;
                ViewLabel = viewLabel;
                ExcludedAxisNames = excludedAxisNames;
                _orthoController = orthoController;
                _orthoController.BeginExport();
            }

            public void Dispose() => _orthoController.EndExport();

            public MxPlot.Core.IMatrixData Data => _mainData;
            public global::Avalonia.Size CurrentRenderSize => _size;
            public bool IsOverlayVisible => _sideView.OverlayManager.OverlaysVisible;
            public string? ViewLabel { get; }
            public IReadOnlyList<string>? ExcludedAxisNames { get; }

            public async Task<byte[]> RenderFrameAsync(int frameIndex, global::Avalonia.Size size, bool withOverlay)
            {
                // Move to the requested frame *before* rebuilding: RebuildSlicesForExportAsync
                // projects/slices against IMatrixData.ActiveIndex as it stands at call time, so
                // setting it afterwards would render every frame one step behind.
                // ActiveIndex writes fire AxisTracker events, hence the UI thread.
                await Dispatcher.UIThread.InvokeAsync(() => _mainData.ActiveIndex = frameIndex);

                int ix = _orthoController.CurrentIX;
                int iy = _orthoController.CurrentIY;
                await _orthoController.RebuildSlicesForExportAsync(ix, iy);

                // RenderToBitmap requires the Compositor, so it must run on the UI thread too.
                return await Dispatcher.UIThread.InvokeAsync<byte[]>(() =>
                {
                    int w = Math.Max(1, (int)Math.Round(size.Width));
                    int h = Math.Max(1, (int)Math.Round(size.Height));
                    var rtb = _sideView.RenderToBitmap(w, h, withOverlay);
                    if (rtb == null) return [];
                    using var wb = new WriteableBitmap(
                        new PixelSize(w, h), new Vector(96, 96),
                        PixelFormat.Bgra8888, AlphaFormat.Premul);
                    using var lck = wb.Lock();
                    rtb.CopyPixels(lck, AlphaFormat.Premul);
                    var buf = new byte[lck.RowBytes * h];
                    System.Runtime.InteropServices.Marshal.Copy(lck.Address, buf, 0, buf.Length);
                    return buf;
                }).GetTask();
            }
        }
    }
}
