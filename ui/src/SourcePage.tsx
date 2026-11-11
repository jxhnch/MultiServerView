import { useEffect, useState } from 'react';
import { api, isNative, type SourceConfigInput } from './bridge';
import { askConfirm } from './Confirm';
import { IconCheck, IconCloud, IconCopy, IconFolder, IconServer } from './Icons';
import { SUPABASE_SQL } from './supabaseSql';
import type { SourceInfo, SourceMode, SourceStatus } from './types';

const LABEL: Record<SourceMode, string> = { local: 'this PC', folder: 'your folder', gist: 'GitHub', supabase: 'Supabase' };

const CHOICES: { mode: SourceMode; title: string; text: string }[] = [
  { mode: 'local', title: 'This PC', text: 'Only on this computer. Nothing leaves it.' },
  { mode: 'folder', title: 'A folder', text: 'A file in a folder you already sync: OneDrive, iCloud Drive, Dropbox, Nextcloud …' },
  { mode: 'gist', title: 'GitHub', text: 'A private gist in your GitHub account. You only need a token.' },
  { mode: 'supabase', title: 'Supabase', text: 'Your own free database. A little setup, but it works from anywhere.' },
];

type Decision = null | { kind: 'empty' | 'vault'; sameVault: boolean };

export function SourcePage({
  info,
  status,
  onChanged,
  onReplaced,
  toast,
}: {
  info: SourceInfo | null;
  status: SourceStatus;
  onChanged: () => Promise<void>;
  /** The vault of this device was replaced by the one at the source: lock and return to the unlock screen. */
  onReplaced: () => void;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const current: SourceMode = info?.configured ? info.mode : 'local';
  const [pick, setPick] = useState<SourceMode>(current);
  const [folder, setFolder] = useState('');
  const [url, setUrl] = useState('');
  const [anonKey, setAnonKey] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [decision, setDecision] = useState<Decision>(null);

  useEffect(() => {
    if (!info) return;
    setPick(info.configured ? info.mode : 'local');
    setFolder(info.folder);
    setUrl(info.url);
    setAnonKey(info.anonKey);
    setEmail(info.email);
  }, [info]);

  const cfg = (): SourceConfigInput => ({ mode: pick === 'local' ? 'folder' : pick, folder: folder.trim(), url: url.trim(), anonKey: anonKey.trim(), email: email.trim(), password });
  const hasStoredPassword = !!info?.hasPassword && info.mode === pick;
  const ready =
    pick === 'folder' ? !!folder.trim() : pick === 'gist' ? !!(password || hasStoredPassword) : pick === 'supabase' ? !!(url.trim() && anonKey.trim() && email.trim() && (password || hasStoredPassword)) : true;

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

  const pickFolder = () =>
    run('folder', async () => {
      const r = await api.sourcePickFolder();
      if (r.path) setFolder(r.path);
    });

  const goLocal = () =>
    run('local', async () => {
      if (!(await askConfirm(`Keep your vault only on this PC from now on?\nThe copy at ${LABEL[current]} stays where it is, but is no longer updated.`, { title: 'Use this PC only', confirmLabel: 'Use this PC only' }))) return;
      await api.sourceDisconnect();
      await onChanged();
      toast('Your vault is now stored on this PC only.');
    });

  const next = () =>
    run('probe', async () => {
      const r = await api.sourceProbe(cfg());
      if (r.kind === 'notavault') {
        setError('Something is stored there, but it is not a MultiServerView vault. Nothing was changed. Choose another folder or account.');
        return;
      }
      setDecision({ kind: r.kind, sameVault: r.sameVault });
    });

  const moveHere = (overwrite: boolean) =>
    run('move', async () => {
      await api.sourceMoveHere(cfg(), overwrite);
      setDecision(null);
      setPassword('');
      await onChanged();
      toast(`Done. Your vault is now saved to ${LABEL[pick]} and every change goes there.`);
    });

  const useExisting = () =>
    run('use', async () => {
      await api.sourceUseExisting(cfg());
      setDecision(null);
      setPassword('');
      toast('Done. Unlock with the master password of that vault. Your previous vault is kept as a backup file.');
      onReplaced();
    });

  const retry = () =>
    run('retry', async () => {
      await api.sourceRetry();
      await onChanged();
    });

  const signUp = () =>
    run('signup', async () => {
      const r = await api.sourceSignUp(cfg());
      toast(r.signedIn ? 'Account created. Click Continue.' : 'Account created. Please confirm the e-mail from Supabase, then click Continue.');
    });

  const stateText = () => {
    if (!info) return '';
    if (current === 'local') return 'Your vault is stored on this PC only.';
    const when = status.lastSaved ? ` · last saved ${new Date(status.lastSaved).toLocaleString(undefined, { dateStyle: 'short', timeStyle: 'short' })}` : '';
    return (status.message || `Saved to ${LABEL[current]}`) + (status.state === 'ready' ? when : '');
  };

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Where is your vault?</h1>
          <p className="muted">
            Your vault is always stored in <b>one</b> place. Pick it here – every change is saved there right away, and the data is encrypted with your master password
            before it leaves this PC.
          </p>
        </div>
      </div>

      {!isNative && <div className="notice">Sources are not available in browser mode – only in the Windows app.</div>}

      <div className={'source-status ' + (info ? status.state : 'local')}>
        <span className="source-dot" />
        <div className="grow">
          <div className="source-line">{stateText()}</div>
          {status.state === 'pending' && <div className="muted small">Your changes are safe on this PC.</div>}
        </div>
        {(status.state === 'pending' || status.state === 'error') && current !== 'local' && (
          <button onClick={retry} disabled={!!busy}>
            Try again
          </button>
        )}
      </div>

      <div className="source-choices">
        {CHOICES.map((c) => (
          <button key={c.mode} type="button" className={'source-card' + (pick === c.mode ? ' on' : '')} onClick={() => { setPick(c.mode); setError(''); }}>
            <span className="source-ico">{c.mode === 'local' ? <IconServer size={20} /> : c.mode === 'folder' ? <IconFolder size={20} /> : <IconCloud size={20} />}</span>
            <span className="source-title">
              {c.title}
              {current === c.mode && <span className="source-badge">In use</span>}
            </span>
            <span className="muted small">{c.text}</span>
          </button>
        ))}
      </div>

      {pick === 'local' ? (
        current !== 'local' && (
          <div className="card-form">
            <p className="muted small">The vault stays on this PC. The copy at {LABEL[current]} is not touched.</p>
            <div className="actions">
              <span className="grow" />
              <button className="primary" onClick={goLocal} disabled={!!busy}>
                Use this PC only
              </button>
            </div>
          </div>
        )
      ) : (
        <div className="card-form">
          {pick === 'folder' && (
            <>
              <div className="row">
                <label className="grow">
                  Folder for your vault
                  <input value={folder} onChange={(e) => setFolder(e.target.value)} placeholder="C:\Users\you\OneDrive\MultiServerView" className="mono" />
                </label>
                <button type="button" style={{ alignSelf: 'flex-end' }} onClick={() => void pickFolder()} disabled={!!busy}>
                  <IconFolder size={15} /> Choose…
                </button>
              </div>
              <p className="muted small">
                The app stores one encrypted file there (<code>MultiServerView-Vault.msv</code>). Pick a folder that OneDrive, iCloud, Dropbox or Nextcloud already keeps in step.
              </p>
            </>
          )}

          {pick === 'gist' && (
            <>
              <label>
                GitHub token
                <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder={hasStoredPassword ? '•••••••• (unchanged)' : 'ghp_…'} className="mono" />
              </label>
              <ol className="muted small steps">
                <li>
                  Open{' '}
                  <a href="https://github.com/settings/tokens/new?scopes=gist&description=MultiServerView" target="_blank" rel="noreferrer">
                    github.com/settings/tokens/new
                  </a>{' '}
                  (the right permission is preselected).
                </li>
                <li>Choose an expiry (or "No expiration"), click <b>Generate token</b> and paste it above.</li>
              </ol>
              <p className="muted small">The app keeps one secret gist with the encrypted vault in your account. GitHub also remembers older versions of it.</p>
            </>
          )}

          {pick === 'supabase' && (
            <>
              <SupabaseSteps />
              <div className="row">
                <label className="grow">
                  Project URL
                  <input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="https://abcdxyz.supabase.co (or just abcdxyz)" />
                </label>
              </div>
              <label>
                Anon (public) key
                <input value={anonKey} onChange={(e) => setAnonKey(e.target.value)} placeholder="eyJhbGciOi…" className="mono" />
              </label>
              <div className="row">
                <label className="grow">
                  E-mail
                  <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="you@example.com" />
                </label>
                <label className="grow">
                  Password for this account
                  <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} placeholder={hasStoredPassword ? '•••••••• (unchanged)' : 'Choose a password'} />
                </label>
              </div>
            </>
          )}

          {error && <div className="error">{error}</div>}

          <div className="actions">
            {pick === 'supabase' && (
              <button onClick={signUp} disabled={!!busy || !url || !anonKey || !email || !password}>
                Create account
              </button>
            )}
            <span className="grow" />
            <button className="primary" onClick={next} disabled={!!busy || !ready}>
              {busy === 'probe' ? 'Looking…' : current === pick ? 'Save settings' : 'Continue'}
            </button>
          </div>
        </div>
      )}

      {decision && (
        <div className="overlay" onMouseDown={() => !busy && setDecision(null)}>
          <div className="modal" style={{ width: 'min(520px, 94vw)' }} onMouseDown={(e) => e.stopPropagation()}>
            {decision.kind === 'empty' ? (
              <>
                <h2>Move your vault to {LABEL[pick]}?</h2>
                <p className="muted small" style={{ marginTop: 0 }}>
                  Nothing is stored there yet. Your vault from this PC is saved there, and from now on every change goes there.
                </p>
                {error && <div className="error">{error}</div>}
                <div className="actions">
                  <button onClick={() => setDecision(null)} disabled={!!busy}>
                    Cancel
                  </button>
                  <span className="grow" />
                  <button className="primary" onClick={() => void moveHere(false)} disabled={!!busy}>
                    {busy === 'move' ? 'Moving…' : 'Move my vault here'}
                  </button>
                </div>
              </>
            ) : (
              <>
                <h2>There is already a vault at {LABEL[pick]}</h2>
                <p className="muted small" style={{ marginTop: 0 }}>
                  {decision.sameVault
                    ? 'It comes from this PC (an older sync). '
                    : 'This is usually your vault from another device. '}
                  You can only have one vault, so choose which one you want to keep:
                </p>
                {error && <div className="error">{error}</div>}
                <div className="choice-list">
                  <button className="primary-choice" autoFocus onClick={() => void useExisting()} disabled={!!busy}>
                    <b>{busy === 'use' ? 'Loading…' : 'Use the vault that is there'}</b>
                    <span className="small">
                      The vault of this PC is replaced by it. Your current vault is kept as a backup file. Afterwards you unlock with the master password of that vault.
                    </span>
                  </button>
                  <button
                    onClick={async () => {
                      if (await askConfirm(`Really replace the vault at ${LABEL[pick]} with the vault of this PC?\nWhat is stored there now is overwritten.`, { title: 'Overwrite the vault there', confirmLabel: 'Overwrite it', danger: true })) await moveHere(true);
                    }}
                    disabled={!!busy}
                  >
                    <b>Replace it with my vault from this PC</b>
                    <span className="muted small">The vault there is overwritten (GitHub keeps older versions, a folder or Supabase does not).</span>
                  </button>
                </div>
                <div className="actions">
                  <span className="grow" />
                  <button onClick={() => setDecision(null)} disabled={!!busy}>
                    Cancel
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </div>
  );
}

function SupabaseSteps() {
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
    <details className="setup" open>
      <summary>Setup in 4 steps (one time, about 3 minutes)</summary>
      <ol className="steps">
        <li>
          <b>Create a free project</b>: {link('https://supabase.com/dashboard/new', 'supabase.com/dashboard/new')}.
        </li>
        <li>
          <b>Create the table</b>: click{' '}
          <button className="ghost" onClick={copy}>
            {copied ? <IconCheck size={14} /> : <IconCopy size={14} />} {copied ? 'Copied' : 'Copy SQL'}
          </button>
          , open the {link('https://supabase.com/dashboard/project/_/sql/new', 'SQL editor')}, paste and press <b>Run</b>.
        </li>
        <li>
          <b>Copy the address and key</b> from {link('https://supabase.com/dashboard/project/_/settings/api', 'Project Settings → API')} into the fields below.
        </li>
        <li>
          <b>Choose an e-mail and a password</b>, click <b>Create account</b>, then <b>Continue</b>. (If Supabase sends a confirmation mail, confirm it first, or switch off "Confirm email" under{' '}
          {link('https://supabase.com/dashboard/project/_/auth/providers', 'Authentication → Providers → Email')}.)
        </li>
      </ol>
    </details>
  );
}
