export type Protocol = 'ssh' | 'rdp' | 'vnc' | 'teamviewer';
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
  jumpHostId: string;
  os: string;
  /** Top level (e.g. a teacher): the UI shows only one workspace at a time. */
  workspace: string;
  /** Origin if the host was imported from Proxmox (empty = created by hand). */
  proxmoxRef: string;
  /** Origin if the host was imported from VMware Workstation (path of the .vmx, lowercase). */
  vmwareRef: string;
  group: string;
  tags: string[];
  favorite: boolean;
  lastUsed: string | null;
  notes: string;
}

/** Private keys stay in the host (C#). The UI only sees this info. */
export interface KeyInfo {
  id: string;
  name: string;
  kind: string;
  hasPassphrase: boolean;
  publicKey: string;
  fingerprint: string;
  updatedAt: string;
}

/** Reusable credentials. The password itself stays in the host. */
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

/** Settings of the host process (C#). */
export interface HostSettings {
  sshKeepAliveSeconds: number;
  sshConnectTimeoutSeconds: number;
  rdpColorDepth: number;
  rdpClipboard: boolean;
  rdpDrives: boolean;
  rdpDynamicResize: boolean;
  rdpWarnOnCertificate: boolean;
  mcpEnabled: boolean;
  mcpPort: number;
  mcpToken: string;
}

export interface SyncInfo {
  configured: boolean;
  mode: 'folder' | 'webdav' | 'supabase';
  folder: string;
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

/** Lifecycle of a session (for the dot on the tab). */
export type SessionStatus = 'connecting' | 'connected' | 'closed' | 'error';

export const DEFAULT_PORT: Record<Protocol, number> = { ssh: 22, rdp: 3389, vnc: 5900, teamviewer: 0 };

export const PROTOCOL_LABEL: Record<Protocol, string> = { ssh: 'SSH', rdp: 'RDP', vnc: 'VNC', teamviewer: 'TeamViewer' };

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
    jumpHostId: '',
    os: '',
    workspace: '',
    proxmoxRef: '',
    vmwareRef: '',
    group: '',
    tags: [],
    favorite: false,
    lastUsed: null,
    notes: '',
  };
}

/** A saved Proxmox connection. The token secret stays in the host (C#). */
export interface ProxmoxInfo {
  id: string;
  name: string;
  url: string;
  tokenId: string;
  hasSecret: boolean;
  /** Pinned certificate fingerprint ('' = the certificate is trusted system-wide). */
  certFingerprint: string;
  updatedAt: string;
}

export interface ProxmoxCert {
  fingerprint: string;
  subject: string;
  notAfter: string;
  trusted: boolean;
}

export interface ProxmoxVmInfo {
  node: string;
  vmid: number;
  type: 'qemu' | 'lxc';
  name: string;
  status: string;
  pool: string;
  tags: string[];
  template: boolean;
  imported: boolean;
}

/** A VM from the VMware Workstation library. */
export interface VmwareVmInfo {
  vmx: string;
  name: string;
  guestOs: string;
  os: string;
  protocol: Protocol;
  connection: string;
  running: boolean;
  ip: string | null;
  imported: boolean;
}
