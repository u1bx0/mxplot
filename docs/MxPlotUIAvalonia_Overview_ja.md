# MxPlot.UI.Avalonia — 概要

**作成日**: 2026-04-22  
**更新日**: 2026-10-26  
**対象**: `MxPlot.UI.Avalonia` (Avalonia 11.3.x), .NET 8 / .NET 10

> 英語版 [MxPlotUIAvalonia_Overview.md](./MxPlotUIAvalonia_Overview.md) の日本語版です。内容に差異がある場合は英語版を正としてください。

---

## MxPlot.UI.Avalonia とは

`MxPlot.UI.Avalonia` は、`MxPlot.Core` のデータを画面に表示するクロスプラットフォームの UI レイヤーです。  
`MxPlot.Core` をモデル（土台）として利用し、次のものを提供します。

- `IMatrixData` を表示する独立ウィンドウ（`MatrixPlotter`）
- LUT・ズーム・パン・オーバーレイに対応した、埋め込み可能な描画コントロール（`MxView`）
- 直交（XYZ スライス）ビューのパネル（`OrthogonalPanel`）
- ラインプロファイル解析用のプロファイルウィンドウ（`ProfilePlotter`）
- コアを変更せずに UI を拡張するためのプラグイン / ツールモデル

Avalonia 11 を対象とし、Windows・macOS・Linux で動作します。

---

## 主なコンポーネント

### `MatrixPlotter` (`Views/MatrixPlotter.cs`)

多機能な `Window` のサブクラスです。1 つの `IMatrixData` につき 1 ウィンドウを開きます。

