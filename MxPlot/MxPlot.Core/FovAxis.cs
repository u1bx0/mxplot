using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;

namespace MxPlot.Core
{
    /// <summary>
    /// Structure representing the world coordinates of each tile.
    /// </summary>
    /// <param name="X">X coordinate in world space.</param>
    /// <param name="Y">Y coordinate in world space.</param>
    /// <param name="Z">Z coordinate in world space.</param>
    public readonly record struct GlobalPoint(double X, double Y, double Z);

    /// <summary>
    /// Tile counts of a FOV grid, along X, Y and Z.
    /// </summary>
    /// <remarks>
    /// A named type rather than a <c>(int, int, int)</c> tuple: the tuple's backing members are
    /// fields named <c>Item1</c>/<c>Item2</c>/<c>Item3</c>, which forced
    /// <c>JsonSerializerOptions.IncludeFields</c> on for the whole <c>.mxd</c> header and wrote
    /// those meaningless names into every file. A record struct serializes as
    /// <c>{"X":..,"Y":..,"Z":..}</c> through ordinary properties.
    /// </remarks>
    /// <param name="X">Number of tiles along X.</param>
    /// <param name="Y">Number of tiles along Y.</param>
    /// <param name="Z">Number of tiles along Z.</param>
    public readonly record struct TileGrid(int X, int Y, int Z);

    /// <summary>
    /// Result of tile overlap validation.
    /// </summary>
    /// <param name="TileIndex">One-dimensional tile index.</param>
    /// <param name="TileX">Grid column number (X coordinate).</param>
    /// <param name="TileY">Grid row number (Y coordinate).</param>
    /// <param name="OverlapX">Pixel overlap with the right neighbor (X+1); null if at the right edge.</param>
    /// <param name="OverlapY">Pixel overlap with the bottom neighbor (Y+1); null if at the bottom edge.</param>
    public record TileOverlapResult(
        int TileIndex,
        int TileX,
        int TileY,
        decimal? OverlapX,
        decimal? OverlapY
    );

    /// <summary>
    /// FOV axis class that inherits from <see cref="Axis"/> and maintains the origin coordinates 
    /// (world coordinates such as stage position) for each FOV. Maps <see cref="Axis.Index"/> to <see cref="GlobalPoint"/>.
    /// </summary>
    /// <remarks>
    /// <b>⚠️ 3D Tiling Limitation:</b><br/>
    /// Currently, only 2D tiling (Z = 1) is supported. 3D tiling (Z > 1) will throw <see cref="NotSupportedException"/>
    /// because proper synchronization between <see cref="Axis.Index"/> and <see cref="ZIndex"/> is not yet implemented.
    /// This feature is reserved for future development.
    /// <para>
    /// For 2D tiling, <see cref="Index"/> and tile coordinates (X, Y) are fully synchronized and work as expected.
    /// </para>
    /// </remarks>
    public class FovAxis : Axis
    {
        private int _zIndex = 0;

        /// <summary>
        /// The origin coordinate of every tile, indexed by one-dimensional tile index.
        /// Individual elements can also be reached through the indexers: <c>FovAxis[ix, iy, iz]</c>.
        /// </summary>
        /// <remarks>
        /// This property is the single serialization surface for the origins; it is restored
        /// through the <c>[JsonConstructor]</c> below. It previously shadowed a
        /// <c>[JsonInclude]</c>-annotated private field, which left it ambiguous whether the array
        /// round-tripped under the field's name, the property's name, or both.
        /// <para>
        /// The array itself is mutable by design — the indexers write through it and raise
        /// <see cref="OriginChanged"/>. Callers that mutate it directly bypass that event.
        /// </para>
        /// </remarks>
        public GlobalPoint[] Origins { get; }

        /// <summary> 
        /// Tile counts when FOVs are arranged in a grid.
        /// Note: The actual display position is determined by the <see cref="Origins"/> property.
        /// </summary>
        public TileGrid TileLayout { get; }

        
        /// <summary>
        /// Specifies the active Z index when tiles exist in the Z-axis direction.
        /// </summary>
        public int ZIndex
        {
            get => _zIndex;
            set
            {
                if (_zIndex != value && value >= 0 && value < TileLayout.Z)
                {
                    _zIndex = value;
                    ZIndexChanged?.Invoke(this, value);
                }
            }
        }


