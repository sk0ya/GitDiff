# Git Diff ZIP Exporter

React + Viteで作成されたGit差分ビューアーおよびZIPエクスポーターです。

## 機能

- ブラウザ内でGitリポジトリをクローン
- 2つのコミットを選択して差分を表示
- 選択した各コミットのファイルをZIPとしてダウンロード

## セットアップ

```bash
# 依存関係をインストール
npm install

# 開発サーバーを起動
npm run dev

# ビルド
npm run build

# プレビュー
npm run preview
```

## 使用技術

- React 18
- Vite
- Tailwind CSS
- isomorphic-git
- LightningFS
- JSZip
- file-saver
