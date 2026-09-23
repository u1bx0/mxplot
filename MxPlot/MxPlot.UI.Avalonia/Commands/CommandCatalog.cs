using Avalonia.Media;
using MxPlot.Core;
using MxPlot.UI.Avalonia.Helpers;
using MxPlot.UI.Avalonia.Views;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MxPlot.UI.Avalonia.Commands
{
    /// <summary>A command as the menu shows it.</summary>
    /// <param name="Group">The menu group it belongs to.</param>
    /// <param name="Label">The menu item text.</param>
    /// <param name="Hint">The menu item tooltip.</param>
    /// <param name="Icon">The menu item icon.</param>
    /// <param name="Command">What runs when the item is chosen.</param>
    /// <param name="IsAvailable">Whether the item is offered for a window; <see langword="null"/> means always.</param>
    internal sealed record CommandDescriptor(
        string Group, string Label, string Hint, Geometry? Icon, IProcessingCommand Command,
        Func<ICommandHost, bool>? IsAvailable = null);

    /// <summary>The commands the hamburger menu builds its groups from.</summary>
    internal static class CommandCatalog
    {
        /// <summary>Started from an axis's context menu rather than from the hamburger menu, so it is not listed in <see cref="All"/>.</summary>
        public static IProcessingCommand ExtractDimension { get; } = new ExtractDimensionCommand();

        public static IReadOnlyList<CommandDescriptor> All { get; } =
        [
            new("Geometry & Dimensions", "Reverse Stack…", "Reverse the frame order along a selected axis",
                MenuIcons.Layers, new ReverseStackCommand(), host => host.Data?.FrameCount > 1),
            new("Geometry & Dimensions", "Transpose…", "Swap X and Y about the origin [0,0] (bottom-left corner)",
                MenuIcons.AutoFix, new TransposeCommand()),
            new("Geometry & Dimensions", "Resample…", "Change the pixel count, keeping the physical extent",
                MenuIcons.AutoFix, new ResampleCommand()),

            new("Filters", "Median…", "Apply a median (hot-pixel removal) filter",
                MenuIcons.AutoFix, new SpatialFilterCommand(SpatialFilterDialog.KernelType.Median)),
            new("Filters", "Gaussian…", "Apply a Gaussian (smoothing) filter",
                MenuIcons.AutoFix, new SpatialFilterCommand(SpatialFilterDialog.KernelType.Gaussian)),

            new("Intensity", "Normalize…", "Scale pixel values so the maximum equals a target value",
                MenuIcons.AutoFix, new NormalizeCommand()),
            new("Intensity", "Log Transform…", "Apply a logarithm transform (ln / log₁₀ / log₂); outputs double",
                MenuIcons.AutoFix, new LogTransformCommand()),

            new("Frequency", "FFT 2D…", "Forward or inverse 2D Fourier transform; outputs complex data",
                MenuIcons.AutoFix, new FftCommand()),

            // Only meaningful when there is more than one channel to collapse.
            new("Conversion", "Convert to Grayscale…",
                "Collapses the Channel axis into a single grayscale channel and lets you choose whether to replace the current window or open a new one.",
                MenuIcons.Grayscale, new GrayscaleCommand(),
                host => host.Data?.Axes.FindAxis("Channel")?.Count > 1),
        ];

        /// <summary>The items of a menu group that are offered for <paramref name="host"/>.</summary>
        public static IEnumerable<CommandDescriptor> InGroup(string group, ICommandHost host)
            => All.Where(c => c.Group == group && (c.IsAvailable?.Invoke(host) ?? true));
    }
}
