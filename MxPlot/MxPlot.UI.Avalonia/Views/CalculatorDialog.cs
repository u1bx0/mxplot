using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Commands;
using MxPlot.UI.Avalonia.Formula;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// Asks for the formula of the Calculator command: new values computed from the window's own data
    /// (<c>m1</c>), the pixel coordinates and the position along the data's axes. Returns the formula and
    /// the dialog's checkboxes on OK, or <c>null</c> on cancel.
    /// </summary>
    /// <remarks>
    /// Below the formula the dialog names only what depends on the window: how its data is read and which
    /// variables it has. Operators and functions are in the guide the "?" button opens at the right. The
    /// Up and Down keys offer the formulas applied before (<see cref="FormulaHistory.Calculator"/>).
    /// </remarks>
    internal sealed class CalculatorDialog : ProcessingDialogBase
    {
        internal sealed record CalculatorParameters(string Formula);

        /// <summary>The formula the dialog starts on when none was applied before: the data unchanged.</summary>
        internal const string DefaultFormula = "m1";

        /// <summary>The references a Calculator formula can read: the window's own data.</summary>
        internal static IReadOnlyList<string> References { get; } = ["m1"];

        private const double BaseWidth = 440;

        /// <summary>The least height the guide gets, so that it stays usable beside a short dialog.</summary>
        private const double MinGuideHeight = 240;

        /// <param name="isMultiFrame">Whether "This frame only" is a real choice for the data.</param>
        /// <param name="isLinkWindow">Whether the owning window is itself kept live by a sync, so "Replace data" is disabled.</param>
        /// <param name="src">Source data, used only to decide whether to show the materialization warning.</param>
        /// <param name="axes">The axes of the data the formula is written for; each gives the formula two variables.</param>
        /// <param name="formula">The formula the dialog starts on.</param>
        /// <param name="editOnly">
        /// Whether only the formula is asked for, without the "This frame only", "Sync source data" and
        /// "Replace data" choices: the formula of an existing result is being changed.
        /// </param>
        internal static Task<DialogAnswer<CalculatorParameters>?> ShowAsync(
            Window owner, bool isMultiFrame, bool isLinkWindow, IMatrixData? src, IReadOnlyList<Axis> axes,
            string formula = DefaultFormula, bool editOnly = false)
            => new CalculatorDialog(isMultiFrame, isLinkWindow, src, axes, formula, editOnly)
                .ShowDialog<DialogAnswer<CalculatorParameters>?>(owner);

        private CalculatorDialog(bool isMultiFrame, bool isLinkWindow, IMatrixData? src, IReadOnlyList<Axis> axes,
            string formula, bool editOnly)
            : base(editOnly ? "Edit Formula" : "Calculator", width: BaseWidth, isLinkWindow: isLinkWindow, canResize: true,
                   src: editOnly ? null : src,
                   thisFrameOnlyDefault: !editOnly && isMultiFrame ? false : (bool?)null,
                   showReplaceData: !editOnly, showSyncSource: !editOnly)
        {
            var variables = FormulaEvaluator.VariableNames(axes);
            var input = new FormulaInput(formula, text => CompiledFormula.Check(text, variables, References, [axes.Select(a => a.Name).ToArray()]),
                history: FormulaHistory.Calculator);

            var guide = new FormulaGuide();
            guide.View.Margin = new Thickness(0, 14, 16, 14);
            // The dialog takes its height from its content, and the guide's text is long: left alone it
            // would be measured in full and stretch the dialog beyond the screen. The guide is therefore
            // given the height the rest of the dialog has, and scrolls inside it.
            Control? main = null;
            var guideBtn = guide.CreateToggleButton(shown =>
            {
                if (shown && main != null) guide.View.Height = Math.Max(MinGuideHeight, main.Bounds.Height);
                // Added to the width the user may have chosen, rather than setting a fixed one.
                double delta = shown ? FormulaGuide.PanelWidth : -FormulaGuide.PanelWidth;
                MinWidth += delta;
                Width += delta;
            });
            // Widening is what resizing is for here: a long formula wraps less.
            MinWidth = 320;

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(new TextBlock
            {
                Text = "Result =",
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(guideBtn, 1);
            header.Children.Add(guideBtn);

            string hint = "ix iy : pixel index    x y : physical coordinates    r = sqrt(x*x + y*y)";
            if (axes.Count > 0)
            {
                // The variables come in pairs after the pixel variables: index, then coordinate.
                int first = FormulaEvaluator.PixelVariables.Count;
                hint += "\n" + string.Join("    ", axes.Select((a, i) =>
                    $"{variables[first + 2 * i]} {variables[first + 1 + 2 * i]} : {a.Name} index, value"));
            }
            hint += "\nm1 (m1[ix, iy] or m1(x, y)) : this data";

            var content = new StackPanel { Spacing = 3 };
            content.Children.Add(header);
            content.Children.Add(input.View);
            content.Children.Add(input.ErrorText);
            content.Children.Add(new TextBlock
            {
                FontSize = 10.5,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Text = hint,
            });
            var frameOptions = BuildFrameOptions();
            if (frameOptions != null) content.Children.Add(frameOptions);

            FinalizeContent(content, onOk: () =>
            {
                // Enter in the box is not wired to OK, so the history list never competes with it.
                if (input.Validate()) Close(Answer(new CalculatorParameters(input.Text.Trim())));
            }, okLabel: editOnly ? "OK" : "Apply");

            // The guide sits to the right of everything the base class laid out.
            main = Content as Control;
            if (main != null)
            {
                Content = null;
                var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                layout.Children.Add(main);
                guide.View.VerticalAlignment = VerticalAlignment.Top;
                // The guide is as tall as the rest of the dialog, so its bottom edge stays level with the buttons.
                main.SizeChanged += (_, e) =>
                {
                    if (guide.View.IsVisible) guide.View.Height = Math.Max(MinGuideHeight, e.NewSize.Height);
                };
                Grid.SetColumn(guide.View, 1);
                layout.Children.Add(guide.View);
                Content = layout;
            }

            Opened += (_, _) =>
            {
                input.Box.Focus();
                input.Box.SelectAll();
            };
        }
    }
}
