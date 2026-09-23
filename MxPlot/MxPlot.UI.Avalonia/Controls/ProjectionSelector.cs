using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Rendering;
using System;
using System.Linq;

namespace MxPlot.UI.Avalonia.Controls
{
    /// <summary>
    /// Identifies the orthogonal view plane.
    /// </summary>
    public enum ProjectionPlane
    {
        /// <summary>XY plane (project along Z).</summary>
        XY,
        /// <summary>XZ plane (project along Y).</summary>
        XZ,
        /// <summary>YZ plane (project along X).</summary>
        YZ,
    }

    /// <summary>
    /// Panel for configuring per-view projection settings in the orthogonal layout.
    /// Each view (XY, X-Z, Z-Y) has an independent enable checkbox and mode combo box.
    /// <para>
    /// Layout:
    /// <code>
    /// ┌─────────────────────────────┐
    /// │ X-Y (Z Projection)          │
    /// │ ☐  [Maximum▾]               │
    /// │ X-Z (Y Projection)          │
    /// │ ☑  [Maximum▾]               │
    /// │ Z-Y (X Projection)          │
    /// │ ☐  [Maximum▾]               │
    /// └─────────────────────────────┘
    /// </code>
    /// </para>
    /// </summary>
    public sealed class ProjectionSelector : UserControl
    {
        private readonly ViewRow _xyRow;
        private readonly ViewRow _xzRow;
        private readonly ViewRow _yzRow;

        private bool _suppressEvents;
        private bool _compositeActive;

        // XY row items. Indices 0-2 are the plain projections every row has; from
        // FirstColorCodedIndex on are the XY-only ColorCoded ones. Every index-based decision below
        // goes through these constants, so adding an item only means touching this block.
        private const int IdxMinimum = 1;
        private const int IdxAverage = 2;
        private const int FirstColorCodedIndex = 3;
        private const int IdxColorRgbMax = 3;
        private const int IdxColorRgbAdd = 4;
        private const int IdxColorMax = 5;
        private const int IdxColorMin = 6;

        // (label, tooltip) per item, in index order. The tooltip is shown when hovering the item in
        // the dropdown, so the modes explain themselves without a manual.
        private static readonly (string Label, string Tip)[] PlainItems =
        {
            ("Maximum", "Maximum intensity projection: each pixel shows its brightest value along the axis."),
            ("Minimum", "Minimum intensity projection: each pixel shows its darkest value along the axis."),
            ("Average", "Average intensity projection: each pixel shows the mean value along the axis."),
        };

        private static readonly (string Label, string Tip)[] XyItems = PlainItems.Concat(new[]
        {
            ("Color (RGB-Max)",
             "Every slice is tinted with its depth color, then R, G and B are max-combined separately. " +
             "Structures at different depths overlap and mix; colors outside the depth palette can appear."),
            ("Color (RGB-Add)",
             "Every slice is tinted with its depth color and the slices are added (clamped at 255), " +
             "as if each depth were an independent light source. Whites out on dense stacks - raise " +
             "the range's max to compensate."),
            ("Color (Max)",
             "Each pixel takes the depth color of the slice where it is brightest, scaled by that " +
             "brightness. The color reads directly as depth; overlaps are not shown."),
            ("Color (Min)",
             "Each pixel takes the depth color of the slice where it is darkest, scaled by that " +
             "value. The color reads directly as depth; overlaps are not shown."),
        }).ToArray();

        private static ComboBoxItem[] CreateItems((string Label, string Tip)[] definitions)
            => definitions.Select(d =>
            {
                var item = new ComboBoxItem { Content = d.Label };
                ToolTip.SetTip(item, d.Tip);
                return item;
            }).ToArray();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when any view's projection state changes.
        /// The handler receives (plane, enabled, mode).
        /// </summary>
        public event EventHandler<(ProjectionPlane Plane, bool IsEnabled, ProjectionMode Mode)>? SelectionChanged;

