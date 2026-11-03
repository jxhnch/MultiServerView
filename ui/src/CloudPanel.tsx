import { useEffect, useState } from 'react';
import { api, isNative, type SyncConfigInput } from './bridge';
import { IconCheck, IconCloud, IconCopy, IconRefresh } from './Icons';
import { SUPABASE_SQL } from './supabaseSql';
import type { SyncInfo, SyncStatus } from './types';

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
  const [url, setUrl] = useState('');
  const [anonKey, setAnonKey] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    if (!info) return;
    setUrl(info.url);
    setAnonKey(info.anonKey);
    setEmail(info.email);
  }, [info]);

  const cfg = (): SyncConfigInput => ({ url: url.trim(), anonKey: anonKey.trim(), email: email.trim(), password });
  const ready = url.trim() && anonKey.trim() && email.trim() && (password || info?.hasPassword);

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
      toast('Cloud verbunden und synchronisiert');
    });

  const test = () =>
    run('test', async () => {
      await api.syncTest({ ...cfg(), password: password || '' });
      toast('Verbindung funktioniert');
    });

  const signUp = () =>
    run('signup', async () => {
      const r = await api.syncSignUp(cfg());
      toast(r.signedIn ? 'Konto erstellt' : 'Konto erstellt – bitte E-Mail bestätigen, dann «Verbinden»', 'ok');
    });

  const now = () =>
    run('now', async () => {
      await api.syncNow();
      await onChanged();
    });

  const disconnect = () =>
    run('disc', async () => {
      if (!confirm('Cloud trennen? Dein lokaler Vault bleibt erhalten, die Cloud-Kopie auch.')) return;
      await api.syncDisconnect();
      await onChanged();
      toast('Cloud getrennt');
    });

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Cloud &amp; Sync</h1>
          <p className="muted">
            Dein Vault wird <b>Ende-zu-Ende verschlüsselt</b>: Die Cloud (Supabase) bekommt nur einen unlesbaren Blob.
            Dein Master-Passwort verlässt dieses Gerät nie. Die Zugangsdaten unten gehören zum Supabase-Konto – das ist
            nicht das Master-Passwort.
          </p>
        </div>
        {info?.configured && (
          <span className={`pill ${status.state === 'error' ? 'error' : status.state === 'syncing' ? 'connecting' : 'connected'}`}>
            <span className="dot-sm" />
            {status.state === 'off' ? 'Verbunden' : status.message || 'Verbunden'}
          </span>
        )}
      </div>

      {!isNative && <div className="notice">Im Browser-Modus ist die Cloud nicht verfügbar – das geht nur in der Windows-App.</div>}

      <div className="card-form">
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
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} placeholder="du@example.com" />
          </label>
          <label className="grow">
            Cloud-Passwort
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              placeholder={info?.hasPassword ? '•••••••• (unverändert)' : 'Passwort für das Supabase-Konto'}
            />
          </label>
        </div>

        {error && <div className="error">{error}</div>}

        <div className="actions">
          <button onClick={signUp} disabled={!!busy || !url || !anonKey || !email || !password}>
            Konto erstellen
          </button>
          <button onClick={test} disabled={!!busy || !ready}>
            Testen
          </button>
          <span className="grow" />
          {info?.configured && (
            <button className="ghost danger" onClick={disconnect} disabled={!!busy}>
              Trennen
            </button>
          )}
          <button className="primary" onClick={save} disabled={!!busy || !ready}>
            <IconCloud size={15} /> {busy === 'save' ? 'Verbinde…' : info?.configured ? 'Speichern & synchronisieren' : 'Verbinden'}
          </button>
        </div>
      </div>

      {info?.configured && (
        <div className="card-form row center-v">
          <div className="grow">
            <div className="list-title">Jetzt synchronisieren</div>
            <div className="muted small">
              {info.lastSync ? `Zuletzt: ${new Date(info.lastSync).toLocaleString('de-CH')}` : 'Noch nicht synchronisiert'} · läuft
              automatisch nach jeder Änderung
            </div>
          </div>
          <button onClick={now} disabled={!!busy}>
            <IconRefresh size={15} /> Sync
          </button>
        </div>
      )}

      <Setup />
    </div>
  );
}

function Setup() {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(SUPABASE_SQL);
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      /* Zwischenablage nicht erlaubt */
    }
  };

  return (
    <details className="card-form setup">
      <summary>Einrichtung in 3 Minuten (einmalig)</summary>
      <ol>
        <li>
          Auf <b>supabase.com</b> ein kostenloses Projekt erstellen.
        </li>
        <li>
          Im Projekt: <b>SQL Editor</b> → den Code unten einfügen → <b>Run</b>.
        </li>
        <li>
          <b>Project Settings → API</b>: <i>Project URL</i> und den <i>anon public</i> Key oben eintragen.
        </li>
        <li>
          Optional unter <b>Authentication → Providers → Email</b> «Confirm email» ausschalten, dann geht die Anmeldung
          sofort. Danach hier «Konto erstellen» und «Verbinden».
        </li>
        <li>
          Auf dem zweiten PC: beim ersten Start «Neues Gerät? Aus Cloud laden» und danach mit dem gleichen Master-Passwort
          entsperren.
        </li>
      </ol>
      <div className="code-head">
        <span className="muted small">SQL</span>
        <button className="ghost" onClick={copy}>
          {copied ? <IconCheck size={14} /> : <IconCopy size={14} />} {copied ? 'Kopiert' : 'Kopieren'}
        </button>
      </div>
      <pre className="code">{SUPABASE_SQL}</pre>
    </details>
  );
}
