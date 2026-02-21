# VolumeAccessor<T> ガイド

**MxPlot.Core 3Dボリューム操作リファレンス**

> 最終更新: 2026-02-08  
> バージョン: 0.0.2

## 📚 目次

1. [はじめに](#はじめに)
2. [基本操作](#基本操作)
3. [投影とリダクション](#投影とリダクション)
4. [パイプライン例](#パイプライン例)
5. [パフォーマンス](#パフォーマンス)
6. [制限事項](#制限事項)
7. [メソッドリファレンス](#メソッドリファレンス)

---

## はじめに

`VolumeAccessor<T>`は、`MatrixData<T>`に格納された3Dボリュームデータへの高性能な読み取り専用アクセスを提供します。

### 主な特徴

- ✅ **ゼロコピーアクセス**: `vol[x, y, z]`による直接3Dアクセス
- ✅ **直交ビュー**: 異なる視点からのボリューム再配置
- ✅ **高速投影**: MIP/MinIP/AIP投影（並列処理）
- ✅ **カスタムリダクション**: ユーザー定義関数による軸縮約
- ✅ **メモリ効率**: ArrayPoolとSpanベース設計

### VolumeAccessorの作成

```csharp
// 3Dデータの作成: 512×512、100フレーム
var data = new MatrixData<float>(512, 512, 100);
data.DefineDimensions(Axis.Z(100, 0, 50, "µm"));

// データ充填
for (int z = 0; z < 100; z++)
    data.Set(z, (ix, iy, x, y) => (float)Math.Sin(x * 0.1) * Math.Cos(y * 0.1) * (z + 1));

// VolumeAccessorの取得
var volume = data.AsVolume();

// ボクセルアクセス
float value = volume[256, 256, 50];  // [x, y, z]
```

---

## 基本操作

### 1. Restack - XZ/YZデータの生成

異なる方向から見るようにボリュームを再配置します。

```csharp
// X方向から見る: YZ平面を積層
var viewFromX = volume.Restack(ViewFrom.X);
// 結果: Width=Y, Height=Z, Frames=X

// Y方向から見る: XZ平面を積層
var viewFromY = volume.Restack(ViewFrom.Y);
// 結果: Width=X, Height=Z, Frames=Y
```

**使用例**: 多方向からの3Dデータ検査、XZ/YZ平面データの生成

### 2. SliceAt - 2D断面抽出

特定のインデックスで2D平面を抽出します。

```csharp
// Y=128でXZ平面を抽出
var xzSlice = volume.SliceAt(ViewFrom.Y, 128);

// X=256でYZ平面を抽出
var yzSlice = volume.SliceAt(ViewFrom.X, 256);
```

**使用例**: 特定断面の検査、代表スライスのエクスポート

---

## 投影とリダクション

### CreateProjection - 組み込み投影

最適化された高速投影を提供します。結果は元データへの**参照コピー（ゼロコピー）**です。

```csharp
using MxPlot.Core.Processing;

// 最大強度投影（MIP）
var mip = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);

// 最小強度投影（MinIP）
var minip = volume.CreateProjection(ViewFrom.Y, ProjectionMode.Minimum);

// 平均強度投影（AIP）
var aip = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Average);
```

**投影モード**:
- `Maximum`: 最大値検出（MIP）
- `Minimum`: 最小値検出（MinIP）
- `Average`: 平均値計算（AIP）

**要件**: 型`T`は`INumber<T>`と`IMinMaxValue<T>`を実装した型に限定されます。

### ReduceAlong - カスタムリダクション

ユーザー定義関数による軸縮約を行います。

```csharp
// 標準偏差マップ
var stdDevMap = volume.ReduceAlong(ViewFrom.X,
    (ix, iy, x, y, axis, values) =>
    {
        double mean = 0;
        foreach (var v in values)
            mean += v;
        mean /= values.Length;
        
        double variance = 0;
        foreach (var v in values)
        {
            double diff = v - mean;
            variance += diff * diff;
        }
        return (float)Math.Sqrt(variance / values.Length);
    });

// 中央値フィルタ
var medianProj = volume.ReduceAlong(ViewFrom.X,
    (ix, iy, x, y, axis, values) =>
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    });
```

**デリゲートシグネチャ**:
```csharp
public delegate T ReduceFunc(
    int ix,                     // グリッドインデックスX
    int iy,                     // グリッドインデックスY
    double x,                   // 空間座標X
    double y,                   // 空間座標Y
    Axis axis,                  // 縮約軸（スケール情報含む）
    ReadOnlySpan<T> values      // 軸に沿った値
);
```

---

## パイプライン例

### 例1: マルチチャンネルデータ処理

```csharp
// 元データ: 512×512、Time（100）×Channel（3）
var multiDim = new MatrixData<float>(512, 512, 300);
multiDim.DefineDimensions(
    Axis.Time(100, 0, 10, "s"),
    Axis.Channel(3)
);

// チャンネル0を抽出してMIP作成
var channel0 = multiDim.ExtractAlong("Channel", 0);
var mip = channel0.AsVolume().CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);
```

### 例2: 直交ビュー（インタラクティブスライサー）

```csharp
// 特定位置での直交断面を抽出
var volume = data.AsVolume();  // 256×256×64のボリューム

// クラスで直交ビューを管理
public class OrthogonalViews
{
    public MatrixData<float> TopView { get; set; }    // XY平面
    public MatrixData<float> SideView { get; set; }   // XZ平面
    public MatrixData<float> FrontView { get; set; }  // YZ平面
}

// 指定位置での断面を取得
OrthogonalViews GetOrthogonalViews(VolumeAccessor<float> volume, int x, int y, int z)
{
    return new OrthogonalViews
    {
        TopView = volume.SliceAt(ViewFrom.Z, z),   // Z=zでのXY平面
        SideView = volume.SliceAt(ViewFrom.Y, y),  // Y=yでのXZ平面
        FrontView = volume.SliceAt(ViewFrom.X, x)  // X=xでのYZ平面
    };
}

// 使用例: マウス位置に応じて動的に更新
void OnMouseMove(int mouseX, int mouseY, int mouseZ)
{
    var views = GetOrthogonalViews(volume, mouseX, mouseY, mouseZ);
    
    // 各ビューを表示
    DisplayImage(views.TopView, "Top View (XY)");
    DisplayImage(views.SideView, "Side View (XZ)");
    DisplayImage(views.FrontView, "Front View (YZ)");
}

// ImageJ風の十字カーソル付きビューア
void UpdateCrosshairViews(int cursorX, int cursorY, int cursorZ)
{
    var views = GetOrthogonalViews(volume, cursorX, cursorY, cursorZ);
    
    // 各ビューにカーソル位置を重ねて表示
    DrawWithCrosshair(views.TopView, cursorX, cursorY);
    DrawWithCrosshair(views.SideView, cursorX, cursorZ);
    DrawWithCrosshair(views.FrontView, cursorY, cursorZ);
}
```

### 例3: 時系列解析

```csharp
// 時間経過における標準偏差を計算
var temporalData = multiDim.ExtractAlong("Channel", 0);
var volume = temporalData.AsVolume();

var stdDevMap = volume.ReduceAlong(ViewFrom.Z, 
    (ix, iy, x, y, timeAxis, timeValues) =>
    {
        double mean = timeValues.ToArray().Average(v => (double)v);
        double variance = timeValues.ToArray().Average(v => 
        {
            double diff = (double)v - mean;
            return diff * diff;
        });
        return (float)Math.Sqrt(variance);
    });
```

---

## パフォーマンス

### ベンチマーク結果

**測定環境**: Intel Core i9-14900KF (24コア、32スレッド)、DDR5-5600 64GB、Windows 11

#### 256×256×64 Ushort (16bit整数)

| 投影方向 | MIP | MinIP | AIP |
|----------|-----|-------|-----|
| **XY (Z投影)** | 2.02 ms | 1.77 ms | 1.60 ms |
| **XZ (Y投影)** | 0.51 ms | 0.54 ms | 0.48 ms |
| **YZ (X投影)** | 0.42 ms | 0.43 ms | 0.48 ms |

#### 256×256×64 Float (32bit浮動小数点)

| 投影方向 | MIP | MinIP | AIP |
|----------|-----|-------|-----|
| **XY (Z投影)** | 1.24 ms | 1.14 ms | 1.11 ms |
| **XZ (Y投影)** | 0.71 ms | 0.68 ms | 0.65 ms |
| **YZ (X投影)** | 0.46 ms | 0.45 ms | 0.63 ms |

**リアルタイム表示性能**: 3方向（Z, X, Y）のMIP同時表示でも**100+ FPS**が期待（ushort）

### 最適化のポイント

1. **組み込み投影を優先**: `CreateProjection`はゼロコピーで最適化済み
2. **投影方向の選択**: X/Y投影はZ投影より高速（データアクセスパターンに依存）
3. **VolumeAccessorを再利用**: 複数操作で同じインスタンスを使用
4. **大きなデータはチャンク処理**: `Restack`は完全コピーを作成

```csharp
// ✅ 推奨（ゼロコピー投影）
var mip = volume.CreateProjection(ViewFrom.Z, ProjectionMode.Maximum);

// ❌ 非推奨（遅い、ラムダオーバーヘッド）
var mip = volume.ReduceAlong(ViewFrom.Z, (ix, iy, x, y, z, vals) => 
    vals.ToArray().Max());
```

---

## 制限事項

### 軸の指定

`AsVolume()`は単一軸でボリュームを構成しますが、複数軸がある場合は軸名を指定する必要があります。

```csharp
// ✅ 有効: 単一軸（自動認識）
var data = new MatrixData<float>(512, 512, 100);
data.DefineDimensions(Axis.Z(100, 0, 50, "µm"));
var vol = data.AsVolume();  // OK - 軸名指定不要

// ✅ 有効: 複数軸（軸名を指定）
var multiAxis = new MatrixData<float>(512, 512, 300);
multiAxis.DefineDimensions(
    Axis.Z(100, 0, 50, "µm"),
    Axis.Time(3, 0, 5, "s")
);
var volZ = multiAxis.AsVolume("Z");     // OK - Z軸に沿ったボリューム
var volTime = multiAxis.AsVolume("Time"); // OK - Time軸に沿ったボリューム

// ✅ 有効: 複数軸 + 基準インデックス指定
var volAtTime1 = multiAxis.AsVolume("Z", baseIndices: new[] { 0, 1 });
// Time=1でのZ軸ボリューム

// ❌ 無効: 複数軸で軸名未指定
// var vol = multiAxis.AsVolume();  // InvalidOperationException
```

### メモリ使用量

- `Restack`は完全コピーを作成（元と同サイズのメモリが必要）
- 512×512×100の`float`: 約100MB × 2 = 200MB
- 大きなボリュームではサブ領域処理を検討

### スレッドセーフティ

- VolumeAccessorは読み取り専用でスレッドセーフ
- 使用中は元のMatrixDataを変更しない
- 複数のVolumeAccessorが同じデータに安全にアクセス可能

---

## メソッドリファレンス

### VolumeAccessor<T>

#### インデクサ
```csharp
public T this[int ix, int iy, int iz] { get; }
```
ゼロコピーでボクセルに直接アクセス。

#### 主要メソッド

```csharp
public MatrixData<T> Restack(ViewFrom direction)
```
異なる視点からボリュームを再配置。完全コピーを作成。

```csharp
public MatrixData<T> SliceAt(ViewFrom axis, int index)
```
指定インデックスで2D断面を抽出。

```csharp
public MatrixData<T> ReduceAlong(ViewFrom axis, ReduceFunc op)
```
カスタム関数による軸縮約。

### VolumeOperator拡張

```csharp
public static MatrixData<T> CreateProjection<T>(
    this VolumeAccessor<T> volume, 
    ViewFrom axis, 
    ProjectionMode mode)
    where T : unmanaged, INumber<T>, IMinMaxValue<T>
```

最適化された投影（MIP/MinIP/AIP）。

### ViewFrom列挙型

```csharp
public enum ViewFrom
{
    X,  // YZ平面に直交（X軸に沿って見る）
    Y,  // XZ平面に直交（Y軸に沿って見る）
    Z   // XY平面（通常のトップビュー）
}
```

### ProjectionMode列挙型

```csharp
public enum ProjectionMode
{
    Maximum,  // 最大強度投影（MIP）
    Minimum,  // 最小強度投影（MinIP）
    Average   // 平均強度投影（AIP）
}
```

---

## 関連項目

- **[MatrixData操作ガイド](MatrixData_Operations_Guide_ja.md)** - 基本的なデータ操作
- **[DimensionStructureガイド](DimensionStructure_MemoryLayout_Guide_ja.md)** - メモリレイアウトの詳細
- [APIリファレンス](../README.md) - 完全なAPIドキュメント

---

**ガイド終了**

*最終更新: 2026-02-08  (Generated by GitHub Copilot)*