        /// <summary>
        /// Fired when a row's "create data" button is clicked. The handler receives the plane to
        /// bake and that row's currently selected mode as the starting point.
        /// </summary>
        public event EventHandler<(ProjectionPlane Plane, ProjectionMode Mode)>? CreateProjectedDataRequested;

        /// <summary>
        /// Shows or hides the per-row "create data" buttons.
        /// </summary>
        /// <param name="canProject">
        /// Whether an orthogonal axis is currently active. Projecting a single-axis volume down to
        /// one frame is still a useful operation, so this is deliberately not gated on hyperstacks.
        /// </param>
        public void UpdateProjectedDataAvailability(bool canProject)
        {
            _xyRow.CreateButton.IsVisible = canProject;
            _xzRow.CreateButton.IsVisible = canProject;
            _yzRow.CreateButton.IsVisible = canProject;
        }

        /// <summary>Whether projection is enabled for the given <paramref name="plane"/>.</summary>
        public bool IsProjectionEnabled(ProjectionPlane plane) => GetRow(plane).CheckBox.IsChecked == true;

        /// <summary>
        /// The projection mode selected for the given <paramref name="plane"/> -- the "which
        /// extremum/how do slices combine" half of the selection. For XY's ColorCoded items this
        /// still returns Maximum (RGB-Max/RGB-Add/Color(Max)) or Minimum (Color(Min)): coloring is
        /// an orthogonal concern layered on top, reported separately by <see cref="IsColorCoded"/>
        /// (and <see cref="GetColorCodedBlend"/>), not a new <see cref="ProjectionMode"/> value --
        /// Core's enum stays untouched.
        /// </summary>
        public ProjectionMode GetMode(ProjectionPlane plane) => GetRow(plane).ComboBox.SelectedIndex switch
        {
            IdxMinimum => ProjectionMode.Minimum,
            IdxAverage => ProjectionMode.Average,
            IdxColorMin => ProjectionMode.Minimum,   // Color (Min), XY row only
            _ => ProjectionMode.Maximum,   // Maximum, Color (RGB-Max), Color (Max), or any other row
        };

        /// <summary>
        /// Whether the given plane's current selection is a ColorCoded (depth-colour) mode rather
        /// than a plain intensity one. Always <c>false</c> for XZ/YZ, which don't offer it -- see
        /// section 3.3.1's decision to keep ColorCoded to the XY/MainView case only.
        /// </summary>
        public bool IsColorCoded(ProjectionPlane plane) =>
            plane == ProjectionPlane.XY && GetRow(plane).ComboBox.SelectedIndex >= FirstColorCodedIndex;

        /// <summary>
        /// For Color (RGB-Max) / (RGB-Add): the <see cref="ColorCodedBlend"/> the depth-colored
        /// slices are blended with (every slice colored by depth, then R/G/B combined by max or by
        /// sum, so overlaps mix). <c>null</c> for everything else, including
        /// Color(Max)/(Min), which
        /// pick a single winning slice instead.
        /// </summary>
        public ColorCodedBlend? GetColorCodedBlend(ProjectionPlane plane)
            => plane != ProjectionPlane.XY ? null : GetRow(plane).ComboBox.SelectedIndex switch
            {
                IdxColorRgbMax => ColorCodedBlend.Maximum,
                IdxColorRgbAdd => ColorCodedBlend.Additive,
                _ => null,
            };

        /// <summary>
        /// Toggles the checkbox (and the combo's enabled/opacity look) without touching
        /// <c>SelectedIndex</c>. Use this instead of <see cref="SetState"/> when the intent is just
        /// "turn the projection off/on, remembering whatever was selected" -- <see cref="SetState"/>
        /// always re-derives <c>SelectedIndex</c> from a <see cref="ProjectionMode"/>, which for XY
        /// cannot represent the ColorCoded items and would silently reset back to a plain mode. Closing the ColorCoded projection window is exactly this case.
        /// </summary>
        public void SetEnabled(ProjectionPlane plane, bool enabled)
        {
            _suppressEvents = true;
            try
            {
                var row = GetRow(plane);
                row.CheckBox.IsChecked = enabled;
                row.ComboBox.IsEnabled = enabled;
                row.ComboBox.Opacity = enabled ? 1.0 : 0.4;
                row.CreateButton.IsEnabled = enabled;
            }
            finally { _suppressEvents = false; }
        }