| 役割 | 内容 |
|---|---|
| ビットマップ描画 | `MxView`（LUT + WriteableBitmap）に委譲 |
| フレーム移動 | 各 `Axis` に対応する `AxisTracker` コントロール |
| 値レンジの制御 | `ValueRangeBar` とインライン設定パネル（Auto / Fixed / All / ROI の各モード） |
| オーバーレイ | 矩形・線・ROI。`OverlayManager` が Skia サーフェス上に描画 |
| 直交ビュー | `OrthogonalPanel` による下側（XZ）と右側（YZ）のスライス |
| ステータスバー | データ型、メモリサイズ、ズーム率、進捗、通知 / トースト |
| 連動プロッタ | `LinkRefresh` による複数ウィンドウの同期表示 |
| Composite モード | 複数チャンネルのカラー合成。[Composite Rendering Guide](./MatrixPlotter_Composite_Guide.md)（英語）を参照 |
| ColorCoded 投影 | 深度をカラーで表す XY（Z 軸）投影。専用の子ウィンドウに表示 — [後述](#colorcoded深度カラーコード投影) |
| 外部制御 | Facade プロパティ（`Lut`, `IsInvertedColor`, `LutDepth`, `RangeMode`, `IsFixedRange`, `FixedRange`）— [Usage Guide](./MatrixPlotter_Usage_Guide.md)（英語）を参照 |
| インタラクティブツール | ツール拡張用の `IPlotterTool` / `IMatrixPlotterContext` |

**ファクトリメソッド:**

```csharp
MatrixPlotter.Create(data, lut: ColorThemes.Jet, title: "My Data").Show();
```

### `MxView` (`Controls/MxView.axaml.cs`)

描画の中核となるコントロールです。`LutBitmapWriter` と `LookupTable` を通して `IMatrixData` のフレームを ARGB ビットマップに変換します。`RenderingMode.Composite` が有効な場合は `CompositeBitmapWriter` を使います。

主なプロパティ:
- `MatrixData` — データソース
- `FrameIndex` — 現在表示中のフレーム
- `Lut` — 適用中の `LookupTable`
- `IsFixedRange` / `FixedMin` / `FixedMax` — 値レンジの上書き
- `IsInvertedColor` / `LutDepth` — LUT の反転と量子化レベル
- `RenderingMode` — `Lut`（既定）、`Composite`、`ColorCoded`（下の [ColorCoded](#colorcoded深度カラーコード投影) を参照）
- `CompositeRecipes` / `CompositeBlendMode` / `CompositeFrameIndices` — 複数チャンネル合成の状態
- `Zoom`, `IsFitToView` — 表示変換
- `OverlayManager` — オーバーレイオブジェクトの管理

主なイベント:
- `MatrixDataChanged` — `MatrixData` が差し替えられたときに発生
- `RenderingPropertyChanged` — 上記の描画プロパティのいずれかが変わったときに発生します。
  `MainView.Lut = …` のように直接代入した値が `MatrixPlotter` とその ViewModel に伝わるのは、このイベントのおかげです
  （[Usage Guide](./MatrixPlotter_Usage_Guide.md) を参照）
- `AutoRangeComputed` — Auto 描画のあと、フレームの最小値・最大値とともに発生
- `ScrollStateChanged` — ズーム / パンが変わったときに発生

> `MxView.BitmapRefreshed` は `internal` で、アセンブリの外からは購読できません。
> 同じ通知を転送している `MatrixPlotter.ViewUpdated` を使ってください。

### `OrthogonalPanel` と `OrthogonalViewController`

`OrthogonalPanel` は、3 つの `MxView` を含むグリッドレイアウトです。
- `MainView` — XY 平面（主ビュー）
- `BottomView` — XZ 平面（X は `MainView` と共有し、Z は画面の縦方向）
- `RightView` — YZ 平面（Y は `MainView` と共有し、Z は画面の横方向）

`OrthogonalViewController` は 3 つのビューをつなぎます。
- すべてのビューで `FrameIndex` と描画設定を同期する
- XY 投影ウィンドウ（Z 軸方向の深度表示）を管理する

直交モードは、`MatrixData` が 3 軸以上のときに自動で有効になります。

### ColorCoded（深度カラーコード投影）

`RenderingMode.ColorCoded` は独立した機能ではなく、Composite のように任意のウィンドウで「入る」モードでもありません。
**XY（Z 軸）投影の一形態**です。Max / Min 投影と同じ「勝者の値」のスキャン（`ExtremumIndexOperation`）に加えて、
勝者の *元の Z インデックス* を深度パレットで色に変換します。そのため次のような性質があります。

- `MatrixPlotter.VolumeOperation.cs` の `OnXYProjectionChanged` が作る**一時的な XY 投影の子ウィンドウ**にだけ現れます。
  通常のウィンドウの `MxView` に直接設定するものではありません。`ProjectionSelector` は X-Y（Z Projection）の行に限り、
  通常の投影モードの上に 4 つの選択肢（Color(RGB-Max) / Color(RGB-Add) / Color(Max) / Color(Min)）を追加して提示します。
  `MxPlot.Core` の `ProjectionMode` 列挙体は変更されておらず、区別は `ProjectionSelector.IsColorCoded(plane)` で別に報告されます。
- 選択肢によって、深度を色にする方法が異なります。`Color(Max)` / `Color(Min)` は、ピクセルごとに最も明るい / 最も暗い 1 枚のスライスの深度色を使うので、色がそのまま深度を表します。
  `Color(RGB-Max)` は各スライスを深度で着色し、R・G・B の成分ごとに最大値を残します。`Color(RGB-Add)` は着色したスライスを合計（255 で頭打ち）するため、密なスタックでは白く飽和します。
  RGB の 2 つは、異なる深度の構造が重なって混色されるので、パレットにない色が現れることがあります。
- 子ウィンドウが実際に持つ `MatrixData` は、通常の「勝者の値」の投影結果（元データと同じ型）のままです。
  Duplicate / Convert / Filter / Save は、他の投影と同じように使えます。特別なのは**表示**だけです。
  `ColorCodedBitmapWriter` が、そのデータと、勝者インデックス / 深度パレット / レンジ / 反転をまとめた `ColorCodedRenderInfo` を組み合わせて描画します。
  この情報は*親*の `OrthogonalViewController` がスキャンして保持します。
  `Color(RGB-Max)` / `Color(RGB-Add)` は、掃引範囲のすべてのスライスをバックグラウンドスレッドで混色し（`ColorCodedRgbBlender`、これも親が保持）、
  結果を `ColorCodedRenderInfo.BlendedArgb` として渡します。ライターはそれをビットマップにコピーするだけです。
- **専用の UI コンポーネント**があります。`MatrixPlotter.ColorCoded.cs` は通常の LUT ヘッダーの
  `LutSelector` / `ValueRangeBar` をそのまま使いますが、詳細パネルは専用のものに差し替えます。
  Start / End（スキャン範囲）と Invert を持ち、深度コード投影には意味のない通常の Level / ヒストグラムのパネルは表示されません。
- Composite と ColorCoded は排他です（どちらも深度 / Z のような軸を前提としているため）。
- 子ウィンドウでのライブ表示とは異なり、通常の Create Projection ダイアログで ColorCoded 投影を**ベイク**すると、
  「勝者の値」のデータは残りません。色付け済みの ARGB 結果を分解して、本物の 3 チャンネル **RGB `MatrixData<byte>`**
  （`ColorAxis.CreateRgb()`）にします。他の RGB データセットと同じ形です。ベイクでは、再現できるように
  Start / End / LUT / Invert（と実際に適用された ValueMin / Max）が操作履歴に記録されます。

### `ProfilePlotter`

`MatrixPlotter` から開く、ラインプロファイル解析用のサブウィンドウです。  
`LineObject` オーバーレイに沿ったピクセル値を、1 次元のグラフとして表示します。  
複数のプロファイルを同時に開くことができ、親の `MatrixPlotter` を閉じると自動的に閉じます。

### `MxPlotHostApplication` (`MxPlotHost.cs`)

MxPlot のウィンドウを、Avalonia 以外のアプリケーション（WinForms、WPF、コンソール）の中でホストするための、最小限の `Avalonia.Application` サブクラスです。
Fluent テーマと MxPlot のスタイルを読み込み、メインウィンドウは作りません。そのため、ホスト側のプロジェクトで `Application` のサブクラスを用意する必要はありません。

```csharp
// アプリケーションの起動時に 1 回だけ、最初の MatrixPlotter.Create() より前に呼ぶ
AppBuilder.Configure<MxPlotHostApplication>()
    .UseWin32()
    .UseSkia()
    .SetupWithoutStarting();
```

> 静的な `MxPlotHost.Initialize()` のようなヘルパーはありません。ホストは、上のように `AppBuilder` を通して
> 自分でプラットフォームバックエンドを選びます。

詳しくは [WinForms / WPF 統合ガイド](./MatrixPlotter_NonAvalonia_Integration_Guide_ja.md) を参照してください。
コンソール向けの方法（追加で `Dispatcher.UIThread.MainLoop` が必要）は
[MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md)（英語）にあります。

### `MxPlotScriptHost` (`MxPlotScriptHost.cs`)

`MxPlotHostApplication` は、ホストがすでにメッセージループを持っていることを前提とします。
`MxPlotScriptHost` はループを持たないホスト向けです。.NET 10 のファイルベースアプリ（`dotnet run app.cs`）、
コンソールツール、ノートブックのセルなどが該当します。`Run(script, configure?)` は Avalonia のメッセージループを開始し、
`script` を UI スレッドとは別のスレッドで実行して、スクリプトが開いたすべてのウィンドウが閉じたら戻ります。
`Start` は REPL / ノートブック向けの非ブロッキング版です（macOS では AppKit がプロセスのメインスレッドでのループを要求するため、
`PlatformNotSupportedException` になります）。Avalonia は一度しか初期化できないので、`Run` / `Start` はプロセスにつき 1 回だけです。

```csharp
MxPlotScriptHost.Run(() =>
{
    var plotter = MxPlotScriptHost.Show(data, title: "My Data");
});
```

API の全体と、`MxPlotHostApplication` と共通のスレッド親和性の規約は
[MatrixPlotter Basic Usage Guide § Scripting with MxPlotScriptHost](./MatrixPlotter_Usage_Guide.md#scripting-with-mxplotscripthost)（英語）を参照してください。

---

## プラグイン / ツールモデル

拡張ポイントは、役割の異なる 3 種類があります。

| インターフェース | 役割 | メニューの位置 |
|---|---|---|
| `IPlotterTool` | キャンバス上で操作するインタラクティブツール（クロップ、計測、注釈）。完了またはキャンセルされるまで、そのウィンドウで有効なまま | Processing メニュー（例: Crop） |
| `IMatrixPlotterPlugin` | 現在のデータ / コンテキストに対する 1 回限りのコマンド | 「Plugins」タブ |
| `IRenderExportPlugin` | 描画したビュー（生データではなく）をファイルに書き出す | 「Export as…」サブメニュー |

### `IPlotterTool`

`IPlotterTool` を実装して、インタラクティブなツール（クロップ、計測、注釈など）を作ります。

```csharp
public interface IPlotterTool : IDisposable
{
    event EventHandler<IMatrixData?>? Completed;
    event EventHandler? Cancelled;

    void Invoke(PlotterToolContext context);
    void NotifyContextChanged(PlotterToolContext newContext) { }  // 既定: 何もしない
}
```

有効なツールは `MatrixPlotter` が内部で管理し、同時に有効にできるのは 1 つだけです。
`Invoke` でツールを開始し、`PlotterToolContext`（`MainView`, `HostVisual`, `Data`, `OrthoPanel`, `DepthAxisName`）を受け取ります。
`Completed` が発生すると、その引数の `IMatrixData?`（ツールの結果。データの変更がなければ `null`）が適用されます
（たとえば、クロップ結果が現在のデータセットに置き換わります）。
`NotifyContextChanged` は、ホストのコンテキストが実行中に変わったとき（深度軸の切り替えやデータの差し替えなど）に、
ツールが自分自身を再検証するためのものです。

### `IMatrixPlotterContext`

プラグインのコードに渡され、ホストのプロッタへの制御された手段を提供します。

```csharp
public interface IMatrixPlotterContext
{
    IMatrixData Data { get; }
    double DisplayMinValue { get; set; }   // 設定すると固定レンジモードにも切り替わる
    double DisplayMaxValue { get; set; }   // 設定すると固定レンジモードにも切り替わる
    TopLevel? Owner { get; }
    string Title { get; }                  // ホストウィンドウのタイトル
    IPlotWindowService WindowService { get; }
    IDisposable ShowSyncedMatrixPlotter(IMatrixData initial, string title,
        Func<PlotSyncInput, CancellationToken, IMatrixData?> transform);
    int ActiveFrameIndex { get; }
    LookupTable? CurrentLut { get; }
    bool IsLutInverted { get; }

    WriteableBitmap RenderFrame(int frameIndex,
                                 double valueMin = double.NaN,
                                 double valueMax = double.NaN);
}
```

`RenderFrame` は、フレームを書き出すプラグインの基本部品です。プロッタの現在の（または上書きした）LUT と値レンジで任意のフレームを描画し、
データのネイティブ解像度の `WriteableBitmap` を返します。返された `WriteableBitmap` は呼び出し側のものです。
全フレームのインデックスについてループすれば、動画 / GIF 書き出しに必要な連番画像が得られます。

`ShowSyncedMatrixPlotter` は、ホストに追従するウィンドウを開きます。最初は `initial` を表示し、その後はホストのアクティブフレームが変わるか、
データが更新されるたびに、`transform` で再計算します（バックグラウンドスレッドで、キャンセル用トークンつきで実行されます）。
ウィンドウはホストと一緒に閉じ、戻り値のハンドルを破棄するとそれより早く閉じます。`PlotSyncInput` は、表示中の `Frame` と、
ホストの完全な `Source` を持ちます（どちらも読み取り専用）。UI スレッドから呼び出してください。

### `IMatrixPlotterPlugin`

`IMatrixPlotterPlugin` を実装して、「Plugins」メニュータブに 1 回限りのコマンドを追加します。

```csharp
public interface IMatrixPlotterPlugin
{
    string CommandName { get; }             // メニューのラベル
    string Description { get; }             // ツールチップ
    string? GroupName => null;              // サブヘッダーでのグループ化（省略可）
    void Run(IMatrixPlotterContext context); // UI スレッドで実行される
}
```

### `IRenderExportPlugin`

`IRenderExportPlugin` を実装すると、すべてのウィンドウの「Export as…」サブメニューに項目を追加できます。
`IMatrixDataWriter`（生のピクセルデータ用で、`FormatRegistry` に登録します）とは異なり、
LUT とオーバーレイが適用済みの*描画されたビュー*を、`IRenderHost.RenderFrameAsync` 経由の BGRA32 フレームとして書き出します。
`MxPlot.UI.Avalonia.Video` が AVI / MP4 の書き出しに使っています。`ExportAsync` の詳しい規約
（進捗報告、キャンセル、`RequiresStack`）は、インターフェース自体の XML ドキュメントを参照してください。

### `MatrixPlotterPluginRegistry`

3 種類のプラグインを管理する静的レジストリです（`Plugins`, `ExportPlugins`。`IPlotterTool` のインスタンスは呼び出しごとに作られるので、ここには登録しません）。
プラグインは `AddPlugin` / `AddExportPlugin` でコードから追加するか、`LoadFromDirectory` で DLL のあるディレクトリから探索して読み込みます。
`PluginsChanged` / `ExportPluginsChanged` は UI スレッドで発生するので、`MatrixPlotter` は一覧が変わったときにメニューを作り直せます。
`IMatrixPlotterContext` が使う `IPlotWindowService` もここで提供します。

---

## カスタム LUT

`MatrixPlotter` は、`LookupTable`（名前空間 `MxPlot.UI.Avalonia.Rendering`）でデータの値を色に対応づけます。
組み込みのテーマは、静的クラス `ColorThemes` のプロパティです（Grayscale, Hot, OrangeHot, GreenHot, Cold, Profiler, Spectrum, Jet, Turbo, Viridis, CoolWarm, Phase, HiLo）。
組み込みもカスタムも、すべてのテーマは名前で登録されており（`ColorThemes.Get(name)`, `ColorThemes.Names`）、LUT セレクタに並ぶのはこの登録済みの一覧です。
カスタム LUT は、次の 2 つの方法で追加できます。

### 方法 1: 外部 `.mlut` ファイル（自動読み込み）

`.mlut` ファイルを、Add-on ルートの `LUTs/` フォルダに置きます。ルートはプラグインやファイルフォーマット拡張と共通です
（`AddOnFolders` と [拡張開発ガイド § 配置まとめ](./MxPlot_Extensions_Guide_ja.md#8-配置まとめ) を参照）。

| ルート | 場所 |
|---|---|
| アプリのフォルダ | `MxPlot.App.exe` の隣（macOS ではバンドル内の `Contents/MacOS`） |
| ユーザーごとのフォルダ | macOS のみ: `~/Library/Application Support/MxPlot`。macOS での推奨の置き場所 |
| `MxPlot.app` を置いたフォルダ | macOS のみ。推奨しません |
| カレントディレクトリ | これも検索されるので、スクリプトは自分専用の `LUTs/` フォルダを持てます |

ファイルが読み込まれる仕組みは次のとおりです。

- `LutSelector` は、プロセス内で最初にセレクタが作られたときに、これらのフォルダを**1 回だけ**スキャンします。アプリの実行中に追加したファイルは、次回の起動まで反映されません。
- サブフォルダもスキャンします。`LUTs` というフォルダ名と `.mlut` という拡張子は、どのプラットフォームでも大文字小文字を区別しません。
- 解析できないファイルはスキップされ、他のファイルは読み込まれます。
- すでに登録されている LUT と同じ名前の LUT は、それを置き換えます（組み込みテーマも対象です）。
- スキャンのきっかけになるのは `LutSelector` だけです。セレクタを使わずに `ColorThemes` を使うコード（たとえばヘッドレスのツール）では、自分で `ColorThemes.LoadFromDirectory(...)` を呼ぶ必要があります。

**ファイル形式:**

```
<LUT 名>
<レベル数>[, <欠損値の色（16 進）>]
<R>, <G>, <B>
<R>, <G>, <B>
...
```

- **1 行目**: LUT の名前（セレクタに表示される）
- **2 行目**: 色のレベル数。続けて、欠損値の色を 16 進の `RRGGBB` で指定できます（例: `5, 0000FF` は青）
- **3 行目以降**: 1 行に 1 色。RGB の 3 つ組（0〜255）、または `RRGGBB` の順の 16 進値（例: `FF8800`）
- 空行と、`#` で始まる行は無視されます

ファイルを読み込むときに補間は行われません。カラー行がレベル数より少ない場合、残りのレベルは最後の色で埋められ、カラー行の方がレベル数よりも多ければ、超えた分は無視されます。補間は表示のときに行われます。LUT は、プロッタの Level 設定（`LutDepth`、既定は 256）に合わせて、
RGB 空間で線形補間によりリサンプリングされるので、短いファイルでもグラデーションを表現できます。
たとえば次の 5 レベルのファイル（`CustomHot.mlut`）は、既定の Level では滑らかなグラデーションで表示され、Level を 5 に下げると 5 色の帯になります。

```
CustomHot
5, 0000FF
0, 0, 0
128, 0, 0
255, 128, 0
255, 255, 128
255, 255, 255
```

### 方法 2: コードからの登録

実行時に LUT を読み込んで登録します。

```csharp
using MxPlot.UI.Avalonia.Rendering;

// ファイルから
var lut = ColorThemes.LoadFromFile("path/to/custom.mlut");
if (lut != null)
    ColorThemes.Register(lut);

// フォルダから（既定ではサブフォルダも対象）。登録できた LUT の数を返す
int count = ColorThemes.LoadFromDirectory("path/to/luts");

// 手動で作る: コンストラクタは R | (G << 8) | (B << 16) で詰めた色を受け取る
// （そのため、ここでは 0xFF0000 が青。.mlut ファイルの RRGGBB とは並びが逆）
int[] colors = new int[256];
for (int i = 0; i < 256; i++)
{
    int r = i;
    int g = i / 2;
    int b = 255 - i;
    colors[i] = r | (g << 8) | (b << 16);
}
var customLut = new LookupTable("MyCustomLUT", colors, missingColor: 0xFF00FF);
ColorThemes.Register(customLut);
```

`LoadFromFile` は、ヘッダーを解析できないファイルでは `null` を返し、3 行未満のファイル（名前、ヘッダー、少なくとも 1 色）では `ArgumentException` を投げます。
登録した LUT は `plotter.Lut = customLut` で割り当てられ、`ColorThemes.Get("MyCustomLUT")` で名前から取得することもできます。

---

## 依存関係

```
MxPlot.UI.Avalonia
    ├── MxPlot.Core          (IMatrixData, MatrixData<T>, Axis, ...)
    ├── MxPlot.Extensions.Fft (Processing > FFT。Math.NET Numerics を使用)
    └── Avalonia 11          (Window, Controls, Skia 描画。LookupTable と ColorThemes はここにある)
```

`MxPlot.Core` は Avalonia に依存せず、ヘッドレスやサーバー用途で単独で使えます。

---

## 関連ドキュメント

- [MatrixPlotter Basic Usage Guide](./MatrixPlotter_Usage_Guide.md)（英語。[Scripting with MxPlotScriptHost](./MatrixPlotter_Usage_Guide.md#scripting-with-mxplotscripthost) を含む）
- [MatrixPlotter Composite Rendering Guide](./MatrixPlotter_Composite_Guide.md)（英語）
- [WinForms / WPF 統合ガイド](./MatrixPlotter_NonAvalonia_Integration_Guide_ja.md)
- [MatrixPlotter メタデータフォーマットガイド](./MatrixPlotter_MetadataFormat_Guide_ja.md)
- [MxPlot 拡張開発ガイド](./MxPlot_Extensions_Guide_ja.md)
