import type { SessionStatus } from './types';

/**
 * Remote screens (RDP in a canvas, VNC) that an AI tool can look at and operate through the MCP server.
 * RdpWebView and VncView register a handle for their tab; App answers the "mcp.screen" requests of the host with it.
 */

export type MouseButton = 'left' | 'right' | 'middle';

export interface ScreenHandle {
  /** Tab id. */
  id: string;
  kind: 'rdp' | 'vnc';
  serverId: string;
  name: string;
  state(): SessionStatus;
  /** Size of the remote screen in pixels, or null while unknown. */
  size(): { width: number; height: number } | null;
  /** PNG (base64, no prefix). */
  screenshot(): Promise<string>;
  move(x: number, y: number): Promise<void>;
  click(x: number, y: number, button: MouseButton, double: boolean): Promise<void>;
  drag(fromX: number, fromY: number, toX: number, toY: number): Promise<void>;
  scroll(x: number, y: number, amount: number): Promise<void>;
  /** method: "unicode" = characters (layout independent), "keys" = key presses for a US layout. */
  type(text: string, method: 'unicode' | 'keys'): Promise<void>;
  /** Space separated keys or shortcuts: "ctrl+c", "alt+tab", "enter". */
  key(keys: string): Promise<void>;
}

const screens = new Map<string, ScreenHandle>();

export function registerScreen(handle: ScreenHandle): () => void {
  screens.set(handle.id, handle);
  return () => {
    if (screens.get(handle.id) === handle) screens.delete(handle.id);
  };
}

export const getScreen = (id: string) => screens.get(id);
export const allScreens = () => [...screens.values()];

export const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- Keys

export interface KeyDef {
  /** KeyboardEvent.key */
  key: string;
  /** KeyboardEvent.code (physical key; the RDP client sends scancodes from it). */
  code: string;
  /** X11 keysym for VNC. */
  keysym: number;
  modifier?: 'ctrl' | 'alt' | 'shift' | 'meta';
}

const NAMED: Record<string, KeyDef> = {
  enter: { key: 'Enter', code: 'Enter', keysym: 0xff0d },
  return: { key: 'Enter', code: 'Enter', keysym: 0xff0d },
  tab: { key: 'Tab', code: 'Tab', keysym: 0xff09 },
  esc: { key: 'Escape', code: 'Escape', keysym: 0xff1b },
  escape: { key: 'Escape', code: 'Escape', keysym: 0xff1b },
  backspace: { key: 'Backspace', code: 'Backspace', keysym: 0xff08 },
  delete: { key: 'Delete', code: 'Delete', keysym: 0xffff },
  del: { key: 'Delete', code: 'Delete', keysym: 0xffff },
  insert: { key: 'Insert', code: 'Insert', keysym: 0xff63 },
  home: { key: 'Home', code: 'Home', keysym: 0xff50 },
  end: { key: 'End', code: 'End', keysym: 0xff57 },
  pageup: { key: 'PageUp', code: 'PageUp', keysym: 0xff55 },
  pagedown: { key: 'PageDown', code: 'PageDown', keysym: 0xff56 },
  up: { key: 'ArrowUp', code: 'ArrowUp', keysym: 0xff52 },
  down: { key: 'ArrowDown', code: 'ArrowDown', keysym: 0xff54 },
  left: { key: 'ArrowLeft', code: 'ArrowLeft', keysym: 0xff51 },
  right: { key: 'ArrowRight', code: 'ArrowRight', keysym: 0xff53 },
  space: { key: ' ', code: 'Space', keysym: 0x20 },
  ctrl: { key: 'Control', code: 'ControlLeft', keysym: 0xffe3, modifier: 'ctrl' },
  control: { key: 'Control', code: 'ControlLeft', keysym: 0xffe3, modifier: 'ctrl' },
  alt: { key: 'Alt', code: 'AltLeft', keysym: 0xffe9, modifier: 'alt' },
  shift: { key: 'Shift', code: 'ShiftLeft', keysym: 0xffe1, modifier: 'shift' },
  win: { key: 'Meta', code: 'MetaLeft', keysym: 0xffeb, modifier: 'meta' },
  windows: { key: 'Meta', code: 'MetaLeft', keysym: 0xffeb, modifier: 'meta' },
  super: { key: 'Meta', code: 'MetaLeft', keysym: 0xffeb, modifier: 'meta' },
  cmd: { key: 'Meta', code: 'MetaLeft', keysym: 0xffeb, modifier: 'meta' },
  meta: { key: 'Meta', code: 'MetaLeft', keysym: 0xffeb, modifier: 'meta' },
};
for (let i = 1; i <= 12; i++) NAMED['f' + i] = { key: 'F' + i, code: 'F' + i, keysym: 0xffbd + i };

