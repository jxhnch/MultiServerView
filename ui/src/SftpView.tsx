import { useCallback, useEffect, useMemo, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent } from 'react';
import { api, onEvent } from './bridge';
import { askConfirm } from './Confirm';
import { ContextMenu, type MenuItem } from './ContextMenu';
import {
  IconArrowLeft, IconArrowRight, IconClose, IconCopy, IconDownload, IconEdit, IconFile, IconFolder, IconPlus, IconRefresh, IconText, IconTrash, IconUpload,
} from './Icons';
import { PasswordPrompt, isAuthError, type Credentials } from './PasswordPrompt';
import { PromptDialog } from './Prompt';
import { RemoteEditor } from './RemoteEditor';
import { OverwriteDialog, PropertiesDialog, type OverwriteChoice } from './SftpDialogs';
import { SftpPane } from './SftpPane';
import { compareEntry, fmtSize, fmtSpeed, joinPath, loadBookmarks, matchesMask, saveBookmarks, type DiffState, type Side } from './sftpUtil';
import type { BrowseResult, FileEntry, Server, SessionStatus } from './types';
import { useSftpPane } from './useSftpPane';

interface Transfer {
  id: string;
  name: string;
  direction: 'up' | 'down';
  move: boolean;
  done: number;
  total: number;
  started: number;
  state: 'running' | 'done' | 'error' | 'cancelled';
  message?: string;
}

type PromptKind = 'mkdir' | 'newfile' | 'rename' | 'mask' | 'unmask';

