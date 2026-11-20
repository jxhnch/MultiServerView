import { useEffect, useRef, useState } from 'react';
import { api } from './bridge';
import { canvasToPng, parseKeys, pointerEvent, registerScreen, sleep, wheelEvent, charKey, type KeyDef, type MouseButton } from './mcpScreens';
import { SessionFrame } from './SessionFrame';
import type { Server, SessionStatus } from './types';

/**
 * RDP session drawn by the UI itself (IronRDP/WebAssembly in a canvas). Used where the native Windows RDP control
 * does not exist (Linux). The host only provides a local WebSocket proxy that does TCP + TLS for the page.
 */
export function RdpWebView({
  tabId,
  server,
  reconnectKey,
  onStatus,
  onReconnect,
  onDuplicate,
  onEdit,
}: {
  tabId: string;
  server: Server;
  active: boolean;
  reconnectKey: number;
  onStatus: (tabId: string, status: SessionStatus) => void;
  onReconnect: () => void;
  onDuplicate: () => void;
  onEdit: () => void;
}) {
  const boxRef = useRef<HTMLDivElement>(null);
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [message, setMessage] = useState('');
  const statusRef = useRef<SessionStatus>('connecting');

  useEffect(() => {
    let cancelled = false;
    let ui: any = null;
    let element: HTMLElement | null = null;
    let resizeTimer: number | undefined;
    let unregister: (() => void) | undefined;
    const setStatus = (s: SessionStatus, msg = '') => {
      if (cancelled) return;
      statusRef.current = s;
      setStatusState(s);
      setMessage(msg);
      onStatus(tabId, s);
    };
    setStatus('connecting');

    const box = boxRef.current!;
    const ro = new ResizeObserver(() => {
      window.clearTimeout(resizeTimer);
      resizeTimer = window.setTimeout(() => {
        const r = box.getBoundingClientRect();
        if (ui && r.width > 100 && r.height > 100) {
          try {
            ui.resize(Math.round(r.width) & ~3, Math.round(r.height));
          } catch {
            /* the server does not support resizing */
          }
        }
      }, 300);
    });
    ro.observe(box);

    void (async () => {
      try {
        const [{ Backend, init, displayControl }] = await Promise.all([import('@devolutions/iron-remote-desktop-rdp'), import('@devolutions/iron-remote-desktop')]);
        await init('warn');
        if (cancelled) return;

        const info = await api.rdpWeb(tabId, server.id);
        if (cancelled) return;
        if (!info.password) throw new Error('This host has no saved password. Edit the host and enter one to connect inside the app.');

        element = document.createElement('iron-remote-desktop');
        element.setAttribute('scale', 'fit');
        element.setAttribute('flexcenter', 'true');
        (element as any).module = Backend;
        element.style.cssText = 'display:block;width:100%;height:100%';
        box.appendChild(element);

        ui = await new Promise<any>((resolve) => element!.addEventListener('ready', (e) => resolve((e as CustomEvent).detail.irgUserInteraction), { once: true }));
        if (cancelled) return;

        const r = box.getBoundingClientRect();
        // xrdp and some servers need a width that is a multiple of 4
        const width = Math.max(800, Math.round(r.width) & ~3);
        const height = Math.max(600, Math.round(r.height));
        const config = ui
          .configBuilder()
          .withUsername(info.username)
          .withPassword(info.password)
          .withServerDomain(info.domain)
          .withDestination(`${info.host}:${server.port || 3389}`)
          .withProxyAddress(info.url)
          .withAuthToken('local')
          .withDesktopSize({ width, height })
          .withExtension(displayControl(true))
          .build();
        ui.setEnableClipboard(true);
        const session = await ui.connect(config);
        if (cancelled) {
          try {
            ui.shutdown();
          } catch {
            /* already shut down */
          }
          return;
        }
        ui.setVisibility(true);
        setStatus('connected');
        unregister = registerRemoteScreen(tabId, server.id, server.name, () => statusRef.current, () => element, ui);
        void session.run().then(
          (end: { reason: () => string }) => setStatus('closed', end.reason()),
          (err: unknown) => setStatus('closed', describe(err)),
        );
      } catch (err) {
        setStatus('error', describe(err));
      }
    })();

    return () => {
      cancelled = true;
      unregister?.();
      ro.disconnect();
      window.clearTimeout(resizeTimer);
      try {
        ui?.shutdown();
      } catch {
        /* already closed */
      }
      element?.remove();
      void api.rdpClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey]);

  return (
    <SessionFrame variant="bar" server={server} status={status} onReconnect={onReconnect} onDuplicate={onDuplicate} onEdit={onEdit}>
      <div className="rdp-box rdp-web" ref={boxRef}>
        {status !== 'connected' && (
          <div className="overlay-msg">
            {status === 'connecting' && <div className="muted">Connecting to {server.host} …</div>}
            {(status === 'error' || status === 'closed') && (
              <>
                <div className={status === 'error' ? 'error' : 'muted'}>{message || 'Disconnected.'}</div>
                <button className="primary" onClick={onReconnect}>
                  Reconnect
                </button>
              </>
            )}
          </div>
        )}
      </div>
    </SessionFrame>
  );
}

/**
 * Lets an AI tool (MCP) see and operate this RDP screen: screenshots come from the canvas, input is sent as the same
 * mouse and keyboard events a person would cause, so the RDP client turns them into RDP input.
 */
function registerRemoteScreen(tabId: string, serverId: string, name: string, state: () => SessionStatus, getElement: () => HTMLElement | null, ui: any): () => void {
  const canvas = () => {
    const c = getElement()?.shadowRoot?.querySelector('canvas') as HTMLCanvasElement | null;
    if (!c) throw new Error('The remote screen is not ready.');
    return c;
  };
  const target = () => canvas();
  const key = (def: Pick<KeyDef, 'key' | 'code'>, down: boolean, mods: { ctrl: boolean; alt: boolean; shift: boolean; meta: boolean }) =>
    window.dispatchEvent(new KeyboardEvent(down ? 'keydown' : 'keyup', { key: def.key, code: def.code, ctrlKey: mods.ctrl, altKey: mods.alt, shiftKey: mods.shift, metaKey: mods.meta, bubbles: true, cancelable: true }));
  const focus = () => getElement()?.focus();

  return registerScreen({
    id: tabId,
    kind: 'rdp',
    serverId,
    name,
    state,
    size: () => {
      try {
        const c = canvas();
        return { width: c.width, height: c.height };
      } catch {
        return null;
      }
    },
    screenshot: () => canvasToPng(canvas()),
    async move(x, y) {
      pointerEvent(target(), canvas(), 'mousemove', x, y);
    },
    async click(x, y, button: MouseButton, double) {
      const c = canvas();
      pointerEvent(target(), c, 'mousemove', x, y);
      await sleep(40);
      for (let i = 0; i < (double ? 2 : 1); i++) {
        pointerEvent(target(), c, 'mousedown', x, y, button);
        await sleep(40);
        pointerEvent(target(), c, 'mouseup', x, y, button);
        await sleep(60);
      }
      focus();
    },
    async drag(fromX, fromY, toX, toY) {
      const c = canvas();
      pointerEvent(target(), c, 'mousemove', fromX, fromY);
      await sleep(60);
      pointerEvent(target(), c, 'mousedown', fromX, fromY);
      const steps = 8;
      for (let i = 1; i <= steps; i++) {
        await sleep(30);
        pointerEvent(target(), c, 'mousemove', fromX + ((toX - fromX) * i) / steps, fromY + ((toY - fromY) * i) / steps);
      }
      await sleep(60);
      pointerEvent(target(), c, 'mouseup', toX, toY);
    },
    async scroll(x, y, amount) {
      const c = canvas();
      pointerEvent(target(), c, 'mousemove', x, y);
      wheelEvent(target(), c, x, y, amount * 100);
    },
    async type(text, method) {
      focus();
      await sleep(30);
      const none = { ctrl: false, alt: false, shift: false, meta: false };
      if (method === 'keys') {
        // Real key presses (US layout): works with every server.
        for (const ch of text) {
          const k = ch === '\n' || ch === '\r' ? { key: 'Enter', code: 'Enter', shift: false } : ch === '\t' ? { key: 'Tab', code: 'Tab', shift: false } : charKey(ch);
          if (!k) continue; // not on a US keyboard: use method=unicode for such characters
          const mods = { ...none, shift: k.shift };
          // Some servers drop a key that follows the Shift press too quickly: give them a moment.
          if (k.shift) {
            key({ key: 'Shift', code: 'ShiftLeft' }, true, mods);
            await sleep(30);
          }
          // the event carries the lower case key plus the Shift state, like a real keyboard under the Shift key
          const ev = { key: k.key.toLowerCase(), code: k.code };
          key(ev, true, mods);
          await sleep(k.shift ? 30 : 10);
          key(ev, false, mods);
          if (k.shift) {
            await sleep(30);
            key({ key: 'Shift', code: 'ShiftLeft' }, false, none);
          }
          await sleep(12);
        }
        return;
      }
      // Printable characters are sent as unicode (independent of the keyboard layout), Enter and Tab as keys.
      ui.setKeyboardUnicodeMode(true);
      try {
        for (const ch of text) {
          if (ch === '\n' || ch === '\r') {
            key({ key: 'Enter', code: 'Enter' }, true, none);
            key({ key: 'Enter', code: 'Enter' }, false, none);
          } else if (ch === '\t') {
            key({ key: 'Tab', code: 'Tab' }, true, none);
            key({ key: 'Tab', code: 'Tab' }, false, none);
          } else {
            key({ key: ch, code: '' }, true, none);
            key({ key: ch, code: '' }, false, none);
          }
          await sleep(8);
        }
      } finally {
        ui.setKeyboardUnicodeMode(false);
      }
    },
    async key(keys) {
      focus();
      await sleep(30);
      for (const combo of parseKeys(keys)) {
        const names = combo.map((k) => k.code).join('+');
        // The browser cannot send these itself: the RDP client has them built in.
        if (names === 'ControlLeft+AltLeft+Delete') {
          ui.ctrlAltDel();
          await sleep(60);
          continue;
        }
        if (names === 'MetaLeft') {
          ui.metaKey();
          await sleep(60);
          continue;
        }
        const mods = { ctrl: false, alt: false, shift: false, meta: false };
        const down: KeyDef[] = [];
        for (const k of combo) {
          if (k.modifier) mods[k.modifier] = true;
          const withShift = charKey(k.key.length === 1 ? k.key : '')?.shift;
          if (withShift) mods.shift = true;
          key(k, true, mods);
          down.push(k);
          await sleep(25);
        }
        for (const k of down.reverse()) {
          key(k, false, mods);
          await sleep(25);
        }
        await sleep(40);
      }
    },
  });
}

/** IronRDP errors carry a kind; translate the common ones. */
function describe(err: unknown): string {
  const e = err as { kind?: () => number; backtrace?: () => string; message?: string };
  try {
    switch (e?.kind?.()) {
      case 1:
      case 2:
        return 'Authentication failed. Check the user name and password of this host.';
      case 3:
        return 'Access denied by the server.';
      case 4:
      case 5:
        return 'Could not reach the RDP server. Check the address, the port and the firewall.';
      case 6:
        return 'The server and the client could not agree on a security protocol (NLA/TLS).';
    }
    const bt = e?.backtrace?.();
    if (bt) return bt.split('\n')[0];
  } catch {
    /* fall through */
  }
  return e?.message ?? String(err);
}
