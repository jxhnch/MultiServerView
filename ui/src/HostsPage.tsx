import { useMemo, useRef, useState } from 'react';
import { IconEdit, IconPlus, IconServer, IconStar, IconUpload, ProtocolIcon } from './Icons';
import { filterServers, recency, timeAgo } from './search';
import { PROTOCOL_LABEL, type Protocol, type Server } from './types';

export function HostsPage({
  servers,
  query,
  onOpen,
  onEdit,
  onFav,
  onNew,
  onImport,
}: {
  servers: Server[];
  query: string;
  onOpen: (s: Server, forceNew: boolean) => void;
  onEdit: (s: Server) => void;
  onFav: (s: Server) => void;
  onNew: () => void;
  onImport: (file: File) => void;
}) {
  const [proto, setProto] = useState<Protocol | 'all'>('all');
  const fileRef = useRef<HTMLInputElement>(null);

  const filtered = useMemo(() => {
    const base = query ? filterServers(servers, query) : [...servers].sort((a, b) => a.name.localeCompare(b.name));
    return proto === 'all' ? base : base.filter((s) => s.protocol === proto);
  }, [servers, query, proto]);

  const recent = useMemo(
    () => (query || proto !== 'all' ? [] : [...servers].filter((s) => s.lastUsed).sort((a, b) => recency(b) - recency(a)).slice(0, 4)),
    [servers, query, proto],
  );

  const counts = useMemo(
    () => ({
      all: servers.length,
      ssh: servers.filter((s) => s.protocol === 'ssh').length,
      rdp: servers.filter((s) => s.protocol === 'rdp').length,
      vnc: servers.filter((s) => s.protocol === 'vnc').length,
    }),
    [servers],
  );

  const card = (s: Server) => (
    <div key={s.id} className={`card ${s.protocol}`} onClick={(e) => onOpen(s, e.shiftKey)} tabIndex={0} onKeyDown={(e) => e.key === 'Enter' && onOpen(s, e.shiftKey)}>
      <div className="card-top">
        <span className={`proto-ico big ${s.protocol}`}>
          <ProtocolIcon protocol={s.protocol} size={20} />
        </span>
        <span className="grow" />
        <button
          className={'icon-btn star' + (s.favorite ? ' on' : '')}
          title={s.favorite ? 'Favorit entfernen' : 'Als Favorit markieren'}
          onClick={(e) => {
            e.stopPropagation();
            onFav(s);
          }}
        >
          <IconStar size={16} filled={s.favorite} />
        </button>
        <button
          className="icon-btn reveal"
          title="Bearbeiten"
          onClick={(e) => {
            e.stopPropagation();
            onEdit(s);
          }}
        >
          <IconEdit size={15} />
        </button>
      </div>
      <div className="card-name">{s.name}</div>
      <div className="muted card-host">
        {s.username ? `${s.username}@` : ''}
        {s.host}
        {s.port !== { ssh: 22, rdp: 3389, vnc: 5900 }[s.protocol] ? `:${s.port}` : ''}
      </div>
      <div className="card-foot">
        {s.group ? <span className="chip">{s.group}</span> : <span />}
        <span className="muted small">{timeAgo(s.lastUsed)}</span>
      </div>
    </div>
  );

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Hosts</h1>
          <p className="muted">
            Klick verbindet. <kbd>Shift</kbd>-Klick öffnet eine zusätzliche Session zum selben Host.
          </p>
        </div>
        <div className="head-actions">
          <input ref={fileRef} type="file" accept=".xml" hidden onChange={(e) => e.target.files?.[0] && onImport(e.target.files[0])} />
          <button title="Hosts aus mRemoteNG (confCons.xml) importieren" onClick={() => fileRef.current?.click()}>
            <IconUpload size={15} /> Import
          </button>
          <button className="primary" onClick={onNew}>
            <IconPlus size={15} /> Neuer Host
          </button>
        </div>
      </div>

      {servers.length === 0 ? (
        <div className="empty-card">
          <IconServer size={30} />
          <h3>Noch keine Hosts</h3>
          <p className="muted">Lege deinen ersten Host an – oder importiere deine mRemoteNG-Liste (confCons.xml).</p>
          <div className="row">
            <button className="primary" onClick={onNew}>
              Host hinzufügen
            </button>
            <button onClick={() => fileRef.current?.click()}>mRemoteNG importieren</button>
          </div>
        </div>
      ) : (
        <>
          <div className="chips">
            {(['all', 'ssh', 'rdp', 'vnc'] as const).map((p) => (
              <button key={p} className={'fchip' + (proto === p ? ' on' : '')} onClick={() => setProto(p)}>
                {p === 'all' ? 'Alle' : PROTOCOL_LABEL[p]} <span className="count">{counts[p]}</span>
              </button>
            ))}
          </div>

          {recent.length > 0 && (
            <>
              <h2 className="sec">Zuletzt benutzt</h2>
              <div className="grid">{recent.map(card)}</div>
            </>
          )}

          <h2 className="sec">{query ? `Treffer für «${query}»` : 'Alle Hosts'}</h2>
          {filtered.length === 0 ? <p className="muted">Kein Host gefunden.</p> : <div className="grid">{filtered.map(card)}</div>}
        </>
      )}
    </div>
  );
}
