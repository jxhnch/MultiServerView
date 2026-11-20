import { useEffect, useRef, useState, type FormEvent } from 'react';
import RFB from '@novnc/novnc';
import { api } from './bridge';
import { canvasToPng, parseKeys, pointerEvent, registerScreen, sleep, wheelEvent, type MouseButton } from './mcpScreens';
import { SessionFrame } from './SessionFrame';
import { getUiSettings } from './uiSettings';
import type { Server, SessionStatus } from './types';

/** VNC session via noVNC. The bytes go through a local bridge in the host (one-time token). */
export function VncView({
  tabId,
  server,
  active,
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
  const rfbRef = useRef<RFB | null>(null);
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [message, setMessage] = useState('');
  const [needsPassword, setNeedsPassword] = useState(false);
  const [pw, setPw] = useState('');
  const activeRef = useRef(active);
  activeRef.current = active;
  const statusRef = useRef<SessionStatus>('connecting');

  const setStatus = (s: SessionStatus, msg = '') => {
    statusRef.current = s;
    setStatusState(s);
    setMessage(msg);
    onStatus(tabId, s);
  };

  useEffect(() => {
    let cancelled = false;
    let rfb: RFB | null = null;
    let unregister: (() => void) | undefined;
    setStatus('connecting');
    setNeedsPassword(false);
    setPw('');

    api
      .vncOpen(server.id, tabId)
      .then(({ url, username, password }) => {
        if (cancelled || !boxRef.current) return;
        boxRef.current.replaceChildren();
        rfb = new RFB(boxRef.current, url, { credentials: { username, password }, shared: true });
        const ui = getUiSettings();
        rfb.scaleViewport = ui.vncScale;
        rfb.qualityLevel = ui.vncQuality;
        rfb.resizeSession = false;
        rfb.background = '#0b0d12';
        rfbRef.current = rfb;

        rfb.addEventListener('connect', () => {
          if (cancelled) return;
          setStatus('connected');
          unregister?.();
          unregister = registerVncScreen(tabId, server.id, server.name, () => statusRef.current, () => boxRef.current, () => rfbRef.current);
          if (activeRef.current) rfb?.focus();
        });
        rfb.addEventListener('disconnect', (e) => {
          if (cancelled) return;
          const clean = (e as CustomEvent<{ clean: boolean }>).detail?.clean;
          setStatus(clean ? 'closed' : 'error', clean ? '' : 'Connection interrupted or refused.');
        });
        rfb.addEventListener('credentialsrequired', () => {
          if (!cancelled) setNeedsPassword(true);
        });
        rfb.addEventListener('securityfailure', (e) => {
          if (cancelled) return;
          const reason = (e as CustomEvent<{ reason?: string }>).detail?.reason;
          setStatus('error', 'Authentication failed' + (reason ? `: ${reason}` : '.'));
        });
      })
      .catch((err: Error) => {
        if (!cancelled) setStatus('error', err.message);
      });

    return () => {
      cancelled = true;
      unregister?.();
      try {
        rfb?.disconnect();
      } catch {
        /* already disconnected */
      }
      rfbRef.current = null;
      void api.vncClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey]);

  useEffect(() => {
    if (active) rfbRef.current?.focus();
    else rfbRef.current?.blur();
  }, [active]);

  const sendPassword = (e: FormEvent) => {
    e.preventDefault();
    rfbRef.current?.sendCredentials({ password: pw });
    setNeedsPassword(false);
  };

  const sendClipboard = async () => {
    try {
      const text = await navigator.clipboard.readText();
      rfbRef.current?.clipboardPasteFrom(text);
    } catch {
      /* clipboard not allowed */
    }
  };

  return (
    <SessionFrame
      server={server}
      status={status}
      onReconnect={onReconnect}
      onDuplicate={onDuplicate}
      onEdit={onEdit}
      extraActions={
        <>
          <button onClick={() => rfbRef.current?.sendCtrlAltDel()} disabled={status !== 'connected'}>
            Ctrl+Alt+Del
          </button>
          <button onClick={sendClipboard} disabled={status !== 'connected'}>
            Send clipboard
          </button>
        </>
      }
    >
      <div className="vnc-wrap">
        <div className="vnc-box" ref={boxRef} />
        {status === 'connecting' && !needsPassword && <div className="overlay-msg muted">Connecting…</div>}
        {(status === 'error' || status === 'closed') && (
          <div className="overlay-msg">
            <div className={status === 'error' ? 'error' : 'muted'}>{message || 'Disconnected.'}</div>
            <button className="primary" onClick={onReconnect}>
              Reconnect
            </button>
          </div>
        )}
        {needsPassword && (
          <form className="overlay-msg" onSubmit={sendPassword}>
            <div>This VNC server requires a password.</div>
            <input type="password" autoFocus placeholder="VNC password" value={pw} onChange={(e) => setPw(e.target.value)} />
            <button className="primary">Sign in</button>
          </form>
        )}
      </div>
    </SessionFrame>
  );
}

/** Lets an AI tool (MCP) see and operate this VNC screen. */
function registerVncScreen(tabId: string, serverId: string, name: string, state: () => SessionStatus, getBox: () => HTMLElement | null, getRfb: () => RFB | null): () => void {
  const canvas = () => {
    const c = getBox()?.querySelector('canvas') as HTMLCanvasElement | null;
    if (!c) throw new Error('The remote screen is not ready.');
    return c;
  };
  const rfb = () => getRfb() ?? (() => { throw new Error('The remote screen is not connected.'); })();
  const press = (keysym: number, code: string, down: boolean) => rfb().sendKey(keysym, code, down);

  return registerScreen({
    id: tabId,
    kind: 'vnc',
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
      pointerEvent(canvas(), canvas(), 'mousemove', x, y);
    },
    async click(x, y, button: MouseButton, double) {
      const c = canvas();
      pointerEvent(c, c, 'mousemove', x, y);
      await sleep(40);
      for (let i = 0; i < (double ? 2 : 1); i++) {
        pointerEvent(c, c, 'mousedown', x, y, button);
        await sleep(40);
        pointerEvent(c, c, 'mouseup', x, y, button);
        await sleep(60);
      }
    },
    async drag(fromX, fromY, toX, toY) {
      const c = canvas();
      pointerEvent(c, c, 'mousemove', fromX, fromY);
      await sleep(60);
      pointerEvent(c, c, 'mousedown', fromX, fromY);
      const steps = 8;
      for (let i = 1; i <= steps; i++) {
        await sleep(30);
        pointerEvent(c, c, 'mousemove', fromX + ((toX - fromX) * i) / steps, fromY + ((toY - fromY) * i) / steps);
      }
      await sleep(60);
      pointerEvent(c, c, 'mouseup', toX, toY);
    },
    async scroll(x, y, amount) {
      const c = canvas();
      pointerEvent(c, c, 'mousemove', x, y);
      wheelEvent(c, c, x, y, amount * 100);
    },
    async type(text) {
      // VNC sends key symbols, which are independent of the layout: both methods are the same here.
      for (const ch of text) {
        const keysym = ch === '\n' || ch === '\r' ? 0xff0d : ch === '\t' ? 0xff09 : ch.codePointAt(0)! < 0x100 ? ch.codePointAt(0)! : 0x01000000 + ch.codePointAt(0)!;
        press(keysym, '', true);
        press(keysym, '', false);
        await sleep(8);
      }
    },
    async key(keys) {
      for (const combo of parseKeys(keys)) {
        for (const k of combo) {
          press(k.keysym, k.code, true);
          await sleep(25);
        }
        for (const k of [...combo].reverse()) {
          press(k.keysym, k.code, false);
          await sleep(25);
        }
        await sleep(40);
      }
    },
  });
}
