// Read-only File System Access API adapter for isomorphic-git
// Maps virtual path "/repo/.git" to the provided gitDirHandle

export type FSLike = {
  readFile: (path: string, opts?: { encoding?: string } | string) => Promise<string | Uint8Array>;
  readdir: (path: string) => Promise<string[]>;
  stat: (path: string) => Promise<any>;
  lstat: (path: string) => Promise<any>;
};

function makeStats(isFile: boolean, size = 0) {
  const mode = isFile ? 0o100644 : 0o040755;
  const stats = {
    mode,
    size,
    mtimeMs: 0,
    ctimeMs: 0,
    isFile: () => isFile,
    isDirectory: () => !isFile,
    isSymbolicLink: () => false,
  } as any;
  return stats;
}

function parseGitPath(path: string) {
  let p = path.replace(/\\/g, '/');
  if (!p.startsWith('/')) p = '/' + p;
  if (p.startsWith('/repo')) p = p.slice('/repo'.length) || '/';
  if (!p.startsWith('/.git')) throw new Error(`Path outside gitdir is not supported: ${path}`);
  p = p.slice('/.git'.length) || '/';
  if (!p.startsWith('/')) p = '/' + p;
  const parts = p.split('/').filter(Boolean);
  return parts; // relative segments inside .git
}

export function createFsAccessAdapter(gitDirHandle: FileSystemDirectoryHandle): FSLike {
  async function getDirHandleFromParts(parts: string[]): Promise<FileSystemDirectoryHandle> {
    let dir = gitDirHandle;
    for (const part of parts) {
      if (!part) continue;
      dir = await dir.getDirectoryHandle(part);
    }
    return dir;
  }

  async function getFileHandleFromParts(parts: string[]): Promise<FileSystemFileHandle> {
    const fileName = parts[parts.length - 1];
    const dirParts = parts.slice(0, -1);
    const dir = await getDirHandleFromParts(dirParts);
    return await dir.getFileHandle(fileName);
  }

  const fs: FSLike = {
    async readFile(path: string, opts?: { encoding?: string } | string): Promise<string | Uint8Array> {
      const parts = parseGitPath(path);
      const fh = await getFileHandleFromParts(parts);
      const file = await fh.getFile();
      if (typeof opts === 'string' ? opts === 'utf8' : opts?.encoding === 'utf8') {
        return await file.text();
      }
      const buf = await file.arrayBuffer();
      return new Uint8Array(buf);
    },

    async readdir(path: string): Promise<string[]> {
      const parts = parseGitPath(path);
      const dir = await getDirHandleFromParts(parts);
      const names: string[] = [];
      // @ts-ignore - File System Access API iterable
      for await (const entry of (dir as any).values()) {
        names.push(entry.name);
      }
      return names;
    },

    async stat(path: string): Promise<any> {
      const parts = parseGitPath(path);
      try {
        const fh = await getFileHandleFromParts(parts);
        const file = await fh.getFile();
        return makeStats(true, file.size);
      } catch (e) {
        const dh = await getDirHandleFromParts(parts);
        if (dh) return makeStats(false, 0);
        throw e;
      }
    },

    async lstat(path: string): Promise<any> {
      return this.stat(path);
    },
  };

  return fs;
}

