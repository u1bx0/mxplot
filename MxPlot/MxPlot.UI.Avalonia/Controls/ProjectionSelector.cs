using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MxPlot.Core.Processing;
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

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when any view's projection state changes.
        /// The handler receives (plane, enabled, mode).
        /// </summary>
        public event EventHandler<(ProjectionPlane Plane, bool IsEnabled, ProjectionMode Mode)>? SelectionChanged;

        /// <summary>Whether projection is enabled for the given <paramref name="plane"/>.</summary>
        public bool IsProjectionEnabled(ProjectionPlane plane) => GetRow(plane).CheckBox.IsChecked == true;

        /// <summary>The projection mode selected for the given <paramref name="plane"/>.</summary>
        public ProjectionMode GetMode(ProjectionPlane plane) => GetRow(plane).ComboBox.SelectedIndex switch
        {
            1 => ProjectionMode.Minimum,
            2 => ProjectionMode.Average,
            _ => ProjectionMode.Maximum,
        };

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
            _xyRow = CreateViewRow("X-Y (Z Projection)", ProjectionPlane.XY);
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

        private ViewRow CreateViewRow(string header, ProjectionPlane plane)
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
                FontSize = 11,
                MinHeight = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            var comboBox = new ComboBox
            {
                ItemsSource = new[] { "Maximum", "Minimum", "Average" },
                SelectedIndex = 0,
                FontSize = 11,
                MinHeight = 0,
                Height = 24,
                MinWidth = 90,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false,
                Opacity = 0.4,
            };

            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (_suppressEvents) return;
                bool enabled = checkBox.IsChecked == true;
                comboBox.IsEnabled = enabled;
                comboBox.Opacity = enabled ? 1.0 : 0.4;
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
            controlRow.Children.Add(checkBox);
            controlRow.Children.Add(comboBox);

            var panel = new StackPanel { Spacing = 0 };
            panel.Children.Add(label);
            panel.Children.Add(controlRow);

            return new ViewRow(panel, label, checkBox, comboBox);
        }

        private ViewRow GetRow(ProjectionPlane plane) => plane switch
        {
            ProjectionPlane.XY => _xyRow,
            ProjectionPlane.XZ => _xzRow,
            ProjectionPlane.YZ => _yzRow,
            _ => throw new ArgumentOutOfRangeException(nameof(plane)),
        };

        private sealed record ViewRow(StackPanel Panel, TextBlock Header, CheckBox CheckBox, ComboBox ComboBox);
    }
}
