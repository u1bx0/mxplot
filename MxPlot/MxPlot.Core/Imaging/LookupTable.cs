using System;

namespace MxPlot.Core.Imaging
{
    /// <summary>
    /// Represents a color lookup table (LUT) used for mapping normalized values
    /// to RGB colors. The LUT stores colors internally in ARGB format (0xAARRGGBB).
    /// 
    /// Input colors are provided in RGB integer format:
    ///     R | (G << 8) | (B << 16)
    /// 
    /// The constructor automatically converts them to ARGB format with full opacity:
    ///     0xFF000000 | (R << 16) | (G << 8) | B
    /// 
    /// This ensures compatibility with WinForms (Bitmap) and WPF (WriteableBitmap),
    /// which both expect ARGB format.
    /// </summary>
    public sealed class LookupTable
    {
        private readonly int[] _colorsARGB;

        /// <summary>
        /// Gets the display name of this lookup table.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the number of color levels (gradation steps) in this LUT.
        /// </summary>
        public int Levels => _colorsARGB.Length;

        /// <summary>
        /// Gets the color used when a value is missing or invalid.
        /// Stored in ARGB format.
        /// </summary>
        public int MissingColor { get; }

        /// <summary>
        /// Initializes a new lookup table.
        /// Input colors in RGB format (R|(G<<8)|(B<<16)) are converted to ARGB format.
        /// </summary>
        /// <param name="name">Display name of the lookup table</param>
        /// <param name="colors">Color array in RGB format</param>
        /// <param name="missingColor">Missing value color in RGB format</param>
        public LookupTable(string name, int[] colors, int missingColor)
        {
            Name = name;

            // Convert RGB to ARGB with full opacity
            _colorsARGB = new int[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                int rgb = colors[i];
                byte r = (byte)rgb;
                byte g = (byte)(rgb >> 8);
                byte b = (byte)(rgb >> 16);
                _colorsARGB[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
            }

            // Convert missing color to ARGB
            byte mr = (byte)missingColor;
            byte mg = (byte)(missingColor >> 8);
            byte mb = (byte)(missingColor >> 16);
            MissingColor = unchecked((int)0xFF000000) | (mr << 16) | (mg << 8) | mb;
        }

        /// <summary>
        /// Returns the internal color array as a Span for fast access.
        /// Colors are in ARGB format.
        /// </summary>
        public Span<int> AsSpan() => _colorsARGB.AsSpan();

        /// <summary>
        /// Gets a read-only memory region that represents the underlying color data.
        /// Colors are in ARGB format.
        /// </summary>
        public ReadOnlyMemory<int> AsReadOnlyMemory() => _colorsARGB.AsMemory();
    }

    /// <summary>
    /// Provides extension methods for lookup table manipulation.
    /// </summary>
    public static class LookupTableExtensions
    {
        /// <summary>
        /// Creates a new lookup table with a different number of levels.
        /// Colors are linearly interpolated in RGB space.
        /// 
        /// Note:
        /// - <see cref="LookupTable.AsSpan"/> returns colors in ARGB format (0xFFRRGGBB).
        ///   This method decodes ARGB correctly (R = bits 16-23, G = bits 8-15, B = bits 0-7)
        ///   before interpolating, and re-encodes to the RGB format expected by the
        ///   <see cref="LookupTable"/> constructor: R | (G &lt;&lt; 8) | (B &lt;&lt; 16).
        /// </summary>
        public static LookupTable Resample(this LookupTable lut, int levels)
        {
            if (lut.Levels == levels)
                return lut;

            var src = lut.AsSpan(); // ARGB format: 0xFFRRGGBB
            int[] colors = new int[levels];

            for (int i = 0; i < levels; i++)
            {
                double ai = i / (double)(levels - 1) * (lut.Levels - 1);
                int i0 = (int)ai;
                int i1 = Math.Min(i0 + 1, lut.Levels - 1);

                // Decode ARGB (0xFFRRGGBB): R at bits 16-23, G at 8-15, B at 0-7
                var c0 = src[i0];
                byte r0 = (byte)(c0 >> 16);
                byte g0 = (byte)(c0 >> 8);
                byte b0 = (byte)c0;

                if (i0 == i1)
                {
                    colors[i] = r0 | (g0 << 8) | (b0 << 16);
                    continue;
                }

                var c1 = src[i1];
                byte r1 = (byte)(c1 >> 16);
                byte g1 = (byte)(c1 >> 8);
                byte b1 = (byte)c1;

                double t = ai - i0;

                byte r = (byte)(r0 + (r1 - r0) * t);
                byte g = (byte)(g0 + (g1 - g0) * t);
                byte b = (byte)(b0 + (b1 - b0) * t);

                // Encode as RGB for the LookupTable constructor: R | (G << 8) | (B << 16)
                colors[i] = r | (g << 8) | (b << 16);
            }

            // lut.MissingColor is ARGB; convert back to RGB for the constructor
            int mc = lut.MissingColor;
            int missingRgb = ((mc >> 16) & 0xFF) | (mc & 0xFF00) | ((mc & 0xFF) << 16);
            return new LookupTable(lut.Name, colors, missingRgb);
        }
    }
}