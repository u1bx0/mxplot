using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Modal dialog for baking an orthogonal projection into a new dataset.
    /// <list type="bullet">
    ///   <item><b>All positions</b> — one projected frame per combination of the remaining axes, so the
    ///   result stays navigable and can be saved or exported as a movie.</item>
    ///   <item><b>This position only</b> — a single projected frame at the current indices.</item>
    /// </list>
    /// The projection mode (Maximum/Minimum/Average, plus ColorCoded's Color(Max)/Color(Min)) is not
    /// editable here — it is fixed to whatever <c>ProjectionSelector</c>'s combo showed at the moment
    /// the "Create Data" button was pressed (<c>initialMode</c>/<c>colorCoded</c>
    /// of <see cref="ShowAsync"/>) and only shown read-only, since a numeric-only dialog gives no visual
    /// feedback to pick a different mode by.
    /// Returns <see cref="CreateProjectionParameters"/> on OK, or <c>null</c> on cancel.
    /// </summary>
    internal sealed class CreateProjectionDialog : ProcessingDialogBase
    {
        internal sealed record CreateProjectionParameters(
            bool ThisPositionOnly,
            bool ReplaceData);

        /// <summary>
        /// Read-only ColorCoded state to display when baking a ColorCoded projection — all values
        /// are inherited as-is from the live view (<c>OrthogonalViewController.ColorCodedParams</c>),
        /// never edited in this dialog.
        /// </summary>
        internal readonly record struct ColorCodedBakeInfo(int Start, int End, string LutName, bool Invert);

        /// <param name="planeLabel">The resulting image plane, e.g. <c>"X-Y"</c> or <c>"X-Z"</c>.</param>
        /// <param name="alongLabel">The direction collapsed by the projection, e.g. <c>"Z"</c> or <c>"Y"</c>.</param>
        /// <param name="axisName">The orthogonal axis supplying the volume.</param>
        /// <param name="colorCoded">
        /// Pass the live ColorCoded state to bake a depth-colour projection instead of a plain
        /// intensity one — shows an extra read-only info block and an axis-rename preview.
        /// </param>
        internal static Task<CreateProjectionParameters?> ShowAsync(
            Window owner,
            ProjectionMode initialMode,
            string planeLabel,
            string alongLabel,
            string axisName,
            IReadOnlyList<Axis> axes,
            bool isLinkWindow = false,
            ColorCodedBakeInfo? colorCoded = null)
        {
            var dlg = new CreateProjectionDialog(initialMode, planeLabel, alongLabel, axisName, axes, isLinkWindow, colorCoded);
            return dlg.ShowDialog<CreateProjectionParameters?>(owner);
        }

        private CreateProjectionDialog(
            ProjectionMode initialMode,
            string planeLabel,
            string alongLabel,
            string axisName,
            IReadOnlyList<Axis> axes,
            bool isLinkWindow,
            ColorCodedBakeInfo? colorCoded)
            : base($"Create {planeLabel} Projected Data", width: 340, isLinkWindow: isLinkWindow)
        {
            const double LW = 90;

            // ── Mode (read-only) ─────────────────────────────────────────────
            string modeText = colorCoded != null
                ? (initialMode == ProjectionMode.Minimum ? "Color (Minimum)" : "Color (Maximum)")
                : initialMode switch
                {
                    ProjectionMode.Minimum => "Minimum",
                    ProjectionMode.Average => "Average",
                    _ => "Maximum",
                };

            var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            modeRow.Children.Add(new TextBlock
            {
                Text = "Mode:",
                FontSize = 11,
                Width = LW,
                VerticalAlignment = VerticalAlignment.Center,
            });
            modeRow.Children.Add(new TextBlock
            {
                Text = modeText,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

            // ── This position only ────────────────────────────────────────────
            var survivingAxes = axes
                .Where(a => !string.Equals(a.Name, axisName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            bool hasSurvivors = survivingAxes.Count > 0;

            var thisPositionCheck = ControlFactory.MakeCheckBox(
                "This position only",
                hint: "Project a single frame at the current indices instead of every combination");
            thisPositionCheck.IsVisible = hasSurvivors;

            // ── Description label ─────────────────────────────────────────────
            var descLabel = new TextBlock
            {
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                MaxWidth = 280,
                Margin = new Thickness(0, 2, 0, 0),
            };

            string survivorNames = string.Join(", ", survivingAxes.Select(a => a.Name));
            int frameCount = survivingAxes.Aggregate(1, (acc, a) => acc * a.Count);

            void UpdateDesc()
            {
                if (!hasSurvivors || thisPositionCheck.IsChecked == true)
                {
                    string at = hasSurvivors
                        ? " at " + string.Join(", ", survivingAxes.Select(a => $"{a.Name}={a.Index}"))
                        : "";
                    descLabel.Text =
                        $"A single {planeLabel} image, projected along {alongLabel} through {axisName}{at}.";
                }
                else
                {
                    descLabel.Text =
                        $"{frameCount} {planeLabel} images, projected along {alongLabel} through {axisName} — " +
                        $"one per combination of {survivorNames}, which stay navigable in the result.";
                }
            }

            thisPositionCheck.IsCheckedChanged += (_, _) => UpdateDesc();
            UpdateDesc();

            // ── ColorCoded read-only info block ─────────────────────────────────
            Control? colorInfoBlock = null;
            if (colorCoded is { } info)
            {
                var infoPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2, 0, 0) };
                infoPanel.Children.Add(MakeInfoRow(LW, "Axis:", axisName));
                infoPanel.Children.Add(MakeInfoRow(LW, "Start–End:", $"{info.Start}–{info.End}"));
                infoPanel.Children.Add(MakeInfoRow(LW, "LUT:", info.LutName));
                infoPanel.Children.Add(MakeInfoRow(LW, "Invert:", info.Invert ? "Yes" : "No"));
                infoPanel.Children.Add(new TextBlock
                {
                    FontSize = 10,
                    FontStyle = FontStyle.Italic,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 280,
                    Margin = new Thickness(0, 4, 0, 0),
                    Text = $"{axisName} → {axisName} (Colored) (R,G,B: byte data)",
                });
                colorInfoBlock = infoPanel;
            }

            // ── Assemble ──────────────────────────────────────────────────────
            var mainContent = new StackPanel { Spacing = 6 };
            mainContent.Children.Add(modeRow);
            if (hasSurvivors) mainContent.Children.Add(thisPositionCheck);
            mainContent.Children.Add(descLabel);
            if (colorInfoBlock != null) mainContent.Children.Add(colorInfoBlock);

            FinalizeContent(mainContent, onOk: () =>
            {
                Close(new CreateProjectionParameters(
                    !hasSurvivors || thisPositionCheck.IsChecked == true,
                    ReplaceDataCheckBox.IsChecked == true));
            }, okLabel: "Create");
        }

        private static StackPanel MakeInfoRow(double labelWidth, string label, string value)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 10,
                Width = labelWidth,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return row;
        }
    }
}
