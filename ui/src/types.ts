export type Protocol = 'ssh' | 'rdp' | 'vnc';
export type AuthMethod = 'password' | 'key';

export interface Server {
  id: string;
  name: string;
  host: string;
  port: number;
  protocol: Protocol;
  username: string;
  password: string;
  auth: AuthMethod;
  keyId: string;
  group: string;
  tags: string[];
  favorite: boolean;
  lastUsed: string | null;
  notes: string;
}

/** Private Keys bleiben im Host (C#). Das UI sieht nur diese Infos. */
export interface KeyInfo {
  id: string;
  name: string;
  kind: string;
  hasPassphrase: boolean;
  updatedAt: string;
}

export interface SyncInfo {
  configured: boolean;
  url: string;
  anonKey: string;
  email: string;
  hasPassword: boolean;
  lastSync: string | null;
}

export type SyncState = 'off' | 'syncing' | 'ok' | 'error';
export interface SyncStatus {
  state: SyncState;
  message: string;
  at?: string;
}

/** Lebenszyklus einer Session (für den Punkt am Tab). */
export type SessionStatus = 'connecting' | 'connected' | 'closed' | 'error';

export const DEFAULT_PORT: Record<Protocol, number> = { ssh: 22, rdp: 3389, vnc: 5900 };

export const PROTOCOL_LABEL: Record<Protocol, string> = { ssh: 'SSH', rdp: 'RDP', vnc: 'VNC' };

export function emptyServer(): Server {
  return {
    id: '',
    name: '',
    host: '',
    port: 22,
    protocol: 'ssh',
    username: '',
    password: '',
    auth: 'password',
    keyId: '',
    group: '',
    tags: [],
    favorite: false,
    lastUsed: null,
    notes: '',
  };
}
