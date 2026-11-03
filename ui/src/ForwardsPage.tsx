import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api, onEvent } from './bridge';
import { IconClose, IconEdit, IconForward, IconPlay, IconPlus, IconStop, IconTrash } from './Icons';
import type { ForwardItem, ForwardRule, ForwardType, Server } from './types';

const TYPE_LABEL: Record<ForwardType, string> = { local: 'Lokal', remote: 'Remote', dynamic: 'SOCKS' };
const TYPE_HELP: Record<ForwardType, string> = {
  local: 'Ein Port auf diesem PC führt zu einem Ziel hinter dem SSH-Server (z. B. eine Datenbank). Entspricht ssh -L.',
  remote: 'Ein Port auf dem SSH-Server führt zu einem Ziel auf diesem PC. Entspricht ssh -R.',
  dynamic: 'Ein SOCKS5-Proxy auf diesem PC, der den Verkehr über den SSH-Server schickt. Entspricht ssh -D.',
};

const describe = (r: ForwardRule) =>
  r.type === 'dynamic'
    ? `SOCKS5 auf ${r.bindHost}:${r.bindPort}`
    : r.type === 'local'
      ? `${r.bindHost}:${r.bindPort}  →  ${r.destHost}:${r.destPort}`
      : `Server:${r.bindPort}  →  ${r.destHost}:${r.destPort}`;

export function ForwardsPage({ servers, toast }: { servers: Server[]; toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [items, setItems] = useState<ForwardItem[]>([]);
  const [editing, setEditing] = useState<ForwardRule | null>(null);
  const [busy, setBusy] = useState<string>('');
  const ssh = servers.filter((s) => s.protocol === 'ssh');
  const nameOf = (id: string) => servers.find((s) => s.id === id)?.name ?? '(Host gelöscht)';

  const load = useCallback(() => api.forwardsList().then(setItems).catch((e: Error) => toast(e.message, 'error')), [toast]);
  useEffect(() => void load(), [load]);

  // Laufzustand live nachführen (auch wenn die Verbindung von selbst abbricht)
  useEffect(
    () =>
      onEvent((e) => {
        if (e.event !== 'forward.status') return;
        setItems((prev) => prev.map((i) => (i.rule.id === e.ruleId ? { ...i, running: e.running } : i)));
        if (!e.running && e.error) toast(`Weiterleitung beendet: ${e.error}`, 'error');
      }),
    [toast],
  );

  async function toggle(item: ForwardItem) {
    setBusy(item.rule.id);
    try {
      if (item.running) await api.forwardsStop(item.rule.id);
      else await api.forwardsStart(item.rule.id);
      await load();
    } catch (e) {
      toast((e as Error).message, 'error');
    } finally {
      setBusy('');
    }
  }

  async function remove(item: ForwardItem) {
    if (!confirm(`Regel «${item.rule.name}» löschen?`)) return;
    try {
      await api.forwardsDelete(item.rule.id);
      await load();
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  const blank: ForwardRule = { id: '', name: '', serverId: ssh[0]?.id ?? '', type: 'local', bindHost: '127.0.0.1', bindPort: 8080, destHost: '127.0.0.1', destPort: 80 };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Port Forwarding</h1>
          <p className="muted">Tunnel über deine SSH-Hosts. Regeln laufen unabhängig von Terminal-Tabs, bis du sie stoppst oder den Vault sperrst.</p>
        </div>
        <button className="primary" disabled={ssh.length === 0} title={ssh.length === 0 ? 'Zuerst einen SSH-Host anlegen' : ''} onClick={() => setEditing(blank)}>
          <IconPlus size={15} /> Neue Regel
        </button>
      </div>

      {items.length === 0 ? (
        <div className="empty-card">
          <IconForward size={28} />
          <h3>Noch keine Regeln</h3>
          <p>Zum Beispiel eine Datenbank, die nur im Server-Netz erreichbar ist, lokal auf Port 5433 verfügbar machen.</p>
        </div>
      ) : (
        <div className="list">
          {items.map((it) => (
            <div className="list-row" key={it.rule.id}>
              <span className={'fw-type ' + it.rule.type}>{TYPE_LABEL[it.rule.type]}</span>
              <div className="grow">
                <div className="list-title">{it.rule.name}</div>
                <div className="muted small">
                  <span className="mono">{describe(it.rule)}</span> · über {nameOf(it.rule.serverId)}
                </div>
              </div>
              <span className={'pill ' + (it.running ? 'connected' : '')}>
                <span className="dot-sm" />
                {it.running ? 'Aktiv' : 'Gestoppt'}
              </span>
              <button className={it.running ? '' : 'primary'} disabled={busy === it.rule.id} onClick={() => void toggle(it)}>
                {it.running ? (
                  <>
                    <IconStop size={14} /> Stopp
                  </>
                ) : (
                  <>
                    <IconPlay size={14} /> Start
                  </>
                )}
              </button>
              <button className="ghost icon-only" title="Bearbeiten" onClick={() => setEditing(it.rule)}>
                <IconEdit size={16} />
              </button>
              <button className="ghost icon-only danger" title="Löschen" onClick={() => void remove(it)}>
                <IconTrash size={16} />
              </button>
            </div>
          ))}
        </div>
      )}

      {editing && (
        <ForwardForm
          initial={editing}
          hosts={ssh}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await load();
            toast('Regel gespeichert');
          }}
        />
      )}
    </div>
  );
}

