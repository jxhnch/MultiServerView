import type { Credentials } from './PasswordPrompt';
import {
  DEFAULT_PORT,
  type BrowseResult,
  type FileEntry,
  type HostSettings,
  type ForwardItem,
  type ForwardRule,
  type HistoryEntry,
  type Identity,
  type KeyInfo,
  type KnownHost,
  type Server,
  type Snippet,
  type SyncInfo,
  type ProxmoxCert,
  type ProxmoxInfo,
  type ProxmoxVmInfo,
  type VmwareVmInfo,
} from './types';

/**
 * Connection to the C# host (WebView2). In a normal browser (npm run dev) there is no host -
 * so a mock version with localStorage and a demo shell runs instead, which lets you develop the UI without Windows.
 * WARNING: the mock stores data unencrypted and is meant for development only.
 */

// ---------------------------------------------------------------- Events from the host

export type HostEvent =
  | { event: 'ssh.data'; sessionId: string; data: string }
  | { event: 'ssh.closed'; sessionId: string; reason: string | null }
  | { event: 'ssh.info'; sessionId: string; message: string }
  | { event: 'rdp.status'; sessionId: string; state: 'connecting' | 'connected' | 'disconnected'; message: string }
  | { event: 'sync.status'; state: 'off' | 'syncing' | 'ok' | 'error'; message: string; at?: string }
  | { event: 'forward.status'; ruleId: string; running: boolean; error: string | null }
  | { event: 'sftp.progress'; sessionId: string; transferId: string; name: string; done: number; total: number; finished: boolean }
  | { event: 'window.state'; maximized: boolean }
  | { event: 'vault.changed' };

const listeners = new Set<(e: HostEvent) => void>();

/** Listens to events from the host. Returns the unsubscribe function. */
export function onEvent(fn: (e: HostEvent) => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}
const dispatch = (e: HostEvent) => listeners.forEach((l) => l(e));

// ---------------------------------------------------------------- Base64 helpers (bytes <-> text)

export function toB64(text: string): string {
  const bytes = new TextEncoder().encode(text);
  let bin = '';
  bytes.forEach((b) => (bin += String.fromCharCode(b)));
  return btoa(bin);
}

/** For xterm onBinary: every character is one byte (0-255). */
export function binaryToB64(text: string): string {
  let bin = '';
  for (let i = 0; i < text.length; i++) bin += String.fromCharCode(text.charCodeAt(i) & 0xff);
  return btoa(bin);
}

export function fromB64(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

// ---------------------------------------------------------------- WebView2

interface WebView2Api {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (e: MessageEvent) => void): void;
}
const webview = (window as unknown as { chrome?: { webview?: WebView2Api } }).chrome?.webview;

export const isNative = !!webview;

type Reply = { id: number; result?: unknown; error?: string };
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
let seq = 0;

if (webview) {
  webview.addEventListener('message', (e) => {
    const msg = e.data as Reply & Partial<HostEvent>;
    if (msg.event) {
      dispatch(msg as HostEvent);
      return;
    }
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.error) p.reject(new Error(msg.error));
    else p.resolve(msg.result);
  });
}

function callNative<T>(method: string, params?: unknown): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const id = ++seq;
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
    webview!.postMessage({ id, method, params });
  });
}

// ---------------------------------------------------------------- Mock (browser dev only)

interface MockState {
  password: string | null;
  unlocked: boolean;
  servers: Server[];
  keys: KeyInfo[];
  identities: Identity[];
  snippets: Snippet[];
  forwards: ForwardRule[];
  history: HistoryEntry[];
  settings: HostSettings;
  sync: SyncInfo;
}
const MOCK_KEY = 'msv-mock-v3';

function loadMock(): MockState {
  try {
    const raw = localStorage.getItem(MOCK_KEY);
    if (raw) {
      const old = JSON.parse(raw) as MockState;
      // Older demo data does not know "workspace" yet.
      return { ...old, servers: old.servers.map((s) => ({ ...s, workspace: s.workspace ?? '', proxmoxRef: s.proxmoxRef ?? '', vmwareRef: s.vmwareRef ?? '' })), unlocked: false };
    }
  } catch {
    /* ignore */
  }
  return {
    password: null,
    unlocked: false,
    servers: [],
    keys: [],
    identities: [],
    snippets: [],
    forwards: [],
    history: [],
    settings: { sshKeepAliveSeconds: 30, sshConnectTimeoutSeconds: 15, rdpColorDepth: 32, rdpClipboard: true, rdpDrives: false, rdpDynamicResize: true, rdpWarnOnCertificate: false },
    sync: { configured: false, mode: 'folder', folder: '', url: '', anonKey: '', email: '', hasPassword: false, lastSync: null },
  };
}
const mock = loadMock();
const persistMock = () => {
  try {
    localStorage.setItem(MOCK_KEY, JSON.stringify(mock));
  } catch {
    /* ignore */
  }
};