        /// <summary>
        /// Restricts the XY row's combo to Maximum/Minimum/Average while Composite mode is active.
        /// Composite and ColorCoded are mutually exclusive -- depth-coloring is defined in terms of
        /// a single winning axis index per pixel, which has no coherent meaning once the same pixel
        /// is already a blend of several Composite channels. Rather than let the
        /// user pick a ColorCoded item and then reject or crash on it, the option is simply not
        /// offered while Composite is on. If the row was already showing a ColorCoded item when
        /// Composite activates, falls back to Maximum and fires <see cref="SelectionChanged"/> like
        /// any other user-driven mode change, so <c>OrthogonalViewController</c>'s normal handling
        /// picks it up and recomputes a plain projection -- no special-casing needed there beyond
        /// its own independent guard in <c>ComputeXYProjectionAsync</c>.
        /// </summary>
        public void SetCompositeActive(bool active)
        {
            if (_compositeActive == active) return;
            _compositeActive = active;

            var combo = _xyRow.ComboBox;
            int oldIndex = combo.SelectedIndex;
            bool wasColorCoded = active && oldIndex >= FirstColorCodedIndex;

            _suppressEvents = true;
            try
            {
                combo.ItemsSource = CreateItems(active ? PlainItems : XyItems);
                // Composite -> restricted list: fall back to Maximum if the old selection no longer
                // exists (it was a ColorCoded item); indices 0-2 are otherwise unaffected either way.
                combo.SelectedIndex = wasColorCoded ? 0 : oldIndex;
            }
            finally { _suppressEvents = false; }

            if (wasColorCoded && _xyRow.CheckBox.IsChecked == true)
                SelectionChanged?.Invoke(this, (ProjectionPlane.XY, true, GetMode(ProjectionPlane.XY)));
        }

        /// <summary>
        /// Re-announces the XY row's current selection as if the user had just made it, so
        /// <c>OrthogonalViewController</c> recomputes the projection. For putting an open XY
        /// projection back after something tore the orthogonal views down underneath it - the row
        /// itself is unchanged, but the controller's projection state was reset. No-op when the row
        /// is off, so callers do not have to check first.
        /// </summary>
        public void RaiseXySelection()
        {
            if (_xyRow.CheckBox.IsChecked != true) return;
            SelectionChanged?.Invoke(this, (ProjectionPlane.XY, true, GetMode(ProjectionPlane.XY)));
        }

        /// <summary>Programmatically set the state for a specific view.</summary>
        public void SetState(ProjectionPlane plane, bool enabled, ProjectionMode mode)
        {
            _suppressEvents = true;
            try
            {
                var row = GetRow(plane);
                row.CheckBox.IsChecked = enabled;
                row.ComboBox.SelectedIndex = mode switch
                {
                    ProjectionMode.Minimum => IdxMinimum,
                    ProjectionMode.Average => IdxAverage,
                    _ => 0,
                };
                row.ComboBox.IsEnabled = enabled;
                row.ComboBox.Opacity = enabled ? 1.0 : 0.4;
                row.CreateButton.IsEnabled = enabled;
            }
            finally { _suppressEvents = false; }
        }

        /// <summary>
        /// Updates the view headers to reflect the actual frozen axis name.
        /// E.g. <c>UpdateAxisName("Time")</c> →
        /// "X-Y (Time Projection)", "X-Time (Y Projection)", "Time-Y (X Projection)".
        /// </summary>
        public void UpdateAxisName(string axisName)
        {
            _xyRow.Header.Text = $"X-Y ({axisName} Projection)";
            _xzRow.Header.Text = $"X-{axisName} (Y Projection)";
            _yzRow.Header.Text = $"{axisName}-Y (X Projection)";
        }

        // ── Constructor ───────────────────────────────────────────────────────

