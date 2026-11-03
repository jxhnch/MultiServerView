import { useState, type FormEvent } from 'react';
import { IconClose } from './Icons';
import type { Server } from './types';

export interface Credentials {
  username?: string;
  password?: string;
  save?: boolean;
}

/** Error text of the host on wrong credentials (see SshConnector.Describe). */
export const isAuthError = (message: string) => message.startsWith('Authentication failed');

/** Asks for username and password like PuTTY when authentication fails (or when no password is saved at all). */
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
  const [username, setUsername] = useState(server.username);
  const [password, setPassword] = useState('');
  const [save, setSave] = useState(false);
  const canSave = !server.identityId && server.auth === 'password';

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!password) return;
    onSubmit({ username: username.trim(), password, save: canSave && save });
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
          <div className="error">Username or password are wrong. Please enter them again.</div>
        ) : (
          <p className="muted small">No password is saved for this host.</p>
        )}
        <label>
          Username
          <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" />
        </label>
        <label>
          Password
          <input autoFocus type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </label>
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
          <button className="primary" disabled={!password}>
            Connect
          </button>
        </div>
      </form>
    </div>
  );
}