function demoServers(): Server[] {
  const mk = (s: Partial<Server> & Pick<Server, 'name' | 'host' | 'protocol'>): Server => ({
    id: crypto.randomUUID(),
    port: DEFAULT_PORT[s.protocol],
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
    ...s,
  });
  const ago = (min: number) => new Date(Date.now() - min * 60_000).toISOString();
  return [
    mk({ name: 'web-prod-01', host: '10.0.10.11', protocol: 'ssh', username: 'deploy', group: 'Production', tags: ['nginx'], favorite: true, lastUsed: ago(5), os: 'debian' }),
    mk({ name: 'db-prod-01', host: '10.0.10.21', protocol: 'ssh', username: 'postgres', group: 'Production', tags: ['postgres'], favorite: true, lastUsed: ago(90), os: 'ubuntu' }),
    mk({ name: 'win-ad-01', host: '10.0.20.5', protocol: 'rdp', username: 'CORP\\Administrator', group: 'Production', tags: ['windows', 'ad'], lastUsed: ago(300) }),
    mk({ name: 'staging-web', host: 'staging.example.ch', protocol: 'ssh', username: 'ubuntu', group: 'Staging', tags: ['nginx'], os: 'alpine' }),
    mk({ name: 'staging-desktop', host: '10.0.30.8', protocol: 'vnc', group: 'Staging', tags: ['linux', 'xfce'] }),
    mk({ name: 'homelab-proxmox', host: '192.168.1.10', protocol: 'ssh', username: 'root', group: 'Homelab', favorite: true, lastUsed: ago(2000), os: 'proxmox' }),
    mk({ name: 'homelab-nas', host: '192.168.1.20', protocol: 'ssh', username: 'admin', group: 'Homelab', os: 'linux' }),
    mk({ name: 'homelab-win11', host: '192.168.1.40', protocol: 'rdp', username: 'john', group: 'Homelab', tags: ['windows'] }),
  ];
}

// Demo shell so a "terminal" is visible in the browser
const mockShells = new Map<string, { line: string; host: string }>();
const PROMPT = '\x1b[1;32mdemo@msv\x1b[0m:\x1b[1;34m~\x1b[0m$ ';

function mockShellWrite(sessionId: string, text: string) {
  dispatch({ event: 'ssh.data', sessionId, data: toB64(text) });
}

function mockShellInput(sessionId: string, input: string) {
  const sh = mockShells.get(sessionId);
  if (!sh) return;
  for (const ch of input) {
    if (ch === '\r') {
      const cmd = sh.line.trim();
      sh.line = '';
      mockShellWrite(sessionId, '\r\n' + mockCommand(cmd, sessionId, sh.host) + (cmd === 'exit' ? '' : PROMPT));
    } else if (ch === '\x7f') {
      if (sh.line.length > 0) {
        sh.line = sh.line.slice(0, -1);
        mockShellWrite(sessionId, '\b \b');
      }
    } else if (ch >= ' ') {
      sh.line += ch;
      mockShellWrite(sessionId, ch);
    }
  }
}

function mockCommand(cmd: string, sessionId: string, host: string): string {
  switch (cmd) {
    case '':
      return '';
    case 'ls':
      return '\x1b[1;34mprojects\x1b[0m  \x1b[1;34mbackups\x1b[0m  notes.txt  \x1b[1;32mdeploy.sh\x1b[0m\r\n';
    case 'whoami':
      return 'demo\r\n';
    case 'hostname':
      return host + '\r\n';
    case 'colors':
      return Array.from({ length: 16 }, (_, i) => `\x1b[48;5;${i}m  \x1b[0m`).join('') + '\r\n';
    case 'clear':
      return '\x1b[2J\x1b[H';
    case 'exit':
      setTimeout(() => {
        mockShells.delete(sessionId);
        dispatch({ event: 'ssh.closed', sessionId, reason: null });
      }, 50);
      return 'logout\r\n';
    default:
      return `bash: ${cmd}: command not found (demo shell: ls, whoami, hostname, colors, clear, exit)\r\n`;
  }
}


