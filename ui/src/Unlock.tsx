import { useState, type FormEvent } from 'react';
import { api } from './bridge';

export function Unlock({ exists, onDone }: { exists: boolean; onDone: () => void }) {
  const [pw, setPw] = useState('');
  const [pw2, setPw2] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError('');
    if (!exists && pw !== pw2) {
      setError('Die Passwörter sind nicht gleich.');
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
      <form className="unlock-card" onSubmit={submit}>
        <div className="logo">▣</div>
        <h1>MultiServerView</h1>
        <p className="muted">
          {exists
            ? 'Gib dein Master-Passwort ein, um den Vault zu entsperren.'
            : 'Neuer Vault. Das Master-Passwort verschlüsselt alle Server und Zugangsdaten. Es gibt keine Wiederherstellung, wenn du es vergisst.'}
        </p>
        <input
          type="password"
          autoFocus
          placeholder="Master-Passwort"
          value={pw}
          onChange={(e) => setPw(e.target.value)}
        />
        {!exists && (
          <input
            type="password"
            placeholder="Passwort wiederholen"
            value={pw2}
            onChange={(e) => setPw2(e.target.value)}
          />
        )}
        {error && <div className="error">{error}</div>}
        <button className="primary" disabled={busy || pw.length === 0}>
          {busy ? 'Moment…' : exists ? 'Entsperren' : 'Vault erstellen'}
        </button>
      </form>
    </div>
  );
}