        public ProjectionSelector()
        {
            // Only XY offers the ColorCoded items -- ColorCoded is scoped to the MainView/XY
            // case (section 3.3.1); XZ/YZ keep the plain 3-item Maximum/Minimum/Average list.
            _xyRow = CreateViewRow("X-Y (Z Projection)", ProjectionPlane.XY, includeColorCoded: true);
            _xzRow = CreateViewRow("X-Z (Y Projection)", ProjectionPlane.XZ);
            _yzRow = CreateViewRow("Z-Y (X Projection)", ProjectionPlane.YZ);

            var stack = new StackPanel
            {
                Spacing = 4,
                Margin = new Thickness(6, 4),
            };
            stack.Children.Add(_xyRow.Panel);
            stack.Children.Add(_xzRow.Panel);
            stack.Children.Add(_yzRow.Panel);

            Content = stack;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the compact "create data" button that sits at the right end of a plane row.
        /// Starts disabled — <see cref="CreateViewRow"/> wires its <c>IsEnabled</c> to the row's own
        /// preview checkbox, since the dialog no longer carries an independent mode selector: the
        /// mode (and, for ColorCoded, Start/End/LUT/Invert) it bakes is now always whatever is
        /// currently live-previewed, so baking only makes sense once that preview is actually on.
        /// </summary>
        private Button CreateProjectedDataButton(ProjectionPlane plane)
        {
            var button = new Button
            {
                Content = new PathIcon
                {
                    Data = MenuIcons.CreateNewData,
                    Width = 12,
                    Height = 12,
                },
                Padding = new Thickness(3),
                MinWidth = 0,
                MinHeight = 0,
                Width = 22,
                Height = 22,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                IsVisible = false,
                IsEnabled = false,
            };
            ToolTip.SetTip(button,
                "Create a new dataset from this projection, so it can be navigated, saved and exported like ordinary data");
            button.Click += (_, _) =>
                CreateProjectedDataRequested?.Invoke(this, (plane, GetMode(plane)));
            return button;
        }

        private ViewRow CreateViewRow(string header, ProjectionPlane plane, bool includeColorCoded = false)
        {
            var label = new TextBlock
            {
                Text = header,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
            };

            var checkBox = new CheckBox
            {
                MinHeight = 0,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            var checkBoxWrapper = new Viewbox
            {
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Child = checkBox,
            };

            var comboBox = new ComboBox
            {
                ItemsSource = CreateItems(includeColorCoded ? XyItems : PlainItems),
                SelectedIndex = 0,
                FontSize = 11,
                MinHeight = 0,
                Height = 24,
                MinWidth = 90,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                IsEnabled = false,
                Opacity = 0.4,
            };

            var createButton = CreateProjectedDataButton(plane);

            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (_suppressEvents) return;
                bool enabled = checkBox.IsChecked == true;
                comboBox.IsEnabled = enabled;
                comboBox.Opacity = enabled ? 1.0 : 0.4;
                createButton.IsEnabled = enabled;
                SelectionChanged?.Invoke(this, (plane, enabled, GetMode(plane)));
            };

            comboBox.SelectionChanged += (_, _) =>
            {
                if (_suppressEvents) return;
                if (checkBox.IsChecked != true) return;
                SelectionChanged?.Invoke(this, (plane, true, GetMode(plane)));
            };

            var controlRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
            };
            controlRow.Children.Add(checkBoxWrapper);
            controlRow.Children.Add(comboBox);
            controlRow.Children.Add(createButton);

            var panel = new StackPanel { Spacing = 0 };
            panel.Children.Add(label);
            panel.Children.Add(controlRow);

            return new ViewRow(panel, label, checkBox, comboBox, createButton);
        }

        private ViewRow GetRow(ProjectionPlane plane) => plane switch
        {
            ProjectionPlane.XY => _xyRow,
            ProjectionPlane.XZ => _xzRow,
            ProjectionPlane.YZ => _yzRow,
            _ => throw new ArgumentOutOfRangeException(nameof(plane)),
        };

        private sealed record ViewRow(StackPanel Panel, TextBlock Header, CheckBox CheckBox, ComboBox ComboBox, Button CreateButton);
    }
}
