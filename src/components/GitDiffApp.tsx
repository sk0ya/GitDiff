import React, { useEffect, useState } from "react";
import git from "isomorphic-git";
import type { WalkerEntry, ReadCommitResult } from "isomorphic-git";
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
  const [repoFolderName, setRepoFolderName] = useState<string>("");
  const [currentBranch, setCurrentBranch] = useState<string>("");

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
      console.log(`.gitフォルダ読み込み開始: ${files.length} ファイル`);

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

      // リポジトリフォルダ名を取得（末尾のスラッシュを除去）
      const folderName = prefix.slice(0, -1) || "リポジトリ";
      setRepoFolderName(folderName);

      // .gitフォルダ内にHEADファイルがあるか確認
      const hasHeadFile = files.some(file =>
        file.webkitRelativePath === prefix + '.git/HEAD'
      );
      if (!hasHeadFile) {
        throw new Error('有効な.gitフォルダではありません（HEADファイルが見つかりません）');
      }

      // 必要なファイルのみをフィルタリング（コミット履歴とdiffに必要な最小限）
      const filteredFiles = files.filter(file => {
        const relativePath = file.webkitRelativePath;
        // プレフィックスを除いた.git以降のパスを取得
        const gitPath = relativePath.substring(prefix.length);

        // 必要なファイルのみを許可リストで指定
        // - HEAD: 現在のブランチ参照
        // - config: リポジトリ設定
        // - refs/**: ブランチとタグの参照
        // - packed-refs: パックされた参照
        // - objects/**: コミット、ツリー、ブロブオブジェクト

        if (gitPath === '.git/HEAD' ||
            gitPath === '.git/config' ||
            gitPath === '.git/packed-refs') {
          return true;
        }

        if (gitPath.startsWith('.git/refs/')) {
          return true;
        }

        if (gitPath.startsWith('.git/objects/')) {
          // objects内でも不要なものを除外
          // info/や pack/*.idx などは除外可能
          if (gitPath.startsWith('.git/objects/info/')) {
            return false;
          }
          return true;
        }

        return false;
      });

      console.log(`必要なファイル: ${filteredFiles.length}/${files.length} (${Math.round(filteredFiles.length/files.length*100)}%)`);

      // 必要なディレクトリを事前に収集
      const directories = new Set<string>();
      const filePaths: Array<{ file: File; path: string }> = [];

      for (const file of filteredFiles) {
        let path = file.webkitRelativePath;
        if (path.startsWith(prefix)) {
          path = path.substring(prefix.length);
        }
        path = '/repo/' + path;

        // ディレクトリパスを収集
        const dirPath = path.substring(0, path.lastIndexOf('/'));
        const dirs = dirPath.split('/').filter(Boolean);
        let currentPath = '';
        for (const d of dirs) {
          currentPath += '/' + d;
          directories.add(currentPath);
        }

        filePaths.push({ file, path });
      }

      // filteredFilesを解放（もう使わない）
      (filteredFiles as any).length = 0;

      // ディレクトリを事前に一括作成
      console.log(`ディレクトリ作成中: ${directories.size} 個`);
      const sortedDirs = Array.from(directories).sort();
      for (const dir of sortedDirs) {
        try {
          await fs.mkdir(dir);
        } catch (e) {
          // ディレクトリが既に存在する場合は無視
        }
      }

      // ファイルを並列処理（バッチサイズ200で制御）
      const BATCH_SIZE = 200;
      console.log(`.gitファイル書き込み中: ${filePaths.length} ファイル`);

      for (let i = 0; i < filePaths.length; i += BATCH_SIZE) {
        const batch = filePaths.slice(i, i + BATCH_SIZE);
        await Promise.all(
          batch.map(async ({ file, path }) => {
            const content = await file.arrayBuffer();
            await fs.writeFile(path, new Uint8Array(content));
            // ArrayBufferを明示的に解放
            return null;
          })
        );
        console.log(`書き込み進捗: ${Math.min(i + BATCH_SIZE, filePaths.length)}/${filePaths.length}`);

        // バッチ処理後にGCを促す（ブラウザ依存だが試す価値あり）
        if (i % (BATCH_SIZE * 5) === 0 && typeof (globalThis as any).gc === 'function') {
          (globalThis as any).gc();
        }
      }

      // 全てのファイル参照を解放
      filePaths.length = 0;

      console.log('.gitファイル読み込み完了、コミット履歴を解析中...');

      // HEADファイルからブランチ名を取得
      try {
        const headContent = await fs.readFile('/repo/.git/HEAD', { encoding: 'utf8' });
        const headStr = typeof headContent === 'string' ? headContent : new TextDecoder().decode(headContent as Uint8Array);
        console.log('HEAD内容:', headStr);

        // "ref: refs/heads/main" の形式から "main" を抽出
        const match = headStr.trim().match(/^ref: refs\/heads\/(.+)$/);
        if (match) {
          setCurrentBranch(match[1]);
        } else {
          // detached HEADの場合
          setCurrentBranch('(detached HEAD)');
        }
      } catch (e) {
        console.error('ブランチ名の取得に失敗:', e);
        setCurrentBranch('(不明)');
      }

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

    // 1つのZIPファイルに変更前と変更後を含める（差分があるファイルのみ）
    const diffZip = new JSZip();

    // 変更前フォルダを作成
    const oldFolder = diffZip.folder("変更前");
    if (!oldFolder) throw new Error("Failed to create old folder");

    // 変更後フォルダを作成
    const newFolder = diffZip.folder("変更後");
    if (!newFolder) throw new Error("Failed to create new folder");

    // 差分があるファイルのみをZIPに追加
    for (const item of valid) {
      const filepath = item.path;

      // 変更前のファイルを取得（削除されていない場合）
      try {
        const oldBlob = await git.readBlob({
          fs,
          dir,
          oid: oldCommit,
          filepath,
        });
        oldFolder.file(filepath, oldBlob.blob);
      } catch (e) {
        // ファイルが存在しない（追加されたファイル）場合はスキップ
      }

      // 変更後のファイルを取得（削除されていない場合）
      try {
        const newBlob = await git.readBlob({
          fs,
          dir,
          oid: newCommit,
          filepath,
        });
        newFolder.file(filepath, newBlob.blob);
      } catch (e) {
        // ファイルが存在しない（削除されたファイル）場合はスキップ
      }
    }

    // 1つのZIPファイルとしてダウンロード
    saveAs(await diffZip.generateAsync({ type: "blob" }), "diff.zip");
  }

  return (
    <div className="max-w-7xl mx-auto p-4">
      <div className="flex items-center justify-between mb-3 text-sm">
        <h1 className="text-lg font-bold text-gray-800">Git Diff ZIP Exporter</h1>
        <div className="flex items-center gap-3">
          <input
            type="file"
            // @ts-ignore - webkitdirectory is not in the type definition
            webkitdirectory=""
            directory=""
            onChange={handleDirectoryUpload}
            className="text-sm border border-gray-300 rounded px-3 py-1 focus:ring-1 focus:ring-blue-500 outline-none file:mr-2 file:py-0.5 file:px-2 file:rounded file:border-0 file:text-xs file:font-medium file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100"
          />
          {commits.length > 0 && (
            <>
              <div className="h-4 w-px bg-gray-300"></div>
              <span className="text-gray-500">リポジトリ: <span className="font-semibold text-gray-800">{repoFolderName}</span></span>
              <div className="h-4 w-px bg-gray-300"></div>
              <span className="text-gray-500">ブランチ: <span className="px-1.5 py-0.5 rounded bg-emerald-100 text-emerald-700 font-medium">{currentBranch}</span></span>
            </>
          )}
        </div>
      </div>

      {loading && (
        <div className="mb-3 p-2 bg-blue-50 border-l-2 border-blue-500 text-blue-700 text-sm flex items-center gap-2">
          <svg className="animate-spin h-4 w-4" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
          </svg>
          読み込み中...
        </div>
      )}

      {commits.length === 0 && !loading && (
        <p className="text-sm text-gray-500">リポジトリの .git フォルダを選択してください</p>
      )}

      {commits.length > 0 && (
        <>

          <div className="bg-white border border-gray-200 rounded overflow-hidden mb-3">
            <div className="flex items-center justify-between bg-gray-50 border-b border-gray-200 px-3 py-2">
              <h2 className="text-sm font-semibold text-gray-700">コミット履歴 - 比較したい2つのコミットを選択してください</h2>
              {selectedCommits.length > 0 && (
                <span className="px-2 py-0.5 bg-blue-100 text-blue-700 rounded text-xs font-semibold">
                  {selectedCommits.length}/2 選択中
                </span>
              )}
            </div>
            <div className="overflow-hidden">
              <div className="bg-gray-100 grid grid-cols-11 gap-2 px-3 py-1.5 text-xs text-gray-600 font-medium border-b border-gray-200">
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
                      className={`grid grid-cols-11 gap-2 px-3 py-1.5 border-b last:border-b-0 hover:bg-gray-50 transition cursor-pointer text-sm ${
                        isSelected
                          ? isOld
                            ? 'bg-blue-50 border-l-2 border-l-blue-500'
                            : isNew
                            ? 'bg-emerald-50 border-l-2 border-l-emerald-500'
                            : 'bg-gray-50 border-l-2 border-l-gray-400'
                          : ''
                      }`}
                    >
                      <div className="col-span-1 flex justify-center items-center">
                        <div className="relative">
                          <input
                            type="checkbox"
                            checked={isSelected}
                            onChange={() => handleCommitToggle(c.oid)}
                            className="w-3.5 h-3.5 cursor-pointer"
                            onClick={(e) => e.stopPropagation()}
                          />
                          {isSelected && selectionLabel && (
                            <span className={`absolute -top-1 -right-6 text-[10px] font-bold px-1 py-0.5 rounded ${
                              isOld ? 'bg-blue-500 text-white' : 'bg-emerald-500 text-white'
                            }`}>
                              {selectionLabel}
                            </span>
                          )}
                        </div>
                      </div>
                      <div className="col-span-5 truncate" title={c.commit.message}>
                        {c.commit.message.split('\n')[0]}
                      </div>
                      <div className="col-span-2 text-gray-600 truncate" title={c.commit.author.name}>
                        {c.commit.author.name}
                      </div>
                      <div className="col-span-2 text-gray-600 text-xs">
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

          <div className="flex items-center justify-between bg-white border border-gray-200 rounded px-3 py-2 mb-3">
            {selectedCommits.length === 2 ? (() => {
              const commit1 = commits.find(c => c.oid === selectedCommits[0]);
              const commit2 = commits.find(c => c.oid === selectedCommits[1]);

              if (!commit1 || !commit2) return null;

              const [oldCommit, newCommit] = commit1.commit.author.timestamp < commit2.commit.author.timestamp
                ? [commit1, commit2]
                : [commit2, commit1];

              return (
                <div className="flex items-center gap-2 text-sm">
                  <span className="text-xs px-1.5 py-0.5 bg-blue-500 text-white rounded font-bold">OLD</span>
                  <span className="text-gray-700 truncate max-w-xs">{oldCommit.commit.message.split('\n')[0]}</span>
                  <span className="text-gray-400">→</span>
                  <span className="text-xs px-1.5 py-0.5 bg-emerald-500 text-white rounded font-bold">NEW</span>
                  <span className="text-gray-700 truncate max-w-xs">{newCommit.commit.message.split('\n')[0]}</span>
                </div>
              );
            })() : <span className="text-sm text-gray-500">2つのコミットを選択してください</span>}
            <button
              onClick={handleDiff}
              disabled={selectedCommits.length !== 2}
              className={`px-4 py-1.5 rounded font-medium text-sm transition flex items-center gap-1.5 ${
                selectedCommits.length === 2
                  ? 'bg-blue-600 text-white hover:bg-blue-700'
                  : 'bg-gray-200 text-gray-400 cursor-not-allowed'
              }`}
            >
              ZIP出力
            </button>
          </div>

          {diff.length > 0 && (
            <div className="bg-white border border-gray-200 rounded overflow-hidden">
              <div className="flex items-center gap-2 bg-gray-50 border-b border-gray-200 px-3 py-1.5">
                <h3 className="text-sm font-semibold text-gray-700">変更されたファイル</h3>
                <span className="px-1.5 py-0.5 rounded bg-blue-100 text-blue-700 text-xs font-bold">
                  {diff.length}
                </span>
              </div>
              <div className="max-h-64 overflow-y-auto">
                {diff.map((d) => (
                  <div key={d.path} className="border-b last:border-b-0 px-3 py-1.5 hover:bg-gray-50 transition">
                    <div className="flex items-center gap-2 text-sm">
                      <span className={`px-1.5 py-0.5 rounded text-xs font-semibold ${
                        d.status === 'modified'
                          ? 'bg-amber-100 text-amber-700'
                          : 'bg-blue-100 text-blue-700'
                      }`}>
                        {d.status}
                      </span>
                      <span className="text-gray-700 font-mono">{d.path}</span>
                    </div>
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
