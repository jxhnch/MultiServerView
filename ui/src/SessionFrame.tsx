import { createContext, useContext, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { HostTile } from './HostTile';
import { IconClose, IconDuplicate, IconEdit, IconMonitor, IconRefresh, IconTerminal } from './Icons';
import type { Server, SessionStatus } from './types';

/** Lets a session close its own tab (provided by the app around every session view). */
export const SessionCloseContext = createContext<(() => void) | null>(null);

function ConnectingOverlay({ server }: { server: Server }) {
  const close = useContext(SessionCloseContext);
  const [logs, setLogs] = useState(false);
  useEffect(() => setLogs(false), [server.id]);
  const proto = server.protocol.toUpperCase();
  if (logs)
    return (
      <button className="conn-chip" onClick={() => setLogs(false)}>
        <span className="conn-spin sm" /> Connecting… · hide logs
      </button>
    );
  return (
    <div className="conn-overlay">
      <div className="conn-card">
        <div className="conn-head">
          <HostTile server={server} size="lg" />
          <div className="conn-title">
            <strong>{server.name}</strong>
            <span>
              {proto} {server.host}
              {server.port ? `:${server.port}` : ''}
            </span>
          </div>
          {server.protocol === 'ssh' && (
            <button className="conn-logs" onClick={() => setLogs(true)}>
              Show logs
            </button>
          )}
        </div>
        <div className="conn-track">
          <span className="conn-spin">
            <IconRefresh size={14} />
          </span>
          <span className="conn-line" />
          <span className="conn-end">{server.protocol === 'ssh' ? <IconTerminal size={14} /> : <IconMonitor size={14} />}</span>
        </div>
        {close && (
          <button className="conn-close" onClick={close}>
            <IconClose size={13} /> Close
          </button>
        )}
      </div>
    </div>
  );
}

const STATUS_TEXT: Record<SessionStatus, string> = {
  connecting: 'Connecting…',
  connected: 'Connected',
  closed: 'Disconnected',
  error: 'Error',
};

/**
 * Frame around every session. Termius style: preferably only the content.
 * - "float": small, unobtrusive toolbar at the top right (SSH, VNC)
 * - "bar":   slim bar above (RDP, because the native window would sit above HTML)
 */
export function SessionFrame({
  server,
  status,
  variant = 'float',
  extraActions,
  onReconnect,
  onDuplicate,
  onEdit,
  children,
}: {
  server: Server;
  status: SessionStatus;
  variant?: 'float' | 'bar';
  extraActions?: ReactNode;
  onReconnect: () => void;
  onDuplicate: () => void;
  onEdit: () => void;
  children: ReactNode;
}) {
  const buttons = (
    <>
      {extraActions}
      <button title="Reconnect" onClick={onReconnect}>
        <IconRefresh size={15} />
      </button>
      <button title="Second session to the same host (Ctrl+Shift+D)" onClick={onDuplicate}>
        <IconDuplicate size={15} />
      </button>
      <button title="Host details" onClick={onEdit}>
        <IconEdit size={15} />
      </button>
    </>
  );

  return (
    <div className="session">
      {variant === 'bar' && (
        <div className="session-bar">
          <span className="session-title">{server.name}</span>
          <span className="muted">
            {server.username ? `${server.username}@` : ''}
            {server.host}:{server.port}
          </span>
          <span className={`pill ${status}`}>
            <span className="dot-sm" />
            {STATUS_TEXT[status]}
          </span>
          <span className="grow" />
          {buttons}
        </div>
      )}
      <div className="session-body">
        {variant === 'float' && <div className="float-tools">{buttons}</div>}
        {children}
        {status === 'connecting' && <ConnectingOverlay server={server} />}
      </div>
    </div>
  );
}
