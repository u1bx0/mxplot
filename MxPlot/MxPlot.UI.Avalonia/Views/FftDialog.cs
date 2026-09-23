using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.Extensions.Fft;
using MxPlot.UI.Avalonia.Helpers;
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
        /// A status line naming the FFT backend in use. When native MKL could be loaded but is
        /// not (x64 process, provider not installed), an info button opens a note on how to
        /// install it.
        /// </summary>
        private static Control BuildBackendRow()
        {
            bool mklInUse = Fft2DExtension.IsNativeMklUsing;
            bool suggestMkl = !Fft2DExtension.IsNativeMklAvailable
                && RuntimeInformation.ProcessArchitecture == Architecture.X64;

            string backend = mklInUse ? "Intel MKL (native)"
                : Fft2DExtension.IsNativeMklAvailable ? "Managed (MKL available)"
                : "Managed";

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

            if (suggestMkl)
            {
                var info = new Button
                {
                    Content = "ℹ",
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
                ToolTip.SetTip(info, "How to make FFT faster");
                info.Flyout = new Flyout
                {
                    Placement = PlacementMode.BottomEdgeAlignedLeft,
                    Content = new TextBlock
                    {
                        Text = MklHint(),
                        FontSize = 11,
                        MaxWidth = 260,
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                row.Children.Add(info);
            }
            return row;
        }

        private static string MklHint()
        {
            string package = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "MathNet.Numerics.MKL.Win-x64"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "MathNet.Numerics.MKL.Linux-x64"
                : "the MathNet.Numerics MKL native package for this platform";
            return "FFT runs on a managed implementation.\n\n" +
                "Adding the Intel MKL native provider to the application makes it much faster " +
                $"(NuGet: {package}). It is picked up automatically the next time the application starts.";
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
