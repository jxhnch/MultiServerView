import { useCallback, useEffect, useState } from 'react';
import { api } from './bridge';
import { IconShield, IconTrash } from './Icons';
import type { KnownHost } from './types';
import { askConfirm } from './Confirm';

export function KnownHostsPage({ toast }: { toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [items, setItems] = useState<KnownHost[]>([]);
  const load = useCallback(() => api.knownHostsList().then(setItems).catch((e: Error) => toast(e.message, 'error')), [toast]);
  useEffect(() => void load(), [load]);

  async function remove(k: KnownHost) {
    if (!(await askConfirm(`Forget the host key of ${k.id}? It will be stored again the next time you connect.`, { title: 'Forget host key', confirmLabel: 'Forget' }))) return;
    try {
      await api.knownHostsRemove(k.id);
      await load();
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  async function clearAll() {
    const sure = await askConfirm(
      `Forget all ${items.length} saved host keys and certificates?\nThe next connection to each server trusts whatever it shows then, so only do this in a network you trust.`,
      { title: 'Clear all known hosts', confirmLabel: 'Clear all', danger: true },
    );
    if (!sure) return;
    try {
      const { removed } = await api.knownHostsClear();
      await load();
      toast(`${removed} known host${removed === 1 ? '' : 's'} forgotten`);
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
            On the first connection the app remembers the fingerprint of every SSH server. If it changes later, the connection is
            refused – this protects against man-in-the-middle attacks. The list is stored together with the rest of the vault.
          </p>
        </div>
        {items.length > 0 && (
          <button className="ghost danger" onClick={() => void clearAll()}>
            <IconTrash size={15} /> Clear all
          </button>
        )}
      </div>
      {items.length === 0 ? (
        <div className="empty-card">
          <IconShield size={28} />
          <h3>No known hosts yet</h3>
          <p>They appear here after the first SSH or SFTP connection.</p>
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
              <button className="ghost icon-only danger" title="Forget" onClick={() => void remove(k)}>
                <IconTrash size={16} />
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
