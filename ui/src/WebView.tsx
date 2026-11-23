import { useEffect, useRef, useState } from 'react';
import { api } from './bridge';
import { IconGlobe, IconRefresh } from './Icons';
import { SessionFrame } from './SessionFrame';
import type { Server, SessionStatus } from './types';

/**
 * Integrated browser: the page of a device (firewall, Proxmox, ...) in a tab. The page is loaded through a local proxy of the host
 * (certificate check, frame headers, cookies, saved login); this view only shows the frame.
 */
export function WebView({
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
  const frameRef = useRef<HTMLIFrameElement>(null);
  const [url, setUrl] = useState('');
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [message, setMessage] = useState('');

  const setStatus = (s: SessionStatus, msg = '') => {
    setStatusState(s);
    setMessage(msg);
    onStatus(tabId, s);
  };

  useEffect(() => {
    let cancelled = false;
    setStatus('connecting');
    setUrl('');
    api
      .webOpen(server.id, tabId)
      .then(({ url }) => {
        if (!cancelled) setUrl(url);
      })
      .catch((e: Error) => {
        if (!cancelled) setStatus('error', e.message);
      });
    return () => {
      cancelled = true;
      void api.webClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [server.id, tabId, reconnectKey]);

  const extra = (
    <>
      <button title="Reload the page" onClick={() => frameRef.current && url && (frameRef.current.src = url)}>
        <IconRefresh size={15} />
      </button>
      <button title="Open in the system browser" onClick={() => void api.webOpenExternal(tabId).catch(() => undefined)}>
        <IconGlobe size={15} />
      </button>
    </>
  );

  return (
    <SessionFrame server={server} status={status} variant="bar" extraActions={extra} onReconnect={onReconnect} onDuplicate={onDuplicate} onEdit={onEdit}>
      {url && (
        <iframe
          ref={frameRef}
          key={url}
          className="web-frame"
          src={url}
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
    </SessionFrame>
  );
}