        /// <summary>
        /// Indexer for direct access to Origins.
        /// </summary>
        /// <param name="index">One-dimensional tile index.</param>
        /// <returns>The <see cref="GlobalPoint"/> at the specified index.</returns>
        public GlobalPoint this[int index]
        {
            get => Origins[index];
            set
            {
                if (Origins[index] == value)
                    return;

                // 1. Update the value
                Origins[index] = value;

                OriginChanged?.Invoke(this, index);
            }
        }
        /// <summary>
        /// Two-dimensional indexer: Z coordinate is fixed to <see cref="ZIndex"/>.
        /// </summary>
        /// <param name="x">X tile coordinate.</param>
        /// <param name="y">Y tile coordinate.</param>
        /// <returns>The <see cref="GlobalPoint"/> at the specified (x, y, ZIndex) position.</returns>
        public GlobalPoint this[int x, int y]
        {
            get
            {
                return Origins[GetIndex(x, y, ZIndex)];
            }
            set
            {
                this[GetIndex(x, y, ZIndex)] = value; // Delegate to one-dimensional indexer
            }
        }

        /// <summary>
        /// Three-dimensional indexer for explicit Z coordinate access.
        /// </summary>
        /// <param name="x">X tile coordinate.</param>
        /// <param name="y">Y tile coordinate.</param>
        /// <param name="z">Z tile coordinate.</param>
        /// <returns>The <see cref="GlobalPoint"/> at the specified (x, y, z) position.</returns>
        public GlobalPoint this[int x, int y, int z]
        {
            get => Origins[GetIndex(x, y, z)];
            set => this[GetIndex(x, y, z)] = value; // Delegate to one-dimensional indexer
        }

        /// <summary>
        /// Event raised when the global coordinate (Origin) of any tile changes.
        /// The event argument contains the one-dimensional tile index that changed.
        /// </summary>
        public event EventHandler<int>? OriginChanged;

        /// <summary>
        /// Event raised when the Z index changes for XY tile display in a 3D grid.
        /// The event argument contains the new Z index value.
        /// </summary>
        public event EventHandler<int>? ZIndexChanged;

        /// <summary>
        /// Creates the bottom-left (origin) coordinates for a grid of gXNum × gYNum tiles,
        /// extended from a base tile.
        /// When a single tile has scale size w × h and pixelOverlap=1, the entire grid size becomes
        /// gw = w × gXNum, gh = h × gYNum.
        /// (Generated and modified with Gemini 3 Pro)
        /// </summary>
        /// <param name="tileScale">The relative coordinate scale of each individual tile.</param>
        /// <param name="gXNum">Number of tiles in the X direction.</param>
        /// <param name="gYNum">Number of tiles in the Y direction.</param>
        /// <param name="pixelOverlap">Number of overlapping pixels between tiles (1 = edges overlap).</param>
        /// <param name="baseTileIndex">Index of the reference tile from which the tile space (coordinates) extends.</param>
        /// <returns>A tuple containing the origins array, tile width, and tile height.</returns>
        public static (GlobalPoint[] origins, double tileWidth, double tileHeight)
            Create2DTileOriginsExtendedFrom(Scale2D tileScale, int gXNum, int gYNum, int pixelOverlap = 1, int baseTileIndex = 0) 
        {
            // Information for a single tile
            int xnum = tileScale.XCount;
            int ynum = tileScale.YCount;
            double xmin = tileScale.XMin;
            double xmax = tileScale.XMax;
            double ymin = tileScale.YMin;
            double ymax = tileScale.YMax;

            // 1. Calculate pixel pitch
            // Based on "XNum - 1", dividing the distance between edge pixel centers by (count - 1)
            // Guard: if xnum = 1, set to 0
            double pixelPitchX = (xnum > 1) ? (xmax - xmin) / (xnum - 1) : 0;
            double pixelPitchY = (ynum > 1) ? (ymax - ymin) / (ynum - 1) : 0;

            // 2. Calculate stride (movement between tiles)
            // (total pixels - overlap pixels) × pixel pitch
            // Example: 3 pixels with 1 pixel overlap = effectively 2 pixels of movement
            double strideX = pixelPitchX * (xnum - pixelOverlap);
            double strideY = pixelPitchY * (ynum - pixelOverlap);

            // 3. Identify the grid position of the base tile
            int baseGx = baseTileIndex % gXNum;
            int baseGy = baseTileIndex / gXNum;
            
            var origins = new GlobalPoint[gXNum * gYNum];

            for (int igy = 0; igy < gYNum; igy++)
            {
                for (int igx = 0; igx < gXNum; igx++)
                {
                    // Current array index
                    int index = igy * gXNum + igx;

                    // Relative grid distance from the base tile
                    int diffX = igx - baseGx;
                    int diffY = igy - baseGy;

                    // Calculate coordinates
                    // Starting from base coordinates (xmin, ymin), move by stride amount
                    // Works correctly even when diff is negative (left/below the base)
                    double currentOriginX = xmin + (diffX * strideX);
                    double currentOriginY = ymin + (diffY * strideY);

                    origins[index] = new GlobalPoint(currentOriginX, currentOriginY, 0);
                }
            }
            return (origins, tileScale.XRange, tileScale.YRange);
        }

