import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { api, isNative } from './bridge';
import { IconClose, IconLock } from './Icons';
import { FONTS, THEMES, findFont, findTheme, getTermSettings, onTermSettings, setTermSettings, type TermSettings } from './terminalThemes';
import { getUiSettings, onUiSettings, setUiSettings, type UiSettings } from './uiSettings';
import type { HostSettings } from './types';

function useTerm(): TermSettings {
  const [s, setS] = useState(getTermSettings());
  useEffect(() => onTermSettings(setS), []);
  return s;
}
function useUi(): UiSettings {
  const [s, setS] = useState(getUiSettings());
  useEffect(() => onUiSettings(setS), []);
  return s;
}

function Card({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <section className="set-card">
      <header>
        <h2>{title}</h2>
        {subtitle && <p className="muted small">{subtitle}</p>}
      </header>
      {children}
    </section>
  );
}

function Row({ title, hint, children }: { title: string; hint?: string; children: ReactNode }) {
  return (
    <div className="set-row">
      <div className="set-label">
        <div className="set-title">{title}</div>
        {hint && <div className="muted small">{hint}</div>}
      </div>
      <div className="set-control">{children}</div>
    </div>
  );
}

function Switch({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <button type="button" role="switch" aria-checked={checked} aria-label={label} className={'switch' + (checked ? ' on' : '')} onClick={() => onChange(!checked)}>
      <i />
    </button>
  );
}

function Seg<T extends string | number>({ value, options, onChange }: { value: T; options: { value: T; label: string }[]; onChange: (v: T) => void }) {
  return (
    <div className="seg slim fit">
      {options.map((o) => (
        <button type="button" key={String(o.value)} className={o.value === value ? 'on' : ''} onClick={() => onChange(o.value)}>
          {o.label}
        </button>
      ))}
    </div>
  );
}

const SHORTCUTS: [string, string][] = [
  ['Ctrl + K', 'Schnell verbinden (nicht im Terminal)'],
  ['Ctrl + Umschalt + K', 'Schnell verbinden (überall, auch im Terminal)'],
  ['Ctrl + Tab / Ctrl + Umschalt + Tab', 'Nächster / vorheriger Tab'],
  ['Ctrl + 1 … 9', 'Tab direkt wählen (1 = Hosts)'],
  ['Ctrl + Umschalt + W', 'Tab schliessen (Mittelklick geht auch)'],
  ['Ctrl + Umschalt + N', 'Neuer Host'],
  ['Ctrl + Umschalt + D', 'Zweite Session zum selben Host'],
  ['Ctrl + Umschalt + C / V', 'Terminal: kopieren / einfügen (Rechtsklick geht auch)'],
  ['Umschalt + Enter / Umschalt + Klick', 'Beim Verbinden eine neue Session öffnen'],
  ['Entf / F2', 'SFTP: Auswahl löschen / umbenennen'],
];

