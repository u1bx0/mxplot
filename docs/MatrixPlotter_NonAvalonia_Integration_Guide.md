# WinForms / WPF から Avalonia MxPlotter を使う手順

**作成日**: 2026-4-19  
**対象バージョン**: `MxPlot.UI.Avalonia` (Avalonia 11.3.14), .NET 10

---

> **📌 このガイドの対象スコープ**  
> このガイドは `MatrixPlotter`（Avalonia ベースの**独立ウィンドウ**）を WinForms / WPF から呼び出す方法を扱います。  
> `MxView` を WinForms コントロール / WPF ウィンドウ内に**埋め込む**（パネルとして組み込む）には  
> `Avalonia.Win32.Interoperability`（`AvaloniaControlHost` / `AvaloniaHost`）が必要ですが、  
> このパッケージは **Avalonia 12 以降でのみ公式提供**されています。  
> 現バージョン（Avalonia 11.3.x）では埋め込みは非対応です。

---

## 概要

`MxPlot.UI.Avalonia.Views.MatrixPlotter` は Avalonia ベースのウィンドウです。  
WinForms・WPF はどちらも Win32 メッセージポンプを使用するため、Avalonia と共存できます。  
`SetupWithoutStarting()` を使うと Avalonia が独自ループを起動せず、ホスト側のポンプを共有します。  
同一 UI スレッドで両フレームワークが動作するため、`Dispatcher` や `SynchronizationContext` の競合は発生しません。

| | WinForms | WPF（OnStartup） | WPF（Generic Host） |
|---|---|---|---|
| 初期化タイミング | `Program.Main()` の冒頭 | `App.OnStartup()` の冒頭 | `Program.Main()` の冒頭 |
| エントリポイント | 明示的な `Main()` | `App.xaml` が自動生成 | `Program.cs` を手動作成 |
| ウィンドウ開放スタイル | 主にコードビハインド | コードビハインド / MVVM | MVVM（DI 前提） |

---

## 1. プロジェクト参照の設定

WinForms / WPF プロジェクトの `.csproj` に以下を追加します。

```xml
<ItemGroup>
  <ProjectReference Include="..\MxPlot.UI.Avalonia\MxPlot.UI.Avalonia.csproj" />
</ItemGroup>

<ItemGroup>
  <!-- Avalonia Windows バックエンド（Win32 + Skia）-->
  <!-- ⚠️ バージョンは MxPlot.UI.Avalonia の依存 Avalonia と完全に一致させること -->
  <PackageReference Include="Avalonia.Win32" Version="11.3.14" />
  <PackageReference Include="Avalonia.Skia" Version="11.3.14" />
</ItemGroup>
```

> **⚠️ バージョン不一致は起動前クラッシュを引き起こします**  
> `Avalonia.Win32` / `Avalonia.Skia` が `MxPlot.UI.Avalonia` の Avalonia バージョンと異なると、  
> `Main()` / `OnStartup()` 到達**前**に終了コード `0xe0434352` でクラッシュします。  
> このエラーは try-catch では捕捉できません。

> **`Avalonia.Desktop` は使用しないでください**  
> Win32/macOS/Linux を束ねたメタパッケージで、Linux 向け `Tmds.DBus.Protocol` の  
> 脆弱性（GHSA-xrw6-gwf8-vvr9, High）が推移的依存として混入します。  
> Windows 専用の `Avalonia.Win32` + `Avalonia.Skia` のみで十分です。

---

## 2. Application クラスの定義（不要）

`MxPlot.UI.Avalonia` は `MxPlotHostApplication`（`Avalonia.Application` サブクラス）を公開しています。  
ホストプロジェクト側で `Application` サブクラスを独自定義する必要は**ありません**。

> **すでに Avalonia Application を持つプロジェクト**（`MxPlot.App` 参照など）では  
> `MxPlotHostApplication` を使わず、既存の `AppBuilder` 初期化をそのまま使ってください。

---

## 3. Avalonia の初期化

### WinForms（Program.cs）