// ---- Demo file systems for SFTP (browser mock only)
type MockFs = Map<string, FileEntry>;
const mkFs = (sep: string, items: [string, number | null][]): MockFs => {
  const m: MockFs = new Map();
  for (const [path, size] of items) {
    const name = path.split(sep).pop()!;
    m.set(path, { name, path, isDir: size === null, size: size ?? 0, modified: new Date(Date.now() - Math.random() * 9e9).toISOString(), mode: size === null ? 'drwxr-xr-x' : '-rw-r--r--' });
  }
  return m;
};
const remoteFs = mkFs('/', [
  ['/home', null], ['/home/demo', null], ['/home/demo/projects', null], ['/home/demo/backups', null],
  ['/home/demo/projects/app.py', 4210], ['/home/demo/projects/README.md', 1288], ['/home/demo/notes.txt', 612],
  ['/home/demo/deploy.sh', 934], ['/home/demo/backups/db-2026-11-01.sql.gz', 52_428_800], ['/home/demo/.bashrc', 3771],
]);
const localFs = mkFs('\\', [
  ['C:\\Users', null], ['C:\\Users\\demo', null], ['C:\\Users\\demo\\Documents', null], ['C:\\Users\\demo\\Downloads', null],
  ['C:\\Users\\demo\\Documents\\quote.pdf', 184_320], ['C:\\Users\\demo\\Documents\\config.yaml', 2048],
  ['C:\\Users\\demo\\Downloads\\release.zip', 12_582_912],
]);
const parentOf = (path: string, sep: string) => {
  const i = path.lastIndexOf(sep);
  return i <= 0 ? (sep === '/' ? '/' : '') : path.slice(0, i);
};
function fsList(fs: MockFs, dir: string, sep: string): BrowseResult {
  const norm = dir.length > 1 && dir.endsWith(sep) && !(sep === '\\' && /^[A-Z]:\\$/.test(dir)) ? dir.slice(0, -1) : dir;
  if (norm !== '/' && !fs.get(norm)?.isDir) throw new Error('Path not found.');
  const entries = [...fs.values()].filter((e) => parentOf(e.path, sep) === norm && e.path !== norm);
  entries.sort((a, b) => Number(b.isDir) - Number(a.isDir) || a.name.localeCompare(b.name));
  return { path: norm, parent: norm === '/' ? '' : parentOf(norm, sep), entries };
}
function fsAdd(fs: MockFs, path: string, isDir: boolean, size: number, sep: string) {
  fs.set(path, { name: path.split(sep).pop()!, path, isDir, size, modified: new Date().toISOString(), mode: isDir ? 'drwxr-xr-x' : '-rw-r--r--' });
}
function fsDelete(fs: MockFs, path: string, sep: string) {
  for (const k of [...fs.keys()]) if (k === path || k.startsWith(path + sep)) fs.delete(k);
}
function fsRename(fs: MockFs, from: string, to: string, sep: string) {
  for (const [k, v] of [...fs.entries()]) {
    if (k === from || k.startsWith(from + sep)) {
      fs.delete(k);
      const np = to + k.slice(from.length);
      fs.set(np, { ...v, path: np, name: np.split(sep).pop()! });
    }
  }
}
function fsCopy(src: MockFs, dst: MockFs, path: string, targetDir: string, srcSep: string, dstSep: string) {
  const root = src.get(path)!;
  const base = targetDir.endsWith(dstSep) ? targetDir : targetDir + dstSep;
  for (const [k, v] of src.entries()) {
    if (k === path || k.startsWith(path + srcSep)) {
      const rel = k.slice(path.length).split(srcSep).join(dstSep);
      const np = base + root.name + rel;
      dst.set(np, { ...v, path: np, name: np.split(dstSep).pop()! });
    }
  }
}
const mockFileText = new Map<string, string>();
const mockRunning = new Set<string>();
let mockKnownHosts: KnownHost[] = [
  { id: 'web-prod-01.example.ch:22', fingerprint: 'SHA256:Gz8m1q0kXb1Tn5oV7mP2sA9fHc3dRyUeWl4JvNkQ0oE' },
  { id: '10.0.10.21:22', fingerprint: 'SHA256:aB3dEfGh1JkLmNoPqRsTuVwXyZ0123456789AbCdEfG' },
];

