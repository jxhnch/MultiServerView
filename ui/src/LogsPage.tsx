import { useCallback, useEffect, useState } from 'react';
import { api } from './bridge';
import { IconClock } from './Icons';
import type { HistoryEntry } from './types';

const KIND: Record<HistoryEntry['kind'], string> = { ssh: 'SSH', sftp: 'SFTP', rdp: 'RDP', vnc: 'VNC', forward: 'Tunnel' };

function duration(e: HistoryEntry): string {
  if (!e.endedAt) return '';
  const s = Math.max(0, Math.round((Date.parse(e.endedAt) - Date.parse(e.startedAt)) / 1000));
  if (s < 60) return `${s} s`;
  if (s < 3600) return `${Math.floor(s / 60)} Min ${s % 60} s`;
  return `${Math.floor(s / 3600)} Std ${Math.floor((s % 3600) / 60)} Min`;
}

export function LogsPage({ toast }: { toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [items, setItems] = useState<HistoryEntry[]>([]);
  const load = useCallback(() => api.historyList().then(setItems).catch((e: Error) => toast(e.message, 'error')), [toast]);
  useEffect(() => void load(), [load]);

  async function clear() {
    if (!confirm('Den ganzen Verlauf löschen?')) return;
    await api.historyClear();
    await load();
  }

  return (
    <div className="page" style={{ maxWidth: 1000 }}>
      <div className="page-head">
        <div>
          <h1>Logs</h1>
          <p className="muted">Wann welcher Host verbunden war und ob es geklappt hat. Bleibt nur auf diesem PC – ohne Passwörter und ohne Terminal-Inhalte.</p>
        </div>
        <div className="head-actions">
          <button onClick={() => void load()}>Aktualisieren</button>
          <button className="danger" disabled={items.length === 0} onClick={() => void clear()}>
            Verlauf löschen
          </button>
        </div>
      </div>
      {items.length === 0 ? (
        <div className="empty-card">
          <IconClock size={28} />
          <h3>Noch kein Verlauf</h3>
        </div>
      ) : (
        <div className="log-table">
          <div className="log-head">
            <span>Zeit</span>
            <span>Host</span>
            <span>Art</span>
            <span>Ergebnis</span>
            <span>Dauer</span>
            <span>Hinweis</span>
          </div>
          {items.map((e) => (
            <div className="log-row" key={e.id}>
              <span className="muted">{new Date(e.startedAt).toLocaleString('de-CH', { dateStyle: 'short', timeStyle: 'medium' })}</span>
              <span className="ellipsis" title={e.host}>
                {e.serverName} <span className="muted small">{e.host}</span>
              </span>
              <span>
                <span className="chip">{KIND[e.kind]}</span>
              </span>
              <span>
                <span className={'pill ' + (e.result === 'ok' ? 'connected' : e.result === 'error' ? 'error' : 'connecting')}>
                  <span className="dot-sm" />
                  {e.result === 'ok' ? 'OK' : e.result === 'error' ? 'Fehler' : 'Läuft'}
                </span>
              </span>
              <span className="muted">{duration(e)}</span>
              <span className="muted small ellipsis" title={e.message}>
                {e.message}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
