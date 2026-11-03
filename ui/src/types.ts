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
  identityId: string;
  os: string;
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

/** Wiederverwendbare Zugangsdaten. Das Passwort selbst bleibt im Host. */
export interface Identity {
  id: string;
  name: string;
  username: string;
  hasPassword: boolean;
  auth: AuthMethod;
  keyId: string;
}

export interface Snippet {
  id: string;
  name: string;
  command: string;
  autoRun: boolean;
}

export type ForwardType = 'local' | 'remote' | 'dynamic';
export interface ForwardRule {
  id: string;
  name: string;
  serverId: string;
  type: ForwardType;
  bindHost: string;
  bindPort: number;
  destHost: string;
  destPort: number;
}
export interface ForwardItem {
  rule: ForwardRule;
  running: boolean;
}

export interface KnownHost {
  id: string;
  fingerprint: string;
}

export interface HistoryEntry {
  id: string;
  serverId: string;
  serverName: string;
  host: string;
  kind: 'ssh' | 'sftp' | 'rdp' | 'vnc' | 'forward';
  startedAt: string;
  endedAt: string | null;
  result: 'running' | 'ok' | 'error';
  message: string;
}

export interface FileEntry {
  name: string;
  path: string;
  isDir: boolean;
  size: number;
  modified: string;
  mode: string;
}
export interface BrowseResult {
  path: string;
  parent: string;
  entries: FileEntry[];
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
    identityId: '',
    os: '',
    group: '',
    tags: [],
    favorite: false,
    lastUsed: null,
    notes: '',
  };
}