        /// <summary>
        /// Generates the origin list for each tile when subdividing a defined total region (totalScale) 
        /// into a specified number of tiles.
        /// Interprets totalScale.XNum/YNum as the pixel count per individual tile, and totalScale.Width/Height 
        /// as the total size → pixel pitch is adjusted accordingly.
        /// (Generated and modified with Gemini 3 Pro)
        /// </summary>
        /// <remarks>
        /// Interpretation of <paramref name="totalScale"/>:
        /// - Min/Max: Physical start and end coordinates of the entire region
        /// - Num: <b>[IMPORTANT]</b> Pixel count per individual tile (NOT the total pixel count)
        /// 
        /// Based on this definition, the pixel pitch is automatically adjusted to fit exactly within the total width.
        /// </remarks>
        /// <param name="totalScale">Scale definition of the total region to subdivide. 
        /// Note that XCount and YCount represent the pixel count per tile.</param>
        /// <param name="gXNum">Number of subdivisions in the X direction.</param>
        /// <param name="gYNum">Number of subdivisions in the Y direction.</param>
        /// <param name="pixelOverlap">Number of overlapping pixels between tiles.</param>
        /// <returns>A tuple containing the origins array (bottom-left coordinates as GlobalPoint), tile width, and tile height.</returns>
        public static (GlobalPoint[] origins, double tileWidth, double titleHeight) 
            Create2DTileOriginsSubdividedFrom(Scale2D totalScale, int gXNum, int gYNum, int pixelOverlap = 1)
        {
            // ---------------------------
            // X-axis calculation
            // ---------------------------
            // ★ Cast to decimal here to start the calculation
            decimal totalWidth = (decimal)totalScale.XRange;
            decimal xMin = (decimal)totalScale.XMin;
            int tileXNum = totalScale.XCount;

            // Calculate denominator (using decimal for consistency, though int would work here)
            decimal totalIntervalsX = (decimal)(gXNum - 1) * (tileXNum - pixelOverlap) + (tileXNum - 1);

            // Guard against division by zero
            if (totalIntervalsX <= 0)
                totalIntervalsX = (tileXNum > 1) ? (tileXNum - 1) : 1; // Safeguard (adjust to context as needed)

            // ★ CRITICAL: Calculate pitch using decimal (28-29 significant digits, greatly reducing error)
            decimal pitchX = totalWidth / totalIntervalsX;

            // Calculate stride and tile width
            decimal strideX = pitchX * (tileXNum - pixelOverlap);
            decimal tileWidthDec = pitchX * (tileXNum - 1);

            // ---------------------------
            // Y-axis calculation (similarly using decimal)
            // ---------------------------
            decimal totalHeight = (decimal)(totalScale.YMax - totalScale.YMin);
            decimal yMin = (decimal)totalScale.YMin;
            int tileYNum = totalScale.YCount;

            decimal totalIntervalsY = (decimal)(gYNum - 1) * (tileYNum - pixelOverlap) + (tileYNum - 1);

            // Guard against division by zero
            if (totalIntervalsY <= 0)
                totalIntervalsY = (tileYNum > 1) ? (tileYNum - 1) : 1;

            decimal pitchY = totalHeight / totalIntervalsY;
            decimal strideY = pitchY * (tileYNum - pixelOverlap);
            decimal tileHeightDec = pitchY * (tileYNum - 1);

            // ---------------------------
            // Generate Origins array
            // ---------------------------
            var origins = new GlobalPoint[gXNum * gYNum];

            for (int igy = 0; igy < gYNum; igy++)
            {
                for (int igx = 0; igx < gXNum; igx++)
                {
                    int index = igy * gXNum + igx;

                    // ★ Calculate coordinates in decimal, then cast to double for storage
                    decimal currentOriginX = xMin + (igx * strideX);
                    decimal currentOriginY = yMin + (igy * strideY);

                    // GlobalPoint constructor accepts double
                    origins[index] = new GlobalPoint((double)currentOriginX, (double)currentOriginY, 0);
                }
            }

            // Return values are also cast back to double
            return (origins, (double)tileWidthDec, (double)tileHeightDec);
        }

