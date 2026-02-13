using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace MxPlot.Core
{
    /// <summary>
    /// 各タイルのワールド座標を表すための構造体
    /// </summary>
    /// <param name="X"></param>
    /// <param name="Y"></param>
    /// <param name="Z"></param>
    public readonly record struct GlobalPoint(double X, double Y, double Z);

    /// <summary>
    /// タイルの重なり検証結果
    /// </summary>
    /// <param name="TileIndex">1次元インデックス</param>
    /// <param name="TileX">グリッド列番号</param>
    /// <param name="TileY">グリッド行番号</param>
    /// <param name="OverlapX">右隣(X+1)との重なり画素数（右端の場合は null）</param>
    /// <param name="OverlapY">下隣(Y+1)との重なり画素数（下端の場合は null）</param>
    public record TileOverlapResult(
        int TileIndex,
        int TileX,
        int TileY,
        decimal? OverlapX,
        decimal? OverlapY
    );

    /// <summary>
    /// FOVの軸、各FOVでの原点座標（ワールド座標=ステージ位置など）を保持するAxisの継承クラス。Indexに対してGlobalPointをマッピングする
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
    [Serializable]
    public class FovAxis : Axis
    {
        // Jsonシリアライズなどでデータが消えないようにプロパティ化推奨
        // (private fieldのままだと保存されない場合があるため)
        [System.Text.Json.Serialization.JsonInclude] // 必要に応じて属性をつける
        private GlobalPoint[] _origins;


        private int _zIndex = 0;

        /// <summary>
        /// 読み取り専用として配列全体へのアクセスを提供（シリアライザ対策兼任）
        /// これを使わなくても、FovAxis[ix, iy, iz]でアクセスできる
        /// </summary>
        public GlobalPoint[] Origins => _origins;

        /// <summary> 
        /// FOVを格子状に配置したときのタイルのレイアウト情報 (X方向の数, Y方向の数, Z方向の数)
        /// ただし、実際の表示位置はOriginプロパティで決定される
        /// </summary>
        public (int X, int Y, int Z) TileLayout { get; }

        
        /// <summary>
        /// z軸方向にもタイルが存在する場合に、アクティブなZインデックスを指定する
        /// 
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
        /// Orignに直接アクセスするインデクサ
        /// </summary>
        /// <param name="index"></param>
        /// <returns></returns>
        public GlobalPoint this[int index]
        {
            get => _origins[index];
            set
            {
                if (_origins[index] == value)
                    return;

                // 1. 値を更新
                _origins[index] = value;

                OriginChanged?.Invoke(this, index);
            }
        }
        /// <summary>
        /// 2次元インデクサ: zはZIndexに固定
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        // 
        public GlobalPoint this[int x, int y]
        {
            get
            {
                return _origins[GetIndex(x, y, ZIndex)];
            }
            set
            {
                this[GetIndex(x, y, ZIndex)] = value; // 1次元インデクサへ委譲
            }
        }

        // 3次元インデクサ
        public GlobalPoint this[int x, int y, int z]
        {
            get => _origins[GetIndex(x, y, z)];
            set => this[GetIndex(x, y, z)] = value; // 1次元インデクサへ委譲
        }

        /// <summary>
        /// 各タイルのGlobal座標（Origin）が変化したときに発生するイベント
        /// </summary>
        public event EventHandler<int>? OriginChanged;

        /// <summary>
        /// 3次元格子で、XYタイル表示をしている場合のZインデックスが変化したときに発生するイベント
        /// </summary>
        public event EventHandler<int>? ZIndexChanged;

        /// <summary>
        /// gXNum * gYNumのタイルの左下座標を設定する
        /// 1枚のタイルのスケールサイズをw * hとしたとき、pixelOverlapが1の場合にタイル全体のサイズはgw = w * gXNum, gh = h * gYNumとなる
        /// (Gemini3 Proで生成、修正)
        /// </summary>
        /// <param name="tileScale">要素タイルの相対座標</param>
        /// <param name="XNum"></param>
        /// <param name="yNum"></param>
        /// <param name="pixelOverlap"> = 1とすると、エッジが重なる</param>
        /// <param name="baseTileIndex">基準とするタイルのindex　このindexからタイル空間（座標）を広げる</param>
        /// <returns></returns>
        public static (GlobalPoint[] origins, double tileWidth, double tileHeight)
            Create2DTileOriginsExtendedFrom(Scale2D tileScale, int gXNum, int gYNum, int pixelOverlap = 1, int baseTileIndex = 0) 
        {
            // 1枚のタイルの情報
            int xnum = tileScale.XCount;
            int ynum = tileScale.YCount;
            double xmin = tileScale.XMin;
            double xmax = tileScale.XMax;
            double ymin = tileScale.YMin;
            double ymax = tileScale.YMax;

            // 1. 画素ピッチ（Pixel Pitch）の計算
            // 「XNum - 1」をベースにするため、両端の画素中心間の距離を (個数 - 1) で割る
            // ※ xnum > 1 である前提ですが、念のため1の場合は0にするガードを入れています
            double pixelPitchX = (xnum > 1) ? (xmax - xmin) / (xnum - 1) : 0;
            double pixelPitchY = (ynum > 1) ? (ymax - ymin) / (ynum - 1) : 0;

            // 2. タイル間の移動量（Stride）の計算
            // (総画素数 - 重なり画素数) × ピクセルピッチ
            // 例: 3画素で1画素重なるなら、実質2画素分移動する
            double strideX = pixelPitchX * (xnum - pixelOverlap);
            double strideY = pixelPitchY * (ynum - pixelOverlap);

            // 3. 基準タイルのグリッド位置を特定
            int baseGx = baseTileIndex % gXNum;
            int baseGy = baseTileIndex / gXNum;
            
            var origins = new GlobalPoint[gXNum * gYNum];

            for (int igy = 0; igy < gYNum; igy++)
            {
                for (int igx = 0; igx < gXNum; igx++)
                {
                    // 現在の配列インデックス
                    int index = igy * gXNum + igx;

                    // 基準タイルからの相対グリッド距離
                    int diffX = igx - baseGx;
                    int diffY = igy - baseGy;

                    // 座標計算
                    // 基準座標(xmin, ymin) を起点に、ストライド分だけ移動させる
                    // diffがマイナス（基準より左/下）の場合も正しく計算されます
                    double currentOriginX = xmin + (diffX * strideX);
                    double currentOriginY = ymin + (diffY * strideY);

                    origins[index] = new GlobalPoint(currentOriginX, currentOriginY, 0);
                }
            }
            return (origins, tileScale.XRange, tileScale.YRange);
        }

        /// <summary>
        /// 定義された全体領域(totalScale)を指定した枚数で分割した際の、各タイルの原点(Origin)リストを生成する
        /// totalScale.XNum .YNumを一つのタイルのピクセル数として、totalScale.Width .Heightを全体サイズとする⇒ピッチが変わる
        /// (Gemini3 Proで生成、修正)
        /// </summary>
        /// <remarks>
        /// <paramref name="totalScale"/> の解釈について：
        /// - Min/Max : 全体の物理的な開始・終了座標
        /// - Num     : 【重要】1タイルあたりの画素数 (全体画素数ではありません)
        /// 
        /// 上記定義に基づき、全体幅にぴったり収まるように画素ピッチ(Pitch)が自動調整されます。
        /// </remarks>
        /// <param name="totalScale">分割対象となる全体領域のスケール定義（ただしXNum, YNumは各タイルのピクセル数であることに注意</param>
        /// <param name="gXNum">X方向の分割枚数</param>
        /// <param name="gYNum">Y方向の分割枚数</param>
        /// <param name="pixelOverlap">タイル間ののりしろ画素数</param>
        /// <returns>各タイルの左下座標(GlobalPoint)の配列</returns>
        public static (GlobalPoint[] origins, double tileWidth, double titleHeight) 
            Create2DTileOriginsSubdividedFrom(Scale2D totalScale, int gXNum, int gYNum, int pixelOverlap = 1)
        {
            // ---------------------------
            // X軸の計算
            // ---------------------------
            // ★ ここで decimal にキャストして計算を開始します
            decimal totalWidth = (decimal)totalScale.XRange;
            decimal xMin = (decimal)totalScale.XMin;
            int tileXNum = totalScale.XCount;

            // 分母の計算 (int同士の計算なのでここはまだintでも平気ですが、念のためdecimalで統一)
            decimal totalIntervalsX = (decimal)(gXNum - 1) * (tileXNum - pixelOverlap) + (tileXNum - 1);

            // ガード処理
            if (totalIntervalsX <= 0)
                totalIntervalsX = (tileXNum > 1) ? (tileXNum - 1) : 1; // 0除算回避の安全策（文脈に合わせて調整してください）

            // ★ ここが最重要：Pitchを decimal で計算（有効桁数が28-29桁になり、誤差が激減します）
            decimal pitchX = totalWidth / totalIntervalsX;

            // StrideとTileWidthの計算
            decimal strideX = pitchX * (tileXNum - pixelOverlap);
            decimal tileWidthDec = pitchX * (tileXNum - 1);

            // ---------------------------
            // Y軸の計算 (同様に decimal 化)
            // ---------------------------
            decimal totalHeight = (decimal)(totalScale.YMax - totalScale.YMin);
            decimal yMin = (decimal)totalScale.YMin;
            int tileYNum = totalScale.YCount;

            decimal totalIntervalsY = (decimal)(gYNum - 1) * (tileYNum - pixelOverlap) + (tileYNum - 1);

            // ガード処理
            if (totalIntervalsY <= 0)
                totalIntervalsY = (tileYNum > 1) ? (tileYNum - 1) : 1;

            decimal pitchY = totalHeight / totalIntervalsY;
            decimal strideY = pitchY * (tileYNum - pixelOverlap);
            decimal tileHeightDec = pitchY * (tileYNum - 1);

            // ---------------------------
            // Origin配列の生成
            // ---------------------------
            var origins = new GlobalPoint[gXNum * gYNum];

            for (int igy = 0; igy < gYNum; igy++)
            {
                for (int igx = 0; igx < gXNum; igx++)
                {
                    int index = igy * gXNum + igx;

                    // ★ decimal で座標を確定させてから double にキャストして格納
                    decimal currentOriginX = xMin + (igx * strideX);
                    decimal currentOriginY = yMin + (igy * strideY);

                    // GlobalPointのコンストラクタが double を受け取ると仮定
                    origins[index] = new GlobalPoint((double)currentOriginX, (double)currentOriginY, 0);
                }
            }

            // 戻り値も double に戻す
            return (origins, (double)tileWidthDec, (double)tileHeightDec);
        }

        /// <summary>
        /// tileScaleに対して、FovAxisの各タイルのOring座標からpixelOverlapがどうなっているかを検証する
        /// つまり、tileScaleのピッチに基づいて、各タイルのエッジが正しく重なっているかを調べる
        /// この場合、タイルの左下座標(origin)を基準に、右端と上端の座標を計算し、隣接タイルの左下座標と比較する
        /// -- という仕様でGemeni 3 Proがロジックを生成
        /// //1. 全て正常かチェック（誤差 0.001 未満ならOKとする）する例
        /// bool isAllValid = overlaps.All(r =>
        /// (r.OverlapX == null || Math.Abs(r.OverlapX.Value - Math.Round(r.OverlapX.Value)) < 0.001m) &&
        ///(r.OverlapY == null || Math.Abs(r.OverlapY.Value - Math.Round(r.OverlapY.Value)) < 0.001m)
        ///);
        /// </summary>
        /// <param name="fovAxis"></param>
        /// <param name="tileScale"></param>
        public static List<TileOverlapResult> ValidatePixelOverlapFor(FovAxis fovAxis, Scale2D tileScale)
        {
            int xnum = tileScale.XCount;
            int ynum = tileScale.YCount;
            GlobalPoint[] origins = fovAxis.Origins;
            int gxnum = fovAxis.TileLayout.X;
            int gynum = fovAxis.TileLayout.Y;

            // 高精度計算用
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
                    // 1. X方向 (右隣) の検証
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

                            // 整数判定（誤差 0.001 未満ならOK）
                            bool isInteger = Math.Abs(overlapX.Value - Math.Round(overlapX.Value)) < 0.001m;
                            string status = isInteger ? "OK" : "WARNING (Sub-pixel)";

                            Debug.WriteLine($"[X-Check] Tile[{ix},{iy}]->Idx{nextIndexX} : Dist={dist:F2}, Shift={shiftPixels:F2}px, Overlap={overlapX:F2}px [{status}]");
                        }
                    }

                    // -------------------------------------------------
                    // 2. Y方向 (下隣) の検証
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
             *使い方の例）
             *
              var overlaps = ValidatePixelOverlapFor(myAxis, myScale);
                // 1. 全て正常かチェック（誤差 0.001 未満ならOKとする）
                bool isAllValid = overlaps.All(r => 
                    (r.OverlapX == null || Math.Abs(r.OverlapX.Value - Math.Round(r.OverlapX.Value)) < 0.001m) &&
                    (r.OverlapY == null || Math.Abs(r.OverlapY.Value - Math.Round(r.OverlapY.Value)) < 0.001m)
                );

                // 2. 意図したOverlap数（例えば10px）と違う箇所を探す
                var badTiles = overlaps.Where(r => r.OverlapX.HasValue && Math.Round(r.OverlapX.Value) != 10).ToList();

                foreach(var bad in badTiles)
                {
                    Console.WriteLine($"Error at Tile {bad.TileIndex}: OverlapX is {bad.OverlapX}");
                }
             * 
             */
        }

        /// <summary>
        /// FOVの要素数で初期化、Originはすべて(0,0,0)⇒後で設定する必要がある
        /// </summary>
        /// <param name="num"></param>
        public FovAxis(int xNum, int yNum, int zNum = 1)
            : base(xNum * yNum * zNum, 0, xNum * yNum * zNum - 1, "FOV", "", true)
        {
            if (zNum > 1)
            {
                // 3D tilingは将来の拡張機能として予約
                throw new NotSupportedException(
                    "3D tiling (zNum > 1) is not currently supported. " +
                    "Index and ZIndex synchronization is not implemented.");
            }
            _origins = new GlobalPoint[Count];
            TileLayout = (xNum, yNum, zNum);
        }

        /// <summary>
        /// FOVの原点リスト（のコピー）で初期化
        /// </summary>
        /// <param name="origins"></param>
        /// <param name="xNum"></param>
        /// <param name="yNum"></param>
        /// <param name="zNum"></param>
        public FovAxis(List<GlobalPoint> origins, int xNum, int yNum, int zNum = 1)
            : base(origins.Count, 0, origins.Count - 1, "FOV", "", true)
        {
            if (zNum > 1)
            {
                // 3D tilingは将来の拡張機能として予約
                throw new NotSupportedException(
                    "3D tiling (zNum > 1) is not currently supported. " +
                    "Index and ZIndex synchronization is not implemented.");
            }

            _origins = origins.ToArray();
            TileLayout = (xNum, yNum, zNum);
            if (xNum * yNum * zNum != origins.Count)
                throw new ArgumentException("Tile layout size does not match origins count.");
        }


        // インデックス計算ロジック
        public int GetIndex(int xIndex, int yIndex, int zIndex=0)
        {
            // デバッグ時のために有効化を推奨
            if (xIndex < 0 || xIndex >= TileLayout.X) throw new ArgumentOutOfRangeException(nameof(xIndex), $"X index {xIndex} is out of range (0-{TileLayout.X - 1})");
            if (yIndex < 0 || yIndex >= TileLayout.Y) throw new ArgumentOutOfRangeException(nameof(yIndex), $"Y index {yIndex} is out of range (0-{TileLayout.Y - 1})");
            if (zIndex < 0 || zIndex >= TileLayout.Z) throw new ArgumentOutOfRangeException(nameof(zIndex), $"Z index {zIndex} is out of range (0-{TileLayout.Z - 1})");

            // 面（スライス）のサイズ = X方向の数 * Y方向の数
            int planeSize = TileLayout.X * TileLayout.Y;

            // 行のサイズ = X方向の数
            int rowSize = TileLayout.X;

            // 計算式: (Zオフセット) + (Yオフセット) + X
            return (zIndex * planeSize) + (yIndex * rowSize) + xIndex;
        }

        public override FovAxis Clone()
        {
            var fov = new FovAxis(new List<GlobalPoint>(this._origins), this.TileLayout.X, this.TileLayout.Y, this.TileLayout.Z);
            return fov;
        }
    }



}