```csharp
using Avalonia;
using MxPlot.UI.Avalonia;

[STAThread]
static void Main()
{
    // ① Avalonia を WinForms のメッセージループに統合
    AppBuilder.Configure<MxPlotHostApplication>()
        .UseWin32()
        .UseSkia()
        .SetupWithoutStarting();

    // ② WinForms の通常初期化
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);
    Application.SetHighDpiMode(HighDpiMode.SystemAware);
    ApplicationConfiguration.Initialize();
    Application.Run(new MainForm());
}
```

### WPF（App.xaml.cs）― OnStartup パターン（デフォルト構成）

Visual Studio の WPF テンプレートはエントリポイントを自動生成するため、`Program.cs` は存在しません。  
Avalonia の初期化は `App.xaml.cs` の `OnStartup` に書きます。

> **`App.xaml` の `StartupUri` に注意**  
> テンプレート既定では `StartupUri="MainWindow.xaml"` が設定されています。  
> 手動で `MainWindow` を生成・`Show()` する場合は `StartupUri` を削除しないと **2 枚ウィンドウが開きます**。

```csharp
// App.xaml.cs
using Avalonia;
using MxPlot.UI.Avalonia;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // ① Avalonia を WPF のメッセージループに統合（base より前に呼ぶ）
        AppBuilder.Configure<MxPlotHostApplication>()
            .UseWin32()
            .UseSkia()
            .SetupWithoutStarting();

        base.OnStartup(e);
        // StartupUri を使う場合はここで終わり。
        // DataContext は MainWindow のコンストラクタ / コードビハインドで設定する。
    }
}
```

---

### WPF（Program.cs）― Generic Host パターン（中〜大規模向け）

プロの現場では `Microsoft.Extensions.Hosting`（Generic Host）を使い `Program.cs` を手動定義する構成が増えています。  
ロギング・設定・DI が一体化し、ASP.NET Core と同じパターンで書けます。

**準備：`Program.cs` を有効にする**

WPF では `App.g.cs` が `Main()` を自動生成するため、そのままでは競合します。  
`.csproj` に `<StartupObject>` を追加して自動生成の `Main()` を無効化します：

```xml
<!-- .csproj -->
<PropertyGroup>
  <StartupObject>YourNamespace.Program</StartupObject>
</PropertyGroup>
```

また `App.xaml` から `StartupUri` を削除します：

```xml
<!-- App.xaml — StartupUri を削除 -->
<Application x:Class="YourNamespace.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Application.Resources />
</Application>
```

**Program.cs**

```csharp
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MxPlot.UI.Avalonia;

namespace YourNamespace;

public class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // ① Avalonia を WPF のメッセージループに統合
        AppBuilder.Configure<MxPlotHostApplication>()
            .UseWin32()
            .UseSkia()
            .SetupWithoutStarting();

        // ② Generic Host でサービス登録
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMatrixPlotterService, MatrixPlotterService>();
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        // ③ WPF アプリ起動
        var app = new App();
        app.InitializeComponent();
        var mainWindow = host.Services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = host.Services.GetRequiredService<MainViewModel>();
        app.Run(mainWindow);
    }
}
```

> **どちらのパターンを選ぶか**
>
> | | OnStartup パターン | Generic Host パターン |
> |---|---|---|
> | `Program.cs` | 不要（デフォルト） | 手動作成が必要 |
> | 追加パッケージ | なし | `Microsoft.Extensions.Hosting` |
> | ロギング・設定 | 別途手配 | 自動で組み込まれる |
> | 推奨場面 | 小〜中規模ツール | 中〜大規模・チーム開発 |

---

## 4. MatrixPlotter を開く

### コードビハインド（WinForms / WPF 共通）

```csharp
using MxPlot.Core;
using MxPlot.Core.Imaging;
using MxPlot.UI.Avalonia.Views;

// ── 最小構成 ──────────────────────────────────────────
var data = MatrixData<float>.Create(512, 512);
// ... data にデータを書き込む ...

MatrixPlotter.Create(data).Show();

// ── LUT とタイトル付き ──────────────────────────────────
MatrixPlotter.Create(data, ColorThemes.Jet, "計測結果 #1").Show();

// ── 参照を保持して後から操作する ────────────────────────
var plotter = MatrixPlotter.Create(data, ColorThemes.Grayscale, "リアルタイム表示");
plotter.Show();
```