export function SettingsPage({ onLock, toast }: { onLock: () => void; toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const ui = useUi();
  const term = useTerm();
  const [host, setHost] = useState<HostSettings | null>(null);
  const [saved, setSaved] = useState(false);
  const [info, setInfo] = useState<{ path: string; version: string } | null>(null);
  const [pwModal, setPwModal] = useState(false);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => {
    void api.settingsGet().then(setHost).catch((e: Error) => toast(e.message, 'error'));
    void api.vaultInfo().then(setInfo).catch(() => undefined);
  }, [toast]);

  const updateHost = useCallback(
    (patch: Partial<HostSettings>) => {
      setHost((h) => {
        if (!h) return h;
        const next = { ...h, ...patch };
        window.clearTimeout(timer.current);
        timer.current = window.setTimeout(() => {
          api
            .settingsSave(next)
            .then((r) => {
              setHost(r);
              setSaved(true);
              window.setTimeout(() => setSaved(false), 1600);
            })
            .catch((e: Error) => toast(e.message, 'error'));
        }, 400);
        return next;
      });
    },
    [toast],
  );

  const theme = findTheme(term.theme);
  const font = findFont(term.font);

  async function exportBackup() {
    try {
      const r = await api.vaultExport();
      if (r.saved) toast('Backup gespeichert: ' + r.path);
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <div className="page settings">
      <div className="page-head">
        <div>
          <h1>Einstellungen</h1>
          <p className="muted">Änderungen gelten sofort. Neue Verbindungs-Einstellungen wirken beim nächsten Verbinden.</p>
        </div>
        <span className={'saved-note' + (saved ? ' on' : '')}>✓ Gespeichert</span>
      </div>

      <Card title="Darstellung">
        <Row title="Design" hint="Gilt für die ganze App ausser dem Terminal (hat sein eigenes Farbschema).">
          <Seg value={ui.theme} onChange={(v) => setUiSettings({ theme: v })} options={[{ value: 'light', label: 'Hell' }, { value: 'dark', label: 'Dunkel' }]} />
        </Row>
      </Card>

      <Card title="Terminal" subtitle="Gilt für alle SSH-Terminals, auch in der geteilten Ansicht.">
        <div className="term-preview" style={{ background: theme.bg, color: theme.fg, fontFamily: font.css, fontSize: term.fontSize }}>
          <div>
            <span style={{ color: theme.ansi[2] }}>demo@server</span>
            <span style={{ color: theme.fg }}>:</span>
            <span style={{ color: theme.ansi[4] }}>~</span>$ ls -la
          </div>
          <div>
            <span style={{ color: theme.ansi[4] }}>drwxr-xr-x</span> projekte&nbsp; <span style={{ color: theme.ansi[3] }}>backups</span>&nbsp; <span style={{ color: theme.ansi[1] }}>fehler.log</span>
          </div>
          <div>
            <span style={{ color: theme.ansi[2] }}>demo@server</span>:<span style={{ color: theme.ansi[4] }}>~</span>$ <span className={'cursor ' + term.cursorStyle} style={{ background: theme.cursor }} />
          </div>
        </div>
        <div className="theme-grid wide">
          {THEMES.map((t) => (
            <button key={t.id} className={'theme-card light' + (term.theme === t.id ? ' on' : '')} onClick={() => setTermSettings({ theme: t.id })}>
              <span className="theme-preview" style={{ background: t.bg }}>
                <i style={{ background: t.fg, width: '70%' }} />
                <i style={{ background: t.ansi[2], width: '45%' }} />
                <i style={{ background: t.ansi[4], width: '58%' }} />
                <i style={{ background: t.ansi[1], width: '30%' }} />
              </span>
              <span className="theme-name">{t.name}</span>
            </button>
          ))}
        </div>
        <Row title="Schriftart">
          <select value={term.font} onChange={(e) => setTermSettings({ font: e.target.value })}>
            {FONTS.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name}
              </option>
            ))}
          </select>
        </Row>
        <Row title="Schriftgrösse">
          <div className="stepper light">
            <button onClick={() => setTermSettings({ fontSize: term.fontSize - 1 })} aria-label="Kleiner">
              −
            </button>
            <span>{term.fontSize}</span>
            <button onClick={() => setTermSettings({ fontSize: term.fontSize + 1 })} aria-label="Grösser">
              +
            </button>
          </div>
        </Row>
        <Row title="Cursor">
          <Seg
            value={term.cursorStyle}
            onChange={(v) => setTermSettings({ cursorStyle: v })}
            options={[{ value: 'bar', label: 'Balken' }, { value: 'block', label: 'Block' }, { value: 'underline', label: 'Unterstrich' }]}
          />
        </Row>
        <Row title="Cursor blinkt">
          <Switch checked={term.cursorBlink} onChange={(v) => setTermSettings({ cursorBlink: v })} label="Cursor blinkt" />
        </Row>
        <Row title="Verlauf (Scrollback)" hint="Wie viele Zeilen du zurückscrollen kannst. Mehr braucht mehr Speicher.">
          <select value={term.scrollback} onChange={(e) => setTermSettings({ scrollback: Number(e.target.value) })}>
            {[1000, 5000, 10000, 50000].map((n) => (
              <option key={n} value={n}>
                {n.toLocaleString('de-CH')} Zeilen
              </option>
            ))}
          </select>
        </Row>
        <Row title="Beim Markieren kopieren" hint="Markierter Text landet sofort in der Zwischenablage (wie bei PuTTY).">
          <Switch checked={term.copyOnSelect} onChange={(v) => setTermSettings({ copyOnSelect: v })} label="Beim Markieren kopieren" />
        </Row>
      </Card>

      <Card title="SSH und SFTP">
        {!host ? (
          <div className="muted pad-sm">Lade…</div>
        ) : (
          <>
            <Row title="Keep-Alive" hint="Alle x Sekunden ein Lebenszeichen senden, damit Firewalls die Verbindung nicht kappen. 0 = aus.">
              <div className="num-field">
                <input type="number" min={0} max={600} value={host.sshKeepAliveSeconds} onChange={(e) => updateHost({ sshKeepAliveSeconds: Number(e.target.value) })} />
                <span className="muted small">Sekunden</span>
              </div>
            </Row>
            <Row title="Zeitlimit beim Verbinden" hint="Wie lange auf den Server gewartet wird, bevor abgebrochen wird.">
              <div className="num-field">
                <input type="number" min={3} max={120} value={host.sshConnectTimeoutSeconds} onChange={(e) => updateHost({ sshConnectTimeoutSeconds: Number(e.target.value) })} />
                <span className="muted small">Sekunden</span>
              </div>
            </Row>
          </>
        )}
      </Card>

      <Card title="Remote Desktop (RDP)" subtitle={isNative ? undefined : 'Nur in der Windows-App wirksam.'}>
        {!host ? (
          <div className="muted pad-sm">Lade…</div>
        ) : (
          <>
            <Row title="Farbtiefe" hint="Niedriger spart Bandbreite bei langsamen Verbindungen.">
              <Seg value={host.rdpColorDepth} onChange={(v) => updateHost({ rdpColorDepth: v })} options={[{ value: 16, label: '16 Bit' }, { value: 24, label: '24 Bit' }, { value: 32, label: '32 Bit' }]} />
            </Row>
            <Row title="Zwischenablage teilen" hint="Kopieren und Einfügen zwischen diesem PC und dem Remote-PC.">
              <Switch checked={host.rdpClipboard} onChange={(v) => updateHost({ rdpClipboard: v })} label="Zwischenablage teilen" />
            </Row>
            <Row title="Lokale Laufwerke einbinden" hint="Deine Laufwerke erscheinen im Remote-PC (Dateien per Explorer kopieren). Nur bei vertrauenswürdigen Servern einschalten.">
              <Switch checked={host.rdpDrives} onChange={(v) => updateHost({ rdpDrives: v })} label="Lokale Laufwerke einbinden" />
            </Row>
            <Row title="Auflösung automatisch anpassen" hint="Der Remote-Desktop passt sich der Fenstergrösse an (statt nur zu skalieren).">
              <Switch checked={host.rdpDynamicResize} onChange={(v) => updateHost({ rdpDynamicResize: v })} label="Auflösung automatisch anpassen" />
            </Row>
            <Row title="Bei Zertifikatsproblemen warnen" hint="Sicherer, aber bei selbstsignierten Zertifikaten (üblich in Firmennetzen) lästig.">
              <Switch checked={host.rdpWarnOnCertificate} onChange={(v) => updateHost({ rdpWarnOnCertificate: v })} label="Bei Zertifikatsproblemen warnen" />
            </Row>
          </>
        )}
      </Card>

      <Card title="VNC">
        <Row title="An Fenster anpassen" hint="Skaliert den Bildschirm des Servers auf die Fenstergrösse.">
          <Switch checked={ui.vncScale} onChange={(v) => setUiSettings({ vncScale: v })} label="An Fenster anpassen" />
        </Row>
        <Row title="Bildqualität" hint="0 = wenig Daten, 9 = beste Qualität.">
          <div className="range-field">
            <input type="range" min={0} max={9} value={ui.vncQuality} onChange={(e) => setUiSettings({ vncQuality: Number(e.target.value) })} />
            <span className="small">{ui.vncQuality}</span>
          </div>
        </Row>
      </Card>

      <Card title="Sicherheit">
        <Row title="Automatisch sperren" hint="Sperrt den Vault nach dieser Zeit ohne Mausbewegung oder Tastatur. Offene Sessions werden dabei getrennt.">
          <select value={ui.autoLockMinutes} onChange={(e) => setUiSettings({ autoLockMinutes: Number(e.target.value) })}>
            <option value={0}>Nie</option>
            {[5, 10, 15, 30, 60].map((m) => (
              <option key={m} value={m}>
                nach {m} Minuten
              </option>
            ))}
          </select>
        </Row>
        <Row title="Master-Passwort" hint="Ändert den Schlüssel des Vaults. Bei aktiver Cloud wird auch der Cloud-Vault aktualisiert.">
          <button onClick={() => setPwModal(true)}>
            <IconLock size={15} /> Ändern…
          </button>
        </Row>
        <Row title="Jetzt sperren">
          <button onClick={onLock}>Vault sperren</button>
        </Row>
      </Card>

      <Card title="Daten und Backup">
        <Row title="Vault-Datei" hint="Verschlüsselt (AES-256). Ohne dein Master-Passwort nicht lesbar.">
          <div className="path-field">
            <code className="selectable ellipsis" title={info?.path}>
              {info?.path ?? '…'}
            </code>
            {isNative && (
              <button className="ghost" onClick={() => void api.vaultReveal()}>
                Im Explorer zeigen
              </button>
            )}
          </div>
        </Row>
        <Row title="Backup exportieren" hint="Speichert eine Kopie der verschlüsselten Vault-Datei, z. B. auf einen USB-Stick.">
          <button onClick={() => void exportBackup()}>Backup speichern…</button>
        </Row>
      </Card>

      <Card title="Tastenkürzel" subtitle="Im Terminal gehören einfache Strg-Kombinationen der Shell, darum laufen App-Befehle mit Umschalt.">
        <div className="kbd-table">
          {SHORTCUTS.map(([k, d]) => (
            <div className="kbd-row" key={k}>
              <span>
                {k.split(' + ').map((part, i, arr) => (
                  <span key={part + i}>
                    <kbd>{part}</kbd>
                    {i < arr.length - 1 ? ' ' : ''}
                  </span>
                ))}
              </span>
              <span className="muted">{d}</span>
            </div>
          ))}
        </div>
        <p className="muted small" style={{ marginTop: 8 }}>
          In einer RDP-Session gehen alle Tasten an den Remote-PC. Dann mit der Maus auf einen anderen Tab klicken.
        </p>
      </Card>

      <Card title="Über MultiServerView">
        <Row title="Version">
          <span>{info?.version || '–'}</span>
        </Row>
      </Card>

      {pwModal && (
        <ChangePassword
          onClose={() => setPwModal(false)}
          onDone={(cloud, message) => {
            setPwModal(false);
            if (cloud === 'updated') toast('Passwort geändert und Cloud aktualisiert. Auf anderen Geräten: Cloud → «Neues Master-Passwort übernehmen».');
            else if (cloud === 'failed') toast('Passwort geändert, aber die Cloud konnte nicht aktualisiert werden: ' + message, 'error');
            else toast('Master-Passwort geändert');
          }}
        />
      )}
    </div>
  );
}

