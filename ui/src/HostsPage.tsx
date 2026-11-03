import { useEffect, useMemo, useRef, useState } from 'react';
import { IconEdit, IconFolder, IconGrid, IconPlus, IconSearch, IconServer, IconStar, IconTrash, IconUpload, IconDuplicate, IconPlay } from './Icons';
import { HostTile } from './HostTile';
import { ContextMenu } from './ContextMenu';
import { isNative } from './bridge';
import { filterServers, parseTarget } from './search';
import type { Server } from './types';

export type ImportKind = 'mremoteng' | 'sshconfig' | 'putty-win' | 'putty-reg' | 'csv';
const IMPORTS: { kind: ImportKind; label: string; hint: string; accept: string; nativeOnly?: boolean }[] = [
  { kind: 'mremoteng', label: 'mRemoteNG', hint: 'confCons.xml', accept: '.xml' },
  { kind: 'sshconfig', label: 'OpenSSH-Config', hint: '~/.ssh/config', accept: '*' },
  { kind: 'putty-win', label: 'PuTTY (aus Windows)', hint: 'liest die gespeicherten Sitzungen direkt', accept: '', nativeOnly: true },
  { kind: 'putty-reg', label: 'PuTTY (.reg-Datei)', hint: 'Export aus regedit', accept: '.reg' },
  { kind: 'csv', label: 'CSV-Datei', hint: 'name, host, port, protocol, user, group …', accept: '.csv,.txt' },
];

