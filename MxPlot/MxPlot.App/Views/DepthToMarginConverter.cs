using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace MxPlot.App.Views
{
    /// <summary>
    /// Converts a tree nesting depth (<see cref="int"/>) to a left-indent <see cref="Thickness"/>
    /// for the dashboard list view's child-window indentation.
    /// Vertical margins (top/bottom = 1) are preserved regardless of depth.
    /// </summary>
    public sealed class DepthToMarginConverter : IValueConverter
    {
        /// <summary>Left-indent in device-independent pixels per depth level.</summary>
        public double IndentWidth { get; set; } = 18.0;

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int depth = value is int d ? d : 0;
            return new Thickness(depth * IndentWidth, 1, 0, 1);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
