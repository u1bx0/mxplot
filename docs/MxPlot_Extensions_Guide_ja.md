# MxPlot 拡張開発ガイド

**バージョン対応日：** 2026-09-23
**対象読者：** MxPlot を外部 DLL で拡張したい開発者

---

## 1. はじめに

MxPlot は 3 種類の外部拡張ポイントを持っています。
外部 DLL を配置するだけで、アプリケーションを再コンパイルすることなく機能を追加できます。

| 拡張ポイント | 何を追加できるか | 必要な参照 |
|---|---|---|
| **IMatrixDataReader / IMatrixDataWriter** | 対応ファイルフォーマット | `MxPlot.Core` のみ |
| **IMatrixPlotterPlugin** | MatrixPlotter ウィンドウの Plugins タブにコマンド追加 | `MxPlot.UI.Avalonia` |
| **IMxPlotAppPlugin** | MxPlot.App（ダッシュボード）の Tools メニューにコマンド追加 | `MxPlot.App` |

---

## 2. 実装状況

現時点での実装状況を正直に記載します。

| 機能 | 実装状況 | 備考 |
|---|---|---|
| `FormatRegistry.ScanAndRegister()` | ✅ 動作 | 起動時に自動実行 |
| `MatrixPlotterPluginRegistry.LoadFromDirectory()` | ✅ 動作 | 起動時に自動実行 |
| `MxPlotAppPluginRegistry.LoadFromDirectory()` | ✅ 動作 | 起動時に自動実行 |
| `FormatRegistry` の起動時自動スキャン | ✅ 動作 | `MxPlot.Extensions.*.dll` を自動検出 |
| Plugin Registry の起動時自動スキャン | ✅ 動作 | `App.axaml.cs` から `plugins/` を自動スキャン |

---

## 3. ファイルフォーマット拡張

### 3.1 概要

最も移植性が高い拡張ポイントです。`MxPlot.Core` のみへの参照で実装でき、
ファイル名が `MxPlot.Extensions.{名前}.dll` であれば起動時に自動登録されます。

### 3.2 プロジェクト設定

```xml
<!-- MyPlugin.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- 出力 DLL 名を規約に従う: MxPlot.Extensions.{Name}.dll -->
    <AssemblyName>MxPlot.Extensions.Zarr</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- MxPlot.Core のみ参照すれば OK -->
    <PackageReference Include="MxPlot.Core" Version="x.x.x" />
    <!-- または直接参照 -->
    <!-- <ProjectReference Include="..\..\MxPlot.Core\MxPlot.Core.csproj"/> -->
  </ItemGroup>
</Project>
```

### 3.3 Reader の実装例

```csharp
using MxPlot.Core;
using MxPlot.Core.IO.Formats;
using System.Collections.Generic;

namespace MxPlot.Extensions.Zarr
{
    /// <summary>
    /// Zarr 形式のリーダー実装例。
    /// DLL 名が MxPlot.Extensions.Zarr.dll であれば起動時に自動登録される。
    /// </summary>
    public sealed class ZarrFormat : IMatrixDataReader, IMatrixDataWriter
    {
        // ── IFileFormatDescriptor ────────────────────────────────────────

        public string FormatName => "Zarr";
        public IReadOnlyList<string> Extensions { get; } = [".zarr"];

        // ── IMatrixDataReader ────────────────────────────────────────────

        // IMatrixDataReaderの必須メンバー。使わなくても実装は必須 — CancellationToken.None
        // （structの既定値）で問題ない。
        public CancellationToken CancellationToken { get; set; }

        // 型を指定して読む版。ファイルの実データ型と異なる場合は例外にする。
        public MatrixData<T> Read<T>(string filePath) where T : unmanaged
        {
            IMatrixData raw = Read(filePath);
            if (raw is MatrixData<T> typed) return typed;
            throw new InvalidOperationException(
                $"File contains '{raw.ValueTypeName}' data, but '{typeof(T).Name}' was requested.");
        }

        // 型を問わず読む版。呼び出し側がファイルの実データ型を知らない場合に使う。
        public IMatrixData Read(string path)
        {
            // 実際の読み込みロジックをここに実装
            var md = new MatrixData<float>(256, 256);
            // ...
            return md;
        }

        // ── IMatrixDataWriter ────────────────────────────────────────────

        // accessorは書き込み先（MMFなど）の裏側ストレージへのアクセスを提供する。結果全体をRAM上に
        // 組み立てずにストリーミングで書き込む実装向けで、使わない場合は無視してよい。
        public void Write<T>(string filePath, MatrixData<T> data, IBackendAccessor accessor) where T : unmanaged
        {
            // 実際の書き込みロジックをここに実装
        }
    }
}
```

