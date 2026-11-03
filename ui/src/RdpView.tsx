import { useCallback, useEffect, useRef, useState } from 'react';
import { api, isNative, onEvent, type Rect } from './bridge';
import { SessionFrame } from './SessionFrame';
import type { Server, SessionStatus } from './types';

/**
 * RDP session. The remote desktop is drawn by the native Windows RDP control, which the host places exactly
 * over the placeholder area (.rdp-box). That is why this element reports its position and
 * size (in real pixels) to the host, and whether it should be visible right now.
 */
export function RdpView({
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
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [message, setMessage] = useState('');
  const showNative = useRef(false);
  const activeRef = useRef(active);
  activeRef.current = active;

  const setStatus = useCallback(
    (s: SessionStatus, msg = '') => {
      setStatusState(s);
      setMessage(msg);
      onStatus(tabId, s);
    },
    [onStatus, tabId],
  );

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
    const visible = activeRef.current && showNative.current;
    if (r) void api.rdpLayout(tabId, r, visible).catch(() => undefined);
    else if (!visible) void api.rdpLayout(tabId, { x: 0, y: 0, w: 1, h: 1 }, false).catch(() => undefined);
  }, [rect, tabId]);

  // Open / reopen the session
  useEffect(() => {
    if (!isNative) {
      setStatus('error', 'RDP only works in the Windows app.');
      return;
    }
    let cancelled = false;
    setStatus('connecting');
    showNative.current = true;

    // Open only after the layout, so the control is created in the right size right away.
    const raf = requestAnimationFrame(() => {
      const r = rect() ?? { x: 0, y: 0, w: 1024, h: 768 };
      api.rdpOpen(tabId, server.id, r).catch((err: Error) => {
        if (cancelled) return;
        showNative.current = false;
        setStatus('error', err.message);
      });
    });

    const off = onEvent((e) => {
      if (e.event !== 'rdp.status' || e.sessionId !== tabId || cancelled) return;
      if (e.state === 'connected') setStatus('connected');
      else if (e.state === 'connecting') setStatus('connecting');
      else {
        // Hide the control so the message underneath becomes visible.
        showNative.current = false;
        setStatus('closed', e.message);
        pushLayout();
      }
    });

    return () => {
      cancelled = true;
      cancelAnimationFrame(raf);
      off();
      void api.rdpClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey]);

  // Keep position/size/visibility up to date
  useEffect(() => {
    if (!isNative) return;
    pushLayout();
    const ro = new ResizeObserver(() => pushLayout());
    if (boxRef.current) ro.observe(boxRef.current);
    window.addEventListener('resize', pushLayout);
    return () => {
      ro.disconnect();
      window.removeEventListener('resize', pushLayout);
    };
  }, [pushLayout, active, status]);

  return (
    <SessionFrame
      variant="bar"
      server={server}
      status={status}
      onReconnect={onReconnect}
      onDuplicate={onDuplicate}
      onEdit={onEdit}
      extraActions={
        isNative && (
          <button className="ghost" title="Opens the normal Windows RDP window (mstsc)" onClick={() => void api.rdpExternal(server.id)}>
            Open in Windows RDP
          </button>
        )
      }
    >
      <div className="rdp-box" ref={boxRef}>
        {status !== 'connected' && (
          <div className="overlay-msg">
            {status === 'connecting' && <div className="muted">Connecting to {server.host} …</div>}
            {(status === 'error' || status === 'closed') && (
              <>
                <div className={status === 'error' ? 'error' : 'muted'}>{message || 'Disconnected.'}</div>
                {isNative && (
                  <button className="primary" onClick={onReconnect}>
                    Reconnect
                  </button>
                )}
              </>
            )}
          </div>
        )}
      </div>
    </SessionFrame>
  );
}
