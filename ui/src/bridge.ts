import { DEFAULT_PORT, type KeyInfo, type Server, type SyncInfo } from './types';

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
  sync: SyncInfo;
}
const MOCK_KEY = 'msv-mock-v2';

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
    group: '',
    tags: [],
    favorite: false,
    lastUsed: null,
    notes: '',
    ...s,
  });
  const ago = (min: number) => new Date(Date.now() - min * 60_000).toISOString();
  return [
    mk({ name: 'web-prod-01', host: '10.0.10.11', protocol: 'ssh', username: 'deploy', group: 'Produktion', tags: ['nginx'], favorite: true, lastUsed: ago(5) }),
    mk({ name: 'db-prod-01', host: '10.0.10.21', protocol: 'ssh', username: 'postgres', group: 'Produktion', tags: ['postgres'], favorite: true, lastUsed: ago(90) }),
    mk({ name: 'win-ad-01', host: '10.0.20.5', protocol: 'rdp', username: 'CORP\\Administrator', group: 'Produktion', tags: ['windows', 'ad'], lastUsed: ago(300) }),
    mk({ name: 'staging-web', host: 'staging.example.ch', protocol: 'ssh', username: 'ubuntu', group: 'Staging', tags: ['nginx'] }),
    mk({ name: 'staging-desktop', host: '10.0.30.8', protocol: 'vnc', group: 'Staging', tags: ['linux', 'xfce'] }),
    mk({ name: 'homelab-proxmox', host: '192.168.1.10', protocol: 'ssh', username: 'root', group: 'Homelab', favorite: true, lastUsed: ago(2000) }),
    mk({ name: 'homelab-nas', host: '192.168.1.20', protocol: 'ssh', username: 'admin', group: 'Homelab' }),
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

async function callMock(method: string, params: any): Promise<unknown> {
  await new Promise((r) => setTimeout(r, 30));
  const need = () => {
    if (!mock.unlocked) throw new Error('Der Vault ist gesperrt.');
  };
  const onlyNative = (what: string) => {
    throw new Error(`${what} funktioniert nur in der Windows-App (im Browser ist nur die Oberfläche mit Demo-Daten).`);
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
  importMremoteng: (xml: string) => call<{ imported: number; skipped: number }>('import.mremoteng', { xml }),

  keysList: () => call<KeyInfo[]>('keys.list'),
  keysSave: (k: { id?: string; name: string; privateKey?: string; passphrase?: string }) => call<KeyInfo>('keys.save', k),
  keysDelete: (id: string) => call<void>('keys.delete', { id }),

  sshOpen: (sessionId: string, serverId: string, cols: number, rows: number) =>
    call<void>('ssh.open', { sessionId, serverId, cols, rows }),
  sshInput: (sessionId: string, data: string) => call<void>('ssh.input', { sessionId, data }),
  sshResize: (sessionId: string, cols: number, rows: number) => call<void>('ssh.resize', { sessionId, cols, rows }),
  sshClose: (sessionId: string) => call<void>('ssh.close', { sessionId }),

  vncOpen: (serverId: string) => call<{ url: string; username: string; password: string }>('vnc.open', { serverId }),

  rdpOpen: (sessionId: string, serverId: string, r: Rect) => call<void>('rdp.open', { sessionId, serverId, ...r }),
  rdpLayout: (sessionId: string, r: Rect, visible: boolean) => call<void>('rdp.layout', { sessionId, ...r, visible }),
  rdpClose: (sessionId: string) => call<void>('rdp.close', { sessionId }),
  rdpSuspend: (suspended: boolean) => call<void>('rdp.suspend', { suspended }),
  rdpExternal: (serverId: string) => call<void>('rdp.external', { serverId }),

  syncGet: () => call<SyncInfo>('sync.get'),
  syncSave: (c: SyncConfigInput) => call<SyncInfo>('sync.save', c),
  syncTest: (c: SyncConfigInput) => call<void>('sync.test', c),
  syncSignUp: (c: SyncConfigInput) => call<{ signedIn: boolean }>('sync.signUp', c),
  syncNow: () => call<{ outcome: string; at: string }>('sync.now'),
  syncDisconnect: () => call<void>('sync.disconnect'),
  syncDownloadInitial: (c: SyncConfigInput) => call<void>('sync.downloadInitial', c),
};