#### オプション：進捗報告対応

重いファイル（大容量バイナリなど）は `IProgressReportable` を実装すると
MxPlot の UI がプログレスバーを自動表示します。

**符号規約（重要）：**

`IProgress<int>` の報告値には専用の符号規約があります。
インターフェイスに `TotalFrames` プロパティを持たせると
ヘッダー解析前に総数が確定しないフォーマットで困るため、
代わりに**最初の1回だけ負値で総数を通知する**方式を採用しています。

| 呼び出し | 値 | 意味 |
|---|---|---|
| ループ前に1回 | `-totalFrames`（負値） | 総フレーム数を宣言 |
| フレーム完了後 | `i`（0-based） | i+1 番目が完了 |
| 完了後（省略可） | `totalFrames` | UI は無視（finally で消去） |

```csharp
public sealed class ZarrFormat : IMatrixDataReader, IProgressReportable
{
    public IProgress<int>? ProgressReporter { get; set; }

    public IMatrixData Read(string path)
    {
        // ヘッダーを解析して総フレーム数を確定させてから通知
        int frameCount = ReadFrameCount(path);

        ProgressReporter?.Report(-frameCount);   // ① 負値で総数を宣言

        for (int i = 0; i < frameCount; i++)
        {
            // ... i 番目のフレームを読む ...
            ProgressReporter?.Report(i);         // ② 0-based インデックスを報告
        }
        // UI は finally ブロックでプログレスバーを消すため、完了報告は省略可
        return result;
    }
}
```

#### オプション：キャンセル対応

`CancellationToken` プロパティ自体は必須（上の基本サンプル参照）ですが、MxPlot の UI が Cancel
ボタンを出してトークンを実際に設定するのは、リーダー側が「本当にチェックしている」と宣言した場合
だけです。既定 `false` の `IsCancellable` を `true` にオーバーライドし、フレーム境界で
`ThrowIfCancellationRequested()` を呼びます。

```csharp
public sealed class ZarrFormat : IMatrixDataReader
{
    public CancellationToken CancellationToken { get; set; }
    public bool IsCancellable => true;

    public IMatrixData Read(string path)
    {
        for (int i = 0; i < frameCount; i++)
        {
            CancellationToken.ThrowIfCancellationRequested(); // ← フレーム境界でチェック
            // ... i 番目のフレームを読む ...
        }
        return result;
    }
}
```

`IsCancellable` を既定の `false` のままにする（プロパティは持つがチェックしない）のも問題ありません。
`CancellationToken.None` が struct 自体の既定値なので、未使用のコストはゼロです。

#### オプション：仮想読み込み対応

数 GB を超えるファイルはオンデマンド読み込みを検討してください。
`IVirtualLoadable` を実装すると MxPlot の「Virtual / InMemory 選択ダイアログ」に対応します。

```csharp
public sealed class ZarrFormat : IMatrixDataReader, IVirtualLoadable
{
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Auto;

    public IMatrixData Read(string path)
    {
        if (LoadingMode == LoadingMode.Virtual)
            return ReadVirtual(path);   // オンデマンドフレームリスト
        return ReadInMemory(path);
    }
}
```

### 3.4 仮想読み込み（Virtual Mode）の詳細実装

