import { useEffect, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose } from './Icons';
import type { KeyInfo, Server } from './types';

export interface Credentials {
  username?: string;
  password?: string;
  /** Sign in with this SSH key from the keychain instead of a password. */
  keyId?: string;
  save?: boolean;
}

/** Error text of the host on wrong credentials (see SshConnector.Describe). */
export const isAuthError = (message: string) => message.startsWith('Authentication failed');

/** Asks for the login like PuTTY when authentication fails (or when no password is saved at all): password or SSH key. */
export function PasswordPrompt({
  server,
  failed,
  onSubmit,
  onCancel,
}: {
  server: Server;
  failed: boolean;
  onSubmit: (cred: Credentials) => void;
  onCancel: () => void;
}) {
  const canUseKey = server.protocol === 'ssh';
  const [mode, setMode] = useState<'password' | 'key'>(canUseKey && server.auth === 'key' ? 'key' : 'password');
  const [username, setUsername] = useState(server.username);
  const [password, setPassword] = useState('');
  const [keys, setKeys] = useState<KeyInfo[] | null>(null);
  const [keyId, setKeyId] = useState(server.keyId);
  const [save, setSave] = useState(false);
  const canSave = !server.identityId;
  const ready = mode === 'password' ? !!password : !!keys?.some((k) => k.id === keyId);

  useEffect(() => {
    if (!canUseKey) return;
    let cancelled = false;
    api
      .keysList()
      .then((list) => {
        if (cancelled) return;
        setKeys(list);
        setKeyId((current) => (list.some((k) => k.id === current) ? current : (list[0]?.id ?? '')));
      })
      .catch(() => !cancelled && setKeys([]));
    return () => {
      cancelled = true;
    };
  }, [canUseKey]);

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!ready) return;
    const login = mode === 'key' ? { keyId } : { password };
    onSubmit({ username: username.trim(), ...login, save: canSave && save });
  }

  return (
    <div className="overlay" onMouseDown={onCancel}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onCancel()}>
        <div className="modal-head">
          <h2>Sign in to {server.name || server.host}</h2>
          <button type="button" className="ghost icon-only" onClick={onCancel}>
            <IconClose size={16} />
          </button>
        </div>
        {failed ? (
          <div className="error">The login did not work. Please check it and try again.</div>
        ) : (
          <p className="muted small">No password is saved for this host.</p>
        )}
        {canUseKey && (
          <div className="seg" role="tablist">
            <button type="button" role="tab" aria-selected={mode === 'password'} className={mode === 'password' ? 'on' : ''} onClick={() => setMode('password')}>
              Password
            </button>
            <button type="button" role="tab" aria-selected={mode === 'key'} className={mode === 'key' ? 'on' : ''} onClick={() => setMode('key')}>
              SSH key
            </button>
          </div>
        )}
        <label>
          Username
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" />
        </label>
        {mode === 'password' ? (
          <label>
            Password
            <input autoFocus type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </label>
        ) : keys && keys.length === 0 ? (
          <p className="muted small">There is no SSH key yet. Create or import one on the Keychain page.</p>
        ) : (
          <label>
            SSH key
            <select autoFocus value={keyId} onChange={(e) => setKeyId(e.target.value)}>
              {(keys ?? []).map((k) => (
                <option key={k.id} value={k.id}>
                  {k.name} ({k.kind})
                </option>
              ))}
            </select>
          </label>
        )}
        {canSave && (
          <label className="toggle-row">
            <span>Save for this host on success</span>
            <input type="checkbox" checked={save} onChange={(e) => setSave(e.target.checked)} />
          </label>
        )}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button className="primary" disabled={!ready}>
            Connect
          </button>
        </div>
      </form>
    </div>
  );
}
