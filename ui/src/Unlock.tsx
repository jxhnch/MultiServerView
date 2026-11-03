import { useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconCloud, IconServer } from './Icons';
import { TitleStrip } from './WindowControls';

export function Unlock({ exists, onDone, onVaultInstalled }: { exists: boolean; onDone: () => void; onVaultInstalled: () => void }) {
  const [pw, setPw] = useState('');
  const [pw2, setPw2] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [cloud, setCloud] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError('');
    if (!exists && pw !== pw2) {
      setError('The passwords do not match.');
      return;
    }
    setBusy(true);
    try {
      if (exists) await api.vaultUnlock(pw);
      else await api.vaultCreate(pw);
      onDone();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="unlock">
      <TitleStrip />
      <div className="unlock-stack">
        <form className="unlock-card" onSubmit={submit}>
          <div className="logo">
            <IconServer size={30} />
          </div>
          <h1>MultiServerView</h1>
          <p className="muted">
            {exists
              ? 'Enter your master password to unlock the vault.'
              : 'New vault. The master password encrypts all servers, keys and passwords. There is no recovery if you forget it.'}
          </p>
          <input type="password" autoFocus placeholder="Master password" value={pw} onChange={(e) => setPw(e.target.value)} />
          {!exists && (
            <input type="password" placeholder="Repeat password" value={pw2} onChange={(e) => setPw2(e.target.value)} />
          )}
          {error && <div className="error">{error}</div>}
          <button className="primary big" disabled={busy || pw.length === 0}>
            {busy ? 'One moment…' : exists ? 'Unlock' : 'Create vault'}
          </button>
        </form>

        {!exists && (
          <div className="unlock-alt">
            {!cloud ? (
              <button className="link" onClick={() => setCloud(true)}>
                <IconCloud size={15} /> New device? Load vault from a sync folder or cloud
              </button>
            ) : (
              <CloudDownload onInstalled={onVaultInstalled} onCancel={() => setCloud(false)} />
            )}
          </div>
        )}
      </div>
    </div>
  );
}

function CloudDownload({ onInstalled, onCancel }: { onInstalled: () => void; onCancel: () => void }) {
  const [mode, setMode] = useState<'folder' | 'supabase'>('folder');
  const [folder, setFolder] = useState('');
  const [url, setUrl] = useState('');
  const [anonKey, setAnonKey] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await api.syncDownloadInitial({ mode, folder: folder.trim(), url, anonKey, email, password });
      onInstalled(); // the vault is now local -> normal unlock dialog
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <form className="unlock-card small" onSubmit={submit}>
      <h3>Load vault</h3>
      <div className="mode-switch">
        <button type="button" className={mode === 'folder' ? 'on' : ''} onClick={() => setMode('folder')}>
          Folder
        </button>
        <button type="button" className={mode === 'supabase' ? 'on' : ''} onClick={() => setMode('supabase')}>
          Supabase
        </button>
      </div>
      <p className="muted small">
        {mode === 'folder'
          ? 'The sync folder that contains the file MultiServerView-Vault.msv (e.g. in OneDrive). '
          : 'Credentials of your Supabase account. '}
        Then you unlock with the <b>same master password</b> as on the other device.
      </p>
      {mode === 'folder' ? (
        <div className="row">
          <input className="grow" placeholder="Sync folder" value={folder} onChange={(e) => setFolder(e.target.value)} />
          <button
            type="button"
            onClick={() =>
              api
                .syncPickFolder()
                .then((r) => r.path && setFolder(r.path))
                .catch((err: Error) => setError(err.message))
            }
          >
            Choose…
          </button>
        </div>
      ) : (
        <>
          <input placeholder="Supabase-URL (https://…supabase.co)" value={url} onChange={(e) => setUrl(e.target.value)} />
          <input placeholder="Anon (public) Key" value={anonKey} onChange={(e) => setAnonKey(e.target.value)} />
          <input type="email" placeholder="E-mail" value={email} onChange={(e) => setEmail(e.target.value)} />
          <input type="password" placeholder="Cloud password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </>
      )}
      {error && <div className="error">{error}</div>}
      <div className="actions">
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
        <span className="grow" />
        <button className="primary" disabled={busy || (mode === 'folder' ? !folder.trim() : !url || !anonKey || !email || !password)}>
          {busy ? 'Loading…' : 'Load'}
        </button>
      </div>
    </form>
  );
}
