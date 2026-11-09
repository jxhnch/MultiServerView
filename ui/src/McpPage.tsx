import { useCallback, useEffect, useRef, useState } from 'react';
import { api, isNative } from './bridge';
import { IconCheck, IconCopy } from './Icons';
import type { HostSettings } from './types';

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
    id: 'gemini',
    label: 'Gemini CLI',
    intro: 'Add this to the Gemini CLI settings:',
    where: '%USERPROFILE%\\.gemini\\settings.json',
    snippet: (url, token) => j({ mcpServers: { multiserverview: { httpUrl: url, headers: { Authorization: `Bearer ${token}` } } } }),
  },
  {
    id: 'desktop',
    label: 'Claude Desktop and others',
    intro: 'Clients that only start local programs can use the small mcp-remote bridge (needs Node.js). Add this to their MCP configuration:',
    where: 'Claude Desktop: %APPDATA%\\Claude\\claude_desktop_config.json',
    snippet: (url, token) =>
      j({ mcpServers: { multiserverview: { command: 'npx', args: ['-y', 'mcp-remote', url, '--header', `Authorization:Bearer ${token}`] } } }),
  },
  {
    id: 'other',
    label: 'Any other client',
    intro: 'Any client that speaks MCP over HTTP (Streamable HTTP) works with these values:',
    snippet: (url, token) => `URL:     ${url}\nHeader:  Authorization: Bearer ${token}\nTransport: HTTP (Streamable HTTP), no OAuth`,
  },
];

export function McpPage({ toast }: { toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [host, setHost] = useState<HostSettings | null>(null);
  const [status, setStatus] = useState<{ running: boolean; error: string | null } | null>(null);
  const [client, setClient] = useState('claude-code');
  const [copied, setCopied] = useState('');
  const timer = useRef<number | undefined>(undefined);

  const loadStatus = useCallback(() => void api.mcpStatus().then(setStatus).catch(() => undefined), []);

  useEffect(() => {
    void api.settingsGet().then(setHost).catch((e: Error) => toast(e.message, 'error'));
    loadStatus();
  }, [toast, loadStatus]);

  const update = (patch: Partial<HostSettings>) =>
    setHost((h) => {
      if (!h) return h;
      const next = { ...h, ...patch };
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => {
        api
          .settingsSave(next)
          .then((r) => {
            setHost(r);
            loadStatus();
          })
          .catch((e: Error) => toast(e.message, 'error'));
      }, 400);
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

  return (
    <div className="page settings">
      <div className="page-head">
        <div>
          <h1>AI tools</h1>
          <p className="muted">
            An MCP server lets AI assistants (Claude Code, Codex, Cursor, Gemini and others) work on your servers through MultiServerView: list hosts, run
            commands, read and write files.
          </p>
        </div>
      </div>

      {!isNative && <div className="notice">The MCP server only exists in the Windows app.</div>}

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
                  onClick={() => update({ mcpEnabled: !host.mcpEnabled })}
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
                    <input type="number" min={1024} max={65535} value={host.mcpPort} onChange={(e) => update({ mcpPort: Number(e.target.value) })} />
                  </div>
                </div>
                <div className="set-row">
                  <div className="set-label">
                    <div className="set-title">Token</div>
                    <div className="muted small">Like a password for the AI tool. To get a new one, switch the server off and on again.</div>
                  </div>
                  <div className="set-control">
                    <button onClick={() => void copy(host.mcpToken, 'token')}>
                      {copied === 'token' ? <IconCheck size={14} /> : <IconCopy size={14} />} {copied === 'token' ? 'Copied' : 'Copy token'}
                    </button>
                  </div>
                </div>
              </>
            )}
          </section>

          {host.mcpEnabled && (
            <section className="set-card">
              <header>
                <h2>Connect your AI tool</h2>
                <p className="muted small">Pick your tool, copy the text and paste it where it says. Formats can change between versions of the tools, so check their documentation if something does not work.</p>
              </header>
              <div className="mode-switch wrap" style={{ margin: '4px 0 10px' }}>
                {CLIENTS.map((c) => (
                  <button type="button" key={c.id} className={client === c.id ? 'on' : ''} onClick={() => setClient(c.id)}>
                    {c.label}
                  </button>
                ))}
              </div>
              <p className="small">{current.intro}</p>
              {current.where && (
                <p className="muted small">
                  File: <code>{current.where}</code>
                </p>
              )}
              <pre className="code-block">{snippet}</pre>
              <div className="row">
                <button className="primary" onClick={() => void copy(snippet, 'snippet')}>
                  {copied === 'snippet' ? <IconCheck size={14} /> : <IconCopy size={14} />} {copied === 'snippet' ? 'Copied' : 'Copy'}
                </button>
              </div>
              <p className="muted small" style={{ marginTop: 12 }}>
                Tools the AI gets: <code>list_hosts</code>, <code>run_command</code>, <code>read_file</code>, <code>write_file</code>, <code>list_directory</code>. SSH
                hosts only; MultiServerView itself never sends your saved passwords or keys to the AI.
              </p>
            </section>
          )}
        </>
      )}
    </div>
  );
}
