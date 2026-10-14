using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.Extensions.Fft;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for configuring a 2D FFT. Returns the <see cref="FftParameters"/> and the dialog's
    /// checkboxes on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class FftDialog : ProcessingDialogBase
    {
        internal sealed record FftParameters(bool Inverse, ShiftOption Shift);

        // ── Factory ───────────────────────────────────────────────────────────

        /// <param name="owner">The window the dialog is shown over.</param>
        /// <param name="isMultiFrame">Whether the source data has more than one frame.</param>
        /// <param name="isLinkWindow">
        /// Whether the owning window is itself being kept live by a sync — if so, "Replace data"
        /// is disabled (see <see cref="ProcessingDialogBase"/>).
        /// </param>
        /// <param name="src">Source data, used only to decide whether to show the materialization warning.</param>
        internal static Task<DialogAnswer<FftParameters>?> ShowAsync(
            Window owner, bool isMultiFrame, bool isLinkWindow = false, IMatrixData? src = null)
        {
            var dlg = new FftDialog(isMultiFrame, isLinkWindow, src);
            return dlg.ShowDialog<DialogAnswer<FftParameters>?>(owner);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private FftDialog(bool isMultiFrame, bool isLinkWindow, IMatrixData? src)
            : base("FFT 2D", width: 300, isLinkWindow: isLinkWindow, canResize: false, src: src,
                   thisFrameOnlyDefault: isMultiFrame ? true : (bool?)null, showSyncSource: true)
        {
            // ── Direction ─────────────────────────────────────────────────────
            const string dirGroup = "FftDirection";
            var forwardRadio = MakeRadio("Forward", dirGroup, isChecked: true,
                "Spatial domain → frequency domain");
            var inverseRadio = MakeRadio("Inverse", dirGroup, isChecked: false,
                "Frequency domain → spatial domain. Forward followed by Inverse with the same DC position restores the original values.");
            var dirRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            dirRow.Children.Add(new TextBlock
            {
                Text = "Direction:",
                FontSize = 11,
                Width = 62,
                VerticalAlignment = VerticalAlignment.Center,
            });
            dirRow.Children.Add(forwardRadio);
            dirRow.Children.Add(inverseRadio);

            // ── DC position ───────────────────────────────────────────────────
            // Only the frequency-domain array's layout is chosen: DC at the center (fftshift layout) or
            // at the corner. The spatial-domain array always has its origin at the corner (index 0).
            var centerDcCheck = ControlFactory.MakeCheckBox(
                "Center DC (frequency domain)",
                hint: "Place the DC component at the center of the frequency-domain data: the result of Forward, the input of Inverse");
            centerDcCheck.IsChecked = true;
            centerDcCheck.Margin = new Thickness(0, 4, 0, -7);

            // ── Layout ────────────────────────────────────────────────────────
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(dirRow);
            panel.Children.Add(centerDcCheck);
            var frameOptions = BuildFrameOptions();
            if (frameOptions != null)
                panel.Children.Add(frameOptions);
            panel.Children.Add(BuildBackendRow());

            FinalizeContent(panel, onOk: () =>
            {
                ShiftOption shift = centerDcCheck.IsChecked == true ? ShiftOption.Centered : ShiftOption.None;
                Close(Answer(new FftParameters(Inverse: inverseRadio.IsChecked == true, Shift: shift)));
            }, okLabel: "Apply");
        }

        // ── Backend indicator ─────────────────────────────────────────────────

        /// <summary>
        /// How native Intel MKL relates to this process, as far as it can be told apart from
        /// outside Math.NET Numerics.
        /// </summary>
        private enum MklState
        {
            /// <summary>Native MKL is the active FFT backend.</summary>
            InUse,
            /// <summary>MKL loaded, but the managed implementation was selected explicitly.</summary>
            AvailableButManaged,
            /// <summary>No MKL exists for this process (not x64, or a platform Intel does not ship).</summary>
            Unsupported,
            /// <summary>MKL could be used here, but none of its files are present.</summary>
            NotInstalled,
            /// <summary>The native library is present, the managed provider assembly is not.</summary>
            ProviderFileMissing,
            /// <summary>The provider assembly is present but could not be loaded.</summary>
            ProviderUnloadable,
            /// <summary>The provider assembly is loaded, the native library is missing.</summary>
            NativeMissing,
            /// <summary>Every file is present, yet Math.NET Numerics did not accept the library.</summary>
            NativeUnusable,
        }

        /// <summary>
        /// Classifies why native MKL is or is not the current backend. The states from
        /// <see cref="MklState.ProviderFileMissing"/> on all mean that part of an MKL installation
        /// is present, which only happens when someone deliberately put it there.
        /// </summary>
        private static MklState GetMklState()
        {
            if (Fft2DExtension.IsNativeMklUsing)
                return MklState.InUse;
            if (Fft2DExtension.IsNativeMklAvailable)
                return MklState.AvailableButManaged;

            // Intel builds MKL for x86/x64 on Windows and Linux only. Anywhere else the managed
            // implementation is the only option and there is nothing for the user to act on.
            bool platformHasMkl = RuntimeInformation.ProcessArchitecture == Architecture.X64
                && (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    || RuntimeInformation.IsOSPlatform(OSPlatform.Linux));
            if (!platformHasMkl)
                return MklState.Unsupported;

            bool providerFilePresent = File.Exists(Fft2DExtension.MklProviderAssemblyPath);
            bool nativePresent = Fft2DExtension.FindMklNativeLibrary() != null;

            if (!providerFilePresent && !nativePresent)
                return MklState.NotInstalled;
            if (!providerFilePresent)
                return MklState.ProviderFileMissing;
            if (!Fft2DExtension.IsMklProviderAssemblyLoaded())
                return MklState.ProviderUnloadable;
            if (!nativePresent)
                return MklState.NativeMissing;
            return MklState.NativeUnusable;
        }

        private static bool IsProblem(MklState state) =>
            state is MklState.ProviderFileMissing or MklState.ProviderUnloadable
                  or MklState.NativeMissing or MklState.NativeUnusable;

        /// <summary>
        /// A status line naming the FFT backend in use. When MKL could be used but is not, a
        /// button opens the details: a suggestion when nothing is installed, a warning when an
        /// installation is present but incomplete or unusable. A partial installation is also
        /// summarized inline, since whoever placed those files is waiting for MKL to be in use.
        /// </summary>
        private static Control BuildBackendRow()
        {
            MklState state = GetMklState();
            bool problem = IsProblem(state);

            string backend = state switch
            {
                MklState.InUse => "Intel MKL (native)",
                MklState.AvailableButManaged => "Managed (MKL available)",
                _ => "Managed",
            };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 6, 0, 0),
            };
            row.Children.Add(new TextBlock
            {
                Text = $"Backend: {backend}",
                FontSize = 11,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center,
            });

            if (state == MklState.InUse)
                ToolTip.SetTip(row, Fft2DExtension.FourierProviderDescription);

            if (state == MklState.NotInstalled || problem)
            {
                var info = new Button
                {
                    Content = problem ? "⚠" : "ℹ",
                    FontSize = 11,
                    MinHeight = 0,
                    MinWidth = 0,
                    Width = 20,
                    Height = 20,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (problem)
                    info.Foreground = Brushes.Orange;
                ToolTip.SetTip(info, problem ? "Intel MKL could not be used" : "How to make FFT faster");
                info.Flyout = new Flyout
                {
                    Placement = PlacementMode.BottomEdgeAlignedLeft,
                    Content = new TextBlock
                    {
                        Text = MklDetail(state),
                        FontSize = 11,
                        MaxWidth = 260,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                row.Children.Add(info);
            }

            if (!problem)
                return row;

            var panel = new StackPanel { Spacing = 2 };
            panel.Children.Add(row);
            panel.Children.Add(new TextBlock
            {
                Text = MklProblemSummary(state),
                FontSize = 11,
                Foreground = Brushes.Orange,
                TextWrapping = TextWrapping.Wrap,
            });
            return panel;
        }

        private static string MklProblemSummary(MklState state) => state switch
        {
            MklState.ProviderFileMissing => "Intel MKL is incomplete: the provider assembly is missing.",
            MklState.ProviderUnloadable => "Intel MKL: the provider assembly could not be loaded.",
            MklState.NativeMissing => "Intel MKL is incomplete: the native library is missing.",
            _ => "Intel MKL was found but could not be initialized.",
        };

        /// <summary>
        /// The flyout text: what is wrong (or what is missing), then the files and the folder they
        /// belong in. Only reached on a platform MKL exists for, so the file names can be stated
        /// outright.
        /// </summary>
        private static string MklDetail(MklState state)
        {
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string nativeFile = windows ? "libMathNetNumericsMKL.dll" : "libMathNetNumericsMKL.so";
            string openMpFile = windows ? "libiomp5md.dll" : "libiomp5.so";
            string nativePackage = windows ? "MathNet.Numerics.MKL.Win-x64" : "MathNet.Numerics.MKL.Linux-x64";

            string opening = state switch
            {
                MklState.NotInstalled =>
                    "FFT runs on a managed implementation. The Intel MKL native provider makes it "
                    + "substantially faster.",
                MklState.ProviderFileMissing =>
                    "The Intel MKL native library is in place, but the managed provider assembly is "
                    + "not. Math.NET Numerics cannot reach the native library without it.",
                MklState.ProviderUnloadable =>
                    $"{Fft2DExtension.MklProviderAssemblyName}.dll is in place but could not be "
                    + "loaded. The file may be damaged, or built for another architecture.",
                MklState.NativeMissing =>
                    $"{Fft2DExtension.MklProviderAssemblyName}.dll is loaded, but the Intel MKL "
                    + "native library was not found.",
                _ =>
                    "Every Intel MKL file is in place, yet Math.NET Numerics did not accept the "
                    + "native library. Its version is most likely not the one this build expects.",
            };

            return opening + "\n\nExpected next to the executable:\n"
                + AppContext.BaseDirectory + "\n"
                + $"  {Fft2DExtension.MklProviderAssemblyName}.dll\n"
                + $"  {nativeFile}\n"
                + $"  {openMpFile}\n\n"
                + $"NuGet: {Fft2DExtension.MklProviderAssemblyName} carries the first file, "
                + $"{nativePackage} the other two. They are picked up the next time the application "
                + "starts.";
        }

        private static RadioButton MakeRadio(string label, string group, bool isChecked, string hint)
        {
            var radio = new RadioButton
            {
                Content = label,
                GroupName = group,
                FontSize = 11,
                IsChecked = isChecked,
                MinHeight = 0,
                Height = 20,
            };
            radio.Classes.Add("compact");
            ToolTip.SetTip(radio, hint);
            return radio;
        }
    }
}
