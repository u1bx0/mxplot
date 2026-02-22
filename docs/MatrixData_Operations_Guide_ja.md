# MatrixData<T> オペレーションガイド

**MxPlot.Core 総合リファレンス**

> 最終更新日: 2026-02-22

*注意: このドキュメントは大部分がAIによって生成されたものであり、正確性の確認が必要です。ライブラリの変更に伴い、一部の記述が古い場合があります。*

## 📚 目次

1. [はじめに (Introduction)](https://www.google.com/search?q=%23introduction)
2. [コアコンセプト (Core Concepts)](https://www.google.com/search?q=%23core-concepts)
3. [単一フレーム MatrixData<T> チュートリアル](https://www.google.com/search?q=%23tutorial-single-frame)
4. [多次元データ チュートリアル](https://www.google.com/search?q=%23tutorial-multi-dimensional)
5. [次元操作 (Dimensional Operations)](https://www.google.com/search?q=%23dimensional-operations)
* *Transpose, Crop, Slice, Extract, Select, Map & Reduce, Reorder*


6. [ボリューム操作 (Volume Operations)](https://www.google.com/search?q=%23volume-operations)
* *VolumeAccessor, Projections (MIP/MinIP/AIP), Manipulation*


7. [算術演算 (Arithmetic Operations)](https://www.google.com/search?q=%23arithmetic-operations)
* *行列間演算, スカラー演算, ブロードキャスト*


8. [パイプライン処理の実例 (Pipeline Examples)](https://www.google.com/search?q=%23pipeline-examples)
9. [極端な例 (Extreme Examples)](https://www.google.com/search?q=%23extreme-examples)

---

<a id="introduction"></a>

## はじめに (Introduction)

`MatrixData<T>` は MxPlot.Core における中心的なデータコンテナであり、物理座標、単位、柔軟な軸管理を備えた多次元の科学データを処理するために設計されています。

### 主な機能

* ✅ **多次元軸のサポート**: 時間 × Z × チャンネル × 波長 × FOV × ...
* ✅ **物理座標系**: 現実世界のスケールと単位（µm、nm、秒など）
* ✅ **型安全性**: `Complex` を含むすべての数値型をサポートするジェネリック設計
* ✅ **ハイパフォーマンス**: SIMD最適化、`Span<T>`、並列処理
* ✅ **ボリュームレンダリング**: MIP/MinIP/AIPプロジェクションによる3Dボリュームアクセス
* ✅ **メタデータ管理**: リッチなメタデータ辞書（Key-Value）

---

<a id="core-concepts"></a>

## コアコンセプト (Core Concepts)

### 1. XY平面 + フレーム軸

```text
MatrixData<T> = [X × Y] + [フレーム軸 (Frame Axis)]
                  ↑                   ↑
               空間次元            多次元構造
             (2D 画像)     (Time, Z, Channel など)

```

### 2. 次元構造

```csharp
// 例: 4D データ (X, Y, Z, Time)
var data = new MatrixData<double>(512, 512, 30);  // 512×512, 30フレーム
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),      // Z軸: 10スライス
    Axis.Time(3, 0, 5, "s")       // Time軸: 3タイムポイント
);
// 合計フレーム数: 10 × 3 = 30 フレーム

```

### 3. 座標系

* **ピクセル中心 (Pixel-Centered)**: 物理スケールはピクセルの中心を基準に計測されます。
* **左下原点 (Left-Bottom Origin)**: 科学分野の慣例に従い、Y軸は上に向かって増加します。
* **不変のサイズ (Immutable Size)**: マトリクスのX/Y寸法は作成後に固定されます。

---

<a id="tutorial-single-frame"></a>

## 単一フレーム MatrixData<T> チュートリアル

```csharp
//
// これは、単一フレーム（2D画像/行列）の MatrixData<T> の作成、アクセス、
// 操作方法を網羅したチュートリアル（チートシート）です。
//

// --------------------------------------------------------------------------------
// 1. 作成と座標系 (Creation and Coordinate System)
// --------------------------------------------------------------------------------

// 単一フレームの MatrixData<T> を作成します。T には任意のアンマネージドな
// プリミティブ値型（int, float, double等）を指定できます。System.Numerics.Complex も標準サポート。
// カスタム MinMaxFinder を提供すれば、独自の構造体も使用可能です。
var md = new MatrixData<int>(128, 128); // 128x128寸法のint配列を確保

// 物理データ座標系を定義します。
// (XMin, YMin) は md[0, 0] に対応し、(XMax, YMax) は md[127, 127] に対応します。
md.SetXYScale(-1, 1, -1, 1); 

// 代替案として、寸法と座標系を一度に定義する Scale2D オブジェクトによる初期化も可能です:
md = new MatrixData<int>(new Scale2D(128, -1, 1, 128, -1, 1));

// Scale2D には便利なファクトリメソッドが用意されています:
var scale1 = Scale2D.Pixels(128, 128);         // XとYは 0 から 127
var scale2 = Scale2D.Centered(128, 128, 2, 2); // XとYは -1.0 から 1.0

// 既存の配列をラップすることも可能です（ゼロアロケーション初期化）。
// T[] の長さは XCount * YCount (128 * 128 = 16384) と完全に一致する必要があります。
var array = new int[128 * 128];
md = new MatrixData<int>(128, 128, array);
// ⚠️ 重要: 配列は参照渡しで保持されます。外部から配列を変更すると
// 即座に MatrixData インスタンスに反映され、その逆も同様です。

// メタデータと物理単位の設定（オプションですがプロットやUIに便利です）
md.XUnit = "mm"; 
md.YUnit = "mm"; 
md.Metadata["key"] = "value"; 


// --------------------------------------------------------------------------------
// 2. 生配列へのアクセス（最高パフォーマンス）
// --------------------------------------------------------------------------------
// 内部の1D配列に直接アクセスするのが、最小のオーバーヘッドで要素を処理する最速の方法です。

var data = md.GetArray(); // data.Length == md.XCount * md.YCount

for(int iy = 0; iy < md.YCount; iy++)
{
    double y = md.YValue(iy);    // グリッドインデックス iy に対応する物理Y座標を取得
    int offset = iy * md.XCount; // 高速化のため行の開始インデックスを事前計算
    
    for (int ix = 0; ix < md.XCount; ix++)
    {
        double x = md.XValue(ix); // グリッドインデックス ix に対応する物理X座標を取得
        int index = offset + ix;  // フラットな1Dインデックスを計算
        
        var value = data[index];
        // ここでハイパフォーマンスな処理を適用...
        
        data[index] = ix + iy;    // 値を書き戻す
    }
}

// フレーム内の最小値と最大値を取得します。
// これはオンデマンドで計算され、後続の呼び出しのために自動的にキャッシュされます。
var (min, max) = md.GetValueRange(); 


// --------------------------------------------------------------------------------
// 3. 高レベルなデータアクセスと変更
// --------------------------------------------------------------------------------

// 方法A: ラムダ関数を使用した関数型生成 (`Set`)
// 非常に読みやすいですが、ピクセルごとにデリゲート呼び出しのわずかなオーバーヘッドがあります。
md.Set((ix, iy, x, y) => (int)(x * x + y * y)); 

// 方法B: 2D インデクサ [ix, iy]
// 正確な基礎データ型 (int) を受け取り、返します。
// 注意: ix (列) が先で、iy (行) が後です。
var value11 = md[1, 1]; 
md[1, 1] = value11 + 2;

// 方法C: 型に依存しない Set メソッド (IMatrixData インターフェース経由で便利)
// md[1, 1] = 50 と同等ですが、double を受け取り内部でキャストします。
md.SetValueAt(1, 1, 50.0); 

// 方法D: 型指定 Set メソッド
// 特定の型をすでに知っている場合は、doubleキャストをバイパスします。
// 3番目のパラメータは frameIndex です（単一フレームの場合は 0）。
md.SetValueAtTyped(1, 1, 0, 50); 

// 方法E: 物理座標による値の設定
// 物理位置 (0.5, 0.5) の値を 100 に設定します。
// 座標は自動的に最も近いグリッドインデックスに解決されます。
md.SetValue(0.5, 0.5, 0, 100); 

// 方法F: インデックスベースの Get メソッド
var value11AsDouble = md.GetValueAt(1, 1);    // キャストされた double を返す
var value11AsInt = md.GetValueAtTyped(1, 1);  // 正確な int を返す


// --------------------------------------------------------------------------------
// 4. 空間補間 (物理座標)
// --------------------------------------------------------------------------------
// MatrixData<T> は任意の物理座標 (x, y) でデータをサンプリングできます。

// デフォルト (interpolate: false) では、最も近いグリッド点の正確な T 値 (int) を返します。
var nearestValue = md.GetValue(0.7, -0.2); 

// 最も近い4点の双一次補間 (Bilinear Interpolation) を使用して T 値を推定できます。
var valueByBilinear = md.GetValue(0.7, -0.2, interpolate: true);

// ⚠️ 整数型に関する注意: 
// T が整数の場合、補間付きの `GetValue` はキャスト（切り捨て）された int を返します。
// 双一次補間の精度を維持するには、`GetValueAsDouble` を使用してください。
var preciseInterpolatedValue = md.GetValueAsDouble(0.7, -0.2, interpolate: true);

```

---

<a id="tutorial-multi-dimensional"></a>

## 多次元データ チュートリアル

```csharp
//
// これは、複数フレームとハイパー次元を持つ MatrixData<T> の作成、
// アクセス、操作方法を網羅したチュートリアル（チートシート）です。
//

// --------------------------------------------------------------------------------
// 1. 作成と軸 (Axis) の定義
// --------------------------------------------------------------------------------

// 複数フレームの MatrixData<T> を作成します。データを格納するために内部で List<T[]> を割り当てます。
var md = new MatrixData<float>(128, 128, 32); 
md.SetXYScale(-1, 1, -1, 1);

// デフォルトでは、md は単純な1Dの "Frame" 軸 (0 〜 31) のみを持っています。
// Axis オブジェクトを使用してハイパースタックの次元を定義できます。
md.DefineDimensions(
    Axis.Channel(2),    // 2チャンネルのChannel軸 (インデックス: 0 〜 1)
    Axis.Z(16, -1, 1)   // 16スライスのZ軸 (インデックス: 0 〜 15, 物理: -1 〜 1)
);
// 注意: 各軸の長さの合計積は、フレーム数 (2 * 16 = 32フレーム) と一致する必要があります。
// Axis.Channel, Axis.Z, Axis.Time は一般的な軸用のファクトリメソッドです。

// コンストラクタで直接すべてを初期化する、よりエレガントな方法:
var xyczt = new MatrixData<ushort>(
    Scale2D.Centered(256, 256, 2, 2), // X/Yの寸法と座標系を定義
    Axis.Channel(2),                  // C = 2
    Axis.Z(16, -1, 1),                // Z = 16 (-1 から 1 µm)
    Axis.Time(64, 0, 10)              // T = 64 (0 から 10 秒)
);
// これにより、256x256ピクセル、2048フレーム (2 * 16 * 64) の5Dデータセットが作成されます。

// カスタムの汎用軸を定義することも可能です:
var hyperStack = new MatrixData<double>(
    Scale2D.Pixels(128, 128),
    new Axis(5, 400, 800, "Wavelength", "nm"),          // インデックス 0~4 を物理値 400~800 nm にマッピング
    new Axis(4, 0, 1, "Sensor", isIndexBasedAxis: true) // シンプルなインデックスベースの軸
);

// データをコピーせずに既存の配列リストをラップできます（ゼロアロケーション初期化）:
var list = new List<byte[]> { new byte[128*128], new byte[128*128], new byte[128*128] };
var md2 = new MatrixData<byte>(128, 128, list);


// --------------------------------------------------------------------------------
// 2. フレームの処理とデータアクセス
// --------------------------------------------------------------------------------

// 特定のフレームインデックスの生の1D配列を取得します。
var array1 = md.GetArray(1); 

// フレームインデックスを省略する（または -1 を渡す）と、ActiveIndex の配列を返します。
var arrayActiveIndex = md.GetArray(); 

// ActiveIndex は主にUIの視覚化（スライダーの変更など）に使用されます。
// これを変更すると ActiveIndexChanged イベントが発生します。
md.ActiveIndex = 2; 
var array2 = md.GetArray(); // フレームインデックス2の配列を返すようになります

// すべてのフレームを一括処理する場合、ForEach が最速のアプローチです。
md.ForEach((frameIndex, array) =>
{
    // ここで1Dの 'array' に対する操作を適用します。
    // 並列処理はデフォルトで有効になっています。
}, useParallel: true); 

// 明示的なフレームインデックスを使用したランダムアクセス:
var val1 = md.GetValueAt(1, 1, 2);             // フレーム2のグリッドインデックス (ix=1, iy=1)
var val2 = md.GetValue(0.2, 0.5, 2, true);     // フレーム2の補間された物理座標 (x=0.2, y=0.5)

md.SetValueAt(1, 1, 2, 100);                   // グリッドインデックスによる設定
md.SetValue(0.2, 0.5, 2, 200);                 // 物理座標による設定

// 多次元インデクサ [ix, iy, c, z, t]:
var val3 = xyczt[2, 2, 0, 2, 3]; // X=2, Y=2, Channel=0, Z=2, Time=3
xyczt[2, 2, 0, 2, 3] = 10; 
// フラットなフレームインデックスはバックグラウンドで自動的かつ効率的に計算されます。


// --------------------------------------------------------------------------------
// 3. 軸 (Axis) プロパティへのアクセス
// --------------------------------------------------------------------------------

// 名前（大文字小文字を区別しない）で軸オブジェクトを取得します。
var zaxis = xyczt["Z"]; 
double zmin = zaxis.Min;                // 例: -1.0
double zmax = zaxis.Max;                // 例: 1.0
int znum = zaxis.Count;                 // 例: 16
int zindex = zaxis.IndexOf(0.2);        // 物理値 0.2 に最も近いインデックスを返す
double zpos = zaxis.ValueAt(2);         // インデックス 2 の物理値を返す


// --------------------------------------------------------------------------------
// 4. スライシング、プロジェクション、ボリューム操作（ゼロコピー設計）
// --------------------------------------------------------------------------------
// 注意: ほとんどの構造的操作は、軽量なビューを返すか、最大のパフォーマンスを
// 引き出すためにゼロコピーのメカニズムを利用しています。

// スライシング: 特定の軸の値に基づいてサブデータセットを抽出します。
var xytz = xyczt.SelectBy("Channel", 1);                 // "Channel" 軸を落とし、4Dデータを返す
var xyz = xyczt.ExtractAlong("Z", [1, 0, 2]);            // C=1, T=2 における3D Zスタックを抽出
var xy = xyczt.SliceAt(("Channel", 0), ("Z", 1), ("Time", 2)); // 単一の2Dフレームを返す

// VolumeAccessor: 多次元データを3Dボリューム (X, Y, および1つのターゲット軸) として表示。
// ActiveIndex を使用して残りの次元 (Time や Channel など) を固定します。
var volume = xyczt.AsVolume("Z"); 

// プロジェクション (次元圧縮):
// Y軸に沿った最大値投影 (MIP) を作成します (結果として X-Z 画像が生成されます)。
var xzProj = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Maximum); 

// 形状変更/再スタック:
// ボリュームをX軸に沿って再スライスし、Y-Z 平面の新しいスタックを返します。
var yzx = volume.Restack(ViewFrom.X); 

// ボリュームからの単一面抽出:
var xzSlice = volume.SliceAt(ViewFrom.Y, 2); // Yインデックス = 2 における X-Z スライスを取得

```

---

<a id="dimensional-operations"></a>

## 次元操作 (Dimensional Operations)

### 転置 (Transpose)

**全フレームの X軸と Y軸を入れ替えます**

```csharp
var original = new MatrixData<double>(100, 50);  // 100×50
var transposed = original.Transpose();           // 50×100

// 用途: 行優先(row-major)から列優先(column-major)データへの変換

```

### 切り抜き (Crop Operations)

```csharp
// 1. ピクセルベースの切り抜き
var cropped = matrix.Crop(startX: 25, startY: 25, width: 50, height: 50);

// 2. 物理座標ベースの切り抜き
var physCrop = matrix.CropByCoordinates(xMin: -5, xMax: 5, yMin: -5, yMax: 5);

// 3. 中心からの切り抜き
var centered = matrix.CropCenter(width: 256, height: 256);

```

### SliceAt (2D), ExtractAlong (3D), および SelectBy (N-1D)

```csharp
// 5Dデータ: X, Y, C=2, Z=10, Time=5 (計100フレーム)
var hyperStack = new MatrixData<double>(512, 512, 100);
hyperStack.DefineDimensions(
   Axis.Channel(2), 
   Axis.Z(10, 0, 50, "µm"), 
   Axis.Time(5, 0, 10, "s")
);

// SliceAt: 特定の軸インデックスで2Dスライスを抽出
var xy = hyperStack.SliceAt(("Channel", 1),("Z",0), ("Time", 2));
// 結果: 512×512 (Channel=1, Z=0, Time=2)

// ExtractAlong: 座標を固定して特定の軸に沿った3Dボリュームを抽出
var xyz = hyperStack.ExtractAlong("Z", baseIndices: new[] {0, 0, 3 });
// 結果: 512×512, 10個の Zフレーム (Channel=0, Time=3)

// SelectBy: 1つの軸を固定して (N-1)D データを抽出
var xyzt = timeLapse.SelectBy("Channel", indexInAxis: 1);
// 結果: 512×512, 10 Z, 5 T (Channel=1)

```

### Map & Reduce (マップとリデュース)

```csharp
// Map: 全フレームの各ピクセルに関数を適用
var normalized = matrix.Map<double, double>(
    (value, x, y, frameIndex) => value / 255.0
);

// 型変換を伴う Map
var converted = matrixInt.Map<int, double>(
    (value, x, y, frame) => value * 0.01
);

// Reduce: フレーム軸に沿って集約
var averaged = timeSeries.Reduce((x, y, values) =>
{
    return values.Average();  // 全フレームの単純平均
});

// カスタムリデュース (例: 最大値)
var maxProjection = stack.Reduce((x, y, values) => values.Max());

```

### 並べ替え (Reorder)

```csharp
// カスタム順序でフレームを並べ替え
var reordered = matrix.Reorder([2, 0, 4, 1, 3]);

// 用途: メタデータの取得時間順にフレームをソートする
var sortedIndices = Enumerable.Range(0, matrix.FrameCount)
    .OrderBy(i => matrix.Metadata[$"Time_{i}"])
    .ToList();
var sorted = matrix.Reorder(sortedIndices);

// データをコピーせず各フレームへの参照を作成するため、同じフレームを繰り返すことも可能
var repeated = matrix.Reorder([0, 0, 1, 1, 2, 2]);

```

---

<a id="volume-operations"></a>

## ボリューム操作 (Volume Operations)

### AsVolume - VolumeAccessorの作成

```csharp
// 1. シンプルな3Dデータ (単一の多次元軸)
var volume3D = new MatrixData<ushort>(256, 256, 64);
volume3D.DefineDimensions(Axis.Z(64, 0, 32, "µm"));
var volume = volume3D.AsVolume();  // 単一軸の場合は軸名の指定不要

// 2. 多軸データ: 軸名を指定する
var xyczt = new MatrixData<double>(128, 128, 60);  // Z=20, Time=3
xyczt.DefineDimensions(Axis.Z(20, 0, 100, "µm"), Axis.Time(3, 0, 5, "s"));

// Time=1 でZ軸に沿ったボリュームを抽出
xyczt.Dimensions["Time"].Index = 1;
var volumeAtT1 = xyczt.AsVolume("Z");  // ActiveIndex が使用される

// または正確な座標を指定
var volumeAtT2 = xyczt.AsVolume("Z", baseIndices: new[] { 0, 2 });  // Z=0, Time=2

```

### ボリューム・プロジェクション (投影)

```csharp
var volume = matrix3D.AsVolume(); 

// 最大値投影 (MIP: Maximum Intensity Projection)
var mipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);  // 上面図
var mipXZ = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Maximum);  // 側面図
var mipYZ = volume.CreateProjection(ViewFrom.X, ProjectionMode.Maximum);  // 正面図

// 最小値投影 (MinIP) - 暗い特徴を捉えるのに便利
var minipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Minimum);

// 平均値投影 (AIP) - ノイズの低減
var aipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Average);

```

### ボリュームの操作

```csharp
// Restack: 異なる表示軸に合わせてボリュームを再構成 (新しい MatrixData として)
var restackedX = volume.Restack(ViewFrom.X);  // YZ スライス
var restackedY = volume.Restack(ViewFrom.Y);  // XZ スライス

// SliceAt: 特定の深度で2Dスライスを抽出
var sliceAtZ20 = volume.SliceAt(ViewFrom.Z, 20);  // Z=20 における XY 平面

// ReduceZ: Z軸に沿ったカスタムリデュース
var medianProj = volume.ReduceZ((x, y, values) =>
{
    var sorted = values.OrderBy(v => v).ToArray();
    return sorted[sorted.Length / 2];  // 中央値 (Median)
});

// ボクセルへの直接アクセス (パフォーマンスのため境界チェックなし)
double voxelValue = volume[x: 10, y: 20, z: 5];

```

---

<a id="arithmetic-operations"></a>

## 算術演算 (Arithmetic Operations)

### 行列間演算 (Matrix-to-Matrix)

```csharp
// 要素ごとの演算
var sum = matrixA.Add(matrixB);
var diff = signal.Subtract(background);  // 背景の減算
var product = matrixA.Multiply(matrixB);
var quotient = matrixA.Divide(matrixB);  // フラットフィールド補正

// ブロードキャスト (Broadcasting): 単一フレームを複数フレームデータに適用可能
var multiFrame = new MatrixData<double>(512, 512, 100);
var singleBackground = new MatrixData<double>(512, 512, 1);
var corrected = multiFrame.Subtract(singleBackground);  // 全フレームから背景を減算

```

### スカラー演算 (Scalar Operations)

```csharp
// スカラー値での算術演算
var scaled = matrix.Multiply(1.5);        // ゲイン補正
var offset = matrix.Add(-100);            // オフセット補正
var shifted = matrix.Subtract(50);

// 用途: キャリブレーション
var calibrated = rawData
    .Subtract(darkCurrent)  // 暗電流を除去
    .Divide(flatField)      // フラットフィールド補正
    .Multiply(gainFactor);  // ゲインを適用

```

### 重要な注意事項 (Important Notes)

* **座標の継承**: 結果のオブジェクトは、**最初の引数**からスケールとメタデータを継承します。
* **次元の検証**: 行列間の演算では、軸の数と構造が一致している必要があります（ブロードキャスト時を除く）。
* **Complex型のサポート**: 行列間演算は可能ですが、スカラー演算には一部制限があります（ドキュメント参照）。

---

<a id="pipeline-examples"></a>

## パイプライン処理の実例 (Pipeline Examples)

### 実例 1: 時系列解析 (Time-Series Analysis)

```csharp
// 4Dデータをロード: X, Y, Z, Time
var timeLapse = MatrixDataSerializer.LoadTyped<ushort>("timelapse.mxd");

// パイプライン: ROI切り抜き → Zスタック抽出 → MIP生成 → 時間方向での平均化
var result = timeLapse
    .CropCenter(width: 256, height: 256)           // 中心にフォーカス
    .ExtractAlong("Z", new[] { 0, 5 })             // Time=5 のZスタックを取得
    .AsVolume()                                    // ボリュームに変換
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);  // MIP

// 追加の処理
var calibrated = result
    .Subtract(background)
    .Multiply(calibrationFactor);

```

### 実例 2: マルチチャンネル処理 (Multi-Channel Processing)

```csharp
// 5Dデータ: X, Y, Z=10, Channel=3, Time=20 (計600フレーム)
var multiChannel = new MatrixData<double>(512, 512, 600);
multiChannel.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(3),
    Axis.Time(20, 0, 30, "s")
);

// すべてのZおよびTimeにわたって緑チャンネル (Channel=1) を抽出
var greenChannel = multiChannel.SliceAt("Channel", 1);  // 512×512, Z=10, Time=20

// 緑チャンネルのタイムラプスMIPを作成
var mipSequence = new List<MatrixData<double>>();
for (int t = 0; t < 20; t++)
{
    var zStackAtT = greenChannel.ExtractAlong("Z", new[] { 0, 0, t });
    var mip = zStackAtT.AsVolume().CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);
    mipSequence.Add(mip);
}

```

### 実例 3: ハイパースペクトル解析 (Hyperspectral Analysis)

```csharp
// ハイパースペクトル・イメージング: X, Y, Wavelength(波長)=31, Time=100
var hyperData = new MatrixData<double>(1024, 1024, 3100);
hyperData.DefineDimensions(
    new Axis(31, 400, 700, "Wavelength", "nm"),  // 400-700 nm
    Axis.Time(100, 0, 10, "s")
);

// 特定の位置でのスペクトル・シグネチャを抽出
int xPos = 512, yPos = 512;
var signature = new double[31];
for (int w = 0; w < 31; w++)
{
    var frameIdx = hyperData.Dimensions.GetFrameIndexFrom(new[] { w, 50 });  // 波長=w, Time=50
    signature[w] = hyperData.GetValueAt(xPos, yPos, frameIdx);
}

// すべての波長にわたる平均強度を計算
var avgAcrossWavelength = hyperData.Reduce((x, y, values) =>
{
    // values の長さは 3100 (波長31 × Time100)
    // 波長ごとにグループ化して平均化
    return Enumerable.Range(0, 31)
        .Select(w => Enumerable.Range(0, 100).Select(t => values[t * 31 + w]).Average())
        .Average();
});

```

---

<a id="extreme-examples"></a>

## 極端な例 (Extreme Examples)

### 🎪 実用的な多次元パイプライン

```csharp
// 6Dデータ: X, Y, Z, Time, Channel, FOV (顕微鏡画像で一般的)
var multiModal = new MatrixData<ushort>(
    Scale2D.Pixels(512, 512),
    [
        Axis.Z(20, 0, 100, "µm"),     // Zスキャン
        Axis.Time(50, 0, 60, "s"),    // タイムラプス
        Axis.Channel(4),              // DAPI, GFP, RFP, Cy5
        new FovAxis(3, 1, 1)          // タイリング (X:3, Y:1, Z:1)
    ]  // 合計: 20×50×4×3 = 12,000 フレーム (実用範囲内！)
);

// 実用的な解析パイプライン
var result = multiModal
    .SelectBy("Channel", 1)              // GFPチャンネルを抽出
    .SelectBy("FOV", 1)                  // 中央のFOVを選択
    .CropCenter(256, 256)                // ROIにフォーカス
    .ExtractAlong("Z", new[] { 0, 25 })  // Time=25 におけるZスタック
    .AsVolume()
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum)
    .Subtract(darkCurrent)
    .Divide(flatField)
    .Multiply(1.5);

MatrixDataSerializer.Save("processed.mxd", result, compress: true);

```

### 🚀 さらに極端な例: 9次元の気象データ（果たして可能か？）

```csharp
var bigData = new MatrixData<float>(
    Scale2D.Pixels(32, 32),
    [
        new Axis(12, 0, 11, "Month"),             // 月
        new Axis(24, 0, 23, "Hour"),              // 時間
        new Axis(10, 0, 10000, "Altitude", "m"),  // 標高 (高度)
        new Axis(7, 0, 6, "DayOfWeek"),           // 曜日
        new Axis(4, 0, 3, "Humidity"),            // 湿度レベル
        new Axis(3, 0, 2, "Pressure"),            // 気圧レベル
        new Axis(5, 0, 4, "Sensor")               // センサー種別
    ]  // 合計: 12×24×10×7×4×3×5 = 1,209,600 フレーム！ => (PCのメモリ不足 / OOMに注意)
);

// SQLライクなクエリによる操作と処理
var result = bigData
    .SelectBy("DayOfWeek", 1)                                   // 月曜日のみ
    .SelectBy("Humidity", 0)                                    // 乾燥条件のみ
    .SelectBy("Pressure", 1)                                    // 中間気圧のみ
    .ExtractAlong("Altitude", new[] { 1, 6, 0, 1 });            // 1月の午前6時におけるセンサー1の高度スタック

```

---


### 🎨 創造的（誤）使用例?

#### 1. 次元を越えたフラクタル生成

```csharp
var fractal4D = new MatrixData<double>(512, 512, 100);
fractal4D.DefineDimensions(Axis.Z(10, 0, 1, ""), Axis.Time(10, 0, 1, ""));

fractal4D.ForEach((frame, array) =>
{
    var coords = fractal4D.Dimensions.GetCoordinatesFrom(frame);
    double z = coords[0] * 0.1;
    double t = coords[1] * 0.1;
    
    for (int iy = 0; iy < 512; iy++)
    {
        for (int ix = 0; ix < 512; ix++)
        {
            double x = (ix - 256) / 256.0;
            double y = (iy - 256) / 256.0;
            array[iy * 512 + ix] = MandelbrotValue(x, y, z, t);
        }
    }
}, useParallel: true);

// 4D MIP可視化を作成
var mipAcrossZT = fractal4D.Reduce((x, y, values) => values.Max());
```

#### 2. 時間反転ビデオ処理

```csharp
var video = LoadVideo("input.mxd");  // MatrixData<byte> with Time axis

// 時間を反転
var indices = Enumerable.Range(0, video.FrameCount).Reverse().ToList();
var reversed = video.Reorder(indices);

// 時間的平滑化を適用
var smoothed = reversed.Map<byte, byte>((value, x, y, frame) =>
{
    int prevFrame = Math.Max(0, frame - 1);
    int nextFrame = Math.Min(reversed.FrameCount - 1, frame + 1);
    
    byte prev = reversed.GetValueAtTyped(x / scale, y / scale, prevFrame);
    byte next = reversed.GetValueAtTyped(x / scale, y / scale, nextFrame);
    
    return (byte)((prev + value + next) / 3);
});
```

#### 3. マルチスケールピラミッド

```csharp
var pyramid = new List<MatrixData<double>>();
var current = originalImage;

for (int level = 0; level < 5; level++)
{
    pyramid.Add(current);
    
    // 2倍ダウンサンプリング
    int newWidth = current.XCount / 2;
    int newHeight = current.YCount / 2;
    var downsampled = new MatrixData<double>(newWidth, newHeight);
    
    downsampled.Set((ix, iy, x, y) =>
    {
        return (current.GetValueAt(ix*2, iy*2) + 
                current.GetValueAt(ix*2+1, iy*2) +
                current.GetValueAt(ix*2, iy*2+1) +
                current.GetValueAt(ix*2+1, iy*2+1)) / 4.0;
    });
    
    current = downsampled;
}
```

---

## メソッドリファレンス

全てを網羅していない可能性があります。

### MatrixData<T> Core Methods

#### Construction
- `MatrixData(int xCount, int yCount, T[]? array = null)` — single-frame; optionally supply a preallocated array for the frame.
- `MatrixData(int xCount, int yCount, int frameCount)` — allocate `frameCount` frames (new arrays).
- `MatrixData(int xCount, int yCount, List<T[]> arrayList)` — use provided arrays (one per frame). Each array must have length `xCount*yCount`.
- `MatrixData(int xCount, int yCount, List<T[]> arrayList, List<List<double>> minValueList, List<List<double>> maxValueList)` — provide structured per-frame min/max lists (useful for `Complex` and other structured types).
- `MatrixData(int xCount, int yCount, List<T[]> arrayList, List<double> primitiveMinValueList, List<double> primitiveMaxValueList)` — provide scalar per-frame min/max values; converted internally to structured lists.
- `MatrixData(Scale2D scale)` — create a single frame using `scale` for XY coordinates.
- `MatrixData(Scale2D scale, params Axis[] axes)` — create with specified `scale` and frame `axes`; total frames = product of axis counts; constructor validates the resulting frame count.

Notes:
- All constructors validate plane size and initialize the internal arrays and `Dimensions` object.
- Min/max statistics are cached per-array and computed on demand; if you provide min/max lists they are used as the initial cache. Otherwise the cache is empty and will be populated on first request.

#### Data Access
- `T GetValueAt(int ix, int iy, int frameIndex = -1)`
- `T GetValueAtTyped(int ix, int iy, int frameIndex = -1)`
- `double GetValueAt(int ix, int iy, int frameIndex = -1)`  // via double conversion
- `T[] GetArray(int frameIndex = -1)`
- `ReadOnlySpan<byte> GetRawBytes(int frameIndex = -1)`

#### Data Modification
- `void SetValueAt(int ix, int iy, double v)`
- `void SetValueAt(int ix, int iy, int frameIndex, double v)`
- `void SetValueAtTyped(int ix, int iy, int frameIndex, T value)`
- `void SetArray(T[] srcArray, int frameIndex = -1)`
- `void SetFromRawBytes(ReadOnlySpan<byte> bytes, int frameIndex = -1)`
- `void Set(Func<int, int, double, double, T> func)`
- `void Set(int frameIndex, Func<int, int, double, double, T> func)`

#### Statistics (updated)
- `(double Min, double Max) GetValueRange()`
- `(double Min, double Max) GetValueRange(int frameIndex)`
- `(double Min, double Max) GetGlobalValueRange()`
- `double GetMinValue()`
- `double GetMaxValue()`
- `void InvalidateValueRange()`
- `void InvalidateValueRange(int frameIndex)`


#### Scaling & Units
- `void SetXYScale(double xmin, double xmax, double ymin, double ymax)`
- `Scale2D GetScale()`
- `double XAt(int ix)`, `double YAt(int iy)`
- `int XIndexOf(double x, bool extendRange = false)`
- `int YIndexOf(double y, bool extendRange = false)`

#### Dimensions
- `void DefineDimensions(params Axis[] axes)`
- `VolumeAccessor<T> AsVolume(string axisName = "", int[]? baseIndices = null)`

### DimensionalOperator Extensions

#### Transformation
- `MatrixData<T> Transpose<T>()`
- `MatrixData<T> Crop<T>(int startX, int startY, int width, int height)`
- `MatrixData<T> CropByCoordinates<T>(double xMin, double xMax, double yMin, double yMax)`
- `MatrixData<T> CropCenter<T>(int width, int height)`

#### Slicing & Extraction
- `MatrixData<T> SliceAt<T>(string axisName, int indexInAxis)`
- `MatrixData<T> ExtractAlong<T>(string axisName, int[] baseIndices, bool deepCopy = false)`

#### Mapping & Reduction
- `MatrixData<TDst> Map<TSrc, TDst>(Func<TSrc, double, double, int, TDst> func, bool useParallel = false)`
- `MatrixData<T> Reduce<T>(Func<int, int, T[], T> aggregator)`
- `void ForEach<T>(Action<int, T[]> action, bool useParallel = true)`

#### Reordering
- `MatrixData<T> Reorder<T>(IEnumerable<int> order, bool deepCopy = false)`

### VolumeAccessor<T> Methods

#### Indexing
- `T this[int ix, int iy, int iz]` - Direct voxel access

#### Projections
- `MatrixData<T> CreateProjection(ViewFrom axis, ProjectionMode mode)` where mode = Maximum | Minimum | Average

#### Restructuring
- `MatrixData<T> Restack(ViewFrom direction)`
- `MatrixData<T> SliceAt(ViewFrom direction, int index)`

#### Reduction
- `MatrixData<T> ReduceZ<T>(Func<int, int, T[], T> reduceFunc)`
- `MatrixData<T> ReduceY<T>(Func<int, int, T[], T> reduceFunc)`
- `MatrixData<T> ReduceX<T>(Func<int, int, T[], T> reduceFunc)`

### MatrixArithmetic Extensions

#### Matrix-to-Matrix
- `MatrixData<T> Add<T>(MatrixData<T> a, MatrixData<T> b)`
- `MatrixData<T> Subtract<T>(MatrixData<T> signal, MatrixData<T> background)`
- `MatrixData<T> Multiply<T>(MatrixData<T> a, MatrixData<T> b)`
- `MatrixData<T> Divide<T>(MatrixData<T> a, MatrixData<T> b)`

#### Scalar Operations
- `MatrixData<T> Multiply<T>(MatrixData<T> data, double scaleFactor)`
- `MatrixData<T> Add<T>(MatrixData<T> data, double scalar)`
- `MatrixData<T> Subtract<T>(MatrixData<T> data, double scalar)`

### I/O Operations

#### MatrixDataSerializer
- `void Save<T>(string filename, MatrixData<T> data, bool compress = false)`
- `MatrixData<T> Load<T>(string filename)`
- `IMatrixData LoadDynamic(string filename)`
- `FileInfo GetFileInfo(string filename)`

#### CSV Handler
- `void SaveCsv<T>(string filename, MatrixData<T> data)`
- `MatrixData<double> LoadCsv(string filename)`

---