async function callMock(method: string, params: any): Promise<unknown> {
  await new Promise((r) => setTimeout(r, 30));
  const need = () => {
    if (!mock.unlocked) throw new Error('The vault is locked.');
  };
  const onlyNative = (what: string) => {
    throw new Error(`${what} is only available in the Windows app (in the browser you only see the interface with demo data).`);
  };

  switch (method) {
    case 'vault.status':
      return { exists: mock.password !== null, unlocked: mock.unlocked };
    case 'vault.create':
      if (!params.password || params.password.length < 8) throw new Error('The master password must be at least 8 characters.');
      mock.password = params.password;
      mock.unlocked = true;
      mock.servers = demoServers();
      persistMock();
      return null;
    case 'vault.unlock':
      if (params.password !== mock.password) throw new Error('Wrong password.');
      mock.unlocked = true;
      return null;
    case 'vault.lock':
      mock.unlocked = false;
      return null;

    case 'servers.list':
      need();
      return mock.servers;
    case 'servers.save': {
      need();
      const s = { ...(params as Server) };
      if (!s.id) s.id = crypto.randomUUID();
      if (!s.port) s.port = DEFAULT_PORT[s.protocol];
      const i = mock.servers.findIndex((x) => x.id === s.id);
      if (i >= 0) mock.servers[i] = s;
      else mock.servers.push(s);
      persistMock();
      return s;
    }
    case 'servers.delete':
      need();
      mock.servers = mock.servers.filter((x) => x.id !== params.id);
      persistMock();
      return null;
    case 'servers.touch': {
      need();
      const s = mock.servers.find((x) => x.id === params.id);
      if (!s) throw new Error('Server not found.');
      s.lastUsed = new Date().toISOString();
      persistMock();
      return s;
    }
    case 'hosts.forget':
      return null;
    case 'import.mremoteng':
      return onlyNative('The mRemoteNG import');

    case 'keys.list':
      need();
      return mock.keys;
    case 'keys.save': {
      need();
      if (!params.name?.trim()) throw new Error('Please give the key a name.');
      const existing = mock.keys.find((k) => k.id === params.id);
      if (!existing && !String(params.privateKey ?? '').includes('PRIVATE KEY'))
        throw new Error('This does not look like a private SSH key (the line "BEGIN … PRIVATE KEY" is missing).');
      const info: KeyInfo = {
        id: existing?.id ?? crypto.randomUUID(),
        name: params.name,
        kind: existing?.kind ?? (String(params.privateKey).includes('OPENSSH') ? 'ED25519' : 'RSA'),
        hasPassphrase: existing?.hasPassphrase ?? !!params.passphrase,
        publicKey: existing?.publicKey ?? '',
        fingerprint: existing?.fingerprint ?? '',
        updatedAt: new Date().toISOString(),
      };
      mock.keys = existing ? mock.keys.map((k) => (k.id === info.id ? info : k)) : [...mock.keys, info];
      persistMock();
      return info;
    }
    case 'keys.generate': {
      need();
      if (!params.name?.trim()) throw new Error('Please give the key a name.');
      const blob = btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(51)))).replace(/=+$/, '');
      const info: KeyInfo = {
        id: crypto.randomUUID(),
        name: params.name.trim(),
        kind: params.type === 'rsa' ? 'RSA' : 'ED25519',
        hasPassphrase: false,
        publicKey: `${params.type === 'rsa' ? 'ssh-rsa' : 'ssh-ed25519'} ${blob} ${params.comment || ''}`.trim(),
        fingerprint: 'SHA256:' + blob.slice(0, 43),
        updatedAt: new Date().toISOString(),
      };
      mock.keys = [...mock.keys, info];
      persistMock();
      return info;
    }
    case 'keys.install':
      need();
      return null;
    case 'keys.delete':
      need();
      mock.keys = mock.keys.filter((k) => k.id !== params.id);
      mock.servers = mock.servers.map((s) => (s.keyId === params.id ? { ...s, keyId: '', auth: 'password' } : s));
      persistMock();
      return null;

    case 'ssh.open': {
      need();
      const server = mock.servers.find((s) => s.id === params.serverId);
      if (!server) throw new Error('Server not found.');
      mockShells.set(params.sessionId, { line: '', host: server.name });
      mock.history.push({ id: crypto.randomUUID(), serverId: server.id, serverName: server.name, host: `${server.host}:${server.port}`, kind: 'ssh', startedAt: new Date().toISOString(), endedAt: null, result: 'running', message: '' });
      persistMock();
      setTimeout(() => {
        mockShellWrite(
          params.sessionId,
          `\x1b[2mDemo shell (browser mock) – connected to ${server.username || 'user'}@${server.host}\x1b[0m\r\n` +
            'Try: ls, whoami, colors, clear, exit\r\n\r\n' +
            PROMPT,
        );
      }, 150);
      await new Promise((r) => setTimeout(r, 1200)); // demo: show the connecting animation
      return null;
    }
    case 'ssh.input':
      mockShellInput(params.sessionId, new TextDecoder().decode(fromB64(params.data)));
      return null;
    case 'ssh.resize':
      return null;
    case 'ssh.close':
      mockShells.delete(params.sessionId);
      return null;

    case 'vnc.open': {
      // For development/tests only: localStorage.setItem('msv-dev-vnc-url', 'ws://127.0.0.1:PORT/TOKEN')
      const devUrl = localStorage.getItem('msv-dev-vnc-url');
      if (devUrl) return { url: devUrl, username: '', password: '' };
      return onlyNative('VNC');
    }
    case 'rdp.open':
      return onlyNative('RDP');
    case 'rdp.layout':
    case 'rdp.close':
    case 'rdp.suspend':
      return null;
    case 'teamviewer.open':
      return onlyNative('TeamViewer');
    case 'rdp.external':
      return onlyNative('RDP');

    case 'dialog.pickFolder':
      return { path: null };
    case 'vmware.scan':
      return {
        vmrun: true,
        vms: MOCK_VMWARE.map((v) => ({ ...v, imported: mock.servers.some((s) => s.vmwareRef === v.vmx.toLowerCase()) })),
      };
    case 'vmware.import': {
      const wanted = MOCK_VMWARE.filter((v) => params.vms.includes(v.vmx));
      for (const v of wanted)
        mock.servers.push({
          ...demoServers()[0],
          id: crypto.randomUUID(),
          name: v.name,
          host: v.ip ?? v.name,
          protocol: v.protocol,
          workspace: params.workspace,
          vmwareRef: v.vmx.toLowerCase(),
          favorite: false,
        });
      persistMock();
      return { imported: wanted.length, noIp: wanted.filter((v) => !v.ip).length, skipped: 0 };
    }
    case 'vmware.removeImported': {
      const before = mock.servers.length;
      mock.servers = mock.servers.filter((s) => !s.vmwareRef);
      persistMock();
      return { removed: before - mock.servers.length };
    }
    case 'proxmox.list':
      return mockProxmox;
    case 'proxmox.probe':
      return { fingerprint: 'AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89', subject: 'CN=pve', notAfter: new Date().toISOString(), trusted: false };
    case 'proxmox.test':
      return { version: '9.2.3', vms: MOCK_PVE_VMS.length };
    case 'proxmox.save': {
      const info: ProxmoxInfo = {
        id: params.id || crypto.randomUUID(),
        name: params.name || 'pve',
        url: params.url,
        tokenId: params.tokenId,
        hasSecret: true,
        certFingerprint: params.certFingerprint ?? '',
        updatedAt: new Date().toISOString(),
      };
      mockProxmox = [...mockProxmox.filter((p) => p.id !== info.id), info];
      return info;
    }
    case 'proxmox.removeImported': {
      const before = mock.servers.length;
      mock.servers = mock.servers.filter((s) => !s.proxmoxRef.startsWith(params.id + ':'));
      persistMock();
      return { removed: before - mock.servers.length };
    }
    case 'proxmox.delete':
      mockProxmox = mockProxmox.filter((p) => p.id !== params.id);
      return null;
    case 'proxmox.vms':
      return MOCK_PVE_VMS.map((v) => ({ ...v, imported: mock.servers.some((s) => s.proxmoxRef === `${params.id}:${v.node}:${v.vmid}`) }));
    case 'proxmox.import': {
      const wanted = MOCK_PVE_VMS.filter((v) => params.vms.some((x: { node: string; vmid: number }) => x.node === v.node && x.vmid === v.vmid));
      for (const v of wanted)
        mock.servers.push({
          ...demoServers()[0],
          id: crypto.randomUUID(),
          name: v.name,
          host: v.name,
          group: params.groupBy === 'node' ? v.node : params.groupBy === 'pool' ? v.pool : '',
          workspace: params.workspaceFromPool && v.pool ? v.pool : params.workspace,
          proxmoxRef: `${params.id}:${v.node}:${v.vmid}`,
          favorite: false,
        });
      persistMock();
      return { imported: wanted.length, noIp: wanted.length, skipped: 0 };
    }
    case 'sync.get':
      return mock.sync;
    case 'sync.save': {
      mock.sync = {
        configured:
          params.mode === 'folder'
            ? !!params.folder
            : !!(params.url && params.anonKey && params.email && (params.password || mock.sync.hasPassword)),
        mode: params.mode,
        folder: params.folder ?? '',
        url: params.url,
        anonKey: params.anonKey,
        email: params.email,
        hasPassword: !!params.password || mock.sync.hasPassword,
        lastSync: mock.sync.lastSync,
      };
      persistMock();
      return mock.sync;
    }
    case 'window.command':
      return { maximized: false };
    case 'sync.pickFolder':
    case 'sync.test':
    case 'sync.signUp':
    case 'sync.now':
    case 'sync.downloadInitial':
      return onlyNative('Sync');
    case 'sync.disconnect':
      mock.sync = { configured: false, mode: 'folder', folder: '', url: '', anonKey: '', email: '', hasPassword: false, lastSync: null };
      persistMock();
      return null;



    // ---- Settings, password, backup
    case 'settings.get':
      return mock.settings;
    case 'settings.save':
      mock.settings = { ...mock.settings, ...params };
      persistMock();
      return mock.settings;
    case 'vault.changePassword':
      need();
      if (params.oldPassword !== mock.password) throw new Error('Wrong password.');
      if (!params.newPassword || params.newPassword.length < 8) throw new Error('The new master password must be at least 8 characters.');
      mock.password = params.newPassword;
      persistMock();
      return { cloud: 'none' };
    case 'vault.summary':
      need();
      return { servers: mock.servers.length, keys: mock.keys.length, identities: mock.identities.length, snippets: mock.snippets.length, forwards: mock.forwards.length, proxmox: mockProxmox.length, knownHosts: mockKnownHosts.length };
    case 'vault.clear':
      need();
      if (params.password !== mock.password) throw new Error('Wrong password.');
      mock.servers = [];
      mock.keys = [];
      mock.identities = [];
      mock.snippets = [];
      mock.forwards = [];
      mockProxmox = [];
      mockKnownHosts = [];
      persistMock();
      return null;
    case 'vault.export':
      return { saved: true, path: 'C:\\Users\\demo\\Documents\\MultiServerView-Backup.msv' };
    case 'vault.info':
      return { path: 'C:\\Users\\demo\\AppData\\Roaming\\MultiServerView\\vault.msv', version: '0.1.0' };
    case 'vault.reveal':
      return null;
    case 'sync.adoptPassword':
    case 'sync.forceUpload':
    case 'import.sshconfig':
    case 'import.putty':
    case 'import.csv':
      return onlyNative('This feature');
    case 'vnc.close':
      return null;

    // ---- Identities
    case 'identities.list':
      need();
      return mock.identities;
    case 'identities.save': {
      need();
      if (!params.name?.trim()) throw new Error('Please give the identity a name.');
      const existing = mock.identities.find((i) => i.id === params.id);
      const info: Identity = {
        id: existing?.id ?? crypto.randomUUID(),
        name: params.name,
        username: params.username ?? '',
        hasPassword: params.password ? true : (existing?.hasPassword ?? false),
        auth: params.auth ?? 'password',
        keyId: params.keyId ?? '',
      };
      mock.identities = existing ? mock.identities.map((i) => (i.id === info.id ? info : i)) : [...mock.identities, info];
      persistMock();
      return info;
    }
    case 'identities.delete':
      need();
      mock.identities = mock.identities.filter((i) => i.id !== params.id);
      mock.servers = mock.servers.map((s) => (s.identityId === params.id ? { ...s, identityId: '' } : s));
      persistMock();
      return null;

    // ---- Snippets
    case 'snippets.list':
      need();
      return mock.snippets;
    case 'snippets.save': {
      need();
      if (!params.name?.trim() || !params.command?.trim()) throw new Error('Name and command are required.');
      const sn: Snippet = { id: params.id || crypto.randomUUID(), name: params.name, command: params.command, autoRun: !!params.autoRun };
      mock.snippets = mock.snippets.some((x) => x.id === sn.id) ? mock.snippets.map((x) => (x.id === sn.id ? sn : x)) : [...mock.snippets, sn];
      persistMock();
      return sn;
    }
    case 'snippets.delete':
      need();
      mock.snippets = mock.snippets.filter((x) => x.id !== params.id);
      persistMock();
      return null;

    // ---- Port forwarding
    case 'forwards.list':
      need();
      return mock.forwards.map((rule): ForwardItem => ({ rule, running: mockRunning.has(rule.id) }));
    case 'forwards.save': {
      need();
      if (!mock.servers.some((s) => s.id === params.serverId)) throw new Error('Please choose an SSH host.');
      if (!(params.bindPort >= 1 && params.bindPort <= 65535)) throw new Error('The port must be between 1 and 65535.');
      if (params.type !== 'dynamic' && (!params.destHost?.trim() || !(params.destPort >= 1))) throw new Error('Destination address and port are missing.');
      const rule: ForwardRule = { ...params, id: params.id || crypto.randomUUID(), name: params.name || `${params.type} ${params.bindPort}` };
      mockRunning.delete(rule.id);
      mock.forwards = mock.forwards.some((x) => x.id === rule.id) ? mock.forwards.map((x) => (x.id === rule.id ? rule : x)) : [...mock.forwards, rule];
      persistMock();
      return rule;
    }
    case 'forwards.delete':
      need();
      mockRunning.delete(params.id);
      mock.forwards = mock.forwards.filter((x) => x.id !== params.id);
      persistMock();
      return null;
    case 'forwards.start':
      need();
      mockRunning.add(params.id);
      setTimeout(() => dispatch({ event: 'forward.status', ruleId: params.id, running: true, error: null }), 0);
      return null;
    case 'forwards.stop':
      mockRunning.delete(params.id);
      setTimeout(() => dispatch({ event: 'forward.status', ruleId: params.id, running: false, error: null }), 0);
      return null;

    // ---- Known hosts / history
    case 'knownhosts.list':
      need();
      return mockKnownHosts;
    case 'knownhosts.remove':
      mockKnownHosts = mockKnownHosts.filter((k) => k.id !== params.id);
      return null;
    case 'history.list':
      need();
      return mock.history.slice().reverse();
    case 'history.clear':
      mock.history = [];
      persistMock();
      return null;

    // ---- SFTP (demo file system in memory)
    case 'sftp.open':
      need();
      if (!mock.servers.some((s) => s.id === params.serverId)) throw new Error('Server not found.');
      return { home: '/home/demo' };
    case 'sftp.readText': {
      const entry = remoteFs.get(params.path);
      if (!entry || entry.isDir) throw new Error('Path not found.');
      const content = mockFileText.get(params.path) ?? `# ${entry.name}\n# (demo content; nothing is written to a server in browser mode)\n`;
      return { content, lineEnding: 'lf', bom: false, size: content.length, modified: 1 };
    }
    case 'sftp.writeText':
      mockFileText.set(params.path, params.content);
      return { modified: 2, size: params.content.length };
    case 'sftp.list':
      return fsList(remoteFs, params.path || '/home/demo', '/');
    case 'sftp.mkdir':
      fsAdd(remoteFs, params.path, true, 0, '/');
      return null;
    case 'sftp.rename':
      fsRename(remoteFs, params.from, params.to, '/');
      return null;
    case 'sftp.delete':
      fsDelete(remoteFs, params.path, '/');
      return null;
    case 'sftp.upload':
    case 'sftp.download': {
      const upload = method === 'sftp.upload';
      const [src, dst, srcSep, dstSep] = upload ? [localFs, remoteFs, '\\', '/'] : [remoteFs, localFs, '/', '\\'];
      const paths: string[] = upload ? params.localPaths : params.remotePaths;
      const target: string = upload ? params.remoteDir : params.localDir;
      for (const path of paths) {
        const entry = src.get(path);
        if (!entry) throw new Error('File not found: ' + path);
        const total = Math.max(entry.size, 1);
        for (const step of [0.3, 0.7, 1]) {
          await new Promise((r) => setTimeout(r, 120));
          dispatch({ event: 'sftp.progress', sessionId: params.sessionId, transferId: params.transferId, name: entry.name, done: Math.round(total * step), total, finished: step === 1 });
        }
        fsCopy(src, dst, path, target, srcSep, dstSep);
      }
      return null;
    }
    case 'sftp.cancel':
    case 'sftp.close':
      return null;
    case 'local.browse':
      return fsList(localFs, params.path || 'C:\\Users\\demo', '\\');
    case 'local.roots':
      return { path: '', parent: '', entries: [{ name: 'C:\\', path: 'C:\\', isDir: true, size: 0, modified: '', mode: '' }] };
    case 'local.mkdir':
      fsAdd(localFs, params.path, true, 0, '\\');
      return null;

    default:
      throw new Error('Unknown method: ' + method);
  }
}

