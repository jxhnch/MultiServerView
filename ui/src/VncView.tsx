import { useEffect, useRef, useState, type FormEvent } from 'react';
import RFB from '@novnc/novnc';
import { api } from './bridge';
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

  const setStatus = (s: SessionStatus, msg = '') => {
    setStatusState(s);
    setMessage(msg);
    onStatus(tabId, s);
  };

  useEffect(() => {
    let cancelled = false;
    let rfb: RFB | null = null;
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
          setStatus('connected');
          if (activeRef.current) rfb?.focus();
        });
        rfb.addEventListener('disconnect', (e) => {
          const clean = (e as CustomEvent<{ clean: boolean }>).detail?.clean;
          setStatus(clean ? 'closed' : 'error', clean ? '' : 'Connection interrupted or refused.');
        });
        rfb.addEventListener('credentialsrequired', () => setNeedsPassword(true));
        rfb.addEventListener('securityfailure', (e) => {
          const reason = (e as CustomEvent<{ reason?: string }>).detail?.reason;
          setStatus('error', 'Authentication failed' + (reason ? `: ${reason}` : '.'));
        });
      })
      .catch((err: Error) => {
        if (!cancelled) setStatus('error', err.message);
      });

    return () => {
      cancelled = true;
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
