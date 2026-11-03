import type { ITheme } from '@xterm/xterm';

export interface TermTheme {
  id: string;
  name: string;
  bg: string;
  fg: string;
  cursor: string;
  selection: string;
  /** 16 ANSI-Farben: schwarz, rot, grün, gelb, blau, magenta, cyan, weiss, dann dieselben hell. */
  ansi: string[];
}

export const THEMES: TermTheme[] = [
  { id: 'night-owl', name: 'Night Owl', bg: '#011627', fg: '#d6deeb', cursor: '#80a4c2', selection: 'rgba(128,164,194,0.3)',
    ansi: ['#011627', '#ef5350', '#22da6e', '#c5e478', '#82aaff', '#c792ea', '#21c7a8', '#ffffff', '#575656', '#ef5350', '#22da6e', '#ffeb95', '#82aaff', '#c792ea', '#7fdbca', '#ffffff'] },
  { id: 'dracula', name: 'Dracula', bg: '#282a36', fg: '#f8f8f2', cursor: '#f8f8f2', selection: 'rgba(98,114,164,0.45)',
    ansi: ['#21222c', '#ff5555', '#50fa7b', '#f1fa8c', '#bd93f9', '#ff79c6', '#8be9fd', '#f8f8f2', '#6272a4', '#ff6e6e', '#69ff94', '#ffffa5', '#d6acff', '#ff92df', '#a4ffff', '#ffffff'] },
  { id: 'one-dark', name: 'One Dark', bg: '#282c34', fg: '#abb2bf', cursor: '#528bff', selection: 'rgba(82,139,255,0.3)',
    ansi: ['#282c34', '#e06c75', '#98c379', '#e5c07b', '#61afef', '#c678dd', '#56b6c2', '#abb2bf', '#5c6370', '#e06c75', '#98c379', '#e5c07b', '#61afef', '#c678dd', '#56b6c2', '#ffffff'] },
  { id: 'tokyo-night', name: 'Tokyo Night', bg: '#1a1b26', fg: '#c0caf5', cursor: '#c0caf5', selection: 'rgba(122,162,247,0.3)',
    ansi: ['#15161e', '#f7768e', '#9ece6a', '#e0af68', '#7aa2f7', '#bb9af7', '#7dcfff', '#a9b1d6', '#414868', '#f7768e', '#9ece6a', '#e0af68', '#7aa2f7', '#bb9af7', '#7dcfff', '#c0caf5'] },
  { id: 'nord', name: 'Nord', bg: '#2e3440', fg: '#d8dee9', cursor: '#d8dee9', selection: 'rgba(136,192,208,0.3)',
    ansi: ['#3b4252', '#bf616a', '#a3be8c', '#ebcb8b', '#81a1c1', '#b48ead', '#88c0d0', '#e5e9f0', '#4c566a', '#bf616a', '#a3be8c', '#ebcb8b', '#81a1c1', '#b48ead', '#8fbcbb', '#eceff4'] },
  { id: 'gruvbox', name: 'Gruvbox Dark', bg: '#282828', fg: '#ebdbb2', cursor: '#ebdbb2', selection: 'rgba(215,153,33,0.3)',
    ansi: ['#282828', '#cc241d', '#98971a', '#d79921', '#458588', '#b16286', '#689d6a', '#a89984', '#928374', '#fb4934', '#b8bb26', '#fabd2f', '#83a598', '#d3869b', '#8ec07c', '#ebdbb2'] },
  { id: 'monokai', name: 'Monokai', bg: '#272822', fg: '#f8f8f2', cursor: '#f8f8f0', selection: 'rgba(73,72,62,0.8)',
    ansi: ['#272822', '#f92672', '#a6e22e', '#f4bf75', '#66d9ef', '#ae81ff', '#a1efe4', '#f8f8f2', '#75715e', '#f92672', '#a6e22e', '#f4bf75', '#66d9ef', '#ae81ff', '#a1efe4', '#f9f8f5'] },
  { id: 'solarized-dark', name: 'Solarized Dark', bg: '#002b36', fg: '#839496', cursor: '#93a1a1', selection: 'rgba(7,54,66,0.9)',
    ansi: ['#073642', '#dc322f', '#859900', '#b58900', '#268bd2', '#d33682', '#2aa198', '#eee8d5', '#002b36', '#cb4b16', '#586e75', '#657b83', '#839496', '#6c71c4', '#93a1a1', '#fdf6e3'] },
  { id: 'hacker-green', name: 'Hacker Green', bg: '#000000', fg: '#22ff22', cursor: '#22ff22', selection: 'rgba(34,255,34,0.25)',
    ansi: ['#000000', '#ff3333', '#22ff22', '#cccc00', '#3399ff', '#cc33cc', '#00cccc', '#cccccc', '#555555', '#ff6666', '#66ff66', '#ffff55', '#66aaff', '#ff66ff', '#55ffff', '#ffffff'] },
  { id: 'light', name: 'Hell', bg: '#fafafa', fg: '#383a42', cursor: '#526fff', selection: 'rgba(64,120,242,0.25)',
    ansi: ['#383a42', '#e45649', '#50a14f', '#c18401', '#4078f2', '#a626a4', '#0184bc', '#a0a1a7', '#4f525e', '#e06c75', '#98c379', '#e5c07b', '#61afef', '#c678dd', '#56b6c2', '#ffffff'] },
];

