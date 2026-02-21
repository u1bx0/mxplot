# DimensionStructure とメモリレイアウトガイド

**MxPlot.Core 技術リファレンス**

> 最終更新: 2026-02-08  
> バージョン: 0.0.2

## 📚 目次

1. [概要](#概要)
2. [メモリレイアウトの基礎](#メモリレイアウトの基礎)
3. [Innermost実装の詳細](#innermost実装の詳細)
4. [ストライド計算](#ストライド計算)
5. [他ライブラリとの比較](#他ライブラリとの比較)
6. [軸定義の再構成と並べ替え](#軸定義の再構成と並べ替え)
7. [実用例](#実用例)
8. [ベストプラクティス](#ベストプラクティス)
9. [FovAxis: 視野タイリング](#fovaxis-視野タイリング)
10. [将来の拡張](#将来の拡張)

---

## 概要

`DimensionStructure`は、`MatrixData<T>`のバッキング配列（`List<T[]>`）における**線形フレームインデックス**と**多次元軸座標**の関係を管理します。この対応関係を理解することは、効率的なデータアクセスと他の科学計算ライブラリとの相互運用性にとって重要です。

### 重要な設計原則

> **最初の軸 = 最速変化（innermost）次元**

これは**MATLAB/Fortran列優先規約**に従っており、最初の次元がメモリ内で最も速く変化します。

---

## メモリレイアウトの基礎

### 基本概念

```csharp
// MatrixData構造
MatrixData<T> = [X × Y] + [Frame Axis 0, Frame Axis 1, ...]
                 ↑              ↑
            空間次元(2D)    多次元フレーム
```

### フレーム配列ストレージ

```csharp
// 内部ストレージ
private List<T[]> _arraysList;  // 各T[]は平坦化された2D画像(X×Y)
```

各フレーム（`T[]`）は、多次元空間内の特定座標における単一のXY平面を表します。

### 例: 4次元データ (X, Y, Z, Time)

```csharp
var data = new MatrixData<double>(512, 512, 50);
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),      // axes[0] - innermost（最速変化）
    Axis.Time(5, 0, 10, "s")      // axes[1] - outermost（最遅変化）
);
```

**`_arraysList`内のフレーム順序:**

| フレームインデックス | Zインデックス | Timeインデックス | メモリ位置 |
|------------------|------------|---------------|----------|
| 0                | 0          | 0             | _arraysList[0] |
| 1                | 1          | 0             | _arraysList[1] |
| 2                | 2          | 0             | _arraysList[2] |
| ...              | ...        | ...           | ...      |
| 9                | 9          | 0             | _arraysList[9] |
| 10               | 0          | 1             | _arraysList[10] |
| 11               | 1          | 1             | _arraysList[11] |
| ...              | ...        | ...           | ...      |
| 49               | 9          | 4             | _arraysList[49] |

**パターン**: Zインデックスが最初に循環（0→9）、その後Timeインデックスが増加。

**視覚化**:

```
Frame:  [Z0T0] [Z1T0] [Z2T0] ... [Z9T0] [Z0T1] [Z1T1] ... [Z9T4]
Index:    0      1      2    ...   9      10     11    ...  49
```

---

## Innermost実装の詳細

### なぜ「最初の軸が最速変化」なのか？

`DimensionStructure`の内部実装を見ると、この設計が明確になります。

#### ストライド配列の初期化（`RegisterAxes`メソッド）

```csharp
private void RegisterAxes(params Axis[] axes)
{
    _axisList.Clear();
    _strides = new int[axes.Length];
    
    int currentStride = 1;

    // 重要: axis[0]が最も頻繁に変わる(Innermost)軸
    for (int i = 0; i < axes.Length; i++)
    {
        var axis = axes[i];
        _axisList.Add(axis);
        
        _strides[i] = currentStride;  // ストライドをキャッシュ
        axis.IndexChanged += AxisIndex_Changed;
        
        currentStride *= axis.Count;  // 次の軸のストライドを累積計算
    }
    
    // 総フレーム数チェック
    if (currentStride != _md.FrameCount)
        throw new ArgumentException($"Total count mismatch.");
}
```

**ポイント**:
- `_strides[0] = 1` が**常に最初の軸**に割り当てられる
- `_strides[i]` は**前の軸までの累積積**
- これにより、axes[0]のインデックスが1増えるとframeIndexも1増える

#### フレームインデックス計算（`GetFrameIndexFrom`メソッド）

```csharp
public int GetFrameIndexFrom(int[] indeces)
{
    if (_axisList.Count == 0) return 0;
    if (indeces.Length != _axisList.Count)
        throw new ArgumentException("Invalid length of indeces!");

    int newIndex = 0;
    // 内積計算: index × stride の総和
    for (int i = 0; i < _axisList.Count; ++i)
    {
        newIndex += indeces[i] * _strides[i];
    }
    return newIndex;
}
```

**例**: Z=3, Channel=2, Time=4 の場合

| 軸 | インデックス | ストライド | 寄与 |
|----|------------|-----------|------|
| Z | indeces[0] | 1 | indeces[0] × 1 |
| C | indeces[1] | 3 | indeces[1] × 3 |
| T | indeces[2] | 6 | indeces[2] × 6 |

```
frameIndex = Z×1 + C×3 + T×6
例: (Z=2, C=1, T=3) → 2×1 + 1×3 + 3×6 = 23
```

Zインデックスは**係数1で直接加算**されるため、Zが1増えるとframeIndexも1増える → **最速変化**

#### 逆変換（`CopyAxisIndicesTo`メソッド）

```csharp
public void CopyAxisIndicesTo(Span<int> buffer, int frameIndex = -1)
{
    if (frameIndex == -1) frameIndex = _md.ActiveIndex;
    if (buffer.Length < this.AxisCount)
        throw new ArgumentException("Destination span is too short.");

    // 各軸のインデックスを逆算
    for (int i = 0; i < _axisList.Count; i++)
    {
        // (frameIndex / stride[i]) % count[i]
        buffer[i] = (frameIndex / _strides[i]) % _axisList[i].Count;
    }
}
```

**例**: frameIndex = 23 の逆変換

```
Z = (23 / 1) % 3 = 23 % 3 = 2
C = (23 / 3) % 2 = 7 % 2 = 1
T = (23 / 6) % 4 = 3 % 4 = 3
→ (Z=2, C=1, T=3)
```

---

## ストライド計算

### ストライドとは？

**ストライド**は、特定の軸で次のインデックスに移動するためにスキップするフレーム数です。

### 公式

軸`i`の場合:
```
stride[i] = 軸iより前のすべての軸のカウントの積
```

### 例: 3軸データ

```csharp
// Z=3, Channel=2, Time=4 (合計: 24フレーム)
data.DefineDimensions(
    Axis.Z(3, ...),        // stride[0] = 1
    Axis.Channel(2, ...),  // stride[1] = 3
    Axis.Time(4, ...)      // stride[2] = 6
);
```

**ストライド計算**:
- `stride[0] = 1` (Zが最速変化)
- `stride[1] = 3` (Channel変更には3フレームスキップ)
- `stride[2] = 6` (Time変更には6フレームスキップ)

### フレームインデックス ↔ 軸インデックス変換

#### **軸インデックス → フレームインデックス**

```csharp
// 与えられた値: Z=2, Channel=1, Time=3
frameIndex = Z * stride[0] + C * stride[1] + T * stride[2]
           = 2 * 1 + 1 * 3 + 3 * 6
           = 2 + 3 + 18
           = 23
```

**実装** (`GetFrameIndexFrom`):
```csharp
int frameIndex = 0;
for (int i = 0; i < axisCount; i++)
{
    frameIndex += axisIndices[i] * strides[i];
}
```

#### **フレームインデックス → 軸インデックス**

```csharp
// 与えられた値: frameIndex = 23
for (int i = 0; i < axisCount; i++)
{
    axisIndices[i] = (frameIndex / strides[i]) % axisCounts[i];
}
```

**結果**:
- `Z = (23 / 1) % 3 = 2`
- `C = (23 / 3) % 2 = 1`
- `T = (23 / 6) % 4 = 3`

---

## 他ライブラリとの比較

### 比較表

| ライブラリ | デフォルト順序 | 最速変化軸 | MxPlot互換性 |
|-----------|--------------|-----------|-------------|
| **MxPlot.Core** | First-fastest | axes[0] | ✅ (基準) |
| **MATLAB** | Column-major | 最初の次元 | ✅ 同じ |
| **NumPy (C順序)** | Row-major | 最後の次元 | ❌ 逆 |
| **NumPy (F順序)** | Column-major | 最初の次元 | ✅ 同じ |
| **OpenCV** | Row-major | 最後の次元 | ❌ 逆 |
| **ImageJ** | 設定可能 | 通常はChannel | △ 異なる |

### NumPy (Python) - Row-Major (C順序)

```python
import numpy as np
arr = np.zeros((4, 2, 3))  # shape = (T, C, Z)
# メモリ順: Z0C0T0, Z1C0T0, Z2C0T0, Z0C1T0, ..., Z2C1T3
# 最後の軸(Z)が最速変化
```

**MxPlotに合わせるには**: Fortran順序を使用
```python
arr = np.zeros((3, 2, 4), order='F')  # shape = (Z, C, T)
# これで最初の軸(Z)が最速変化
```

### MATLAB - Column-Major (Fortran)

```matlab
A = zeros(3, 2, 4);  % size = [Z, C, T]
% 最初の次元(Z)が最速変化
% メモリ順: Z0C0T0, Z1C0T0, Z2C0T0, Z0C1T0, ...
```

**✅ MxPlotと完全一致！**

### OpenCV (C++) - Row-Major

```cpp
cv::Mat volume(std::vector<int>{4, 2, 3}, CV_64F);  // dims = {T, C, Z}
// 最後の次元(Z)が最速変化
```

**❌ MxPlotと逆**

### ImageJ (Java) - ハイパースタック

```java
ImagePlus imp = IJ.openImage("path/to/hyperstack.tif");
// 典型的順序: XYCZT または XYZCT
// 通常はChannelが最速変化（設定可能）
```

**△ 異なる規約** - ImageJは可視化のためChannelを優先。

---

## FovAxis: 視野タイリング

### FovAxisとは？

`FovAxis`は、**空間的に配置された撮像タイル**（複数の視野）を表現するための特殊な軸です。基本的な`Axis`を拡張し、以下を持ちます:

1. **タイルレイアウト**: 空間配置（例: 4×3グリッド）
2. **グローバル原点**: 各タイルの実世界座標

### 使用例: マルチタイル顕微鏡

タイルスキャン顕微鏡では、大きなサンプルを複数の重複または隣接する視野で撮像します:

```
       Y軸↑
          |
+-------+-------+-------+-------+
| FOV 8 | FOV 9 |FOV 10 |FOV 11 |  ← 行2（上）
+-------+-------+-------+-------+
| FOV 4 | FOV 5 | FOV 6 | FOV 7 |  ← 行1（中）
+-------+-------+-------+-------+
| FOV 0 | FOV 1 | FOV 2 | FOV 3 |  ← 行0（下、原点）
+-------+-------+-------+-------+
          |
          └─→ X軸
```

**注**: Y軸は上方向が正（科学的慣例）。FOV 0は左下が原点。

### FovAxisの機能

#### 1. **タイルレイアウト**

```csharp
public struct TileLayout
{
    public int X { get; init; }  // X方向のタイル数
    public int Y { get; init; }  // Y方向のタイル数
    public int Z { get; init; }  // Z方向のタイル数（3Dタイリング用）
}
```

#### 2. **グローバル原点**

各FOVはグローバル座標（ワールド位置）を持ちます:

```csharp
public readonly struct GlobalPoint
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}
```

### 基本的な使い方

```csharp
// 4×3タイルグリッドを作成
var fovAxis = new FovAxis(tilesX: 4, tilesY: 3);

// またはカスタムグローバル原点を指定
var origins = new List<GlobalPoint>
{
    new(0, 0, 0),     // FOV 0
    new(95, 0, 0),    // FOV 1 (95µmオフセット)
    new(190, 0, 0),   // FOV 2
    // ... 合計12個
};
var fovAxis = new FovAxis(origins, tilesX: 4, tilesY: 3);
```

### MatrixDataとの統合

```csharp
// 6次元データ: X, Y, Z, Time, Channel, FOV
var data = new MatrixData<ushort>(
    Scale2D.Pixels(512, 512),
    [
        Axis.Z(10, 0, 50, "µm"),
        Axis.Time(20, 0, 30, "s"),
        Axis.Channel(3),
        new FovAxis(4, 3)  // 4×3タイリング
    ]
);
// 合計フレーム: 10 × 20 × 3 × 12 = 7,200
```

### FOVデータへのアクセス

```csharp
// 特定のFOVを取得
var fov = data.Dimensions["FOV"] as FovAxis;
fov.Index = 5;  // グリッド位置[1, 1]のFOVを選択

// ワールド座標を取得
var globalPos = fov[5];
Console.WriteLine($"FOV center: ({globalPos.X}, {globalPos.Y})");

// 単一FOVから全データを抽出
var fovData = data.SliceAt("FOV", 5);
```

### 高度な機能

#### 重複タイル

```csharp
// 重複領域を定義
var origins = new List<GlobalPoint>
{
    new(0, 0, 0),
    new(90, 0, 0),    // 10µm重複（タイル幅 = 100µm）
    new(180, 0, 0),
    // ...
};
```

#### 3Dタイリング（現在未サポート）

```csharp
// ⚠️ 警告: 3Dタイリングはまだ実装されていません
// 以下のコードは NotSupportedException を発生させます:

var fovAxis = new FovAxis(tilesX: 2, tilesY: 2, tilesZ: 3);
// ❌ 例外: "3D tiling (zNum > 1) is not currently supported.
//            Index and ZIndex synchronization is not implemented."
```

**理由**: 3次元タイルナビゲーションのための`Axis.Index`と`FovAxis.ZIndex`の適切な同期がまだ実装されていません。この機能は将来の開発のために予約されています。

**現在の状態**: 2Dタイリング（Z = 1）のみが完全にサポートされています。

---

## 実用例

### 例1: Zスタックタイムラプス

```csharp
// 共焦点顕微鏡: タイムラプス付きZスキャン
var zStack = new MatrixData<ushort>(512, 512, 200);
zStack.DefineDimensions(
    Axis.Z(20, 0, 100, "µm"),     // 20 Zスライス（高速）
    Axis.Time(10, 0, 60, "s")     // 10時間点（低速）
);

// Time=5でのZスタックにアクセス
for (int z = 0; z < 20; z++)
{
    int frameIndex = z + 5 * 20;  // z * stride[0] + t * stride[1]
    var slice = zStack.GetArray(frameIndex);
    // スライスを処理...
}
```

**メモリレイアウト**:
```
フレーム 0-19:   Z0T0, Z1T0, ..., Z19T0  (Time=0)
フレーム 20-39:  Z0T1, Z1T1, ..., Z19T1  (Time=1)
...
フレーム 180-199: Z0T9, Z1T9, ..., Z19T9  (Time=9)
```

### 例2: マルチチャンネルイメージング

```csharp
// 4チャンネル蛍光顕微鏡
var multiChannel = new MatrixData<ushort>(1024, 1024, 400);
multiChannel.DefineDimensions(
    Axis.Channel(4),              // DAPI, GFP, RFP, Cy5
    Axis.Z(10, 0, 50, "µm"),
    Axis.Time(10, 0, 120, "s")
);

// Z=5, Time=3での全チャンネルを取得
for (int c = 0; c < 4; c++)
{
    int frameIndex = c + 5 * 4 + 3 * 40;  // c*1 + z*4 + t*40
    var channel = multiChannel.GetArray(frameIndex);
}
```

### 例3: Zスタック付きFOVタイリング

```csharp
// タイリング付き大型組織イメージング
var tiledImage = new MatrixData<ushort>(512, 512, 240);
tiledImage.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),      // stride = 1
    Axis.Channel(2),               // stride = 10
    new FovAxis(3, 4)              // 3×4グリッド, stride = 20
);

// グリッド位置[1, 2]（FOVインデックス=7）のFOVを処理
var fov = tiledImage.Dimensions["FOV"] as FovAxis;
fov.TileLayout;  // {X=3, Y=4, Z=1}

// このFOVの全チャンネルでZスタックを抽出
for (int c = 0; c < 2; c++)
{
    for (int z = 0; z < 10; z++)
    {
        int frameIndex = z + c * 10 + 7 * 20;
        var slice = tiledImage.GetArray(frameIndex);
    }
}
```

---

## 軸定義の再構成と並べ替え

### DefineDimensionsによる再定義

`DefineDimensions`を複数回呼び出すことで、軸構造を**動的に再定義**できます。

```csharp
// 初期定義: Z, Time
var data = new MatrixData<double>(512, 512, 100);
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Time(10, 0, 30, "s")
);

// 後で再定義: Channel, Z, Time (軸追加/再配置)
// 注意: FrameCount (100) は変わらないため、新しい軸構造の積も100でなければならない
data.DefineDimensions(
    Axis.Channel(2),
    Axis.Z(5, 0, 25, "µm"),
    Axis.Time(10, 0, 30, "s")
);  // 2 × 5 × 10 = 100 ✅

// FrameCount不一致はエラー
// data.DefineDimensions(Axis.Z(20), Axis.Time(10));  // 20 × 10 = 200 ❌
```

**使用例**: データ取得後に軸の意味を変更したい場合
```csharp
// 取得時は単純なフレームシーケンス
var rawData = AcquireData();  // 240 frames

// 後で実際の軸構造を定義
rawData.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(4),
    Axis.Time(6, 0, 30, "s")
);  // 10 × 4 × 6 = 240
```

### Reorderによるフレーム並べ替え

`Reorder`拡張メソッドを使用して、フレームの**物理的な順序を変更**できます。

#### 基本使用法

```csharp
// 元の順序: 0, 1, 2, 3, 4
var original = new MatrixData<double>(100, 100, 5);

// カスタム順序に並べ替え
var reordered = original.Reorder(new[] { 4, 2, 0, 3, 1 });
// 新しい順序: 4, 2, 0, 3, 1
```

#### Shallow Copy vs Deep Copy

```csharp
// デフォルト: Shallow Copy（参照を共有）
var shallowReorder = data.Reorder(newOrder, deepCopy: false);
// - 元の配列への参照を再配置
// - メモリ効率的（コピーなし）
// - 元データを変更すると影響を受ける

// Deep Copy: 新しいMatrixDataを生成
var deepReorder = data.Reorder(newOrder, deepCopy: true);
// - 配列を完全コピー
// - 独立した新しいデータ
// - 元データの変更に影響されない
```

#### 実用例：時系列の逆転

```csharp
// タイムシリーズを時間逆転
var timeSeries = new MatrixData<ushort>(512, 512, 100);
timeSeries.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Time(10, 0, 60, "s")
);

// 時間軸を逆転（各Zスライスで時間を逆に）
var reversedIndices = new List<int>();
for (int t = 9; t >= 0; t--)  // 時間逆順
{
    for (int z = 0; z < 10; z++)  // Z順序は維持
    {
        reversedIndices.Add(z + t * 10);
    }
}
var reversed = timeSeries.Reorder(reversedIndices, deepCopy: true);
```

#### 実用例：メタデータによるソート

```csharp
// 取得時刻でフレームをソート
var unsorted = LoadFromMicroscope();  // 取得順序がバラバラ

// メタデータから実際の取得時刻を取得してソート
var sortedIndices = Enumerable.Range(0, unsorted.FrameCount)
    .OrderBy(i => double.Parse(unsorted.Metadata[$"AcquisitionTime_{i}"]))
    .ToList();

var sorted = unsorted.Reorder(sortedIndices, deepCopy: false);  // 軽量な並べ替え
```

### 軸名指定による並べ替え

`Reorder(string[], bool)`メソッドを使用すると、新しい軸順序を指定してメモリレイアウトを変更できます。

#### 基本的な使い方

```csharp
// 元: Z=10, Channel=3, Time=5 (Zが最速変化)
var data = new MatrixData<double>(512, 512, 150);
data.DefineDimensions(
    Axis.Z(10, 0, 50, "µm"),
    Axis.Channel(3),
    Axis.Time(5, 0, 10, "s")
);

// Channelを最速変化にするために並べ替え
var reordered = data.Reorder(new[] { "Channel", "Z", "Time" });

// 結果: Channel=3, Z=10, Time=5 (Channelが最速変化)
// Frame 0: (C=0, Z=0, T=0), Frame 1: (C=1, Z=0, T=0), Frame 2: (C=2, Z=0, T=0), ...
```

#### 使用例

**1. 外部ツール用のデータ準備**

```csharp
// 元のMxPlotフォーマット: Z, Channel, Time
var mxData = LoadFromMicroscope();

// ImageJの期待形式: Channel, Z, Time
var forImageJ = mxData.Reorder(new[] { "Channel", "Z", "Time" });
ExportToImageJ(forImageJ);

// NumPy (C-order)の期待形式: Time, Channel, Z (最後が最速変化)
var forNumPy = mxData.Reorder(new[] { "Time", "Channel", "Z" });
ExportToNumPy(forNumPy);
```

**2. 処理の最適化**

```csharp
// 元: Time, Z, Channel
var timeSeries = LoadTimelapseData();

// ボリューム処理のためにZを最速変化に
var optimized = timeSeries.Reorder(new[] { "Z", "Time", "Channel" });

// これでZスライスがメモリ上で連続となり、効率的なMIP処理が可能
var mip = optimized.AsVolume("Z").CreateProjection(ViewFrom.Z);
```

**3. UI/ビューアの要件**

```csharp
// ビューアはChannelが最速変化を期待（RGBインターリーブ用）
var forDisplay = rawData.Reorder(new[] { "Channel", "X", "Y" });
```

### 再定義 vs 並べ替えの使い分け

| 操作 | DefineDimensions | Reorder (インデックス) | Reorder (軸名) |
|------|------------------|---------------------|---------------|
| **目的** | 軸構造の解釈変更 | フレーム順序の変更 | メモリレイアウトの変更 |
| **FrameCount** | 変更不可 | 変更不可 | 変更不可 |
| **物理順序** | 変わらない | 変わる | 変わる |
| **軸構造** | 変わる | 削除される | 変わる（同じ軸） |
| **使用例** | 軸の意味再定義 | ソート、逆転、抽出 | 相互運用性 |
| **コスト** | 低（メタデータのみ） | 中〜高 | 中〜高 |

**組み合わせ例**:
```csharp
// 1. フレーム順序を並べ替え
var reordered = rawData.Reorder(correctOrder, deepCopy: true);

// 2. 新しい軸構造を定義
reordered.DefineDimensions(
    Axis.Z(10, ...),
    Axis.Channel(3, ...),
    Axis.Time(8, ...)
);
```

**軸並べ替えの例**:
```csharp
// 互換性のためにメモリレイアウトを変更
var reordered = rawData.Reorder(new[] { "Channel", "Z", "Time" });
// 軸構造は保持されるが、メモリ順序が変わる
```

---

## ベストプラクティス

### 1. 軸順序をドキュメント化

```csharp
// 軸順序を説明するメタデータを追加
data.Metadata["AxisOrder"] = "Z, Channel, Time";
data.Metadata["AxisDescription"] = "データ取得時の実際の順序を記録";
```

**重要**: 軸順序はデータ取得方法に依存します。最初の軸（axes[0]）が最速変化（stride=1）になりますが、最適な順序はアプリケーション固有です。
- **カメラRGB**: `Channel, X, Y` (RGBが最速変化)
- **Zスタックスキャン**: `Z, Channel, Time` (Z深度が最速変化)
- **タイムラプス優先**: `Time, Z, Channel` (時間が最速変化)

### 2. タイリングにはFovAxisを使用

```csharp
// ✅ 空間タイルには汎用Axisではなく、FovAxisを使用
var fovAxis = new FovAxis(4, 3);  // 不可: new Axis(12, ...)
```

### 3. ターゲットプラットフォームに合わせる

**MATLABユーザー向け**:
```csharp
// MATLAB規約と一致（column-major）
data.DefineDimensions(Axis.Z(10), Axis.Channel(3), Axis.Time(5));
```

**NumPyユーザー向け**:
```csharp
// ⚠️ 軸順序を慎重に考慮
// NumPy: shape = (T, C, Z) → 最後が最速変化
// MxPlot等価（最初が最速変化）:
data.DefineDimensions(Axis.Z(10), Axis.Channel(3), Axis.Time(5));
```

---

## 将来の拡張

### 1. 不規則タイリング

**現在の制限**: FovAxisは規則的なグリッド間隔を前提

**提案拡張**: 任意のタイル位置をサポート
```csharp
// 将来のAPI
var irregularFov = new FovAxis(
    origins: customPositions,
    topology: TileTopology.Irregular
);
```

### 2. 3Dボリュームタイリング

**現在**: 2Dタイリング（X, Yグリッド）のみ

**制限**: 3Dタイリングには`Index`（グローバルフレーム位置）と`ZIndex`（現在のZ平面）の適切な同期が必要ですが、まだ実装されていません。

**提案**: クリアード組織イメージング用完全3Dタイリング
```csharp
// 将来: Index/ZIndex同期を持つ真の3Dタイリング
var volumeTiles = new FovAxis(
    tilesX: 3, tilesY: 3, tilesZ: 5,
    overlap: new Vector3(10, 10, 5)  // µm重複
);

// ナビゲーションは以下のように動作:
volumeTiles.Index = 42;  // 自動的にZIndexを更新
Console.WriteLine($"Z平面: {volumeTiles.ZIndex}");  // 自動計算
```

**実装計画**:
- `ZIndex`を計算プロパティ化: `ZIndex => Index / (X * Y)`
- `XIndex`と`YIndex`の計算プロパティを追加
- 既存の2Dコードとの後方互換性を確保

### 3. スティッチングメタデータ

**提案**: スティッチング情報の埋め込み
```csharp
public class FovAxis : Axis
{
    public StitchingParameters Stitching { get; set; }
}

public record StitchingParameters
{
    public double OverlapX { get; init; }
    public double OverlapY { get; init; }
    public BlendMode BlendMode { get; init; }
}
```

### 4. スパース軸サポート

**現在**: 軸インデックスの全組み合わせが存在

**提案**: 欠損フレームのサポート（スパースデータ）
```csharp
// 将来: すべての(Z, T)組み合わせが取得されていない
var sparseData = new MatrixData<ushort>(512, 512);
sparseData.DefineSparseAxes(
    Axis.Z(10),
    Axis.Time(100),
    acquiredFrames: new[] { (0,0), (1,0), (0,5), (5,10) }
);
```

### 5. 適応軸解像度

**提案**: 軸に沿った可変間隔
```csharp
// 将来: 不均一なZ間隔
var adaptiveZ = new AdaptiveAxis(
    positions: new[] { 0, 1, 2, 5, 10, 20 },  // µm
    name: "Z"
);
```

---

## トラブルシューティング

### 問題1: 軸順序の混乱

**症状**: データがスクランブルまたは非連続に見える

**解決策**: 軸順序が期待通りか確認
```csharp
// ストライド値を確認
var dims = data.Dimensions;
for (int i = 0; i < dims.AxisCount; i++)
{
    Console.WriteLine($"{dims[i].Name}: stride = ?");  // 内部ストライド
}
```

### 問題2: NumPyインポート不一致

**症状**: NumPyエクスポート後にフレームが間違った順序

**解決策**: 変換時に軸順序を反転
```python
# NumPy (T, C, Z) → MxPlot (Z, C, T)
mxplot_axes = numpy_array.transpose(2, 1, 0)
```

### 問題3: FOVグローバル座標

**症状**: 不正な原点によりスティッチング失敗

**解決策**: グローバル原点がステージ座標と一致するか確認
```csharp
var fov = data.Dimensions["FOV"] as FovAxis;
for (int i = 0; i < fov.Count; i++)
{
    var pos = fov[i];
    Console.WriteLine($"FOV {i}: ({pos.X}, {pos.Y}, {pos.Z})");
}
```

---

## まとめ

### 重要ポイント

1. **最初の軸 = 最速変化**（stride = 1）
2. **MATLAB互換**（列優先規約）
3. **NumPyと逆**（C順序配列には転置が必要）
4. **FovAxis**は空間タイル組織を提供
5. **ストライドベースインデックス**により効率的な変換が可能

### 参照公式

```
frameIndex = Σ(axisIndex[i] × stride[i])

ここで stride[i] = ∏(axisCount[j] for j < i)
```

---

**関連ドキュメント**:
- [MatrixData操作ガイド](MatrixData_Operations_Guide_ja.md)
- [VolumeAccessorガイド](VolumeAccessor_Guide_ja.md)
- [パフォーマンスレポート](VolumeOperator_Performance_Report.md)

---

*最終更新: 2026-02-08*
