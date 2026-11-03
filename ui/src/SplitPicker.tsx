import { useMemo, useState } from 'react';
import { HostTile } from './HostTile';
import { IconClose, IconSearch } from './Icons';
import { filterServers } from './search';
import type { Server } from './types';

/** Mehrere SSH-Hosts auswählen, die zusammen in einer geteilten Ansicht geöffnet werden. */
export function SplitPicker({ servers, onConfirm, onClose }: { servers: Server[]; onConfirm: (ids: string[]) => void; onClose: () => void }) {
  const [query, setQuery] = useState('');
  const [picked, setPicked] = useState<string[]>([]);
  const list = useMemo(() => (query ? filterServers(servers, query) : [...servers].sort((a, b) => a.name.localeCompare(b.name))), [servers, query]);
  const toggle = (id: string) => setPicked((p) => (p.includes(id) ? p.filter((x) => x !== id) : [...p, id]));

  return (
    <div className="overlay" onMouseDown={onClose}>
      <div className="modal" onMouseDown={(e) => e.stopPropagation()} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Geteilte Ansicht</h2>
          <button className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <p className="hint">Wähle mindestens 2 SSH-Hosts. Sie öffnen sich nebeneinander in einem Tab; mit «Broadcast» geht deine Eingabe an alle gleichzeitig.</p>
        <div className="searchbar">
          <IconSearch size={16} />
          <input autoFocus placeholder="Hosts filtern…" value={query} onChange={(e) => setQuery(e.target.value)} />
        </div>
        <div className="pick-list">
          {list.map((s) => (
            <label key={s.id} className={'pick-row' + (picked.includes(s.id) ? ' on' : '')}>
              <input type="checkbox" checked={picked.includes(s.id)} onChange={() => toggle(s.id)} />
              <HostTile server={s} size="sm" />
              <span className="pname">{s.name}</span>
              <span className="muted small grow ellipsis">{s.username ? `${s.username}@` : ''}{s.host}</span>
              {s.group && <span className="chip">{s.group}</span>}
            </label>
          ))}
          {list.length === 0 && <div className="muted pad-sm">Keine SSH-Hosts gefunden.</div>}
        </div>
        <div className="actions">
          <span className="muted small">{picked.length} ausgewählt</span>
          <span className="grow" />
          <button onClick={onClose}>Abbrechen</button>
          <button className="primary" disabled={picked.length < 2} onClick={() => onConfirm(picked)}>
            Öffnen
          </button>
        </div>
      </div>
    </div>
  );
}
