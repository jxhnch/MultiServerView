import { useState, type FormEvent } from 'react';
import type { BulkLogin } from './bridge';
import { IconClose } from './Icons';
import type { Identity, KeyInfo, Server } from './types';

/** One login for several hosts: an identity, an SSH key, or user name and password. */
export function BulkLoginDialog({
  hosts,
  keys,
  identities,
  onApply,
  onClose,
}: {
  hosts: Server[];
  keys: KeyInfo[];
  identities: Identity[];
  onApply: (login: BulkLogin) => void;
  onClose: () => void;
}) {
  const [mode, setMode] = useState<BulkLogin['mode']>(keys.length ? 'key' : identities.length ? 'identity' : 'password');
  const [keyId, setKeyId] = useState(keys[0]?.id ?? '');
  const [identityId, setIdentityId] = useState(identities[0]?.id ?? '');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');

  // An SSH key only works for SSH hosts; nothing can be set on a serial port. The others stay as they are.
  const fits = hosts.filter((s) => (mode === 'key' ? s.protocol === 'ssh' : s.protocol !== 'serial')).length;
  const complete = mode === 'key' ? !!keyId : mode === 'identity' ? !!identityId : !!(username.trim() || password);
  const ready = complete && fits > 0;

  function submit(e: FormEvent) {
    e.preventDefault();
    if (!ready) return;
    if (mode === 'identity') onApply({ mode, identityId });
    else if (mode === 'key') onApply({ mode, keyId, username: username.trim() });
    else onApply({ mode, username: username.trim(), password });
  }

  const tab = (value: BulkLogin['mode'], label: string) => (
    <button type="button" role="tab" aria-selected={mode === value} className={mode === value ? 'on' : ''} onClick={() => setMode(value)}>
      {label}
    </button>
  );

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>
            Set the login of {hosts.length} host{hosts.length === 1 ? '' : 's'}
          </h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <div className="seg" role="tablist">
          {tab('key', 'SSH key')}
          {tab('identity', 'Identity')}
          {tab('password', 'User + password')}
        </div>
        {mode === 'key' &&
          (keys.length === 0 ? (
            <p className="muted small">There is no SSH key yet. Create or import one on the Keychain page.</p>
          ) : (
            <label>
              SSH key
              <select value={keyId} onChange={(e) => setKeyId(e.target.value)}>
                {keys.map((k) => (
                  <option key={k.id} value={k.id}>
                    {k.name} ({k.kind})
                  </option>
                ))}
              </select>
            </label>
          ))}
        {mode === 'identity' &&
          (identities.length === 0 ? (
            <p className="muted small">There is no identity yet. Create one on the Keychain page.</p>
          ) : (
            <label>
              Identity
              <select value={identityId} onChange={(e) => setIdentityId(e.target.value)}>
                {identities.map((i) => (
                  <option key={i.id} value={i.id}>
                    {i.name} ({i.username})
                  </option>
                ))}
              </select>
            </label>
          ))}
        {mode !== 'identity' && (
          <label>
            Username (empty = keep each host's own)
            <input value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="off" />
          </label>
        )}
        {mode === 'password' && (
          <label>
            Password (empty = keep each host's own)
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
          </label>
        )}
        <p className="muted small">
          Applies to {fits} of {hosts.length}
          {fits < hosts.length ? (mode === 'key' ? ' (only SSH hosts can use a key)' : ' (serial hosts have no login)') : ''}.
          {mode !== 'identity' ? ' An identity assigned to these hosts is removed.' : ''}
        </p>
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={!ready}>
            Apply
          </button>
        </div>
      </form>
    </div>
  );
}
