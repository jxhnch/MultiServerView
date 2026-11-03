import { useCallback, useEffect, useState } from 'react';
import { api } from './bridge';
import { IconClock } from './Icons';
import type { HistoryEntry } from './types';
import { askConfirm } from './Confirm';

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
    if (!(await askConfirm('Delete the whole history?', { title: 'Delete history', confirmLabel: 'Delete', danger: true }))) return;
    await api.historyClear();
    await load();
  }

  return (
    <div className="page" style={{ maxWidth: 1000 }}>
      <div className="page-head">
        <div>
          <h1>Logs</h1>
          <p className="muted">When which host was connected and whether it worked. Stays on this PC only – without passwords and without terminal content.</p>
        </div>
        <div className="head-actions">
          <button onClick={() => void load()}>Refresh</button>
          <button className="danger" disabled={items.length === 0} onClick={() => void clear()}>
            Delete history
          </button>
        </div>
      </div>
      {items.length === 0 ? (
        <div className="empty-card">
          <IconClock size={28} />
          <h3>No history yet</h3>
        </div>
      ) : (
        <div className="log-table">
          <div className="log-head">
            <span>Time</span>
            <span>Host</span>
            <span>Type</span>
            <span>Result</span>
            <span>Duration</span>
            <span>Note</span>
          </div>
          {items.map((e) => (
            <div className="log-row" key={e.id}>
              <span className="muted">{new Date(e.startedAt).toLocaleString('en-GB', { dateStyle: 'short', timeStyle: 'medium' })}</span>
              <span className="ellipsis" title={e.host}>
                {e.serverName} <span className="muted small">{e.host}</span>
              </span>
              <span>
                <span className="chip">{KIND[e.kind]}</span>
              </span>
              <span>
                <span className={'pill ' + (e.result === 'ok' ? 'connected' : e.result === 'error' ? 'error' : 'connecting')}>
                  <span className="dot-sm" />
                  {e.result === 'ok' ? 'OK' : e.result === 'error' ? 'Error' : 'Running'}
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
