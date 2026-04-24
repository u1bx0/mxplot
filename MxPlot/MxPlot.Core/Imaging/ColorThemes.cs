using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MxPlot.Core.Imaging
{
    /// <summary>
    /// Provides a collection of predefined and user-registered color themes for use in data visualization.
    /// </summary>
    /// <remarks>
    /// <para>This class offers access to a variety of built-in color lookup tables and supports loading and registering custom themes.</para>
    /// 
    /// <para><strong>Built-in Colormaps and Licenses:</strong></para>
    /// <list type="table">
    ///   <listheader>
    ///     <term>Colormap</term>
    ///     <description>License / Source</description>
    ///   </listheader>
    ///   <item>
    ///     <term>Grayscale, Hot, OrangeHot, GreenHot, Cold, BSMod, Spectrum, HiLo</term>
    ///     <description>Original implementation (Public Domain)</description>
    ///   </item>
    ///   <item>
    ///     <term>Jet</term>
    ///     <description>MATLAB standard formula (widely used, no restrictions)</description>
    ///   </item>
    ///   <item>
    ///     <term>Turbo</term>
    ///     <description>Google AI (Apache 2.0 License)
    ///     <br/>Reference: https://ai.googleblog.com/2019/08/turbo-improved-rainbow-colormap-for.html</description>
    ///   </item>
    ///   <item>
    ///     <term>Viridis</term>
    ///     <description>Matplotlib (CC0 - Public Domain)
    ///     <br/>Designed by: Eric Firing, Nathaniel Smith, and Stefan van der Walt
    ///     <br/>Reference: https://matplotlib.org/stable/users/explain/colors/colormaps.html</description>
    ///   </item>
    ///   <item>
    ///     <term>CoolWarm</term>
    ///     <description>Kenneth Moreland (Public Domain)
    ///     <br/>Reference: https://www.kennethmoreland.com/color-advice/</description>
    ///   </item>
    ///   <item>
    ///     <term>Phase</term>
    ///     <description>Simple diverging colormap (Original)</description>
    ///   </item>
    /// </list>
    /// 
    /// <para><strong>Usage:</strong></para>
    /// <code>
    /// // Get by property
    /// var turbo = ColorThemes.Turbo;
    /// 
    /// // Get by name
    /// var lut = ColorThemes.Get("Viridis");
    /// 
    /// // Load from file
    /// var custom = ColorThemes.LoadFromFile("custom.mlut");
    /// 
    /// // Register custom LUT
    /// ColorThemes.Register(custom);
    /// </code>
    /// </remarks>
    public static class ColorThemes
    {
        private const int DefaultLevels = 256;

        private static readonly Dictionary<string, LookupTable> _lutSet =
            new(StringComparer.OrdinalIgnoreCase);

        // -------------------------
        // Built-in theme properties
        // -------------------------

        public static LookupTable Grayscale { get; }
        public static LookupTable Hot { get; }
        public static LookupTable OrangeHot { get; }
        public static LookupTable GreenHot { get; }
        public static LookupTable BSMod { get; }
        public static LookupTable Spectrum { get; }
        public static LookupTable Cold { get; }
        public static LookupTable Jet { get; }
        public static LookupTable Turbo { get; }
        public static LookupTable CoolWarm { get; }
        public static LookupTable Phase { get; }
        public static LookupTable Viridis { get; }
        public static LookupTable HiLo { get; }

        // -------------------------
        // Static constructor
        // -------------------------

        static ColorThemes()
        {
            int red = 0xFF0000; // Red for missing values
            int green = 0x00FF00; // Green for missing values
            int blue = 0x0000FF; // Blue for missing values
            int magenta = 0xFF00FF; // Magenta for missing values
            //int black = 0x000000; // Black for missing values
            int brawn = 0x8B4513; // Brown for missing values

            // Create built-in LUTs
            Grayscale = new LookupTable("Grayscale", CreateGrayscale(DefaultLevels), red);
            Hot = new LookupTable("Hot", CreateHot(DefaultLevels), blue);
            OrangeHot = new LookupTable("OrangeHot", CreateOrangeHot(DefaultLevels), blue);
            GreenHot = new LookupTable("GreenHot", CreateGreenHot(DefaultLevels), red);
            BSMod = new LookupTable("BSMod", CreateBSMod(DefaultLevels), red);
            Spectrum = new LookupTable("Spectrum", CreateSpectrum(DefaultLevels), brawn);
            Cold = new LookupTable("Cold", CreateCold(DefaultLevels), red);
            Jet = new LookupTable("Jet", CreateJet(DefaultLevels), magenta);
            Turbo = new LookupTable("Turbo", CreateTurbo(DefaultLevels), magenta);
            CoolWarm = new LookupTable("CoolWarm", CreateCoolWarm(DefaultLevels), green);
            Phase = new LookupTable("Phase", CreatePhase(DefaultLevels), green);
            Viridis = new LookupTable("Viridis", CreateViridis(DefaultLevels), magenta);
            var hilo = CreateGrayscale(DefaultLevels);
            hilo[0] = blue; hilo[hilo.Length - 1] = red;
            HiLo = new LookupTable("HiLo", hilo, green);

            // Register them in the dictionary
            Register(Grayscale);
            Register(Hot);
            Register(OrangeHot);
            Register(GreenHot);
            Register(Cold);
            Register(Spectrum);
            Register(BSMod);
            Register(Jet);
            Register(Turbo);
            Register(Viridis);
            Register(CoolWarm);
            Register(Phase);
            Register(HiLo);
        }

        // -------------------------
        // Registry API
        // -------------------------

        /// <summary>
        /// Registers the specified lookup table in the internal registry, using its name as the key.
        /// </summary>
        /// <remarks>If a lookup table with the same name already exists in the registry, it will be
        /// overwritten by the new instance. Use this method to enable dynamic addition and retrieval of lookup tables
        /// by name.</remarks>
        /// <param name="lut">The lookup table to register. This parameter must not be null, and its Name property must be unique within
        /// the registry.</param>
        public static void Register(LookupTable lut)
        {
            _lutSet[lut.Name] = lut;
        }

        public static LookupTable Get(string name)
        {
            return _lutSet[name];
        }

        public static IEnumerable<string> Names => _lutSet.Keys;

        // -------------------------
        // External LUT loader
        // -------------------------

        public static LookupTable? LoadFromFile(string path)
        {
            var lines = File.ReadAllLines(path)
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .ToArray();
            return CreateFrom(lines);   
        }

        /// <summary>
        /// Creates a new instance of the LookupTable class from an array of strings containing the table's name,
        /// header, and color definitions.
        /// </summary>
        /// <remarks>Empty lines and lines starting with '#' are ignored before processing. The first line is regarded as the name of LUT. The second 
        /// line must specify the number of color levels and may include an optional missing color value in hexadecimal
        /// format.
        /// <code>
        /// Name
        /// 256 [, optional missingColor] # color is int32
        /// 0, 0, 0
        /// ....
        /// </code>
        /// </remarks>
        /// <param name="lines">An array of strings representing the input lines. The first line specifies the name, the second line
        /// contains the header with level and optional missing color information, and each subsequent line defines a
        /// color value.</param>
        /// <returns>A LookupTable instance if the input is valid; otherwise, null if an error occurs during creation.</returns>
        /// <exception cref="ArgumentException">Thrown when the input does not contain at least three lines (name, header, and at least one color).</exception>"
        public static LookupTable? CreateFrom(string[] lines)
        {
            // Remove empty lines and comment lines
            lines = lines
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                .ToArray();

            if (lines.Length < 3)
                throw new ArgumentException("At least 3 lines are required: name, header, and at least one color.");

            try
            {
                // LUT name
                string name = lines[0].Trim();

                // Header: levels, optional missingColor
                var header = lines[1].Split(',', StringSplitOptions.TrimEntries);
                int levels = int.Parse(header[0]);
                int missingColor = header.Length > 1 ? Convert.ToInt32(header[1], 16) : 0;

                // Read color lines (may be fewer or more)
                int colorLines = lines.Length - 2;
                var colors = new int[levels];

                // Fill available lines
                int minCount = Math.Min(levels, colorLines);
                for (int i = 0; i < minCount; i++)
                    colors[i] = ParseColor(lines[i + 2]);

                // If fewer lines than levels → fill with last color
                if (colorLines < levels)
                {
                    int last = colors[minCount - 1];
                    for (int i = minCount; i < levels; i++)
                        colors[i] = last;
                }
                // If more lines than levels → ignore extra lines

                return new LookupTable(name, colors, missingColor);
            }
            catch (Exception e)
            {
                Debug.WriteLine($"Error creating LUT from lines: {e.Message}");
            }

            return null;
        }

        private static int ParseColor(string s)
        {
            s = s.Trim();

            if (s.Contains(','))
            {
                var parts = s.Split(',');
                int r = int.Parse(parts[0]);
                int g = int.Parse(parts[1]);
                int b = int.Parse(parts[2]);
                return r | (g << 8) | (b << 16);
            }
            else
            {
                int rgb = Convert.ToInt32(s, 16);
                int r = (rgb >> 16) & 0xFF;
                int g = (rgb >> 8) & 0xFF;
                int b = rgb & 0xFF;
                return r | (g << 8) | (b << 16);
            }
        }

        // -------------------------
        // Built-in LUT generators
        // -------------------------

        private static int[] CreateGrayscale(int levels)
        {
            var colors = new int[levels];
            for (int i = 0; i < levels; i++)
            {
                int v = (i * 255) / (levels - 1);
                colors[i] = v | (v << 8) | (v << 16);
            }
            return colors;
        }

        private static int[] CreateHot(int levels)
        {
            // Warm: Black → Red → Yellow → White (LSColorPalette.createWarmColorPalette)
            var colors = new int[levels];
            int div = levels / 3;
            const int cMax = 255;

            for (int i = 0; i < 3; i++)
            {
                for (int d = 0; d < div; d++)
                {
                    double t = d / (double)div;

                    int r = 0, g = 0, b = 0;

                    switch (i)
                    {
                        case 0:
                            // Black → Red
                            r = (int)(cMax * t);
                            g = 0;
                            b = 0;
                            break;

                        case 1:
                            // Red → Yellow
                            r = cMax;
                            g = (int)(cMax * t);
                            b = 0;
                            break;

                        case 2:
                            // Yellow → White
                            r = cMax;
                            g = cMax;
                            b = (int)(cMax * t);
                            break;
                    }

                    colors[div * i + d] = r | (g << 8) | (b << 16);
                }
            }

            // Ensure first is black and last is white
            colors[0] = 0x000000;
            colors[levels - 1] = 0xFFFFFF;

            return colors;
        }

        

        private static int[] CreateOrangeHot(int levels)
        {
            // LSColorPalette.createOrangeHotColorPalette
            var colors = new int[levels];
            int div = levels / 3;
            const int cMax = 255;

            for (int i = 0; i < 3; i++)
            {
                for (int d = 0; d < div; d++)
                {
                    double t = d / (double)div;

                    int r = 0, g = 0, b = 0;

                    switch (i)
                    {
                        case 0:
                            // Black → Dark Orange
                            r = (int)(cMax * t);
                            g = (int)(cMax * t) / 2;
                            b = 0;
                            break;

                        case 1:
                            // Dark Orange → Bright Orange
                            r = cMax;
                            g = (int)(cMax * t) / 2 + 127;
                            b = 0;
                            break;

                        case 2:
                            // Bright Orange → White
                            r = cMax;
                            g = cMax;
                            b = (int)(cMax * t);
                            break;
                    }

                    colors[div * i + d] = r | (g << 8) | (b << 16);
                }
            }

            colors[0] = 0x000000;
            colors[levels - 1] = 0xFFFFFF;

            return colors;
        }

        private static int[] CreateGreenHot(int levels)
        {
            // LSColorPalette.createGreenColorPalette
            var colors = new int[levels];
            int div = levels / 3;
            const int cMax = 255;

            for (int i = 0; i < 3; i++)
            {
                for (int d = 0; d < div; d++)
                {
                    double t = d / (double)div;

                    int r = 0, g = 0, b = 0;

                    switch (i)
                    {
                        case 0:
                            // Black → Green
                            r = 0;
                            g = (int)(cMax * t);
                            b = 0;
                            break;

                        case 1:
                            // Green → Yellow
                            r = (int)(cMax * t);
                            g = cMax;
                            b = 0;
                            break;

                        case 2:
                            // Yellow → White
                            r = cMax;
                            g = cMax;
                            b = (int)(cMax * t);
                            break;
                    }

                    colors[div * i + d] = r | (g << 8) | (b << 16);
                }
            }

            colors[0] = 0x000000;
            colors[levels - 1] = 0xFFFFFF;

            return colors;
        }

        private static int[] CreateBSMod(int levels)
        {
            // LSColorPalette.createBeamStarModColorPalette
            int[] colors = new int[levels];
            int div = levels / 7;
            int remain = levels - div * 7;
            const int cMax = 255;

            for (int i = 0; i < 7; i++)
            {
                for (int d = 0; d < div; d++)
                {
                    double t = d / (double)div;

                    int r = 0, g = 0, b = 0;

                    switch (i)
                    {
                        case 0:
                            // Black → Dark Blue (R=t*0.5, G=0, B=t)
                            r = (int)(cMax * t * 0.5);
                            g = 0;
                            b = (int)(cMax * t);
                            break;

                        case 1:
                            // Dark Blue → Blue (R=0.5-t*0.5, G=0, B=1)
                            r = (int)(cMax * (0.5 - t * 0.5));
                            g = 0;
                            b = cMax;
                            break;

                        case 2:
                            // Blue → Cyan (R=0, G=t, B=1)
                            r = 0;
                            g = (int)(cMax * t);
                            b = cMax;
                            break;

                        case 3:
                            // Cyan → Green (R=0, G=1, B=1-t)
                            r = 0;
                            g = cMax;
                            b = (int)(cMax * (1.0 - t));
                            break;

                        case 4:
                            // Green → Yellow (R=t, G=1, B=0)
                            r = (int)(cMax * t);
                            g = cMax;
                            b = 0;
                            break;

                        case 5:
                            // Yellow → Red (R=1, G=1-t, B=0)
                            r = cMax;
                            g = (int)(cMax * (1.0 - t));
                            b = 0;
                            break;

                        case 6:
                            // Red → White (R=1, G=t, B=t)
                            r = cMax;
                            g = (int)(cMax * t);
                            b = (int)(cMax * t);
                            break;
                    }

                    colors[div * i + d] = r | (g << 8) | (b << 16);
                }
            }

            // Start with black
            colors[0] = 0x000000;

            // Remaining slots filled with white
            for (int i = 0; i < remain; i++)
                colors[div * 7 + i] = 0xFFFFFF;

            return colors;
        }

        private static int[] CreateSpectrum(int levels)
        {
            // LSColorPalette.createSpectrumColorPalette
            int[] colors = new int[levels];
            int divNum = 4;
            int div = levels / divNum;
            int remain = levels - div * divNum;
            const int cMax = 255;

            for (int i = 0; i < divNum; i++)
            {
                for (int d = 0; d < div; d++)
                {
                    double t = d / (double)div;

                    int r = 0, g = 0, b = 0;

                    switch (i)
                    {
                        case 0:
                            // Blue → Cyan (R=0, G=t, B=1)
                            r = 0;
                            g = (int)(cMax * t);
                            b = cMax;
                            break;

                        case 1:
                            // Cyan → Green (R=0, G=1, B=1-t)
                            r = 0;
                            g = cMax;
                            b = (int)(cMax * (1.0 - t));
                            break;

                        case 2:
                            // Green → Yellow (R=t, G=1, B=0)
                            r = (int)(cMax * t);
                            g = cMax;
                            b = 0;
                            break;

                        case 3:
                            // Yellow → Red (R=1, G=1-t, B=0)
                            r = cMax;
                            g = (int)(cMax * (1.0 - t));
                            b = 0;
                            break;
                    }

                    colors[div * i + d] = r | (g << 8) | (b << 16);
                }
            }

            // First is blue
            colors[0] = 0 | (0 << 8) | (255 << 16);  // RGB: (0, 0, 255)

            // Remaining slots filled with red
            for (int i = 0; i < remain; i++)
                colors[div * divNum + i] = 0xFF | (0 << 8) | (0 << 16);  // RGB: (255, 0, 0)

            return colors;
        }

        private static int[] CreateCold(int levels)
        {
            // LSColorPalette.createColdColorPalette
            int[] colors = new int[levels];
            int div = levels / 3;
            const int cMax = 255;

            for (int i = 0; i < 3; i++)
            {
                for (int d = 0; d < div; d++)
                {
                    double t = d / (double)div;

                    int r = 0, g = 0, b = 0;

                    switch (i)
                    {
                        case 0:
                            // Black → Blue
                            r = 0;
                            g = 0;
                            b = (int)(cMax * t);
                            break;

                        case 1:
                            // Blue → Cyan
                            r = 0;
                            g = (int)(cMax * t);
                            b = cMax;
                            break;

                        case 2:
                            // Cyan → White
                            r = (int)(cMax * t);
                            g = cMax;
                            b = cMax;
                            break;
                    }

                    colors[div * i + d] = r | (g << 8) | (b << 16);
                }
            }

            // First is black
            colors[0] = 0x000000;

            // Last is white
            colors[levels - 1] = 0xFFFFFF;

            return colors;
        }

        private static int[] CreateJet(int levels)
        {
            // MATLAB Jet: Dark Blue → Cyan → Yellow → Orange → Dark Red
            int[] colors = new int[levels];

            for (int i = 0; i < levels; i++)
            {
                double t = i / (double)(levels - 1);

                // Jet colormap formula (MATLAB standard)
                double r = Math.Clamp(1.5 - Math.Abs(4 * t - 3), 0, 1);
                double g = Math.Clamp(1.5 - Math.Abs(4 * t - 2), 0, 1);
                double b = Math.Clamp(1.5 - Math.Abs(4 * t - 1), 0, 1);

                int R = (int)(r * 255);
                int G = (int)(g * 255);
                int B = (int)(b * 255);

                colors[i] = R | (G << 8) | (B << 16);
            }

            return colors;
        }

        

        // ------------------------------------------------------------
        // Turbo (Google AI, Apache 2.0) - Official 256-color LUT
        // https://ai.googleblog.com/2019/08/turbo-improved-rainbow-colormap-for.html
        // ------------------------------------------------------------
        private static int[] CreateTurbo(int levels)
        {
            // Official Google Turbo 256-color lookup table (R, G, B triplets)
            ReadOnlySpan<byte> turbo256 = 
            [
                48,18,59, 50,21,67, 51,24,74, 52,27,81, 53,30,88, 54,33,95, 55,36,102, 56,39,109,
                57,42,115, 58,45,121, 59,47,128, 60,50,134, 61,53,139, 62,56,145, 63,59,151, 63,62,156,
                64,64,162, 65,67,167, 65,70,172, 66,73,177, 66,75,181, 67,78,186, 68,81,191, 68,84,195,
                68,86,199, 69,89,203, 69,92,207, 69,94,211, 70,97,214, 70,100,218, 70,102,221, 70,105,224,
                70,107,227, 71,110,230, 71,113,233, 71,115,235, 71,118,238, 71,120,240, 71,123,242, 70,125,244,
                70,128,246, 70,130,248, 70,133,250, 70,135,251, 69,138,252, 69,140,253, 68,143,254, 67,145,254,
                66,148,255, 65,150,255, 64,153,255, 62,155,254, 61,158,254, 59,160,253, 58,163,252, 56,165,251,
                55,168,250, 53,171,248, 51,173,247, 49,175,245, 47,178,244, 46,180,242, 44,183,240, 42,185,238,
                40,188,235, 39,190,233, 37,192,231, 35,195,228, 34,197,226, 32,199,223, 31,201,221, 30,203,218,
                28,205,216, 27,208,213, 26,210,210, 26,212,208, 25,213,205, 24,215,202, 24,217,200, 24,219,197,
                24,221,194, 24,222,192, 24,224,189, 25,226,187, 25,227,185, 26,228,182, 28,230,180, 29,231,178,
                31,233,175, 32,234,172, 34,235,170, 37,236,167, 39,238,164, 42,239,161, 44,240,158, 47,241,155,
                50,242,152, 53,243,148, 56,244,145, 60,245,142, 63,246,138, 67,247,135, 70,248,132, 74,248,128,
                78,249,125, 82,250,122, 85,250,118, 89,251,115, 93,252,111, 97,252,108, 101,253,105, 105,253,102,
                109,254,98, 113,254,95, 117,254,92, 121,254,89, 125,255,86, 128,255,83, 132,255,81, 136,255,78,
                139,255,75, 143,255,73, 146,255,71, 150,254,68, 153,254,66, 156,254,64, 159,253,63, 161,253,61,
                164,252,60, 167,252,58, 169,251,57, 172,251,56, 175,250,55, 177,249,54, 180,248,54, 183,247,53,
                185,246,53, 188,245,52, 190,244,52, 193,243,52, 195,241,52, 198,240,52, 200,239,52, 203,237,52,
                205,236,52, 208,234,52, 210,233,53, 212,231,53, 215,229,53, 217,228,54, 219,226,54, 221,224,55,
                223,223,55, 225,221,55, 227,219,56, 229,217,56, 231,215,57, 233,213,57, 235,211,57, 236,209,58,
                238,207,58, 239,205,58, 241,203,58, 242,201,58, 244,199,58, 245,197,58, 246,195,58, 247,193,58,
                248,190,57, 249,188,57, 250,186,57, 251,184,56, 251,182,55, 252,179,54, 252,177,54, 253,174,53,
                253,172,52, 254,169,51, 254,167,50, 254,164,49, 254,161,48, 254,158,47, 254,155,45, 254,153,44,
                254,150,43, 254,147,42, 254,144,41, 253,141,39, 253,138,38, 252,135,37, 252,132,35, 251,129,34,
                251,126,33, 250,123,31, 249,120,30, 249,117,29, 248,114,28, 247,111,26, 246,108,25, 245,105,24,
                244,102,23, 243,99,21, 242,96,20, 241,93,19, 240,91,18, 239,88,17, 237,85,16, 236,83,15,
                235,80,14, 234,78,13, 232,75,12, 231,73,12, 229,71,11, 228,69,10, 226,67,10, 225,65,9,
                223,63,8, 221,61,8, 220,59,7, 218,57,7, 216,55,6, 214,53,6, 212,51,5, 210,49,5,
                208,47,5, 206,45,4, 204,43,4, 202,42,4, 200,40,3, 197,38,3, 195,37,3, 193,35,2,
                190,33,2, 188,32,2, 185,30,2, 183,29,2, 180,27,1, 178,26,1, 175,24,1, 172,23,1,
                169,22,1, 167,20,1, 164,19,1, 161,18,1, 158,16,1, 155,15,1, 152,14,1, 149,13,1,
                146,11,1, 142,10,1, 139,9,2, 136,8,2, 133,7,2, 129,6,2, 126,5,2, 122,4,3
            ];

            int[] colors = new int[levels];

            if (levels == 256)
            {
                // Direct mapping for 256 levels
                for (int i = 0; i < 256; i++)
                {
                    int r = turbo256[i * 3 + 0];
                    int g = turbo256[i * 3 + 1];
                    int b = turbo256[i * 3 + 2];
                    colors[i] = r | (g << 8) | (b << 16);
                }
            }
            else
            {
                // Resample to requested number of levels
                for (int i = 0; i < levels; i++)
                {
                    double t = i / (double)(levels - 1);
                    double pos = t * 255.0;
                    int idx0 = (int)Math.Floor(pos);
                    int idx1 = Math.Min(idx0 + 1, 255);
                    double frac = pos - idx0;

                    int r0 = turbo256[idx0 * 3 + 0];
                    int g0 = turbo256[idx0 * 3 + 1];
                    int b0 = turbo256[idx0 * 3 + 2];

                    int r1 = turbo256[idx1 * 3 + 0];
                    int g1 = turbo256[idx1 * 3 + 1];
                    int b1 = turbo256[idx1 * 3 + 2];

                    int r = (int)(r0 + (r1 - r0) * frac);
                    int g = (int)(g0 + (g1 - g0) * frac);
                    int b = (int)(b0 + (b1 - b0) * frac);

                    colors[i] = r | (g << 8) | (b << 16);
                }
            }

            return colors;
        }


        // ------------------------------------------------------------
        // CoolWarm (Moreland, Public Domain) — 端点線形補間版
        // Blue → White → Red の Moreland 典型値
        // ------------------------------------------------------------
        private static int[] CreateCoolWarm(int levels)
        {
            int[] colors = new int[levels];

            // Moreland の代表端点
            (int r, int g, int b) low = (59, 76, 192);
            (int r, int g, int b) mid = (221, 221, 221);
            (int r, int g, int b) high = (180, 4, 38);

            for (int i = 0; i < levels; i++)
            {
                double t = i / (double)(levels - 1);

                (int r, int g, int b) a, bch;
                double u;

                if (t < 0.5)
                {
                    a = low;
                    bch = mid;
                    u = t / 0.5;
                }
                else
                {
                    a = mid;
                    bch = high;
                    u = (t - 0.5) / 0.5;
                }

                int R = (int)(a.r + (bch.r - a.r) * u);
                int G = (int)(a.g + (bch.g - a.g) * u);
                int B = (int)(a.b + (bch.b - a.b) * u);

                colors[i] = R | (G << 8) | (B << 16);
            }

            return colors;
        }


        // ------------------------------------------------------------
        //  Blue → White → Red のシンプル diverging
        // ------------------------------------------------------------
        private static int[] CreatePhase(int levels)
        {
            int[] colors = new int[levels];

            (int r, int g, int b) low = (0, 0, 255);
            (int r, int g, int b) mid = (255, 255, 255);
            (int r, int g, int b) high = (255, 0, 0);

            for (int i = 0; i < levels; i++)
            {
                double t = i / (double)(levels - 1);

                (int r, int g, int b) a, bch;
                double u;

                if (t < 0.5)
                {
                    a = low;
                    bch = mid;
                    u = t / 0.5;
                }
                else
                {
                    a = mid;
                    bch = high;
                    u = (t - 0.5) / 0.5;
                }

                int R = (int)(a.r + (bch.r - a.r) * u);
                int G = (int)(a.g + (bch.g - a.g) * u);
                int B = (int)(a.b + (bch.b - a.b) * u);

                colors[i] = R | (G << 8) | (B << 16);
            }

            return colors;
        }


        // ------------------------------------------------------------
        // Viridis (Matplotlib, CC0) - Perceptually uniform colormap
        // Designed by Eric Firing, Nathaniel Smith, and Stefan van der Walt
        // DarkPurple → DarkBlue → Teal → YellowGreen → BrightYellow
        // ------------------------------------------------------------
        private static int[] CreateViridis(int levels)
        {
            int[] colors = new int[levels];

            // Viridis key colors (5 control points for smooth transition)
            (int r, int g, int b) c0 = (68, 1, 84);      // Dark purple
            (int r, int g, int b) c1 = (59, 82, 139);    // Dark blue
            (int r, int g, int b) c2 = (33, 145, 140);   // Teal
            (int r, int g, int b) c3 = (94, 201, 98);    // Yellow-green
            (int r, int g, int b) c4 = (253, 231, 37);   // Bright yellow

            for (int i = 0; i < levels; i++)
            {
                double t = i / (double)(levels - 1);

                (int r, int g, int b) a, bch;
                double u;

                if (t < 0.25)
                {
                    a = c0; bch = c1; u = t / 0.25;
                }
                else if (t < 0.50)
                {
                    a = c1; bch = c2; u = (t - 0.25) / 0.25;
                }
                else if (t < 0.75)
                {
                    a = c2; bch = c3; u = (t - 0.50) / 0.25;
                }
                else
                {
                    a = c3; bch = c4; u = (t - 0.75) / 0.25;
                }

                int R = (int)(a.r + (bch.r - a.r) * u);
                int G = (int)(a.g + (bch.g - a.g) * u);
                int B = (int)(a.b + (bch.b - a.b) * u);

                colors[i] = R | (G << 8) | (B << 16);
            }

            return colors;
        }
    }
}