        /// <summary>
        /// Validates the pixel overlap for each tile in FovAxis based on their Origin coordinates
        /// relative to the given tileScale.
        /// Checks whether tile edges properly overlap based on tileScale's pitch by calculating
        /// the right and top edge coordinates from each tile's bottom-left origin and comparing
        /// with adjacent tiles' bottom-left origins.
        /// Logic generated by Gemini 3 Pro based on this specification.
        /// </summary>
        /// <remarks>
        /// Example usage to check if all overlaps are valid (tolerance &lt; 0.001):
        /// <code>
        /// bool isAllValid = overlaps.All(r =>
        ///     (r.OverlapX == null || Math.Abs(r.OverlapX.Value - Math.Round(r.OverlapX.Value)) &lt; 0.001m) &amp;&amp;
        ///     (r.OverlapY == null || Math.Abs(r.OverlapY.Value - Math.Round(r.OverlapY.Value)) &lt; 0.001m)
        /// );
        /// </code>
        /// </remarks>
        /// <param name="fovAxis">The FOV axis to validate.</param>
        /// <param name="tileScale">The scale definition for individual tiles.</param>
        /// <returns>A list of <see cref="TileOverlapResult"/> containing overlap validation results for each tile.</returns>
        public static List<TileOverlapResult> ValidatePixelOverlapFor(FovAxis fovAxis, Scale2D tileScale)
        {
            int xnum = tileScale.XCount;
            int ynum = tileScale.YCount;
            GlobalPoint[] origins = fovAxis.Origins;
            int gxnum = fovAxis.TileLayout.X;
            int gynum = fovAxis.TileLayout.Y;

            // For high-precision calculation
            decimal xpitch = Convert.ToDecimal(tileScale.XStep);
            decimal ypitch = Convert.ToDecimal(tileScale.YStep);

            var results = new List<TileOverlapResult>();

            Debug.WriteLine("=== Pixel Overlap Validation Start ===");
            Debug.WriteLine($"Tile: {gxnum} x {gynum}, TileSize: {xnum} x {ynum}");

            for (int iy = 0; iy < gynum; iy++)
            {
                for (int ix = 0; ix < gxnum; ix++)
                {
                    int currentIndex = iy * gxnum + ix;
                    GlobalPoint currentOrigin = origins[currentIndex];

                    decimal? overlapX = null;
                    decimal? overlapY = null;

                    // -------------------------------------------------
                    // 1. Validation in X direction (right neighbor)
                    // -------------------------------------------------
                    if (ix < gxnum - 1)
                    {
                        int nextIndexX = currentIndex + 1;
                        decimal currentX = (decimal)currentOrigin.X;
                        decimal nextX = (decimal)origins[nextIndexX].X;

                        decimal dist = nextX - currentX;

                        if (xpitch != 0)
                        {
                            decimal shiftPixels = dist / xpitch;
                            overlapX = xnum - shiftPixels;

                            // Integer validation (tolerance < 0.001 is OK)
                            bool isInteger = Math.Abs(overlapX.Value - Math.Round(overlapX.Value)) < 0.001m;
                            string status = isInteger ? "OK" : "WARNING (Sub-pixel)";

                            Debug.WriteLine($"[X-Check] Tile[{ix},{iy}]->Idx{nextIndexX} : Dist={dist:F2}, Shift={shiftPixels:F2}px, Overlap={overlapX:F2}px [{status}]");
                        }
                    }

                    // -------------------------------------------------
                    // 2. Validation in Y direction (bottom neighbor)
                    // -------------------------------------------------
                    if (iy < gynum - 1)
                    {
                        int nextIndexY = currentIndex + gxnum;
                        decimal currentY = (decimal)currentOrigin.Y;
                        decimal nextY = (decimal)origins[nextIndexY].Y;

                        decimal dist = nextY - currentY;

                        if (ypitch != 0)
                        {
                            decimal shiftPixels = dist / ypitch;
                            overlapY = ynum - shiftPixels;

                            bool isInteger = Math.Abs(overlapY.Value - Math.Round(overlapY.Value)) < 0.001m;
                            string status = isInteger ? "OK" : "WARNING (Sub-pixel)";

                            Debug.WriteLine($"[Y-Check] Tile[{ix},{iy}]->Idx{nextIndexY} : Dist={dist:F2}, Shift={shiftPixels:F2}px, Overlap={overlapY:F2}px [{status}]");
                        }
                    }

                    results.Add(new TileOverlapResult(currentIndex, ix, iy, overlapX, overlapY));
                }
            }

            Debug.WriteLine("=== Validation Finished ===");
            return results;

            /**
             * Example usage:
             *
               var overlaps = ValidatePixelOverlapFor(myAxis, myScale);
                // 1. Check if all are valid (tolerance < 0.001 is OK)
                bool isAllValid = overlaps.All(r => 
                    (r.OverlapX == null || Math.Abs(r.OverlapX.Value - Math.Round(r.OverlapX.Value)) < 0.001m) &&
                    (r.OverlapY == null || Math.Abs(r.OverlapY.Value - Math.Round(r.OverlapY.Value)) < 0.001m)
                );

                // 2. Find tiles where overlap differs from the intended value (e.g., 10px)
                var badTiles = overlaps.Where(r => r.OverlapX.HasValue && Math.Round(r.OverlapX.Value) != 10).ToList();

                foreach(var bad in badTiles)
                {
                    Console.WriteLine($"Error at Tile {bad.TileIndex}: OverlapX is {bad.OverlapX}");
                }
             * 
                   */
             }