「Virtual Mode」とは、ファイル全体を RAM に読み込まず、
フレームを **オンデマンドで読み込む** モードです。
非圧縮ファイルではメモリマップドファイル（MMF）経由で読み込み、この節ではその実装を説明します。
数 GB を超えるファイルに有効です。

フレーム単位で独立に圧縮されている場合（圧縮マルチ IFD の TIFF など）は MMF を使えませんが、
`VirtualFrames<T>` を継承して `ReadFrame(index, ct)` で 1 フレームをデコードする実装にすれば、
遅延読み込みは可能です。参考実装は `MxPlot.Extensions.Tiff` の `TiffDecodedFrames<T>` です。

#### 前提：Virtual Mode に適したフォーマットの条件

| 条件 | 説明 |
|---|---|
| **ランダムアクセス可能** | 各フレームのバイトオフセットが事前にわかる（非圧縮バイナリ等） |
| **非圧縮** または **フレーム単位で独立して展開可能** | 圧縮ストリームをシーケンシャルにしか読めないフォーマットは不可 |
| **固定レイアウト** | フレーム i のオフセットをスキャンできる |

FITS や Raw Binary はこの条件を満たします。
フレームが JPEG/PNG 圧縮されている場合、MMF ルートは使えません（上記の `VirtualFrames<T>` 継承による遅延デコードか、InMemory を選びます）。

#### 実装パターン

Virtual 読み込みの実装は **2 ステップ** です。

**Step 1：ファイルをスキャンしてオフセットテーブルを構築**（ピクセルデータは読まない）

```csharp
// 戻り値: offsets[frameIndex][stripIndex], byteCounts[frameIndex][stripIndex]
// 非圧縮の場合は stripIndex は常に 0 のみ（1 フレーム = 1 ストリップ）
private static (long[][] offsets, long[][] byteCounts) ScanOffsets(
    string path, int frameCount, int width, int height, int bytesPerPixel)
{
    var offsets    = new long[frameCount][];
    var byteCounts = new long[frameCount][];
    long frameBytes = (long)width * height * bytesPerPixel;

    // フォーマット固有のヘッダーサイズを読み、フレームデータ開始位置を決定
    long dataStart = ReadHeaderSize(path); // フォーマット依存の実装

    for (int i = 0; i < frameCount; i++)
    {
        offsets[i]    = [dataStart + i * frameBytes];
        byteCounts[i] = [frameBytes];
    }
    return (offsets, byteCounts);
}
```

**Step 2：`StrippedMmfFrames<T>` を構築して `MatrixData<T>` に渡す**

```csharp
public IMatrixData ReadVirtual(string path)
{
    // ① ヘッダー情報だけ読む（軽い処理）
    var (width, height, frameCount, bytesPerPixel) = ReadHeaderInfo(path);

    // ② オフセットテーブルをスキャン（ピクセルは読まない）
    var (offsets, byteCounts) = ScanOffsets(path, frameCount, width, height, bytesPerPixel);

    // ③ StrippedMmfFrames を生成（MMF はここで開かれる）
    //    isYFlipped: ファイルが上の行から格納されているなら true（下記「Y 方向の向き」参照）
    //    isBigEndian: ファイルのバイトオーダー。ホストと異なる場合だけ自動でスワップされる
    var vf = new StrippedMmfFrames<float>(
        path, width, height, offsets, byteCounts, isYFlipped: false, isBigEndian: false);

    // ④ MatrixData に渡す（所有権は MatrixData に移転、Dispose は自動）
    var md = MatrixData<float>.CreateAsVirtualFrames(width, height, vf);
    md.SetXYScale(...);
    return md;
}
```

#### `IVirtualLoadable` の実装

```csharp
public sealed class MyFormat : IMatrixDataReader, IVirtualLoadable
{
    public LoadingMode LoadingMode { get; set; } = LoadingMode.Auto;

    public IMatrixData Read(string path)
    {
        var (width, height, frameCount) = ReadHeaderInfo(path);

        // Auto の場合、ファイルサイズとフレーム数で自動判定
        long fileBytes = new FileInfo(path).Length;
        var mode = VirtualPolicy.Resolve(LoadingMode, fileBytes, frameCount);

        return mode == LoadingMode.Virtual
            ? ReadVirtual(path, width, height, frameCount)
            : ReadInMemory(path, width, height, frameCount);
    }
}
```

