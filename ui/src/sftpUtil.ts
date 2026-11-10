import type { FileEntry } from './types';

export type Side = 'local' | 'remote';
export type SortKey = 'name' | 'size' | 'date' | 'mode';
export type DiffState = 'only' | 'newer' | 'older' | 'differs' | 'same';

export const fmtSize = (n: number) => {
  if (n < 1024) return `${n} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let v = n / 1024;
  let i = 0;
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024;
    i++;
  }
  return `${v >= 10 ? v.toFixed(0) : v.toFixed(1)} ${units[i]}`;
};

export const fmtDate = (iso: string) => {
  const d = new Date(iso);
  return isNaN(d.getTime()) || d.getFullYear() < 1980 ? '' : d.toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' });
};

export const fmtDateLong = (iso: string) => {
  const d = new Date(iso);
  return isNaN(d.getTime()) || d.getFullYear() < 1980 ? '–' : d.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'medium' });
};

export const fmtSpeed = (bytesPerSecond: number) => (bytesPerSecond > 0 ? `${fmtSize(bytesPerSecond)}/s` : '');

/** Split a path into clickable parts (Windows "C:\\a\\b" or Unix "/a/b"). */
export function crumbs(path: string, side: Side): { label: string; path: string }[] {
  if (side === 'remote') {
    const parts = path.split('/').filter(Boolean);
    return [{ label: '/', path: '/' }, ...parts.map((p, i) => ({ label: p, path: '/' + parts.slice(0, i + 1).join('/') }))];
  }
  const win = path.includes('\\') || /^[A-Za-z]:/.test(path);
  const sep = win ? '\\' : '/';
  const parts = path.split(/[\\/]/).filter(Boolean);
  const out: { label: string; path: string }[] = [{ label: 'This PC', path: '' }];
  parts.forEach((p, i) => {
    const joined = parts.slice(0, i + 1).join(sep);
    out.push({ label: p, path: win ? (i === 0 ? joined + '\\' : joined) : '/' + joined });
  });
  return out;
}

export const joinPath = (dir: string, name: string, side: Side) => {
  const sep = side === 'remote' || !dir.includes('\\') ? '/' : '\\';
  return dir.endsWith(sep) ? dir + name : dir + sep + name;
};

export const baseName = (path: string) => path.replace(/[\\/]+$/, '').split(/[\\/]/).pop() ?? path;

/** Folders first, then by the chosen column. */
export function sortEntries(entries: FileEntry[], key: SortKey, asc: boolean): FileEntry[] {
  const dir = asc ? 1 : -1;
  const cmp = (a: FileEntry, b: FileEntry) => {
    switch (key) {
      case 'size':
        return a.size - b.size;
      case 'date':
        return new Date(a.modified).getTime() - new Date(b.modified).getTime();
      case 'mode':
        return a.mode.localeCompare(b.mode);
      default:
        return a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' });
    }
  };
  return [...entries].sort((a, b) => Number(b.isDir) - Number(a.isDir) || cmp(a, b) * dir || a.name.localeCompare(b.name));
}

/** Masks like "*.log;*.txt" or "backup-??.sql" (case-insensitive). */
export function matchesMask(name: string, mask: string): boolean {
  return mask
    .split(/[;,]/)
    .map((m) => m.trim())
    .filter(Boolean)
    .some((m) => {
      const re = new RegExp('^' + m.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$', 'i');
      return re.test(name);
    });
}

/** How an entry relates to the file with the same name on the other side (for "Compare folders"). */
export function compareEntry(entry: FileEntry, other: Map<string, FileEntry>): DiffState {
  const o = other.get(entry.name);
  if (!o) return 'only';
  if (entry.isDir || o.isDir) return 'same';
  const delta = (new Date(entry.modified).getTime() - new Date(o.modified).getTime()) / 1000;
  if (Math.abs(delta) > 2) return delta > 0 ? 'newer' : 'older';
  return entry.size === o.size ? 'same' : 'differs';
}

export const DIFF_LABEL: Record<DiffState, string> = {
  only: 'only here',
  newer: 'newer here',
  older: 'older here',
  differs: 'differs',
  same: '',
};

export function loadBookmarks(key: string): string[] {
  try {
    const raw = localStorage.getItem(key);
    const list = raw ? (JSON.parse(raw) as unknown) : [];
    return Array.isArray(list) ? list.filter((x): x is string => typeof x === 'string') : [];
  } catch {
    return [];
  }
}

export function saveBookmarks(key: string, list: string[]) {
  try {
    localStorage.setItem(key, JSON.stringify(list));
  } catch {
    /* not critical */
  }
}
