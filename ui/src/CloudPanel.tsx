import { useEffect, useState } from 'react';
import { api, isNative, type SyncConfigInput } from './bridge';
import { IconCheck, IconCloud, IconCopy, IconRefresh, IconFolder } from './Icons';
import { SUPABASE_SQL } from './supabaseSql';
import type { SyncInfo, SyncStatus } from './types';
import { askConfirm } from './Confirm';

export function CloudPanel({
  info,
  status,
  onChanged,
  toast,
}: {
  info: SyncInfo | null;
  status: SyncStatus;
  onChanged: () => Promise<void>;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const [mode, setMode] = useState<'folder' | 'gist' | 'supabase'>('folder');
  const [folder, setFolder] = useState('');
  const [url, setUrl] = useState('');
  const [anonKey, setAnonKey] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    if (!info) return;
    setMode(info.configured ? info.mode : 'folder');
    setFolder(info.folder);
    setUrl(info.url);
    setAnonKey(info.anonKey);
    setEmail(info.email);
  }, [info]);

  const cfg = (): SyncConfigInput => ({ mode, folder: folder.trim(), url: url.trim(), anonKey: anonKey.trim(), email: email.trim(), password });
  const ready =
    mode === 'folder'
      ? !!folder.trim()
      : mode === 'gist'
        ? !!(password || info?.hasPassword)
        : url.trim() && anonKey.trim() && email.trim() && (password || info?.hasPassword);

  async function pickFolder() {
    try {
      const r = await api.syncPickFolder();
      if (r.path) setFolder(r.path);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function run(label: string, fn: () => Promise<void>) {
    setBusy(label);
    setError('');
    try {
      await fn();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy('');
    }
  }

  const save = () =>
    run('save', async () => {
      await api.syncSave(cfg());
      setPassword('');
      await onChanged();
      await api.syncNow();
      await onChanged();
      toast('Sync connected and synchronized');
    });

  const test = () =>
    run('test', async () => {
      await api.syncTest({ ...cfg(), password: password || '' });
      toast('Connection works');
    });

  const signUp = () =>
    run('signup', async () => {
      const r = await api.syncSignUp(cfg());
      if (!r.signedIn) {
        toast('Account created. Please confirm the e-mail from Supabase, then click "Connect".');
        return;
      }
      // signed in right away: connect and synchronize in one go
      await api.syncSave(cfg());
      setPassword('');
      await onChanged();
      await api.syncNow();
      await onChanged();
      toast('Account created, sync connected and synchronized');
    });

  const now = () =>
    run('now', async () => {
      await api.syncNow();
      await onChanged();
    });

  const disconnect = () =>
    run('disc', async () => {
      if (!(await askConfirm('Disconnect sync? Your local vault stays, and so does the copy in the folder or in the cloud.', { title: 'Disconnect sync', confirmLabel: 'Disconnect' }))) return;
      await api.syncDisconnect();
      await onChanged();
      toast('Sync disconnected');
    });

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Sync</h1>
          <p className="muted">
            Your vault is <b>end-to-end encrypted</b>: only an unreadable blob is stored in the folder, your GitHub gist or at Supabase.
            Your master password never leaves this device.
          </p>
        </div>
        {info?.configured && (
          <span className={`pill ${status.state === 'error' ? 'error' : status.state === 'syncing' ? 'connecting' : 'connected'}`}>
            <span className="dot-sm" />
            {status.state === 'off' ? 'Connected' : status.message || 'Connected'}
          </span>
        )}
      </div>

      {!isNative && <div className="notice">Sync is not available in browser mode – it only works in the Windows app.</div>}

      {mode === 'supabase' && !info?.configured && <Setup open />}

      <div className="card-form">
        <div className="mode-switch">
          <button type="button" className={mode === 'folder' ? 'on' : ''} onClick={() => setMode('folder')}>
            Folder <span className="muted small">(OneDrive, iCloud, Syncthing …)</span>
          </button>
          <button type="button" className={mode === 'gist' ? 'on' : ''} onClick={() => setMode('gist')}>
            GitHub <span className="muted small">(private gist, only a token)</span>
          </button>
          <button type="button" className={mode === 'supabase' ? 'on' : ''} onClick={() => setMode('supabase')}>
            Supabase <span className="muted small">(own account)</span>
          </button>
        </div>
        {mode === 'folder' ? (
          <>
            <div className="row">
              <label className="grow">
                Sync folder
                <input value={folder} onChange={(e) => setFolder(e.target.value)} placeholder="C:\Users\you\OneDrive\MultiServerView" className="mono" />
              </label>
              <button type="button" style={{ alignSelf: 'flex-end' }} onClick={() => void pickFolder()} disabled={!!busy}>
                <IconFolder size={15} /> Choose…
              </button>
            </div>
            <p className="muted small">
              The app stores the encrypted file <code>MultiServerView-Vault.msv</code> there. Your folder service brings it to your other PCs,
              and the app merges the changes. Without the master password the file is unreadable. On the second PC, choose the same folder.
            </p>
          </>
        ) : mode === 'gist' ? (
          <>
            <label>
              GitHub token
              <input
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder={info?.hasPassword ? '•••••••• (unchanged)' : 'ghp_…'}
                className="mono"
              />
            </label>
            <ol className="muted small steps">
              <li>
                Open <a href="https://github.com/settings/tokens/new?scopes=gist&description=MultiServerView" target="_blank" rel="noreferrer">github.com/settings/tokens/new</a> (opens with
                the right permission selected).
              </li>
              <li>Choose an expiry (or "No expiration"), click <b>Generate token</b> and paste it above.</li>
              <li>
                On your second PC use the same token under "New device? Load vault …". The app finds the vault by itself.
              </li>
            </ol>
            <p className="muted small">
              The app stores one <b>secret gist</b> with the encrypted vault in your GitHub account. Only the token holder can find it, and without
              the master password the content is unreadable. Revoke the token on GitHub at any time to cut the access.
            </p>
          </>
        ) : (
          <>
        <div className="row">
          <label className="grow">
            Supabase-URL
            <input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://abcdxyz.supabase.co" />
          </label>
        </div>
        <label>
          Anon (public) Key
          <input value={anonKey} onChange={(e) => setAnonKey(e.target.value)} placeholder="eyJhbGciOi…" className="mono" />
        </label>
        <div className="row">
          <label className="grow">
            E-Mail
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="you@example.com" />
          </label>
          <label className="grow">
            Cloud password
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder={info?.hasPassword ? '•••••••• (unchanged)' : 'Password for the Supabase account'}
            />
          </label>
        </div>

          </>
        )}

        {error && <div className="error">{error}</div>}

        <div className="actions">
          {mode === 'supabase' && (
            <button onClick={signUp} disabled={!!busy || !url || !anonKey || !email || !password}>
              {info?.configured ? 'Create account' : 'Create account and connect'}
            </button>
          )}
          <button onClick={test} disabled={!!busy || !ready}>
            Test
          </button>
          <span className="grow" />
          {info?.configured && (
            <button className="ghost danger" onClick={disconnect} disabled={!!busy}>
              Disconnect
            </button>
          )}
          <button className="primary" onClick={save} disabled={!!busy || !ready}>
            <IconCloud size={15} /> {busy === 'save' ? 'Connecting…' : info?.configured ? 'Save & sync' : 'Connect'}
          </button>
        </div>
      </div>

      {info?.configured && (
        <div className="card-form row center-v">
          <div className="grow">
            <div className="list-title">Sync now</div>
            <div className="muted small">
              {info.lastSync ? `Last: ${new Date(info.lastSync).toLocaleString('en-GB')}` : 'Not synchronized yet'} · runs
              automatically after every change
            </div>
          </div>
          <button onClick={now} disabled={!!busy}>
            <IconRefresh size={15} /> Sync
          </button>
        </div>
      )}

      {info?.configured && <PasswordTools toast={toast} onChanged={onChanged} />}

      {mode === 'supabase' && info?.configured && <Setup open={false} />}
    </div>
  );
}

