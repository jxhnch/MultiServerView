import { useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconEdit, IconPlus, IconTrash, IconUser } from './Icons';
import type { Identity, KeyInfo, Server } from './types';
import { askConfirm } from './Confirm';

/** Reusable credentials (username + password or key) for several hosts. */
export function IdentitiesSection({
  identities,
  keys,
  servers,
  onChanged,
  toast,
}: {
  identities: Identity[];
  keys: KeyInfo[];
  servers: Server[];
  onChanged: () => Promise<void>;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const [editing, setEditing] = useState<Identity | null>(null);

  async function remove(i: Identity) {
    const used = servers.filter((s) => s.identityId === i.id).length;
    if (!(await askConfirm(`Delete the identity "${i.name}"?${used > 0 ? `\n${used} host(s) use it and fall back to their own credentials.` : ''}`, { title: 'Delete identity', confirmLabel: 'Delete', danger: true }))) return;
    try {
      await api.identitiesDelete(i.id);
      await onChanged();
      toast('Identity deleted');
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <>
      <div className="page-head" style={{ marginTop: 18 }}>
        <div>
          <h1 style={{ fontSize: 18 }}>Identities</h1>
          <p className="muted">
            Save a username and password (or key) once and pick it for several hosts. When the password changes, you only change it here.
          </p>
        </div>
        <button className="primary" onClick={() => setEditing({ id: '', name: '', username: '', hasPassword: false, auth: 'password', keyId: '' })}>
          <IconPlus size={15} /> Identity
        </button>
      </div>

      {identities.length === 0 ? (
        <p className="muted">No identities yet.</p>
      ) : (
        <div className="list">
          {identities.map((i) => {
            const used = servers.filter((s) => s.identityId === i.id).length;
            return (
              <div className="list-row" key={i.id}>
                <span className="list-ico">
                  <IconUser size={18} />
                </span>
                <div className="grow">
                  <div className="list-title">{i.name}</div>
                  <div className="muted small">
                    {i.username || '(no username)'} · {i.auth === 'key' ? 'SSH key' : i.hasPassword ? 'password' : 'no password'} · {used} Host{used === 1 ? '' : 's'}
                  </div>
                </div>
                <button className="ghost icon-only" title="Edit" onClick={() => setEditing(i)}>
                  <IconEdit size={16} />
                </button>
                <button className="ghost icon-only danger" title="Delete" onClick={() => void remove(i)}>
                  <IconTrash size={16} />
                </button>
              </div>
            );
          })}
        </div>
      )}

      {editing && (
        <IdentityForm
          initial={editing}
          keys={keys}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await onChanged();
            toast('Identity saved');
          }}
        />
      )}
    </>
  );
}

function IdentityForm({ initial, keys, onClose, onSaved }: { initial: Identity; keys: KeyInfo[]; onClose: () => void; onSaved: () => Promise<void> }) {
  const [name, setName] = useState(initial.name);
  const [username, setUsername] = useState(initial.username);
  const [password, setPassword] = useState('');
  const [auth, setAuth] = useState(initial.auth);
  const [keyId, setKeyId] = useState(initial.keyId);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (auth === 'key' && !keyId) {
      setError('Please choose a key.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      await api.identitiesSave({ id: initial.id || undefined, name: name.trim(), username: username.trim(), password, auth, keyId: auth === 'key' ? keyId : '' });
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
          <h2>{initial.id ? 'Edit identity' : 'New identity'}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <label>
          Name
          <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. root on all Debian servers" />
        </label>
        <label>
          Username
          <input value={username} onChange={(e) => setUsername(e.target.value)} placeholder="root" />
        </label>
        <div className="seg slim">
          <button type="button" className={auth === 'password' ? 'on' : ''} onClick={() => setAuth('password')}>
            Password
          </button>
          <button type="button" className={auth === 'key' ? 'on' : ''} onClick={() => setAuth('key')}>
            SSH-Key
          </button>
        </div>
        {auth === 'password' ? (
          <label>
            Password
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder={initial.hasPassword ? '•••••••• (unchanged)' : ''} />
          </label>
        ) : (
          <label>
            Key
            <select value={keyId} onChange={(e) => setKeyId(e.target.value)}>
              <option value="">Choose key…</option>
              {keys.map((k) => (
                <option key={k.id} value={k.id}>
                  {k.name} ({k.kind})
                </option>
              ))}
            </select>
          </label>
        )}
        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={busy || !name.trim()}>
            Save
          </button>
        </div>
      </form>
    </div>
  );
}
