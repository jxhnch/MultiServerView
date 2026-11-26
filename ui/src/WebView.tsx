import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { api, onEvent, type Rect } from './bridge';
import { IconArrowLeft, IconArrowRight, IconGlobe, IconRefresh } from './Icons';
import { SessionFrame } from './SessionFrame';
import type { Server, SessionStatus } from './types';

/**
 * Integrated browser: the page of a device (firewall, Proxmox, ...) in a tab.
 * Windows: a real browser control that the host places exactly over the area below the address bar (.web-box), so this view
 * only reports position, size and visibility. Linux: the host serves the page through a local proxy and it is shown in a frame.
 */
export function WebView({
  tabId,
  server,
  active,
  reconnectKey,
  onStatus,
  onReconnect,
  onDuplicate,
  onEdit,
  onCorrected,
}: {
  /** The app found that another address works and saved it. */
  onCorrected: (server: Server, address: string) => void;
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
  const frameRef = useRef<HTMLIFrameElement>(null);
  const [mode, setMode] = useState<'native' | 'frame' | null>(null);
  const [frameUrl, setFrameUrl] = useState('');
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [message, setMessage] = useState('');
  const [address, setAddress] = useState('');
  const [typing, setTyping] = useState<string | null>(null);
  const [history, setHistory] = useState({ back: false, forward: false });
  const activeRef = useRef(active);
  activeRef.current = active;
  const failed = useRef(false);

  const setStatus = (s: SessionStatus, msg = '') => {
    failed.current = s === 'error';
    setStatusState(s);
    setMessage(msg);
    onStatus(tabId, s);
  };

  const rect = useCallback((): Rect | null => {
    const el = boxRef.current;
    if (!el) return null;
    const r = el.getBoundingClientRect();
    const d = window.devicePixelRatio || 1;
    if (r.width < 10 || r.height < 10) return null;
    return { x: Math.round(r.left * d), y: Math.round(r.top * d), w: Math.round(r.width * d), h: Math.round(r.height * d) };
  }, []);

  const pushLayout = useCallback(() => {
    const r = rect();
    const visible = activeRef.current && !failed.current;
    void api.webLayout(tabId, r ?? { x: 0, y: 0, w: 1, h: 1 }, visible && r !== null).catch(() => undefined);
  }, [rect, tabId]);

  // Open / reopen
  useEffect(() => {
    let cancelled = false;
    setStatus('connecting');
    setMode(null);
    setFrameUrl('');
    setAddress('');
    setTyping(null);

    const off = onEvent((e) => {
      if (cancelled || e.event !== 'web.status' || e.sessionId !== tabId) return;
      if (e.url) setAddress(e.url);
      setHistory({ back: e.canGoBack, forward: e.canGoForward });
      if (e.state === 'connected' || e.state === 'connecting') setStatus(e.state);
      else if (e.state === 'error') setStatus('error', e.message);
    });

    const raf = requestAnimationFrame(() => {
      api
        .webOpen(server.id, tabId, rect() ?? { x: 0, y: 0, w: 1024, h: 768 })
        .then(({ native, url, correction }) => {
          if (correction) onCorrected(server, correction);
          if (cancelled) return;
          setMode(native ? 'native' : 'frame');
          if (native) setAddress(url);
          else setFrameUrl(url);
        })
        .catch((e: Error) => {
          if (!cancelled) setStatus('error', e.message);
        });
    });

    return () => {
      cancelled = true;
      cancelAnimationFrame(raf);
      off();
      void api.webClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [server.id, tabId, reconnectKey]);

  // Keep position, size and visibility of the native browser up to date
  useEffect(() => {
    if (mode !== 'native') return;
    pushLayout();
    const ro = new ResizeObserver(() => pushLayout());
    if (boxRef.current) ro.observe(boxRef.current);
    window.addEventListener('resize', pushLayout);
    return () => {
      ro.disconnect();
      window.removeEventListener('resize', pushLayout);
    };
  }, [mode, pushLayout, active, status]);

  const command = (c: 'back' | 'forward' | 'reload' | 'home' | 'go' | 'external', url = '') => void api.webCommand(tabId, c, url).catch(() => undefined);

  const go = (e: FormEvent) => {
    e.preventDefault();
    if (typing !== null) {
      setStatus('connecting');
      command('go', typing);
    }
    setTyping(null);
    (document.activeElement as HTMLElement | null)?.blur();
  };

  const reload = () => {
    if (mode === 'native') {
      setStatus('connecting');
      command('reload');
    } else if (frameRef.current && frameUrl) frameRef.current.src = frameUrl;
  };

  return (
    <SessionFrame
      server={server}
      status={status}
      variant="bar"
      extraActions={
        <button title="Open in the system browser" onClick={() => (mode === 'native' ? command('external') : void api.webOpenExternal(tabId).catch(() => undefined))}>
          <IconGlobe size={15} />
        </button>
      }
      onReconnect={onReconnect}
      onDuplicate={onDuplicate}
      onEdit={onEdit}
    >
      <div className="web-view">
        {mode === 'native' && (
          <div className="web-nav">
            <button title="Back" disabled={!history.back} onClick={() => command('back')}>
              <IconArrowLeft size={15} />
            </button>
            <button title="Forward" disabled={!history.forward} onClick={() => command('forward')}>
              <IconArrowRight size={15} />
            </button>
            <button title="Reload" onClick={reload}>
              <IconRefresh size={15} />
            </button>
            <button
              title="Start page of this host"
              onClick={() => {
                setStatus('connecting');
                command('home');
              }}
            >
              <IconGlobe size={15} />
            </button>
            <form onSubmit={go}>
              <input
                aria-label="Address"
                spellCheck={false}
                value={typing ?? address}
                onFocus={(e) => e.target.select()}
                onChange={(e) => setTyping(e.target.value)}
                onBlur={() => setTyping(null)}
              />
            </form>
          </div>
        )}
        <div className="web-box" ref={boxRef}>
          {mode === 'frame' && frameUrl && (
            <iframe
              ref={frameRef}
              key={frameUrl}
              className="web-frame"
              src={frameUrl}
              title={server.name}
              allow="clipboard-read; clipboard-write; fullscreen"
              onLoad={() => setStatus('connected')}
            />
          )}
          {status === 'error' && (
            <div className="web-error">
              <strong>Could not open the page</strong>
              <p>{message}</p>
            </div>
          )}
        </div>
      </div>
    </SessionFrame>
  );
}