function Setup({ open }: { open: boolean }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(SUPABASE_SQL);
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      /* clipboard not allowed */
    }
  };
  const link = (href: string, text: string) => (
    <a href={href} target="_blank" rel="noreferrer">
      {text}
    </a>
  );

  return (
    <details className="card-form setup" open={open}>
      <summary>Setup in 4 steps (one time, about 3 minutes)</summary>
      <ol className="steps">
        <li>
          <b>Create a free project</b>: {link('https://supabase.com/dashboard/new', 'supabase.com/dashboard/new')} (any name, choose a region near you and a database password you do not need again).
        </li>
        <li>
          <b>Create the table</b>: click <button className="ghost" onClick={copy}>{copied ? <IconCheck size={14} /> : <IconCopy size={14} />} {copied ? 'Copied' : 'Copy SQL'}</button>, open the{' '}
          {link('https://supabase.com/dashboard/project/_/sql/new', 'SQL editor')}, paste and press <b>Run</b>.
        </li>
        <li>
          <b>Copy the address and key</b>: open {link('https://supabase.com/dashboard/project/_/settings/api', 'Project Settings → API')} and paste the <i>Project URL</i> (the project id alone also works) and the{' '}
          <i>anon public</i> key below.
        </li>
        <li>
          <b>Choose an e-mail and a password</b> for your sync account and click <b>Create account and connect</b>. If Supabase sends a confirmation mail, confirm it
          and click <b>Connect</b> (or switch off "Confirm email" under{' '}
          {link('https://supabase.com/dashboard/project/_/auth/providers', 'Authentication → Providers → Email')}).
        </li>
      </ol>
      <details>
        <summary className="muted small">Show the SQL</summary>
        <pre className="code">{SUPABASE_SQL}</pre>
      </details>
    </details>
  );
}

