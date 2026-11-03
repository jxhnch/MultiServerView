import { useMemo, useRef } from 'react';
import { IconPlus, IconSearch, IconServer, IconStar, IconUpload, ProtocolIcon } from './Icons';
import { filterServers, parseTarget } from './search';
import type { Server } from './types';

/** Termius-Stil: Suchzeile oben, darunter schlichte Karten ("ssh, root"). */
export function HostsPage({
  servers,
  query,
  setQuery,
  selectedId,
  openServerIds,
  onSelect,
  onConnect,
  onNew,
  onQuick,
  onImport,
}: {
  servers: Server[];
  query: string;
  setQuery: (q: string) => void;
  selectedId?: string;
  openServerIds: Set<string>;
  onSelect: (s: Server) => void;
  onConnect: (s: Server, forceNew: boolean) => void;
  onNew: () => void;
  onQuick: (draft: Partial<Server>) => void;
  onImport: (file: File) => void;
}) {
  const fileRef = useRef<HTMLInputElement>(null);
  const q = query.trim();

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
        <input ref={fileRef} type="file" accept=".xml" hidden onChange={(e) => e.target.files?.[0] && onImport(e.target.files[0])} />
        <button className="tool" title="Hosts aus mRemoteNG (confCons.xml) importieren" onClick={() => fileRef.current?.click()}>
          <IconUpload size={15} /> Import
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
                    onDoubleClick={(e) => onConnect(s, e.shiftKey)}
                    onKeyDown={(e) => e.key === 'Enter' && onConnect(s, e.shiftKey)}
                  >
                    <span className={`tile ${s.protocol}`}>
                      <ProtocolIcon protocol={s.protocol} size={20} />
                    </span>
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
          <p className="muted small">Doppelklick oder Enter verbindet · Klick zeigt Details · Shift öffnet eine zusätzliche Session</p>
        </>
      )}
    </div>
  );
}
