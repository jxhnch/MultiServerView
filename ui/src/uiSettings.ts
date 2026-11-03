/** Settings that only affect the interface (stored in the WebView2 browser storage). */
export interface UiSettings {
  theme: 'light' | 'dark';
  /** Minutes of inactivity until the vault is locked automatically (0 = never). */
  autoLockMinutes: number;
  vncScale: boolean;
  /** 0 (least data) to 9 (best quality). */
  vncQuality: number;
}

const KEY = 'msv-ui';
const DEFAULTS: UiSettings = { theme: 'light', autoLockMinutes: 0, vncScale: true, vncQuality: 6 };
let current: UiSettings = load();
const listeners = new Set<(s: UiSettings) => void>();

function load(): UiSettings {
  try {
    const raw = localStorage.getItem(KEY);
    if (raw) return { ...DEFAULTS, ...(JSON.parse(raw) as Partial<UiSettings>) };
    // Take over the old theme key
    if (localStorage.getItem('msv-theme') === 'dark') return { ...DEFAULTS, theme: 'dark' };
  } catch {
    /* not a problem */
  }
  return { ...DEFAULTS };
}

export const getUiSettings = () => current;

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
  return () => listeners.delete(fn);
}
