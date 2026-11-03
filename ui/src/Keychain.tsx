import { useRef, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconCopy, IconKey, IconLock, IconPlus, IconTrash, IconUpload } from './Icons';
import { IdentitiesSection } from './Identities';
import { GenerateKeyDialog, PublicKeyDialog } from './KeyDialogs';
import type { Identity, KeyInfo, Server } from './types';
import { askConfirm } from './Confirm';

export function Keychain({
  keys,
  identities,
  servers,
  onChanged,
  toast,
}: {
  keys: KeyInfo[];
  identities: Identity[];
  servers: Server[];
  onChanged: () => Promise<void>;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const [adding, setAdding] = useState(false);
  const [generating, setGenerating] = useState(false);
  const [showPublic, setShowPublic] = useState<KeyInfo | null>(null);

  async function remove(k: KeyInfo) {
    const used = servers.filter((s) => s.keyId === k.id).length;
    const warn = used > 0 ? `\n${used} host(s) use this key and fall back to a password.` : '';
    if (!(await askConfirm(`Really delete the key "${k.name}"?${warn}`, { title: 'Delete key', confirmLabel: 'Delete', danger: true }))) return;
    try {
      await api.keysDelete(k.id);
      await onChanged();
      toast('Key deleted');
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Keys</h1>
          <p className="muted">
            Private SSH keys are stored encrypted in the vault and can be shared by several hosts.
            After import, the private part never leaves the host process.
          </p>
        </div>
        <div className="row-actions">
          <button className="primary" onClick={() => setGenerating(true)}>
            <IconPlus size={15} /> Generate key
          </button>
          <button onClick={() => setAdding(true)}>
            <IconUpload size={15} /> Import key
          </button>
        </div>
      </div>

      {keys.length === 0 ? (
        <div className="empty-card">
          <IconKey size={28} />
          <h3>No keys yet</h3>
          <p className="muted">Generate a new key pair or import an existing private key (OpenSSH or PEM).</p>
          <button className="primary" onClick={() => setGenerating(true)}>
            Generate key
          </button>
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
                    {k.kind || 'Key'} · {k.hasPassphrase ? 'with passphrase' : 'no passphrase'} · {used} Host{used === 1 ? '' : 's'}
                  </div>
                </div>
                {k.hasPassphrase && (
                  <span className="muted" title="Passphrase saved">
                    <IconLock size={15} />
                  </span>
                )}
                <button className="ghost icon-only" title="Show public key / install on a server" onClick={() => setShowPublic(k)} disabled={!k.publicKey}>
                  <IconCopy size={16} />
                </button>
                <button className="ghost icon-only danger" title="Delete" onClick={() => remove(k)}>
                  <IconTrash size={16} />
                </button>
              </div>
            );
          })}
        </div>
      )}

      <IdentitiesSection identities={identities} keys={keys} servers={servers} onChanged={onChanged} toast={toast} />

      {generating && (
        <GenerateKeyDialog
          onClose={() => setGenerating(false)}
          onCreated={async (key) => {
            setGenerating(false);
            await onChanged();
            setShowPublic(key);
          }}
        />
      )}
      {showPublic && <PublicKeyDialog keyInfo={showPublic} servers={servers} onClose={() => setShowPublic(null)} onChanged={onChanged} toast={toast} />}

      {adding && (
        <AddKey
          onClose={() => setAdding(false)}
          onSaved={async () => {
            setAdding(false);
            await onChanged();
            toast('Key saved');
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
          <h2>Add key</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <label>
          Name
          <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. work laptop" />
        </label>

        <label>
          Private key
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
            <IconUpload size={15} /> Choose file…
          </button>
        </div>

        <label>
          Passphrase (if the key has one)
          <input type="password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} />
        </label>

        <p className="muted small">
          Supported: OpenSSH (ed25519, rsa, ecdsa) and PEM. Convert PuTTY keys (.ppk) in PuTTYgen first via
          "Conversions → Export OpenSSH key".
        </p>

        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={busy || !name.trim() || !privateKey.trim()}>
            Save
          </button>
        </div>
      </form>
    </div>
  );
}