`VirtualPolicy.Resolve` は以下のどちらかが満たされると Virtual を選びます：
- ファイルサイズが `VirtualPolicy.ThresholdBytes`（デフォルト 2 GB）を超える
- フレーム数が `VirtualPolicy.ThresholdFrames`（デフォルト 1000）を超える

どちらの閾値もアプリ側から実行時に変更できます。

#### マルチストリップとタイル：どちらを使うか

| レイアウト | クラス | 典型的なフォーマット |
|---|---|---|
| **ストリップ**（1 フレーム = 1〜N 行のまとまり） | `StrippedMmfFrames<T>` | FITS, Raw Binary, ストリップ TIFF |
| **タイル**（1 フレーム = M×N タイルのまとまり） | `TiledMmfFrames<T>` | タイル TIFF, 大型顕微鏡フォーマット |

**ストリップの場合**（`StrippedMmfFrames<T>`）：

```csharp
// offsets[frameIndex][stripIndex] — 行のまとまりごとのオフセット
// 非圧縮の場合は stripIndex = 0 のみ（1 フレーム 1 ストリップ）
var vf = new StrippedMmfFrames<float>(
    path, width, height, offsets, byteCounts, isYFlipped: false, isBigEndian: false);
```

**タイルの場合**（`TiledMmfFrames<T>`）：

```csharp
// offsets[frameIndex][tileIndex] — tileIndex は左→右、上→下の順
// tileWidth, tileHeight はフォーマット固有のタイルサイズ
var vf = new TiledMmfFrames<ushort>(
    path, imageWidth, imageHeight,
    tileWidth, tileHeight,
    offsets, byteCounts, isYFlipped: false, isBigEndian: false);

// 右端・下端のタイルはパディングを持つ場合があるが、
// TiledMmfFrames がクリッピングを自動処理する
```

タイルフォーマットでは `offsets[frameIndex]` の各要素が  
`tilesAcross × tilesDown` 個のタイルオフセットに対応します。

#### キャッシュの設定

デフォルトでは、キャッシュサイズは空きメモリとフレームサイズから自動で決まり
（`VirtualCachePolicy`、16〜8192 フレーム）、`NeighborStrategy` が近傍フレームを先読みします。
必要に応じて変更できます。

```csharp
vf.CacheCapacity = 32;                                                // キャッシュサイズを変更
vf.CacheStrategy = new NeighborStrategy(lookAhead: 4, lookBehind: 2); // 先読みを調整
```

#### Y 方向の向き

MatrixData は左下原点（0 行目が一番下の行）です。ファイルが上の行から格納されている場合は
`isYFlipped: true` にすると、読み出し時に行の順序が反転されます。

| フォーマット | ファイル内の行順 | `isYFlipped` |
|---|---|---|
| TIFF | 上の行から | `true` |
| FITS | 下の行から | `false` |
| BMP（高さが正の値） | 下の行から | `false` |

---

### 3.5 配置場所

```
MxPlot.App.exe
MxPlot.Core.dll
MxPlot.Extensions.Zarr.dll    ← exe と同じディレクトリに置くだけ
```

`FormatRegistry` の静的コンストラクタが起動時に `MxPlot.Extensions.*.dll` を
`AppContext.BaseDirectory` から自動スキャンします。アプリ側のコード変更は不要です。

---

## 4. MatrixPlotter プラグイン（IMatrixPlotterPlugin）

### 4.1 概要

MatrixPlotter ウィンドウの **Plugins タブ**にコマンドを追加します。
「現在表示中のデータを処理する」用途に最適です。

### 4.2 プロジェクト設定

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>MyCompany.MxPlotPlugin.GaussianFit</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MxPlot.UI.Avalonia" Version="x.x.x" />
  </ItemGroup>