/** For when the master password was changed on another device (or this device should replace the synced vault). */
function PasswordTools({ toast, onChanged }: { toast: (msg: string, kind?: 'ok' | 'error') => void; onChanged: () => Promise<void> }) {
  const [adopting, setAdopting] = useState(false);
  const [pw, setPw] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  async function adopt(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await api.syncAdoptPassword(pw);
      await onChanged();
      toast('New master password adopted');
      setAdopting(false);
      setPw('');
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function force() {
    if (!(await askConfirm('Overwrite the synced vault with the state of this device?\nChanges from other devices that are not here yet will be lost.', { title: 'Overwrite vault', confirmLabel: 'Overwrite', danger: true }))) return;
    try {
      await api.syncForceUpload();
      toast('Synced vault overwritten with this vault');
      await onChanged();
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <details className="card-form setup">
      <summary>Advanced: master password changed?</summary>
      <p className="muted small">
        When the master password is changed, the synced vault is encrypted with the new key. Other devices that still use the old password
        can no longer sync until they adopt the new password.
      </p>
      <div className="actions" style={{ justifyContent: 'flex-start' }}>
        <button onClick={() => setAdopting(true)}>Adopt new master password…</button>
        <button className="ghost danger" onClick={() => void force()}>
          Overwrite synced vault with this vault
        </button>
      </div>
      {adopting && (
        <form className="card-form" style={{ background: 'var(--surface-2)' }} onSubmit={adopt}>
          <label>
            New master password (the one you set on the other device)
            <input type="password" autoFocus value={pw} onChange={(e) => setPw(e.target.value)} />
          </label>
          {error && <div className="error">{error}</div>}
          <div className="actions">
            <button type="button" onClick={() => setAdopting(false)}>
              Cancel
            </button>
            <button className="primary" disabled={busy || pw.length < 8}>
              Adopt
            </button>
          </div>
        </form>
      )}
    </details>
  );
}
