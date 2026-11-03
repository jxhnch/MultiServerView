import type { ReactNode } from 'react';
import { IconDuplicate, IconEdit, IconRefresh, ProtocolIcon } from './Icons';
import type { Server, SessionStatus } from './types';

const STATUS_TEXT: Record<SessionStatus, string> = {
  connecting: 'Verbinde…',
  connected: 'Verbunden',
  closed: 'Getrennt',
  error: 'Fehler',
};

/** Rahmen um jede Session: Kopfleiste mit Status und Aktionen, darunter der eigentliche Inhalt. */
export function SessionFrame({
  server,
  status,
  extraActions,
  onReconnect,
  onDuplicate,
  onEdit,
  children,
}: {
  server: Server;
  status: SessionStatus;
  extraActions?: ReactNode;
  onReconnect: () => void;
  onDuplicate: () => void;
  onEdit: () => void;
  children: ReactNode;
}) {
  return (
    <div className="session">
      <div className="session-bar">
        <span className={`proto-ico ${server.protocol}`}>
          <ProtocolIcon protocol={server.protocol} size={16} />
        </span>
        <span className="session-title">{server.name}</span>
        <span className="muted session-sub">
          {server.username ? `${server.username}@` : ''}
          {server.host}:{server.port}
        </span>
        <span className={`pill ${status}`}>
          <span className="dot-sm" />
          {STATUS_TEXT[status]}
        </span>
        <span className="grow" />
        {extraActions}
        <button className="ghost" title="Neu verbinden" onClick={onReconnect}>
          <IconRefresh size={15} /> Neu verbinden
        </button>
        <button className="ghost icon-only" title="Zweite Session zum selben Host (Ctrl+Shift+D)" onClick={onDuplicate}>
          <IconDuplicate size={15} />
        </button>
        <button className="ghost icon-only" title="Host bearbeiten" onClick={onEdit}>
          <IconEdit size={15} />
        </button>
      </div>
      <div className="session-body">{children}</div>
    </div>
  );
}
