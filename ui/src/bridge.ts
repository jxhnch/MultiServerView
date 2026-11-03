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
} from './types';

/**
 * Verbindung zum C#-Host (WebView2). Im normalen Browser (npm run dev) gibt es den Host nicht -
 * dann läuft eine Mock-Version mit localStorage und einer Demo-Shell, damit man das UI ohne Windows
 * entwickeln kann. ACHTUNG: Der Mock speichert unverschlüsselt und ist nur für die Entwicklung gedacht.
 */

// ---------------------------------------------------------------- Events vom Host

export type HostEvent =
  | { event: 'ssh.data'; sessionId: string; data: string }
  | { event: 'ssh.closed'; sessionId: string; reason: string | null }
  | { event: 'ssh.info'; sessionId: string; message: string }
  | { event: 'rdp.status'; sessionId: string; state: 'connecting' | 'connected' | 'disconnected'; message: string }
  | { event: 'sync.status'; state: 'off' | 'syncing' | 'ok' | 'error'; message: string; at?: string }
  | { event: 'forward.status'; ruleId: string; running: boolean; error: string | null }
  | { event: 'sftp.progress'; sessionId: string; transferId: string; name: string; done: number; total: number; finished: boolean }
  | { event: 'vault.changed' };

const listeners = new Set<(e: HostEvent) => void>();

/** Hört auf Events vom Host. Gibt die Abmelde-Funktion zurück. */
export function onEvent(fn: (e: HostEvent) => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}
const dispatch = (e: HostEvent) => listeners.forEach((l) => l(e));

// ---------------------------------------------------------------- Base64-Helfer (Bytes <-> Text)

export function toB64(text: string): string {
  const bytes = new TextEncoder().encode(text);
  let bin = '';
  bytes.forEach((b) => (bin += String.fromCharCode(b)));
  return btoa(bin);
}

/** Für xterm onBinary: jedes Zeichen ist ein Byte (0-255). */
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

// ---------------------------------------------------------------- Mock (nur Browser-Dev)

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
    if (raw) return { ...(JSON.parse(raw) as MockState), unlocked: false };
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
    sync: { configured: false, url: '', anonKey: '', email: '', hasPassword: false, lastSync: null },
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
    group: '',
    tags: [],
    favorite: false,
    lastUsed: null,
    notes: '',
    ...s,
  });
  const ago = (min: number) => new Date(Date.now() - min * 60_000).toISOString();
  return [
    mk({ name: 'web-prod-01', host: '10.0.10.11', protocol: 'ssh', username: 'deploy', group: 'Produktion', tags: ['nginx'], favorite: true, lastUsed: ago(5), os: 'debian' }),
    mk({ name: 'db-prod-01', host: '10.0.10.21', protocol: 'ssh', username: 'postgres', group: 'Produktion', tags: ['postgres'], favorite: true, lastUsed: ago(90), os: 'ubuntu' }),
    mk({ name: 'win-ad-01', host: '10.0.20.5', protocol: 'rdp', username: 'CORP\\Administrator', group: 'Produktion', tags: ['windows', 'ad'], lastUsed: ago(300) }),
    mk({ name: 'staging-web', host: 'staging.example.ch', protocol: 'ssh', username: 'ubuntu', group: 'Staging', tags: ['nginx'], os: 'alpine' }),
    mk({ name: 'staging-desktop', host: '10.0.30.8', protocol: 'vnc', group: 'Staging', tags: ['linux', 'xfce'] }),
    mk({ name: 'homelab-proxmox', host: '192.168.1.10', protocol: 'ssh', username: 'root', group: 'Homelab', favorite: true, lastUsed: ago(2000), os: 'proxmox' }),
    mk({ name: 'homelab-nas', host: '192.168.1.20', protocol: 'ssh', username: 'admin', group: 'Homelab', os: 'linux' }),
    mk({ name: 'homelab-win11', host: '192.168.1.40', protocol: 'rdp', username: 'john', group: 'Homelab', tags: ['windows'] }),
  ];
}

// Demo-Shell, damit man im Browser ein "Terminal" sieht
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
      return '\x1b[1;34mprojekte\x1b[0m  \x1b[1;34mbackups\x1b[0m  notizen.txt  \x1b[1;32mdeploy.sh\x1b[0m\r\n';
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
      return `bash: ${cmd}: Befehl nicht gefunden (Demo-Shell: ls, whoami, hostname, colors, clear, exit)\r\n`;
  }
}


