using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MxPlot.Core;
using MxPlot.Core.Imaging;
using System;
using System.Numerics;
using System.Threading.Tasks;

namespace MxPlot.UI.Avalonia.Rendering
{
    /// <summary>
    /// Provides high-performance rendering of <see cref="IMatrixData"/> to Avalonia
    /// <see cref="WriteableBitmap"/> using a lookup table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The internal rendering logic is identical to the WinForms <c>BitmapWriter</c>.
    /// The only platform-specific difference is how the pixel buffer is acquired:
    /// Avalonia uses <see cref="WriteableBitmap.Lock"/> returning an
    /// <see cref="ILockedFramebuffer"/>, whereas WinForms uses <c>Bitmap.LockBits</c>.
    /// </para>
    /// <para>
    /// Color format compatibility: <see cref="LookupTable"/> stores colors as
    /// ARGB integers (<c>0xAARRGGBB</c>), which in little-endian memory layout is
    /// [B][G][R][A] — identical to Avalonia's <see cref="PixelFormat.Bgra8888"/>.
    /// No conversion is required.
    /// </para>
    /// </remarks>
    public class BitmapWriter
    {
        #region Fields

        private unsafe delegate void RenderMethodDelegate(
            IMatrixData source,
            int frameIndex,
            int* targetPtr,
            int strideInts,
            int width,
            int height,
            ReadOnlyMemory<int> lutMemory,
            double valueScale,
            double valueOffset,
            int lutMaxIndex);

        private LookupTable _lookupTable;
        private double _valueMin = double.NaN;
        private double _valueMax = double.NaN;
        private bool _isInvertedColor;
        private ParallelOptions? _parallelOptions;
        private int[]? _cachedColorMap = null;

        #endregion

        #region Properties

        /// <summary>Gets or sets the lookup table used for color mapping.</summary>
        public LookupTable LookupTable
        {
            get => _lookupTable;
            set
            {
                if (_lookupTable != value)
                {
                    _lookupTable = value ?? throw new ArgumentNullException(nameof(value));
                    UpdateCachedColorMap();
                }
            }
        }

        /// <summary>Gets the data type of the matrix values this writer is configured to render.</summary>
        public Type ValueType { get; private set; }

        /// <summary>Gets or sets the minimum value for color mapping.</summary>
        public double ValueMin
        {
            get => _valueMin;
            set { if (_valueMin != value) { _valueMin = value; UpdateCachedColorMap(); } }
        }

        /// <summary>Gets or sets the maximum value for color mapping.</summary>
        public double ValueMax
        {
            get => _valueMax;
            set { if (_valueMax != value) { _valueMax = value; UpdateCachedColorMap(); } }
        }

        /// <summary>Gets or sets whether to invert the color mapping (swap min/max).</summary>
        public bool IsInvertedColor
        {
            get => _isInvertedColor;
            set { if (_isInvertedColor != value) { _isInvertedColor = value; UpdateCachedColorMap(); } }
        }

        /// <summary>
        /// When true, the bitmap is rendered with Y-axis flipped so that data row 0 (YMin)
        /// maps to the bitmap bottom and data row height-1 (YMax) maps to the bitmap top.
        /// Defaults to true (scientific convention: Y increases upward).
        /// Set to false for orthogonal side views whose ViewTransform already handles orientation.
        /// </summary>
        public bool FlipY { get; set; } = true;

        /// <summary>
        /// Gets or sets a converter function for Complex data (e.g. <c>c => c.Magnitude</c>).
        /// Must be a <see cref="Func{Complex, Double}"/>.
        /// </summary>
        public object? StructValueConverter { get; set; }

        /// <summary>Gets or sets parallel processing options. Null for sequential (default).</summary>
        public ParallelOptions? ParallelOptions
        {
            get => _parallelOptions;
            set => _parallelOptions = value;
        }

