import { useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconCheck, IconClose, IconCopy } from './Icons';
import { PasswordPrompt, isAuthError, type Credentials } from './PasswordPrompt';
import type { KeyInfo, Server } from './types';

type Toast = (msg: string, kind?: 'ok' | 'error') => void;

/** Generate a new key pair. Afterwards <see cref="PublicKeyDialog"/> opens with the public key. */
export function GenerateKeyDialog({
  onClose,
  onCreated,
}: {
  onClose: () => void;
  onCreated: (key: KeyInfo) => Promise<void>;
}) {
  const [name, setName] = useState('');
  const [type, setType] = useState<'ed25519' | 'rsa'>('ed25519');
  const [bits, setBits] = useState(4096);
  const [comment, setComment] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      const key = await api.keysGenerate({ name: name.trim(), type, bits, comment: comment.trim() });
      await onCreated(key);
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Generate SSH key</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <p className="muted small">
          The private part stays encrypted in this vault, the public part goes onto the server (into <code>~/.ssh/authorized_keys</code>).
        </p>

        <label>
          Name
          <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. My PC" />
        </label>

        <label>
          Type
          <select value={type} onChange={(e) => setType(e.target.value as 'ed25519' | 'rsa')}>
            <option value="ed25519">Ed25519 (recommended, short and fast)</option>
            <option value="rsa">RSA (for very old servers)</option>
          </select>
        </label>

        {type === 'rsa' && (
          <label>
            Length
            <select value={bits} onChange={(e) => setBits(Number(e.target.value))}>
              <option value={3072}>3072 bit</option>
              <option value={4096}>4096 bit</option>
            </select>
          </label>
        )}

        <label>
          Comment (optional, appears at the end of the public key)
          <input value={comment} onChange={(e) => setComment(e.target.value)} placeholder="e.g. john@my-pc" />
        </label>

        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={busy || !name.trim()}>
            {busy ? 'Generating…' : 'Generate'}
          </button>
        </div>
      </form>
    </div>
  );
}

/** Shows the public key for copying and, on request, installs it on a host over SSH (like ssh-copy-id). */
export function PublicKeyDialog({
  keyInfo,
  servers,
  onClose,
  onChanged,
  toast,
}: {
  keyInfo: KeyInfo;
  servers: Server[];
  onClose: () => void;
  onChanged: () => Promise<void>;
  toast: Toast;
}) {
  const hosts = servers.filter((s) => s.protocol === 'ssh');
  const [serverId, setServerId] = useState(hosts[0]?.id ?? '');
  const [switchHost, setSwitchHost] = useState(true);
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [needLogin, setNeedLogin] = useState<'none' | 'missing' | 'failed'>('none');

  const target = hosts.find((h) => h.id === serverId);

  async function copy() {
    try {
      await navigator.clipboard.writeText(keyInfo.publicKey);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      toast('Copying is not possible – please select the text and copy it with Ctrl+C.', 'error');
    }
  }

  async function install(cred?: Credentials) {
    if (!target) return;
    setBusy(true);
    setError('');
    try {
      await api.keysInstall({ keyId: keyInfo.id, serverId: target.id, switchHost, ...cred });
      await onChanged();
      toast(`Key installed on "${target.name || target.host}"${switchHost ? ' – the host now uses the key' : ''}`);
      onClose();
    } catch (err) {
      const message = (err as Error).message;
      setError(message);
      if (isAuthError(message)) setNeedLogin('failed');
      setBusy(false);
    }
  }

  function startInstall() {
    // No password saved (and no key): ask right away.
    if (target && target.auth === 'password' && !target.password && !target.identityId) setNeedLogin('missing');
    else void install();
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <div className="modal wide" onMouseDown={(e) => e.stopPropagation()} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Public key of "{keyInfo.name}"</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <label>
          This line belongs in <code>~/.ssh/authorized_keys</code> on the server
          <textarea className="mono selectable" rows={4} readOnly spellCheck={false} value={keyInfo.publicKey} onFocus={(e) => e.currentTarget.select()} />
        </label>
        <div className="muted small">
          {keyInfo.kind} · Fingerprint {keyInfo.fingerprint}
        </div>
        <div>
          <button type="button" onClick={copy}>
            {copied ? <IconCheck size={15} /> : <IconCopy size={15} />} {copied ? 'Copied' : 'Copy public key'}
          </button>
        </div>

        <h3 style={{ marginBottom: 0 }}>Install directly on a host</h3>
        {hosts.length === 0 ? (
          <p className="muted small">Add an SSH host first, then you can install the key here with one click.</p>
        ) : (
          <>
            <p className="muted small">
              Logs in once with the saved credentials and adds the key to <code>authorized_keys</code> (Linux/Unix servers only).
            </p>
            <label>
              Host
              <select value={serverId} onChange={(e) => setServerId(e.target.value)}>
                {hosts.map((h) => (
                  <option key={h.id} value={h.id}>
                    {h.name || h.host} ({h.username ? h.username + '@' : ''}
                    {h.host})
                  </option>
                ))}
              </select>
            </label>
            {target && !target.identityId && (
              <label className="toggle-row">
                <span>Switch the host to this key afterwards</span>
                <input type="checkbox" checked={switchHost} onChange={(e) => setSwitchHost(e.target.checked)} />
              </label>
            )}
            {error && needLogin === 'none' && <div className="error">{error}</div>}
            <div className="actions">
              <span className="grow" />
              <button type="button" onClick={onClose}>
                Close
              </button>
              <button type="button" className="primary" disabled={busy || !target} onClick={startInstall}>
                {busy ? 'Installing…' : 'Install on host'}
              </button>
            </div>
          </>
        )}
        {hosts.length === 0 && (
          <div className="actions">
            <span className="grow" />
            <button type="button" onClick={onClose}>
              Close
            </button>
          </div>
        )}
      </div>

      {needLogin !== 'none' && target && (
        <PasswordPrompt
          server={target}
          failed={needLogin === 'failed'}
          onCancel={() => {
            setNeedLogin('none');
            setError('');
          }}
          onSubmit={(cred) => {
            setNeedLogin('none');
            void install(cred);
          }}
        />
      )}
    </div>
  );
}
