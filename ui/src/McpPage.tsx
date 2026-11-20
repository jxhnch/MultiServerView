import { useCallback, useEffect, useRef, useState } from 'react';
import { api, isNative, type McpConfigStatus } from './bridge';
import { IconCheck, IconCopy } from './Icons';
import type { HostSettings } from './types';
import { getUiSettings, onUiSettings, setUiSettings } from './uiSettings';

interface Client {
  id: string;
  label: string;
  intro: string;
  /** Text to paste, built from the address and token. */
  snippet: (url: string, token: string) => string;
  where?: string;
}

const j = (o: unknown) => JSON.stringify(o, null, 2);

const CLIENTS: Client[] = [
  {
    id: 'claude-code',
    label: 'Claude Code',
    intro: 'Run this in any terminal:',
    snippet: (url, token) => `claude mcp add --transport http multiserverview ${url} --header "Authorization: Bearer ${token}"`,
  },
  {
    id: 'codex',
    label: 'Codex (OpenAI)',
    intro: 'Add this to the Codex configuration file. It needs a Codex version with support for HTTP MCP servers.',
    where: '%USERPROFILE%\\.codex\\config.toml',
    snippet: (url, token) => `[mcp_servers.multiserverview]\nurl = "${url}"\nhttp_headers = { Authorization = "Bearer ${token}" }`,
  },
  {
    id: 'cursor',
    label: 'Cursor',
    intro: 'Add this to the Cursor MCP file (or use Settings → MCP → Add new server).',
    where: '%USERPROFILE%\\.cursor\\mcp.json',
    snippet: (url, token) => j({ mcpServers: { multiserverview: { url, headers: { Authorization: `Bearer ${token}` } } } }),
  },
  {
    id: 'vscode',
    label: 'VS Code (Copilot)',
    intro: 'Add this to the MCP file of your workspace (or run "MCP: Add Server" in VS Code).',
    where: '.vscode\\mcp.json',
    snippet: (url, token) => j({ servers: { multiserverview: { type: 'http', url, headers: { Authorization: `Bearer ${token}` } } } }),
  },
  {
    id: 'antigravity',
    label: 'Antigravity',
    intro: 'Add this to the Antigravity MCP config (or open Additional Options (…) → MCP Servers → Manage MCP Servers → View raw config) and click Refresh:',
    where: '%USERPROFILE%\\.gemini\\config\\mcp_config.json (Linux: ~/.gemini/config/mcp_config.json)',
    snippet: (url, token) => j({ mcpServers: { multiserverview: { url, headers: { Authorization: `Bearer ${token}` } } } }),
  },
  {
    id: 'desktop',
    label: 'Claude Desktop and others',
    intro: 'Clients that only start local programs can use the small mcp-remote bridge (needs Node.js). Add this to their MCP configuration:',
    where: 'Claude Desktop: %APPDATA%\\Claude\\claude_desktop_config.json',
    snippet: (url, token) =>
      j({ mcpServers: { multiserverview: { command: 'npx', args: ['-y', 'mcp-remote', url, '--allow-http', '--header', `Authorization:Bearer ${token}`] } } }),
  },
  {
    id: 'other',
    label: 'Any other client',
    intro: 'Any client that speaks MCP over HTTP (Streamable HTTP) works with these values:',
    snippet: (url, token) => `URL:     ${url}\nHeader:  Authorization: Bearer ${token}\nTransport: HTTP (Streamable HTTP), no OAuth`,
  },
];

const NO_WORKSPACE = '(none)';

