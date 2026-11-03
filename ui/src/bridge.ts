import { DEFAULT_PORT, type Server } from './types';

/**
 * Verbindung zum C#-Host (WebView2). Im normalen Browser (npm run dev) gibt es den Host nicht -
 * dann laeuft eine Mock-Version mit localStorage, damit man das UI ohne Windows entwickeln kann.
 * ACHTUNG: Der Mock speichert unverschluesselt und ist nur fuer die Entwicklung gedacht.
 */

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
    const msg = e.data as Reply;
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
}
const MOCK_KEY = 'msv-mock';

function loadMock(): MockState {
  try {
    const raw = localStorage.getItem(MOCK_KEY);
    if (raw) return { ...(JSON.parse(raw) as MockState), unlocked: false };
  } catch {
    /* ignore */
  }
  return { password: null, unlocked: false, servers: [] };
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
    mk({ name: 'win-ad-01', host: '10.0.20.5', protocol: 'rdp', username: 'Administrator', group: 'Produktion', tags: ['windows', 'ad'], lastUsed: ago(300) }),
    mk({ name: 'staging-web', host: 'staging.example.ch', protocol: 'ssh', username: 'ubuntu', group: 'Staging', tags: ['nginx'] }),
    mk({ name: 'staging-desktop', host: '10.0.30.8', protocol: 'vnc', group: 'Staging', tags: ['linux', 'xfce'] }),
    mk({ name: 'homelab-proxmox', host: '192.168.1.10', protocol: 'ssh', username: 'root', group: 'Homelab', favorite: true, lastUsed: ago(2000) }),
    mk({ name: 'homelab-nas', host: '192.168.1.20', protocol: 'ssh', username: 'admin', group: 'Homelab' }),
    mk({ name: 'homelab-win11', host: '192.168.1.40', protocol: 'rdp', username: 'john', group: 'Homelab', tags: ['windows'] }),
  ];
}

async function callMock(method: string, params: any): Promise<unknown> {
  await new Promise((r) => setTimeout(r, 40));
  const need = () => {
    if (!mock.unlocked) throw new Error('Der Vault ist gesperrt.');
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
    default:
      throw new Error('Unbekannte Methode: ' + method);
  }
}

// ---------------------------------------------------------------- oeffentliche API

const call = <T>(method: string, params?: unknown): Promise<T> =>
  isNative ? callNative<T>(method, params) : (callMock(method, params) as Promise<T>);

export const api = {
  vaultStatus: () => call<{ exists: boolean; unlocked: boolean }>('vault.status'),
  vaultCreate: (password: string) => call<void>('vault.create', { password }),
  vaultUnlock: (password: string) => call<void>('vault.unlock', { password }),
  vaultLock: () => call<void>('vault.lock'),
  serversList: () => call<Server[]>('servers.list'),
  serversSave: (s: Server) => call<Server>('servers.save', s),
  serversDelete: (id: string) => call<void>('servers.delete', { id }),
  serversTouch: (id: string) => call<Server>('servers.touch', { id }),
};