バックグラウンドスレッドから開く場合：

```csharp
await Task.Run(async () =>
{
    ProcessData(data);
    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(()
        => MatrixPlotter.Create(data).Show());
});
```

### MVVM（WPF 向け）

#### パターン①：DI なし・CommunityToolkit.Mvvm のみ（最小構成）

`MatrixPlotter.Create()` は static メソッドなので、サービス抽象化や DI なしで直接呼べます。

```csharp
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MxPlot.UI.Avalonia.Views;

public partial class MainViewModel : ObservableObject
{
    [RelayCommand]
    private async Task OpenPlotterAsync()
    {
        // 重い処理はバックグラウンドで実行
        var data = await Task.Run(() => LoadData());

        // MatrixPlotter の生成・表示は Avalonia UI スレッドで実行
        await Dispatcher.UIThread.InvokeAsync(()
            => MatrixPlotter.Create(data, "解析結果").Show());
    }
}
```

`App.xaml` に `StartupUri` を設定し、ViewModel はコードビハインドで渡します：

```csharp
// App.xaml.cs
protected override void OnStartup(StartupEventArgs e)
{
    AppBuilder.Configure<MxPlotHostApplication>()
        .UseWin32()
        .UseSkia()
        .SetupWithoutStarting();

    base.OnStartup(e);
    // StartupUri="MainWindow.xaml" でウィンドウが自動生成される場合、
    // DataContext は MainWindow のコンストラクタかコードビハインドで設定する
}
```

```csharp
// MainWindow.xaml.cs
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
```

#### パターン②：サービスインターフェイス + DI（テスト容易性重視）

ViewModel からの `Dispatcher.UIThread` 直呼び出しを避けてテスト容易にしたい場合：

**サービスインターフェイス**

```csharp
public interface IMatrixPlotterService
{
    void Show(IMatrixData data, string title = "");
    Task ShowAsync(IMatrixData data, string title = "");
}
```

**実装**

```csharp
using Avalonia.Threading;
using MxPlot.UI.Avalonia.Views;

public class MatrixPlotterService : IMatrixPlotterService
{
    public void Show(IMatrixData data, string title = "")
    {
        Dispatcher.UIThread.VerifyAccess();
        MatrixPlotter.Create(data, title: title).Show();
    }

    public Task ShowAsync(IMatrixData data, string title = "")
        => Dispatcher.UIThread.InvokeAsync(()
               => MatrixPlotter.Create(data, title: title).Show()
           ).GetTask();
}
```

**ViewModel**

```csharp
public partial class MainViewModel : ObservableObject
{
    private readonly IMatrixPlotterService _plotterService;

    public MainViewModel(IMatrixPlotterService plotterService)
    {
        _plotterService = plotterService;
    }

    [RelayCommand]
    private async Task OpenPlotterAsync()
    {
        var data = await Task.Run(() => LoadData());
        await _plotterService.ShowAsync(data, "解析結果");
    }
}
```

**DI 登録（App.xaml.cs）**

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    AppBuilder.Configure<MxPlotHostApplication>()
        .UseWin32()
        .UseSkia()
        .SetupWithoutStarting();

    var services = new ServiceCollection();
    services.AddSingleton<IMatrixPlotterService, MatrixPlotterService>();
    services.AddTransient<MainViewModel>();

    var provider = services.BuildServiceProvider();
    new MainWindow { DataContext = provider.GetRequiredService<MainViewModel>() }.Show();

    base.OnStartup(e);
}
```

> **どちらを選ぶか**
>
> | | パターン① | パターン② |
> |---|---|---|
> | 追加パッケージ | CommunityToolkit.Mvvm のみ | + Microsoft.Extensions.DependencyInjection |
> | ViewModel のテスト | `Dispatcher.UIThread` の初期化が必要 | モックに差し替え可能 |
> | 推奨場面 | 小規模・単発ツール | 中規模以上・ユニットテストあり |

---

## 5. データ更新と再描画

データを書き換えた後は `plotter.Refresh()` を呼ぶと再描画されます。

### パターン A：インデクサ（最も一般的）

```csharp
data[ix, iy] = newValue;   // インデクサが内部キャッシュを自動 Invalidate
plotter.Refresh();          // UI / 非 UI スレッドどちらからでも可
```

### パターン B：GetArray で配列を直接書き換え

```csharp
var arr = data.GetArray();          // この時点でキャッシュが自動 Invalidate される
arr[iy * width + ix] = newValue;
plotter.Refresh();
```

### パターン C：GetArray → GetValueRange → 外部修正（例外ケース）

```csharp
var arr = data.GetArray();
var range = data.GetValueRange();   // キャッシュ確定（IsValid = true）
arr[iy * width + ix] = newValue;    // → キャッシュが stale になる