const PUNCT: Record<string, string> = {
  '-': 'Minus', '=': 'Equal', '[': 'BracketLeft', ']': 'BracketRight', ';': 'Semicolon', "'": 'Quote', ',': 'Comma', '.': 'Period', '/': 'Slash', '\\': 'Backslash', '`': 'Backquote',
};

/** One printable character (US layout): the key and whether Shift is needed. */
export function charKey(ch: string): (KeyDef & { shift: boolean }) | null {
  if (ch.length !== 1) return null;
  const lower = ch.toLowerCase();
  if (/[a-zA-Z]/.test(ch)) return { key: ch, code: 'Key' + lower.toUpperCase(), keysym: ch.charCodeAt(0), shift: ch !== lower };
  if (/[0-9]/.test(ch)) return { key: ch, code: 'Digit' + ch, keysym: ch.charCodeAt(0), shift: false };
  if (ch === ' ') return { key: ' ', code: 'Space', keysym: 0x20, shift: false };
  if (PUNCT[ch]) return { key: ch, code: PUNCT[ch], keysym: ch.charCodeAt(0), shift: false };
  const shifted: Record<string, string> = { '!': 'Digit1', '@': 'Digit2', '#': 'Digit3', $: 'Digit4', '%': 'Digit5', '^': 'Digit6', '&': 'Digit7', '*': 'Digit8', '(': 'Digit9', ')': 'Digit0', _: 'Minus', '+': 'Equal', '{': 'BracketLeft', '}': 'BracketRight', ':': 'Semicolon', '"': 'Quote', '<': 'Comma', '>': 'Period', '?': 'Slash', '|': 'Backslash', '~': 'Backquote' };
  if (shifted[ch]) return { key: ch, code: shifted[ch], keysym: ch.charCodeAt(0), shift: true };
  return null;
}

/** "ctrl+alt+delete" -> list of keys, modifiers first. */
export function parseCombo(combo: string): KeyDef[] {
  const parts = combo
    .trim()
    .split('+')
    .map((p) => p.trim())
    .filter(Boolean);
  if (parts.length === 0) throw new Error('No key given.');
  return parts.map((raw) => {
    const named = NAMED[raw.toLowerCase()];
    if (named) return named;
    const c = charKey(raw.length === 1 ? raw.toLowerCase() : '');
    if (c) return c;
    throw new Error(`Unknown key "${raw}".`);
  });
}

/** Several combos separated by spaces: "ctrl+a ctrl+c". */
export const parseKeys = (keys: string): KeyDef[][] => keys.trim().split(/\s+/).map(parseCombo);

// ---------------------------------------------------------------- DOM pointer helpers (canvas based screens)

const BUTTONS: Record<MouseButton, number> = { left: 0, middle: 1, right: 2 };

/** Converts remote screen pixels to client coordinates of the canvas on the page. */
export function canvasPoint(canvas: HTMLCanvasElement, x: number, y: number): { clientX: number; clientY: number } {
  const r = canvas.getBoundingClientRect();
  if (r.width < 2 || r.height < 2) throw new Error('The screen is not visible. Open its tab first.');
  const w = canvas.width || r.width;
  const h = canvas.height || r.height;
  return { clientX: r.left + (x * r.width) / w, clientY: r.top + (y * r.height) / h };
}

export function pointerEvent(target: EventTarget, canvas: HTMLCanvasElement, type: 'mousemove' | 'mousedown' | 'mouseup' | 'dblclick', x: number, y: number, button: MouseButton = 'left'): void {
  const { clientX, clientY } = canvasPoint(canvas, x, y);
  const b = BUTTONS[button];
  const pressed = type === 'mousedown' ? 1 << b : 0;
  target.dispatchEvent(new MouseEvent(type, { bubbles: true, composed: true, cancelable: true, clientX, clientY, screenX: clientX, screenY: clientY, button: b, buttons: pressed, view: window }));
}

export function wheelEvent(target: EventTarget, canvas: HTMLCanvasElement, x: number, y: number, deltaY: number): void {
  const { clientX, clientY } = canvasPoint(canvas, x, y);
  target.dispatchEvent(new WheelEvent('wheel', { bubbles: true, composed: true, cancelable: true, clientX, clientY, deltaY, deltaMode: 1 }));
}

export async function canvasToPng(canvas: HTMLCanvasElement): Promise<string> {
  const url = canvas.toDataURL('image/png');
  const i = url.indexOf(',');
  return i >= 0 ? url.slice(i + 1) : url;
}
