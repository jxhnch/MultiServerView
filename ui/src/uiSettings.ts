/** Einstellungen, die nur die Oberfläche betreffen (liegen im Browser-Speicher von WebView2). */
export interface UiSettings {
  theme: 'light' | 'dark';
  /** Minuten Untätigkeit bis der Vault automatisch gesperrt wird (0 = nie). */
  autoLockMinutes: number;
  vncScale: boolean;
  /** 0 (kleinste Datenmenge) bis 9 (beste Qualität). */
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
    // Übernahme des alten Theme-Schlüssels
    if (localStorage.getItem('msv-theme') === 'dark') return { ...DEFAULTS, theme: 'dark' };
  } catch {
    /* nicht schlimm */
  }
  return { ...DEFAULTS };
}

export const getUiSettings = () => current;

export function setUiSettings(patch: Partial<UiSettings>) {
  current = { ...current, ...patch };
  try {
    localStorage.setItem(KEY, JSON.stringify(current));
  } catch {
    /* nicht schlimm */
  }
  listeners.forEach((l) => l(current));
}

export function onUiSettings(fn: (s: UiSettings) => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}