</Project>
```

### 4.3 実装例

```csharp
using MxPlot.Core;
using MxPlot.UI.Avalonia.Plugins;

namespace MyCompany.MxPlotPlugin.GaussianFit
{
    /// <summary>
    /// MatrixPlotter の Plugins タブに表示されるコマンド。
    /// ユーザーがクリックすると Run() が UI スレッドで呼ばれる。
    /// </summary>
    public sealed class GaussianFitPlugin : IMatrixPlotterPlugin
    {
        public string CommandName => "Gaussian Fit";
        public string Description => "現在のフレームにガウス関数をフィットします。";

        // グループ化したい場合は GroupName を返す（省略可）
        public string? GroupName => "Analysis";

        public void Run(IMatrixPlotterContext ctx)
        {
            var data = ctx.Data;

            // 重い処理はバックグラウンドスレッドで実行する
            // ctx.Owner はダイアログのオーナーウィンドウとして使用可能
            var result = RunGaussianFit(data);

            // 結果を新しいウィンドウで表示
            ctx.WindowService.ShowMatrixPlotter(result, "Gaussian Fit Result");
        }

        private IMatrixData RunGaussianFit(IMatrixData data)
        {
            // ... 実際のフィット処理 ...
            return data; // 仮
        }
    }
}
```

#### グループ化の例

```csharp
// 同じ GroupName を返すと Plugins タブでサブグループに折りたたまれる
public string? GroupName => "Deconvolution";
public string CommandName => "Wiener Filter";

public string? GroupName => "Deconvolution";
public string CommandName => "Richardson-Lucy";
```

### 4.4 登録方法

#### 方法 A：プログラムからの直接登録

```csharp
using MxPlot.UI.Avalonia.Plugins;

// アプリ起動時に一度だけ呼ぶ
MatrixPlotterPluginRegistry.AddPlugin(new GaussianFitPlugin());
```

#### 方法 B：DLL ディレクトリからの自動スキャン

```csharp
// アプリ起動時（App.axaml.cs の OnFrameworkInitializationCompleted など）
var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
MatrixPlotterPluginRegistry.LoadFromDirectory(pluginsDir);
```

**配置場所の例：**
```
MxPlot.App.exe
plugins/
  MyCompany.MxPlotPlugin.GaussianFit.dll    ← ここに置く
  MyCompany.MxPlotPlugin.Deconvolution.dll
