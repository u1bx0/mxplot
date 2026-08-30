using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core.Processing;
using MxPlot.UI.Avalonia.Helpers;
using System;

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
        /// extremum/how do slices combine" half of the selection. For XY's Color(Max)/Color(Min)
        /// items (indices 3/4), this still returns Maximum/Minimum respectively: colouring is an
        /// orthogonal concern layered on top, reported separately by <see cref="IsColorCoded"/>,
        /// not a new <see cref="ProjectionMode"/> value (see Tests.Documents/Working/ColorCoded/
        /// ColorCoded_View_InitialDesign.md section 3.3.3 for why Core's enum stays untouched).
        /// </summary>
        public ProjectionMode GetMode(ProjectionPlane plane) => GetRow(plane).ComboBox.SelectedIndex switch
        {
            1 => ProjectionMode.Minimum,
            2 => ProjectionMode.Average,
            4 => ProjectionMode.Minimum,   // Color (Min), XY row only
            _ => ProjectionMode.Maximum,   // 0 = Maximum, 3 = Color (Max), or any other row
        };

        /// <summary>
        /// Whether the given plane's current selection is a ColorCoded (depth-colour) mode rather
        /// than a plain intensity one. Always <c>false</c> for XZ/YZ, which don't offer it -- see
        /// section 3.3.1's decision to keep ColorCoded to the XY/MainView case only.
        /// </summary>
        public bool IsColorCoded(ProjectionPlane plane) =>
            plane == ProjectionPlane.XY && GetRow(plane).ComboBox.SelectedIndex >= 3;

        /// <summary>
        /// Toggles the checkbox (and the combo's enabled/opacity look) without touching
        /// <c>SelectedIndex</c>. Use this instead of <see cref="SetState"/> when the intent is just
        /// "turn the projection off/on, remembering whatever was selected" -- <see cref="SetState"/>
        /// always re-derives <c>SelectedIndex</c> from a <see cref="ProjectionMode"/>, which for XY
        /// cannot represent Color(Max)/Color(Min) (indices 3/4) and would silently reset back to a
        /// plain mode. Closing the ColorCoded projection window is exactly this case.
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
        /// Composite and ColorCoded are mutually exclusive -- depth-colouring is defined in terms of
        /// a single winning axis index per pixel, which has no coherent meaning once the same pixel
        /// is already a blend of several Composite channels (see
        /// Tests.Documents/Working/ColorCoded/ColorCoded_View_InitialDesign.md). Rather than let the
        /// user pick Color(Max)/(Min) and then reject or crash on it, the option is simply not
        /// offered while Composite is on. If the row was already showing Color(Max)/(Min) when
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
            bool wasColorCoded = active && oldIndex >= 3;

            _suppressEvents = true;
            try
            {
                combo.ItemsSource = active
                    ? new[] { "Maximum", "Minimum", "Average" }
                    : new[] { "Maximum", "Minimum", "Average", "Color (Max)", "Color (Min)" };
                // Composite -> restricted list: fall back to Maximum if the old selection no longer
                // exists (it was Color(Max)/(Min)); indices 0-2 are otherwise unaffected either way.
                combo.SelectedIndex = wasColorCoded ? 0 : oldIndex;
            }
            finally { _suppressEvents = false; }

            if (wasColorCoded && _xyRow.CheckBox.IsChecked == true)
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
                    ProjectionMode.Minimum => 1,
                    ProjectionMode.Average => 2,
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
            // Only XY offers Color(Max)/Color(Min) -- ColorCoded is scoped to the MainView/XY
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
                ItemsSource = includeColorCoded
                    ? new[] { "Maximum", "Minimum", "Average", "Color (Max)", "Color (Min)" }
                    : new[] { "Maximum", "Minimum", "Average" },
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
