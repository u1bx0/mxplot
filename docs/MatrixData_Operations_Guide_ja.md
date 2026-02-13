# MatrixData<T> 操作ガイド



**MxPlot.Core 包括的リファレンス**

> 最終更新: 2026-02-8  
> バージョン: 0.0.2-alpha

*注意：AI生成ドキュメントに基づいており、まだ細部まで検証しきれていません。*

## 📚 目次

1. [はじめに](#はじめに)
2. [コアコンセプト](#コアコンセプト)
3. [データ作成と初期化](#データ作成と初期化)
4. [次元操作](#次元操作)
5. [ボリューム操作](#ボリューム操作)
6. [算術演算](#算術演算)
7. [パイプライン例](#パイプライン例)
8. [極端な例](#極端な例)
9. [メソッドリファレンス](#メソッドリファレンス)

---

## はじめに

`MatrixData<T>`はMxPlot.Coreの中核的なデータコンテナで、物理座標、単位、柔軟な軸管理を備えた多次元科学データの処理を目的として設計されています。

### 主な特徴

- ✅ **多軸サポート**: Time × Z × Channel × Wavelength × FOV × ...
- ✅ **物理座標**: 単位付き実世界スケーリング（µm、nm、秒など）
- ✅ **型安全性**: `Complex`を含むすべての数値型をサポート
- ✅ **高性能**: SIMD最適化、Span<T>、並列処理
- ✅ **ボリュームレンダリング**: MIP/MinIP/AIP投影による3Dボリュームアクセス
- ✅ **メタデータ管理**: 豊富なメタデータディクショナリ

---

## コアコンセプト

### 1. XY平面 + フレーム軸

```
MatrixData<T> = [X × Y] + [Frame Axis]
                 ↑          ↑
            空間次元    多次元軸
            (2D画像)   (Time, Z, Channel等)
```

### 2. 次元構造

```csharp
// 例: 4次元データ (X, Y, Z, Time)
var data = new MatrixData<double>(512, 512, 30);  // 512×512, 30フレーム
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),      // Z: 10スライス
    Axis.Time(3, 0, 5, "s")       // Time: 3時間点
);
// 合計: 10 × 3 = 30 フレーム
```

**メモリレイアウトの重要原則**: 最初の軸が最速変化（stride=1）します。上の例では、Zインデックスが最初に循環し、その後Timeが進みます。

> 📖 **詳細**: 次元構造、ストライド計算、メモリレイアウトの完全な説明は [DimensionStructureガイド](DimensionStructure_MemoryLayout_Guide_ja.md)を参照してください。

### 3. 座標系

- **ピクセル中心**: ピクセル中心で測定される物理スケーリング
- **左下原点**: Y軸は上向き（科学的慣例）
- **不変サイズ**: 作成後のマトリックス次元は固定

---

## データ作成と初期化

### 基本的な作成

```csharp
// 1. シンプルな2Dマトリックス
var matrix2D = new MatrixData<double>(100, 100);
matrix2D.SetXYScale(0, 10, 0, 10);  // 0-10mm範囲
matrix2D.XUnit = "mm";

// 2. 明示的なフレーム数を持つ3Dデータ
var matrix3D = new MatrixData<ushort>(512, 512, 50);

// 3. Scale2Dを使用
var scale = new Scale2D(1024, -50, 50, 1024, -50, 50);
var matrixScaled = new MatrixData<double>(scale, 100);

// 4. 事前割り当て配列から
var arrays = new List<double[]> { new double[100*100], new double[100*100] };
var matrixFromArrays = new MatrixData<double>(100, 100, arrays);
```

### 多軸初期化

```csharp
// 方法1: コレクション式 (C# 12)
var xyczt = new MatrixData<int>(
    Scale2D.Pixels(5, 5),
    [ 
        Axis.Time(4, 0, 2, "s"),     // T=4
        Axis.Z(3, 0, 4, "µm"),       // Z=3
        Axis.Channel(2)              // C=2
    ]  // 合計: 4×3×2 = 24 フレーム
);
xyczt.SetXYScale(-1, 1, -1, 1);

// 方法2: 従来のparams配列
var xyczt2 = new MatrixData<int>(5, 5, 24);
xyczt2.DefineDimensions(
    Axis.Time(4, 0, 2, "s"),
    Axis.Z(3, 0, 4, "µm"),
    Axis.Channel(2)
);

// データ初期化
for (int i = 0; i < xyczt.FrameCount; i++)
    xyczt.Set(i, (ix, iy, x, y) => i + ix * iy);
```

### データ入力

```csharp
// 1. ラムダ関数（インデックスと座標付き）
matrix.Set((ix, iy, x, y) => Math.Sin(x) * Math.Cos(y));

// 2. フレームごとのラムダ
for (int frame = 0; frame < matrix.FrameCount; frame++)
    matrix.Set(frame, (ix, iy, x, y) => frame * x * y);

// 3. 配列直接アクセス
var array = matrix.GetArray(0);
for (int i = 0; i < array.Length; i++)
    array[i] = i * 0.5;

// 4. SetArray（事前計算データ）
var newArray = new double[matrix.XCount * matrix.YCount];
// ... newArrayを埋める ...
matrix.SetArray(newArray, frameIndex: 5);
```

---

## 次元操作

### Transpose（転置）

**全フレームのXY軸を入れ替え**

```csharp
var original = new MatrixData<double>(100, 50);  // 100×50
var transposed = original.Transpose();           // 50×100

// 使用例: 行優先から列優先データへの変換
```

### Crop操作

```csharp
// 1. ピクセルベースのクロップ
var cropped = matrix.Crop(startX: 25, startY: 25, width: 50, height: 50);

// 2. 物理座標クロップ
var physCrop = matrix.CropByCoordinates(xMin: -5, xMax: 5, yMin: -5, yMax: 5);

// 3. 中心クロップ
var centered = matrix.CropCenter(width: 256, height: 256);
```

### SliceAt (2D), ExtractAlong (3D), およびSnapTo (N-1D)

```csharp
// 5次元データ: X, Y, C=2, Z=10, Time=5 (100フレーム)
var hyperStack = new MatrixData<double>(512, 512, 100);
hyperStack.DefineDimensions(
   Axis.Channel(2), 
   Axis.Z(10, 0, 50, "µm"), 
   Axis.Time(5, 0, 10, "s")
);

// SliceAt: 特定軸インデックスで2Dスライスを抽出
var xy = hyperStack.SliceAt(("Channel", 1),("Z",0), ("Time", 2));
// 結果: 512×512　（Channel = 1、 Z = 0、Time = 2の2Dスライス）

// ExtractAlong: 指定軸の3Dスタックを抽出
var xyz = hyperStack.ExtractAlong("Z", baseIndices: new[] {0, 0, 3 });
// 結果: 512×512、10Zフレーム（Channel=0、 Time=3の3Dボリューム）

// SnapTo: 特定軸を単一インデックスにスナップ(次元を落とす）
var xyzt = timeLapse.SnapTo("Channel", indexInAxis: 1);
// 結果: 512×512、10Z、5T（Channel=1の4Dデータ）

```

### Map と Reduce

```csharp
// Map: 全フレームの各ピクセルに関数を適用
var normalized = matrix.Map<double, double>(
    (value, x, y, frameIndex) => value / 255.0
);

// 型変換を伴うMap
var converted = matrixInt.Map<int, double>(
    (value, x, y, frame) => value * 0.01
);

// Reduce: フレーム軸全体で集約
var averaged = timeSeries.Reduce((x, y, values) =>
{
    return values.Average();  // 全フレームの単純平均
});

// カスタムリダクション（例: 最大値）
var maxProjection = stack.Reduce((x, y, values) => values.Max());
```

### Reorder（並べ替え）

#### フレームインデックスによる並べ替え

```csharp
// カスタム順序でフレームを並べ替え
var reordered = matrix.Reorder(new[] { 2, 0, 4, 1, 3 });

// 使用例: メタデータから取得時刻でフレームをソート
var sortedIndices = Enumerable.Range(0, matrix.FrameCount)
    .OrderBy(i => matrix.Metadata[$"Time_{i}"])
    .ToList();
var sorted = matrix.Reorder(sortedIndices);
```

#### 軸名による並べ替え（メモリレイアウト変更）

```csharp
// 元: Z, Channel, Time（Zが最速変化）
var data = new MatrixData<double>(512, 512, 150);
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(3),
    Axis.Time(5, 0, 10, "s")
);

// Channelを最速変化にするために軸順序を変更
var reordered = data.Reorder(new[] { "Channel", "Z", "Time" });

// 使用例: 外部ツールとの互換性
var forImageJ = data.Reorder(new[] { "Channel", "Z", "Time" });  // ImageJ形式
var forNumPy = data.Reorder(new[] { "Time", "Channel", "Z" });   // NumPy C-order
```

---

## ボリューム操作

> 📖 **詳細**: ボリューム投影のアルゴリズム、パフォーマンス特性、内部実装については [VolumeAccessorガイド](VolumeAccessor_Guide_ja.md)を参照してください。

### AsVolume - VolumeAccessorの作成

```csharp
// 1. シンプルな3Dデータ（単一軸）
var volume3D = new MatrixData<ushort>(256, 256, 64);
volume3D.DefineDimensions(Axis.Z(64, 0, 32, "µm"));
var volume = volume3D.AsVolume();  // 単一軸の場合、軸名不要

// 2. 多軸データ: 軸名を指定
var xyczt = new MatrixData<double>(128, 128, 60);  // Z=20, Time=3
xyczt.DefineDimensions(Axis.Z(20, 0, 100, "µm"), Axis.Time(3, 0, 5, "s"));

// Time=1でZ軸に沿ったボリュームを抽出
xyczt.Dimensions["Time"].Index = 1;
var volumeAtT1 = xyczt.AsVolume("Z");  // ActiveIndexを使用

// または正確な座標を指定
var volumeAtT2 = xyczt.AsVolume("Z", baseIndices: new[] { 0, 2 });  // Z=0, Time=2
```

### ボリューム投影

```csharp
var volume = matrix3D.AsVolume();

// 最大値投影（Maximum Intensity Projection）
var mipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);  // 上面図
var mipXZ = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Maximum);  // 側面図
var mipYZ = volume.CreateProjection(ViewFrom.X, ProjectionMode.Maximum);  // 正面図

// 最小値投影（Minimum Intensity Projection）- 暗い特徴に有用
var minipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Minimum);

// 平均投影（Average Intensity Projection）- ノイズ低減
var aipXY = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Average);
```

### ボリューム操作

```csharp
// Restack: 異なる視点軸用にボリュームを再編成
var restackedX = volume.Restack(ViewFrom.X);  // YZスライス
var restackedY = volume.Restack(ViewFrom.Y);  // XZスライス

// SliceAt: 特定深度で2Dスライスを抽出
var sliceAtZ20 = volume.SliceAt(ViewFrom.Z, 20);  // Z=20のXY平面

// ReduceZ: Z軸に沿ったカスタムリダクション
var medianProj = volume.ReduceZ((x, y, values) =>
{
    var sorted = values.OrderBy(v => v).ToArray();
    return sorted[sorted.Length / 2];  // 中央値
});

// ボクセル直接アクセス（パフォーマンスのため境界チェックなし）
double voxelValue = volume[x: 10, y: 20, z: 5];
```

---

## 算術演算

### マトリックス間演算

```csharp
// 要素ごとの演算
var sum = matrixA.Add(matrixB);
var diff = signal.Subtract(background);  // バックグラウンド減算
var product = matrixA.Multiply(matrixB);
var quotient = matrixA.Divide(matrixB);  // フラットフィールド補正

// ブロードキャスト: 単一フレームを多フレームデータに適用可能
var multiFrame = new MatrixData<double>(512, 512, 100);
var singleBackground = new MatrixData<double>(512, 512, 1);
var corrected = multiFrame.Subtract(singleBackground);  // 全フレームからバックグラウンド減算
```

### スカラー演算

```csharp
// スカラー値との算術演算
var scaled = matrix.Multiply(1.5);        // ゲイン補正
var offset = matrix.Add(-100);            // オフセット補正
var shifted = matrix.Subtract(50);

// 使用例: キャリブレーション
var calibrated = rawData
    .Subtract(darkCurrent)  // ダークカレント除去
    .Divide(flatField)      // フラットフィールド補正
    .Multiply(gainFactor);  // ゲイン適用
```

### 重要な注意事項

- **座標継承**: 結果は**第一引数**からスケール/メタデータを継承
- **次元検証**: 軸は個数と構造が一致する必要あり
- **Complex対応**: マトリックス間演算OK、スカラー演算は制限あり（ドキュメント参照）

---

## パイプライン例

### 例1: タイムシリーズ解析

```csharp
// 4次元データをロード: X, Y, Z, Time
var timeLapse = MatrixDataSerializer.LoadTyped<ushort>("timelapse.mxd");

// パイプライン: ROIクロップ → Zスタック抽出 → MIP → 時間平均
var result = timeLapse
    .CropCenter(width: 256, height: 256)           // 中心に焦点
    .ExtractAlong("Z", new[] { 0, 5 })             // Time=5でZスタック取得
    .AsVolume()                                     // ボリュームに変換
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);  // MIP

// さらなる処理
var calibrated = result
    .Subtract(background)
    .Multiply(calibrationFactor);
```

### 例2: マルチチャンネル処理

```csharp
// 5次元データ: X, Y, Z=10, Channel=3, Time=20 (600フレーム)
var multiChannel = new MatrixData<double>(512, 512, 600);
multiChannel.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(3),
    Axis.Time(20, 0, 30, "s")
);

// 緑チャンネル（Channel=1）を全ZとTimeで抽出
var greenChannel = multiChannel.SnapTo("Channel", 1);  // 512×512, Z=10, Time=20

// 緑チャンネルのタイムラプスMIPを作成
var mipSequence = new List<MatrixData<double>>();
for (int t = 0; t < 20; t++)
{
    var zStackAtT = greenChannel.ExtractAlong("Z", new[] { 0, 0, t });
    var mip = zStackAtT.AsVolume().CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);
    mipSequence.Add(mip);
}
```

### 例3: ハイパースペクトル解析

```csharp
// ハイパースペクトルイメージング: X, Y, Wavelength=31, Time=100
var hyperData = new MatrixData<double>(1024, 1024, 3100);
hyperData.DefineDimensions(
    new Axis(31, 400, 700, "Wavelength", "nm"),  // 400-700nm
    Axis.Time(100, 0, 10, "s")
);

// 特定位置でのスペクトルシグネチャを抽出
int xPos = 512, yPos = 512;
var signature = new double[31];
for (int w = 0; w < 31; w++)
{
    var frameIdx = hyperData.Dimensions.GetFrameIndexFrom(new[] { w, 50 });  // Wavelength=w, Time=50
    signature[w] = hyperData.GetValueAt(xPos, yPos, frameIdx);
}

// 全波長の平均強度
var avgAcrossWavelength = hyperData.Reduce((x, y, values) =>
{
    // valuesは長さ3100（31波長 × 100時間点）
    // 波長ごとにグループ化して平均
    return Enumerable.Range(0, 31)
        .Select(w => Enumerable.Range(0, 100).Select(t => values[t * 31 + w]).Average())
        .Average();
});
```

---

## 極端な例

### 🎪 実用的な多次元パイプライン

```csharp
// 6次元データ: X, Y, Z, Time, Channel, FOV（顕微鏡では一般的）
var hyperStack = new MatrixData<ushort>(
        Scale2D.Pixels(512, 512),
      [
        Axis.Z(20, 0, 100, "µm"),     // Z方向走査
        Axis.Time(50, 0, 60, "s"),    // タイムラプス
        Axis.Channel(4),                // DAPI, GFP, RFP, Cy5
        new FovAxis(3, 1, 1)          // タイリング (X:3, Y:1, Z:1)
      ]  // 合計: 20×50×4×3 = 12,000フレーム
      );

// 実用的な解析パイプライン
var result = hyperStack
    .SnapTo("Channel", 1)                // GFPチャンネルを抽出
    .SnapTo("FOV", 1)                      // 中央FOVを選択
    .CropCenter(256, 256)               // ROIに焦点
    .ExtractAlong("Z", new[] { 0, 25 }) // Time=25でZスタック
    .AsVolume()
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum)
    .Subtract(darkCurrent)
    .Divide(flatField)
    .Multiply(1.5);

MatrixDataSerializer.Save("processed.mxd", result, compress: true);
```

### 🚀 もっと極端な例：9次元気象データ（こんな使い方もある？）

```csharp
var bigData = new MatrixData<float>(
    Scale2D.Pixels(32, 32),
    [
        new Axis(12, 1, 12, "Month"),                 // 月
        new Axis(24, 0, 23, "Hour"),                    // 時間
        new Axis(10, 0, 10000, "Altitude", "m"),  // 高度
        new Axis(7, 0, 6, "DayOfWeek"),             // 曜日
        new Axis(4, 0, 3, "Humidity"),                 // 湿度レベル
        new Axis(3, 0, 2, "Pressure"),                  // 気圧レベル
        new Axis(5, 0, 4, "Sensor")                     // センサー種類
    ]  // 合計: 12×24×10×7×4×3×5 = 1,209,600 フレーム！ => OutOfMemoryに注意
);

// SQLのようにデータ抽出・処理
var result = bigData
    .SnapTo("DayOfWeek", 1)    // 月曜日だけ
    .SnapTo("Humidity", 0)        // 乾燥状態だけ
    .SnapTo("Pressure", 1)         // 中圧だけ
    .ExtractAlong("Altitude", new[] { 1, 6, 0, 1 });  // 1月の午前6時、センサー1の高度スタック

// どのくらい時間がかかるかは試してみてね！
```

### 🚀 パフォーマンスモンスター

```csharp
// 1TBのデータを処理（仮想的に）
var hugeData = new MatrixData<ushort>(4096, 4096, 10000);  // ~335GB（ushortの場合）

// Mapによる並列処理
var processed = hugeData.Map<ushort, double>(
    (value, x, y, frame) =>
    {
        // ピクセルごとの複雑な処理
        double normalized = value / 65535.0;
        double filtered = ApplyGaussianKernel(normalized, x, y);
        return filtered * CalibrationFactor;
    },
    useParallel: true  // フレームレベル並列化
);

// これは実際に動く！（十分なRAMがあれば）
```

### 🎨 創造的（誤）使用例

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

### MatrixData<T> コアメソッド

#### 構築
- `MatrixData(int xCount, int yCount)`
- `MatrixData(int xCount, int yCount, int frameCount)`
- `MatrixData(Scale2D scale, params Axis[] axes)`
- `MatrixData(Scale2D scale, IEnumerable<Axis> axes)`
- `MatrixData(int xCount, int yCount, List<T[]> arrays)`

#### データアクセス
- `T GetValueAt(int ix, int iy, int frameIndex = -1)`
- `T GetValueAtTyped(int ix, int iy, int frameIndex = -1)`
- `double GetValueAt(int ix, int iy, int frameIndex = -1)`  // double変換経由
- `T[] GetArray(int frameIndex = -1)`
- `ReadOnlySpan<byte> GetRawBytes(int frameIndex = -1)`

#### データ変更
- `void SetValueAt(int ix, int iy, double v)`
- `void SetValueAt(int ix, int iy, int frameIndex, double v)`
- `void SetValueAtTyped(int ix, int iy, int frameIndex, T value)`
- `void SetArray(T[] srcArray, int frameIndex = -1)`
- `void SetFromRawBytes(ReadOnlySpan<byte> bytes, int frameIndex = -1)`
- `void Set(Func<int, int, double, double, T> func)`
- `void Set(int frameIndex, Func<int, int, double, double, T> func)`

#### 統計
- `(double Min, double Max) GetMinMaxValues()`
- `(double Min, double Max) GetMinMaxValues(int frameIndex)`
- `(double Min, double Max) GetGlobalMinMaxValues()`
- `double GetMinValue()`
- `double GetMaxValue()`
- `void RefreshValueRange()`, `RefreshValueRange(int frameIndex)`

#### スケーリングと単位
- `void SetXYScale(double xmin, double xmax, double ymin, double ymax)`
- `Scale2D GetScale()`
- `double XAt(int ix)`, `double YAt(int iy)`
- `int XIndexOf(double x, bool extendRange = false)`
- `int YIndexOf(double y, bool extendRange = false)`

#### 次元
- `void DefineDimensions(params Axis[] axes)`
- `VolumeAccessor<T> AsVolume(string axisName = "", int[]? baseIndices = null)`

### DimensionalOperator 拡張メソッド

#### 変換
- `MatrixData<T> Transpose<T>()`
- `MatrixData<T> Crop<T>(int startX, int startY, int width, int height)`
- `MatrixData<T> CropByCoordinates<T>(double xMin, double xMax, double yMin, double yMax)`
- `MatrixData<T> CropCenter<T>(int width, int height)`

#### スライシングと抽出
- `MatrixData<T> SliceAt<T>(string axisName, int indexInAxis)`
- `MatrixData<T> SnapTo<T>(string axisName, int indexInAxis, bool deepCopy = false)`
- `MatrixData<T> ExtractAlong<T>(string axisName, int[] baseIndices, bool deepCopy = false)`

#### マッピングとリダクション
- `MatrixData<TDst> Map<TSrc, TDst>(Func<TSrc, double, double, int, TDst> func, bool useParallel = false)`
- `MatrixData<T> Reduce<T>(Func<int, int, T[], T> aggregator)`
- `void ForEach<T>(Action<int, T[]> action, bool useParallel = true)`

#### 並べ替え
- `MatrixData<T> Reorder<T>(IEnumerable<int> order, bool deepCopy = false)`

### VolumeAccessor<T> メソッド

#### インデクサ
- `T this[int ix, int iy, int iz]` - ボクセル直接アクセス

#### 投影
- `MatrixData<T> CreateProjection(ViewFrom axis, ProjectionMode mode)` where mode = Maximum | Minimum | Average

#### 再構成
- `MatrixData<T> Restack(ViewFrom direction)`
- `MatrixData<T> SliceAt(ViewFrom direction, int index)`

#### リダクション
- `MatrixData<T> ReduceZ<T>(Func<int, int, T[], T> reduceFunc)`
- `MatrixData<T> ReduceY<T>(Func<int, int, T[], T> reduceFunc)`
- `MatrixData<T> ReduceX<T>(Func<int, int, T[], T> reduceFunc)`

### MatrixArithmetic 拡張メソッド

#### マトリックス間
- `MatrixData<T> Add<T>(MatrixData<T> a, MatrixData<T> b)`
- `MatrixData<T> Subtract<T>(MatrixData<T> signal, MatrixData<T> background)`
- `MatrixData<T> Multiply<T>(MatrixData<T> a, MatrixData<T> b)`
- `MatrixData<T> Divide<T>(MatrixData<T> a, MatrixData<T> b)`

#### スカラー演算
- `MatrixData<T> Multiply<T>(MatrixData<T> data, double scaleFactor)`
- `MatrixData<T> Add<T>(MatrixData<T> data, double scalar)`
- `MatrixData<T> Subtract<T>(MatrixData<T> data, double scalar)`

### I/O操作

#### MatrixDataSerializer
- `void Save<T>(string filename, MatrixData<T> data, bool compress = false)`
- `MatrixData<T> Load<T>(string filename)`
- `IMatrixData LoadDynamic(string filename)`
- `FileInfo GetFileInfo(string filename)`

#### CSVハンドラ
- `void SaveCsv<T>(string filename, MatrixData<T> data)`
- `MatrixData<double> LoadCsv(string filename)`

---

## ベストプラクティス

### 1. メモリ管理

```csharp
// ✅ 良い: MatrixDataインスタンスを再利用
var temp = new MatrixData<double>(512, 512, 100);
for (int iteration = 0; iteration < 10; iteration++)
{
    // 可能な限りインプレース処理
    ProcessData(temp);
}

// ❌ 悪い: ループ内で新しいインスタンスを作成
for (int iteration = 0; iteration < 10; iteration++)
{
    var temp = new MatrixData<double>(512, 512, 100);  // 毎回割り当て！
    ProcessData(temp);
}
```

### 2. パイプライン設計

```csharp
// ✅ 良い: 操作をチェーン、中間割り当てを最小化
var result = data
    .CropCenter(256, 256)
    .ExtractAlong("Z", indices)
    .AsVolume()
    .CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);

// ❌ 悪い: すべての中間結果を保存
var cropped = data.CropCenter(256, 256);
var extracted = cropped.ExtractAlong("Z", indices);
var volume = extracted.AsVolume();
var result = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);
```

### 3. 並列処理

```csharp
// 大きなフレーム数に対して並列処理を使用
var processed = largeData.Map<ushort, double>(
    (value, x, y, frame) => ExpensiveProcessing(value),
    useParallel: true  // ← 通常>10フレームで有効化
);

// 小さなフレーム数（<10）では、オーバーヘッドが利益を上回る
```

### 4. 次元設計

```csharp
// ✅ 良い: 論理的な軸順序（最も速く変化するものを最初に）
data.DefineDimensions(
    Axis.Z(10, ...),      // 最速変化
    Axis.Channel(3, ...),
    Axis.Time(100, ...)   // 最遅変化
);
// フレーム順序: Z0C0T0, Z1C0T0, ..., Z9C0T0, Z0C1T0, ...

// 規約: 内側（高速） → 外側（低速）
```

---

## パフォーマンス特性

| 操作 | 計算量 | 並列化可能 | メモリ |
|------|--------|-----------|--------|
| `GetValueAt()` | O(1) | 不可 | 最小 |
| `Transpose()` | O(N×M×F) | 可 | フルコピー |
| `Crop()` | O(W×H×F) | 可 | 部分コピー |
| `Map()` | O(N×M×F) | 可 | フルコピー |
| `Reduce()` | O(N×M×F) | 可 | 1フレーム |
| `AsVolume()` | O(1) | 不可 | ゼロコピービュー |
| `CreateProjection()` | O(N×M×D) | 可 | 1フレーム |
| `Add()/Subtract()` | O(N×M×F) | 可 | フルコピー |

*N=XCount, M=YCount, F=FrameCount, D=Depth, W=CropWidth, H=CropHeight*

---

## トラブルシューティング

### よくある問題

**Q: 多軸データで`AsVolume()`が例外を投げるのはなぜ？**

A: 多軸データの場合、Z方向を表す軸を指定する必要があります：
```csharp
// ❌ 間違い
var volume = multiAxisData.AsVolume();

// ✅ 正しい
var volume = multiAxisData.AsVolume("Z");
```

**Q: 算術演算が"Dimension mismatch"で失敗する**

A: フレーム数と次元構造が一致する必要があります：
```csharp
// 次元は互換性が必要
var a = new MatrixData<double>(100, 100, 10);
a.DefineDimensions(Axis.Z(10, 0, 50, "µm"));

var b = new MatrixData<double>(100, 100, 10);
b.DefineDimensions(Axis.Time(10, 0, 5, "s"));  // ❌ 構造が異なる！

// var result = a.Add(b);  // ArgumentExceptionを投げる
```

**Q: メモリ使用量が高すぎる？**

A: 以下を検討してください：
1. 可能な限り`deepCopy: false`を使用
2. 非常に大きなデータセットはチャンクで処理
3. 精度が許容できれば`double`の代わりに`ushort`または`float`を使用
4. ストレージに圧縮を有効化: `MatrixDataSerializer.Save(..., compress: true)`

---

## 関連項目

- **[DimensionStructureガイド](DimensionStructure_MemoryLayout_Guide_ja.md)** - 次元構造、メモリレイアウト、ストライド計算の詳細
- **[VolumeAccessorガイド](VolumeAccessor_Guide_ja.md)** - ボリューム投影のアルゴリズムとパフォーマンス
- [パフォーマンスレポート](VolumeOperator_Performance_Report.md) - ベンチマーク結果
- [APIリファレンス](../README.md) - 完全なAPIドキュメント

---

**ガイド終了**

*最終更新: 2026-02-08  (Generated by GitHub Copilot  --正しくない説明をしている可能性があります )*
