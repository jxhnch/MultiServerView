import { useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconEdit, IconPlus, IconTrash, IconUser } from './Icons';
import type { Identity, KeyInfo, Server } from './types';

/** Wiederverwendbare Zugangsdaten (Benutzer + Passwort oder Key) für mehrere Hosts. */
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
    if (!confirm(`Identity «${i.name}» löschen?${used > 0 ? `\n${used} Host(s) benutzen sie und fallen auf ihre eigenen Zugangsdaten zurück.` : ''}`)) return;
    try {
      await api.identitiesDelete(i.id);
      await onChanged();
      toast('Identity gelöscht');
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
            Benutzer und Passwort (oder Key) einmal speichern und bei mehreren Hosts auswählen. Ändert sich das Passwort, änderst du es nur hier.
          </p>
        </div>
        <button className="primary" onClick={() => setEditing({ id: '', name: '', username: '', hasPassword: false, auth: 'password', keyId: '' })}>
          <IconPlus size={15} /> Identity
        </button>
      </div>

      {identities.length === 0 ? (
        <p className="muted">Noch keine Identities.</p>
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
                    {i.username || '(kein Benutzer)'} · {i.auth === 'key' ? 'SSH-Key' : i.hasPassword ? 'Passwort' : 'ohne Passwort'} · {used} Host{used === 1 ? '' : 's'}
                  </div>
                </div>
                <button className="ghost icon-only" title="Bearbeiten" onClick={() => setEditing(i)}>
                  <IconEdit size={16} />
                </button>
                <button className="ghost icon-only danger" title="Löschen" onClick={() => void remove(i)}>
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
            toast('Identity gespeichert');
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
      setError('Bitte einen Key auswählen.');
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
          <h2>{initial.id ? 'Identity bearbeiten' : 'Neue Identity'}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <label>
          Name
          <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="z. B. root auf allen Debian-Servern" />
        </label>
        <label>
          Benutzer
          <input value={username} onChange={(e) => setUsername(e.target.value)} placeholder="root" />
        </label>
        <div className="seg slim">
          <button type="button" className={auth === 'password' ? 'on' : ''} onClick={() => setAuth('password')}>
            Passwort
          </button>
          <button type="button" className={auth === 'key' ? 'on' : ''} onClick={() => setAuth('key')}>
            SSH-Key
          </button>
        </div>
        {auth === 'password' ? (
          <label>
            Passwort
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder={initial.hasPassword ? '•••••••• (unverändert)' : ''} />
          </label>
        ) : (
          <label>
            Key
            <select value={keyId} onChange={(e) => setKeyId(e.target.value)}>
              <option value="">Key wählen…</option>
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
            Abbrechen
          </button>
          <button className="primary" disabled={busy || !name.trim()}>
            Speichern
          </button>
        </div>
      </form>
    </div>
  );
}
