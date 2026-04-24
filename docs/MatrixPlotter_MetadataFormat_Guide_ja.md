# MatrixPlotter メタデータフォーマットガイド

**MxPlot.Core / MxPlot.UI.Avalonia — メタデータ運用規約**

> 最終更新日: 2026-04-24

*注意: このドキュメントは大部分がAIによって生成されたものであり、正確性の確認が必要です。*

## 📚 目次

1. [概要](#概要)
2. [Metadata 辞書の基本](#metadata-辞書の基本)
3. [キー名前空間](#キー名前空間)
4. [フォーマットヘッダーメタデータ（Core API）](#フォーマットヘッダーメタデータcore-api)
   - [API リファレンス](#api-リファレンス)
   - [保存メカニズム](#保存メカニズム)
   - [IO ハンドラー作成者向けガイド](#io-ハンドラー作成者向けガイド)
   - [派生時とラウンドトリップの挙動](#派生時とラウンドトリップの挙動)
5. [処理履歴（UI 層）](#処理履歴ui-層)
   - [History エントリ形式](#history-エントリ形式)
   - [履歴の記録方法](#履歴の記録方法)
   - [VisibleSystemKeys メカニズム](#visiblesystemkeys-メカニズム)
6. [UI での動作まとめ](#ui-での動作まとめ)
7. [ファイル構成](#ファイル構成)

---

## 概要

`IMatrixData`（実体は `MatrixData<T>`）は、ピクセルデータに付随する任意の情報を自由に保持できる `IDictionary<string, string>` 型の `Metadata` プロパティを持っています。キーと値はともに文字列で、フォーマット固有の情報や計測パラメータ、コメントなどを格納するために使われます。

UI 層である **MatrixPlotter**（`MxPlot.UI.Avalonia`）はこの `Metadata` をさらに活用し、次の3種類の情報を同じ辞書に格納しています:

| 用途 | 例 | 管理者 |
|---|---|---|
| **UI 設定** | LUT の最小・最大値、表示モード | MatrixPlotter システム |
| **フォーマット固有情報** | OME-XML ヘッダー、FITS ヘッダーカード | IO ハンドラー（OmeTiffHandler 等） |
| **処理履歴** | Crop・Duplicate 等の操作ログ | MatrixPlotter システム |

これらのキーの中には、**ユーザーが誤って編集・削除すると困るもの**があります（例: OME-XML をユーザーが手動書き換えるとフォーマット整合性が壊れる）。一方で `Metadata` は `IDictionary` であり Core レベルでは書き込みを制限する仕組みがありません。

そこで、このドキュメントでは**フォーマットヘッダーメタデータの規約**と、Core ライブラリと UI 層がどのように連携するかを解説します:

- **フォーマットヘッダー** — 元ファイルフォーマット固有のヘッダー情報（FITS ヘッダーテキスト、OME-XML 等）を格納するメタデータエントリ。自動的に読み取り専用として扱われ、派生時にはコピーされない
- **UI での編集制限** — フォーマットヘッダーキーは 🔒 付きで表示され、ユーザー編集がブロックされる（UI 層の責任）
- **システム管理キー** — `mxplot.*` 名前空間に属する内部用キー
- **処理履歴** — データに適用された操作の自動記録

> **重要**: Core ライブラリ（`MxPlot.Core`）は Metadata 辞書への書き込みを物理的に禁止**しません**。`MarkAsFormatHeader` はキーがフォーマット固有のヘッダー情報であるという意図を記録します。これには2つの効果があります: (1) UI 消費者がそのキーを読み取り専用として扱う、(2) `CopyPropertiesFrom` が派生時にそのキーをスキップする。Core ライブラリは編集制限を強制しません — それは UI 層の責任です。その他の規約（History、VisibleSystemKeys、UI 表示ルール）も同様に **MatrixPlotter（UI 層）固有**です。

---

## Metadata 辞書の基本

```csharp
public interface IMatrixData
{
    IDictionary<string, string> Metadata { get; }
    // ...
}
```

- キーは**大文字小文字を区別しない**（`StringComparer.OrdinalIgnoreCase`）
- 値は常に `string` — 複雑なデータはシリアライズして格納（JSON、CSV、XML 等）
- `CopyPropertiesFrom()` はフォーマットヘッダーキーとそのトラッキングキーを**除き**、すべてのメタデータエントリをコピーする（[フォーマットヘッダーメタデータ](#フォーマットヘッダーメタデータcore-api)を参照）

---

## キー名前空間

| キーパターン | 管理者 | UI 表示 | 編集可否 | `CopyPropertiesFrom` でコピー | 例 |
|---|---|---|---|---|---|
| *(ユーザー定義)* | ユーザー / IO ハンドラー | ✅ 表示 | ✅ 可 | ✅ される | `user_note`, `experiment_id` |
| `mxplot.*`（一般） | MatrixPlotter システム | ❌ 非表示 | ❌ 不可 | ✅ される | `mxplot.lut.min`, `mxplot.metadata.format_header` |
| `mxplot.*` + `VisibleSystemKeys` | MatrixPlotter システム | ✅ 表示名で表示 | ❌ 不可 | ✅ される | `mxplot.data.history` → "History" |
| *(任意キー)* + `MarkAsFormatHeader` | IO ハンドラー | ✅ 🔒付きで表示 ¹ | ❌ 不可 ¹ | ❌ **されない** ² | `OME_XML`, `FITS_HEADER` |

> ¹ UI 層が `IsFormatHeader()` を参照して制限します。Core ライブラリ自体はこの制限を強制しません。
>
> ² フォーマットヘッダーエントリはソースファイルを記述するものであり、派生（Crop、Filter、Slice 等）後は無効になります。`Clone()`（同一データの完全な複製）では全メタデータが保持されます。

`mxplot.` 接頭辞は `PlotterConfigKeys.Prefix` で定義されています。この接頭辞に一致するキーは、`PlotterConfigKeys.VisibleSystemKeys` に明示的に登録されていない限り、Metadata タブに表示されません。

---

## フォーマットヘッダーメタデータ（Core API）

フォーマットヘッダーキーは、元ファイルフォーマット固有のヘッダー情報（FITS ヘッダーテキスト、OME-XML 等）を格納するメタデータエントリです。キーをフォーマットヘッダーとしてマークすると、2つの効果があります:

1. **UI で読み取り専用** — 消費者がそのキーを読み取り専用として扱う（編集無効化）
2. **派生時に除外** — `CopyPropertiesFrom` がフォーマットヘッダーエントリをスキップする。ヘッダーはソースファイルを記述するものであり、Crop、Filter、Slice 等の操作後は無効になるため

> `Clone()`（完全な複製）はフォーマットヘッダーを含む**すべての**メタデータをコピーします。`CopyPropertiesFrom`（派生）のみが除外します。

### API リファレンス

`MatrixData.Static.cs`（`MxPlot.Core` 名前空間）の3つの拡張メソッド:

```csharp
// キーをフォーマットヘッダーとしてマーク
data.MarkAsFormatHeader("OME_XML", "FITS_HEADER");

// キーがフォーマットヘッダーかを確認
bool isFH = data.IsFormatHeader("OME_XML"); // true

// 全フォーマットヘッダーキーを取得
IReadOnlySet<string> fhKeys = data.GetFormatHeaderKeys();
```

### 保存メカニズム

フォーマットヘッダーマーカーは Metadata 辞書内に**カンマ区切りリスト**として保存されます:

```
Metadata["mxplot.metadata.format_header"] = "OME_XML,FITS_HEADER"
```

この設計が選択された理由:

| メリット | 説明 |
|---|---|
| **ゼロコストのラウンドトリップ** | `Metadata` を永続化するフォーマットライター（MXD、OME-TIFF、FITS）が自動的にフォーマットヘッダーマーカーも保存する |
| **自己記述的** | データ自身がどのキーがフォーマットヘッダーかを記録する — 静的レジストリや DLL ロードタイミングの問題なし |
| **Core モデル変更不要** | `IMatrixData` に追加のフィールドやインターフェースは不要 |

キー `mxplot.metadata.format_header` 自体は `mxplot.*` 予約名前空間ルールにより UI 非表示です。

### IO ハンドラー作成者向けガイド

ファイルフォーマットリーダーが**フォーマットに正規なメタデータ**（TIFF 内の OME-XML、FITS ヘッダーカード等）を読み込む場合、値を設定した直後にフォーマットヘッダーとしてマークしてください:

```csharp
// ✅ IO ハンドラーの推奨パターン
public static void ApplyMetadataToMatrixData(IMatrixData md, MyFormatMetadata meta)
{
    // メタデータ値を設定
    md.Metadata["MY_FORMAT_HEADER"] = meta.RawHeaderText;

    // フォーマットヘッダーとしてマーク
    // → UI は読み取り専用として扱い、CopyPropertiesFrom は派生時にスキップ
    md.MarkAsFormatHeader("MY_FORMAT_HEADER");
}
```

**組み込みの実例:**

| ハンドラー | キー | 内容 |
|---|---|---|
| `OmeTiffHandler` | `OME_XML` | IMAGEDESCRIPTION タグの元 OME-XML 文字列 |
| `FitsHandler` | `FITS_HEADER` | 生の 80 文字 FITS ヘッダーカード |

**ガイドライン:**

- `MarkAsFormatHeader` は**読み込みパス**（書き込みパスではなく）で呼び出す
- フォーマットを反映した説明的な大文字キー名を選択する
- 値は大きくてよい（完全な XML、全ヘッダーカード）— UI は等幅フォントとコピーボタンで表示する
- キーに `mxplot.*` 接頭辞は**使用しない** — この名前空間は MatrixPlotter 内部で予約されている

### 派生時とラウンドトリップの挙動

**直接読み込み → 保存（同一データ）:**

```
ファイル読み込み（IO 層）
  │  md.Metadata["OME_XML"] = xml;
  │  md.MarkAsFormatHeader("OME_XML");
  ▼
Metadata 辞書:
  "OME_XML"                        = "<OME>..."
  "mxplot.metadata.format_header"  = "OME_XML"
  │
  │  MXD / OME-TIFF / FITS に保存 → 両エントリが永続化
  │  再読み込み → 両エントリが復元
  ▼
UI: IsFormatHeader("OME_XML") → true → 読み取り専用表示
```

**派生（Crop、Filter 等）:**

```
ソース: OME_XML フォーマットヘッダーを持つ OME-TIFF
  │
  │  CopyPropertiesFrom(source)
  │    → "OME_XML" をスキップ（フォーマットヘッダー）
  │    → "mxplot.metadata.format_header" をスキップ（フォーマットヘッダーの登録キー）
  │    → ユーザーメタデータ、History 等はコピー
  ▼
派生データ: クリーン — 古いフォーマットヘッダーなし
  │
  │  FITS で保存 → フォーマット間の交差汚染なし
  ▼
FITS 再読み込み → FITS_HEADER のみがフォーマットヘッダー
```

これにより、フォーマット変換後に OME-XML が FITS ファイルに混入する（またはその逆）**フォーマット間交差汚染**の問題を防止します。

---

## 処理履歴（UI 層）

> **スコープ**: この機能は `MxPlot.UI.Avalonia`（MatrixPlotter）内で完結しています。Core ライブラリは処理履歴の知識を持ちません。

### History エントリ形式

履歴はシステムキー `mxplot.data.history` に JSON 配列として格納されます:

```json
[
  {
    "op": "Crop",
    "at": "2026-04-12T15:30:00+09:00",
    "from": "Sample.ome.tif",
    "detail": "X=10 Y=20 W=100 H=100"
  },
  {
    "op": "Duplicate",
    "at": "2026-04-12T15:31:00+09:00",
    "from": "Crop of Sample.ome.tif"
  }
]
```

| フィールド | 必須 | 説明 |
|---|---|---|
| `op` | ✅ | 操作名（短い識別子） |
| `at` | ✅ | ISO 8601 タイムスタンプ（タイムゾーン付き） |
| `from` | ⚪ | ソースの MatrixPlotter ウィンドウタイトルまたはファイル名。新規作成の場合は `null` |
| `detail` | ⚪ | パラメータの人間可読な要約。パラメータなしの操作では `null` |

### 履歴の記録方法

`MatrixPlotter.AppendHistory()` は各処理の完了時に呼び出されます:

```csharp
// シグネチャ
internal static void AppendHistory(
    IMatrixData data,
    string operation,
    string? from,
    string? detail = null)
```

現在の呼び出し箇所:

| 呼び出し箇所 | `op` | `from` | `detail` |
|---|---|---|---|
| `ApplyCropResult` | `"Crop"` | ウィンドウタイトル | `"X=10 Y=20 W=100 H=100"`（単一フレームCrop時は `" (frame N)"` を付加） |
| `DuplicateWindowAsync` | `"Duplicate"` | ウィンドウタイトル | *(なし)* |
| `ConvertValueTypeAsync` | `"Convert Type"` | ウィンドウタイトル | `"float → double; scale [0, 255] → [0, 1]"` または `"float → int; direct cast"` |
| `ReverseStackAsync` | `"Reverse Stack"` | ウィンドウタイトル | `"all frames"` または `"axis: Z"` |

このメソッドの動作:
1. 既存の JSON 配列をパース（`CopyPropertiesFrom` 経由で継承された場合など）
2. 新しいエントリを末尾に追加
3. Metadata 辞書にシリアライズして書き戻す

### VisibleSystemKeys メカニズム

History キーは保護のために `mxplot.*` 名前空間を使用しますが、Metadata タブに**表示**する必要があります。これは `PlotterConfigKeys.VisibleSystemKeys` で実現されます:

```csharp
internal static class PlotterConfigKeys
{
    public const string Prefix = "mxplot.";

    // 内部キー → 表示名
    public static readonly Dictionary<string, string> VisibleSystemKeys = new()
    {
        ["mxplot.data.history"] = "History",
    };

    // mxplot.* キーは予約済み。ただし VisibleSystemKeys は除外
    public static bool IsReserved(string key) =>
        key.StartsWith(Prefix) && !VisibleSystemKeys.ContainsKey(key);
}
```

Metadata タブでは `🔒 History`（内部キーではなくマッピングの表示名）として表示されます。

**なぜ `MarkAsFormatHeader` を使わないのか？**

| | `MarkAsFormatHeader` | `mxplot.*` 名前空間 |
|---|---|---|
| ユーザーが同名キーを作成可能 | ⚠️ 可能（衝突リスク） | ❌ 不可能 |
| 保護メカニズム | Metadata 内の CSV | キー接頭辞ルール |
| 派生時のコピー（`CopyPropertiesFrom`） | ❌ されない（除外） | ✅ される |
| 適用対象 | IO ハンドラーのフォーマット固有情報（ファイル固有） | システム管理の内部データ |

---

## UI での動作まとめ

MatrixPlotter の Metadata タブは3カテゴリのキーを扱います:

```
MatrixData.Metadata
│
├── ユーザーキー: "user_note" = "..."
│   → そのまま表示、自由に編集可能
│
├── フォーマットヘッダーキー: "OME_XML" = "<OME>..."
│   → "🔒 OME_XML" として表示、閲覧のみ（TextBox.IsReadOnly）
│   → Delete / Save ボタン無効化
│   → 管理: mxplot.metadata.format_header の CSV
│   → CopyPropertiesFrom でコピーされない（派生時に除外）
│
├── 非表示システムキー: "mxplot.lut.min" = "0"
│   → リストに表示されない（PlotterConfigKeys.IsReserved = true）
│
└── 表示可能システムキー: "mxplot.data.history" = "[...]"
    → "🔒 History" として表示（VisibleSystemKeys の表示名）
    → 閲覧のみ、ユーザーは作成/削除不可
    → 管理: mxplot.* 名前空間 + VisibleSystemKeys 例外
```

`MatrixPlotter.InfoTab.cs` のヘルパーメソッド:

| メソッド | 役割 |
|---|---|
| `ResolveMetaKey(rawDisplayKey)` | `"🔒 History"` → `"mxplot.data.history"`（逆引き） |
| `IsDisplayKeyReadOnly(rawDisplayKey, data)` | 🔒 接頭辞付きキーまたはフォーマットヘッダーキーで `true` を返す |

---

## ファイル構成

```
MxPlot.Core/
├── MatrixData.Static.cs         ← MarkAsFormatHeader / IsFormatHeader / GetFormatHeaderKeys
│                                   （IMatrixData 拡張メソッド）
│                                   CopyPropertiesFrom — フォーマットヘッダーキーをスキップ
├── IO/
│   └── FitsHandler.cs           ← MarkAsFormatHeader("FITS_HEADER")
│
MxPlot.Extensions.Tiff/
│   └── OmeTiffHandler.cs        ← MarkAsFormatHeader("OME_XML")
│
MxPlot.UI.Avalonia/Views/
├── PlotterConfigKeys.cs         ← Prefix, VisibleSystemKeys, IsReserved()
├── MatrixPlotter.History.cs     ← HistoryMetaKey, AppendHistory()
└── MatrixPlotter.InfoTab.cs     ← ResolveMetaKey(), IsDisplayKeyReadOnly(), RefreshMetaTab()
```