// ---- Demo-Dateisysteme für SFTP (nur Browser-Mock)
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
  ['/home', null], ['/home/demo', null], ['/home/demo/projekte', null], ['/home/demo/backups', null],
  ['/home/demo/projekte/app.py', 4210], ['/home/demo/projekte/README.md', 1288], ['/home/demo/notizen.txt', 612],
  ['/home/demo/deploy.sh', 934], ['/home/demo/backups/db-2026-11-01.sql.gz', 52_428_800], ['/home/demo/.bashrc', 3771],
]);
const localFs = mkFs('\\', [
  ['C:\\Users', null], ['C:\\Users\\demo', null], ['C:\\Users\\demo\\Documents', null], ['C:\\Users\\demo\\Downloads', null],
  ['C:\\Users\\demo\\Documents\\angebot.pdf', 184_320], ['C:\\Users\\demo\\Documents\\konfig.yaml', 2048],
  ['C:\\Users\\demo\\Downloads\\release.zip', 12_582_912],
]);
const parentOf = (path: string, sep: string) => {
  const i = path.lastIndexOf(sep);
  return i <= 0 ? (sep === '/' ? '/' : '') : path.slice(0, i);
};
function fsList(fs: MockFs, dir: string, sep: string): BrowseResult {
  const norm = dir.length > 1 && dir.endsWith(sep) && !(sep === '\\' && /^[A-Z]:\\$/.test(dir)) ? dir.slice(0, -1) : dir;
  if (norm !== '/' && !fs.get(norm)?.isDir) throw new Error('Pfad nicht gefunden.');
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
const mockRunning = new Set<string>();
let mockKnownHosts: KnownHost[] = [
  { id: 'web-prod-01.example.ch:22', fingerprint: 'SHA256:Gz8m1q0kXb1Tn5oV7mP2sA9fHc3dRyUeWl4JvNkQ0oE' },
  { id: '10.0.10.21:22', fingerprint: 'SHA256:aB3dEfGh1JkLmNoPqRsTuVwXyZ0123456789AbCdEfG' },
];

async function callMock(method: string, params: any): Promise<unknown> {
  await new Promise((r) => setTimeout(r, 30));
  const need = () => {
    if (!mock.unlocked) throw new Error('Der Vault ist gesperrt.');
  };
  const onlyNative = (what: string) => {
    throw new Error(`${what} gibt es nur in der Windows-App (im Browser siehst du nur die Oberfläche mit Demo-Daten).`);
  };

  switch (method) {
    case 'vault.status':
      return { exists: mock.password !== null, unlocked: mock.unlocked };
    case 'vault.create':
      if (!params.password || params.password.length < 8) throw new Error('Das Master-Passwort muss mindestens 8 Zeichen haben.');
      mock.password = params.password;
      mock.unlocked = true;
      mock.servers = demoServers();
      persistMock();
      return null;
    case 'vault.unlock':
      if (params.password !== mock.password) throw new Error('Falsches Passwort.');
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
      if (!s) throw new Error('Server nicht gefunden.');
      s.lastUsed = new Date().toISOString();
      persistMock();
      return s;
    }
    case 'hosts.forget':
      return null;
    case 'import.mremoteng':
      return onlyNative('Der mRemoteNG-Import');

    case 'keys.list':
      need();
      return mock.keys;
    case 'keys.save': {
      need();
      if (!params.name?.trim()) throw new Error('Bitte dem Key einen Namen geben.');
      const existing = mock.keys.find((k) => k.id === params.id);
      if (!existing && !String(params.privateKey ?? '').includes('PRIVATE KEY'))
        throw new Error('Das sieht nicht nach einem privaten SSH-Key aus (Zeile «BEGIN … PRIVATE KEY» fehlt).');
      const info: KeyInfo = {
        id: existing?.id ?? crypto.randomUUID(),
        name: params.name,
        kind: existing?.kind ?? (String(params.privateKey).includes('OPENSSH') ? 'ED25519' : 'RSA'),
        hasPassphrase: existing?.hasPassphrase ?? !!params.passphrase,
        updatedAt: new Date().toISOString(),
      };
      mock.keys = existing ? mock.keys.map((k) => (k.id === info.id ? info : k)) : [...mock.keys, info];
      persistMock();
      return info;
    }
    case 'keys.delete':
      need();
      mock.keys = mock.keys.filter((k) => k.id !== params.id);
      mock.servers = mock.servers.map((s) => (s.keyId === params.id ? { ...s, keyId: '', auth: 'password' } : s));
      persistMock();
      return null;

    case 'ssh.open': {
      need();
      const server = mock.servers.find((s) => s.id === params.serverId);
      if (!server) throw new Error('Server nicht gefunden.');
      mockShells.set(params.sessionId, { line: '', host: server.name });
      mock.history.push({ id: crypto.randomUUID(), serverId: server.id, serverName: server.name, host: `${server.host}:${server.port}`, kind: 'ssh', startedAt: new Date().toISOString(), endedAt: null, result: 'running', message: '' });
      persistMock();
      setTimeout(() => {
        mockShellWrite(
          params.sessionId,
          `\x1b[2mDemo-Shell (Browser-Mock) – verbunden mit ${server.username || 'user'}@${server.host}\x1b[0m\r\n` +
            'Probier: ls, whoami, colors, clear, exit\r\n\r\n' +
            PROMPT,
        );
      }, 150);
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
      // Nur für Entwicklung/Tests: localStorage.setItem('msv-dev-vnc-url', 'ws://127.0.0.1:PORT/TOKEN')
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
    case 'rdp.external':
      return onlyNative('RDP');

    case 'sync.get':
      return mock.sync;
    case 'sync.save': {
      mock.sync = {
        configured: !!(params.url && params.anonKey && params.email && (params.password || mock.sync.hasPassword)),
        url: params.url,
        anonKey: params.anonKey,
        email: params.email,
        hasPassword: !!params.password || mock.sync.hasPassword,
        lastSync: mock.sync.lastSync,
      };
      persistMock();
      return mock.sync;
    }
    case 'sync.test':
    case 'sync.signUp':
    case 'sync.now':
    case 'sync.downloadInitial':
      return onlyNative('Die Cloud-Synchronisation');
    case 'sync.disconnect':
      mock.sync = { configured: false, url: '', anonKey: '', email: '', hasPassword: false, lastSync: null };
      persistMock();
      return null;



    // ---- Einstellungen, Passwort, Backup
    case 'settings.get':
      return mock.settings;
    case 'settings.save':
      mock.settings = { ...mock.settings, ...params };
      persistMock();
      return mock.settings;
    case 'vault.changePassword':
      need();
      if (params.oldPassword !== mock.password) throw new Error('Falsches Passwort.');
      if (!params.newPassword || params.newPassword.length < 8) throw new Error('Das neue Master-Passwort muss mindestens 8 Zeichen haben.');
      mock.password = params.newPassword;
      persistMock();
      return { cloud: 'none' };
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
      return onlyNative('Diese Funktion');
    case 'vnc.close':
      return null;

    // ---- Identities
    case 'identities.list':
      need();
      return mock.identities;
    case 'identities.save': {
      need();
      if (!params.name?.trim()) throw new Error('Bitte der Identity einen Namen geben.');
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
      if (!params.name?.trim() || !params.command?.trim()) throw new Error('Name und Befehl sind Pflicht.');
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

    // ---- Port-Weiterleitungen
    case 'forwards.list':
      need();
      return mock.forwards.map((rule): ForwardItem => ({ rule, running: mockRunning.has(rule.id) }));
    case 'forwards.save': {
      need();
      if (!mock.servers.some((s) => s.id === params.serverId)) throw new Error('Bitte einen SSH-Host auswählen.');
      if (!(params.bindPort >= 1 && params.bindPort <= 65535)) throw new Error('Der Port muss zwischen 1 und 65535 liegen.');
      if (params.type !== 'dynamic' && (!params.destHost?.trim() || !(params.destPort >= 1))) throw new Error('Ziel-Adresse und Ziel-Port fehlen.');
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

    // ---- Known Hosts / Verlauf
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

    // ---- SFTP (Demo-Dateisystem im Speicher)
    case 'sftp.open':
      need();
      if (!mock.servers.some((s) => s.id === params.serverId)) throw new Error('Server nicht gefunden.');
      return { home: '/home/demo' };
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
        if (!entry) throw new Error('Datei nicht gefunden: ' + path);
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
      throw new Error('Unbekannte Methode: ' + method);
  }
}

// ---------------------------------------------------------------- öffentliche API

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
}

export interface SyncConfigInput {
  url: string;
  anonKey: string;
  email: string;
  password: string;
}

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
  importMremoteng: (xml: string) => call<ImportOutcome>('import.mremoteng', { xml }),

  keysList: () => call<KeyInfo[]>('keys.list'),
  keysSave: (k: { id?: string; name: string; privateKey?: string; passphrase?: string }) => call<KeyInfo>('keys.save', k),
  keysDelete: (id: string) => call<void>('keys.delete', { id }),

  sshOpen: (sessionId: string, serverId: string, cols: number, rows: number) =>
    call<void>('ssh.open', { sessionId, serverId, cols, rows }),
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

  sftpOpen: (sessionId: string, serverId: string) => call<{ home: string }>('sftp.open', { sessionId, serverId }),
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
  vaultInfo: () => call<{ path: string; version: string }>('vault.info'),
  vaultReveal: () => call<void>('vault.reveal'),
  syncAdoptPassword: (password: string) => call<void>('sync.adoptPassword', { password }),
  syncForceUpload: () => call<void>('sync.forceUpload'),
  importSshConfig: (text: string) => call<ImportOutcome>('import.sshconfig', { text }),
  importPutty: (text?: string) => call<ImportOutcome>('import.putty', { text: text ?? '' }),
  importCsv: (text: string) => call<ImportOutcome>('import.csv', { text }),

  syncGet: () => call<SyncInfo>('sync.get'),
  syncSave: (c: SyncConfigInput) => call<SyncInfo>('sync.save', c),
  syncTest: (c: SyncConfigInput) => call<void>('sync.test', c),
  syncSignUp: (c: SyncConfigInput) => call<{ signedIn: boolean }>('sync.signUp', c),
  syncNow: () => call<{ outcome: string; at: string }>('sync.now'),
  syncDisconnect: () => call<void>('sync.disconnect'),
  syncDownloadInitial: (c: SyncConfigInput) => call<void>('sync.downloadInitial', c),
};
