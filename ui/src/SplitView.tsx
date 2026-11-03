import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { TerminalView } from './TerminalView';
import { IconBroadcast, IconClose, IconEdit, IconGrid, IconPlus, IconRefresh } from './Icons';
import { HostTile } from './HostTile';
import type { Server, SessionStatus } from './types';

export interface SplitPane {
  id: string;
  serverId: string;
  reconnectKey: number;
}

/**
 * Split view: several SSH terminals side by side in one tab.
 * With "Broadcast", everything you type into one terminal goes to all others at the same time.
 */
export function SplitView({
  tabId,
  panes,
  onPanesChange,
  servers,
  byId,
  active,
  onStatus,
  onEdit,
  onOpenSftp,
  onManageSnippets,
}: {
  tabId: string;
  panes: SplitPane[];
  onPanesChange: (panes: SplitPane[]) => void;
  servers: Server[];
  byId: Map<string, Server>;
  active: boolean;
  onStatus: (tabId: string, status: SessionStatus) => void;
  onEdit: (s: Server) => void;
  onOpenSftp: (s: Server) => void;
  onManageSnippets: () => void;
}) {
  const [broadcast, setBroadcast] = useState(false);
  const [focused, setFocused] = useState<string | null>(null);
  const [statuses, setStatuses] = useState<Record<string, SessionStatus>>({});
  const [adding, setAdding] = useState(false);
  const senders = useRef(new Map<string, (text: string) => void>());
  const addRef = useRef<HTMLDivElement>(null);

  // Overall status for the tab icon
  useEffect(() => {
    const all = panes.map((p) => statuses[p.id] ?? 'connecting');
    onStatus(tabId, all.includes('error') ? 'error' : all.includes('connecting') ? 'connecting' : all.length > 0 && all.every((s) => s === 'closed') ? 'closed' : 'connected');
  }, [statuses, panes, tabId, onStatus]);

  const paneStatus = useCallback((id: string, s: SessionStatus) => setStatuses((p) => (p[id] === s ? p : { ...p, [id]: s })), []);

  const fanOut = useCallback(
    (fromPane: string, data: string) => {
      if (!broadcast) return;
      for (const [id, send] of senders.current) if (id !== fromPane) send(data);
    },
    [broadcast],
  );

  useEffect(() => {
    if (!adding) return;
    const onDown = (e: MouseEvent) => !addRef.current?.contains(e.target as Node) && setAdding(false);
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [adding]);

  const cols = panes.length <= 1 ? 1 : panes.length <= 4 ? 2 : panes.length <= 9 ? 3 : 4;
  const sshHosts = useMemo(() => servers.filter((s) => s.protocol === 'ssh').sort((a, b) => a.name.localeCompare(b.name)), [servers]);
  const bump = (id: string) => onPanesChange(panes.map((p) => (p.id === id ? { ...p, reconnectKey: p.reconnectKey + 1 } : p)));

  return (
    <div className="split">
      <div className="split-bar">
        <span className="tile sm md-neutral">
          <IconGrid size={13} />
        </span>
        <span className="session-title">Split view</span>
        <span className="muted small">{panes.length} Terminals</span>
        <span className="grow" />
        <button className={'bc-btn' + (broadcast ? ' on' : '')} onClick={() => setBroadcast((b) => !b)} title="Send input to all terminals at once">
          <IconBroadcast size={15} /> Broadcast {broadcast ? 'on' : 'off'}
        </button>
        <div className="pop-anchor" ref={addRef}>
          <button onClick={() => setAdding((a) => !a)}>
            <IconPlus size={15} /> Host
          </button>
          {adding && (
            <div className="popover" style={{ width: 300 }}>
              <div className="pop-list" style={{ width: '100%' }}>
                {sshHosts.map((s) => (
                  <button
                    key={s.id}
                    className="pop-item"
                    onClick={() => {
                      setAdding(false);
                      onPanesChange([...panes, { id: crypto.randomUUID(), serverId: s.id, reconnectKey: 0 }]);
                    }}
                  >
                    <span className="pop-name">{s.name}</span>
                    <span className="pop-sub">{s.username ? `${s.username}@` : ''}{s.host}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>

      {broadcast && (
        <div className="bc-banner">
          <IconBroadcast size={15} /> Broadcast active: everything you type goes to all {panes.length} terminals. Be careful with commands that have an effect!
          <button className="link" onClick={() => setBroadcast(false)}>
            Turn off
          </button>
        </div>
      )}

      <div className="split-grid" style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))` }}>
        {panes.map((p) => {
          const server = byId.get(p.serverId);
          const st = statuses[p.id] ?? 'connecting';
          return (
            <div key={p.id} className={'split-pane' + (focused === p.id ? ' focus' : '') + (broadcast ? ' bc' : '')} onFocusCapture={() => setFocused(p.id)}>
              <div className="pane-head">
                {server ? <HostTile server={server} size="sm" /> : null}
                <span className="pane-name">{server?.name ?? '(host deleted)'}</span>
                <span className={`status-dot ${st}`} />
                <span className="grow" />
                {server && (
                  <button className="ghost icon-only" title="Host details" onClick={() => onEdit(server)}>
                    <IconEdit size={13} />
                  </button>
                )}
                <button className="ghost icon-only" title="Reconnect" onClick={() => bump(p.id)}>
                  <IconRefresh size={14} />
                </button>
                <button
                  className="ghost icon-only"
                  title="Close this terminal"
                  onClick={() => {
                    senders.current.delete(p.id);
                    onPanesChange(panes.filter((x) => x.id !== p.id));
                  }}
                >
                  <IconClose size={14} />
                </button>
              </div>
              <div className="pane-body">
                {server && (
                  <TerminalView
                    tabId={p.id}
                    server={server}
                    active={active}
                    reconnectKey={p.reconnectKey}
                    onStatus={paneStatus}
                    onReconnect={() => bump(p.id)}
                    onDuplicate={() => undefined}
                    onEdit={() => onEdit(server)}
                    onOpenSftp={() => onOpenSftp(server)}
                    onManageSnippets={onManageSnippets}
                    bare
                    registerSender={(fn) => (fn ? senders.current.set(p.id, fn) : senders.current.delete(p.id))}
                    onUserInput={(d) => fanOut(p.id, d)}
                  />
                )}
              </div>
            </div>
          );
        })}
        {panes.length === 0 && <div className="muted pad-sm">No terminals left. Add one with "+ Host".</div>}
      </div>
    </div>
  );
}
