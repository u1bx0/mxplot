using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.UI.Avalonia.Formula;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Views
{
    /// <summary>
    /// The formula guide: a side panel a formula dialog opens with its "?" button. With the search box
    /// empty it lists the operators, the constants, the ways to read data and every function by group;
    /// typing narrows it to the entries whose name or description matches. The functions come from
    /// <see cref="FormulaFunctions.Docs"/>, so a new function appears here without further work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The text is long and scrolls inside the panel, so the panel needs a bounded height: either a
    /// window of a set size to stretch in, or a <see cref="Layoutable.Height"/> set on <see cref="View"/>
    /// by a dialog that sizes itself to its content.
    /// </para>
    /// <para>
    /// Headings are bold, what is written in a formula (<c>sin(x)</c>, <c>m1(x, y)</c>) is monospaced and
    /// semi-bold, and the explanations are a little lighter than the rest. The text can be selected and copied.
    /// </para>
    /// </remarks>
    internal sealed class FormulaGuide
    {
        /// <summary>How a piece of the guide is shown.</summary>
        private enum Look
        {
            /// <summary>Layout: indentation and gaps.</summary>
            Plain,

            /// <summary>A section title.</summary>
            Heading,

            /// <summary>What is written in a formula.</summary>
            Code,

            /// <summary>An explanation.</summary>
            Note,
        }

        private readonly record struct GuideRun(string Text, Look Look);

        private static readonly FontFamily Monospace = new("Cascadia Code, Consolas, monospace");

        /// <summary>
        /// How a formula reads a reference (<c>m1</c>, <c>m2</c>, ...): the forms the parser accepts, with
        /// what each gives. They are not functions, so they are described here rather than in
        /// <see cref="FormulaFunctions"/>.
        /// </summary>
        internal static IReadOnlyList<(string Signature, string Description)> ReferenceDocs { get; } =
        [
            ("m1", "The value at the pixel being computed. The data must have the grid of the result."),
            ("m1[ix, iy]", "The value at a pixel, e.g. m1[ix + 1, iy]. NaN outside the data."),
            ("m1[ix, iy, frame]", "The value at a pixel of another frame, by its number in the data (0, 1, 2, ...)."),
            ("m1[ix, iy, iz:2]",
                "The value at a pixel of the frame where the Z axis is at position 2, the other axes as in the frame being read. "
                + "Name an axis by its index variable (iz, ic, it, ia1, ...), and name several if needed: m1[ix, iy, iz:2, it:0]. "
                + "The position is an expression: m1[ix, iy, it: it - 1]."),
            ("m1(x, y)", "The value at a physical position, interpolated between pixels. Works when the grids differ. NaN outside the data."),
            ("m1(x, y, frame)", "The same for a given frame; axes can be named as in m1[ix, iy, iz:2]."),
            ("m1.Max()  m1.Min()", "The largest / smallest value of the frame being read. Normalize a frame: m1 / m1.Max()."),
            ("m1.Max(frame)  m1.Min(frame)", "The largest / smallest value of a given frame. NaN when there is no such frame."),
            ("m1.MaxAll()  m1.MinAll()",
                "The largest / smallest value over all frames. For Virtual data, only the frames loaded so far are counted."),
        ];

        /// <summary>The width a dialog grows by while the guide is shown.</summary>
        public const double PanelWidth = 290;

        private readonly TextBox _search;
        private readonly SelectableTextBlock _text;

        /// <summary>The panel; hidden until <see cref="IsVisible"/> is set.</summary>
        public Control View { get; }

        public FormulaGuide()
        {
            _search = new TextBox
            {
                Watermark = "Search functions…",
                FontSize = 11,
                Height = 24,
                MinHeight = 0,
                Padding = new Thickness(6, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            _text = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(6, 4),
            };
            var box = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(100, 160, 160, 160)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                MinHeight = 120,
                Child = new ScrollViewer
                {
                    Content = _text,
                    VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                },
            };

            // The lighter color of the explanations is derived from the text color, which is known only
            // once the text is in the tree and changes with the theme.
            _search.TextChanged += (_, _) => Refresh();
            _text.AttachedToVisualTree += (_, _) => Refresh();
            _text.ActualThemeVariantChanged += (_, _) => Refresh();
            Refresh();

            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 4 };
            layout.Children.Add(_search);
            Grid.SetRow(box, 1);
            layout.Children.Add(box);

            View = new Border
            {
                Width = PanelWidth - 10,
                Margin = new Thickness(10, 0, 0, 0),
                Child = layout,
                IsVisible = false,
            };
        }

        /// <summary>Whether the panel is shown.</summary>
        public bool IsVisible
        {
            get => View.IsVisible;
            set => View.IsVisible = value;
        }

        /// <summary>
        /// A small "?" toggle that shows and hides the guide, calling <paramref name="onToggled"/> with the
        /// new state so the dialog can change its width.
        /// </summary>
        public Button CreateToggleButton(Action<bool> onToggled)
        {
            var button = new Button
            {
                Content = "?",
                FontSize = 11,
                Width = 22,
                Height = 20,
                MinHeight = 0,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(button, "Show or hide the formula guide: operators, constants and functions");
            button.Click += (_, _) =>
            {
                IsVisible = !IsVisible;
                onToggled(IsVisible);
            };
            return button;
        }

        /// <summary>
        /// The guide text for <paramref name="query"/>, without formatting: the whole reference when it is
        /// blank, otherwise the functions whose name, signature or description contains it.
        /// </summary>
        internal static string Describe(string? query)
            => string.Join("\n", Build(query).Select(line => string.Concat(line.Select(run => run.Text)))).TrimEnd();

        // ── Display ───────────────────────────────────────────────────────────

        private void Refresh()
        {
            var inlines = _text.Inlines!;
            inlines.Clear();

            IBrush note = _text.Foreground is ISolidColorBrush solid
                ? new SolidColorBrush(solid.Color, solid.Opacity * 0.72)
                : Brushes.Gray;

            var lines = Build(_search.Text);
            for (int i = 0; i < lines.Count; i++)
            {
                foreach (var run in lines[i]) inlines.Add(ToInline(run, note));
                if (i < lines.Count - 1) inlines.Add(new LineBreak());
            }
        }

        private static Run ToInline(GuideRun run, IBrush note)
        {
            var inline = new Run(run.Text);
            switch (run.Look)
            {
                case Look.Heading:
                    inline.FontWeight = FontWeight.Bold;
                    inline.FontSize = 12;
                    break;
                case Look.Code:
                    inline.FontFamily = Monospace;
                    inline.FontWeight = FontWeight.SemiBold;
                    inline.FontSize = 10.5;
                    break;
                case Look.Note:
                    inline.Foreground = note;
                    break;
            }
            return inline;
        }

        // ── Content ───────────────────────────────────────────────────────────

        private static GuideRun P(string text) => new(text, Look.Plain);
        private static GuideRun H(string text) => new(text, Look.Heading);
        private static GuideRun C(string text) => new(text, Look.Code);
        private static GuideRun N(string text) => new(text, Look.Note);

        /// <summary>The guide as lines of formatted pieces.</summary>
        private static List<List<GuideRun>> Build(string? query)
        {
            query = query?.Trim();
            return string.IsNullOrEmpty(query) ? Overview() : Search(query);
        }

        private static List<List<GuideRun>> Overview()
        {
            var lines = new List<List<GuideRun>>();
            void Add(params GuideRun[] runs) => lines.Add([.. runs]);
            void Gap() => lines.Add([]);

            Add(H("OPERATORS"));
            Add(P("  "), C("+  -  *  /  %"));
            Add(P("  "), C("==  !=  <  <=  >  >="), N("    give 1 or 0"));
            Add(P("  "), C("&&  ||  !"), N("    non-zero counts as true"));
            Add(P("  "), C("a ? b : c"), N("    b when a is true, else c"));
            Add(P("  "), C("( )"), N("    grouping"));
            Add(N("  For a power write "), C("pow(a, b)"), N(", not ^."));
            Gap();
            Add(H("CONSTANTS"));
            Add(P("  "), C("pi  tau  e  true  false"));
            Gap();
            Add(N("Every value is a number. A read outside the data gives NaN, and NaN passes through"));
            Add(N("calculations; "), C("num(v)"), N(" replaces it. NaN is not equal to itself: "), C("v != v"), N(" tests for it."));
            Gap();
            Add(H("DATA  (m1, m2, ...)"));
            foreach (var (signature, description) in ReferenceDocs)
            {
                Add(P("  "), C(signature));
                Add(N("      " + description));
            }

            foreach (var group in FormulaFunctions.Docs.GroupBy(d => d.Category))
            {
                Gap();
                Add(H(group.Key.ToUpperInvariant()));

                // Self-explanatory functions share a line; the others get a line of explanation each.
                var plain = group.Where(d => d.Description == null).ToArray();
                if (plain.Length > 0)
                {
                    var runs = new List<GuideRun> { P("  ") };
                    for (int i = 0; i < plain.Length; i++)
                    {
                        if (i > 0) runs.Add(P("  "));
                        runs.Add(C(plain[i].Signature));
                    }
                    lines.Add(runs);
                }
                foreach (var doc in group.Where(d => d.Description != null))
                {
                    Add(P("  "), C(doc.Signature));
                    Add(N("      " + doc.Description));
                }
            }
            return lines;
        }

        private static List<List<GuideRun>> Search(string query)
        {
            bool Matches(string? s) => s != null && s.Contains(query, StringComparison.OrdinalIgnoreCase);

            // Functions whose name matches come before those that only mention the text.
            var byName = FormulaFunctions.Docs.Where(d => Matches(d.Name)).ToList();
            var others = FormulaFunctions.Docs.Where(d => !Matches(d.Name) && (Matches(d.Signature) || Matches(d.Description)));
            var found = byName.Concat(others).ToList();
            var references = ReferenceDocs.Where(r => Matches(r.Signature) || Matches(r.Description)).ToList();

            var lines = new List<List<GuideRun>>();
            if (found.Count == 0 && references.Count == 0)
            {
                lines.Add([N($"No function matches '{query}'.")]);
                return lines;
            }

            foreach (var (signature, description) in references)
            {
                lines.Add([C(signature)]);
                lines.Add([N("    " + description)]);
                lines.Add([]);
            }
            foreach (var doc in found)
            {
                lines.Add([C(doc.Signature)]);
                lines.Add([N("    " + (doc.Description ?? $"({doc.Category})"))]);
                lines.Add([]);
            }
            return lines;
        }
    }
}
