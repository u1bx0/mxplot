using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MxPlot.Core;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Defines a rendering context that carries mode-specific parameters for a single
    /// <see cref="IBitmapWriter.Render"/> invocation.
    /// <para>
    /// This is a marker interface with no members. Each rendering mode defines its own
    /// concrete context type (e.g. <see cref="LutRenderingContext"/>,
    /// <c>CompositeRenderingContext</c>) containing mode-specific data such as lookup tables,
    /// blend recipes, or frame indices.
    /// </para>
    /// </summary>
    public interface IRenderingContext { }

    /// <summary>
    /// Abstracts the rendering of <see cref="IMatrixData"/> into an Avalonia
    /// <see cref="WriteableBitmap"/>.
    /// <para>
    /// Each implementation encapsulates a specific rendering strategy (e.g. LUT-based
    /// pseudo-color, multi-channel composite, color-coded depth). The rendering parameters
    /// that vary per invocation are passed via an <see cref="IRenderingContext"/>; parameters
    /// that remain stable across frames are exposed as mutable properties on the writer instance.
    /// </para>
    /// </summary>
    public interface IBitmapWriter
    {
        /// <summary>
        /// Gets the element type of matrix data this writer can render
        /// (e.g. <c>typeof(ushort)</c>, <c>typeof(float)</c>).
        /// </summary>
        Type ValueType { get; }

        /// <summary>
        /// Gets or sets whether the bitmap is rendered with the Y-axis flipped so that
        /// data row 0 maps to the bitmap bottom and the last row maps to the top.
        /// Defaults to <c>true</c> (scientific convention: Y increases upward).
        /// </summary>
        bool FlipY { get; set; }

        /// <summary>
        /// Gets or sets parallel processing options for the pixel loop.
        /// Set to <c>null</c> for sequential execution (default).
        /// </summary>
        ParallelOptions? ParallelOptions { get; set; }

        /// <summary>
        /// Gets or sets a converter for struct-based value types
        /// (e.g. <c>Func&lt;Complex, double&gt;</c> for Complex data).
        /// Ignored for primitive numeric types. The implementation is responsible for
        /// casting this to the appropriate delegate type.
        /// </summary>
        object? StructValueConverter { get; set; }

        /// <summary>
        /// Renders the matrix data into the target bitmap using the specified context.
        /// </summary>
        /// <param name="source">Source matrix data. Must not be <c>null</c>.</param>
        /// <param name="target">
        /// Target <see cref="WriteableBitmap"/>. Must use <see cref="PixelFormat.Bgra8888"/>
        /// and match the source dimensions.
        /// </param>
        /// <param name="context">
        /// Mode-specific rendering parameters. Must be the context type expected by
        /// this writer implementation.
        /// </param>
        void Render(IMatrixData source, WriteableBitmap target, IRenderingContext context);
    }
}