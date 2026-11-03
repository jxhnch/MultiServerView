import { useEffect, useMemo, useRef, useState } from 'react';
import type { Server } from './types';
import { filterServers } from './search';

export function CommandPalette({
  servers,
  onOpen,
  onNew,
  onClose,
}: {
  servers: Server[];
  onOpen: (s: Server) => void;
  onNew: () => void;
  onClose: () => void;
}) {
  const [query, setQuery] = useState('');
  const [index, setIndex] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  const results = useMemo(() => filterServers(servers, query).slice(0, 50), [servers, query]);

  useEffect(() => setIndex(0), [query]);
  useEffect(() => {
    listRef.current?.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
  }, [index]);

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setIndex((i) => Math.min(i + 1, Math.max(results.length - 1, 0)));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setIndex((i) => Math.max(i - 1, 0));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const s = results[index];
      if (s) onOpen(s);
    } else if (e.key === 'Escape') {
      onClose();
    }
  }

  return (
    <div className="overlay top" onMouseDown={onClose}>
      <div className="palette" onMouseDown={(e) => e.stopPropagation()}>
        <input
          autoFocus
          placeholder="Server suchen und verbinden…  (Name, Host, Gruppe, Tag)"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={onKey}
        />
        <div className="palette-list" ref={listRef}>
          {results.map((s, i) => (
            <div
              key={s.id}
              className={'palette-item' + (i === index ? ' active' : '')}
              onMouseMove={() => setIndex(i)}
              onClick={() => onOpen(s)}
            >
              <span className={`badge ${s.protocol}`}>{s.protocol}</span>
              <span className="pname">{s.name}</span>
              <span className="muted phost">{s.host}</span>
              {s.group && <span className="chip">{s.group}</span>}
            </div>
          ))}
          {results.length === 0 && (
            <div className="palette-empty">
              Kein Treffer.{' '}
              <button className="link" onClick={onNew}>
                Neuen Server anlegen
              </button>
            </div>
          )}
        </div>
        <div className="palette-foot muted">
          <span>↑↓ wählen</span>
          <span>↵ verbinden</span>
          <span>Esc schliessen</span>
        </div>
      </div>
    </div>
  );
}