        private RenderMethodDelegate RenderInternal { get; }
        private Action UpdateCachedColorMap { get; }

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new <see cref="BitmapWriter"/>.
        /// </summary>
        /// <param name="lookupTable">The lookup table for color mapping.</param>
        /// <param name="type">The data type of matrix values (e.g. <c>typeof(ushort)</c>).</param>
        public BitmapWriter(LookupTable lookupTable, Type type)
        {
            _lookupTable = lookupTable ?? throw new ArgumentNullException(nameof(lookupTable));
            ValueType = type;

            UpdateCachedColorMap = type switch
            {
                Type t when t == typeof(byte)   => () => UpdateCachedColorMapProc(),
                Type t when t == typeof(ushort) => () => UpdateCachedColorMapProc(),
                Type t when t == typeof(short)  => () => UpdateCachedColorMapProc(),
                _                               => () => { }
            };

            unsafe
            {
                RenderInternal = type switch
                {
                    Type t when t == typeof(byte)    => RenderByte,
                    Type t when t == typeof(ushort)  => RenderUShort,
                    Type t when t == typeof(short)   => RenderShort,
                    Type t when t == typeof(int)     => RenderInt,
                    Type t when t == typeof(float)   => RenderFloat,
                    Type t when t == typeof(double)  => RenderDouble,
                    Type t when t == typeof(Complex) => RenderComplex,
                    _                                => RenderFallback
                };
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Creates a new <see cref="WriteableBitmap"/> from the specified matrix data.
        /// </summary>
        /// <param name="source">The source matrix data.</param>
        /// <param name="frameIndex">Frame index to render.</param>
        /// <param name="lut">Lookup table for color mapping.</param>
        /// <param name="valueMin">
        /// Minimum value for color mapping.
        /// Pass <see cref="double.NaN"/> (default) to auto-compute from the frame data.
        /// </param>
        /// <param name="valueMax">
        /// Maximum value for color mapping.
        /// Pass <see cref="double.NaN"/> (default) to auto-compute from the frame data.
        /// </param>
        /// <param name="dpi">Screen DPI. Defaults to 96 × 96.</param>
        public static WriteableBitmap CreateBitmap(
            IMatrixData source,
            int frameIndex,
            LookupTable lut,
            double valueMin = double.NaN,
            double valueMax = double.NaN,
            global::Avalonia.Vector dpi = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            if (double.IsNaN(valueMin) || double.IsNaN(valueMax))
            {
                var (min, max) = source.GetValueRange(frameIndex);
                valueMin = min;
                valueMax = max;
            }

            var effectiveDpi = dpi == default ? new global::Avalonia.Vector(96, 96) : dpi;
            var bmp = new WriteableBitmap(
                new PixelSize(source.XCount, source.YCount),
                effectiveDpi,
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            var writer = new BitmapWriter(lut, source.ValueType);
            writer.SetValueRange(valueMin, valueMax);
            writer.Render(source, frameIndex, bmp);
            return bmp;
        }

        /// <summary>Sets both min and max in a single call, triggering one color-map rebuild.</summary>
        public void SetValueRange(double min, double max)
        {
            _valueMin = min;
            _valueMax = max;
            UpdateCachedColorMap();
        }

        /// <summary>
        /// Sets multiple rendering properties in one call.
        /// Only triggers color-map recalculation when something actually changed.
        /// </summary>
        public void SetProperties(LookupTable lut, double valueMin, double valueMax, bool isInvertedColor)
        {
            bool needsUpdate = false;
            if (_lookupTable != lut)          { _lookupTable = lut ?? throw new ArgumentNullException(nameof(lut)); needsUpdate = true; }
            if (_valueMax != valueMax)         { _valueMax = valueMax;               needsUpdate = true; }
            if (_valueMin != valueMin)         { _valueMin = valueMin;               needsUpdate = true; }
            if (_isInvertedColor != isInvertedColor) { _isInvertedColor = isInvertedColor; needsUpdate = true; }
            if (needsUpdate) UpdateCachedColorMap();
        }

        /// <summary>
        /// Renders the specified frame of <paramref name="source"/> into <paramref name="target"/>.
        /// </summary>
        /// <param name="source">Source matrix data.</param>
        /// <param name="frameIndex">Frame index to render.</param>
        /// <param name="target">
        /// Target <see cref="WriteableBitmap"/>. Must use <see cref="PixelFormat.Bgra8888"/>
        /// and match source dimensions.
        /// </param>
        public void Render(IMatrixData source, int frameIndex, WriteableBitmap target)
        {
            CheckValidity(source, frameIndex, target);
            CallRenderProc(source, frameIndex, target);
        }

        #endregion

        #region Private Methods

        private void CheckValidity(IMatrixData source, int frameIndex, WriteableBitmap target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));

            if (target.PixelSize.Width != source.XCount || target.PixelSize.Height != source.YCount)
                throw new ArgumentException(
                    $"Target bitmap size ({target.PixelSize.Width}×{target.PixelSize.Height}) " +
                    $"does not match source data size ({source.XCount}×{source.YCount}).");

            if (frameIndex < 0 || frameIndex >= source.FrameCount)
                throw new ArgumentOutOfRangeException(nameof(frameIndex),
                    $"Frame index must be in [0, {source.FrameCount - 1}]. Got: {frameIndex}");

            if (double.IsInfinity(_valueMin) || double.IsInfinity(_valueMax))
                throw new InvalidOperationException(
                    $"Invalid value range: Min={_valueMin}, Max={_valueMax}");
        }

        private void CallRenderProc(IMatrixData source, int frameIndex, WriteableBitmap target)
        {
            // Lock() acquires exclusive write access to the pixel buffer for the using scope.
            using var fb = target.Lock();

            unsafe
            {
                double viewValueMin = _isInvertedColor ? _valueMax : _valueMin;
                double viewValueMax = _isInvertedColor ? _valueMin : _valueMax;
                double range = viewValueMax - viewValueMin;
                if (range == 0) range = 1.0;

                double valueScale  = (_lookupTable.Levels - 1) / range;
                double valueOffset = -viewValueMin * valueScale;
                int lutMaxIndex    = _lookupTable.Levels - 1;

                int width       = source.XCount;
                int height      = source.YCount;
                int posStride   = fb.RowBytes / 4;
                int* targetPtr;
                int strideInts;
                if (FlipY)
                {
                    // Data row 0 (YMin) → bitmap bottom; row height-1 (YMax) → bitmap top.
                    targetPtr  = (int*)fb.Address + (height - 1) * posStride;
                    strideInts = -posStride;
                }
                else
                {
                    targetPtr  = (int*)fb.Address;
                    strideInts = posStride;
                }

                var lutMemory = _lookupTable.AsReadOnlyMemory();
                RenderInternal(source, frameIndex, targetPtr, strideInts, width, height,
                               lutMemory, valueScale, valueOffset, lutMaxIndex);
            }
        }

        private void UpdateCachedColorMapProc()
        {
            int size = 0, offset = 0;
            if      (ValueType == typeof(byte))   { size = 256; }
            else if (ValueType == typeof(ushort))  { size = 65536; }
            else if (ValueType == typeof(short))   { size = 65536; offset = 32768; }
            if (size == 0) return;

            _cachedColorMap = new int[size];
            double viewValueMin = _isInvertedColor ? _valueMax : _valueMin;
            double viewValueMax = _isInvertedColor ? _valueMin : _valueMax;
            double range = viewValueMax - viewValueMin;
            if (range == 0) range = 1.0;
            double valueScale  = (_lookupTable.Levels - 1) / range;
            double valueOffset = -viewValueMin * valueScale;
            int lutMaxIndex    = _lookupTable.Levels - 1;
            var lut            = _lookupTable.AsSpan();

            for (int v = 0; v < size; v++)
            {
                int index = (int)((v - offset) * valueScale + valueOffset);
                _cachedColorMap[v] = lut[Math.Clamp(index, 0, lutMaxIndex)];
            }
        }

        #endregion

        #region Type-Specific Rendering Methods
        // ── All loops are identical to the WinForms BitmapWriter. ──────────────
        // Platform difference: pixel pointer comes from ILockedFramebuffer.Address
        // rather than BitmapData.Scan0, but the arithmetic is the same.

        private unsafe void RenderByte(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = source as MatrixData<byte> ?? throw new InvalidCastException("Expected MatrixData<byte>");
            if (_cachedColorMap == null) throw new NullReferenceException("Call SetValueRange before rendering.");
            var mem = typedData.AsMemory(frameIndex);

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    ReadOnlySpan<byte> row = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix) pRow[ix] = _cachedColorMap[row[ix]];
                });
            }
            else
            {
                for (int iy = 0; iy < height; iy++)
                {
                    ReadOnlySpan<byte> row = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ix++) pRow[ix] = _cachedColorMap[row[ix]];
                }
            }
        }

        private unsafe void RenderUShort(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            var data = source as MatrixData<ushort> ?? throw new InvalidCastException("Expected MatrixData<ushort>");
            if (_cachedColorMap == null) throw new NullReferenceException("Call SetValueRange before rendering.");
            var mem = data.AsMemory(frameIndex);

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    ReadOnlySpan<ushort> row = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix) pRow[ix] = _cachedColorMap[row[ix]];
                });
            }
            else
            {
                for (int iy = 0; iy < height; iy++)
                {
                    int* pRow = targetPtr + iy * strideInts;
                    ReadOnlySpan<ushort> row = mem.Span.Slice(iy * width, width);
                    for (int ix = 0; ix < width; ix++) pRow[ix] = _cachedColorMap[row[ix]];
                }
            }
        }

        private unsafe void RenderShort(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = source as MatrixData<short> ?? throw new InvalidCastException("Expected MatrixData<short>");
            if (_cachedColorMap == null) throw new NullReferenceException("Call SetValueRange before rendering.");
            var mem = typedData.AsMemory(frameIndex);

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix) pRow[ix] = _cachedColorMap[values[ix] + 32768];
                });
            }
            else
            {
                for (int iy = 0; iy < height; iy++)
                {
                    var values = mem.Span.Slice(iy * width, width);
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix) pRow[ix] = _cachedColorMap[values[ix] + 32768];
                }
            }
        }

        private unsafe void RenderInt(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = source as MatrixData<int> ?? throw new InvalidCastException("Expected MatrixData<int>");
            var mem = typedData.AsMemory(frameIndex);

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                        pRow[ix] = lut[Math.Clamp((int)(values[ix] * valueScale + valueOffset), 0, lutMaxIndex)];
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ix++)
                        pRow[ix] = lut[Math.Clamp((int)(values[rowOffset + ix] * valueScale + valueOffset), 0, lutMaxIndex)];
                }
            }
        }

        private unsafe void RenderFloat(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = source as MatrixData<float> ?? throw new InvalidCastException("Expected MatrixData<float>");
            int missingColor = _lookupTable.MissingColor;
            var mem = typedData.AsMemory(frameIndex);

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[rowOffset + ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        private unsafe void RenderDouble(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            var typedData = source as MatrixData<double> ?? throw new InvalidCastException("Expected MatrixData<double>");
            int missingColor = _lookupTable.MissingColor;
            var mem = typedData.AsMemory(frameIndex);

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = values[rowOffset + ix];
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        private unsafe void RenderComplex(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            var data = source as MatrixData<Complex> ?? throw new InvalidCastException("Expected MatrixData<Complex>");
            if (StructValueConverter is not Func<Complex, double> converter)
                throw new InvalidOperationException(
                    "StructValueConverter must be set to Func<Complex, double> for Complex rendering. " +
                    "Example: writer.StructValueConverter = (Func<Complex, double>)(c => c.Magnitude);");
            int missingColor = _lookupTable.MissingColor;
            var mem = data.AsMemory(frameIndex);

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    var values = mem.Span.Slice(iy * width, width);
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = converter(values[ix]);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var values = mem.Span;
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int rowOffset = iy * width;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = converter(values[rowOffset + ix]);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        private unsafe void RenderFallback(
            IMatrixData source, int frameIndex, int* targetPtr, int strideInts, int width, int height,
            ReadOnlyMemory<int> lutMemory, double valueScale, double valueOffset, int lutMaxIndex)
        {
            int missingColor = _lookupTable.MissingColor;

            if (_parallelOptions != null)
            {
                Parallel.For(0, height, _parallelOptions, iy =>
                {
                    var lut = lutMemory.Span;
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = source.GetValueAt(ix, iy, frameIndex);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                });
            }
            else
            {
                var lut = lutMemory.Span;
                for (int iy = 0; iy < height; iy++)
                {
                    int* pRow = targetPtr + iy * strideInts;
                    for (int ix = 0; ix < width; ++ix)
                    {
                        double v = source.GetValueAt(ix, iy, frameIndex);
                        pRow[ix] = double.IsNaN(v) ? missingColor
                            : lut[Math.Clamp((int)(v * valueScale + valueOffset), 0, lutMaxIndex)];
                    }
                }
            }
        }

        #endregion
    }
}