             /// <summary>
             /// JSON deserialization constructor. Only <c>origins</c> and <c>tileLayout</c> are required —
             /// <c>Count</c>, <c>Min</c>, <c>Max</c> are derived, and <c>Name</c>/<c>Unit</c> are restored via setters.
             /// </summary>
             /// <param name="origins">Array of tile origin coordinates.</param>
             /// <param name="tileLayout">Tile layout (X, Y, Z counts).</param>
             [JsonConstructor]
             private FovAxis(GlobalPoint[] origins, TileGrid tileLayout)
                 : base(origins?.Length ?? 0, 0, (origins?.Length ?? 1) - 1, "FOV", "", isIndexBased: true)
             {
                 Origins = origins ?? Array.Empty<GlobalPoint>();
                 TileLayout = tileLayout;
             }

             /// <summary>
             /// Initializes FovAxis with the specified tile counts. 
             /// All origins are set to (0,0,0) and must be configured later.
             /// </summary>
             /// <param name="xNum">Number of tiles in the X direction.</param>
             /// <param name="yNum">Number of tiles in the Y direction.</param>
             /// <param name="zNum">Number of tiles in the Z direction (default: 1).</param>
             /// <exception cref="NotSupportedException">Thrown when zNum &gt; 1 (3D tiling not yet supported).</exception>
             public FovAxis(int xNum, int yNum, int zNum = 1)
            : base(xNum * yNum * zNum, 0, xNum * yNum * zNum - 1, "FOV", "", true)
        {
            if (zNum > 1)
            {
                // 3D tiling is reserved as a future enhancement
                throw new NotSupportedException(
                    "3D tiling (zNum > 1) is not currently supported. " +
                    "Index and ZIndex synchronization is not implemented.");
            }
            Origins = new GlobalPoint[Count];
            TileLayout = new TileGrid(xNum, yNum, zNum);
        }

