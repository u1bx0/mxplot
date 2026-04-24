# 空間フィルター操作ガイド

**MxPlot.Core.Processing — 包括リファレンス**

> 最終更新日: 2026-04-12

*注意: このドキュメントは大部分がAIによって生成されたものであり、正確性の確認が必要です。*

## 📚 目次

1. [概要](#概要)
2. [アーキテクチャ](#アーキテクチャ)
3. [組み込みカーネル](#組み込みカーネル)
   - [MedianKernel](#mediankernel)
   - [GaussianKernel](#gaussiankernel)
4. [基本的な使い方](#基本的な使い方)
   - [拡張メソッドによる直接呼び出し](#拡張メソッドによる直接呼び出し)
   - [Operationパイプライン（型消去）](#operationパイプライン型消去)
   - [シングルフレーム・特定フレームのフィルタリング](#シングルフレーム特定フレームのフィルタリング)
5. [実践例](#実践例)
   - [ホットピクセル除去](#ホットピクセル除去)
   - [ガウシアンによるノイズ低減](#ガウシアンによるノイズ低減)
   - [マルチフレーム一括処理](#マルチフレーム一括処理)
   - [Reorder による特定軸フレームの抽出とフィルタリング](#reorder-による特定軸フレームの抽出とフィルタリング)
   - [進捗報告とキャンセル](#進捗報告とキャンセル)
   - [Parallel vs Sequential 性能比較](#parallel-vs-sequential-性能比較)
6. [カスタムカーネルによる拡張](#カスタムカーネルによる拡張)
   - [IFilterKernel インターフェース](#ifilterkernel-インターフェース)
   - [例: 平均（ボックス）フィルター](#例-平均ボックスフィルター)
   - [例: Min/Max（膨張/収縮）フィルター](#例-minmax膨張収縮フィルター)
   - [例: 重み付きカスタムカーネル](#例-重み付きカスタムカーネル)
7. [設計メモ](#設計メモ)
   - [3層アーキテクチャ](#3層アーキテクチャ)
   - [エッジ処理（Clamp）](#エッジ処理clamp)
   - [T↔double 変換](#tdouble-変換)
   - [CopyPropertiesFrom ヘルパー](#copypropertiesfrom-ヘルパー)
8. [ファイル構成](#ファイル構成)
9. [APIリファレンスまとめ](#apiリファレンスまとめ)

---

## 概要

空間フィルターフレームワークは、`MatrixData<T>` にピクセル単位の近傍フィルター（メディアン、ガウシアンなど）を適用する**カーネル注入モデル**を提供します。

- ✅ **任意の数値型 T** — `double`, `float`, `int`, `byte`, `ushort`, `Complex` 等
- ✅ **全フレーム一括処理** — 1回の呼び出しで全フレームをフィルタリング
- ✅ **フレームレベル並列処理** — `FrameCount ≥ 2` の場合、`Parallel.For` でフレーム並列
- ✅ **エッジクランプ** — 境界ピクセルは最近傍の端ピクセルを繰り返して処理
- ✅ **プラガブルカーネル** — `IFilterKernel` を実装するだけで新しいフィルターを追加可能
- ✅ **IOperation パイプライン** — 型消去された `IMatrixData.Apply()` で UI から利用可能
- ✅ **進捗報告・キャンセル** — `IProgress<int>` と `CancellationToken` 対応

---

## アーキテクチャ

```
UI層 (IMatrixData — T は不明)
  │
  │  data.Apply(new SpatialFilterOperation(kernel))
  ▼
SpatialFilterOperation : IMatrixDataOperation         ← 型消去ブリッジ
  │
  │  Execute<T>(MatrixData<T> src)                    ← ここで T が解決される
  ▼
FilterOperator.ApplyFilter<T>(source, kernel, ...)    ← ジェネリックアルゴリズム
  │
  │  kernel.Apply(Span<double>, count)
  ▼
IFilterKernel (MedianKernel / GaussianKernel / ...)   ← 戦略注入
```

| レイヤー | ファイル | 責務 |
|---|---|---|
| カーネル戦略 | `FilterKernel.cs` | `IFilterKernel` インターフェース + 組み込み実装 |
| ジェネリックアルゴリズム | `FilterOperator.cs` | `ApplyFilter<T>()` 拡張メソッド (`MatrixData<T>` 上) |
| Operation ブリッジ | `FilterOperations.cs` | `SpatialFilterOperation` record (`IMatrixDataOperation`) |

---

## 組み込みカーネル

### MedianKernel

近傍の値をソートし中央値を返します。ソルト＆ペッパーノイズやホット/デッドピクセルの除去に非常に効果的で、エッジを保存する特性があります。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `radius` | `int` | `1` | カーネル半径。`1` → 3×3、`2` → 5×5、`3` → 7×7 |

```csharp
new MedianKernel()           // 3×3（デフォルト）
new MedianKernel(radius: 2)  // 5×5
new MedianKernel(radius: 3)  // 7×7
```

**カーネルサイズ**: 近傍あたり `(2 × radius + 1)²` ピクセル。

### GaussianKernel

ガウシアン加重平均を適用して画像を平滑化します。単純なボックスフィルターと比較して、エッジのぼけを最小限に抑えながら高周波ノイズを低減します。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `radius` | `int` | `1` | カーネル半径 |
| `sigma` | `double` | `radius / 2.0` | ガウス分布の標準偏差。大きいほど平滑化が強い |

```csharp
new GaussianKernel()                        // 3×3, σ=0.5
new GaussianKernel(radius: 2, sigma: 1.0)  // 5×5, σ=1.0
new GaussianKernel(radius: 3, sigma: 1.5)  // 7×7, σ=1.5
```

**注意**: 画像の端で完全なカーネルが収まらない場合、ガウシアンカーネルは均一平均にフォールバックします。

---

## 基本的な使い方

### 拡張メソッドによる直接呼び出し

コンパイル時に `T` が既知の場合、拡張メソッドを直接使用します:

```csharp
using MxPlot.Core.Processing;

// メディアン 3×3
MatrixData<double> result = source.ApplyFilter(new MedianKernel());

// ガウシアン 5×5
MatrixData<ushort> result2 = source.ApplyFilter(new GaussianKernel(radius: 2, sigma: 1.0));
```

### Operationパイプライン（型消去）

`IMatrixData`（T 不明、例: UI コード）で使用する場合:

```csharp
using MxPlot.Core.Processing;

IMatrixData data = ...;  // T は不明

// メディアンフィルター（Apply パイプライン経由）
IMatrixData result = data.Apply(new SpatialFilterOperation(new MedianKernel()));

// ガウシアンフィルター（進捗報告付き）
IMatrixData result2 = data.Apply(new SpatialFilterOperation(
    new GaussianKernel(radius: 2, sigma: 1.5),
    Progress: progress,
    CancellationToken: cts.Token));
```

### シングルフレーム・特定フレームのフィルタリング

**重要**: `ApplyFilter` は常に**全フレーム**を処理します。シングルフレームや特定のフレームだけにフィルターを適用したい場合は、事前に `SliceAt` でフレームを抽出してからフィルターを適用します。

```csharp
// マルチフレームデータ（例: 50フレームのタイムラプス）
var timelapse = new MatrixData<double>(256, 256, 50);

// ✅ 特定フレーム（フレーム10）だけをフィルタリング
var frame10 = timelapse.SliceAt(10);
var filtered = frame10.ApplyFilter(new MedianKernel(radius: 1));
// filtered.FrameCount == 1

// ✅ シングルフレームデータにはそのまま ApplyFilter
var singleFrame = new MatrixData<double>(512, 512);
var result = singleFrame.ApplyFilter(new GaussianKernel(radius: 1));
```

`SliceAt` は軽量な操作（データのシャローコピー）であるため、「抽出→フィルター」のパターンはオーバーヘッドが極めて小さく、推奨されるアプローチです。

---

## 実践例

### ホットピクセル除去

科学画像処理における一般的なユースケース — センサーノイズによる孤立した輝点/暗点の除去:

```csharp
// 512×512 画像の (50, 50) にホットピクセル
var data = new MatrixData<double>(512, 512);
data.GetArray()[50 * 512 + 50] = 65535.0;  // ホットピクセル

// メディアン 3×3 で孤立した外れ値を除去（エッジは保存）
var cleaned = data.ApplyFilter(new MedianKernel(radius: 1));
// ホットピクセルは消え、周辺ピクセルは不変
```

### ガウシアンによるノイズ低減

大規模な特徴を保持しながらノイジーなデータを平滑化:

```csharp
var noisy = LoadNoisyData();

// 軽い平滑化: 3×3, σ=1.0
var smooth = noisy.ApplyFilter(new GaussianKernel(radius: 1, sigma: 1.0));

// 強い平滑化: 5×5, σ=2.0
var verySmooth = noisy.ApplyFilter(new GaussianKernel(radius: 2, sigma: 2.0));
```

### マルチフレーム一括処理

全フレームが自動的に処理されます。`FrameCount ≥ 2` の場合、フレームレベルの並列処理がデフォルトで有効になります:

```csharp
// 512×512 × 100 フレーム（例: タイムラプス）
var timelapse = new MatrixData<float>(512, 512, 100);
// ... データの充填 ...

// 全100フレームが並列でフィルタリング
var filtered = timelapse.ApplyFilter(new MedianKernel(radius: 1));
// filtered.FrameCount == 100
```

### Reorder による特定軸フレームの抽出とフィルタリング

多次元データ（例: Z × Time）から特定の軸に沿ってフレームを抽出し、フィルタリングするパターンです。`Reorder` + `SelectBy` を使うことで、次元構造を意識した柔軟なフレーム選択が可能になります:

```csharp
// 256×256, Z=10, Time=5 の4Dデータ
var data = new MatrixData<double>(256, 256, 50);
data.DefineDimensions(
    new Axis(10, 0, 100, "Z", "µm"),
    new Axis(5, 0, 10, "T", "s")
);

// 特定の時刻 (T=2) の全Zスライスを抽出
var atTime2 = data.SelectBy("T", 2);
// atTime2: 256×256, 10 frames (Z slices only)

// 全Zスライスにメディアンフィルターを適用
var filtered = atTime2.ApplyFilter(new MedianKernel(radius: 1));
// filtered: 256×256, 10 frames — フレーム並列で処理

// 特定のZスライス1枚だけにフィルターを適用する場合
var z5 = data.SelectBy("T", 2).SliceAt(5);  // T=2, Z=5
var filteredSlice = z5.ApplyFilter(new GaussianKernel(radius: 2, sigma: 1.0));
```

**ポイント**: `SelectBy` で次元を絞り込み → `SliceAt` で1フレーム抽出 → `ApplyFilter` という流れで、多次元データの任意の断面にフィルターを適用できます。

### 進捗報告とキャンセル

```csharp
var cts = new CancellationTokenSource();
var progress = new Progress<int>(value =>
{
    if (value < 0)
        Console.WriteLine($"総フレーム数: {-value}");
    else
        Console.WriteLine($"フレーム {value} 完了");
});

try
{
    var result = data.ApplyFilter(
        new MedianKernel(radius: 1),
        progress: progress,
        cancellationToken: cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("フィルターがキャンセルされました。");
}
```

**進捗プロトコル**:
1. 最初の報告: 負の値（`-FrameCount`）— 総フレーム数のヒント
2. 以降の報告: `0, 1, 2, ...` — 各フレームの完了時

### Parallel vs Sequential 性能比較

```csharp
// 逐次処理を強制（デバッグやベンチマークに有用）
var seqResult = data.ApplyFilter(new MedianKernel(), useParallel: false);

// デフォルト: FrameCount ≥ 2 で並列処理
var parResult = data.ApplyFilter(new MedianKernel(), useParallel: true);
```

---

## カスタムカーネルによる拡張

### IFilterKernel インターフェース

```csharp
public interface IFilterKernel
{
    /// <summary>カーネル半径。Radius=1 → 3×3、Radius=2 → 5×5。</summary>
    int Radius { get; }

    /// <summary>
    /// 近傍から出力値を計算する。
    /// バッファを自由に変更可能（例: ソート用）。
    /// </summary>
    /// <param name="values">スクラッチバッファ。先頭 <paramref name="count"/> 要素のみ有効。</param>
    /// <param name="count">有効な要素数（エッジでは少なくなる場合あり）。</param>
    double Apply(Span<double> values, int count);
}
```

**ポイント**:
- `Radius` で各方向の近傍ピクセル収集範囲を決定
- `Apply` は近傍を `Span<double>` で受け取る — ソート、変更、読み取りは自由
- `count` はエッジ部分で `(2R+1)²` より小さくなる場合がある（クランプ境界）

### 例: 平均（ボックス）フィルター

```csharp
public sealed class MeanKernel : IFilterKernel
{
    public int Radius { get; }

    public MeanKernel(int radius = 1) => Radius = radius;

    public double Apply(Span<double> values, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++)
            sum += values[i];
        return sum / count;
    }
}

// 使用例
var smoothed = data.ApplyFilter(new MeanKernel(radius: 2));
```

### 例: Min/Max（膨張/収縮）フィルター

```csharp
public sealed class MinKernel : IFilterKernel
{
    public int Radius { get; }
    public MinKernel(int radius = 1) => Radius = radius;

    public double Apply(Span<double> values, int count)
    {
        double min = values[0];
        for (int i = 1; i < count; i++)
            if (values[i] < min) min = values[i];
        return min;
    }
}

public sealed class MaxKernel : IFilterKernel
{
    public int Radius { get; }
    public MaxKernel(int radius = 1) => Radius = radius;

    public double Apply(Span<double> values, int count)
    {
        double max = values[0];
        for (int i = 1; i < count; i++)
            if (values[i] > max) max = values[i];
        return max;
    }
}
```

### 例: 重み付きカスタムカーネル

任意のユーザー定義重み（例: ラプラシアン、ソーベル風）を持つカーネル:

```csharp
public sealed class WeightedKernel : IFilterKernel
{
    public int Radius { get; }
    private readonly double[] _weights;

    public WeightedKernel(int radius, double[] weights)
    {
        Radius = radius;
        int expected = (2 * radius + 1) * (2 * radius + 1);
        if (weights.Length != expected)
            throw new ArgumentException($"期待される重み数は {expected} ですが、{weights.Length} が指定されました。");
        _weights = weights;
    }

    public double Apply(Span<double> values, int count)
    {
        int fullSize = _weights.Length;
        if (count == fullSize)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
                sum += values[i] * _weights[i];
            return sum;
        }
        // エッジのフォールバック: 単純平均
        double avg = 0;
        for (int i = 0; i < count; i++) avg += values[i];
        return avg / count;
    }
}
```

---

## 設計メモ

### 3層アーキテクチャ

| レイヤー | 役割 | 存在理由 |
|---|---|---|
| `IFilterKernel` | 純粋な計算戦略 | Open-Closed: 新フィルター = 新クラス1つ、他の変更不要 |
| `FilterOperator` | ジェネリック `<T>` アルゴリズム | `Span<T>`, `AsSpan()`, `T↔double` 変換に `T` が必要 |
| `SpatialFilterOperation` | `IMatrixDataOperation` ブリッジ | UI層は `IMatrixData`（T なし）で動作; Visitor パターンで T を解決 |

いずれのレイヤーも冗長ではなく、1つでも省くと型消去パイプラインが壊れるか、カーネル拡張性が失われます。

### エッジ処理（Clamp）

画像の境界では、近傍が有効なピクセル範囲にクランプされます:

```
ピクセル (0, 0)、radius=1 の場合:
  近傍 X 範囲: max(0, 0-1)..min(W-1, 0+1) = 0..1
  近傍 Y 範囲: max(0, 0-1)..min(H-1, 0+1) = 0..1
  → 3×3 = 9 の代わりに 2×2 = 4 値
```

カーネルの `count` パラメータは、実際に収集された有効な近傍数を反映します。

### T↔double 変換

すべてのカーネル計算は `double` 空間で実行されます:
1. `T → double`: `MatrixData<T>.ToDoubleConverter`（型ごとの静的デリゲート）
2. カーネルが `double` で計算
3. `double → T`: `MatrixData<T>.FromDoubleConverter`

これにより、サポートされるすべての型（`byte`, `ushort`, `int`, `float`, `double`, `Complex` 等）を統一的に処理できます。

### CopyPropertiesFrom ヘルパー

結果マトリックスは共通ヘルパーでソースのプロパティを継承します:

```csharp
result.CopyPropertiesFrom(source);
// コピーされるもの: XYスケール, XUnit, YUnit, Metadata, DimensionStructure
```

このユーティリティは `DimensionalOperator`, `MatrixArithmetic` などの他のオペレーターと共有され、コード重複を防止しています。

---

## ファイル構成

```
MxPlot.Core/Processing/
├── FilterKernel.cs         ← IFilterKernel + MedianKernel + GaussianKernel
├── FilterOperator.cs       ← FilterOperator.ApplyFilter<T>() 拡張メソッド
└── FilterOperations.cs     ← SpatialFilterOperation record (IMatrixDataOperation)
```

---

## APIリファレンスまとめ

### FilterOperator（拡張メソッド）

```csharp
public static MatrixData<T> ApplyFilter<T>(
    this MatrixData<T> source,
    IFilterKernel kernel,
    bool useParallel = true,
    IProgress<int>? progress = null,
    CancellationToken cancellationToken = default) where T : unmanaged
```

### SpatialFilterOperation（Record）

```csharp
public record SpatialFilterOperation(
    IFilterKernel Kernel,
    IProgress<int>? Progress = null,
    CancellationToken CancellationToken = default) : IMatrixDataOperation
```

### IFilterKernel（インターフェース）

```csharp
public interface IFilterKernel
{
    int Radius { get; }
    double Apply(Span<double> values, int count);
}
```

### 組み込みカーネル

| カーネル | パラメータ | 説明 |
|---|---|---|
| `MedianKernel(int radius = 1)` | `radius` | 近傍をソート → 中央値を返す |
| `GaussianKernel(int radius = 1, double sigma = 0)` | `radius`, `sigma` | ガウシアン加重平均（σ のデフォルトは `radius / 2.0`） |
