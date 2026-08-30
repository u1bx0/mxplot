using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Clipboard handling partial: image and CSV/TSV import from clipboard.
    /// </summary>
    public partial class MxPlotAppWindow
    {
        // ── Clipboard ────────────────────────────────────────────────────────

        // Platform image-format identifiers tried in order of preference
        private static readonly string[] ClipboardImageFormats =
            ["PNG", "image/png", "public.png", "public.tiff", "com.apple.tiff", "Bitmap"];

        private async Task<bool> ClipboardHasImageAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) return false;
                var formats = await clipboard.GetFormatsAsync();
                return formats?.Any(f =>
                    ClipboardImageFormats.Contains(f, StringComparer.OrdinalIgnoreCase)) == true;
            }
            catch { return false; }
        }

        private async Task<bool> ClipboardHasUsableDataAsync()
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null) return false;

                var formats = await clipboard.GetFormatsAsync();
                if (formats == null) return false;

                // 1. Check for image formats
                bool hasImage = formats.Any(f =>
                    ClipboardImageFormats.Contains(f, StringComparer.OrdinalIgnoreCase) ||
                    f.Contains("image", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("tiff", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("png", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("bmp", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("pict", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains("avaloniaui", StringComparison.OrdinalIgnoreCase));

                if (hasImage) return true;

                //2. Check for text formats
                return formats.Any(f =>
                    f.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                    f.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
                    f.Equals("public.utf8-plain-text", StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        private async Task<Bitmap?> GetClipboardBitmapAsync()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return null;

            var available = await clipboard.GetFormatsAsync() ?? [];
            Debug.WriteLine(
                $"[Clipboard] available: {string.Join(", ", available)}");

            // On macOS, Avalonia's GetDataAsync returns null for all native NSPasteboard
            // image formats. As a workaround, MxView.CopyImageAsync caches the PNG bytes
            // of the last in-process copy. If the clipboard still carries the avaloniaui
            // in-process format (meaning no other app has overwritten it since), use the cache.
            bool hasInProc = available.Any(f =>
                f.Contains("avaloniaui", StringComparison.OrdinalIgnoreCase));
            if (hasInProc && MxPlot.UI.Avalonia.Controls.MxView.LastCopiedPng is { Length: > 4 } cached)
            {
                Console.WriteLine("[Clipboard] using in-process PNG cache");
                try
                {
                    using var cacheMem = new MemoryStream(cached);
                    return new Bitmap(cacheMem);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Clipboard] cache decode failed: {ex.Message}");
                }
            }

            var predefined = ClipboardImageFormats
                .Where(f => available.Any(a =>
                    string.Equals(a, f, StringComparison.OrdinalIgnoreCase)));
            var extra = available
                .Where(f => !ClipboardImageFormats.Any(cf =>
                    string.Equals(cf, f, StringComparison.OrdinalIgnoreCase))
                    && (f.Contains("image", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("tiff", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("png", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("bmp", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("pict", StringComparison.OrdinalIgnoreCase)
                     || f.Contains("avaloniaui", StringComparison.OrdinalIgnoreCase)));

            foreach (var fmt in predefined.Concat(extra))
            {
                try
                {
                    var data = await clipboard.GetDataAsync(fmt);
                    Console.WriteLine(
                        $"[Clipboard] fmt={fmt}  type={data?.GetType().Name ?? "null"}" +
                        $"  len={(data is byte[] b0 ? b0.Length : -1)}");

                    if (data is Bitmap inProcBmp)
                        return inProcBmp;

                    byte[]? bytes = data switch
                    {
                        byte[] b => b,
                        MemoryStream ms => ms.ToArray(),
                        Stream s => ReadStreamToBytes(s),
                        _ => null,
                    };
                    if (bytes == null || bytes.Length < 4) continue;

                    using var skBmp = SkiaSharp.SKBitmap.Decode(bytes);
                    if (skBmp != null)
                    {
                        using var png = new MemoryStream();
                        skBmp.Encode(png, SkiaSharp.SKEncodedImageFormat.Png, 100);
                        png.Position = 0;
                        return new Bitmap(png);
                    }

                    using var ms2 = new MemoryStream(bytes);
                    return new Bitmap(ms2);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[Clipboard] fmt={fmt} exception: {ex.Message}");
                }
            }

            // Fall back for macOS: try to get the image using the native NSPasteboard API via AppleScript
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Console.WriteLine("[Clipboard] Avalonia failed. Falling back to macOS native AppleScript...");
                string tempFilePath = Path.Combine(Path.GetTempPath(), "mxplot_clipboard.png");
                try
                {
                    string script = $"""
                        try
                            set tempFile to POSIX file "{tempFilePath}"
                            set f to open for access tempFile with write permission
                            set eof of f to 0
                            write (the clipboard as «class PNGf») to f
                            close access f
                        on error errMsg
                            try
                                close access file "{tempFilePath}"
                            end try
                            log errMsg
                        end try
                        """;

                    var psi = new ProcessStartInfo
                    {
                        FileName = "osascript",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(script);

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        string errorOutput = await process.StandardError.ReadToEndAsync();
                        await process.WaitForExitAsync();

                        if (!string.IsNullOrWhiteSpace(errorOutput))
                        {
                            Console.WriteLine($"[Clipboard] osascript stderr: {errorOutput.Trim()}");
                        }

                        if (File.Exists(tempFilePath) && new FileInfo(tempFilePath).Length > 0)
                        {
                            byte[] fileBytes = await File.ReadAllBytesAsync(tempFilePath);
                            File.Delete(tempFilePath);

                            using var ms = new MemoryStream(fileBytes);
                            Console.WriteLine("[Clipboard] Successfully extracted PNG image via AppleScript.");
                            return new Bitmap(ms);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Clipboard] macOS fallback failed: {ex.Message}");
                }
                finally
                {
                    if (File.Exists(tempFilePath))
                    {
                        try { File.Delete(tempFilePath); } catch { }
                    }
                }
            }

            return null;
        }

        private static byte[] ReadStreamToBytes(Stream s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        private async void HamburgerOpenFromClipboard_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Try image first
            var bmp = await GetClipboardBitmapAsync();
            if (bmp != null)
            {
                await OpenClipboardImageAsync(bmp);
                return;
            }

            // Try plain text → CSV
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            string? text = clipboard == null ? null : await clipboard.TryGetTextAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                await OpenClipboardCsvAsync(text);
                return;
            }

            await ShowErrorAsync("No image or CSV text found in clipboard.");
        }

        private async Task OpenClipboardImageAsync(Bitmap bmp)
        {
            try
            {
                int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;

                byte[] raw;
                int stride;
                using (var wb = new WriteableBitmap(bmp.PixelSize, bmp.Dpi,
                           PixelFormat.Rgba8888, AlphaFormat.Unpremul))
                using (var fb = wb.Lock())
                {
                    // Use the framebuffer overload, not CopyPixels(PixelRect, IntPtr, ...):
                    // the IntPtr overload blits in the SOURCE bitmap's native pixel format
                    // (Bgra8888 for Windows clipboard bitmaps) and ignores the format declared
                    // on the destination WriteableBitmap, which silently swaps R and B.
                    // This overload converts into the framebuffer's declared Rgba8888 layout,
                    // so the byte offsets below really are R, G, B on every platform.
                    bmp.CopyPixels(fb, AlphaFormat.Unpremul);
                    stride = fb.RowBytes;
                    raw = new byte[stride * h];
                    Marshal.Copy(fb.Address, raw, 0, raw.Length);
                }

                // No "grayscale or channels?" prompt: the pixels already answer it. A colour image
                // arrives as R/G/B channels (and MatrixPlotter opens it in Composite), a gray one as
                // a single channel. Turning a colour image gray afterwards is Edit ▸ Convert to Grayscale.
                bool grayscale = await Task.Run(() => IsMonochrome(raw, stride, w, h));
                var md = await Task.Run(() => ClipboardPixelsToMatrixData(raw, stride, w, h, grayscale));

                var title = grayscale ? "Clipboard  (Grayscale)" : "Clipboard  (RGB)";
                MatrixPlotter.Create(md, title: title).Show();
            }
            finally
            {
                bmp.Dispose();
            }
        }

        /// <summary>Data structure interpretation mode for profile plotting.</summary>
        private enum ProfileDataFormat
        {
            /// <summary>Pairs of X,Y columns: (col0,col1), (col2,col3), ...</summary>
            XyXyXy,
            /// <summary>Shared X column: col0=X, col1..n=Y1..Yn</summary>
            XyYyYy
        }

        /// <summary>Target plotter type for CSV data.</summary>
        private enum PlotterType { Matrix, Profile }

        /// <summary>Parsed CSV structure with optional header labels and NaN indices.</summary>
        private record CsvStructure(
                    MatrixData<double> Data,
                    string[]? HeaderLabels,
                    IList<int> NanIndices);

        /// <summary>
        /// Analyzes clipboard text as CSV/TSV and returns the parsed structure and separator.
        /// Returns null if parsing fails.
        /// </summary>
        private static (CsvStructure? Structure, string? Sep) AnalyzeClipboardCsv(string text)
        {
            var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) return (null, null);

            foreach (var sep in new[] { "\t", "," })
            {
                var structure = TryParseCsvStructure(lines, sep);
                if (structure != null) return (structure, sep);
            }
            return (null, null);
        }

        /// <summary>
        /// Parses CSV lines with the given separator and extracts header labels if present.
        /// Returns null if parsing fails or data is insufficient.
        /// </summary>
        private static CsvStructure? TryParseCsvStructure(string[] lines, string sep)
        {
            MatrixData<double> md;
            try { md = MxPlot.Core.IO.CsvHandler.CreateFrom<double>(lines, sep, flipY: true); }
            catch { return null; }

            if (md.XCount < 1 || md.YCount < 1) return null;

            // Extract header labels from CSV_HEADER metadata (non-numeric first line)
            string[]? headerLabels = null;
            if (md.Metadata.TryGetValue("CSV_HEADER", out var headerText) && !string.IsNullOrWhiteSpace(headerText))
            {
                var firstLine = headerText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault();
                if (firstLine != null)
                {
                    headerLabels = firstLine.Split(new[] { sep }, StringSplitOptions.None)
                        .Select(s => s.Trim())
                        .ToArray();
                }
            }

            // Collect flat indices of NaN values.
            // FrameCount is always 1 for CSV/TSV, so scanning AsSpan(0) covers all cells.
            var nanIndices = new List<int>();
            var span = md.AsSpan(0);
            for (int i = 0; i < span.Length; i++)
                if (double.IsNaN(span[i])) 
                    nanIndices.Add(i);

            return new CsvStructure(md, headerLabels, nanIndices);
        }

        /// <summary>
        /// Detects the most likely ProfileDataFormat for the given MatrixData.
        /// Returns the suggested format and whether the data is ambiguous (both formats possible).
        /// </summary>
        private static (ProfileDataFormat Format, bool IsAmbiguous) DetectProfileFormat(MatrixData<double> md)
        {
            int cols = md.XCount;
            int rows = md.YCount;

            // Heuristic 1: xyxyxy requires even column count
            bool canBeXyXyXy = cols % 2 == 0 && cols >= 2;

            // Heuristic 2: xyyyyy requires at least 2 columns
            bool canBeXyYyYy = cols >= 2;

            // Heuristic 3: Time-series data (many rows) suggests xyyyyy; spectral comparison (many cols) suggests xyxyxy
            bool preferXyYyYy = rows > cols;

            if (canBeXyXyXy && canBeXyYyYy)
                return preferXyYyYy ? (ProfileDataFormat.XyYyYy, true) : (ProfileDataFormat.XyXyXy, true);
            if (canBeXyXyXy) return (ProfileDataFormat.XyXyXy, false);
            if (canBeXyYyYy) return (ProfileDataFormat.XyYyYy, false);

            // Fallback: treat as single-series xyyyyy
            return (ProfileDataFormat.XyYyYy, false);
        }

        /// <summary>
        /// Converts MatrixData to a list of PlotSeries according to the specified format.
        /// Header labels are used for series names if available.
        /// </summary>
        private static IReadOnlyList<MxPlot.UI.Avalonia.Controls.PlotSeries> ConvertToProfileSeries(
            MatrixData<double> md,
            string[]? headerLabels,
            ProfileDataFormat format)
        {
            var series = new List<MxPlot.UI.Avalonia.Controls.PlotSeries>();
            var array = md.GetArray(0);

            if (format == ProfileDataFormat.XyXyXy)
            {
                // xyxyxy: (col 0, col 1), (col 2, col 3), ...
                int pairCount = md.XCount / 2;
                for (int i = 0; i < pairCount; i++)
                {
                    var points = new List<(double X, double Y)>();
                    for (int row = 0; row < md.YCount; row++)
                    {
                        double x = array[row * md.XCount + i * 2];
                        double y = array[row * md.XCount + i * 2 + 1];
                        if (!double.IsNaN(x) && !double.IsNaN(y))
                            points.Add((x, y));
                    }
                    string name = GetSeriesName(headerLabels, i * 2 + 1, i);
                    series.Add(new MxPlot.UI.Avalonia.Controls.PlotSeries(points, name, MxPlot.UI.Avalonia.Controls.PlotStyle.Line));
                }
            }
            else
            {
                // xyyyyy: col 0 = X, col 1..n = Y1..Yn
                var xValues = Enumerable.Range(0, md.YCount)
                    .Select(row => array[row * md.XCount])
                    .ToArray();

                for (int col = 1; col < md.XCount; col++)
                {
                    var points = new List<(double X, double Y)>();
                    for (int row = 0; row < md.YCount; row++)
                    {
                        double x = xValues[row];
                        double y = array[row * md.XCount + col];
                        if (!double.IsNaN(x) && !double.IsNaN(y))
                            points.Add((x, y));
                    }
                    string name = GetSeriesName(headerLabels, col, col - 1);
                    series.Add(new MxPlot.UI.Avalonia.Controls.PlotSeries(points, name, MxPlot.UI.Avalonia.Controls.PlotStyle.Line));
                }
            }
            return series;
        }

        /// <summary>
        /// Gets a series name from header labels or generates a default name.
        /// </summary>
        private static string GetSeriesName(string[]? headerLabels, int colIndex, int seriesIndex)
        {
            if (headerLabels != null && colIndex < headerLabels.Length && !string.IsNullOrWhiteSpace(headerLabels[colIndex]))
                return headerLabels[colIndex];
            return $"Series {seriesIndex + 1}";
        }

        private async Task OpenClipboardCsvAsync(string text)
        {
            var (structure, sep) = await Task.Run(() => AnalyzeClipboardCsv(text));
            if (structure == null)
            {
                await ShowErrorAsync("Clipboard text could not be parsed as CSV (comma or tab separated).");
                return;
            }

            var md = structure.Data;
            var sepLabel = sep == "\t" ? "TSV" : "CSV";

            var (suggestedFormat, isAmbiguous) = DetectProfileFormat(md);

            var choice = await ShowCsvPlotterTypeDialogAsync(
                md.XCount, md.YCount, sepLabel, suggestedFormat, isAmbiguous,
                structure.NanIndices.Count);                        // ← 追加
            if (choice == null) return;

            var (plotterType, profileFormat, nanPadding) = choice.Value;

            // Apply NaN fill in-place if requested (works for both Matrix and Profile)
            if (nanPadding.HasValue)
            {
                var array = md.GetArray(0);
                foreach (var idx in structure.NanIndices)
                    array[idx] = nanPadding.Value;
            }

            if (plotterType == PlotterType.Matrix)
            {
                var title = $"Clipboard  ({sepLabel}  {md.XCount}\u00d7{md.YCount})";
                MatrixPlotter.Create(md, title: title).Show();
            }
            else
            {
                var series = ConvertToProfileSeries(md, structure.HeaderLabels, profileFormat);
                var title = $"Clipboard  ({sepLabel}  {series.Count} series)";
                new ProfilePlotter(series, "X", "Y", title).Show();
            }
        }

        /// <summary>
        /// Shows a dialog asking whether to load CSV/TSV data as MatrixPlotter (2D image) or ProfilePlotter (1D profile).
        /// For ambiguous profile data, displays separate buttons for xyyyyy and xyxyxy formats.
        /// </summary>
        /// <returns>Selected plotter type and profile format, or null if cancelled.</returns>
        private async Task<(PlotterType Type, ProfileDataFormat Format, double? NanPadding)?> ShowCsvPlotterTypeDialogAsync(
            int cols, int rows, string sepLabel, ProfileDataFormat suggestedFormat, bool isAmbiguous,
            int nanCount)
        {
            PlotterType? selectedType = null;
            ProfileDataFormat selectedFormat = suggestedFormat;

            // ── Load-as buttons ──────────────────────────────────────
            var matrixBtn = new Button
            {
                Content = "2D Image",
                Width = 120,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            ToolTip.SetTip(matrixBtn, "2D Image by MatrixPlotter");

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            btnRow.Children.Add(matrixBtn);

            Button? profileXyyyBtn = null;
            Button? profileXyxyBtn = null;
            Button? profileBtn = null;

            if (isAmbiguous)
            {
                profileXyyyBtn = new Button
                {
                    Content = "Profile(s): Xyyy\u2026",
                    Width = 130,
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                ToolTip.SetTip(profileXyyyBtn, "1D Profile(s) with shared X:\nX Y₁ Y₂ Y₃ ...");

                profileXyxyBtn = new Button
                {
                    Content = "Profile(s): XyXy\u2026",
                    Width = 130,
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                ToolTip.SetTip(profileXyxyBtn, "1D Profile(s) with pairs:\nX₁ Y₁ X₂ Y₂ ...");

                btnRow.Children.Add(profileXyyyBtn);
                btnRow.Children.Add(profileXyxyBtn);
            }
            else
            {
                profileBtn = new Button
                {
                    Content = suggestedFormat == ProfileDataFormat.XyYyYy
                        ? "Profile(s): Xyyy\u2026"
                        : "Profile(s): XyXy\u2026",
                    Width = 130,
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                ToolTip.SetTip(profileBtn,
                    suggestedFormat == ProfileDataFormat.XyYyYy
                        ? "1D Profile(s) with shared X:\nX Y₁ Y₂ Y₃ ..."
                        : "1D Profile(s) with pairs:\nX₁ Y₁ X₂ Y₂ ...");
                btnRow.Children.Add(profileBtn);
            }

            // ── NaN handling section (only when NaN exists) ──────────
            RadioButton? keepNanRadio = null;
            RadioButton? fillNanRadio = null;
            TextBox? fillValueBox = null;

            var stack = new StackPanel { Margin = new Thickness(16) };
            stack.Children.Add(new TextBlock
            {
                Text = $"{sepLabel} data: columns = {cols}, rows = {rows}",
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 4)
            });
            stack.Children.Add(new TextBlock
            {
                Text = "Load as:",
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            });
            stack.Children.Add(btnRow);

            if (nanCount > 0)
            {
                fillValueBox = new TextBox
                {
                    Text = "0",
                    Width = 70,
                    Height = 22,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(4, 1, 4, 1),
                    IsEnabled = false       // enabled when fillNanRadio is checked
                };

                keepNanRadio = new RadioButton
                {
                    Content = "Keep NaN",
                    IsChecked = true,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                fillNanRadio = new RadioButton
                {
                    Content = "Fill with",
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                // Enable/disable TextBox according to radio selection
                fillNanRadio.IsCheckedChanged += (_, _) =>
                    fillValueBox.IsEnabled = fillNanRadio.IsChecked == true;

                var nanInfoText = new TextBlock
                {
                    Text = $"Non-numeric (NaN) detected: {nanCount} of {cols * rows} cells.",
                    Opacity = 0.6,
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var nanRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center
                };
                nanRow.Children.Add(keepNanRadio);
                nanRow.Children.Add(fillNanRadio);
                nanRow.Children.Add(fillValueBox);

                // Separator line
                var separator = new Border
                {
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                    Margin = new Thickness(0, 10, 0, 6)
                };

                stack.Children.Add(separator);
                stack.Children.Add(nanInfoText);
                stack.Children.Add(new TextBlock
                {
                    Text = "NaN handling:",
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 2, 0, 6)
                });
                stack.Children.Add(nanRow);
            }

            var cancelBtn = new Button
            {
                Content = "Cancel",
                Width = 72,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            stack.Children.Add(new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                Margin = new Thickness(0, 10, 0, 0)
            });
            stack.Children.Add(cancelBtn);

            var dlg = new Window
            {
                Title = "Open from Clipboard",
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontSize = 11,
                Content = stack
            };

            // ── Helpers ──────────────────────────────────────────────
            // Reads NanPadding from radio/textbox state at the moment a button is clicked
            double? GetNanPadding()
            {
                if (nanCount == 0 || keepNanRadio?.IsChecked == true)
                    return null;                                    // Keep NaN
                if (double.TryParse(
                        fillValueBox!.Text,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double v))
                    return v;
                // Invalid input: highlight and abort
                fillValueBox.Background = new SolidColorBrush(Color.FromRgb(255, 200, 200));
                return double.NaN;                                  // sentinel: validation failed
            }

            void SelectAndClose(PlotterType type, ProfileDataFormat fmt)
            {
                var padding = GetNanPadding();
                if (double.IsNaN(padding ?? 0) && padding.HasValue) return;  // validation failed, keep dialog open
                selectedType = type;
                selectedFormat = fmt;
                dlg.Close();
            }

            // ── Wire up buttons ──────────────────────────────────────
            matrixBtn.Click += (_, _) => SelectAndClose(PlotterType.Matrix, suggestedFormat);

            if (profileXyyyBtn != null)
                profileXyyyBtn.Click += (_, _) => SelectAndClose(PlotterType.Profile, ProfileDataFormat.XyYyYy);
            if (profileXyxyBtn != null)
                profileXyxyBtn.Click += (_, _) => SelectAndClose(PlotterType.Profile, ProfileDataFormat.XyXyXy);
            if (profileBtn != null)
                profileBtn.Click += (_, _) => SelectAndClose(PlotterType.Profile, suggestedFormat);

            fillValueBox?.TextChanged += (_, _) => fillValueBox.Background = null;
            cancelBtn.Click += (_, _) => dlg.Close();

            await WithTopmostSuspended(() => dlg.ShowDialog<object?>(this));
            if (selectedType == null) return null;

            // Resolve final NanPadding for return
            double? nanPadding = GetNanPadding();
            return (selectedType.Value, selectedFormat, nanPadding);
        }


        /// <summary>
        /// Returns <c>true</c> when every pixel satisfies R == G == B, i.e. the clipboard image
        /// carries no colour information. Mirrors the same test the image-file reader applies.
        /// </summary>
        /// <remarks>
        /// A colour image bails out on its first coloured pixel, which is almost always immediate;
        /// only a genuinely gray image pays for a full pass over the buffer.
        /// </remarks>
        private static bool IsMonochrome(byte[] raw, int stride, int w, int h)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    if (raw[i] != raw[i + 1] || raw[i + 1] != raw[i + 2]) return false;
                }
            }
            return true;
        }

        private static MatrixData<byte> ClipboardPixelsToMatrixData(
            byte[] raw, int stride, int w, int h, bool grayscale)
        {
            if (grayscale)
            {
                var gray = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + x * 4;
                        gray[(h - 1 - y) * w + x] = (byte)(0.2126 * raw[i] + 0.7152 * raw[i + 1] + 0.0722 * raw[i + 2]);
                    }
                return new MatrixData<byte>(w, h, new List<byte[]> { gray });
            }
            else
            {
                var r = new byte[w * h];
                var g = new byte[w * h];
                var b = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * stride + x * 4;
                        int idx = (h - 1 - y) * w + x;
                        r[idx] = raw[i];
                        g[idx] = raw[i + 1];
                        b[idx] = raw[i + 2];
                    }
                var md = new MatrixData<byte>(w, h, new List<byte[]> { r, g, b });
                md.DefineDimensions(ColorAxis.CreateRgb());
                return md;
            }
        }
    }

}
