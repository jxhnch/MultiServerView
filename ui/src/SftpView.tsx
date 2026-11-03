import { useCallback, useEffect, useRef, useState } from 'react';
import { api, onEvent } from './bridge';
import {
  IconArrowLeft, IconArrowRight, IconArrowUp, IconClose, IconEdit, IconFile, IconFolder, IconPlus, IconRefresh, IconTrash,
} from './Icons';
import { PromptDialog } from './Prompt';
import type { BrowseResult, FileEntry, Server, SessionStatus } from './types';

type Side = 'local' | 'remote';
interface Transfer {
  id: string;
  name: string;
  direction: 'up' | 'down';
  done: number;
  total: number;
  state: 'running' | 'done' | 'error' | 'cancelled';
  message?: string;
}

const fmtSize = (n: number) => {
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
const fmtDate = (iso: string) => {
  const d = new Date(iso);
  return isNaN(d.getTime()) || d.getFullYear() < 1980
    ? ''
    : d.toLocaleString('de-CH', { dateStyle: 'short', timeStyle: 'short' });
};

/** Pfad in anklickbare Teile zerlegen (Windows "C:\\a\\b" oder Unix "/a/b"). */
function crumbs(path: string, side: Side): { label: string; path: string }[] {
  if (side === 'remote') {
    const parts = path.split('/').filter(Boolean);
    return [{ label: '/', path: '/' }, ...parts.map((p, i) => ({ label: p, path: '/' + parts.slice(0, i + 1).join('/') }))];
  }
  const win = path.includes('\\') || /^[A-Za-z]:/.test(path);
  const sep = win ? '\\' : '/';
  const parts = path.split(/[\\/]/).filter(Boolean);
  const out: { label: string; path: string }[] = [{ label: 'Dieser PC', path: '' }];
  parts.forEach((p, i) => {
    const joined = parts.slice(0, i + 1).join(sep);
    out.push({ label: p, path: win ? (i === 0 ? joined + '\\' : joined) : '/' + joined });
  });
  return out;
}

const joinPath = (dir: string, name: string, side: Side) => {
  const sep = side === 'remote' || !dir.includes('\\') ? '/' : '\\';
  return dir.endsWith(sep) ? dir + name : dir + sep + name;
};

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
  const [local, setLocal] = useState<BrowseResult | null>(null);
  const [remote, setRemote] = useState<BrowseResult | null>(null);
  const [sel, setSel] = useState<Record<Side, Set<string>>>({ local: new Set(), remote: new Set() });
  const [transfers, setTransfers] = useState<Transfer[]>([]);
  const [prompt, setPrompt] = useState<null | { kind: 'mkdir' | 'rename'; side: Side; entry?: FileEntry }>(null);
  const [dropSide, setDropSide] = useState<Side | null>(null);
  const lastClick = useRef<Record<Side, string | null>>({ local: null, remote: null });
  const remotePath = useRef('/');
  const localPath = useRef('');
  const activeRef = useRef(active);
  activeRef.current = active;

  const setStatus = useCallback(
    (s: SessionStatus) => {
      setStatusState(s);
      onStatus(tabId, s);
    },
    [onStatus, tabId],
  );

  const loadLocal = useCallback(async (path: string) => {
    try {
      const r = path === '' ? await api.localRoots() : await api.localBrowse(path);
      localPath.current = r.path;
      setLocal(r);
      setSel((s) => ({ ...s, local: new Set() }));
    } catch (e) {
      setError((e as Error).message);
    }
  }, []);

  const loadRemote = useCallback(
    async (path: string) => {
      try {
        const r = await api.sftpList(tabId, path);
        remotePath.current = r.path;
        setRemote(r);
        setSel((s) => ({ ...s, remote: new Set() }));
        setError('');
      } catch (e) {
        setError((e as Error).message);
      }
    },
    [tabId],
  );

  // Verbinden
  useEffect(() => {
    let cancelled = false;
    setStatus('connecting');
    setError('');
    api
      .sftpOpen(tabId, server.id)
      .then(async ({ home }) => {
        if (cancelled) return;
        setStatus('connected');
        await loadRemote(home);
      })
      .catch((e: Error) => {
        if (cancelled) return;
        setStatus('error');
        setError(e.message);
      });
    return () => {
      cancelled = true;
      void api.sftpClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey]);

  // Lokale Ansicht: Startordner (Home) beim ersten Mal
  useEffect(() => {
    if (!local) void api.localBrowse('').then((r) => { localPath.current = r.path; setLocal(r); }).catch(() => undefined);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Fortschritt
  useEffect(
    () =>
      onEvent((e) => {
        if (e.event !== 'sftp.progress' || e.sessionId !== tabId) return;
        setTransfers((prev) =>
          prev.map((t) => (t.id === e.transferId ? { ...t, name: e.name, done: e.done, total: e.total, state: e.finished ? t.state : 'running' } : t)),
        );
      }),
    [tabId],
  );

  // ------------------------------------------------------------------ Aktionen
  const entriesOf = (side: Side) => (side === 'local' ? local?.entries : remote?.entries) ?? [];
  const selected = (side: Side) => entriesOf(side).filter((e) => sel[side].has(e.path));

  function select(side: Side, entry: FileEntry, e: React.MouseEvent) {
    setSel((prev) => {
      const next = new Set(e.ctrlKey || e.metaKey ? prev[side] : []);
      const list = entriesOf(side);
      if (e.shiftKey && lastClick.current[side]) {
        const a = list.findIndex((x) => x.path === lastClick.current[side]);
        const b = list.findIndex((x) => x.path === entry.path);
        list.slice(Math.min(a, b), Math.max(a, b) + 1).forEach((x) => next.add(x.path));
      } else if ((e.ctrlKey || e.metaKey) && prev[side].has(entry.path)) next.delete(entry.path);
      else next.add(entry.path);
      return { ...prev, [side]: next };
    });
    if (!e.shiftKey) lastClick.current[side] = entry.path;
  }

  async function transfer(direction: 'up' | 'down', entries: FileEntry[]) {
    if (entries.length === 0) return;
    const destList = direction === 'up' ? remote?.entries : local?.entries;
    const clash = entries.filter((en) => destList?.some((d) => d.name === en.name));
    if (clash.length > 0 && !confirm(`${clash.map((c) => c.name).join(', ')} existiert am Ziel bereits und wird überschrieben. Fortfahren?`)) return;

    const id = crypto.randomUUID();
    const label = entries.length === 1 ? entries[0].name : `${entries.length} Elemente`;
    setTransfers((prev) => [{ id, name: label, direction, done: 0, total: 0, state: 'running' as const }, ...prev].slice(0, 8));
    try {
      if (direction === 'up') await api.sftpUpload(tabId, id, entries.map((x) => x.path), remotePath.current);
      else await api.sftpDownload(tabId, id, entries.map((x) => x.path), localPath.current);
      setTransfers((prev) => prev.map((t) => (t.id === id ? { ...t, state: 'done', done: t.total } : t)));
      if (direction === 'up') await loadRemote(remotePath.current);
      else await loadLocal(localPath.current);
    } catch (e) {
      const msg = (e as Error).message;
      setTransfers((prev) => prev.map((t) => (t.id === id ? { ...t, state: msg === 'Abgebrochen.' ? 'cancelled' : 'error', message: msg } : t)));
    }
  }

  async function removeSelected() {
    const items = selected('remote');
    if (items.length === 0) return;
    if (!confirm(`${items.length === 1 ? `«${items[0].name}»` : `${items.length} Elemente`} auf dem Server endgültig löschen?${items.some((i) => i.isDir) ? '\nOrdner werden mit Inhalt gelöscht.' : ''}`)) return;
    try {
      for (const it of items) await api.sftpDelete(tabId, it.path);
    } catch (e) {
      setError((e as Error).message);
    }
    await loadRemote(remotePath.current);
  }

  async function submitPrompt(value: string) {
    const p = prompt;
    setPrompt(null);
    if (!p) return;
    try {
      if (p.kind === 'mkdir') {
        if (p.side === 'remote') {
          await api.sftpMkdir(tabId, joinPath(remotePath.current, value, 'remote'));
          await loadRemote(remotePath.current);
        } else {
          await api.localMkdir(joinPath(localPath.current, value, 'local'));
          await loadLocal(localPath.current);
        }
      } else if (p.entry) {
        const dir = remotePath.current;
        await api.sftpRename(tabId, p.entry.path, joinPath(dir, value, 'remote'));
        await loadRemote(dir);
      }
    } catch (e) {
      setError((e as Error).message);
    }
  }

  // Drag & Drop zwischen den Seiten
  const onDragStart = (side: Side, entry: FileEntry, e: React.DragEvent) => {
    if (!sel[side].has(entry.path)) setSel((s) => ({ ...s, [side]: new Set([entry.path]) }));
    e.dataTransfer.setData('application/x-msv-side', side);
    e.dataTransfer.effectAllowed = 'copy';
  };
  const onDrop = (target: Side, e: React.DragEvent) => {
    e.preventDefault();
    setDropSide(null);
    const from = e.dataTransfer.getData('application/x-msv-side') as Side | '';
    if (!from || from === target) return;
    const items = selected(from).length > 0 ? selected(from) : [];
    void transfer(from === 'local' ? 'up' : 'down', items);
  };

  // Tastatur: Entf löscht, F2 benennt um (nur Server-Seite)
  const onKey = (side: Side, e: React.KeyboardEvent) => {
    if (side !== 'remote') return;
    if (e.key === 'Delete') void removeSelected();
    if (e.key === 'F2') {
      const one = selected('remote');
      if (one.length === 1) setPrompt({ kind: 'rename', side, entry: one[0] });
    }
  };

  // ------------------------------------------------------------------ Darstellung
  const pane = (side: Side) => {
    const data = side === 'local' ? local : remote;
    const title = side === 'local' ? 'Dieser PC' : server.name;
    const navigate = (p: string) => (side === 'local' ? loadLocal(p) : loadRemote(p));
    const count = sel[side].size;
    return (
      <section
        className={'sftp-pane' + (dropSide === side ? ' drop' : '')}
        onDragOver={(e) => {
          e.preventDefault();
          if (dropSide !== side) setDropSide(side);
        }}
        onDragLeave={() => setDropSide((d) => (d === side ? null : d))}
        onDrop={(e) => onDrop(side, e)}
        onKeyDown={(e) => onKey(side, e)}
        tabIndex={0}
      >
        <header>
          <span className="sftp-title">{title}</span>
          <span className="grow" />
          <button className="ghost icon-only" title="Ordner nach oben" disabled={!data || (data.parent === '' && data.path === '')} onClick={() => data && navigate(data.parent)}>
            <IconArrowUp size={15} />
          </button>
          <button className="ghost icon-only" title="Neu laden" onClick={() => navigate(data?.path ?? '')}>
            <IconRefresh size={15} />
          </button>
          <button className="ghost icon-only" title="Neuer Ordner" disabled={!data || (side === 'local' && data.path === '')} onClick={() => setPrompt({ kind: 'mkdir', side })}>
            <IconPlus size={15} />
          </button>
          {side === 'remote' && (
            <>
              <button className="ghost icon-only" title="Umbenennen (F2)" disabled={count !== 1} onClick={() => setPrompt({ kind: 'rename', side, entry: selected('remote')[0] })}>
                <IconEdit size={15} />
              </button>
              <button className="ghost icon-only danger" title="Löschen (Entf)" disabled={count === 0} onClick={() => void removeSelected()}>
                <IconTrash size={15} />
              </button>
            </>
          )}
        </header>
        <nav className="crumbs">
          {data &&
            crumbs(data.path, side).map((c, i, arr) => (
              <span key={c.path + i}>
                <button className="crumb" onClick={() => navigate(c.path)}>
                  {c.label}
                </button>
                {i < arr.length - 1 && <span className="sep">›</span>}
              </span>
            ))}
        </nav>
        <div className="file-head">
          <span className="c-name">Name</span>
          <span className="c-size">Grösse</span>
          <span className="c-date">Geändert</span>
          {side === 'remote' && <span className="c-mode">Rechte</span>}
        </div>
        <div className="file-list">
          {data?.entries.length === 0 && <div className="muted pad-sm">Dieser Ordner ist leer.</div>}
          {data?.entries.map((en) => (
            <div
              key={en.path}
              className={'file-row' + (sel[side].has(en.path) ? ' sel' : '')}
              draggable
              onDragStart={(e) => onDragStart(side, en, e)}
              onClick={(e) => select(side, en, e)}
              onDoubleClick={() => (en.isDir ? navigate(en.path) : void transfer(side === 'local' ? 'up' : 'down', [en]))}
            >
              <span className="c-name">
                <span className={'f-ico' + (en.isDir ? ' dir' : '')}>{en.isDir ? <IconFolder size={16} /> : <IconFile size={16} />}</span>
                <span className="f-name">{en.name}</span>
              </span>
              <span className="c-size muted">{en.isDir ? '' : fmtSize(en.size)}</span>
              <span className="c-date muted">{fmtDate(en.modified)}</span>
              {side === 'remote' && <span className="c-mode muted mono">{en.mode}</span>}
            </div>
          ))}
        </div>
        <footer className="muted small">
          {data ? `${data.entries.length} Einträge` : ''}
          {count > 0 ? ` · ${count} ausgewählt` : ''}
        </footer>
      </section>
    );
  };

  return (
    <div className="sftp">
      <div className="sftp-bar">
        <span className="session-title">{server.name}</span>
        <span className="muted">SFTP · {server.username ? `${server.username}@` : ''}{server.host}:{server.port}</span>
        <span className={`pill ${status}`}>
          <span className="dot-sm" />
          {status === 'connected' ? 'Verbunden' : status === 'connecting' ? 'Verbinde…' : 'Fehler'}
        </span>
        <span className="grow" />
        <button className="ghost" onClick={onReconnect}>
          <IconRefresh size={15} /> Neu verbinden
        </button>
        <button className="ghost" onClick={onEdit}>
          <IconEdit size={15} /> Host
        </button>
      </div>

      {status === 'error' && !remote ? (
        <div className="sftp-fail">
          <div className="error">{error || 'Verbindung fehlgeschlagen.'}</div>
          <button className="primary" onClick={onReconnect}>
            Neu verbinden
          </button>
        </div>
      ) : (
        <div className="sftp-main">
          {pane('local')}
          <div className="sftp-mid">
            <button className="primary icon-only" title="Auswahl hochladen" disabled={sel.local.size === 0 || status !== 'connected'} onClick={() => void transfer('up', selected('local'))}>
              <IconArrowRight size={17} />
            </button>
            <button className="primary icon-only" title="Auswahl herunterladen" disabled={sel.remote.size === 0 || status !== 'connected'} onClick={() => void transfer('down', selected('remote'))}>
              <IconArrowLeft size={17} />
            </button>
          </div>
          {pane('remote')}
        </div>
      )}

      {error && status === 'connected' && (
        <div className="sftp-error">
          <span className="error">{error}</span>
          <button className="ghost icon-only" onClick={() => setError('')}>
            <IconClose size={14} />
          </button>
        </div>
      )}

      {transfers.length > 0 && (
        <div className="transfers">
          {transfers.map((t) => (
            <div key={t.id} className={'transfer ' + t.state}>
              <span className="t-dir">{t.direction === 'up' ? '↑' : '↓'}</span>
              <span className="t-name">{t.name}</span>
              <div className="t-bar">
                <i style={{ width: `${t.total > 0 ? Math.min(100, (t.done / t.total) * 100) : t.state === 'done' ? 100 : 4}%` }} />
              </div>
              <span className="t-info muted small">
                {t.state === 'running' && (t.total > 0 ? `${fmtSize(t.done)} / ${fmtSize(t.total)}` : 'Starte…')}
                {t.state === 'done' && 'Fertig'}
                {t.state === 'cancelled' && 'Abgebrochen'}
                {t.state === 'error' && <span className="error">{t.message}</span>}
              </span>
              {t.state === 'running' ? (
                <button className="ghost icon-only" title="Abbrechen" onClick={() => void api.sftpCancel(t.id)}>
                  <IconClose size={14} />
                </button>
              ) : (
                <button className="ghost icon-only" title="Entfernen" onClick={() => setTransfers((p) => p.filter((x) => x.id !== t.id))}>
                  <IconClose size={14} />
                </button>
              )}
            </div>
          ))}
        </div>
      )}

      {prompt && (
        <PromptDialog
          title={prompt.kind === 'mkdir' ? 'Neuer Ordner' : 'Umbenennen'}
          label={prompt.kind === 'mkdir' ? 'Name des Ordners' : 'Neuer Name'}
          initial={prompt.kind === 'rename' ? prompt.entry?.name : ''}
          confirmLabel={prompt.kind === 'mkdir' ? 'Anlegen' : 'Umbenennen'}
          onSubmit={(v) => void submitPrompt(v)}
          onClose={() => setPrompt(null)}
        />
      )}
    </div>
  );
}
