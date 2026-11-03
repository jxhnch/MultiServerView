import { useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconCloud, IconServer } from './Icons';

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
      <div className="unlock-stack">
        <form className="unlock-card" onSubmit={submit}>
          <div className="logo">
            <IconServer size={30} />
          </div>
          <h1>MultiServerView</h1>
          <p className="muted">
            {exists
              ? 'Gib dein Master-Passwort ein, um den Vault zu entsperren.'
              : 'Neuer Vault. Das Master-Passwort verschlüsselt alle Server, Keys und Passwörter. Es gibt keine Wiederherstellung, wenn du es vergisst.'}
          </p>
          <input type="password" autoFocus placeholder="Master-Passwort" value={pw} onChange={(e) => setPw(e.target.value)} />
          {!exists && (
            <input type="password" placeholder="Passwort wiederholen" value={pw2} onChange={(e) => setPw2(e.target.value)} />
          )}
          {error && <div className="error">{error}</div>}
          <button className="primary big" disabled={busy || pw.length === 0}>
            {busy ? 'Moment…' : exists ? 'Entsperren' : 'Vault erstellen'}
          </button>
        </form>

        {!exists && (
          <div className="unlock-alt">
            {!cloud ? (
              <button className="link" onClick={() => setCloud(true)}>
                <IconCloud size={15} /> Neues Gerät? Vault aus der Cloud laden
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
      await api.syncDownloadInitial({ url, anonKey, email, password });
      onInstalled(); // Vault liegt jetzt lokal -> normaler Entsperren-Dialog
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <form className="unlock-card small" onSubmit={submit}>
      <h3>Aus Cloud laden</h3>
      <p className="muted small">
        Zugangsdaten deines Supabase-Kontos. Danach entsperrst du mit dem <b>gleichen Master-Passwort</b> wie auf dem anderen Gerät.
      </p>
      <input placeholder="Supabase-URL (https://…supabase.co)" value={url} onChange={(e) => setUrl(e.target.value)} />
      <input placeholder="Anon (public) Key" value={anonKey} onChange={(e) => setAnonKey(e.target.value)} />
      <input type="email" placeholder="E-Mail" value={email} onChange={(e) => setEmail(e.target.value)} />
      <input type="password" placeholder="Cloud-Passwort" value={password} onChange={(e) => setPassword(e.target.value)} />
      {error && <div className="error">{error}</div>}
      <div className="actions">
        <button type="button" onClick={onCancel}>
          Abbrechen
        </button>
        <span className="grow" />
        <button className="primary" disabled={busy || !url || !anonKey || !email || !password}>
          {busy ? 'Lade…' : 'Laden'}
        </button>
      </div>
    </form>
  );
}