// ---------------------------------------------------------------- public API

const call = <T>(method: string, params?: unknown): Promise<T> =>
  isNative ? callNative<T>(method, params) : (callMock(method, params) as Promise<T>);

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface ImportOutcome {
  imported: number;
  skipped: number;
  duplicates?: number;
  keysImported?: number;
  keysSkipped?: number;
  passwordsFailed?: number;
}

export interface ProxmoxInput {
  id?: string;
  name: string;
  url: string;
  tokenId: string;
  tokenSecret: string;
  certFingerprint?: string;
}

export interface SyncConfigInput {
  mode: 'folder' | 'supabase';
  folder: string;
  url: string;
  anonKey: string;
  email: string;
  password: string;
}

/** Demo data for browser mode (development only). */
let mockProxmox: ProxmoxInfo[] = [{ id: 'demo-pve', name: 'Demo-Proxmox', url: 'https://pve.example.com:8006', tokenId: 'root@pam!msv', hasSecret: true, certFingerprint: 'AB:CD', updatedAt: new Date().toISOString() }];
const MOCK_PVE_VMS: ProxmoxVmInfo[] = [
  { node: 'pve', vmid: 100, type: 'qemu', name: 'win11-smith', status: 'running', pool: 'Smith', tags: ['school'], template: false, imported: false },
  { node: 'pve', vmid: 101, type: 'qemu', name: 'debian-smith', status: 'running', pool: 'Smith', tags: [], template: false, imported: false },
  { node: 'pve', vmid: 102, type: 'qemu', name: 'ubuntu-jones', status: 'stopped', pool: 'Jones', tags: [], template: false, imported: false },
  { node: 'pve', vmid: 200, type: 'lxc', name: 'web', status: 'running', pool: '', tags: [], template: false, imported: false },
];

