// Read-only File System Access API adapter for isomorphic-git
// Maps virtual path "/repo/.git" to the provided gitDirHandle

export type FSLike = {
  readFile: (path: string, opts?: { encoding?: string } | string) => Promise<string | Uint8Array>;
  readdir: (path: string) => Promise<string[]>;
  stat: (path: string) => Promise<any>;
  lstat: (path: string) => Promise<any>;
  readlink: (path: string) => Promise<string>;
  // stubs for APIs isomorphic-git may probe
  writeFile?: (path: string, data: Uint8Array | string) => Promise<void>;
  mkdir?: (path: string) => Promise<void>;
  rmdir?: (path: string) => Promise<void>;
  unlink?: (path: string) => Promise<void>;
  symlink?: (target: string, path: string) => Promise<void>;
  open?: (...args: any[]) => Promise<any>;
  read?: (...args: any[]) => Promise<any>;
  close?: (...args: any[]) => Promise<void>;
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

    async readlink(_path: string): Promise<string> {
      // No symlinks are expected in .git for our use; throw to indicate unsupported
      const err: any = new Error('readlink not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    // callback-API names that isomorphic-git attempts to bind; we provide stubs
    async writeFile(_path: string, _data: Uint8Array | string): Promise<void> {
      const err: any = new Error('writeFile not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    async mkdir(_path: string): Promise<void> {
      const err: any = new Error('mkdir not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    async rmdir(_path: string): Promise<void> {
      const err: any = new Error('rmdir not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    async unlink(_path: string): Promise<void> {
      const err: any = new Error('unlink not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    async symlink(_target: string, _path: string): Promise<void> {
      const err: any = new Error('symlink not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    async open(): Promise<any> {
      const err: any = new Error('open not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    async read(): Promise<any> {
      const err: any = new Error('read not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
    async close(): Promise<void> {
      const err: any = new Error('close not supported');
      err.code = 'ENOTSUP';
      throw err;
    },
  };

  // Some consumers may look for `fs.promises.*` (Node-style). Point it to self.
  (fs as any).promises = fs;
  return fs;
}