data.Invalidate();                  // このケースのみ明示的 Invalidate が必要
plotter.Refresh();
```

---

## 6. スレッドセーフな使い方

`plotter.Refresh()` は**非 UI スレッドから呼んでも安全**です。  
内部で `Dispatcher.UIThread.CheckAccess()` によりスレッド判定し、非 UI スレッドの場合は `Post()` でスケジュールします。

```csharp
await Task.Run(() =>
{
    ProcessData(data);
    plotter.Refresh();   // fire-and-forget でキューに入る
});
```

> 描画完了を待つ必要がある場合は `Dispatcher.UIThread.InvokeAsync()` を直接使ってください。

---

## 7. 高頻度更新パターン（タイマー方式）

`Refresh()` を毎回呼ぶと UI スレッドのキューが詰まる場合があります。  
**dirty flag + タイマー**で更新頻度を制限するのがベストプラクティスです。

```csharp
private volatile bool _isDirty = false;

// フォームの初期化（WinForms 例。WPF は DispatcherTimer で同様に実装）
void InitRefreshTimer()
{
    var timer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30 fps
    timer.Tick += (_, _) =>
    {
        if (_isDirty) { _isDirty = false; plotter.Refresh(); }
    };
    timer.Start();
}

void OnNewSample(int ix, int iy, float value)
{
    data[ix, iy] = value;
    _isDirty = true;   // Refresh は呼ばない
}
```

---

## 8. 留意事項

| 項目 | 内容 |
|---|---|
| 初期化順序 | `SetupWithoutStarting()` はホストアプリの初期化（`Application.Run` / `base.OnStartup`）より**前** |
| スレッド | `MatrixPlotter.Create().Show()` は UI スレッドから呼ぶ。`Refresh()` のみスレッドセーフ |
| WPF Dispatcher | WPF の `Application.Current.Dispatcher` と Avalonia の `Dispatcher.UIThread` は同一スレッドを指す |
| `data.Invalidate()` | 通常不要。インデクサ / `GetArray()` が自動的に内部キャッシュを無効化する |
| DPI（WinForms） | `Application.SetHighDpiMode(HighDpiMode.SystemAware)` を必ず設定する |
| バージョン固定 | `Avalonia.Win32` / `Avalonia.Skia` は `MxPlot.UI.Avalonia` の Avalonia バージョンと一致させる |
| `Avalonia.Desktop` | **使用禁止**（`Tmds.DBus.Protocol` 脆弱性 GHSA-xrw6-gwf8-vvr9 が混入） |

---

## Appendix：ProfilePlotter — (X, Y) シリーズの表示

`ProfilePlotter` は `MatrixPlotter` と同じく独立ウィンドウとして動作する、折れ線／散布図プロッタです。  
`Avalonia` の初期化（セクション 3）が済んでいれば、`MatrixPlotter` と同じ手順でそのまま使えます。

### 主要クラス

| クラス | 場所 | 役割 |
|---|---|---|
| `ProfilePlotter` | `MxPlot.UI.Avalonia.Views` | ウィンドウ本体。メニュー・情報パネル付き |
| `ProfilePlotControl` | `MxPlot.UI.Avalonia.Controls` | プロット描画コントロール（`ProfilePlotter.Plot` でアクセス） |
| `PlotSeries` | `MxPlot.UI.Avalonia.Controls` | 1 本のシリーズデータ（点列 + 名前 + スタイル） |
| `PlotStyle` | `MxPlot.UI.Avalonia.Controls` | `Line` / `Marker` / `MarkedLine` |

---

### 基本的な使い方

#### 単一シリーズ

```csharp
using MxPlot.UI.Avalonia.Views;
using MxPlot.UI.Avalonia.Controls;

