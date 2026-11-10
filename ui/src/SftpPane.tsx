import { useEffect, useRef, useState, type DragEvent, type KeyboardEvent, type MouseEvent, type Ref } from 'react';
import { ContextMenu, type MenuItem } from './ContextMenu';
import { IconArrowLeft, IconArrowRight, IconArrowUp, IconEye, IconFile, IconFolder, IconPlus, IconRefresh, IconStar } from './Icons';
import { crumbs, DIFF_LABEL, fmtDate, fmtSize, type DiffState, type SortKey } from './sftpUtil';
import type { PaneModel } from './useSftpPane';
import type { FileEntry } from './types';

export interface PaneProps {
  model: PaneModel;
  title: string;
  paneRef: Ref<HTMLElement>;
  diff: Map<string, DiffState> | null;
  bookmarks: string[];
  enabled: boolean;
  dropActive: boolean;
  setDropActive: (on: boolean) => void;
  /** Navigates and reports problems to the user. */
  go: (path: string) => void;
  onToggleBookmark: () => void;
  onNewFile: () => void;
  onNewFolder: () => void;
  onActivate: (e: FileEntry) => void;
  onContext: (ev: MouseEvent, entry: FileEntry | null) => void;
  onKeyDown: (e: KeyboardEvent) => void;
  onDragStartEntry: (entry: FileEntry, e: DragEvent) => void;
  onDropOnPane: (e: DragEvent) => void;
  onDropOnFolder: (folder: FileEntry, e: DragEvent) => void;
  onFocusPane: () => void;
}

const COLUMNS: { key: SortKey; label: string; cls: string; remoteOnly?: boolean }[] = [
  { key: 'name', label: 'Name', cls: 'c-name' },
  { key: 'size', label: 'Size', cls: 'c-size' },
  { key: 'date', label: 'Modified', cls: 'c-date' },
  { key: 'mode', label: 'Permissions', cls: 'c-mode', remoteOnly: true },
];