export function McpPage({ toast, workspaces = [] }: { toast: (msg: string, kind?: 'ok' | 'error') => void; workspaces?: string[] }) {
  const [host, setHost] = useState<HostSettings | null>(null);
  const [status, setStatus] = useState<{ running: boolean; error: string | null } | null>(null);
  const [client, setClient] = useState('claude-code');
  const [copied, setCopied] = useState('');
  const [cfg, setCfg] = useState<McpConfigStatus | null>(null);
  const [allCfg, setAllCfg] = useState<Record<string, McpConfigStatus>>({});
  const [vscodeFolder, setVscodeFolder] = useState('');
  const [portText, setPortText] = useState('');
  const [cfgBusy, setCfgBusy] = useState(false);
  const [cfgNote, setCfgNote] = useState<{ kind: 'ok' | 'error'; text: string } | null>(null);
  const [ui, setUi] = useState(getUiSettings);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => onUiSettings(setUi), []);

  const loadStatus = useCallback(() => void api.mcpStatus().then(setStatus).catch(() => undefined), []);
  const loadAllCfg = useCallback(
    () =>
      void api
        .mcpConfigureAll()
        .then(setAllCfg)
        .catch(() => setAllCfg({})),
    [],
  );

  useEffect(() => {
    void api.settingsGet().then(setHost).catch((e: Error) => toast(e.message, 'error'));
    loadStatus();
  }, [toast, loadStatus]);

  useEffect(() => {
    if (host) setPortText(String(host.mcpPort));
  }, [host?.mcpPort]);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  useEffect(() => setCfgNote(null), [client]);

  useEffect(() => {
    if (!isNative || !host?.mcpEnabled || !host.mcpToken) {
      setAllCfg({});
      return;
    }
    loadAllCfg();
  }, [host?.mcpEnabled, host?.mcpPort, host?.mcpToken, loadAllCfg]);

  useEffect(() => {
    if (!isNative || !host?.mcpEnabled || !host.mcpToken || (client === 'vscode' && !vscodeFolder)) {
      setCfg(null);
      return;
    }
    let cancelled = false;
    void api
      .mcpConfigure(client, 'status', client === 'vscode' ? vscodeFolder : undefined)
      .then((r) => {
        if (!cancelled) setCfg(r);
      })
      .catch(() => {
        if (!cancelled) setCfg(null);
      });
    return () => {
      cancelled = true;
    };
  }, [client, vscodeFolder, host?.mcpEnabled, host?.mcpPort, host?.mcpToken]);

  const saveSettings = (next: HostSettings) => {
    api
      .settingsSave(next)
      .then((r) => {
        setHost(r);
        loadStatus();
      })
      .catch((e: Error) => toast(e.message, 'error'));
  };

  const update = (patch: Partial<HostSettings>, immediate = false) =>
    setHost((h) => {
      if (!h) return h;
      const next = { ...h, ...patch };
      window.clearTimeout(timer.current);
      if (immediate) saveSettings(next);
      else timer.current = window.setTimeout(() => saveSettings(next), 400);
      return next;
    });

  const copy = async (text: string, what: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(what);
      window.setTimeout(() => setCopied(''), 1600);
    } catch {
      toast('Copying is not allowed here. Select the text and copy it by hand.', 'error');
    }
  };

  const url = `http://127.0.0.1:${host?.mcpPort ?? 7676}/mcp`;
  const current = CLIENTS.find((c) => c.id === client) ?? CLIENTS[0];
  const snippet = host ? current.snippet(url, host.mcpToken) : '';
  const where = cfg?.supported && cfg.path ? cfg.path : current.where;
  const outdatedClients = CLIENTS.filter((c) => allCfg[c.id]?.configured && !allCfg[c.id]?.upToDate).map((c) => c.label);

  const pickVscodeFolder = async () => {
    try {
      const r = await api.pickFolder('Select VS Code workspace folder');
      if (r.path) {
        setVscodeFolder(r.path);
        setCfgNote(null);
      }
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  };

  const configure = async (action: 'add' | 'remove') => {
    setCfgBusy(true);
    setCfgNote(null);
    try {
      const r = await api.mcpConfigure(client, action, client === 'vscode' ? vscodeFolder : undefined);
      setCfg(r);
      loadAllCfg();
      const bak = r.backupPath ? ' (backup saved as .bak)' : '';
      const hint =
        client === 'antigravity'
          ? "Click Refresh in Antigravity's MCP Servers panel to load the tools."
          : `Restart or reload ${current.label} to apply the change.`;
      setCfgNote({
        kind: 'ok',
        text: action === 'add' ? `Saved to ${r.path}${bak}. ${hint}` : `Removed from ${r.path}${bak}. ${hint}`,
      });
    } catch (e) {
      setCfgNote({ kind: 'error', text: (e as Error).message });
    } finally {
      setCfgBusy(false);
    }
  };

  return (
    <div className="page settings">
      <div className="page-head">
        <div>
          <h1>AI tools</h1>
          <p className="muted">
            An MCP server lets AI assistants (Claude Code, Codex, Cursor, Antigravity and others) work on your servers through MultiServerView: list hosts, run
            commands, read and write files.
          </p>
        </div>
      </div>

      {!isNative && <div className="notice">The MCP server only exists in the desktop app.</div>}

      {host && (
        <>
          <section className="set-card">
            <header>
              <h2>MCP server</h2>
            </header>
            <div className="set-row">
              <div className="set-label">
                <div className="set-title">Enable</div>
                <div className="muted small">
                  Listens on 127.0.0.1 only and needs the token below. While it is on, any program that knows the token can run commands on your SSH hosts,
                  as long as the vault is unlocked. Only enable it when you want that.
                </div>
              </div>
              <div className="set-control">
                <button
                  type="button"
                  role="switch"
                  aria-checked={host.mcpEnabled}
                  aria-label="Enable MCP server"
                  className={'switch' + (host.mcpEnabled ? ' on' : '')}
                  onClick={() => update({ mcpEnabled: !host.mcpEnabled, ...(host.mcpEnabled ? { mcpToken: '' } : {}) }, true)}
                >
                  <i />
                </button>
              </div>
            </div>
            {host.mcpEnabled && (
              <>
                <div className="set-row">
                  <div className="set-label">
                    <div className="set-title">Status</div>
                  </div>
                  <div className="set-control">
                    {status?.error ? (
                      <span className="error">{status.error}</span>
                    ) : (
                      <span className={'pill ' + (status?.running ? 'connected' : 'connecting')}>{status?.running ? `Running on ${url}` : 'Starting…'}</span>
                    )}
                  </div>
                </div>
                <div className="set-row">
                  <div className="set-label">
                    <div className="set-title">Port</div>
                  </div>
                  <div className="set-control">
                    <input
                      type="number"
                      min={1024}
                      max={65535}
                      value={portText}
                      onChange={(e) => {
                        const v = e.target.value;
                        setPortText(v);
                        const n = Number(v);
                        if (v.trim() !== '' && Number.isInteger(n) && n >= 1024 && n <= 65535) update({ mcpPort: n });
                      }}
                      onBlur={() => setPortText(String(host.mcpPort))}
                    />
                  </div>
                </div>
                <div className="set-row">
                  <div className="set-label">
                    <div className="set-title">Read-only mode</div>
                    <div className="muted small">Allows listing hosts and reading files/folders only. Blocks running commands and writing files.</div>
                  </div>
                  <div className="set-control">
                    <button
                      type="button"
                      role="switch"
                      aria-checked={host.mcpReadOnly}
                      aria-label="Read-only mode"
                      className={'switch' + (host.mcpReadOnly ? ' on' : '')}
                      onClick={() => update({ mcpReadOnly: !host.mcpReadOnly }, true)}
                    >
                      <i />
                    </button>
                  </div>
                </div>
                <div className="set-row">
                  <div className="set-label">
                    <div className="set-title">Allowed workspaces</div>
                    <div className="muted small">Restrict which hosts the AI can see and access over MCP.</div>
                  </div>
                  <div className="set-control" style={{ flexDirection: 'column', alignItems: 'flex-end', gap: 8 }}>
                    <div className="seg slim fit">
                      <button
                        type="button"
                        className={host.mcpAllowedWorkspaces.length === 0 ? 'on' : ''}
                        onClick={() => update({ mcpAllowedWorkspaces: [] }, true)}
                      >
                        All workspaces
                      </button>
                      <button
                        type="button"
                        className={host.mcpAllowedWorkspaces.length > 0 ? 'on' : ''}
                        onClick={() =>
                          host.mcpAllowedWorkspaces.length === 0 &&
                          update({ mcpAllowedWorkspaces: [workspaces[0] ?? NO_WORKSPACE] }, true)
                        }
                      >
                        Selected only
                      </button>
                    </div>
                    {host.mcpAllowedWorkspaces.length > 0 && (
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 10, justifyContent: 'flex-end' }}>
                        {[NO_WORKSPACE, ...workspaces, ...host.mcpAllowedWorkspaces.filter((w) => w !== NO_WORKSPACE && !workspaces.includes(w))].map(
                          (ws) => {
                            const checked = host.mcpAllowedWorkspaces.includes(ws);
                            return (
                              <label key={ws} className="row-inline small" style={{ cursor: 'pointer' }}>
                                <input
                                  type="checkbox"
                                  checked={checked}
                                  onChange={() => {
                                    const next = checked
                                      ? host.mcpAllowedWorkspaces.filter((x) => x !== ws)
                                      : [...host.mcpAllowedWorkspaces, ws];
                                    if (next.length > 0) update({ mcpAllowedWorkspaces: next }, true);
                                  }}
                                />
                                {ws === NO_WORKSPACE ? 'No workspace' : ws}
                              </label>
                            );
                          },
                        )}
                      </div>
                    )}
                  </div>
                </div>
                <div className="set-row">
                  <div className="set-label">
                    <div className="set-title">Token</div>
                    <div className="muted small">Like a password for the AI tool. To get a new one, switch the server off and on again.</div>
                  </div>
                  <div className="set-control">
                    <button disabled={!host.mcpToken} onClick={() => void copy(host.mcpToken, 'token')}>
                      {copied === 'token' ? <IconCheck size={14} /> : <IconCopy size={14} />} {copied === 'token' ? 'Copied' : 'Copy token'}
                    </button>
                  </div>
                </div>
                {ui.autoLockMinutes > 0 && (
                  <>
                    <div className="set-row">
                      <div className="set-label">
                        <div className="set-title">Keep unlocked during MCP activity</div>
                        <div className="muted small">
                          Auto-lock is set to {ui.autoLockMinutes} min (Settings → Security). Enable this to reset the inactivity timer whenever an AI tool calls the
                          MCP server.
                        </div>
                      </div>
                      <div className="set-control">
                        <button
                          type="button"
                          role="switch"
                          aria-checked={ui.mcpKeepUnlocked}
                          aria-label="Keep unlocked during MCP activity"
                          className={'switch' + (ui.mcpKeepUnlocked ? ' on' : '')}
                          onClick={() => setUiSettings({ mcpKeepUnlocked: !ui.mcpKeepUnlocked })}
                        >
                          <i />
                        </button>
                      </div>
                    </div>
                    {!ui.mcpKeepUnlocked && (
                      <div className="notice small" style={{ marginTop: 10 }}>
                        Auto-lock is enabled ({ui.autoLockMinutes} min). Once the vault locks, AI tool calls will fail until you unlock MultiServerView again.
                      </div>
                    )}
                  </>
                )}
              </>
            )}
          </section>

          {host.mcpEnabled && host.mcpToken.length > 0 && (
            <section className="set-card">
              <header>
                <h2>Connect your AI tool</h2>
                <p className="muted small">Pick your tool, copy the text and paste it where it says. Formats can change between versions of the tools, so check their documentation if something does not work.</p>
              </header>
              <div className="mode-switch wrap" style={{ margin: '4px 0 10px' }}>
                {CLIENTS.map((c) => {
                  const outdated = Boolean(allCfg[c.id]?.configured && !allCfg[c.id]?.upToDate);
                  return (
                    <button
                      type="button"
                      key={c.id}
                      className={client === c.id ? 'on' : ''}
                      title={outdated ? 'Saved config uses a different port or token' : undefined}
                      onClick={() => setClient(c.id)}
                    >
                      {c.label}
                      {outdated && (
                        <span
                          style={{
                            display: 'inline-block',
                            width: 6,
                            height: 6,
                            borderRadius: '50%',
                            background: 'var(--warn)',
                            marginLeft: 6,
                            verticalAlign: 'middle',
                          }}
                        />
                      )}
                    </button>
                  );
                })}
              </div>
              {outdatedClients.length > 0 && (
                <div className="notice small" style={{ marginBottom: 10 }}>
                  Outdated saved config: {outdatedClients.join(', ')} — open the tab and click <strong>Update config</strong>.
                </div>
              )}
              <p className="small">{current.intro}</p>
              {where && (
                <p className="muted small">
                  File: <code>{where}</code>
                </p>
              )}
              <pre className="code-block">{snippet}</pre>
              <div className="row">
                <button className="primary" onClick={() => void copy(snippet, 'snippet')}>
                  {copied === 'snippet' ? <IconCheck size={14} /> : <IconCopy size={14} />} {copied === 'snippet' ? 'Copied' : 'Copy'}
                </button>
                {isNative && client === 'vscode' && (
                  <button type="button" disabled={cfgBusy} onClick={() => void pickVscodeFolder()}>
                    {vscodeFolder ? 'Change folder…' : 'Choose workspace folder…'}
                  </button>
                )}
                {isNative && cfg?.supported && (
                  <>
                    {!cfg.configured && (
                      <button type="button" disabled={cfgBusy} onClick={() => void configure('add')}>
                        Add to config
                      </button>
                    )}
                    {cfg.configured && !cfg.upToDate && (
                      <button type="button" disabled={cfgBusy} onClick={() => void configure('add')}>
                        Update config
                      </button>
                    )}
                    {cfg.configured && (
                      <button type="button" className="danger" disabled={cfgBusy} onClick={() => void configure('remove')}>
                        Remove
                      </button>
                    )}
                  </>
                )}
              </div>
              {cfgNote ? (
                <div className={cfgNote.kind === 'ok' ? 'notice ok small' : 'error small'} style={{ marginTop: 10 }}>
                  {cfgNote.text}
                </div>
              ) : cfg?.error ? (
                <div className="error small" style={{ marginTop: 10 }}>
                  {cfg.error}
                </div>
              ) : cfg?.configured && !cfg.upToDate ? (
                <p className="muted small" style={{ marginTop: 8 }}>
                  The saved config uses a different port or token. Click <strong>Update config</strong> to refresh it.
                </p>
              ) : cfg?.configured && cfg.upToDate ? (
                <p className="muted small" style={{ marginTop: 8 }}>
                  Configured in <code>{cfg.path}</code>.
                </p>
              ) : null}
              <p className="muted small" style={{ marginTop: 12 }}>
                Tools the AI gets:{' '}
                {host.mcpReadOnly ? (
                  <>
                    <code>list_hosts</code>, <code>read_file</code>, <code>list_directory</code> (read-only mode)
                  </>
                ) : (
                  <>
                    <code>list_hosts</code>, <code>run_command</code>, <code>read_file</code>, <code>write_file</code>, <code>list_directory</code>
                  </>
                )}
                . SSH hosts only; MultiServerView itself never sends your saved passwords or keys to the AI.
              </p>
            </section>
          )}
        </>
      )}
    </div>
  );
}