// (double X, double Y) のリストを渡すだけ
var points = Enumerable.Range(0, 100)
    .Select(i => ((double)i, Math.Sin(i * 0.1)))
    .ToList();

new ProfilePlotter(points,
    name       : "sin(x)",
    xAxisLabel : "Index",
    yAxisLabel : "Value",
    title      : "サイン波").Show();
```

#### 複数シリーズ（PlotSeries リスト）

```csharp
var series = new List<PlotSeries>
{
    new(points1, "sin",  PlotStyle.Line,       Colors.SteelBlue),
    new(points2, "cos",  PlotStyle.Line,       Colors.OrangeRed),
    new(points3, "data", PlotStyle.MarkedLine, lineWidth: 1.0),
};

new ProfilePlotter(series,
    xAxisLabel : "Time [s]",
    yAxisLabel : "Amplitude",
    title      : "波形比較").Show();
```

#### 複数シリーズ（点列リストを並列渡し）

```csharp
var pointSets = new List<IReadOnlyList<(double, double)>> { points1, points2 };
var names     = new List<string> { "Channel A", "Channel B" };

new ProfilePlotter(pointSets, names,
    xAxisLabel : "Freq [Hz]",
    yAxisLabel : "Power",
    style      : PlotStyle.Line,
    title      : "スペクトル").Show();
```

---

### データ更新（リアルタイム）

`ProfilePlotter` を保持しておき、`Plot.UpdatePoints()` または `Plot.UpdatePointsAndFit()` で更新します。

```csharp
var plotter = new ProfilePlotter(initialPoints, name: "測定値");
plotter.Show();

// ── ビュー範囲を維持したまま点列だけ更新（最速） ────────────────────
plotter.Plot.UpdatePoints(seriesIndex: 0, newPoints);

// ── 点列を更新してビューをデータに合わせてフィット ────────────────
plotter.Plot.UpdatePointsAndFit(seriesIndex: 0, newPoints);

// ── 全シリーズを差し替え ────────────────────────────────────────────
plotter.Plot.SetData(newSeriesList, "X", "Y", "新しいタイトル");
```

> **スレッド**: `UpdatePoints` / `SetData` は Avalonia UI スレッドから呼んでください。  
> バックグラウンドスレッドから呼ぶ場合は `Dispatcher.UIThread.InvokeAsync` でラップします。

---

### 軸範囲の固定

```csharp
// X 軸を 0〜100 に固定、Y 軸は自動
plotter.Plot.XAxisFixed = true;
plotter.Plot.XFixedMin  = 0;
plotter.Plot.XFixedMax  = 100;

// 既知のデータ範囲を一括セット（O(N) のデータスキャンをスキップ、高速）
plotter.Plot.SetViewRange(xRange: (0, 100), yRange: (-1.5, 1.5));
```

---

### 情報パネルへのテキスト出力

```csharp
plotter.AppendInfoLine("Peak: 1.234 @ t=42");
plotter.AppendInfoLine($"RMS: {rms:F3}");
plotter.ClearInfo();
plotter.InfoText = "書き換え";
```

---

### PlotSeries のプロパティ一覧

| プロパティ | 型 | 既定 | 説明 |
|---|---|---|---|
| `Points` | `IReadOnlyList<(double X, double Y)>` | ― | 点列 |
| `Name` | `string` | `""` | 凡例に表示する名前 |
| `Style` | `PlotStyle` | `Line` | `Line` / `Marker` / `MarkedLine` |
| `Color` | `Color?` | `null`（パレット自動） | 系列色 |
| `LineWidth` | `double` | `1.5` | 線幅（dip） |