function ForwardForm({ initial, hosts, onClose, onSaved }: { initial: ForwardRule; hosts: Server[]; onClose: () => void; onSaved: () => Promise<void> }) {
  const [r, setR] = useState(initial);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const set = <K extends keyof ForwardRule>(k: K, v: ForwardRule[K]) => setR((p) => ({ ...p, [k]: v }));

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await api.forwardsSave({ ...r, id: r.id || undefined, name: r.name.trim(), destHost: r.destHost.trim(), bindHost: r.bindHost.trim() || '127.0.0.1' });
      await onSaved();
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>{initial.id ? 'Regel bearbeiten' : 'Neue Regel'}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <div className="seg">
          {(['local', 'remote', 'dynamic'] as ForwardType[]).map((t) => (
            <button type="button" key={t} className={r.type === t ? 'on' : ''} onClick={() => set('type', t)}>
              {TYPE_LABEL[t]}
            </button>
          ))}
        </div>
        <p className="hint">{TYPE_HELP[r.type]}</p>

        <label>
          Über SSH-Host
          <select value={r.serverId} onChange={(e) => set('serverId', e.target.value)}>
            {hosts.map((h) => (
              <option key={h.id} value={h.id}>
                {h.name} ({h.host})
              </option>
            ))}
          </select>
        </label>
        <label>
          Name (optional)
          <input value={r.name} onChange={(e) => set('name', e.target.value)} placeholder="z. B. Postgres Produktion" />
        </label>

        <div className="row">
          {r.type !== 'remote' && (
            <label className="grow">
              Lokale Adresse
              <input value={r.bindHost} onChange={(e) => set('bindHost', e.target.value)} placeholder="127.0.0.1" />
            </label>
          )}
          <label style={{ width: 120 }}>
            {r.type === 'remote' ? 'Port auf dem Server' : 'Lokaler Port'}
            <input type="number" value={r.bindPort} onChange={(e) => set('bindPort', Number(e.target.value))} />
          </label>
        </div>

        {r.type !== 'dynamic' && (
          <div className="row">
            <label className="grow">
              {r.type === 'local' ? 'Ziel-Adresse (vom Server aus gesehen)' : 'Ziel-Adresse (auf diesem PC)'}
              <input value={r.destHost} onChange={(e) => set('destHost', e.target.value)} placeholder="127.0.0.1 oder db.intern" />
            </label>
            <label style={{ width: 120 }}>
              Ziel-Port
              <input type="number" value={r.destPort} onChange={(e) => set('destPort', Number(e.target.value))} />
            </label>
          </div>
        )}

        {r.bindHost && !['127.0.0.1', 'localhost', '::1'].includes(r.bindHost.trim()) && r.type !== 'remote' && (
          <p className="hint" style={{ color: 'var(--warn)' }}>
            Achtung: Mit «{r.bindHost}» kann auch jeder andere im Netzwerk diesen Tunnel benutzen. Für nur diesen PC: 127.0.0.1.
          </p>
        )}

        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Abbrechen
          </button>
          <button className="primary" disabled={busy || !r.serverId}>
            Speichern
          </button>
        </div>
      </form>
    </div>
  );
}
