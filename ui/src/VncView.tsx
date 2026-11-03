import { useEffect, useRef, useState, type FormEvent } from 'react';
import RFB from '@novnc/novnc';
import { api } from './bridge';
import { SessionFrame } from './SessionFrame';
import type { Server, SessionStatus } from './types';

/** VNC-Session über noVNC. Die Bytes laufen über eine lokale Brücke im Host (Einmal-Token). */
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
      .vncOpen(server.id)
      .then(({ url, username, password }) => {
        if (cancelled || !boxRef.current) return;
        boxRef.current.replaceChildren();
        rfb = new RFB(boxRef.current, url, { credentials: { username, password }, shared: true });
        rfb.scaleViewport = true;
        rfb.resizeSession = false;
        rfb.background = '#0b0d12';
        rfbRef.current = rfb;

        rfb.addEventListener('connect', () => {
          setStatus('connected');
          if (activeRef.current) rfb?.focus();
        });
        rfb.addEventListener('disconnect', (e) => {
          const clean = (e as CustomEvent<{ clean: boolean }>).detail?.clean;
          setStatus(clean ? 'closed' : 'error', clean ? '' : 'Verbindung unterbrochen oder abgelehnt.');
        });
        rfb.addEventListener('credentialsrequired', () => setNeedsPassword(true));
        rfb.addEventListener('securityfailure', (e) => {
          const reason = (e as CustomEvent<{ reason?: string }>).detail?.reason;
          setStatus('error', 'Anmeldung fehlgeschlagen' + (reason ? `: ${reason}` : '.'));
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
        /* schon getrennt */
      }
      rfbRef.current = null;
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
      /* Zwischenablage nicht erlaubt */
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
          <button className="ghost" onClick={() => rfbRef.current?.sendCtrlAltDel()} disabled={status !== 'connected'}>
            Strg+Alt+Entf
          </button>
          <button className="ghost" onClick={sendClipboard} disabled={status !== 'connected'}>
            Zwischenablage senden
          </button>
        </>
      }
    >
      <div className="vnc-wrap">
        <div className="vnc-box" ref={boxRef} />
        {status === 'connecting' && !needsPassword && <div className="overlay-msg muted">Verbinde…</div>}
        {(status === 'error' || status === 'closed') && (
          <div className="overlay-msg">
            <div className={status === 'error' ? 'error' : 'muted'}>{message || 'Verbindung getrennt.'}</div>
            <button className="primary" onClick={onReconnect}>
              Neu verbinden
            </button>
          </div>
        )}
        {needsPassword && (
          <form className="overlay-msg" onSubmit={sendPassword}>
            <div>Dieser VNC-Server verlangt ein Passwort.</div>
            <input type="password" autoFocus placeholder="VNC-Passwort" value={pw} onChange={(e) => setPw(e.target.value)} />
            <button className="primary">Anmelden</button>
          </form>
        )}
      </div>
    </SessionFrame>
  );
}
