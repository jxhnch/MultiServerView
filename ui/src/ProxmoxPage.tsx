import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { askConfirm } from './Confirm';
import { IconClose, IconCube, IconEdit, IconPlus, IconRefresh, IconTrash } from './Icons';
import type { Identity, ProxmoxCert, ProxmoxInfo, ProxmoxVmInfo, Server } from './types';

type Toast = (msg: string, kind?: 'ok' | 'error') => void;

/** Proxmox connections and the VM import from them. Part of the "Import" page. */
export function ProxmoxSection({
  servers,
  identities,
  workspaces,
  activeWorkspace,
  onImported,
  onRemoveImported,
  toast,
}: {
  servers: Server[];
  identities: Identity[];
  workspaces: string[];
  activeWorkspace: string;
  onImported: () => Promise<void>;
  /** Deletes all hosts imported from this connection. Returns the number. */
  onRemoveImported: (connectionId: string) => Promise<number>;
  toast: Toast;
}) {
  const [items, setItems] = useState<ProxmoxInfo[]>([]);
  const [editing, setEditing] = useState<ProxmoxInfo | 'new' | null>(null);
  const [importing, setImporting] = useState<ProxmoxInfo | null>(null);

  const load = useCallback(() => api.proxmoxList().then(setItems).catch((e: Error) => toast(e.message, 'error')), [toast]);
  useEffect(() => void load(), [load]);

  const importedCount = (p: ProxmoxInfo) => servers.filter((s) => s.proxmoxRef.startsWith(p.id + ':')).length;

  async function removeImported(p: ProxmoxInfo) {
    const n = importedCount(p);
    if (
      !(await askConfirm(
        `Delete the ${n} imported host${n === 1 ? '' : 's'} from "${p.name}"?\nHosts you created by hand stay. Changes to the imported hosts (e.g. saved passwords) will be lost.`,
        { title: 'Delete imported hosts', confirmLabel: 'Delete', danger: true },
      ))
    )
      return;
    try {
      const removed = await onRemoveImported(p.id);
      toast(`${removed} host${removed === 1 ? '' : 's'} deleted`);
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  async function remove(p: ProxmoxInfo) {
    if (!(await askConfirm(`Delete the connection "${p.name}"?\nThe imported hosts stay.`, { title: 'Delete Proxmox connection', confirmLabel: 'Delete', danger: true }))) return;
    try {
      await api.proxmoxDelete(p.id);
      await load();
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <section className="import-section">
      <div className="sec-head">
        <div>
          <div className="sec-label">Proxmox</div>
          <p className="muted small">
            Connect your Proxmox server and add VMs and containers as hosts with one click. The app only reads (listing and addresses), it changes nothing on the server.
          </p>
        </div>
        <button className="primary" onClick={() => setEditing('new')}>
          <IconPlus size={15} /> Connect Proxmox
        </button>
      </div>

      {items.length === 0 ? (
        <div className="empty-card">
          <IconCube size={28} />
          <h3>No Proxmox connection yet</h3>
          <p className="muted">
            Create an API token in Proxmox: <b>Datacenter → Permissions → API Tokens → Add</b>. Give the token the role <b>PVEAuditor</b> (Datacenter → Permissions →
            Add → API Token Permission). You only see the secret once, so enter it here.
          </p>
          <button className="primary" onClick={() => setEditing('new')}>
            Connect Proxmox
          </button>
        </div>
      ) : (
        <div className="list">
          {items.map((p) => (
            <div className="list-row" key={p.id}>
              <span className="list-ico">
                <IconCube size={18} />
              </span>
              <div className="grow">
                <div className="list-title">{p.name}</div>
                <div className="muted small">
                  {p.url} · {p.tokenId} · {p.certFingerprint ? 'Certificate pinned' : 'Certificate trusted'}
                  {importedCount(p) > 0 ? ` · ${importedCount(p)} imported` : ''}
                </div>
              </div>
              <button className="primary" onClick={() => setImporting(p)}>
                Import VMs
              </button>
              {importedCount(p) > 0 && (
                <button className="ghost danger" title="Delete all hosts imported from this connection" onClick={() => void removeImported(p)}>
                  Delete imported
                </button>
              )}
              <button className="ghost icon-only" title="Edit" onClick={() => setEditing(p)}>
                <IconEdit size={16} />
              </button>
              <button className="ghost icon-only danger" title="Delete" onClick={() => void remove(p)}>
                <IconTrash size={16} />
              </button>
            </div>
          ))}
        </div>
      )}

      {editing && (
        <ConnectDialog
          initial={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onSaved={async (saved) => {
            setEditing(null);
            await load();
            toast('Proxmox connection saved');
            if (editing === 'new') setImporting(saved);
          }}
        />
      )}
      {importing && (
        <ImportDialog
          connection={importing}
          identities={identities}
          workspaces={workspaces}
          activeWorkspace={activeWorkspace}
          onClose={() => setImporting(null)}
          onDone={async (text) => {
            setImporting(null);
            await onImported();
            toast(text);
          }}
        />
      )}
    </section>
  );
}

// ---------------------------------------------------------------------------------------------------- Create / edit a connection

type Phase = 'form' | 'cert' | 'ok';

function ConnectDialog({ initial, onClose, onSaved }: { initial: ProxmoxInfo | null; onClose: () => void; onSaved: (saved: ProxmoxInfo) => Promise<void> }) {
  const [name, setName] = useState(initial?.name ?? '');
  const [url, setUrl] = useState(initial?.url ?? '');
  const [tokenId, setTokenId] = useState(initial?.tokenId ?? '');
  const [secret, setSecret] = useState('');
  const [pin, setPin] = useState(initial?.certFingerprint ?? ''); // pinned fingerprint ('' = the certificate is trusted system-wide)
  const [cert, setCert] = useState<ProxmoxCert | null>(null);
  const [phase, setPhase] = useState<Phase>('form');
  const [result, setResult] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const filled = url.trim() && tokenId.trim() && (secret || initial?.hasSecret);
  const input = (extra: object = {}) => ({ id: initial?.id, name: name.trim(), url: url.trim(), tokenId: tokenId.trim(), tokenSecret: secret, ...extra });

  async function check(fingerprint: string) {
    const r = await api.proxmoxTest(input({ certFingerprint: fingerprint }));
    setPin(fingerprint);
    setResult(`Connected · Proxmox VE ${r.version} · ${r.vms} VM${r.vms === 1 ? '' : 's'} and containers visible`);
    setPhase('ok');
  }

  async function next(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      if (phase === 'ok') {
        const saved = await api.proxmoxSave(input({ certFingerprint: pin }));
        await onSaved(saved);
        return;
      }
      if (phase === 'cert' && cert) {
        await check(cert.fingerprint);
        return;
      }
      // phase 'form': look at the certificate first
      const c = await api.proxmoxProbe(url.trim());
      setCert(c);
      if (c.trusted) {
        await check(''); // normal, trusted certificate: nothing to pin
      } else if (initial?.certFingerprint && initial.certFingerprint === c.fingerprint) {
        await check(c.fingerprint); // same certificate as in the saved connection
      } else {
        setPhase('cert');
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const changed = !!initial?.certFingerprint && !!cert && !cert.trusted && initial.certFingerprint !== cert.fingerprint;

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal wide" onMouseDown={(e) => e.stopPropagation()} onSubmit={next} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>{initial ? 'Edit connection' : 'Connect Proxmox'}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <label>
          Name (optional)
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. School or Homelab" disabled={phase !== 'form'} />
        </label>
        <label>
          Address
          <input autoFocus value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://pve.example.com:8006" disabled={phase !== 'form'} />
        </label>
        <label>
          Token-ID
          <input className="mono" value={tokenId} onChange={(e) => setTokenId(e.target.value)} placeholder="user@realm!tokenname, e.g. root@pam!msv" disabled={phase !== 'form'} />
        </label>
        <label>
          Token-Secret
          <input
            type="password"
            value={secret}
            onChange={(e) => setSecret(e.target.value)}
            placeholder={initial?.hasSecret ? '•••••••• (unchanged)' : 'shown only once in Proxmox'}
            disabled={phase !== 'form'}
          />
        </label>

        {phase === 'cert' && cert && (
          <div className="notice">
            <b>{changed ? 'The certificate has changed!' : 'The certificate of this server is not trusted yet.'}</b>
            <div className="muted small" style={{ marginTop: 4 }}>
              This is normal for Proxmox (self-signed). Compare the fingerprint with Proxmox: <i>Node → System → Certificates</i>. If it matches, the app remembers it and
              warns you in the future if it changes.
            </div>
            <div className="mono small" style={{ marginTop: 6, wordBreak: 'break-all' }}>
              SHA256 {cert.fingerprint}
            </div>
            {changed && <div className="error" style={{ marginTop: 6 }}>You confirmed a different fingerprint before. Only trust it if you renewed the certificate yourself.</div>}
          </div>
        )}
        {phase === 'ok' && <div className="notice ok">{result}</div>}
        {error && <div className="error">{error}</div>}

        <div className="actions">
          {phase !== 'form' && (
            <button type="button" onClick={() => setPhase('form')}>
              Back
            </button>
          )}
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={busy || !filled}>
            {busy ? 'Checking…' : phase === 'ok' ? 'Save' : phase === 'cert' ? 'Trust certificate' : 'Check connection'}
          </button>
        </div>
      </form>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------------- Import

const key = (v: ProxmoxVmInfo) => `${v.node}:${v.vmid}`;

function ImportDialog({
  connection,
  identities,
  workspaces,
  activeWorkspace,
  onClose,
  onDone,
}: {
  connection: ProxmoxInfo;
  identities: Identity[];
  workspaces: string[];
  activeWorkspace: string;
  onClose: () => void;
  onDone: (text: string) => Promise<void>;
}) {
  const [vms, setVms] = useState<ProxmoxVmInfo[] | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState('');
  // How the VMs are sorted: 'pool' = every pool becomes a workspace, 'fixed' = everything into one workspace.
  const [wsMode, setWsMode] = useState<'pool' | 'fixed'>('pool');
  const [workspace, setWorkspace] = useState(activeWorkspace);
  const [groupBy, setGroupBy] = useState<'none' | 'pool' | 'node'>('none');
  const [identityId, setIdentityId] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    setVms(null);
    setError('');
    try {
      const list = (await api.proxmoxVms(connection.id)).filter((v) => !v.template);
      setVms(list);
      setSelected(new Set(list.filter((v) => !v.imported).map(key))); // preselect everything new
      const pools = list.some((v) => v.pool);
      setWsMode(pools ? 'pool' : 'fixed');
      setGroupBy('none');
    } catch (e) {
      setError((e as Error).message);
      setVms([]);
    }
  }, [connection.id]);
  useEffect(() => void load(), [load]);

  const hasPools = !!vms?.some((v) => v.pool);
  const visible = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (vms ?? []).filter((v) => !f || `${v.name} ${v.pool} ${v.node} ${v.vmid} ${v.tags.join(' ')}`.toLowerCase().includes(f));
  }, [vms, filter]);
  const selectable = visible.filter((v) => !v.imported);
  const allOn = selectable.length > 0 && selectable.every((v) => selected.has(key(v)));
  const newCount = vms?.filter((v) => !v.imported).length ?? 0;

  function toggleAll() {
    setSelected((prev) => {
      const next = new Set(prev);
      for (const v of selectable) allOn ? next.delete(key(v)) : next.add(key(v));
      return next;
    });
  }

  async function run(e: FormEvent) {
    e.preventDefault();
    if (!vms) return;
    setBusy(true);
    setError('');
    try {
      const chosen = vms.filter((v) => selected.has(key(v))).map((v) => ({ node: v.node, vmid: v.vmid }));
      const r = await api.proxmoxImport({ id: connection.id, vms: chosen, workspace: workspace.trim(), workspaceFromPool: wsMode === 'pool' && hasPools, identityId, groupBy: wsMode === 'pool' && groupBy === 'pool' ? 'none' : groupBy });
      const parts = [`${r.imported} host${r.imported === 1 ? '' : 's'} imported`];
      if (r.noIp) parts.push(`${r.noIp} without an IP address (VM off or no guest agent) – add the address in the host`);
      await onDone(parts.join(', ') + '.');
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal wide" onMouseDown={(e) => e.stopPropagation()} onSubmit={run} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Import VMs · {connection.name}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <div className="row center-v">
          <input className="grow" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter by name, pool, node, tag …" />
          <button type="button" className="ghost icon-only" title="Reload" onClick={() => void load()}>
            <IconRefresh size={16} />
          </button>
        </div>

        {vms === null && <div className="muted">Loading VMs…</div>}
        {vms !== null && vms.length === 0 && !error && <div className="muted">The token sees no VMs. Is the PVEAuditor role missing?</div>}
        {vms !== null && vms.length > 0 && (
          <>
            <div className="row center-v">
              <label className="toggle-row grow" style={{ flexDirection: 'row' }}>
                <span>
                  Select all new <span className="muted small">({newCount} new, {vms.length - newCount} already imported)</span>
                </span>
                <input type="checkbox" checked={allOn} onChange={toggleAll} />
              </label>
            </div>
            <div className="list pve-list">
              {visible.map((v) => (
                <label className={'list-row' + (v.imported ? ' dim' : '')} key={key(v)} style={{ flexDirection: 'row' }}>
                  <input type="checkbox" disabled={v.imported} checked={selected.has(key(v))} onChange={(e) => setSelected((prev) => {
                    const next = new Set(prev);
                    e.target.checked ? next.add(key(v)) : next.delete(key(v));
                    return next;
                  })} />
                  <span className={'status-dot ' + (v.status === 'running' ? 'connected' : '')} title={v.status === 'running' ? 'running' : 'off'} />
                  <div className="grow">
                    <div className="list-title">{v.name}</div>
                    <div className="muted small">
                      {v.type === 'lxc' ? 'Container' : 'VM'} {v.vmid} · {v.node}
                      {v.tags.length > 0 ? ' · ' + v.tags.join(', ') : ''}
                    </div>
                  </div>
                  {v.pool && <span className="chip">{v.pool}</span>}
                  {v.imported && <span className="chip">already imported</span>}
                </label>
              ))}
            </div>

            <div className="group-card">
              <h3>Organize</h3>
              {hasPools && (
                <label className="toggle-row">
                  <span>
                    Every pool becomes its own workspace
                    <span className="muted small"> (VMs without a pool: into the workspace below)</span>
                  </span>
                  <input
                    type="radio"
                    name="ws-mode"
                    checked={wsMode === 'pool'}
                    onChange={() => {
                      setWsMode('pool');
                      if (groupBy === 'pool') setGroupBy('none'); // the pool is then already the workspace
                    }}
                  />
                </label>
              )}
              <label className="toggle-row">
                <span>
                  Everything into one workspace
                  <span className="muted small">{hasPools ? ' (the pool becomes the group)' : ''}</span>
                </span>
                <input
                  type="radio"
                  name="ws-mode"
                  checked={wsMode === 'fixed' || !hasPools}
                  onChange={() => {
                    setWsMode('fixed');
                    if (hasPools && groupBy === 'none') setGroupBy('pool');
                  }}
                />
              </label>
              <label>
                {wsMode === 'pool' && hasPools ? 'Workspace for VMs without a pool' : 'Workspace name'}
                <input list="pve-workspaces" value={workspace} onChange={(e) => setWorkspace(e.target.value)} placeholder="empty = no workspace" />
              </label>
              <datalist id="pve-workspaces">
                {workspaces.map((w) => (
                  <option key={w} value={w} />
                ))}
              </datalist>
              <label>
                Group
                <select value={groupBy} onChange={(e) => setGroupBy(e.target.value as 'none' | 'pool' | 'node')}>
                  <option value="none">None</option>
                  <option value="pool" disabled={wsMode === 'pool' && hasPools}>
                    Proxmox pool
                  </option>
                  <option value="node">Proxmox node</option>
                </select>
              </label>
              <label>
                Credentials
                <select value={identityId} onChange={(e) => setIdentityId(e.target.value)}>
                  <option value="">None (enter later in the host)</option>
                  {identities.map((i) => (
                    <option key={i.id} value={i.id}>
                      {i.name} ({i.username})
                    </option>
                  ))}
                </select>
              </label>
            </div>
          </>
        )}

        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={busy || selected.size === 0}>
            {busy ? 'Importing…' : `Import ${selected.size}`}
          </button>
        </div>
      </form>
    </div>
  );
}