/** Termius-Stil: Suchzeile oben, darunter schlichte Karten ("ssh, root"). */
export function HostsPage({
  servers,
  query,
  setQuery,
  selectedId,
  openServerIds,
  onSelect,
  onConnect,
  onSftp,
  onFav,
  onDuplicateHost,
  onDelete,
  onNew,
  onQuick,
  onImport,
  onSplit,
}: {
  servers: Server[];
  query: string;
  setQuery: (q: string) => void;
  selectedId?: string;
  openServerIds: Set<string>;
  onSelect: (s: Server) => void;
  onConnect: (s: Server, forceNew: boolean) => void;
  onSftp: (s: Server) => void;
  onFav: (s: Server) => void;
  onDuplicateHost: (s: Server) => void;
  onDelete: (s: Server) => void;
  onNew: () => void;
  onQuick: (draft: Partial<Server>) => void;
  onImport: (kind: ImportKind, file?: File) => void;
  onSplit: () => void;
}) {
  const fileRef = useRef<HTMLInputElement>(null);
  const pendingKind = useRef<ImportKind>('mremoteng');
  const importRef = useRef<HTMLDivElement>(null);
  const [importOpen, setImportOpen] = useState(false);
  const [menu, setMenu] = useState<{ x: number; y: number; server: Server } | null>(null);
  const q = query.trim();

  useEffect(() => {
    if (!importOpen) return;
    const onDown = (e: MouseEvent) => !importRef.current?.contains(e.target as Node) && setImportOpen(false);
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [importOpen]);

  function startImport(it: (typeof IMPORTS)[number]) {
    setImportOpen(false);
    pendingKind.current = it.kind;
    if (it.kind === 'putty-win') return onImport(it.kind);
    if (fileRef.current) {
      fileRef.current.accept = it.accept === '*' ? '' : it.accept;
      fileRef.current.click();
    }
  }

  const filtered = useMemo(
    () => (q ? filterServers(servers, q) : [...servers].sort((a, b) => a.name.localeCompare(b.name))),
    [servers, q],
  );

  // Gruppen wie Ordner; ohne Gruppen steht einfach "Hosts" darüber.
  const grouped = useMemo(() => {
    const m = new Map<string, Server[]>();
    for (const s of filtered) {
      const g = s.group || '';
      m.set(g, [...(m.get(g) ?? []), s]);
    }
    const hasGroups = [...m.keys()].some((g) => g !== '');
    return [...m.entries()]
      .sort(([a], [b]) => (a === '' ? 1 : b === '' ? -1 : a.localeCompare(b)))
      .map(([g, list]) => [g || (hasGroups ? 'Ohne Gruppe' : 'Hosts'), list] as const);
  }, [filtered]);

  const target = q ? parseTarget(q) : null;
  const canConnect = q.length > 0 && (filtered.length > 0 || target !== null);

  function submit(forceNew = false) {
    if (!q) return;
    if (filtered.length > 0) onConnect(filtered[0], forceNew);
    else if (target) onQuick(target);
  }

  const sub = (s: Server) => `${s.protocol}, ${s.username || s.host}`;

  return (
    <div className="hosts-main">
      <div className="searchbar">
        <IconSearch size={16} />
        <input
          autoFocus
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key !== 'Enter') return;
            e.preventDefault(); // verhindert, dass das Enter im gleich geöffneten Panel landet
            submit(e.shiftKey);
          }}
          placeholder="Host suchen oder  ssh user@hostname  eingeben…"
        />
        <button className={canConnect ? 'ready' : ''} disabled={!canConnect} onClick={() => submit()}>
          Connect
        </button>
      </div>

      <div className="toolbar">
        <button className="primary" onClick={onNew}>
          <IconPlus size={15} /> Neuer Host
        </button>
        <input
          ref={fileRef}
          type="file"
          hidden
          onChange={(e) => {
            const f = e.target.files?.[0];
            e.target.value = '';
            if (f) onImport(pendingKind.current, f);
          }}
        />
        <div className="pop-anchor" ref={importRef}>
          <button className="tool" onClick={() => setImportOpen((o) => !o)}>
            <IconUpload size={15} /> Import
          </button>
          {importOpen && (
            <div className="popover light" style={{ left: 0, right: 'auto', width: 330 }}>
              <div className="pop-section">Hosts importieren aus</div>
              <div className="pop-list" style={{ width: '100%' }}>
                {IMPORTS.filter((i) => !i.nativeOnly || isNative).map((it) => (
                  <button key={it.kind} className="pop-item" onClick={() => startImport(it)}>
                    <span className="pop-name">{it.label}</span>
                    <span className="pop-sub">{it.hint}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
        <button className="tool" title="Mehrere SSH-Hosts nebeneinander in einem Tab" onClick={onSplit}>
          <IconGrid size={15} /> Geteilte Ansicht
        </button>
      </div>

      {servers.length === 0 ? (
        <div className="empty-card">
          <IconServer size={30} />
          <h3>Noch keine Hosts</h3>
          <p>Lege einen Host an, tippe oben «ssh user@server» ein oder importiere deine mRemoteNG-Liste.</p>
        </div>
      ) : (
        <>
          {filtered.length === 0 && (
            <p className="muted">
              Kein Host gefunden.{target ? ' Enter legt «' + (target.host ?? '') + '» neu an und verbindet.' : ''}
            </p>
          )}
          {grouped.map(([name, list]) => (
            <section key={name}>
              <div className="sec-label">{name}</div>
              <div className="hgrid" style={{ marginTop: 10 }}>
                {list.map((s) => (
                  <div
                    key={s.id}
                    className={'hcard' + (selectedId === s.id ? ' sel' : '')}
                    tabIndex={0}
                    title={`${s.name} – ${s.host}:${s.port}`}
                    onClick={() => onSelect(s)}
                    onContextMenu={(e) => {
                      e.preventDefault();
                      setMenu({ x: e.clientX, y: e.clientY, server: s });
                    }}
                    onDoubleClick={(e) => onConnect(s, e.shiftKey)}
                    onKeyDown={(e) => e.key === 'Enter' && onConnect(s, e.shiftKey)}
                  >
                    <HostTile server={s} />
                    <span className="hcard-text">
                      <span className="hcard-name">{s.name}</span>
                      <span className="hcard-sub">{sub(s)}</span>
                    </span>
                    {s.favorite && (
                      <span className="fav" title="Favorit">
                        <IconStar size={13} filled />
                      </span>
                    )}
                    {openServerIds.has(s.id) && <span className="status-dot connected live-dot" title="Session offen" />}
                  </div>
                ))}
              </div>
            </section>
          ))}
          <p className="muted small">Doppelklick oder Enter verbindet · Klick zeigt Details · Rechtsklick für mehr · Shift öffnet eine zusätzliche Session</p>
        </>
      )}

      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(null)}
          items={[
            { label: 'Verbinden', icon: <IconPlay size={14} />, onClick: () => onConnect(menu.server, false) },
            { label: 'In neuem Tab verbinden', icon: <IconDuplicate size={14} />, onClick: () => onConnect(menu.server, true) },
            ...(menu.server.protocol === 'ssh' ? [{ label: 'SFTP öffnen', icon: <IconFolder size={14} />, onClick: () => onSftp(menu.server) }] : []),
            { label: 'Details bearbeiten', icon: <IconEdit size={14} />, onClick: () => onSelect(menu.server), separator: true },
            { label: menu.server.favorite ? 'Favorit entfernen' : 'Als Favorit markieren', icon: <IconStar size={14} filled={menu.server.favorite} />, onClick: () => onFav(menu.server) },
            { label: 'Duplizieren', icon: <IconDuplicate size={14} />, onClick: () => onDuplicateHost(menu.server) },
            { label: 'Löschen', icon: <IconTrash size={14} />, danger: true, separator: true, onClick: () => onDelete(menu.server) },
          ]}
        />
      )}
    </div>
  );
}
