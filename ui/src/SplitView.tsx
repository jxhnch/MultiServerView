import { useEffect, useRef, useState } from 'react';
import { IconArrowRight, IconBroadcast, IconClose, IconEdit, IconGrid, IconRefresh } from './Icons';
import { HostTile } from './HostTile';
import { TerminalSlot } from './TerminalPool';
import type { Server, SessionStatus } from './types';

export interface SplitPane {
  id: string;
  serverId: string;
  reconnectKey: number;
}

/**
 * Split view: several terminals side by side in one tab, made by dragging a tab onto another one.
 * With "Broadcast", everything you type into one terminal goes to all others at the same time.
 * The terminals themselves live in the TerminalPool of the app; here they only get their places.
 */
export function SplitView({
  panes,
  byId,
  statuses,
  nodes,
  broadcast,
  onBroadcast,
  onReconnect,
  onClosePane,
  onPopOut,
  onEdit,
}: {
  panes: SplitPane[];
  byId: Map<string, Server>;
  statuses: Record<string, SessionStatus>;
  nodes: Map<string, HTMLDivElement>;
  broadcast: boolean;
  onBroadcast: (on: boolean) => void;
  onReconnect: (paneId: string) => void;
  onClosePane: (paneId: string) => void;
  /** Gives this terminal its own tab again. */
  onPopOut: (paneId: string) => void;
  onEdit: (s: Server) => void;
}) {
  const [focused, setFocused] = useState<string | null>(null);
  const grid = useRef<HTMLDivElement>(null);

  // A native listener: the terminal is not a React child of its pane, so React's focus events do not pass through here.
  useEffect(() => {
    const el = grid.current;
    if (!el) return;
    const onFocus = (e: FocusEvent) => setFocused((e.target as Element).closest<HTMLElement>('[data-pane]')?.dataset.pane ?? null);
    el.addEventListener('focusin', onFocus);
    return () => el.removeEventListener('focusin', onFocus);
  }, []);

  const cols = panes.length <= 1 ? 1 : panes.length <= 4 ? 2 : panes.length <= 9 ? 3 : 4;

  return (
    <div className="split">
      <div className="split-bar">
        <span className="tile sm md-neutral">
          <IconGrid size={13} />
        </span>
        <span className="session-title">Split view</span>
        <span className="muted small">{panes.length} Terminals · drag another tab in here to add it</span>
        <span className="grow" />
        <button className={'bc-btn' + (broadcast ? ' on' : '')} onClick={() => onBroadcast(!broadcast)} title="Send input to all terminals at once">
          <IconBroadcast size={15} /> Broadcast {broadcast ? 'on' : 'off'}
        </button>
      </div>

      {broadcast && (
        <div className="bc-banner">
          <IconBroadcast size={15} /> Broadcast active: everything you type goes to all {panes.length} terminals. Be careful with commands that have an effect!
          <button className="link" onClick={() => onBroadcast(false)}>
            Turn off
          </button>
        </div>
      )}

      <div className="split-grid" ref={grid} style={{ gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))` }}>
        {panes.map((p) => {
          const server = byId.get(p.serverId);
          return (
            <div key={p.id} data-pane={p.id} className={'split-pane' + (focused === p.id ? ' focus' : '') + (broadcast ? ' bc' : '')}>
              <div className="pane-head">
                {server ? <HostTile server={server} size="sm" /> : null}
                <span className="pane-name">{server?.name ?? '(host deleted)'}</span>
                <span className={`status-dot ${statuses[p.id] ?? 'connecting'}`} />
                <span className="grow" />
                {server && (
                  <button className="ghost icon-only" title="Host details" onClick={() => onEdit(server)}>
                    <IconEdit size={13} />
                  </button>
                )}
                <button className="ghost icon-only" title="Reconnect" onClick={() => onReconnect(p.id)}>
                  <IconRefresh size={14} />
                </button>
                <button className="ghost icon-only" title="Back into its own tab (the session stays)" onClick={() => onPopOut(p.id)}>
                  <IconArrowRight size={14} />
                </button>
                <button className="ghost icon-only" title="Close this terminal" onClick={() => onClosePane(p.id)}>
                  <IconClose size={14} />
                </button>
              </div>
              <div className="pane-body">{server && <TerminalSlot nodes={nodes} id={p.id} />}</div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
