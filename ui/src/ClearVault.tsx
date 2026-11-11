import { useEffect, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose } from './Icons';

/** What the user has to type (step 2). */
const PHRASE = 'CLEAR VAULT';

interface Summary {
  servers: number;
  keys: number;
  identities: number;
  snippets: number;
  forwards: number;
  proxmox: number;
  knownHosts: number;
}

const plural = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`;

/**
 * Clears the whole vault - deliberately in three steps: 1. see what will be deleted (with a chance to back up),
 * 2. type a phrase, 3. enter the master password. The master password itself stays.
 */
export function ClearVaultDialog({ onClose, onCleared, toast }: { onClose: () => void; onCleared: () => Promise<void>; toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [step, setStep] = useState<1 | 2 | 3>(1);
  const [summary, setSummary] = useState<Summary | null>(null);
  const [phrase, setPhrase] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    api.vaultSummary().then(setSummary).catch((e: Error) => setError(e.message));
  }, []);

  async function backup() {
    try {
      const r = await api.vaultExport();
      if (r.saved) toast('Backup saved: ' + r.path);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError('');
    if (step === 1) return setStep(2);
    if (step === 2) return phrase === PHRASE ? setStep(3) : undefined;
    setBusy(true);
    try {
      await api.vaultClear(password);
      await onCleared();
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  const lines = summary
    ? [
        plural(summary.servers, 'Host', 'Hosts'),
        plural(summary.keys, 'key', 'keys'),
        plural(summary.identities, 'Identity', 'Identities'),
        plural(summary.snippets, 'Snippet', 'Snippets'),
        plural(summary.forwards, 'port forward', 'port forwards'),
        plural(summary.proxmox, 'Proxmox connection', 'Proxmox connections'),
        plural(summary.knownHosts, 'known host key', 'known host keys'),
      ]
    : [];

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Clear vault · step {step} of 3</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        {step === 1 && (
          <>
            <p className="confirm-text">
              This deletes <b>everything</b> from the vault, permanently:
            </p>
            <ul className="clear-list">
              {lines.map((l) => (
                <li key={l}>{l}</li>
              ))}
            </ul>
            <p className="muted small">
              The master password stays. If your vault is stored at a folder, GitHub or Supabase, the empty vault is saved there too.
            </p>
            <div>
              <button type="button" onClick={() => void backup()}>
                Save a backup first…
              </button>
            </div>
          </>
        )}

        {step === 2 && (
          <label>
            To confirm, type <b>{PHRASE}</b>
            <input autoFocus value={phrase} onChange={(e) => setPhrase(e.target.value)} placeholder={PHRASE} autoComplete="off" spellCheck={false} />
          </label>
        )}

        {step === 3 && (
          <label>
            Last step: your master password
            <input autoFocus type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
          </label>
        )}

        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className={'primary' + (step === 3 ? ' danger' : '')} disabled={busy || (step === 1 && !summary) || (step === 2 && phrase !== PHRASE) || (step === 3 && !password)}>
            {step === 3 ? (busy ? 'Clearing…' : 'Clear permanently') : 'Next'}
          </button>
        </div>
      </form>
    </div>
  );
}