        /// <summary>
        /// Initializes FovAxis with a copy of the provided origin list.
        /// </summary>
        /// <param name="origins">List of origin coordinates for each tile.</param>
        /// <param name="xNum">Number of tiles in the X direction.</param>
        /// <param name="yNum">Number of tiles in the Y direction.</param>
        /// <param name="zNum">Number of tiles in the Z direction (default: 1).</param>
        /// <exception cref="NotSupportedException">Thrown when zNum &gt; 1 (3D tiling not yet supported).</exception>
        /// <exception cref="ArgumentException">Thrown when the tile layout size does not match the origins count.</exception>
        public FovAxis(List<GlobalPoint> origins, int xNum, int yNum, int zNum = 1)
            : base(origins.Count, 0, origins.Count - 1, "FOV", "", true)
        {
            if (zNum > 1)
            {
                // 3D tiling is reserved as a future enhancement
                throw new NotSupportedException(
                    "3D tiling (zNum > 1) is not currently supported. " +
                    "Index and ZIndex synchronization is not implemented.");
            }

            Origins = origins.ToArray();
            TileLayout = new TileGrid(xNum, yNum, zNum);
            if (xNum * yNum * zNum != origins.Count)
                throw new ArgumentException("Tile layout size does not match origins count.");
        }


        /// <summary>
        /// Converts 3D tile coordinates (x, y, z) to a one-dimensional array index.
        /// </summary>
        /// <param name="xIndex">X tile coordinate.</param>
        /// <param name="yIndex">Y tile coordinate.</param>
        /// <param name="zIndex">Z tile coordinate (default: 0).</param>
        /// <returns>The one-dimensional array index.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when any index is out of range.</exception>
        public int GetIndex(int xIndex, int yIndex, int zIndex=0)
        {
            // Recommended to enable for debugging
            if (xIndex < 0 || xIndex >= TileLayout.X) throw new ArgumentOutOfRangeException(nameof(xIndex), $"X index {xIndex} is out of range (0-{TileLayout.X - 1})");
            if (yIndex < 0 || yIndex >= TileLayout.Y) throw new ArgumentOutOfRangeException(nameof(yIndex), $"Y index {yIndex} is out of range (0-{TileLayout.Y - 1})");
            if (zIndex < 0 || zIndex >= TileLayout.Z) throw new ArgumentOutOfRangeException(nameof(zIndex), $"Z index {zIndex} is out of range (0-{TileLayout.Z - 1})");

            // Plane (slice) size = X count × Y count
            int planeSize = TileLayout.X * TileLayout.Y;

            // Row size = X count
            int rowSize = TileLayout.X;

            // Formula: (Z offset) + (Y offset) + X
            return (zIndex * planeSize) + (yIndex * rowSize) + xIndex;
        }

        /// <summary>
        /// Creates a deep copy of this <see cref="FovAxis"/> instance.
        /// </summary>
        /// <returns>A new <see cref="FovAxis"/> instance with the same origin values and tile layout.</returns>
        public override FovAxis Clone()
        {
            var fov = new FovAxis(new List<GlobalPoint>(this.Origins), this.TileLayout.X, this.TileLayout.Y, this.TileLayout.Z);
            fov.Unit = this.Unit;
            return fov;
        }
    }



}
