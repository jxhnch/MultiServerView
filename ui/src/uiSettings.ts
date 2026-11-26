/** Settings that only affect the interface (stored in the WebView2 browser storage). */
export interface UiSettings {
  /** 'system' follows the light or dark setting of the operating system. */
  themeMode: 'system' | 'light' | 'dark';
  /** Minutes of inactivity until the vault is locked automatically (0 = never). */
  autoLockMinutes: number;
  vncScale: boolean;
  /** 0 (least data) to 9 (best quality). */
  vncQuality: number;
  /** Ask GitHub for a newer version when the app starts. */
  checkUpdates: boolean;
  /** Reset the auto-lock inactivity timer on MCP requests. */
  mcpKeepUnlocked: boolean;
  /** Dots in the host list that show which hosts answer on their port. */
  hostStatus: boolean;
}

const KEY = 'msv-ui';
const DEFAULTS: UiSettings = { themeMode: 'system', autoLockMinutes: 0, vncScale: true, vncQuality: 6, checkUpdates: true, mcpKeepUnlocked: false, hostStatus: true };
let current: UiSettings = load();
const listeners = new Set<(s: UiSettings) => void>();

function load(): UiSettings {
  try {
    const raw = localStorage.getItem(KEY);
    if (raw) {
      const { theme, ...saved } = JSON.parse(raw) as Partial<UiSettings> & { theme?: string };
      // Before 'system' existed, light was the default and was saved along with any other setting: only dark was a choice.
      return { ...DEFAULTS, ...(theme === 'dark' ? { themeMode: 'dark' as const } : {}), ...saved };
    }
    // Take over the old theme key
    if (localStorage.getItem('msv-theme') === 'dark') return { ...DEFAULTS, themeMode: 'dark' };
  } catch {
    /* not a problem */
  }
  return { ...DEFAULTS };
}

export const getUiSettings = () => current;

const systemDark = window.matchMedia('(prefers-color-scheme: dark)');

/** What is really shown right now. */
export const effectiveTheme = (): 'light' | 'dark' => (current.themeMode === 'system' ? (systemDark.matches ? 'dark' : 'light') : current.themeMode);

/** Calls back when the setting changes, or when the system switches between light and dark. */
export function onTheme(fn: (theme: 'light' | 'dark') => void): () => void {
  const tell = () => fn(effectiveTheme());
  const off = onUiSettings(tell);
  systemDark.addEventListener('change', tell);
  tell();
  return () => {
    off();
    systemDark.removeEventListener('change', tell);
  };
}

export function setUiSettings(patch: Partial<UiSettings>) {
  current = { ...current, ...patch };
  try {
    localStorage.setItem(KEY, JSON.stringify(current));
  } catch {
    /* not a problem */
  }
  listeners.forEach((l) => l(current));
}

export function onUiSettings(fn: (s: UiSettings) => void): () => void {
  listeners.add(fn);
  return () => void listeners.delete(fn);
}
