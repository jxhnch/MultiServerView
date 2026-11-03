import type { ReactNode } from 'react';
import { IconDuplicate, IconEdit, IconRefresh } from './Icons';
import type { Server, SessionStatus } from './types';

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
      </div>
    </div>
  );
}
