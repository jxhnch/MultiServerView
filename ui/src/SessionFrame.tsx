import type { ReactNode } from 'react';
import { IconDuplicate, IconEdit, IconRefresh } from './Icons';
import type { Server, SessionStatus } from './types';

const STATUS_TEXT: Record<SessionStatus, string> = {
  connecting: 'Verbinde…',
  connected: 'Verbunden',
  closed: 'Getrennt',
  error: 'Fehler',
};

/**
 * Rahmen um jede Session. Termius-Stil: möglichst nur der Inhalt.
 * - "float": kleine, dezente Werkzeugleiste oben rechts (SSH, VNC)
 * - "bar":   schmale Leiste oberhalb (RDP, weil das native Fenster über HTML liegen würde)
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
      <button title="Neu verbinden" onClick={onReconnect}>
        <IconRefresh size={15} />
      </button>
      <button title="Zweite Session zum selben Host (Ctrl+Shift+D)" onClick={onDuplicate}>
        <IconDuplicate size={15} />
      </button>
      <button title="Host-Details" onClick={onEdit}>
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
