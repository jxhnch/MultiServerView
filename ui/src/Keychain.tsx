import { useRef, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconKey, IconLock, IconPlus, IconTrash, IconUpload } from './Icons';
import type { KeyInfo, Server } from './types';

export function Keychain({
  keys,
  servers,
  onChanged,
  toast,
}: {
  keys: KeyInfo[];
  servers: Server[];
  onChanged: () => Promise<void>;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const [adding, setAdding] = useState(false);

  async function remove(k: KeyInfo) {
    const used = servers.filter((s) => s.keyId === k.id).length;
    const warn = used > 0 ? `\n${used} Host(s) benutzen diesen Key und fallen auf Passwort zurück.` : '';
    if (!confirm(`Key «${k.name}» wirklich löschen?${warn}`)) return;
    try {
      await api.keysDelete(k.id);
      await onChanged();
      toast('Key gelöscht');
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Schlüssel</h1>
          <p className="muted">
            Private SSH-Keys liegen verschlüsselt im Vault und werden von mehreren Hosts gemeinsam benutzt.
            Nach dem Import verlässt der private Teil nie mehr den Host-Prozess.
          </p>
        </div>
        <button className="primary" onClick={() => setAdding(true)}>
          <IconPlus size={15} /> Key hinzufügen
        </button>
      </div>

      {keys.length === 0 ? (
        <div className="empty-card">
          <IconKey size={28} />
          <h3>Noch keine Schlüssel</h3>
          <p className="muted">Füge einen privaten Key im OpenSSH- oder PEM-Format hinzu.</p>
          <button onClick={() => setAdding(true)}>Key hinzufügen</button>
        </div>
      ) : (
        <div className="list">
          {keys.map((k) => {
            const used = servers.filter((s) => s.keyId === k.id).length;
            return (
              <div className="list-row" key={k.id}>
                <span className="list-ico">
                  <IconKey size={18} />
                </span>
                <div className="grow">
                  <div className="list-title">{k.name}</div>
                  <div className="muted small">
                    {k.kind || 'Key'} · {k.hasPassphrase ? 'mit Passphrase' : 'ohne Passphrase'} · {used} Host{used === 1 ? '' : 's'}
                  </div>
                </div>
                {k.hasPassphrase && (
                  <span className="muted" title="Passphrase gespeichert">
                    <IconLock size={15} />
                  </span>
                )}
                <button className="ghost icon-only danger" title="Löschen" onClick={() => remove(k)}>
                  <IconTrash size={16} />
                </button>
              </div>
            );
          })}
        </div>
      )}

      {adding && (
        <AddKey
          onClose={() => setAdding(false)}
          onSaved={async () => {
            setAdding(false);
            await onChanged();
            toast('Key gespeichert');
          }}
        />
      )}
    </div>
  );
}

function AddKey({ onClose, onSaved }: { onClose: () => void; onSaved: () => Promise<void> }) {
  const [name, setName] = useState('');
  const [privateKey, setPrivateKey] = useState('');
  const [passphrase, setPassphrase] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  async function pickFile(file: File | undefined) {
    if (!file) return;
    setPrivateKey(await file.text());
    if (!name) setName(file.name.replace(/\.(pem|key|txt)$/i, ''));
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await api.keysSave({ name: name.trim(), privateKey, passphrase });
      await onSaved();
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal wide" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Key hinzufügen</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <label>
          Name
          <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="z. B. Arbeit-Laptop" />
        </label>

        <label>
          Privater Schlüssel
          <textarea
            rows={7}
            spellCheck={false}
            className="mono"
            value={privateKey}
            onChange={(e) => setPrivateKey(e.target.value)}
            placeholder={'-----BEGIN OPENSSH PRIVATE KEY-----\n…'}
          />
        </label>
        <div>
          <input ref={fileRef} type="file" hidden onChange={(e) => pickFile(e.target.files?.[0])} />
          <button type="button" onClick={() => fileRef.current?.click()}>
            <IconUpload size={15} /> Datei wählen…
          </button>
        </div>

        <label>
          Passphrase (falls der Key eine hat)
          <input type="password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} />
        </label>

        <p className="muted small">
          Unterstützt: OpenSSH (ed25519, rsa, ecdsa) und PEM. PuTTY-Keys (.ppk) vorher in PuTTYgen über
          «Conversions → Export OpenSSH key» umwandeln.
        </p>

        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Abbrechen
          </button>
          <button className="primary" disabled={busy || !name.trim() || !privateKey.trim()}>
            Speichern
          </button>
        </div>
      </form>
    </div>
  );
}