export function SftpView({
  tabId,
  server,
  active,
  reconnectKey,
  onStatus,
  onReconnect,
  onEdit,
}: {
  tabId: string;
  server: Server;
  active: boolean;
  reconnectKey: number;
  onStatus: (tabId: string, status: SessionStatus) => void;
  onReconnect: () => void;
  onEdit: () => void;
}) {
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const credRef = useRef<Credentials | undefined>(undefined);
  const [retry, setRetry] = useState(0);
  const [authPrompt, setAuthPrompt] = useState(false);
  const [editing, setEditing] = useState<FileEntry | null>(null);
  const [transfers, setTransfers] = useState<Transfer[]>([]);
  const [prompt, setPrompt] = useState<null | { kind: PromptKind; side: Side; entry?: FileEntry }>(null);
  const [dropSide, setDropSide] = useState<Side | null>(null);
  const [focusSide, setFocusSide] = useState<Side>('remote');
  const [compare, setCompare] = useState(false);
  const [ctx, setCtx] = useState<null | { x: number; y: number; side: Side; entry: FileEntry | null }>(null);
  const [props, setProps] = useState<null | { side: Side; entries: FileEntry[] }>(null);
  const [overwrite, setOverwrite] = useState<null | { names: string[]; resolve: (c: OverwriteChoice) => void }>(null);
  const dragFrom = useRef<Side | null>(null);
  const paneEls = { local: useRef<HTMLElement>(null), remote: useRef<HTMLElement>(null) };
  const noticeTimer = useRef<number | undefined>(undefined);

  const loadLocal = useCallback((path: string): Promise<BrowseResult> => (path === '' ? api.localRoots() : api.localBrowse(path)), []);
  const loadRemote = useCallback((path: string): Promise<BrowseResult> => api.sftpList(tabId, path), [tabId]);
  const local = useSftpPane('local', loadLocal);
  const remote = useSftpPane('remote', loadRemote);
  const pane = (side: Side) => (side === 'local' ? local : remote);

  const bmKey = (side: Side) => `msv-sftp-bm-${side === 'remote' ? server.id : 'local'}`;
  const [bookmarks, setBookmarks] = useState<Record<Side, string[]>>(() => ({ local: loadBookmarks('msv-sftp-bm-local'), remote: loadBookmarks(`msv-sftp-bm-${server.id}`) }));

  const setStatus = useCallback(
    (s: SessionStatus) => {
      setStatusState(s);
      onStatus(tabId, s);
    },
    [onStatus, tabId],
  );

  const flash = useCallback((msg: string) => {
    setNotice(msg);
    window.clearTimeout(noticeTimer.current);
    noticeTimer.current = window.setTimeout(() => setNotice(''), 4000);
  }, []);

  const fail = (e: unknown) => setError((e as Error).message);

  // Connect
  useEffect(() => {
    let cancelled = false;
    setStatus('connecting');
    setError('');
    const cred = credRef.current;
    credRef.current = undefined;
    api
      .sftpOpen(tabId, server.id, cred)
      .then(async ({ home }) => {
        if (cancelled) return;
        setStatus('connected');
        await remote.navigate(home);
      })
      .catch((e: Error) => {
        if (cancelled) return;
        setStatus('error');
        setError(e.message);
        if (isAuthError(e.message)) setAuthPrompt(true);
      });
    return () => {
      cancelled = true;
      void api.sftpClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey, retry]);

  // Local pane: start in the home folder the first time
  useEffect(() => {
    void api.localBrowse('').then(local.show).catch(() => undefined);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Progress of transfers and saves of files opened with a local program
  useEffect(
    () =>
      onEvent((e) => {
        if (e.event === 'sftp.progress' && e.sessionId === tabId)
          setTransfers((prev) => prev.map((t) => (t.id === e.transferId ? { ...t, done: e.done, total: e.total } : t)));
        if (e.event === 'sftp.externalSaved' && e.sessionId === tabId) {
          if (e.error) setError(`Could not upload "${e.name}": ${e.error}`);
          else flash(`"${e.name}" was saved to the server.`);
        }
      }),
    [tabId, flash],
  );

  const connected = status === 'connected';

  // the keyboard works in the pane that had the focus last
  useEffect(() => {
    if (active && connected) paneEls[focusSide].current?.focus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, connected]);

  const other = (side: Side): Side => (side === 'local' ? 'remote' : 'local');

  // ------------------------------------------------------------------ Navigation and selection
  const go = (side: Side, path: string) => {
    pane(side)
      .navigate(path)
      .then(() => setError(''))
      .catch(fail);
  };

  const reload = async (side: Side) => {
    try {
      await pane(side).reload();
    } catch (e) {
      fail(e);
    }
  };

  const toggleBookmark = (side: Side) => {
    const path = pane(side).data?.path;
    if (!path) return;
    setBookmarks((prev) => {
      const list = prev[side].includes(path) ? prev[side].filter((b) => b !== path) : [...prev[side], path];
      saveBookmarks(bmKey(side), list);
      return { ...prev, [side]: list };
    });
  };

  const activate = (side: Side, entry: FileEntry) => {
    if (entry.isDir) return go(side, entry.path);
    if (side === 'local') void api.localOpen(entry.path).catch(fail);
    else if (connected) void api.sftpOpenExternal(tabId, entry.path).then(() => flash(`Opened "${entry.name}". Saving it in your program uploads it again.`)).catch(fail);
  };

  const selectWhere = (side: Side, test: (e: FileEntry) => boolean, add = false) => {
    const m = pane(side);
    const next = new Set(add ? m.sel : []);
    for (const e of m.view) if (test(e)) next.add(e.path);
    m.setSel(next);
  };

  // ------------------------------------------------------------------ Compare folders
  const diffs = useMemo(() => {
    if (!compare || !local.data || !remote.data) return { local: null, remote: null };
    const build = (mine: FileEntry[], theirs: FileEntry[]) => {
      const map = new Map(theirs.map((e) => [e.name, e]));
      return new Map<string, DiffState>(mine.map((e) => [e.path, compareEntry(e, map)]));
    };
    return { local: build(local.data.entries, remote.data.entries), remote: build(remote.data.entries, local.data.entries) };
  }, [compare, local.data, remote.data]);

  // ------------------------------------------------------------------ Transfers
  const askOverwrite = (names: string[]) => new Promise<OverwriteChoice>((resolve) => setOverwrite({ names, resolve }));

  async function transferEntries(from: Side, entries: FileEntry[], move: boolean) {
    if (entries.length === 0 || !connected) return;
    const to = other(from);
    const target = pane(to).data;
    if (!target || (to === 'local' && target.path === '')) {
      setError('Open a folder on the other side first.');
      return;
    }

    const existing = new Map(target.entries.map((e) => [e.name, e]));
    let list = entries;
    const clash = entries.filter((e) => existing.has(e.name));
    if (clash.length > 0) {
      const choice = await askOverwrite(clash.map((c) => c.name));
      if (choice === 'cancel') return;
      if (choice === 'skip') list = entries.filter((e) => !existing.has(e.name));
      if (choice === 'newer')
        list = entries.filter((e) => {
          const o = existing.get(e.name);
          return !o || e.isDir || new Date(e.modified).getTime() > new Date(o.modified).getTime() + 2000;
        });
    }
    if (list.length === 0) {
      flash('Nothing to transfer.');
      return;
    }

    const id = crypto.randomUUID();
    const label = list.length === 1 ? list[0].name : `${list.length} items`;
    const direction = from === 'local' ? 'up' : 'down';
    setTransfers((prev) => [{ id, name: label, direction: direction as 'up' | 'down', move, done: 0, total: 0, started: Date.now(), state: 'running' as const }, ...prev].slice(0, 8));
    try {
      if (direction === 'up') await api.sftpUpload(tabId, id, list.map((x) => x.path), pane('remote').data?.path ?? '/');
      else await api.sftpDownload(tabId, id, list.map((x) => x.path), pane('local').data?.path ?? '');
      if (move) {
        if (from === 'local') await api.localDelete(list.map((x) => x.path));
        else for (const it of list) await api.sftpDelete(tabId, it.path);
      }
      setTransfers((prev) => prev.map((t) => (t.id === id ? { ...t, state: 'done', done: t.total } : t)));
    } catch (e) {
      const msg = (e as Error).message;
      setTransfers((prev) => prev.map((t) => (t.id === id ? { ...t, state: msg === 'Cancelled.' ? 'cancelled' : 'error', message: msg } : t)));
    }
    await Promise.all([reload('remote'), reload('local')]);
  }

  /** Move inside one pane (drag onto a folder). */
  async function moveInto(side: Side, entries: FileEntry[], folder: FileEntry) {
    const moving = entries.filter((e) => e.path !== folder.path);
    if (moving.length === 0) return;
    const names = new Set((await (side === 'remote' ? api.sftpList(tabId, folder.path) : api.localBrowse(folder.path))).entries.map((e) => e.name));
    const clash = moving.filter((m) => names.has(m.name));
    if (clash.length > 0) {
      setError(`"${clash[0].name}" already exists in "${folder.name}". Nothing was moved.`);
      return;
    }
    try {
      for (const e of moving) {
        const to = joinPath(folder.path, e.name, side);
        if (side === 'remote') await api.sftpRename(tabId, e.path, to);
        else await api.localRename(e.path, to);
      }
      flash(`Moved ${moving.length === 1 ? `"${moving[0].name}"` : `${moving.length} items`} to "${folder.name}".`);
    } catch (e) {
      fail(e);
    }
    await reload(side);
  }

  async function deleteEntries(side: Side, items: FileEntry[]) {
    if (items.length === 0) return;
    const what = items.length === 1 ? `"${items[0].name}"` : `${items.length} items`;
    const hasDir = items.some((i) => i.isDir);
    const ok =
      side === 'remote'
        ? await askConfirm(`Permanently delete ${what} from the server?${hasDir ? '\nFolders are deleted with their contents.' : ''}`, { title: 'Delete from server', confirmLabel: 'Delete', danger: true })
        : await askConfirm(`Move ${what} to the Recycle Bin?`, { title: 'Delete on this PC', confirmLabel: 'Move to Recycle Bin', danger: true });
    if (!ok) return;
    try {
      if (side === 'remote') for (const it of items) await api.sftpDelete(tabId, it.path);
      else await api.localDelete(items.map((i) => i.path));
    } catch (e) {
      fail(e);
    }
    await reload(side);
  }

  async function submitPrompt(value: string) {
    const p = prompt;
    setPrompt(null);
    if (!p) return;
    const m = pane(p.side);
    const dir = m.data?.path ?? '';
    try {
      if (p.kind === 'mask' || p.kind === 'unmask') {
        const test = (e: FileEntry) => matchesMask(e.name, value);
        if (p.kind === 'mask') selectWhere(p.side, test, true);
        else {
          const next = new Set(m.sel);
          for (const e of m.view) if (test(e)) next.delete(e.path);
          m.setSel(next);
        }
        return;
      }
      const target = joinPath(dir, value, p.side);
      if (p.kind === 'mkdir') {
        if (p.side === 'remote') await api.sftpMkdir(tabId, target);
        else await api.localMkdir(target);
      } else if (p.kind === 'newfile') {
        if (p.side === 'remote') await api.sftpCreateFile(tabId, target);
        else await api.localCreateFile(target);
      } else if (p.entry) {
        if (p.side === 'remote') await api.sftpRename(tabId, p.entry.path, target);
        else await api.localRename(p.entry.path, target);
      }
      await reload(p.side);
    } catch (e) {
      fail(e);
    }
  }

  const copyPath = (path: string) => void navigator.clipboard.writeText(path).then(() => flash('Path copied.')).catch(() => setError('Copying is not allowed here.'));

  // ------------------------------------------------------------------ Drag & drop
  const onDragStartEntry = (side: Side, entry: FileEntry, e: DragEvent) => {
    const m = pane(side);
    if (!m.sel.has(entry.path)) m.setSel(new Set([entry.path]));
    dragFrom.current = side;
    e.dataTransfer.setData('application/x-msv-side', side);
    e.dataTransfer.effectAllowed = 'copyMove';
  };
  const draggedEntries = (side: Side) => {
    const m = pane(side);
    return m.selected();
  };
  const onDropOnPane = (target: Side, _e: DragEvent) => {
    const from = dragFrom.current;
    dragFrom.current = null;
    if (!from || from === target) return;
    void transferEntries(from, draggedEntries(from), false);
  };
  const onDropOnFolder = (target: Side, folder: FileEntry, _e: DragEvent) => {
    const from = dragFrom.current;
    dragFrom.current = null;
    if (!from) return;
    if (from === target) void moveInto(target, draggedEntries(from), folder);
    else void transferEntries(from, draggedEntries(from), false); // dropping on a folder of the other side = copy into the current folder there
  };

  // ------------------------------------------------------------------ Keyboard (like WinSCP)
  const startRename = (side: Side) => {
    const one = pane(side).selected();
    if (one.length === 1) setPrompt({ kind: 'rename', side, entry: one[0] });
  };

  const onPaneKey = (side: Side, e: KeyboardEvent) => {
    const m = pane(side);
    const sel = m.selected();
    const focused = m.view.find((x) => x.path === m.cursor) ?? sel[0];
    const ctrl = e.ctrlKey || e.metaKey;

    if (e.altKey && e.key === 'ArrowLeft') return void m.back().catch(fail);
    if (e.altKey && e.key === 'ArrowRight') return void m.forward().catch(fail);
    if (e.altKey && e.key === 'Enter') {
      if (sel.length > 0) setProps({ side, entries: sel });
      return;
    }
    if (ctrl) {
      if (e.key === 'a' || e.key === 'A') {
        e.preventDefault();
        return selectWhere(side, () => true);
      }
      if (e.key === 'r' || e.key === 'R') {
        e.preventDefault();
        return void reload(side);
      }
      if (e.key === 'h' || e.key === 'H') {
        e.preventDefault();
        return m.setShowHidden(!m.showHidden);
      }
      return;
    }

    switch (e.key) {
      case 'Enter':
        if (focused) activate(side, focused);
        break;
      case 'Backspace':
        if (m.data && m.data.parent !== m.data.path) go(side, m.data.parent);
        break;
      case 'Delete':
      case 'F8':
        e.preventDefault();
        void deleteEntries(side, sel);
        break;
      case 'F2':
        e.preventDefault();
        startRename(side);
        break;
      case 'F4':
        e.preventDefault();
        if (sel.length === 1 && !sel[0].isDir) {
          if (side === 'remote') setEditing(sel[0]);
          else void api.localOpen(sel[0].path).catch(fail);
        }
        break;
      case 'F5':
        e.preventDefault();
        void transferEntries(side, sel, false);
        break;
      case 'F6':
        e.preventDefault();
        void transferEntries(side, sel, true);
        break;
      case 'F7':
        e.preventDefault();
        setPrompt({ kind: 'mkdir', side });
        break;
      case 'Tab':
        e.preventDefault();
        paneEls[other(side)].current?.focus();
        break;
      case 'Escape':
        m.setSel(new Set());
        break;
      case '*': {
        const next = new Set(m.view.filter((x) => !m.sel.has(x.path)).map((x) => x.path));
        m.setSel(next);
        break;
      }
      case '+':
        setPrompt({ kind: 'mask', side });
        break;
      case '-':
        setPrompt({ kind: 'unmask', side });
        break;
      default:
    }
  };

  // ------------------------------------------------------------------ Context menu
  const contextItems = (): MenuItem[] => {
    if (!ctx) return [];
    const { side, entry } = ctx;
    const m = pane(side);
    const to = other(side);
    const targets = entry ? (m.sel.has(entry.path) ? m.selected() : [entry]) : [];
    const one = targets.length === 1 ? targets[0] : null;
    const moveWord = side === 'local' ? 'Upload' : 'Download';
    const items: MenuItem[] = [];

    if (entry) {
      items.push({ label: entry.isDir && one ? 'Open folder' : 'Open', icon: <IconFolder size={14} />, disabled: !one, onClick: () => one && activate(side, one) });
      if (side === 'remote') items.push({ label: 'Edit (built-in editor)', icon: <IconText size={14} />, disabled: !one || one.isDir, onClick: () => one && setEditing(one) });
      if (side === 'local') items.push({ label: 'Show in Explorer', icon: <IconFolder size={14} />, disabled: !one, onClick: () => one && void api.localReveal(one.path).catch(fail) });
      items.push({ label: `${moveWord} (F5)`, icon: side === 'local' ? <IconUpload size={14} /> : <IconDownload size={14} />, separator: true, disabled: !connected, onClick: () => void transferEntries(side, targets, false) });
      items.push({ label: `${moveWord} and delete here (F6)`, icon: side === 'local' ? <IconUpload size={14} /> : <IconDownload size={14} />, disabled: !connected, onClick: () => void transferEntries(side, targets, true) });
      items.push({ label: 'Rename (F2)', icon: <IconEdit size={14} />, separator: true, disabled: !one, onClick: () => startRename(side) });
      items.push({ label: side === 'remote' ? 'Delete (Del)' : 'Delete – Recycle Bin (Del)', icon: <IconTrash size={14} />, danger: true, onClick: () => void deleteEntries(side, targets) });
      items.push({ label: side === 'remote' ? 'Properties and permissions (Alt+Enter)' : 'Properties (Alt+Enter)', icon: <IconFile size={14} />, separator: true, onClick: () => setProps({ side, entries: targets }) });
      items.push({ label: 'Copy path', icon: <IconCopy size={14} />, disabled: !one, onClick: () => one && copyPath(one.path) });
      void to;
    }
    items.push({ label: 'New file…', icon: <IconFile size={14} />, separator: !!entry, disabled: !m.data || (side === 'local' && m.data.path === ''), onClick: () => setPrompt({ kind: 'newfile', side }) });
    items.push({ label: 'New folder… (F7)', icon: <IconPlus size={14} />, disabled: !m.data || (side === 'local' && m.data.path === ''), onClick: () => setPrompt({ kind: 'mkdir', side }) });
    items.push({ label: 'Select all (Ctrl+A)', separator: true, onClick: () => selectWhere(side, () => true) });
    items.push({ label: 'Select by mask… (+)', onClick: () => setPrompt({ kind: 'mask', side }) });
    items.push({ label: 'Invert selection (*)', onClick: () => m.setSel(new Set(m.view.filter((x) => !m.sel.has(x.path)).map((x) => x.path))) });
    if (compare)
      items.push({
        label: 'Select newer and missing files',
        onClick: () => {
          const d = diffs[side];
          selectWhere(side, (x) => !x.isDir && (d?.get(x.path) === 'only' || d?.get(x.path) === 'newer' || d?.get(x.path) === 'differs'));
        },
      });
    items.push({ label: 'Refresh (Ctrl+R)', icon: <IconRefresh size={14} />, separator: true, onClick: () => void reload(side) });
    return items;
  };

  // ------------------------------------------------------------------ Layout
  const paneProps = (side: Side) => {
    const m = pane(side);
    return {
      model: m,
      title: side === 'local' ? 'This PC' : server.name,
      paneRef: paneEls[side],
      diff: diffs[side],
      bookmarks: bookmarks[side],
      enabled: side === 'local' || connected,
      dropActive: dropSide === side,
      setDropActive: (on: boolean) => setDropSide(on ? side : null),
      go: (path: string) => go(side, path),
      onToggleBookmark: () => toggleBookmark(side),
      onNewFile: () => setPrompt({ kind: 'newfile', side }),
      onNewFolder: () => setPrompt({ kind: 'mkdir', side }),
      onActivate: (en: FileEntry) => activate(side, en),
      onContext: (ev: MouseEvent, entry: FileEntry | null) => setCtx({ x: ev.clientX, y: ev.clientY, side, entry }),
      onKeyDown: (ev: KeyboardEvent) => onPaneKey(side, ev),
      onDragStartEntry: (en: FileEntry, ev: DragEvent) => onDragStartEntry(side, en, ev),
      onDropOnPane: (ev: DragEvent) => onDropOnPane(side, ev),
      onDropOnFolder: (folder: FileEntry, ev: DragEvent) => onDropOnFolder(side, folder, ev),
      onFocusPane: () => setFocusSide(side),
    };
  };

  const promptTitle: Record<PromptKind, [string, string, string]> = {
    mkdir: ['New folder', 'Folder name', 'Create'],
    newfile: ['New file', 'File name', 'Create'],
    rename: ['Rename', 'New name', 'Rename'],
    mask: ['Select by mask', 'Mask (e.g. *.log;*.txt)', 'Select'],
    unmask: ['Deselect by mask', 'Mask (e.g. *.log;*.txt)', 'Deselect'],
  };

  const running = transfers.filter((t) => t.state === 'running').length;

  return (
    <div className="sftp">
      <div className="sftp-bar">
        <span className="session-title">{server.name}</span>
        <span className="muted">SFTP · {server.username ? `${server.username}@` : ''}{server.host}:{server.port}</span>
        <span className={`pill ${status}`}>
          <span className="dot-sm" />
          {status === 'connected' ? 'Connected' : status === 'connecting' ? 'Connecting…' : 'Error'}
        </span>
        <span className="grow" />
        <button className={'ghost' + (compare ? ' on' : '')} title="Mark files that differ between the two folders" onClick={() => setCompare((c) => !c)}>
          Compare folders
        </button>
        <button className="ghost" onClick={onReconnect}>
          <IconRefresh size={15} /> Reconnect
        </button>
        <button className="ghost" onClick={onEdit}>
          <IconEdit size={15} /> Host
        </button>
      </div>

      {status === 'error' && !remote.data ? (
        <div className="sftp-fail">
          <div className="error">{error || 'Connection failed.'}</div>
          <button className="primary" onClick={onReconnect}>
            Reconnect
          </button>
        </div>
      ) : (
        <div className="sftp-main">
          <SftpPane {...paneProps('local')} />
          <div className="sftp-mid">
            <button className="primary icon-only" title="Upload selection (F5)" disabled={local.sel.size === 0 || !connected} onClick={() => void transferEntries('local', local.selected(), false)}>
              <IconArrowRight size={17} />
            </button>
            <button className="primary icon-only" title="Download selection (F5)" disabled={remote.sel.size === 0 || !connected} onClick={() => void transferEntries('remote', remote.selected(), false)}>
              <IconArrowLeft size={17} />
            </button>
          </div>
          <SftpPane {...paneProps('remote')} />
        </div>
      )}

      <div className="fkeys muted small">
        <span><b>F2</b> Rename</span>
        <span><b>F4</b> Edit</span>
        <span><b>F5</b> Copy</span>
        <span><b>F6</b> Move</span>
        <span><b>F7</b> New folder</span>
        <span><b>Del</b> Delete</span>
        <span><b>Alt+Enter</b> Properties</span>
        <span><b>Tab</b> Other side</span>
        <span className="grow" />
        <span>{focusSide === 'local' ? 'This PC' : server.name} is active</span>
      </div>

      {(error || notice) && (
        <div className={'sftp-error' + (error ? '' : ' ok')}>
          <span className={error ? 'error' : ''}>{error || notice}</span>
          <button className="ghost icon-only" onClick={() => (error ? setError('') : setNotice(''))}>
            <IconClose size={14} />
          </button>
        </div>
      )}

      {transfers.length > 0 && (
        <div className="transfers">
          {transfers.map((t) => {
            const secs = Math.max((Date.now() - t.started) / 1000, 0.5);
            const speed = t.done / secs;
            const eta = speed > 0 && t.total > t.done ? Math.ceil((t.total - t.done) / speed) : 0;
            return (
              <div key={t.id} className={'transfer ' + t.state}>
                <span className="t-dir">{t.direction === 'up' ? '↑' : '↓'}</span>
                <span className="t-name">
                  {t.name}
                  {t.move && <span className="muted small"> (move)</span>}
                </span>
                <div className="t-bar">
                  <i style={{ width: `${t.total > 0 ? Math.min(100, (t.done / t.total) * 100) : t.state === 'done' ? 100 : 4}%` }} />
                </div>
                <span className="t-info muted small">
                  {t.state === 'running' && (t.total > 0 ? `${fmtSize(t.done)} / ${fmtSize(t.total)} · ${fmtSpeed(speed)}${eta ? ` · ${eta}s left` : ''}` : 'Starting…')}
                  {t.state === 'done' && 'Done'}
                  {t.state === 'cancelled' && 'Cancelled'}
                  {t.state === 'error' && <span className="error">{t.message}</span>}
                </span>
                {t.state === 'running' ? (
                  <button className="ghost icon-only" title="Cancel" onClick={() => void api.sftpCancel(t.id)}>
                    <IconClose size={14} />
                  </button>
                ) : (
                  <button className="ghost icon-only" title="Remove" onClick={() => setTransfers((p) => p.filter((x) => x.id !== t.id))}>
                    <IconClose size={14} />
                  </button>
                )}
              </div>
            );
          })}
          {transfers.length > 1 && running === 0 && (
            <button className="ghost small" onClick={() => setTransfers([])}>
              Clear list
            </button>
          )}
        </div>
      )}

      {ctx && <ContextMenu x={ctx.x} y={ctx.y} items={contextItems()} onClose={() => setCtx(null)} />}
      {prompt && (
        <PromptDialog
          title={promptTitle[prompt.kind][0]}
          label={promptTitle[prompt.kind][1]}
          initial={prompt.kind === 'rename' ? prompt.entry?.name : prompt.kind === 'mask' || prompt.kind === 'unmask' ? '*.*' : ''}
          confirmLabel={promptTitle[prompt.kind][2]}
          onSubmit={(v) => void submitPrompt(v)}
          onClose={() => setPrompt(null)}
        />
      )}
      {overwrite && (
        <OverwriteDialog
          names={overwrite.names}
          onChoice={(c) => {
            overwrite.resolve(c);
            setOverwrite(null);
          }}
        />
      )}
      {props && (
        <PropertiesDialog
          side={props.side}
          sessionId={tabId}
          entries={props.entries}
          onClose={() => setProps(null)}
          onChanged={() => void reload(props.side)}
          toast={(m, k) => (k === 'error' ? setError(m) : flash(m))}
        />
      )}
      {editing && <RemoteEditor sessionId={tabId} file={editing} onClose={() => setEditing(null)} />}
      {authPrompt && (
        <PasswordPrompt
          server={server}
          failed
          onCancel={() => setAuthPrompt(false)}
          onSubmit={(cred) => {
            credRef.current = cred;
            setAuthPrompt(false);
            setRetry((n) => n + 1);
          }}
        />
      )}
    </div>
  );
}

