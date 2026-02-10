# GitDiff

Git リポジトリの2つのコミット間で変更されたファイルを抽出・エクスポートするWPFデスクトップアプリケーションです。

## 機能

### コミット間の差分比較
- Gitリポジトリを読み込み、コミット履歴を一覧表示
- BaseコミットとTargetコミットを選択して差分ファイルを抽出
- ファイルの変更状態（Added / Modified / Deleted / Renamed / Copied）を色分け表示

### フィルタリング
- **Committerフィルター** - 特定の作成者のコミットのみを対象に差分を抽出
- **Mergeコミット除外** - マージコミットを差分検出から除外
- **フォルダツリーフィルター** - フォルダ単位でエクスポート対象を選択

### ファイルエクスポート
- 変更されたファイルをTargetコミット時点の内容で出力フォルダにエクスポート
- ディレクトリ構造を維持して出力

### Diff ビューア
- ファイルをダブルクリックで詳細な差分ビューアを表示
- Unified / Side-by-Side の2つの表示モードを切替可能
- ファイル単位のコミット履歴をナビゲーション

### C0テストケース生成
- C#ファイルの変更メソッドを解析し、分岐条件（if / switch / case 等）を抽出
- C0カバレッジ用テストケースをTSV形式でクリップボードに出力

## スクリーンショット

<!-- スクリーンショットがあればここに追加 -->

## 必要環境

- Windows 10 以降
- .NET 9.0 Runtime

## ビルド

```bash
dotnet build
```

## 実行

```bash
dotnet run --project GitDiff
```

## 技術スタック

| カテゴリ | 技術 |
|---------|------|
| フレームワーク | WPF (.NET 9) |
| UI | [Material Design In XAML Toolkit](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit) |
| MVVM | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) |
| Git操作 | [LibGit2Sharp](https://github.com/libgit2/libgit2sharp) |

## プロジェクト構成

```
GitDiff/
├── Models/          # データモデル (CommitInfo, DiffFileInfo, C0TestCase 等)
├── Services/        # ビジネスロジック (GitService, FileExportService, C0CaseService)
├── ViewModels/      # ViewModel (MainViewModel, DiffViewerViewModel)
├── Views/           # ウィンドウ (DiffViewerWindow)
├── Converters/      # 値コンバーター
├── MainWindow.xaml  # メインウィンドウ
└── App.xaml         # アプリケーション設定
```

## ライセンス

MIT
