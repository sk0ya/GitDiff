import React, { useEffect, useRef, useState } from "react";
import git from "isomorphic-git";
import type { WalkerEntry, ReadCommitResult } from "isomorphic-git";
import LightningFS from "@isomorphic-git/lightning-fs";
import JSZip from "jszip";
import { saveAs } from "file-saver";
import { createFsAccessAdapter } from "../lib/fsAccessAdapter";

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
  const [zipping, setZipping] = useState(false);
  const [zipProgress, setZipProgress] = useState<number>(0);
  const [diffing, setDiffing] = useState(false);
  const [repoFolderName, setRepoFolderName] = useState<string>("");
  const [currentBranch, setCurrentBranch] = useState<string>("");
  const [logDepth, setLogDepth] = useState<number>(200);
  const [loadingMore, setLoadingMore] = useState<boolean>(false);
  const listRef = useRef<HTMLDivElement | null>(null);
  const [listScrollTop, setListScrollTop] = useState(0);
  const [listHeight, setListHeight] = useState(384); // default ~max-h-96
  const GIT_CACHE = useRef<Record<string, any>>({});

  // tunables
  const INITIAL_LOG_DEPTH = 200;
  const LOG_PAGE_SIZE = 200;
  const ZIP_READ_CONCURRENCY = 12; // 同時に読み出す変更ファイル数（6→12に増加）
  const MAX_ZIP_FILE_BYTES = 25 * 1024 * 1024; // 1ファイルの上限（25MB）
  const MAX_ZIP_FILES = 2000; // 変更ファイルが多すぎる場合の制限
  const ROW_HEIGHT = 36; // コミット行の仮想化固定高さ(px)

  useEffect(() => {
    const el = listRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setListHeight(el.clientHeight));
    ro.observe(el);
    setListHeight(el.clientHeight);
    return () => ro.disconnect();
  }, []);

  // 初期化
  useEffect(() => {
    const fs = new LightningFS("fs");
    const pfs = fs.promises;
    setFs(pfs);
  }, []);

  // File System Access APIを使用してディレクトリを読み込む（直接読み取りアダプタ採用）
  async function handleDirectoryPickerClick() {

    // File System Access API対応チェック
    if (!('showDirectoryPicker' in window)) {
      alert('このブラウザはFile System Access APIに対応していません。Chromeなどのモダンブラウザをご使用ください。');
      return;
    }

    setLoading(true);
    const dir = "/repo";

    try {
      // フォルダピッカーを表示（.gitフォルダを選択してもらう）
      const dirHandle = await (window as any).showDirectoryPicker({
        mode: 'read',
      });

      console.log('選択されたフォルダ:', dirHandle.name);

      // フォルダ名を設定（.gitフォルダの親フォルダ名を取得したいが、APIの制限で直接取得できないため、選択されたフォルダ名を使用）
      setRepoFolderName(dirHandle.name === '.git' ? 'リポジトリ' : dirHandle.name);

      // .git フォルダかどうかを確認（HEADファイルの存在チェック）
      let isGitFolder = false;
      try {
        await dirHandle.getFileHandle('HEAD');
        isGitFolder = true;
      } catch {
        // HEADファイルが見つからない場合は .git サブフォルダを探す
      }

      const gitDirHandle = isGitFolder ? dirHandle : await dirHandle.getDirectoryHandle('.git');
      // 直接読み取り用のfsアダプタを用意
      const directFs = createFsAccessAdapter(gitDirHandle) as any;
      setFs(directFs);
      console.log('.git 直接読み取りモードで履歴を解析中...');

      // HEADファイルからブランチ名を取得
      try {
        const headContent = await directFs.readFile('/repo/.git/HEAD', { encoding: 'utf8' });
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

      // コミット履歴を取得（初期は限定深さ）
      const logs = await git.log({ fs: directFs, dir, depth: INITIAL_LOG_DEPTH });
      console.log(`コミット履歴取得完了: ${logs.length} コミット`);
      setCommits(logs);
      setLogDepth(INITIAL_LOG_DEPTH);
    } catch (e: any) {
      if (e.name === 'AbortError') {
        console.log('ユーザーがキャンセルしました');
      } else {
        alert("エラー: " + e.message);
        console.error(e);
      }
    } finally {
      setLoading(false);
    }
  }

  // レガシーブラウザ向けのフォールバック（従来のinput[type=file]方式）
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
      const firstPath = files[0].webkitRelativePath;
      console.log('最初のファイルパス:', firstPath);

      // ".git" の位置を見つける
      const gitIndex = firstPath.indexOf('.git');
      if (gitIndex === -1) {
        throw new Error('.gitディレクトリを選択してください');
      }

      // プレフィックスを取得
      const prefix = firstPath.substring(0, gitIndex);
      console.log('プレフィックス:', prefix);

      // リポジトリフォルダ名を取得
      const folderName = prefix.slice(0, -1) || "リポジトリ";
      setRepoFolderName(folderName);

      // .gitフォルダ内にHEADファイルがあるか確認
      const hasHeadFile = files.some(file =>
        file.webkitRelativePath === prefix + '.git/HEAD'
      );
      if (!hasHeadFile) {
        throw new Error('有効な.gitフォルダではありません（HEADファイルが見つかりません）');
      }

      // 必要なファイルのみをフィルタリング
      const filteredFiles = files.filter(file => {
        const relativePath = file.webkitRelativePath;
        const gitPath = relativePath.substring(prefix.length);

        if (gitPath === '.git/HEAD' ||
            gitPath === '.git/config' ||
            gitPath === '.git/packed-refs') {
          return true;
        }

        if (gitPath.startsWith('.git/refs/')) {
          return true;
        }

        if (gitPath.startsWith('.git/objects/')) {
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

      // ファイルを並列処理
      const BATCH_SIZE = 200;
      console.log(`.gitファイル書き込み中: ${filePaths.length} ファイル`);

      for (let i = 0; i < filePaths.length; i += BATCH_SIZE) {
        const batch = filePaths.slice(i, i + BATCH_SIZE);
        await Promise.all(
          batch.map(async ({ file, path }) => {
            const content = await file.arrayBuffer();
            await fs.writeFile(path, new Uint8Array(content));
            return null;
          })
        );
        console.log(`書き込み進捗: ${Math.min(i + BATCH_SIZE, filePaths.length)}/${filePaths.length}`);
      }

      filePaths.length = 0;

      console.log('.gitファイル読み込み完了、コミット履歴を解析中...');

      // HEADファイルからブランチ名を取得
      try {
        const headContent = await fs.readFile('/repo/.git/HEAD', { encoding: 'utf8' });
        const headStr = typeof headContent === 'string' ? headContent : new TextDecoder().decode(headContent as Uint8Array);
        console.log('HEAD内容:', headStr);

        const match = headStr.trim().match(/^ref: refs\/heads\/(.+)$/);
        if (match) {
          setCurrentBranch(match[1]);
        } else {
          setCurrentBranch('(detached HEAD)');
        }
      } catch (e) {
        console.error('ブランチ名の取得に失敗:', e);
        setCurrentBranch('(不明)');
      }

      // コミット履歴を取得（初期は限定深さ）
      const logs = await git.log({ fs, dir, depth: INITIAL_LOG_DEPTH });
      console.log(`コミット履歴取得完了: ${logs.length} コミット`);
      setCommits(logs);
      setLogDepth(INITIAL_LOG_DEPTH);
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

    setDiffing(true);
    console.time('diff_walk');
    const changes = await git.walk({
      fs,
      dir,
      cache: GIT_CACHE.current,
      trees: [
        git.TREE({ ref: oldCommit }),
        git.TREE({ ref: newCommit }),
      ],
      map: async (filepath: string, entries: (WalkerEntry | null)[]): Promise<DiffItem | undefined> => {
        if (!filepath) return undefined;
        const [a, b] = entries;

        // 早期リターン: 両方nullなら無視
        if (!a && !b) return undefined;

        // type()を先に取得して、blob以外は早期リターン
        const [aType, bType] = await Promise.all([
          a?.type(),
          b?.type()
        ]);

        // ディレクトリは無視（高速化）
        if (aType === 'tree' || bType === 'tree') return undefined;

        // 片方のみblob（追加/削除）
        if ((aType === 'blob' && bType !== 'blob') || (aType !== 'blob' && bType === 'blob')) {
          const status = !aType || aType !== 'blob' ? 'added' : 'deleted';
          return { path: filepath, status };
        }

        // 両方blobの場合のみOID比較
        if (aType === 'blob' && bType === 'blob') {
          const [aOid, bOid] = await Promise.all([
            a!.oid(),
            b!.oid()
          ]);

          if (aOid !== bOid) {
            return { path: filepath, status: 'modified' };
          }
        }

        return undefined;
      },
    });
    console.timeEnd('diff_walk');
    console.log('walk paths:', changes.length);

    let valid = changes.filter((item: DiffItem | undefined): item is DiffItem => item !== undefined && item !== null);
    console.log('diff entries:', valid.length);
    setDiff(valid);
    setDiffing(false);

    // 1つのZIPファイルに変更前と変更後を含める（差分があるファイルのみ）
    const diffZip = new JSZip();

    // 変更前フォルダを作成
    const oldFolder = diffZip.folder("変更前");
    if (!oldFolder) throw new Error("Failed to create old folder");

    // 変更後フォルダを作成
    const newFolder = diffZip.folder("変更後");
    if (!newFolder) throw new Error("Failed to create new folder");

    // 多すぎる場合は上限に丸める
    if (valid.length > MAX_ZIP_FILES) {
      alert(`変更ファイル数が多いため、先頭${MAX_ZIP_FILES}件のみZIPに含めます（${valid.length}件中）。`);
      valid = valid.slice(0, MAX_ZIP_FILES);
    }

    // Start ZIP progress UI
    setZipping(true);
    setZipProgress(0);
    

    // 差分があるファイルのみをZIPに追加（並列数を制御）
    console.time('blob_read');
    for (let i = 0; i < valid.length; i += ZIP_READ_CONCURRENCY) {
      const batch = valid.slice(i, i + ZIP_READ_CONCURRENCY);
      await Promise.all(
        batch.map(async (item: DiffItem) => {
          const filepath = item.path;
          const isAdded = item.status === 'added';
          const isDeleted = item.status === 'deleted';

          // 変更前（削除されたファイルのみ、または変更されたファイル）
          if (!isAdded) {
            try {
              const oldBlob = await git.readBlob({ fs, dir, cache: GIT_CACHE.current, oid: oldCommit, filepath });
              const size = (oldBlob.blob as Uint8Array).byteLength ?? 0;
              if (size <= MAX_ZIP_FILE_BYTES) {
                oldFolder.file(filepath, oldBlob.blob as Uint8Array);
              } else {
                console.warn(`Skip old large file (> ${MAX_ZIP_FILE_BYTES}): ${filepath}`);
              }
            } catch (e) {
              console.warn(`Failed to read old blob: ${filepath}`, e);
            }
          }

          // 変更後（追加されたファイルのみ、または変更されたファイル）
          if (!isDeleted) {
            try {
              const newBlob = await git.readBlob({ fs, dir, cache: GIT_CACHE.current, oid: newCommit, filepath });
              const size = (newBlob.blob as Uint8Array).byteLength ?? 0;
              if (size <= MAX_ZIP_FILE_BYTES) {
                newFolder.file(filepath, newBlob.blob as Uint8Array);
              } else {
                console.warn(`Skip new large file (> ${MAX_ZIP_FILE_BYTES}): ${filepath}`);
              }
            } catch (e) {
              console.warn(`Failed to read new blob: ${filepath}`, e);
            }
          }
        })
      );

      // 進捗を更新
      const progress = Math.min(100, Math.round(((i + ZIP_READ_CONCURRENCY) / valid.length) * 100));
      setZipProgress(progress);

      // Yield to UI so progress feels responsive
      await new Promise(requestAnimationFrame);
    }
    console.timeEnd('blob_read');

    // 1つのZIPファイルとしてダウンロード
    const blob = await diffZip.generateAsync(
      { type: "blob", compression: "STORE" },
      (meta) => {
        if (typeof meta.percent === 'number') {
          setZipProgress(Math.min(100, Math.max(0, Math.round(meta.percent))));
        }
      }
    );
    saveAs(blob, "diff.zip");
    setZipping(false);
    setZipProgress(0);
  }

  return (
    <div className="max-w-7xl mx-auto p-4">
      <div className="flex items-center justify-between mb-3 text-sm">
        <h1 className="text-lg font-bold text-gray-800">Git Diff ZIP Exporter</h1>
        <div className="flex items-center gap-3">
          {/* File System Access API対応ブラウザ向けボタン */}
          {'showDirectoryPicker' in window ? (
            <button
              onClick={handleDirectoryPickerClick}
              className="text-sm border border-gray-300 rounded px-3 py-1.5 bg-white hover:bg-gray-50 focus:ring-1 focus:ring-blue-500 outline-none font-medium text-gray-700 transition"
            >
              📁 リポジトリフォルダを選択
            </button>
          ) : (
            /* レガシーブラウザ向けフォールバック */
            <input
              type="file"
              // @ts-ignore - webkitdirectory is not in the type definition
              webkitdirectory=""
              directory=""
              onChange={handleDirectoryUpload}
              className="text-sm border border-gray-300 rounded px-3 py-1 focus:ring-1 focus:ring-blue-500 outline-none file:mr-2 file:py-0.5 file:px-2 file:rounded file:border-0 file:text-xs file:font-medium file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100"
            />
          )}
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
      {diffing && (
        <div className="mb-3 p-2 bg-purple-50 border-l-2 border-purple-500 text-purple-800 text-sm flex items-center gap-2">
          <svg className="animate-spin h-4 w-4" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
          </svg>
          差分を計算中...
        </div>
      )}
      {zipping && (
        <div className="mb-3 p-2 bg-amber-50 border-l-2 border-amber-500 text-amber-800 text-sm flex items-center gap-2">
          <svg className="animate-spin h-4 w-4" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
          </svg>
          ZIPを作成中... {zipProgress}%
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
              <div
                ref={listRef}
                className="max-h-96 overflow-y-auto relative"
                onScroll={(e) => setListScrollTop((e.target as HTMLDivElement).scrollTop)}
              >
                {(() => {
                  const total = commits.length;
                  const visibleCount = Math.ceil(listHeight / ROW_HEIGHT) + 10; // overscan
                  const start = Math.max(0, Math.floor(listScrollTop / ROW_HEIGHT) - 5);
                  const end = Math.min(total, start + visibleCount);
                  const items = commits.slice(start, end);
                  return (
                    <div style={{ height: total * ROW_HEIGHT + 'px', position: 'relative' }}>
                      <div style={{ position: 'absolute', top: start * ROW_HEIGHT + 'px', left: 0, right: 0 }}>
                        {items.map((c) => {
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
                  );
                })()}
              </div>
              <div className="px-3 py-2 border-t border-gray-200 flex items-center justify-center bg-white">
                <button
                  disabled={loadingMore}
                  onClick={async () => {
                    if (!fs) return;
                    try {
                      setLoadingMore(true);
                      const dir = "/repo";
                      const nextDepth = logDepth + LOG_PAGE_SIZE;
                      const logs = await git.log({ fs, dir, depth: nextDepth });
                      setCommits(prev => {
                        const newOnes = logs.slice(prev.length);
                        return prev.concat(newOnes);
                      });
                      setLogDepth(nextDepth);
                    } finally {
                      setLoadingMore(false);
                    }
                  }}
                  className={`px-3 py-1.5 rounded text-sm font-medium ${loadingMore ? 'bg-gray-200 text-gray-400' : 'bg-gray-100 text-gray-700 hover:bg-gray-200'}`}
                >
                  {loadingMore ? '読み込み中...' : 'さらに読み込む'}
                </button>
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