export const FONTS = [
  { id: 'cascadia', name: 'Cascadia Code', css: '"Cascadia Code", "Cascadia Mono", Consolas, monospace' },
  { id: 'consolas', name: 'Consolas', css: 'Consolas, "Cascadia Mono", monospace' },
  { id: 'jetbrains', name: 'JetBrains Mono', css: '"JetBrains Mono", "Cascadia Code", Consolas, monospace' },
  { id: 'courier', name: 'Courier New', css: '"Courier New", Courier, monospace' },
];

export interface TermSettings {
  theme: string;
  font: string;
  fontSize: number;
  cursorStyle: 'bar' | 'block' | 'underline';
  cursorBlink: boolean;
  scrollback: number;
  /** Markierten Text sofort in die Zwischenablage kopieren. */
  copyOnSelect: boolean;
}

const KEY = 'msv-terminal';
const DEFAULTS: TermSettings = { theme: 'night-owl', font: 'cascadia', fontSize: 14, cursorStyle: 'bar', cursorBlink: true, scrollback: 10000, copyOnSelect: false };
let current: TermSettings = load();
const listeners = new Set<(s: TermSettings) => void>();

function load(): TermSettings {
  try {
    const raw = localStorage.getItem(KEY);
    if (raw) return { ...DEFAULTS, ...(JSON.parse(raw) as Partial<TermSettings>) };
  } catch {
    /* nicht schlimm */
  }
  return { ...DEFAULTS };
}

export const getTermSettings = () => current;

export function setTermSettings(patch: Partial<TermSettings>) {
  current = { ...current, ...patch, fontSize: Math.min(26, Math.max(9, patch.fontSize ?? current.fontSize)) };
  try {
    localStorage.setItem(KEY, JSON.stringify(current));
  } catch {
    /* nicht schlimm */
  }
  listeners.forEach((l) => l(current));
}

export function onTermSettings(fn: (s: TermSettings) => void): () => void {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

export const findTheme = (id: string) => THEMES.find((t) => t.id === id) ?? THEMES[0];
export const findFont = (id: string) => FONTS.find((f) => f.id === id) ?? FONTS[0];

export function toXtermTheme(t: TermTheme): ITheme {
  const [black, red, green, yellow, blue, magenta, cyan, white, bBlack, bRed, bGreen, bYellow, bBlue, bMagenta, bCyan, bWhite] = t.ansi;
  return {
    background: t.bg, foreground: t.fg, cursor: t.cursor, cursorAccent: t.bg, selectionBackground: t.selection,
    black, red, green, yellow, blue, magenta, cyan, white,
    brightBlack: bBlack, brightRed: bRed, brightGreen: bGreen, brightYellow: bYellow,
    brightBlue: bBlue, brightMagenta: bMagenta, brightCyan: bCyan, brightWhite: bWhite,
  };
}