```

`LoadFromDirectory()` は `MxPlot.App` の `App.axaml.cs`（`OnFrameworkInitializationCompleted`）から
自動的に呼ばれます。MxPlot.App をそのまま使う場合、アプリ開発者は何もする必要はなく、プラグイン DLL を
`plugins/` フォルダに置くだけで動作します。方法 A の直接登録は、MxPlot.App を使わず自前のホストアプリを
作る場合など、この自動スキャンに乗らないケースで使います。

---

## 5. MxPlot.App プラグイン（IMxPlotAppPlugin）

### 5.1 概要

MxPlot ダッシュボード（メインウィンドウ）の **☰ → Tools メニュー**にコマンドを追加します。
「現在開いているすべてのデータセット」にアクセスできるため、
複数ウィンドウにまたがる処理や、ファイルの一括エクスポートなどに向いています。

### 5.2 プロジェクト設定

`MxPlot.App` はリファレンスアプリケーションであってライブラリではないため、NuGetパッケージとしては公開されていません（将来のバージョンで `IMxPlotAppPlugin`/`IMxPlotAppContext` を小さな `MxPlot.App.Plugins` パッケージに切り出し、通常の `PackageReference` で参照できるようにする予定です）。それまでは、次の2通りのいずれかでこの2つのインターフェース型を参照してください。

- **ソースをクローンして `ProjectReference`** — `MxPlot.App\MxPlot.App.csproj` を直接参照する。
- **ビルド済みの `MxPlot.App.dll` を直接参照** — `HintPath` に、ローカルにインストール済みのMxPlot.App（`MxPlot.exe` があるフォルダ）を指す。リポジトリのクローンなしで、リリース版のアプリだけで開発できる。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- MxPlot.App自体のTFMと一致させる必要がある — プラグインDLLはMxPlot.App自身のプロセスに
         （Assembly.LoadFromで）読み込まれるため、TFMが一致しないと読み込めない。 -->
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>MyCompany.MxPlotAppPlugin.BatchExport</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MxPlot" Version="x.x.x" />

    <!-- MxPlot.App自体はNuGetパッケージではないので、ローカルにインストール済みのDLLを直接参照する。
         IMxPlotAppPlugin/IMxPlotAppContextのためだけに必要。Private=false（CopyLocal=false）が重要 —
         これがないとこのプラグイン自身のビルド出力にMxPlot.App.dllがコピーされてしまい、
         MxPlotAppPluginRegistry.LoadFromDirectory()がplugins フォルダ内の全DLLを読み込もうとする際に、
         MxPlot.App.dllの重複コピーが無駄な読み込みの原因になる。 -->
    <Reference Include="MxPlot.App">
      <HintPath>C:\Path\To\Installed\MxPlot.App\MxPlot.App.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

### 5.3 実装例

```csharp
using Avalonia.Platform.Storage;
using MxPlot.App.Plugins;
using MxPlot.Core;
using MxPlot.Core.IO.Formats;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MyCompany.MxPlotAppPlugin.BatchExport
{
    /// <summary>
    /// 現在開いているすべてのデータを一括で CSV エクスポートするプラグイン。
    /// </summary>
    public sealed class BatchCsvExportPlugin : IMxPlotAppPlugin
    {
        public string CommandName => "Batch CSV Export";
        public string Description => "すべての開いているデータセットを CSV に書き出します。";

        public void Run(IMxPlotAppContext ctx)
        {
            var datasets = ctx.OpenDatasets;
            if (datasets.Count == 0) return;

            // ctx.Owner でダイアログオーナーを取得
            // ctx.SelectedDatasets で選択中のみ対象にすることも可能
            foreach (var data in datasets)
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"{data.XCount}x{data.YCount}.csv");

                // dataはIMatrixData（呼び出し側は要素型を知らない）なので、
                // IMatrixDataWriter.Write<T>を直接ではなく、IMatrixData.SaveAs経由で呼ぶ。
                var writer = FormatRegistry.CreateWriter(path);
                if (writer != null) data.SaveAs(path, writer);
            }
        }
    }
}
```

#### ファイルダイアログを使う例（async 版）

`Run()` は UI スレッドで呼ばれますが、async にすることで
Avalonia のダイアログ API を使えます。

```csharp
public async void Run(IMxPlotAppContext ctx)
{
    // ctx.Owner を使ってファイル保存ダイアログを開く
    if (ctx.Owner is null) return;
    var sp = ctx.Owner.StorageProvider;
    var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
    {
        Title = "Save Result",
        SuggestedFileName = "result.csv",
    });
    if (file is null) return;

    var path = file.TryGetLocalPath();
    if (path is null) return;

    // PrimarySelection: リストで最後にフォーカスされたデータ
    if (ctx.PrimarySelection is { } data)
    {
        var writer = FormatRegistry.CreateWriter(path);
        if (writer != null) data.SaveAs(path, writer);
    }
}
```

### 5.4 登録方法

`IMatrixPlotterPlugin` と同じパターンです。

```csharp
// 直接登録
MxPlotAppPluginRegistry.AddPlugin(new BatchCsvExportPlugin());

// ディレクトリスキャン（App.axaml.cs から自動的に呼ばれる）
MxPlotAppPluginRegistry.LoadFromDirectory(
    Path.Combine(AppContext.BaseDirectory, "plugins"));
