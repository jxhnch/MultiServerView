import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { askConfirm } from './Confirm';
import { IconClose, IconRefresh } from './Icons';
import type { Identity, Server, VmwareVmInfo } from './types';

const NETWORK: Record<string, string> = { nat: 'NAT', bridged: 'Bridged', hostonly: 'Host-only', custom: 'Custom network' };

/** Imports VMs from the local VMware Workstation library as hosts. */
export function VmwareImportDialog({
  servers,
  identities,
  workspaces,
  activeWorkspace,
  onClose,
  onDone,
  onRemoveImported,
}: {
  servers: Server[];
  identities: Identity[];
  workspaces: string[];
  activeWorkspace: string;
  onClose: () => void;
  onDone: (text: string) => Promise<void>;
  /** Deletes all hosts imported from VMware. Returns the number. */
  onRemoveImported: () => Promise<number>;
}) {
  const [vms, setVms] = useState<VmwareVmInfo[] | null>(null);
  const [vmrun, setVmrun] = useState(true);
  const [folders, setFolders] = useState<string[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [filter, setFilter] = useState('');
  const [workspace, setWorkspace] = useState(activeWorkspace);
  const [identityId, setIdentityId] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const importedCount = servers.filter((s) => s.vmwareRef).length;

  const scan = useCallback(async (extra: string[]) => {
    setVms(null);
    setError('');
    try {
      // Query each extra folder separately and merge the results (the library and the default folder are always included).
      const results = await Promise.all((extra.length ? extra : ['']).map((f) => api.vmwareScan(f)));
      const merged = new Map<string, VmwareVmInfo>();
      for (const r of results) for (const v of r.vms) merged.set(v.vmx.toLowerCase(), v);
      const list = [...merged.values()].sort((a, b) => a.name.localeCompare(b.name));
      setVmrun(results.every((r) => r.vmrun));
      setVms(list);
      setSelected((prev) => (prev.size > 0 ? new Set([...prev].filter((p) => merged.has(p.toLowerCase()))) : new Set(list.filter((v) => !v.imported).map((v) => v.vmx))));
    } catch (e) {
      setError((e as Error).message);
      setVms([]);
    }
  }, []);
  useEffect(() => void scan(folders), [scan, folders]);

  async function addFolder() {
    try {
      const r = await api.pickFolder('Choose a folder with VMware VMs (all .vmx files inside are found)');
      if (r.path && !folders.includes(r.path)) setFolders([...folders, r.path]);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  const visible = useMemo(() => {
    const f = filter.trim().toLowerCase();
    return (vms ?? []).filter((v) => !f || `${v.name} ${v.guestOs} ${v.ip ?? ''}`.toLowerCase().includes(f));
  }, [vms, filter]);
  const selectable = visible.filter((v) => !v.imported);
  const allOn = selectable.length > 0 && selectable.every((v) => selected.has(v.vmx));
  const newCount = vms?.filter((v) => !v.imported).length ?? 0;
  const withoutIp = vms?.filter((v) => selected.has(v.vmx) && !v.ip && !v.running).length ?? 0;

  function toggleAll() {
    setSelected((prev) => {
      const next = new Set(prev);
      for (const v of selectable) allOn ? next.delete(v.vmx) : next.add(v.vmx);
      return next;
    });
  }

  async function run(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      const r = await api.vmwareImport({ vms: [...selected], workspace: workspace.trim(), identityId });
      const parts = [`${r.imported} host${r.imported === 1 ? '' : 's'} imported`];
      if (r.noIp) parts.push(`${r.noIp} without a known address (VM never started) – add the address in the host`);
      await onDone(parts.join(', ') + '.');
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  async function removeImported() {
    if (
      !(await askConfirm(`Delete the ${importedCount} host${importedCount === 1 ? '' : 's'} imported from VMware?\nThe VMs themselves are untouched. Hosts you created by hand stay.`, {
        title: 'Delete imported hosts',
        confirmLabel: 'Delete',
        danger: true,
      }))
    )
      return;
    try {
      const n = await onRemoveImported();
      await onDone(`${n} host${n === 1 ? '' : 's'} deleted.`);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal wide" onMouseDown={(e) => e.stopPropagation()} onSubmit={run} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Import VMware Workstation</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <div className="row center-v">
          <input className="grow" value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Filter by name or operating system …" />
          <button type="button" onClick={() => void addFolder()} title="Search an additional folder for VMs">
            Add folder…
          </button>
          <button type="button" className="ghost icon-only" title="Rescan" onClick={() => void scan(folders)}>
            <IconRefresh size={16} />
          </button>
        </div>
        {folders.length > 0 && <div className="muted small">Also searched: {folders.join(' · ')}</div>}
        {!vmrun && (
          <div className="muted small">
            VMware Workstation was not found. Addresses of running VMs are then only known from the DHCP entries of VMware.
          </div>
        )}

        {vms === null && <div className="muted">Reading VMware library…</div>}
        {vms !== null && vms.length === 0 && !error && (
          <div className="muted">No VMs found. Use "Add folder…" to choose a folder that contains your .vmx files.</div>
        )}
        {vms !== null && vms.length > 0 && (
          <>
            <label className="toggle-row">
              <span>
                Select all new <span className="muted small">({newCount} new, {vms.length - newCount} already imported)</span>
              </span>
              <input type="checkbox" checked={allOn} onChange={toggleAll} />
            </label>
            <div className="list pve-list">
              {visible.map((v) => (
                <label className={'list-row' + (v.imported ? ' dim' : '')} key={v.vmx} style={{ flexDirection: 'row' }}>
                  <input
                    type="checkbox"
                    disabled={v.imported}
                    checked={selected.has(v.vmx)}
                    onChange={(e) =>
                      setSelected((prev) => {
                        const next = new Set(prev);
                        e.target.checked ? next.add(v.vmx) : next.delete(v.vmx);
                        return next;
                      })
                    }
                  />
                  <span className={'status-dot ' + (v.running ? 'connected' : '')} title={v.running ? 'running' : 'off'} />
                  <div className="grow">
                    <div className="list-title">{v.name}</div>
                    <div className="muted small">
                      {v.guestOs || 'Unknown operating system'} · {v.protocol.toUpperCase()} · {v.ip ?? 'Address not known yet'}
                    </div>
                  </div>
                  {v.connection && <span className="chip">{NETWORK[v.connection] ?? v.connection}</span>}
                  {v.imported && <span className="chip">already imported</span>}
                </label>
              ))}
            </div>

            <div className="group-card">
              <h3>Organize</h3>
              <label>
                Into workspace
                <input list="vmw-workspaces" value={workspace} onChange={(e) => setWorkspace(e.target.value)} placeholder="empty = no workspace" />
              </label>
              <datalist id="vmw-workspaces">
                {workspaces.map((w) => (
                  <option key={w} value={w} />
                ))}
              </datalist>
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
              {withoutIp > 0 && (
                <div className="muted small">
                  {withoutIp} selected VM{withoutIp === 1 ? ' has' : 's have'} never been started, so its address is unknown. Start it once and import again, or enter the address
                  in the host later.
                </div>
              )}
            </div>
          </>
        )}

        {error && <div className="error">{error}</div>}
        <div className="actions">
          {importedCount > 0 && (
            <button type="button" className="ghost danger" onClick={() => void removeImported()}>
              Delete imported ({importedCount})
            </button>
          )}
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
