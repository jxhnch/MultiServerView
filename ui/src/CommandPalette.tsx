import { useEffect, useMemo, useRef, useState } from 'react';
import { IconCloud, IconKey, IconLock, IconPlus, ProtocolIcon } from './Icons';
import { filterServers } from './search';
import type { Server } from './types';

export type PaletteAction = 'new' | 'keys' | 'cloud' | 'lock';

interface Item {
  key: string;
  server?: Server;
  action?: PaletteAction;
  label: string;
  hint?: string;
}

const ACTIONS: Item[] = [
  { key: 'a-new', action: 'new', label: 'Neuen Host anlegen', hint: 'Ctrl+Shift+N' },
  { key: 'a-keys', action: 'keys', label: 'Schlüssel verwalten' },
  { key: 'a-cloud', action: 'cloud', label: 'Cloud & Sync' },
  { key: 'a-lock', action: 'lock', label: 'Vault sperren' },
];

export function CommandPalette({
  servers,
  openServerIds,
  onOpen,
  onAction,
  onClose,
}: {
  servers: Server[];
  openServerIds: Set<string>;
  onOpen: (s: Server, forceNew: boolean) => void;
  onAction: (a: PaletteAction) => void;
  onClose: () => void;
}) {
  const [query, setQuery] = useState('');
  const [index, setIndex] = useState(0);
  const listRef = useRef<HTMLDivElement>(null);

  const items = useMemo<Item[]>(() => {
    const hosts = filterServers(servers, query).slice(0, 40).map((s) => ({ key: s.id, server: s, label: s.name }));
    const q = query.trim().toLowerCase();
    const actions = ACTIONS.filter((a) => q.length > 0 && a.label.toLowerCase().includes(q));
    // Ohne Eingabe: zuerst Hosts (zuletzt benutzt oben), Aktionen am Ende.
    return q.length === 0 ? [...hosts, ...ACTIONS] : [...hosts, ...actions];
  }, [servers, query]);

  useEffect(() => setIndex(0), [query]);
  useEffect(() => {
    listRef.current?.querySelector('.active')?.scrollIntoView({ block: 'nearest' });
  }, [index]);

  function choose(item: Item | undefined, forceNew: boolean) {
    if (!item) return;
    if (item.server) onOpen(item.server, forceNew);
    else if (item.action) onAction(item.action);
  }

  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setIndex((i) => Math.min(i + 1, Math.max(items.length - 1, 0)));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setIndex((i) => Math.max(i - 1, 0));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      choose(items[index], e.shiftKey);
    } else if (e.key === 'Escape') {
      onClose();
    }
  }

  const actionIcon = (a?: PaletteAction) =>
    a === 'new' ? <IconPlus size={16} /> : a === 'keys' ? <IconKey size={16} /> : a === 'cloud' ? <IconCloud size={16} /> : <IconLock size={16} />;

  return (
    <div className="overlay top" onMouseDown={onClose}>
      <div className="palette" onMouseDown={(e) => e.stopPropagation()}>
        <input
          autoFocus
          placeholder="Host suchen und verbinden…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={onKey}
        />
        <div className="palette-list" ref={listRef}>
          {items.map((it, i) => (
            <div
              key={it.key}
              className={'palette-item' + (i === index ? ' active' : '')}
              onMouseMove={() => setIndex(i)}
              onClick={(e) => choose(it, e.shiftKey)}
            >
              {it.server ? (
                <span className={`tile sm ${it.server.protocol}`}>
                  <ProtocolIcon protocol={it.server.protocol} size={14} />
                </span>
              ) : (
                <span className="tile sm action">{actionIcon(it.action)}</span>
              )}
              <span className="pname">{it.label}</span>
              {it.server && (
                <span className="muted phost">
                  {it.server.username ? `${it.server.username}@` : ''}
                  {it.server.host}
                </span>
              )}
              {it.server && openServerIds.has(it.server.id) && <span className="chip live">offen</span>}
              {it.server?.group && <span className="chip">{it.server.group}</span>}
              {it.hint && <kbd>{it.hint}</kbd>}
            </div>
          ))}
          {items.length === 0 && <div className="palette-empty">Kein Treffer.</div>}
        </div>
        <div className="palette-foot muted">
          <span>
            <kbd>↑</kbd> <kbd>↓</kbd> wählen
          </span>
          <span>
            <kbd>↵</kbd> verbinden
          </span>
          <span>
            <kbd>⇧ ↵</kbd> zusätzliche Session
          </span>
          <span>
            <kbd>Esc</kbd> schliessen
          </span>
        </div>
      </div>
    </div>
  );
}
