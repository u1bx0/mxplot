using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MxPlot.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Defines the rendering recipe (styling, contrast, and color) for a single layer/frame 
    /// in a composite drawing.
    /// </summary>
    /// <param name="IsVisible">Indicates whether the layer/frame is visible.</param>
    /// <param name="ColorArgb">The ARGB color value for the layer/frame.</param>
    /// <param name="ValueMin">The minimum value for the layer/frame.</param>
    /// <param name="ValueMax">The maximum value for the layer/frame.</param>
    /// <param name="Gain">The gain applied to the layer/frame. Defaults to 1.0.</param>
    /// <param name="Gamma">The gamma correction applied to the layer/frame. Defaults to 1.0.</param>
    /// <param name="ValueConverter">
    /// An optional converter for struct-based value types (e.g., <c>Func&lt;Complex, double&gt;</c> for Complex data).
    /// Ignored for primitive types such as ushort and float.
    /// The render implementation is responsible for casting this to the appropriate delegate type.
    /// </param>
    public record BlendRecipe(
        bool IsVisible,
        int ColorArgb,
        double ValueMin,
        double ValueMax,
        double Gain = 1.0,
        double Gamma = 1.0,
        object? ValueConverter = null);

    /// <summary>
    /// Specifies how multiple layers are blended together during composite rendering.
    /// </summary>
    public enum BlendMode
    {
        /// <summary>Pixel values are added together. Best for multi-color fluorescence.</summary>
        Additive,

        /// <summary>The maximum pixel value among layers is kept. Best for depth/time color coding.</summary>
        Maximum
    }

    /// <summary>
    /// A high-performance writer that composites multiple matrix frames into a single WriteableBitmap
    /// using pre-calculated look-up tables (LUTs) and multi-threading.
    /// </summary>
    public class CompositeBitmapWriter
    {
        /// <summary>
        /// The internal delegate signature for type-specific rendering loops.
        /// </summary>
        private unsafe delegate void RenderCompositeDelegate(
            IMatrixData source,
            int[] frameIndices,
            int* targetPtr,
            int strideInts,
            int width,
            int height,
            BlendMode blendMode);

        /// <summary>Gets the primitive type of the data being rendered.</summary>
        public Type ValueType { get; }

        /// <summary>Optional settings for parallel loop execution.</summary>
        public ParallelOptions? ParallelOptions { get; set; }

        /// <summary>
        /// When true, the bitmap is rendered with Y-axis flipped so that data row 0 (YMin)
        /// maps to the bitmap bottom. Defaults to true (scientific convention: Y increases upward).
        /// </summary>
        public bool FlipY { get; set; } = true;

        private RenderCompositeDelegate RenderInternal { get; }

        // --- Direct-index LUT (byte / ushort / short) ---
        // Key: layer index. Size: 256 (byte), 65536 (ushort), 65536 (short, accessed with +32768 offset).
        // null entry means the layer is inactive (not visible or zero gain).
        private int[][]? _cachedDirectColorMaps;

        // --- Scale/offset LUT (int / float / double / Complex) ---
        // Size: ScaleLutSize elements per layer. Raw value is mapped to a LUT index via
        // cached scale/offset before lookup. null entry means the layer is inactive.
        private int[][]? _cachedScaleColorMaps;
        private double[]? _cachedScales;
        private double[]? _cachedOffsets;
        private const int ScaleLutSize = 4096;

        // Per-layer converters for Complex. Populated alongside _cachedScaleColorMaps.
        private Func<Complex, double>[]? _cachedConverters;

        /// <summary>
        /// Initializes a new instance of the <see cref="CompositeBitmapWriter"/> class for the specified value type.
        /// </summary>
        public CompositeBitmapWriter(Type type)
        {
            ValueType = type ?? throw new ArgumentNullException(nameof(type));

            unsafe
            {
                RenderInternal = type switch
                {
                    Type t when t == typeof(byte)    => RenderByteComposite,
                    Type t when t == typeof(ushort)  => RenderUShortComposite,
                    Type t when t == typeof(short)   => RenderShortComposite,
                    Type t when t == typeof(int)     => RenderIntComposite,
                    Type t when t == typeof(float)   => RenderFloatComposite,
                    Type t when t == typeof(double)  => RenderDoubleComposite,
                    Type t when t == typeof(Complex) => RenderComplexComposite,
                    _ => throw new NotSupportedException($"Type {type} is not supported.")
                };
            }
        }

        /// <summary>
        /// Creates a new <see cref="WriteableBitmap"/> by compositing the specified frames
        /// from <paramref name="source"/> using the provided recipes.
        /// </summary>
        /// <param name="source">The source matrix data.</param>
        /// <param name="frameIndices">Frame indices to composite. Must match <paramref name="recipes"/> length.</param>
        /// <param name="recipes">Per-layer rendering recipes (color, min/max, gain, gamma).</param>
        /// <param name="blendMode">How layers are blended together.</param>
        /// <param name="dpi">Screen DPI. Defaults to 96×96.</param>
        public static WriteableBitmap CreateBitmap(
            IMatrixData source,
            int[] frameIndices,
            IList<BlendRecipe> recipes,
            BlendMode blendMode = BlendMode.Additive,
            global::Avalonia.Vector dpi = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var effectiveDpi = dpi == default ? new global::Avalonia.Vector(96, 96) : dpi;
            var bmp = new WriteableBitmap(
                new global::Avalonia.PixelSize(source.XCount, source.YCount),
                effectiveDpi,
                global::Avalonia.Platform.PixelFormat.Bgra8888,
                global::Avalonia.Platform.AlphaFormat.Premul);

            var writer = new CompositeBitmapWriter(source.ValueType);
            writer.Render(source, frameIndices, bmp, recipes, blendMode);
            return bmp;
        }

        /// <summary>
        /// Composites multiple frames from the source data into the target bitmap based on the provided recipes.
        /// </summary>
        public void Render(
            IMatrixData source,
            int[] frameIndices,
            WriteableBitmap target,
            IList<BlendRecipe> recipes,
            BlendMode blendMode = BlendMode.Additive)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (recipes == null) throw new ArgumentNullException(nameof(recipes));
            if (frameIndices.Length != recipes.Count)
                throw new ArgumentException("The number of frame indices must match the number of recipes.");
            if (source.ValueType != ValueType)
                throw new ArgumentException($"Source type {source.ValueType} does not match writer type {ValueType}.");
            if (target.PixelSize.Width != source.XCount || target.PixelSize.Height != source.YCount)
                throw new ArgumentException(
                    $"Target bitmap size ({target.PixelSize.Width}×{target.PixelSize.Height}) " +
                    $"does not match source data size ({source.XCount}×{source.YCount}).");

            // 1. Pre-calculate LUTs based on the data type to eliminate math in the inner loop
            if (ValueType == typeof(byte))
                UpdateDirectColorMaps(recipes, size: 256, indexOffset: 0);
            else if (ValueType == typeof(ushort))
                UpdateDirectColorMaps(recipes, size: 65536, indexOffset: 0);
            else if (ValueType == typeof(short))
                UpdateDirectColorMaps(recipes, size: 65536, indexOffset: 32768);
            else
                UpdateScaleColorMaps(recipes);

            // 2. Lock the bitmap and get the pointer, applying FlipY the same way as BitmapWriter
            using var fb = target.Lock();
            unsafe
            {
                int height = source.YCount;
                int posStride = fb.RowBytes / 4;
                int* targetPtr;
                int strideInts;
                if (FlipY)
                {
                    targetPtr = (int*)fb.Address + (height - 1) * posStride;
                    strideInts = -posStride;
                }
                else
                {
                    targetPtr = (int*)fb.Address;
                    strideInts = posStride;
                }

                // 3. Dispatch to the strongly-typed rendering loop
                RenderInternal(source, frameIndices, targetPtr, strideInts, source.XCount, height, blendMode);
            }
        }

        // =================================================================================
        // LUT Builder Helpers
        // =================================================================================

        /// <summary>
        /// Builds a color LUT for each layer where a raw integer value is the direct LUT index.
        /// Used for byte (size=256, offset=0), ushort (size=65536, offset=0),
        /// and short (size=65536, offset=32768).
        /// All of ValueMin, ValueMax, Gain, Gamma, and Color are baked in.
        /// </summary>
        private void UpdateDirectColorMaps(IList<BlendRecipe> recipes, int size, int indexOffset)
        {
            int layerCount = recipes.Count;
            _cachedDirectColorMaps = new int[layerCount][];

            for (int i = 0; i < layerCount; i++)
            {
                var recipe = recipes[i];
                if (!recipe.IsVisible || recipe.Gain <= 0)
                {
                    _cachedDirectColorMaps[i] = null!;
                    continue;
                }

                double range = recipe.ValueMax - recipe.ValueMin;
                if (range == 0) range = 1.0;

                int baseR = (recipe.ColorArgb >> 16) & 0xFF;
                int baseG = (recipe.ColorArgb >> 8) & 0xFF;
                int baseB = recipe.ColorArgb & 0xFF;

                int[] map = new int[size];
                for (int v = 0; v < size; v++)
                {
                    double t = ((v - indexOffset) - recipe.ValueMin) / range;
                    t = Math.Clamp(t, 0.0, 1.0);
                    map[v] = PackLayerColor(t, recipe.Gain, recipe.Gamma, baseR, baseG, baseB);
                }
                _cachedDirectColorMaps[i] = map;
            }
        }

        /// <summary>
        /// Builds <see cref="ScaleLutSize"/>-element color LUTs for scale/offset types
        /// (int, float, double, Complex). All of ValueMin, ValueMax, Gain, Gamma, and Color
        /// are baked in. Also caches scale/offset arrays for the render loops.
        /// For Complex, each recipe's <see cref="BlendRecipe.ValueConverter"/> is cast to
        /// <c>Func&lt;Complex, double&gt;</c> and stored in <see cref="_cachedConverters"/>.
        /// </summary>
        private void UpdateScaleColorMaps(IList<BlendRecipe> recipes)
        {
            int layerCount = recipes.Count;
            _cachedScaleColorMaps = new int[layerCount][];
            _cachedScales = new double[layerCount];
            _cachedOffsets = new double[layerCount];

            bool isComplex = ValueType == typeof(Complex);
            _cachedConverters = isComplex ? new Func<Complex, double>[layerCount] : null;

            for (int i = 0; i < layerCount; i++)
            {
                var recipe = recipes[i];
                if (!recipe.IsVisible || recipe.Gain <= 0)
                {
                    _cachedScaleColorMaps[i] = null!;
                    continue;
                }

                if (isComplex)
                {
                    _cachedConverters![i] = recipe.ValueConverter as Func<Complex, double>
                        ?? throw new InvalidOperationException(
                            $"Layer {i}: BlendRecipe.ValueConverter must be Func<Complex, double> for Complex rendering.");
                }

                double range = recipe.ValueMax - recipe.ValueMin;
                if (range == 0) range = 1.0;
                _cachedScales[i] = (ScaleLutSize - 1) / range;
                _cachedOffsets[i] = -recipe.ValueMin * _cachedScales[i];

                int baseR = (recipe.ColorArgb >> 16) & 0xFF;
                int baseG = (recipe.ColorArgb >> 8) & 0xFF;
                int baseB = recipe.ColorArgb & 0xFF;

                int[] map = new int[ScaleLutSize];
                for (int v = 0; v < ScaleLutSize; v++)
                {
                    double t = v / (double)(ScaleLutSize - 1);
                    map[v] = PackLayerColor(t, recipe.Gain, recipe.Gamma, baseR, baseG, baseB);
                }
                _cachedScaleColorMaps[i] = map;
            }
        }

        /// <summary>
        /// Computes a packed RGB integer (no alpha) for a single LUT entry.
        /// <paramref name="t"/> is the normalized intensity [0.0, 1.0] before gain/gamma.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int PackLayerColor(double t, double gain, double gamma, int baseR, int baseG, int baseB)
        {
            double tGamma = (gamma == 1.0) ? t : Math.Pow(t, gamma);
            int intensity = Math.Clamp((int)(tGamma * gain * 255.0), 0, 255);
            return (((baseR * intensity) / 255) << 16)
                 | (((baseG * intensity) / 255) << 8)
                 |  ((baseB * intensity) / 255);
        }

        // =================================================================================
        // Pixel Blend / Pack Helpers
        // =================================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BlendPixel(BlendMode mode, int mappedColor, ref int r, ref int g, ref int b)
        {
            int cr = (mappedColor >> 16) & 0xFF;
            int cg = (mappedColor >> 8) & 0xFF;
            int cb = mappedColor & 0xFF;
            if (mode == BlendMode.Additive)
            {
                r += cr; g += cg; b += cb;
            }
            else
            {
                if (cr > r) r = cr;
                if (cg > g) g = cg;
                if (cb > b) b = cb;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ClampAndPackPixel(BlendMode mode, int r, int g, int b)
        {
            if (mode == BlendMode.Additive)
            {
                r = Math.Min(r, 255);
                g = Math.Min(g, 255);
                b = Math.Min(b, 255);
            }
            return (255 << 24) | (r << 16) | (g << 8) | b;
        }

        // =================================================================================
        // Active Layer Extraction Helper
        // =================================================================================

        private static int[] GetActiveIndices(int layerCount, int[][] colorMaps)
        {
            var list = new List<int>(layerCount);
            for (int i = 0; i < layerCount; i++)
                if (colorMaps[i] != null) list.Add(i);
            return list.ToArray();
        }

        // =================================================================================
        // Type-Specific Rendering Loops
        // =================================================================================

        private unsafe void RenderByteComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = source as MatrixData<byte> ?? throw new InvalidCastException();
            var maps = _cachedDirectColorMaps!;
            int[] activeIndices = GetActiveIndices(frameIndices.Length, maps);
            int activeCount = activeIndices.Length;
            if (activeCount == 0) return;

            var memories = new ReadOnlyMemory<byte>[frameIndices.Length];
            foreach (int a in activeIndices)
                memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int a = 0; a < activeCount; a++)
                    {
                        int idx = activeIndices[a];
                        BlendPixel(blendMode, maps[idx][memories[idx].Span[rowOffset + ix]], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderUShortComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = source as MatrixData<ushort> ?? throw new InvalidCastException();
            var maps = _cachedDirectColorMaps!;
            int[] activeIndices = GetActiveIndices(frameIndices.Length, maps);
            int activeCount = activeIndices.Length;
            if (activeCount == 0) return;

            var memories = new ReadOnlyMemory<ushort>[frameIndices.Length];
            foreach (int a in activeIndices)
                memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int a = 0; a < activeCount; a++)
                    {
                        int idx = activeIndices[a];
                        BlendPixel(blendMode, maps[idx][memories[idx].Span[rowOffset + ix]], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderShortComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = source as MatrixData<short> ?? throw new InvalidCastException();
            var maps = _cachedDirectColorMaps!;
            int[] activeIndices = GetActiveIndices(frameIndices.Length, maps);
            int activeCount = activeIndices.Length;
            if (activeCount == 0) return;

            var memories = new ReadOnlyMemory<short>[frameIndices.Length];
            foreach (int a in activeIndices)
                memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int a = 0; a < activeCount; a++)
                    {
                        int idx = activeIndices[a];
                        // short index is offset by +32768 to map [-32768, 32767] → [0, 65535]
                        BlendPixel(blendMode, maps[idx][memories[idx].Span[rowOffset + ix] + 32768], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderIntComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = source as MatrixData<int> ?? throw new InvalidCastException();
            var maps = _cachedScaleColorMaps!;
            int[] activeIndices = GetActiveIndices(frameIndices.Length, maps);
            int activeCount = activeIndices.Length;
            if (activeCount == 0) return;

            var memories = new ReadOnlyMemory<int>[frameIndices.Length];
            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;
            foreach (int a in activeIndices)
                memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int a = 0; a < activeCount; a++)
                    {
                        int idx = activeIndices[a];
                        int lutIndex = Math.Clamp((int)(memories[idx].Span[rowOffset + ix] * scales[idx] + offsets[idx]), 0, lutMax);
                        BlendPixel(blendMode, maps[idx][lutIndex], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderFloatComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = source as MatrixData<float> ?? throw new InvalidCastException();
            var maps = _cachedScaleColorMaps!;
            int[] activeIndices = GetActiveIndices(frameIndices.Length, maps);
            int activeCount = activeIndices.Length;
            if (activeCount == 0) return;

            var memories = new ReadOnlyMemory<float>[frameIndices.Length];
            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;
            foreach (int a in activeIndices)
                memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int a = 0; a < activeCount; a++)
                    {
                        int idx = activeIndices[a];
                        float val = memories[idx].Span[rowOffset + ix];
                        if (float.IsNaN(val)) continue;
                        int lutIndex = Math.Clamp((int)(val * scales[idx] + offsets[idx]), 0, lutMax);
                        BlendPixel(blendMode, maps[idx][lutIndex], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderDoubleComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = source as MatrixData<double> ?? throw new InvalidCastException();
            var maps = _cachedScaleColorMaps!;
            int[] activeIndices = GetActiveIndices(frameIndices.Length, maps);
            int activeCount = activeIndices.Length;
            if (activeCount == 0) return;

            var memories = new ReadOnlyMemory<double>[frameIndices.Length];
            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            int lutMax = ScaleLutSize - 1;
            foreach (int a in activeIndices)
                memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int a = 0; a < activeCount; a++)
                    {
                        int idx = activeIndices[a];
                        double val = memories[idx].Span[rowOffset + ix];
                        if (double.IsNaN(val)) continue;
                        int lutIndex = Math.Clamp((int)(val * scales[idx] + offsets[idx]), 0, lutMax);
                        BlendPixel(blendMode, maps[idx][lutIndex], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }

        private unsafe void RenderComplexComposite(
            IMatrixData source, int[] frameIndices, int* targetPtr, int strideInts,
            int width, int height, BlendMode blendMode)
        {
            var typedData = source as MatrixData<Complex> ?? throw new InvalidCastException();
            var maps = _cachedScaleColorMaps!;
            int[] activeIndices = GetActiveIndices(frameIndices.Length, maps);
            int activeCount = activeIndices.Length;
            if (activeCount == 0) return;

            var memories = new ReadOnlyMemory<Complex>[frameIndices.Length];
            var scales = _cachedScales!;
            var offsets = _cachedOffsets!;
            var converters = _cachedConverters!;
            int lutMax = ScaleLutSize - 1;
            foreach (int a in activeIndices)
                memories[a] = typedData.AsMemory(frameIndices[a]);

            var pOpts = ParallelOptions ?? new ParallelOptions();
            Parallel.For(0, height, pOpts, iy =>
            {
                int* pRow = targetPtr + iy * strideInts;
                int rowOffset = iy * width;
                for (int ix = 0; ix < width; ix++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int a = 0; a < activeCount; a++)
                    {
                        int idx = activeIndices[a];
                        double val = converters[idx](memories[idx].Span[rowOffset + ix]);
                        if (double.IsNaN(val)) continue;
                        int lutIndex = Math.Clamp((int)(val * scales[idx] + offsets[idx]), 0, lutMax);
                        BlendPixel(blendMode, maps[idx][lutIndex], ref r, ref g, ref b);
                    }
                    pRow[ix] = ClampAndPackPixel(blendMode, r, g, b);
                }
            });
        }
    }

}