const MOCK_VMWARE: Omit<VmwareVmInfo, 'imported'>[] = [
  { vmx: 'C:\\VMs\\Windows 11 x64\\Windows 11 x64.vmx', name: 'Windows 11 x64', guestOs: 'windows11-64', os: 'windows', protocol: 'rdp', connection: 'nat', running: true, ip: '192.168.40.128' },
  { vmx: 'C:\\VMs\\ubuntu\\ubuntu.vmx', name: 'ubuntu', guestOs: 'ubuntu-64', os: 'ubuntu', protocol: 'ssh', connection: 'nat', running: false, ip: null },
];

export const api = {
  vaultStatus: () => call<{ exists: boolean; unlocked: boolean }>('vault.status'),
  vaultCreate: (password: string) => call<void>('vault.create', { password }),
  vaultUnlock: (password: string) => call<void>('vault.unlock', { password }),
  vaultLock: () => call<void>('vault.lock'),

  serversList: () => call<Server[]>('servers.list'),
  serversSave: (s: Server) => call<Server>('servers.save', s),
  serversDelete: (id: string) => call<void>('servers.delete', { id }),
  serversTouch: (id: string) => call<Server>('servers.touch', { id }),
  hostsForget: (id: string) => call<void>('hosts.forget', { id }),
  importMremoteng: (xml: string, workspace?: string, masterPassword?: string) => call<ImportOutcome>('import.mremoteng', { xml, workspace, masterPassword }),

  keysList: () => call<KeyInfo[]>('keys.list'),
  keysSave: (k: { id?: string; name: string; privateKey?: string; passphrase?: string }) => call<KeyInfo>('keys.save', k),
  keysDelete: (id: string) => call<void>('keys.delete', { id }),
  keysGenerate: (k: { name: string; type: 'ed25519' | 'rsa'; bits?: number; comment?: string }) => call<KeyInfo>('keys.generate', k),
  keysInstall: (k: { keyId: string; serverId: string; switchHost?: boolean } & Credentials) => call<void>('keys.install', k),

  sshOpen: (sessionId: string, serverId: string, cols: number, rows: number, cred?: Credentials) =>
    call<void>('ssh.open', { sessionId, serverId, cols, rows, ...cred }),
  sshInput: (sessionId: string, data: string) => call<void>('ssh.input', { sessionId, data }),
  sshResize: (sessionId: string, cols: number, rows: number) => call<void>('ssh.resize', { sessionId, cols, rows }),
  sshClose: (sessionId: string) => call<void>('ssh.close', { sessionId }),

  vncOpen: (serverId: string, sessionId: string) => call<{ url: string; username: string; password: string }>('vnc.open', { serverId, sessionId }),
  vncClose: (sessionId: string) => call<void>('vnc.close', { sessionId }),

  rdpOpen: (sessionId: string, serverId: string, r: Rect) => call<void>('rdp.open', { sessionId, serverId, ...r }),
  rdpLayout: (sessionId: string, r: Rect, visible: boolean) => call<void>('rdp.layout', { sessionId, ...r, visible }),
  rdpClose: (sessionId: string) => call<void>('rdp.close', { sessionId }),
  rdpSuspend: (suspended: boolean) => call<void>('rdp.suspend', { suspended }),
  rdpExternal: (serverId: string) => call<void>('rdp.external', { serverId }),
  teamviewerOpen: (serverId: string) => call<void>('teamviewer.open', { serverId }),

  identitiesList: () => call<Identity[]>('identities.list'),
  identitiesSave: (i: { id?: string; name: string; username: string; password?: string; auth: 'password' | 'key'; keyId: string }) =>
    call<Identity>('identities.save', i),
  identitiesDelete: (id: string) => call<void>('identities.delete', { id }),

  snippetsList: () => call<Snippet[]>('snippets.list'),
  snippetsSave: (s: { id?: string; name: string; command: string; autoRun: boolean }) => call<Snippet>('snippets.save', s),
  snippetsDelete: (id: string) => call<void>('snippets.delete', { id }),

  forwardsList: () => call<ForwardItem[]>('forwards.list'),
  forwardsSave: (r: Omit<ForwardRule, 'id'> & { id?: string }) => call<ForwardRule>('forwards.save', r),
  forwardsDelete: (id: string) => call<void>('forwards.delete', { id }),
  forwardsStart: (id: string) => call<void>('forwards.start', { id }),
  forwardsStop: (id: string) => call<void>('forwards.stop', { id }),

  knownHostsList: () => call<KnownHost[]>('knownhosts.list'),
  knownHostsRemove: (id: string) => call<void>('knownhosts.remove', { id }),
  historyList: () => call<HistoryEntry[]>('history.list'),
  historyClear: () => call<void>('history.clear'),

  sftpOpen: (sessionId: string, serverId: string, cred?: Credentials) => call<{ home: string }>('sftp.open', { sessionId, serverId, ...cred }),
  sftpReadText: (sessionId: string, path: string) =>
    call<{ content: string; lineEnding: 'lf' | 'crlf'; bom: boolean; size: number; modified: number }>('sftp.readText', { sessionId, path }),
  sftpWriteText: (a: {
    sessionId: string;
    path: string;
    content: string;
    lineEnding: 'lf' | 'crlf';
    bom: boolean;
    expectedModified: number;
    expectedSize: number;
    force: boolean;
  }) => call<{ modified: number; size: number }>('sftp.writeText', a),
  sftpList: (sessionId: string, path: string) => call<BrowseResult>('sftp.list', { sessionId, path }),
  sftpMkdir: (sessionId: string, path: string) => call<void>('sftp.mkdir', { sessionId, path }),
  sftpRename: (sessionId: string, from: string, to: string) => call<void>('sftp.rename', { sessionId, from, to }),
  sftpDelete: (sessionId: string, path: string) => call<void>('sftp.delete', { sessionId, path }),
  sftpUpload: (sessionId: string, transferId: string, localPaths: string[], remoteDir: string) =>
    call<void>('sftp.upload', { sessionId, transferId, localPaths, remoteDir }),
  sftpDownload: (sessionId: string, transferId: string, remotePaths: string[], localDir: string) =>
    call<void>('sftp.download', { sessionId, transferId, remotePaths, localDir }),
  sftpCancel: (transferId: string) => call<void>('sftp.cancel', { transferId }),
  sftpClose: (sessionId: string) => call<void>('sftp.close', { sessionId }),
  localBrowse: (path: string) => call<BrowseResult>('local.browse', { path }),
  localRoots: () => call<BrowseResult>('local.roots'),
  localMkdir: (path: string) => call<void>('local.mkdir', { path }),

  settingsGet: () => call<HostSettings>('settings.get'),
  settingsSave: (s: HostSettings) => call<HostSettings>('settings.save', s),
  vaultChangePassword: (oldPassword: string, newPassword: string) =>
    call<{ cloud: 'none' | 'updated' | 'failed'; message?: string }>('vault.changePassword', { oldPassword, newPassword }),
  vaultExport: () => call<{ saved: boolean; path?: string }>('vault.export'),
  vaultSummary: () =>
    call<{ servers: number; keys: number; identities: number; snippets: number; forwards: number; proxmox: number; knownHosts: number }>('vault.summary'),
  vaultClear: (password: string) => call<unknown>('vault.clear', { password }),
  vaultInfo: () => call<{ path: string; version: string }>('vault.info'),
  vaultReveal: () => call<void>('vault.reveal'),
  syncAdoptPassword: (password: string) => call<void>('sync.adoptPassword', { password }),
  syncForceUpload: () => call<void>('sync.forceUpload'),
  importSshConfig: (text: string, workspace?: string) => call<ImportOutcome>('import.sshconfig', { text, workspace }),
  importPutty: (text?: string, workspace?: string) => call<ImportOutcome>('import.putty', { text: text ?? '', workspace }),
  importCsv: (text: string, workspace?: string) => call<ImportOutcome>('import.csv', { text, workspace }),

  pickFolder: (title: string) => call<{ path: string | null }>('dialog.pickFolder', { title }),
  vmwareScan: (folder: string) => call<{ vmrun: boolean; vms: VmwareVmInfo[] }>('vmware.scan', { folder }),
  vmwareImport: (a: { vms: string[]; workspace: string; identityId: string }) => call<{ imported: number; noIp: number; skipped: number }>('vmware.import', a),
  vmwareRemoveImported: () => call<{ removed: number }>('vmware.removeImported'),
  proxmoxList: () => call<ProxmoxInfo[]>('proxmox.list'),
  proxmoxProbe: (url: string) => call<ProxmoxCert>('proxmox.probe', { url }),
  proxmoxTest: (c: ProxmoxInput) => call<{ version: string; vms: number }>('proxmox.test', c),
  proxmoxSave: (c: ProxmoxInput) => call<ProxmoxInfo>('proxmox.save', c),
  proxmoxDelete: (id: string) => call<void>('proxmox.delete', { id }),
  proxmoxRemoveImported: (id: string) => call<{ removed: number }>('proxmox.removeImported', { id }),
  proxmoxVms: (id: string) => call<ProxmoxVmInfo[]>('proxmox.vms', { id }),
  proxmoxImport: (a: { id: string; vms: { node: string; vmid: number }[]; workspace: string; workspaceFromPool: boolean; identityId: string; groupBy: 'none' | 'pool' | 'node' }) =>
    call<{ imported: number; noIp: number; skipped: number }>('proxmox.import', a),
  syncGet: () => call<SyncInfo>('sync.get'),
  syncSave: (c: SyncConfigInput) => call<SyncInfo>('sync.save', c),
  syncTest: (c: SyncConfigInput) => call<void>('sync.test', c),
  syncSignUp: (c: SyncConfigInput) => call<{ signedIn: boolean }>('sync.signUp', c),
  syncNow: () => call<{ outcome: string; at: string }>('sync.now'),
  syncDisconnect: () => call<void>('sync.disconnect'),
  windowCommand: (command: 'state' | 'minimize' | 'maximize' | 'close') => call<{ maximized: boolean }>('window.command', { command }),
  syncPickFolder: () => call<{ path: string | null }>('sync.pickFolder'),
  syncDownloadInitial: (c: SyncConfigInput) => call<void>('sync.downloadInitial', c),
};