```

`LoadFromDirectory()` は `App.axaml.cs` から自動的に呼ばれます。MxPlot.App をそのまま使う場合、
プラグイン DLL を `plugins/` フォルダに置くだけで動作し、他に何もする必要はありません。
直接登録（`AddPlugin`）は、MxPlot.App を使わず `MatrixPlotter`/`MxPlot.UI.Avalonia` を組み込んだ
自前のホストアプリを作る場合や、プラグインを別 DLL ではなくホストに直接コンパイルして組み込む場合に使います。

---

## 6. 1 DLL に複数の拡張を混在させる

1 つの DLL に複数の拡張タイプを含めることができます。
各 Registry の `LoadFromDirectory()` はそれぞれ自分のインターフェイスを持つ型だけを
選んで登録するため、混在しても問題ありません。

```csharp
// 1 つの DLL に複数含めた例
// MxPlot.Extensions.MyCompanyTools.dll

// ① ファイルフォーマット（自動登録）
public class MyPropFormat : IMatrixDataReader { ... }

// ② MatrixPlotter プラグイン
public class MyAnalysisPlugin : IMatrixPlotterPlugin { ... }

// ③ MxPlot.App プラグイン
public class MyBatchPlugin : IMxPlotAppPlugin { ... }
```

---

## 7. 現状の制限と将来の拡張ポイント

### 現状できないこと

以下は現時点では**意図的に公開していない**機能です。
将来、具体的な需要が生じた時点で安全な抽象インターフェイスとして追加する予定です。

| 機能 | 説明 | 対応方針 |
|---|---|---|
| MxView オーバーレイの追加 | Plugin から描画オブジェクトを MxView に追加 | `IMatrixPlotterViewContext` を将来追加 |
| クロスヘア移動イベントの購読 | クロスヘア位置変化をプラグインがフック | 同上 |
| データの差し替え（Volume 操作） | Plugin が MatrixPlotter の表示データを入れ替え | `IMatrixPlotterVolumeContext` を将来追加 |
| MxView の動作フック | ポインタイベント・キーイベントの横取り | 慎重に検討中（ImageJ 方式は採用しない） |

### 設計原則

MxPlot の拡張 API は「**許可リスト型コンテキスト**」の原則に従います。
`MatrixPlotter` や `MxView` のオブジェクトをプラグインに直接渡すことはしません。
将来の拡張も必ず薄い抽象インターフェイス（`IMatrixPlotterViewContext` 等）として追加します。

理由：
- プラグインが UI を壊せないようにする
- MxPlot.UI.Avalonia / WinForms 両方で同じプラグイン DLL が動くようにする
- MxPlot の内部リファクタリングでプラグインが壊れないようにする

---

## 8. 配置まとめ

```
[アプリ実行ディレクトリ]
│
├─ MxPlot.App.exe                         ← アプリ本体
├─ MxPlot.Core.dll
├─ MxPlot.UI.Avalonia.dll
├─ MxPlot.App.dll
│
├─ MxPlot.Extensions.OmeTiff.dll          ← FormatRegistry が自動スキャン
├─ MxPlot.Extensions.Hdf5.dll             ← 同上
├─ MxPlot.Extensions.MyPropFormat.dll     ← 同上（命名規約を守れば自動）
│
└─ plugins/                               ← MxPlot.App が起動時に自動スキャン
   ├─ MyCompany.MxPlotPlugin.GaussianFit.dll
   └─ MyCompany.MxPlotAppPlugin.BatchExport.dll
```

### 命名規約まとめ

| 種別 | 規約 | 自動検出 |
|---|---|---|
| ファイルフォーマット DLL | `MxPlot.Extensions.{Name}.dll` | ✅ `AppContext.BaseDirectory` から `FormatRegistry` が自動スキャン |
| MatrixPlotter プラグイン DLL | 任意（`*.dll`） | ✅ `plugins/` を MxPlot.App が起動時に自動スキャン |
| MxPlot.App プラグイン DLL | 任意（`*.dll`） | ✅ 同上 |

`LoadFromDirectory()` 自体はディレクトリを引数に取る汎用 API で、`plugins/` に固定されているわけでは
ありません。MxPlot.App がその引数に `plugins/` を渡して起動時に呼んでいるだけなので、MxPlot.App を
そのまま使う場合はこのフォルダに置くだけで自動検出されます（詳しくは 4.4 / 5.4 節）。
