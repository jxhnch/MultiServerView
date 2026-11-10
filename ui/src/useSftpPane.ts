import { useCallback, useMemo, useRef, useState } from 'react';
import { sortEntries, matchesMask, type Side, type SortKey } from './sftpUtil';
import type { BrowseResult, FileEntry } from './types';

export interface PaneModel {
  side: Side;
  data: BrowseResult | null;
  /** Entries as shown: filtered, hidden files removed if switched off, sorted. */
  view: FileEntry[];
  sel: Set<string>;
  setSel: (s: Set<string>) => void;
  cursor: string | null;
  setCursor: (p: string | null) => void;
  sort: { key: SortKey; asc: boolean };
  toggleSort: (key: SortKey) => void;
  filter: string;
  setFilter: (f: string) => void;
  showHidden: boolean;
  setShowHidden: (v: boolean) => void;
  canBack: boolean;
  canForward: boolean;
  /** Opens a folder (and remembers it in the history). */
  navigate: (path: string) => Promise<void>;
  back: () => Promise<void>;
  forward: () => Promise<void>;
  reload: () => Promise<void>;
  /** Replaces the list (e.g. after the first connect) without a history entry. */
  show: (r: BrowseResult) => void;
  selected: () => FileEntry[];
}

/** State of one file pane: listing, selection, sorting, filter, hidden files and back/forward history. */
export function useSftpPane(side: Side, load: (path: string) => Promise<BrowseResult>): PaneModel {
  const [data, setData] = useState<BrowseResult | null>(null);
  const [sel, setSel] = useState<Set<string>>(new Set());
  const [cursor, setCursor] = useState<string | null>(null);
  const [sort, setSort] = useState<{ key: SortKey; asc: boolean }>({ key: 'name', asc: true });
  const [filter, setFilter] = useState('');
  const [showHidden, setShowHidden] = useState(true);
  const history = useRef<string[]>([]);
  const index = useRef(-1);
  const [, bump] = useState(0);

  const view = useMemo(() => {
    let list = data?.entries ?? [];
    if (!showHidden) list = list.filter((e) => !e.name.startsWith('.'));
    const f = filter.trim();
    if (f) list = list.filter((e) => (/[*?]/.test(f) ? matchesMask(e.name, f) : e.name.toLowerCase().includes(f.toLowerCase())));
    return sortEntries(list, sort.key, sort.asc);
  }, [data, showHidden, filter, sort]);

  const open = useCallback(
    async (path: string, push: boolean) => {
      const r = await load(path);
      setData(r);
      setSel(new Set());
      setCursor(null);
      if (push) {
        history.current = history.current.slice(0, index.current + 1);
        if (history.current[history.current.length - 1] !== r.path) history.current.push(r.path);
        index.current = history.current.length - 1;
        bump((n) => n + 1);
      }
    },
    [load],
  );

  const go = async (delta: number) => {
    const next = index.current + delta;
    if (next < 0 || next >= history.current.length) return;
    index.current = next;
    bump((n) => n + 1);
    await open(history.current[next], false);
  };

  return {
    side,
    data,
    view,
    sel,
    setSel,
    cursor,
    setCursor,
    sort,
    toggleSort: (key) => setSort((s) => (s.key === key ? { key, asc: !s.asc } : { key, asc: true })),
    filter,
    setFilter,
    showHidden,
    setShowHidden,
    canBack: index.current > 0,
    canForward: index.current < history.current.length - 1,
    navigate: (path) => open(path, true),
    back: () => go(-1),
    forward: () => go(1),
    reload: async () => {
      // keep the selection of entries that still exist
      const keep = sel;
      const r = await load(data?.path ?? '');
      setData(r);
      setSel(new Set([...keep].filter((p) => r.entries.some((e) => e.path === p))));
    },
    show: (r) => {
      setData(r);
      setSel(new Set());
      if (history.current.length === 0) {
        history.current = [r.path];
        index.current = 0;
        bump((n) => n + 1);
      }
    },
    selected: () => (data?.entries ?? []).filter((e) => sel.has(e.path)),
  };
}
