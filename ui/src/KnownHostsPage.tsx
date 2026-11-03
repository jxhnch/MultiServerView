import { useCallback, useEffect, useState } from 'react';
import { api } from './bridge';
import { IconShield, IconTrash } from './Icons';
import type { KnownHost } from './types';

export function KnownHostsPage({ toast }: { toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [items, setItems] = useState<KnownHost[]>([]);
  const load = useCallback(() => api.knownHostsList().then(setItems).catch((e: Error) => toast(e.message, 'error')), [toast]);
  useEffect(() => void load(), [load]);

  async function remove(k: KnownHost) {
    if (!confirm(`Host-Key von ${k.id} vergessen? Beim nächsten Verbinden wird er neu gespeichert.`)) return;
    try {
      await api.knownHostsRemove(k.id);
      await load();
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Known Hosts</h1>
          <p className="muted">
            Beim ersten Verbinden merkt sich die App den Fingerabdruck jedes SSH-Servers. Ändert er sich später, wird die Verbindung
            abgelehnt – das schützt vor Man-in-the-Middle-Angriffen. Die Liste wird mit der Cloud synchronisiert.
          </p>
        </div>
      </div>
      {items.length === 0 ? (
        <div className="empty-card">
          <IconShield size={28} />
          <h3>Noch keine bekannten Hosts</h3>
          <p>Sie erscheinen hier nach der ersten SSH- oder SFTP-Verbindung.</p>
        </div>
      ) : (
        <div className="list">
          {items.map((k) => (
            <div className="list-row" key={k.id}>
              <span className="list-ico">
                <IconShield size={18} />
              </span>
              <div className="grow" style={{ minWidth: 0 }}>
                <div className="list-title">{k.id}</div>
                <div className="muted small mono ellipsis selectable">{k.fingerprint}</div>
              </div>
              <button className="ghost icon-only danger" title="Vergessen" onClick={() => void remove(k)}>
                <IconTrash size={16} />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