function ChangePassword({ onClose, onDone }: { onClose: () => void; onDone: (cloud: 'none' | 'updated' | 'failed', message?: string) => void }) {
  const [oldPw, setOldPw] = useState('');
  const [newPw, setNewPw] = useState('');
  const [newPw2, setNewPw2] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (newPw !== newPw2) {
      setError('Die neuen Passwörter sind nicht gleich.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      const r = await api.vaultChangePassword(oldPw, newPw);
      onDone(r.cloud, r.message);
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>Master-Passwort ändern</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <label>
          Aktuelles Passwort
          <input type="password" autoFocus value={oldPw} onChange={(e) => setOldPw(e.target.value)} />
        </label>
        <label>
          Neues Passwort (mindestens 8 Zeichen)
          <input type="password" value={newPw} onChange={(e) => setNewPw(e.target.value)} />
        </label>
        <label>
          Neues Passwort wiederholen
          <input type="password" value={newPw2} onChange={(e) => setNewPw2(e.target.value)} />
        </label>
        <p className="hint">Es gibt keine Wiederherstellung. Wer das Passwort vergisst, kommt nicht mehr an den Vault.</p>
        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Abbrechen
          </button>
          <button className="primary" disabled={busy || !oldPw || newPw.length < 8}>
            Ändern
          </button>
        </div>
      </form>
    </div>
  );
}