export function SftpPane(p: PaneProps) {
  const { model, title } = p;
  const { data, view, sel, cursor } = model;
  const side = model.side;
  const [editing, setEditing] = useState<string | null>(null);
  const [menu, setMenu] = useState<null | { x: number; y: number; kind: 'new' | 'bookmarks' }>(null);
  const [folderDrop, setFolderDrop] = useState<string | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const lastAnchor = useRef<string | null>(null);

  // keep the cursor row visible while moving with the arrow keys
  useEffect(() => {
    if (!cursor || !listRef.current) return;
    const row = [...listRef.current.querySelectorAll<HTMLElement>('[data-path]')].find((r) => r.dataset.path === cursor);
    row?.scrollIntoView({ block: 'nearest' });
  }, [cursor]);

  const click = (entry: FileEntry, e: MouseEvent) => {
    const next = new Set(e.ctrlKey || e.metaKey ? sel : []);
    if (e.shiftKey && lastAnchor.current) {
      const a = view.findIndex((x) => x.path === lastAnchor.current);
      const b = view.findIndex((x) => x.path === entry.path);
      if (a >= 0 && b >= 0) view.slice(Math.min(a, b), Math.max(a, b) + 1).forEach((x) => next.add(x.path));
    } else if ((e.ctrlKey || e.metaKey) && sel.has(entry.path)) next.delete(entry.path);
    else next.add(entry.path);
    model.setSel(next);
    model.setCursor(entry.path);
    if (!e.shiftKey) lastAnchor.current = entry.path;
  };

  const onKey = (e: KeyboardEvent) => {
    if ((e.target as HTMLElement).tagName === 'INPUT') return;
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp' || e.key === 'Home' || e.key === 'End') {
      e.preventDefault();
      if (view.length === 0) return;
      const at = view.findIndex((x) => x.path === cursor);
      const next = e.key === 'Home' ? 0 : e.key === 'End' ? view.length - 1 : Math.max(0, Math.min(view.length - 1, (at < 0 ? (e.key === 'ArrowDown' ? -1 : view.length) : at) + (e.key === 'ArrowDown' ? 1 : -1)));
      const target = view[next];
      if (e.shiftKey && lastAnchor.current) {
        const a = view.findIndex((x) => x.path === lastAnchor.current);
        model.setSel(new Set(view.slice(Math.min(a, next), Math.max(a, next) + 1).map((x) => x.path)));
      } else {
        model.setSel(new Set([target.path]));
        lastAnchor.current = target.path;
      }
      model.setCursor(target.path);
      return;
    }
    if (e.key === ' ') {
      e.preventDefault();
      const entry = view.find((x) => x.path === cursor);
      if (entry) {
        const next = new Set(sel);
        if (next.has(entry.path)) next.delete(entry.path);
        else next.add(entry.path);
        model.setSel(next);
      }
      return;
    }
    p.onKeyDown(e);
  };

  const openMenu = (kind: 'new' | 'bookmarks', e: MouseEvent) => {
    const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
    setMenu({ x: r.left, y: r.bottom + 4, kind });
  };

  const menuItems = (): MenuItem[] =>
    menu?.kind === 'new'
      ? [
          { label: 'New file…', icon: <IconFile size={14} />, onClick: p.onNewFile },
          { label: 'New folder…', icon: <IconFolder size={14} />, onClick: p.onNewFolder },
        ]
      : p.bookmarks.length === 0
        ? [{ label: 'No bookmarks yet – use the star to add this folder', disabled: true, onClick: () => undefined }]
        : p.bookmarks.map((b) => ({ label: b, icon: <IconStar size={13} />, onClick: () => p.go(b) }));

  const atRoot = !data || (data.parent === '' && data.path === '');
  const bookmarked = !!data && p.bookmarks.includes(data.path);
  const totalSelected = model.selected().reduce((a, e) => a + (e.isDir ? 0 : e.size), 0);

  return (
    <section
      ref={p.paneRef}
      className={'sftp-pane' + (p.dropActive ? ' drop' : '')}
      tabIndex={0}
      onFocus={p.onFocusPane}
      onMouseDown={p.onFocusPane}
      onKeyDown={onKey}
      onDragOver={(e) => {
        e.preventDefault();
        if (!p.dropActive) p.setDropActive(true);
      }}
      onDragLeave={() => p.setDropActive(false)}
      onDrop={(e) => {
        p.setDropActive(false);
        p.onDropOnPane(e);
      }}
    >
      <header>
        <span className="sftp-title">{title}</span>
        <span className="grow" />
        <button className="ghost icon-only" title="Back (Alt+Left)" disabled={!model.canBack || !p.enabled} onClick={() => void model.back().catch(() => undefined)}>
          <IconArrowLeft size={15} />
        </button>
        <button className="ghost icon-only" title="Forward (Alt+Right)" disabled={!model.canForward || !p.enabled} onClick={() => void model.forward().catch(() => undefined)}>
          <IconArrowRight size={15} />
        </button>
        <button className="ghost icon-only" title="Up one folder (Backspace)" disabled={atRoot || !p.enabled} onClick={() => data && p.go(data.parent)}>
          <IconArrowUp size={15} />
        </button>
        <button className="ghost icon-only" title="Refresh (Ctrl+R)" disabled={!p.enabled} onClick={() => p.go(data?.path ?? '')}>
          <IconRefresh size={15} />
        </button>
        <button className="ghost icon-only" title="New file or folder (F7 = folder)" disabled={!data || (side === 'local' && data.path === '')} onClick={(e) => openMenu('new', e)}>
          <IconPlus size={15} />
        </button>
        <button className={'ghost icon-only' + (bookmarked ? ' on' : '')} title={bookmarked ? 'Remove this folder from the bookmarks' : 'Bookmark this folder'} disabled={!data || data.path === ''} onClick={p.onToggleBookmark}>
          <IconStar size={15} filled={bookmarked} />
        </button>
        <button className="ghost small-btn" title="Bookmarks" onClick={(e) => openMenu('bookmarks', e)}>
          ▾
        </button>
        <button className={'ghost icon-only' + (model.showHidden ? ' on' : '')} title={model.showHidden ? 'Hidden files are shown (Ctrl+H to hide)' : 'Hidden files are hidden (Ctrl+H to show)'} onClick={() => model.setShowHidden(!model.showHidden)}>
          <IconEye size={15} />
        </button>
      </header>

      <div className="addr-row">
        {editing !== null ? (
          <input
            className="addr-input mono"
            autoFocus
            value={editing}
            onChange={(e) => setEditing(e.target.value)}
            onFocus={(e) => e.currentTarget.select()}
            onBlur={() => setEditing(null)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                const v = editing.trim();
                setEditing(null);
                if (v) p.go(v);
              }
              if (e.key === 'Escape') setEditing(null);
            }}
          />
        ) : (
          <nav className="crumbs" title="Click empty space to type a path (Ctrl+L)" onDoubleClick={() => data && setEditing(data.path)} onClick={(e) => e.target === e.currentTarget && data && setEditing(data.path)}>
            {data &&
              crumbs(data.path, side).map((c, i, arr) => (
                <span key={c.path + i}>
                  <button className="crumb" onClick={() => p.go(c.path)}>
                    {c.label}
                  </button>
                  {i < arr.length - 1 && <span className="sep">›</span>}
                </span>
              ))}
          </nav>
        )}
        <input className="filter-input" placeholder="Filter (e.g. *.log)" value={model.filter} onChange={(e) => model.setFilter(e.target.value)} onKeyDown={(e) => e.key === 'Escape' && model.setFilter('')} />
      </div>

      <div className="file-head">
        {COLUMNS.filter((c) => !c.remoteOnly || side === 'remote').map((c) => (
          <button key={c.key} className={c.cls + ' head-btn'} onClick={() => model.toggleSort(c.key)}>
            {c.label}
            {model.sort.key === c.key && <span className="sort-arrow">{model.sort.asc ? '▲' : '▼'}</span>}
          </button>
        ))}
      </div>

      <div
        className="file-list"
        ref={listRef}
        onContextMenu={(e) => {
          if (e.target === e.currentTarget) {
            e.preventDefault();
            p.onContext(e, null);
          }
        }}
        onClick={(e) => {
          if (e.target === e.currentTarget) model.setSel(new Set());
        }}
      >
        {data && view.length === 0 && <div className="muted pad-sm">{model.filter ? 'Nothing matches the filter.' : 'This folder is empty.'}</div>}
        {view.map((en) => {
          const d = p.diff?.get(en.path);
          return (
            <div
              key={en.path}
              data-path={en.path}
              className={'file-row' + (sel.has(en.path) ? ' sel' : '') + (cursor === en.path ? ' cur' : '') + (folderDrop === en.path ? ' drop-folder' : '') + (d && d !== 'same' ? ' diff-' + d : '')}
              draggable
              onDragStart={(e) => p.onDragStartEntry(en, e)}
              onDragOver={(e) => {
                if (en.isDir) {
                  e.preventDefault();
                  e.stopPropagation();
                  setFolderDrop(en.path);
                  if (p.dropActive) p.setDropActive(false);
                }
              }}
              onDragLeave={() => setFolderDrop((f) => (f === en.path ? null : f))}
              onDrop={(e) => {
                setFolderDrop(null);
                if (en.isDir) {
                  e.preventDefault();
                  e.stopPropagation();
                  p.setDropActive(false);
                  p.onDropOnFolder(en, e);
                }
              }}
              onClick={(e) => click(en, e)}
              onDoubleClick={() => p.onActivate(en)}
              onContextMenu={(e) => {
                e.preventDefault();
                if (!sel.has(en.path)) {
                  model.setSel(new Set([en.path]));
                  model.setCursor(en.path);
                }
                p.onContext(e, en);
              }}
            >
              <span className="c-name">
                <span className={'f-ico' + (en.isDir ? ' dir' : '')}>{en.isDir ? <IconFolder size={16} /> : <IconFile size={16} />}</span>
                <span className="f-name">{en.name}</span>
                {en.isLink && <span className="link-mark" title="Symbolic link">↪</span>}
                {d && d !== 'same' && <span className={'diff-tag ' + d}>{DIFF_LABEL[d]}</span>}
              </span>
              <span className="c-size muted">{en.isDir ? '' : fmtSize(en.size)}</span>
              <span className="c-date muted">{fmtDate(en.modified)}</span>
              {side === 'remote' && <span className="c-mode muted mono">{en.mode}</span>}
            </div>
          );
        })}
      </div>

      <footer className="muted small">
        {data ? `${view.length}${view.length !== data.entries.length ? ` of ${data.entries.length}` : ''} entries` : ''}
        {sel.size > 0 ? ` · ${sel.size} selected${totalSelected > 0 ? ` (${fmtSize(totalSelected)})` : ''}` : ''}
      </footer>

      {menu && <ContextMenu x={menu.x} y={menu.y} items={menuItems()} onClose={() => setMenu(null)} />}
    </section>
  );
}
