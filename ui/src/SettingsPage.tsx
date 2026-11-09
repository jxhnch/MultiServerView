import { useCallback, useEffect, useRef, useState, type FormEvent, type ReactNode } from 'react';
import { api, isNative } from './bridge';
import { ClearVaultDialog } from './ClearVault';
import { IconClose, IconLock, IconRefresh } from './Icons';
import { offerUpdate } from './updates';
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
  ['Ctrl + K', 'Quick connect (not inside a terminal)'],
  ['Ctrl + Shift + K', 'Quick connect (everywhere, also inside a terminal)'],
  ['Ctrl + Tab / Ctrl + Shift + Tab', 'Next / previous tab'],
  ['Ctrl + 1 … 9', 'Jump to a tab (1 = Hosts)'],
  ['Ctrl + Shift + W', 'Close tab (middle-click works too)'],
  ['Ctrl + Shift + N', 'New host'],
  ['Ctrl + Shift + D', 'Second session to the same host'],
  ['Ctrl + Shift + C / V', 'Terminal: copy / paste (right-click works too)'],
  ['Shift + Enter / Shift + Click', 'Open a new session when connecting'],
  ['Del / F2', 'SFTP: delete / rename selection'],
];

export function SettingsPage({ onLock, onCleared, toast }: { onLock: () => void; onCleared: () => Promise<void>; toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const ui = useUi();
  const term = useTerm();
  const [host, setHost] = useState<HostSettings | null>(null);
  const [saved, setSaved] = useState(false);
  const [info, setInfo] = useState<{ path: string; version: string } | null>(null);
  const [pwModal, setPwModal] = useState(false);
  const [clearModal, setClearModal] = useState(false);
  const [checking, setChecking] = useState(false);
  const [mcp, setMcp] = useState<{ running: boolean; error: string | null } | null>(null);
  const mcpUrl = `http://127.0.0.1:${host?.mcpPort ?? 7676}/mcp`;
  const mcpCommand = `claude mcp add --transport http multiserverview ${mcpUrl} --header "Authorization: Bearer ${host?.mcpToken ?? ''}"`;
  const copyText = async (text: string, done: string) => {
    try {
      await navigator.clipboard.writeText(text);
      toast(done);
    } catch {
      toast('Copying is not allowed here. Select the text and copy it by hand.', 'error');
    }
  };
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => {
    void api.settingsGet().then(setHost).catch((e: Error) => toast(e.message, 'error'));
    void api.mcpStatus().then(setMcp).catch(() => undefined);
    void api.vaultInfo().then(setInfo).catch(() => undefined);
  }, [toast]);

  const checkNow = async () => {
    setChecking(true);
    try {
      const u = await api.updateCheck();
      if (!u.available) toast(`You have the latest version (${u.current}).`);
      else await offerUpdate(u, toast);
    } catch (e) {
      toast((e as Error).message, 'error');
    } finally {
      setChecking(false);
    }
  };

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
              void api.mcpStatus().then(setMcp).catch(() => undefined);
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
      if (r.saved) toast('Backup saved: ' + r.path);
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <div className="page settings">
      <div className="page-head">
        <div>
          <h1>Settings</h1>
          <p className="muted">Changes apply immediately. New connection settings take effect the next time you connect.</p>
        </div>
        <span className={'saved-note' + (saved ? ' on' : '')}>✓ Saved</span>
      </div>

      <Card title="Appearance">
        <Row title="Design" hint="Applies to the whole app except the terminal (it has its own color scheme).">
          <Seg value={ui.theme} onChange={(v) => setUiSettings({ theme: v })} options={[{ value: 'light', label: 'Light' }, { value: 'dark', label: 'Dark' }]} />
        </Row>
      </Card>

      <Card title="Terminal" subtitle="Applies to all SSH terminals, including the split view.">
        <div className="term-preview" style={{ background: theme.bg, color: theme.fg, fontFamily: font.css, fontSize: term.fontSize }}>
          <div>
            <span style={{ color: theme.ansi[2] }}>demo@server</span>
            <span style={{ color: theme.fg }}>:</span>
            <span style={{ color: theme.ansi[4] }}>~</span>$ ls -la
          </div>
          <div>
            <span style={{ color: theme.ansi[4] }}>drwxr-xr-x</span> projects&nbsp; <span style={{ color: theme.ansi[3] }}>backups</span>&nbsp; <span style={{ color: theme.ansi[1] }}>error.log</span>
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
        <Row title="Font">
          <select value={term.font} onChange={(e) => setTermSettings({ font: e.target.value })}>
            {FONTS.map((f) => (
              <option key={f.id} value={f.id}>
                {f.name}
              </option>
            ))}
          </select>
        </Row>
        <Row title="Font size">
          <div className="stepper light">
            <button onClick={() => setTermSettings({ fontSize: term.fontSize - 1 })} aria-label="Smaller">
              −
            </button>
            <span>{term.fontSize}</span>
            <button onClick={() => setTermSettings({ fontSize: term.fontSize + 1 })} aria-label="Larger">
              +
            </button>
          </div>
        </Row>
        <Row title="Cursor">
          <Seg
            value={term.cursorStyle}
            onChange={(v) => setTermSettings({ cursorStyle: v })}
            options={[{ value: 'bar', label: 'Bar' }, { value: 'block', label: 'Block' }, { value: 'underline', label: 'Underline' }]}
          />
        </Row>
        <Row title="Cursor blinks">
          <Switch checked={term.cursorBlink} onChange={(v) => setTermSettings({ cursorBlink: v })} label="Cursor blinks" />
        </Row>
        <Row title="History (scrollback)" hint="How many lines you can scroll back. More lines need more memory.">
          <select value={term.scrollback} onChange={(e) => setTermSettings({ scrollback: Number(e.target.value) })}>
            {[1000, 5000, 10000, 50000].map((n) => (
              <option key={n} value={n}>
                {n.toLocaleString('en-US')} lines
              </option>
            ))}
          </select>
        </Row>
        <Row title="Copy on select" hint="Selected text goes straight to the clipboard (like PuTTY).">
          <Switch checked={term.copyOnSelect} onChange={(v) => setTermSettings({ copyOnSelect: v })} label="Copy on select" />
        </Row>
      </Card>

      <Card title="SSH and SFTP">
        {!host ? (
          <div className="muted pad-sm">Loading…</div>
        ) : (
          <>
            <Row title="Keep-Alive" hint="Send a signal every x seconds so firewalls do not drop the connection. 0 = off.">
              <div className="num-field">
                <input type="number" min={0} max={600} value={host.sshKeepAliveSeconds} onChange={(e) => updateHost({ sshKeepAliveSeconds: Number(e.target.value) })} />
                <span className="muted small">seconds</span>
              </div>
            </Row>
            <Row title="Connection timeout" hint="How long to wait for the server before giving up.">
              <div className="num-field">
                <input type="number" min={3} max={120} value={host.sshConnectTimeoutSeconds} onChange={(e) => updateHost({ sshConnectTimeoutSeconds: Number(e.target.value) })} />
                <span className="muted small">seconds</span>
              </div>
            </Row>
          </>
        )}
      </Card>

      <Card title="Remote Desktop (RDP)" subtitle={isNative ? undefined : 'Only effective in the Windows app.'}>
        {!host ? (
          <div className="muted pad-sm">Loading…</div>
        ) : (
          <>
            <Row title="Color depth" hint="Lower saves bandwidth on slow connections.">
              <Seg value={host.rdpColorDepth} onChange={(v) => updateHost({ rdpColorDepth: v })} options={[{ value: 16, label: '16 Bit' }, { value: 24, label: '24 Bit' }, { value: 32, label: '32 Bit' }]} />
            </Row>
            <Row title="Share clipboard" hint="Copy and paste between this PC and the remote PC.">
              <Switch checked={host.rdpClipboard} onChange={(v) => updateHost({ rdpClipboard: v })} label="Share clipboard" />
            </Row>
            <Row title="Redirect local drives" hint="Your drives appear on the remote PC (copy files with Explorer). Only enable for trusted servers.">
              <Switch checked={host.rdpDrives} onChange={(v) => updateHost({ rdpDrives: v })} label="Redirect local drives" />
            </Row>
            <Row title="Adjust resolution automatically" hint="The remote desktop follows the window size (instead of just scaling).">
              <Switch checked={host.rdpDynamicResize} onChange={(v) => updateHost({ rdpDynamicResize: v })} label="Adjust resolution automatically" />
            </Row>
            <Row title="Warn on certificate problems" hint="Safer, but annoying with self-signed certificates (common in company networks).">
              <Switch checked={host.rdpWarnOnCertificate} onChange={(v) => updateHost({ rdpWarnOnCertificate: v })} label="Warn on certificate problems" />
            </Row>
          </>
        )}
      </Card>

      <Card title="VNC">
        <Row title="Fit to window" hint="Scales the server screen to the window size.">
          <Switch checked={ui.vncScale} onChange={(v) => setUiSettings({ vncScale: v })} label="Fit to window" />
        </Row>
        <Row title="Image quality" hint="0 = little data, 9 = best quality.">
          <div className="range-field">
            <input type="range" min={0} max={9} value={ui.vncQuality} onChange={(e) => setUiSettings({ vncQuality: Number(e.target.value) })} />
            <span className="small">{ui.vncQuality}</span>
          </div>
        </Row>
      </Card>

      <Card title="Security">
        <Row title="Auto-lock" hint="Locks the vault after this time without mouse or keyboard activity. Open sessions are disconnected.">
          <select value={ui.autoLockMinutes} onChange={(e) => setUiSettings({ autoLockMinutes: Number(e.target.value) })}>
            <option value={0}>Never</option>
            {[5, 10, 15, 30, 60].map((m) => (
              <option key={m} value={m}>
                after {m} minutes
              </option>
            ))}
          </select>
        </Row>
        <Row title="Master password" hint="Changes the key of the vault. With sync enabled, the synced vault is updated as well.">
          <button onClick={() => setPwModal(true)}>
            <IconLock size={15} /> Change…
          </button>
        </Row>
        <Row title="Lock now">
          <button onClick={onLock}>Lock vault</button>
        </Row>
      </Card>

      {host && (
        <Card title="AI tools (MCP server)" subtitle="Lets AI tools such as Claude Code work on your servers through MultiServerView.">
          <Row title="Enable MCP server" hint="Listens on 127.0.0.1 only and needs the token below. While it is on, any program that knows the token can run commands on your SSH hosts as long as the vault is unlocked.">
            <input type="checkbox" checked={host.mcpEnabled} onChange={(e) => updateHost({ mcpEnabled: e.target.checked })} />
          </Row>
          {host.mcpEnabled && (
            <>
              {mcp?.error && <div className="error">{mcp.error}</div>}
              <Row title="Port">
                <input type="number" min={1024} max={65535} value={host.mcpPort} onChange={(e) => updateHost({ mcpPort: Number(e.target.value) })} />
              </Row>
              <p className="muted small" style={{ marginTop: 8 }}>
                Add it to Claude Code with this command (the token is part of it):
              </p>
              <pre className="code-block">{mcpCommand}</pre>
              <div className="row">
                <button onClick={() => void copyText(mcpCommand, 'Command copied')}>Copy command</button>
                <button onClick={() => void copyText(host.mcpToken, 'Token copied')}>Copy token</button>
              </div>
              <p className="muted small" style={{ marginTop: 8 }}>
                Tools: <code>list_hosts</code>, <code>run_command</code>, <code>read_file</code>, <code>write_file</code>, <code>list_directory</code>.
                Other MCP clients: URL <code>{mcpUrl}</code>, header <code>Authorization: Bearer &lt;token&gt;</code>.
              </p>
            </>
          )}
        </Card>
      )}

      <Card title="Data and backup">
        <Row title="Vault file" hint="Encrypted (AES-256). Unreadable without your master password.">
          <div className="path-field">
            <code className="selectable ellipsis" title={info?.path}>
              {info?.path ?? '…'}
            </code>
            {isNative && (
              <button className="ghost" onClick={() => void api.vaultReveal()}>
                Show in Explorer
              </button>
            )}
          </div>
        </Row>
        <Row title="Export backup" hint="Saves a copy of the encrypted vault file, e.g. to a USB stick.">
          <button onClick={() => void exportBackup()}>Save backup…</button>
        </Row>
        <Row title="Clear vault" hint="Deletes all hosts, keys, identities, snippets and connections. The master password stays. Requires several confirmations.">
          <button className="danger" onClick={() => setClearModal(true)}>
            Clear vault…
          </button>
        </Row>
      </Card>

      <Card title="Keyboard shortcuts" subtitle="Inside a terminal, plain Ctrl combinations belong to the shell, so app commands use Shift.">
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
          In an RDP session, all keys go to the remote PC. Click another tab with the mouse instead.
        </p>
      </Card>

      <Card title="About MultiServerView">
        <Row title="Version">
          <span>{info?.version || '–'}</span>
        </Row>
        <Row title="Updates" hint="Asks GitHub (github.com/jxhnch/MultiServerView) for a newer release. Nothing else is sent.">
          <button disabled={checking} onClick={() => void checkNow()}>
            <IconRefresh size={15} /> {checking ? 'Checking…' : 'Check for updates'}
          </button>
        </Row>
        <Row title="Check at startup">
          <input type="checkbox" checked={ui.checkUpdates} onChange={(e) => setUiSettings({ checkUpdates: e.target.checked })} />
        </Row>
      </Card>

      {clearModal && (
        <ClearVaultDialog
          onClose={() => setClearModal(false)}
          toast={toast}
          onCleared={async () => {
            setClearModal(false);
            await onCleared();
            toast('Vault cleared');
          }}
        />
      )}
      {pwModal && (
        <ChangePassword
          onClose={() => setPwModal(false)}
          onDone={(cloud, message) => {
            setPwModal(false);
            if (cloud === 'updated') toast('Password changed and synced vault updated. On your other devices: Sync → "Adopt new master password".');
            else if (cloud === 'failed') toast('Password changed, but the synced vault could not be updated: ' + message, 'error');
            else toast('Master password changed');
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
      setError('The new passwords do not match.');
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
          <h2>Change master password</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <label>
          Current password
          <input type="password" autoFocus value={oldPw} onChange={(e) => setOldPw(e.target.value)} />
        </label>
        <label>
          New password (at least 8 characters)
          <input type="password" value={newPw} onChange={(e) => setNewPw(e.target.value)} />
        </label>
        <label>
          Repeat new password
          <input type="password" value={newPw2} onChange={(e) => setNewPw2(e.target.value)} />
        </label>
        <p className="hint">There is no recovery. If you forget the password, the vault can no longer be opened.</p>
        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={busy || !oldPw || newPw.length < 8}>
            Change
          </button>
        </div>
      </form>
    </div>
  );
}
