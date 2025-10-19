import React, { useEffect, useState } from "react";
import git from "isomorphic-git";
import type { WalkerEntry, TreeEntry, ReadCommitResult } from "isomorphic-git";
import LightningFS from "@isomorphic-git/lightning-fs";
import JSZip from "jszip";
import { saveAs } from "file-saver";

type PromisifiedFS = InstanceType<typeof LightningFS>['promises'];

interface DiffItem {
  path: string;
  status: string;
}

export default function GitDiffApp() {
  const [fs, setFs] = useState<PromisifiedFS | null>(null);
  const [commits, setCommits] = useState<ReadCommitResult[]>([]);
  const [selectedCommits, setSelectedCommits] = useState<string[]>([]);
  const [diff, setDiff] = useState<DiffItem[]>([]);
  const [loading, setLoading] = useState(false);

  // 初期化
  useEffect(() => {
    const fs = new LightningFS("fs");
    const pfs = fs.promises;
    setFs(pfs);
  }, []);

  // ディレクトリをアップロードして読み込む
  async function handleDirectoryUpload(e: React.ChangeEvent<HTMLInputElement>) {
    if (!e.target.files || !fs) return;

    setLoading(true);
    const dir = "/repo";

    try {
      // .gitディレクトリ内のファイルをLightningFSにコピー
      const files = Array.from(e.target.files);
      console.log(`アップロード開始: ${files.length} ファイル`);

      if (files.length === 0) {
        throw new Error('ファイルが選択されていません');
      }

      // 最初のファイルのパスから基準ディレクトリを取得
      // 例: "GitDiff/.git/HEAD" → "GitDiff/.git" を除去して "/repo/.git" にマッピング
      const firstPath = files[0].webkitRelativePath;
      console.log('最初のファイルパス:', firstPath);

      // ".git" の位置を見つける
      const gitIndex = firstPath.indexOf('.git');
      if (gitIndex === -1) {
        throw new Error('.gitディレクトリを選択してください');
      }

      // プレフィックスを取得（例: "GitDiff/" の部分）
      const prefix = firstPath.substring(0, gitIndex);
      console.log('プレフィックス:', prefix);

      for (let i = 0; i < files.length; i++) {
        const file = files[i];
        let path = file.webkitRelativePath;

        // プレフィックスを削除して /repo 配下に配置
        // 例: "GitDiff/.git/HEAD" → "/repo/.git/HEAD"
        if (path.startsWith(prefix)) {
          path = path.substring(prefix.length);
        }
        path = '/repo/' + path;

        if (i % 100 === 0) {
          console.log(`処理中: ${i}/${files.length} - ${path}`);
        }

        const dirPath = path.substring(0, path.lastIndexOf('/'));

        // ディレクトリを作成
        const dirs = dirPath.split('/').filter(Boolean);
        let currentPath = '';
        for (const d of dirs) {
          currentPath += '/' + d;
          try {
            await fs.mkdir(currentPath);
          } catch (e) {
            // ディレクトリが既に存在する場合は無視
          }
        }

        // ファイルを読み込んで書き込む
        const content = await file.arrayBuffer();
        await fs.writeFile(path, new Uint8Array(content));
      }

      console.log('ファイルコピー完了、コミット履歴を取得中...');
      console.log('dirパス:', dir);

      // コミット履歴を取得
      const logs = await git.log({ fs, dir });
      console.log(`コミット履歴取得完了: ${logs.length} コミット`);
      setCommits(logs);
    } catch (e: any) {
      alert("エラー: " + e.message);
      console.error(e);
    } finally {
      setLoading(false);
    }
  }

  // コミット選択のハンドラー
  function handleCommitToggle(oid: string) {
    setSelectedCommits(prev => {
      if (prev.includes(oid)) {
        // 既に選択されている場合は解除
        return prev.filter(id => id !== oid);
      } else {
        // 新しく選択する場合
        if (prev.length >= 2) {
          // 既に2つ選択されている場合は、最初に選択したものを削除
          return [...prev.slice(1), oid];
        } else {
          return [...prev, oid];
        }
      }
    });
  }

  async function handleDiff(): Promise<void> {
    if (selectedCommits.length !== 2) {
      alert("2つのコミットを選んでください！");
      return;
    }
    if (!fs) return;

    const dir = "/repo";
    // コミットの時系列で判定（タイムスタンプが古い方がold）
    const commit1 = commits.find(c => c.oid === selectedCommits[0]);
    const commit2 = commits.find(c => c.oid === selectedCommits[1]);

    if (!commit1 || !commit2) return;

    const [oldCommit, newCommit] = commit1.commit.author.timestamp < commit2.commit.author.timestamp
      ? [selectedCommits[0], selectedCommits[1]]
      : [selectedCommits[1], selectedCommits[0]];

    const changes = await git.walk({
      fs,
      dir,
      trees: [
        git.TREE({ ref: oldCommit }),
        git.TREE({ ref: newCommit }),
      ],
      map: async (filepath: string, entries: (WalkerEntry | null)[]): Promise<DiffItem | undefined> => {
        if (!filepath) return undefined;
        const [a, b] = entries;
        const aType = await a?.type();
        const bType = await b?.type();
        if (aType === "blob" || bType === "blob") {
          const aContent = await a?.content();
          const bContent = await b?.content();
          if (!aContent || !bContent)
            return { path: filepath, status: "added/removed" };
          if (aContent.toString() !== bContent.toString())
            return { path: filepath, status: "modified" };
        }
        return undefined;
      },
    });

    const valid = changes.filter((item: DiffItem | undefined): item is DiffItem => item !== undefined && item !== null);
    setDiff(valid);

    // ZIPを生成してダウンロード
    const oldZip = await createZip(oldCommit);
    const newZip = await createZip(newCommit);
    saveAs(await oldZip.generateAsync({ type: "blob" }), "old_version.zip");
    saveAs(await newZip.generateAsync({ type: "blob" }), "new_version.zip");
  }

  async function createZip(oid: string): Promise<JSZip> {
    if (!fs) throw new Error("FS not initialized");

    const zip = new JSZip();
    const dir = "/repo";
    const tree = await git.readTree({ fs, dir, oid });
    const filesFolder = zip.folder("files");
    if (!filesFolder) throw new Error("Failed to create folder");

    await addTreeToZip(dir, tree.tree, filesFolder);
    return zip;
  }

  async function addTreeToZip(baseDir: string, treeEntries: TreeEntry[], zipFolder: JSZip): Promise<void> {
    if (!fs) return;

    for (const entry of treeEntries) {
      if (entry.type === "tree") {
        const subtree = await git.readTree({
          fs,
          dir: baseDir,
          oid: entry.oid,
        });
        const subFolder = zipFolder.folder(entry.path);
        if (subFolder) {
          await addTreeToZip(baseDir, subtree.tree, subFolder);
        }
      } else if (entry.type === "blob") {
        const blob = await git.readBlob({
          fs,
          dir: baseDir,
          oid: entry.oid,
        });
        zipFolder.file(entry.path, blob.blob);
      }
    }
  }

  return (
    <div className="p-6">
      <h1 className="text-2xl font-bold mb-4">Git Diff ZIP Exporter</h1>

      <div className="mb-6">
        <label className="block mb-2 font-semibold">
          リポジトリの .git ディレクトリを選択:
        </label>
        <input
          type="file"
          // @ts-ignore - webkitdirectory is not in the type definition
          webkitdirectory=""
          directory=""
          onChange={handleDirectoryUpload}
          className="border p-2"
        />
        {loading && <p className="mt-2 text-blue-600">読み込み中...</p>}
      </div>

      {commits.length === 0 && !loading && (
        <p className="text-gray-500">リポジトリの .git フォルダを選択してください</p>
      )}

      {commits.length > 0 && (
        <>
          <div className="mb-4">
            <h2 className="text-lg font-semibold mb-2">コミット履歴</h2>
            <p className="text-sm text-gray-600 mb-3">
              比較したい2つのコミットを選択してください
              {selectedCommits.length > 0 && (
                <span className="ml-2 font-semibold text-blue-600">
                  ({selectedCommits.length}/2 選択中)
                </span>
              )}
            </p>
            <div className="border rounded-lg overflow-hidden">
              <div className="bg-gray-100 grid grid-cols-11 gap-2 p-3 font-semibold text-sm border-b">
                <div className="col-span-1 text-center">選択</div>
                <div className="col-span-5">コミットメッセージ</div>
                <div className="col-span-2">作成者</div>
                <div className="col-span-2">日時</div>
                <div className="col-span-1">ハッシュ</div>
              </div>
              <div className="max-h-96 overflow-y-auto">
                {commits.map((c) => {
                  const isSelected = selectedCommits.includes(c.oid);

                  // 選択されている場合、タイムスタンプでOld/Newを判定
                  let selectionLabel = '';
                  let isOld = false;
                  let isNew = false;

                  if (isSelected && selectedCommits.length === 2) {
                    const commit1 = commits.find(commit => commit.oid === selectedCommits[0]);
                    const commit2 = commits.find(commit => commit.oid === selectedCommits[1]);

                    if (commit1 && commit2) {
                      const olderOid = commit1.commit.author.timestamp < commit2.commit.author.timestamp
                        ? selectedCommits[0]
                        : selectedCommits[1];
                      const newerOid = olderOid === selectedCommits[0] ? selectedCommits[1] : selectedCommits[0];

                      if (c.oid === olderOid) {
                        selectionLabel = 'Old';
                        isOld = true;
                      } else if (c.oid === newerOid) {
                        selectionLabel = 'New';
                        isNew = true;
                      }
                    }
                  }

                  const date = new Date(c.commit.author.timestamp * 1000);

                  return (
                    <div
                      key={c.oid}
                      onClick={() => handleCommitToggle(c.oid)}
                      className={`grid grid-cols-11 gap-2 p-3 border-b hover:bg-gray-50 transition-colors cursor-pointer ${
                        isSelected
                          ? isOld
                            ? 'bg-blue-100 border-l-4 border-l-blue-600'
                            : isNew
                            ? 'bg-green-100 border-l-4 border-l-green-600'
                            : 'bg-gray-100 border-l-4 border-l-gray-400'
                          : ''
                      }`}
                    >
                      <div className="col-span-1 flex justify-center items-center">
                        <div className="relative">
                          <input
                            type="checkbox"
                            checked={isSelected}
                            onChange={() => handleCommitToggle(c.oid)}
                            className="w-5 h-5 cursor-pointer"
                            onClick={(e) => e.stopPropagation()}
                          />
                          {isSelected && selectionLabel && (
                            <span className={`absolute -top-1 -right-6 text-xs font-bold px-1 rounded ${
                              isOld ? 'text-blue-600' : 'text-green-600'
                            }`}>
                              {selectionLabel}
                            </span>
                          )}
                        </div>
                      </div>
                      <div className="col-span-5 text-sm">
                        <div className="font-medium truncate" title={c.commit.message}>
                          {c.commit.message.split('\n')[0]}
                        </div>
                      </div>
                      <div className="col-span-2 text-sm text-gray-600 truncate" title={c.commit.author.name}>
                        {c.commit.author.name}
                      </div>
                      <div className="col-span-2 text-sm text-gray-600">
                        {date.toLocaleString('ja-JP', {
                          year: 'numeric',
                          month: '2-digit',
                          day: '2-digit',
                          hour: '2-digit',
                          minute: '2-digit'
                        })}
                      </div>
                      <div className="col-span-1 text-xs text-gray-500 font-mono truncate" title={c.oid}>
                        {c.oid.slice(0, 7)}
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>

          <div className="flex items-center gap-4 mb-6">
            <button
              onClick={handleDiff}
              disabled={selectedCommits.length !== 2}
              className={`px-6 py-3 rounded-lg font-semibold transition-colors ${
                selectedCommits.length === 2
                  ? 'bg-blue-600 text-white hover:bg-blue-700'
                  : 'bg-gray-300 text-gray-500 cursor-not-allowed'
              }`}
            >
              差分を表示して ZIP 出力
            </button>
            {selectedCommits.length === 2 && (() => {
              const commit1 = commits.find(c => c.oid === selectedCommits[0]);
              const commit2 = commits.find(c => c.oid === selectedCommits[1]);

              if (!commit1 || !commit2) return null;

              const [oldCommit, newCommit] = commit1.commit.author.timestamp < commit2.commit.author.timestamp
                ? [commit1, commit2]
                : [commit2, commit1];

              return (
                <div className="text-sm text-gray-600">
                  <span className="font-semibold text-blue-600">Old: {oldCommit.commit.message.split('\n')[0].slice(0, 25)}...</span>
                  {' → '}
                  <span className="font-semibold text-green-600">New: {newCommit.commit.message.split('\n')[0].slice(0, 25)}...</span>
                </div>
              );
            })()}
          </div>

          {diff.length > 0 && (
            <div className="mt-6">
              <h3 className="font-semibold mb-2">変更されたファイル ({diff.length}件):</h3>
              <div className="border rounded-lg max-h-64 overflow-y-auto">
                {diff.map((d) => (
                  <div key={d.path} className="border-b last:border-b-0 p-2 hover:bg-gray-50">
                    <span className={`inline-block px-2 py-1 rounded text-xs font-semibold mr-2 ${
                      d.status === 'modified' ? 'bg-yellow-100 text-yellow-800' : 'bg-blue-100 text-blue-800'
                    }`}>
                      {d.status}
                    </span>
                    <span className="text-sm">{d.path}</span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
}
