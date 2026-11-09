import { useMemo, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconEye, IconCheck, IconFolder } from './Icons';
import { HostTile } from './HostTile';
import { DEFAULT_PORT, PROTOCOL_LABEL, type Identity, type KeyInfo, type Protocol, type Server } from './types';
import { askConfirm } from './Confirm';

const PROTOCOLS: Protocol[] = ['ssh', 'rdp', 'vnc', 'teamviewer'];

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
    </label>
  );
}

/**
 * Detail panel for a host (docked on the right or as a drawer above a session).
 * "Connect" saves if needed and connects right away.
 */
export function HostPanel({
  initial,
  drawer,
  groups,
  workspaces,
  keys,
  identities,
  jumpCandidates,
  onSave,
  onConnect,
  onSftp,
  onDelete,
  onClose,
  onGoToKeys,
  toast,
}: {
  initial: Server;
  drawer?: boolean;
  groups: string[];
  workspaces: string[];
  keys: KeyInfo[];
  identities: Identity[];
  jumpCandidates: Server[];
  onSave: (s: Server) => Promise<Server>;
  onConnect: (s: Server) => Promise<void>;
  onSftp: (s: Server) => Promise<void>;
  onDelete: (s: Server) => Promise<void>;
  onClose: () => void;
  onGoToKeys: () => void;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const [s, setS] = useState<Server>(initial);
  const [tags, setTags] = useState(initial.tags.join(', '));
  const [showPw, setShowPw] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const isNew = !initial.id;
  // Focus the first missing field (e.g. the password after quick connect with "ssh root@host").
  const focusField: 'host' | 'user' | 'pw' = !initial.host ? 'host' : !initial.username ? 'user' : 'pw';

  const set = <K extends keyof Server>(k: K, v: Server[K]) => setS((p) => ({ ...p, [k]: v }));
  const build = (): Server => ({
    ...s,
    name: s.name.trim() || s.host.trim(),
    host: s.host.trim(),
    workspace: s.workspace.trim(),
    group: s.group.trim(),
    tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
  });
  const dirty = useMemo(
    () => isNew || JSON.stringify(build()) !== JSON.stringify(initial),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [s, tags],
  );

  function setProtocol(p: Protocol) {
    setS((prev) => ({
      ...prev,
      protocol: p,
      port: prev.port === DEFAULT_PORT[prev.protocol] ? DEFAULT_PORT[p] : prev.port,
    }));
  }

  function validate(): string {
    if (!s.host.trim()) return 'Please enter an address.';
    if (!s.identityId && s.protocol === 'ssh' && s.auth === 'key' && !s.keyId) return 'Please choose a key (or switch to "Password").';
    return '';
  }

  async function run(action: 'save' | 'connect' | 'sftp') {
    const problem = validate();
    if (problem) {
      setError(problem);
      return;
    }
    setBusy(true);
    setError('');
    try {
      const data = build();
      if (action === 'connect') await onConnect(data);
      else if (action === 'sftp') await onSftp(data);
      else {
        await onSave(data);
        toast('Saved');
      }
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function forgetHostKey() {
    try {
      await api.hostsForget(initial.id);
      toast('Host key forgotten – it will be stored again on the next connect');
    } catch (err) {
      toast((err as Error).message, 'error');
    }
  }

  const usesKey = s.protocol === 'ssh' && s.auth === 'key';
  const submit = (e: FormEvent) => {
    e.preventDefault();
    void run('connect');
  };

  return (
    <aside className={'panel' + (drawer ? ' drawer' : '')} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
      <div className="panel-head">
        <h2>{isNew ? 'New host' : 'Host details'}</h2>
        {!isNew && (
          <button className="ghost icon-only" title="Save" disabled={!dirty || busy} onClick={() => void run('save')}>
            <IconCheck size={17} />
          </button>
        )}
        <button className="ghost icon-only" title="Close" onClick={onClose}>
          <IconClose size={17} />
        </button>
      </div>

      <form className="panel-body" onSubmit={submit} id="host-form">
        <div className="seg">
          {PROTOCOLS.map((p) => (
            <button type="button" key={p} className={s.protocol === p ? 'on' : ''} onClick={() => setProtocol(p)}>
              {PROTOCOL_LABEL[p]}
            </button>
          ))}
        </div>

        <div className="group-card">
          <h3>Address</h3>
          <div className="addr-row">
            <HostTile server={s} size="md" />
            <input
              autoFocus={isNew && focusField === 'host'}
              value={s.host}
              onChange={(e) => set('host', e.target.value)}
              placeholder={s.protocol === 'teamviewer' ? 'TeamViewer ID, e.g. 123 456 789' : '192.168.1.10 or server.example.com'}
            />
          </div>
        </div>

        <div className="group-card">
          <h3>General</h3>
          <Field label="Name">
            <input value={s.name} onChange={(e) => set('name', e.target.value)} placeholder="e.g. web-prod-01" />
          </Field>
          <Field label="Workspace">
            <input list="workspaces" value={s.workspace} onChange={(e) => set('workspace', e.target.value)} placeholder="e.g. Smith (teacher, customer …)" />
          </Field>
          <datalist id="workspaces">
            {workspaces.map((w) => (
              <option key={w} value={w} />
            ))}
          </datalist>
          <Field label="Group">
            <input list="groups" value={s.group} onChange={(e) => set('group', e.target.value)} placeholder="e.g. Production" />
          </Field>
          <datalist id="groups">
            {groups.map((g) => (
              <option key={g} value={g} />
            ))}
          </datalist>
          <Field label="Tags">
            <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="separate with commas" />
          </Field>
          <label className="toggle-row">
            <span>Favorite</span>
            <input type="checkbox" checked={s.favorite} onChange={(e) => set('favorite', e.target.checked)} />
          </label>
        </div>

        {s.protocol !== 'teamviewer' && (
        <div className="group-card">
          <div className="port-row">
            {PROTOCOL_LABEL[s.protocol]} on
            <input type="number" value={s.port} onChange={(e) => set('port', Number(e.target.value))} />
            Port
          </div>
        </div>
        )}

        {s.protocol !== 'teamviewer' && (
        <div className="group-card">
          <h3>Connection</h3>
          <Field label="Via jump host">
            <select value={s.jumpHostId} onChange={(e) => set('jumpHostId', e.target.value)}>
              <option value="">Connect directly</option>
              {jumpCandidates
                .filter((j) => j.id !== s.id && j.protocol === 'ssh')
                .map((j) => (
                  <option key={j.id} value={j.id}>
                    {j.name} ({j.host})
                  </option>
                ))}
            </select>
          </Field>
          {s.jumpHostId && <p className="hint">The connection goes to the jump host first and from there to this server (also multi-hop if the jump host has one itself).</p>}
        </div>
        )}

        <div className="group-card">
          <h3>Credentials</h3>
          {identities.length > 0 && (
            <Field label="Identity (shared credentials)">
              <select value={s.identityId} onChange={(e) => set('identityId', e.target.value)}>
                <option value="">Own credentials</option>
                {identities.map((i) => (
                  <option key={i.id} value={i.id}>
                    {i.name}
                  </option>
                ))}
              </select>
            </Field>
          )}
          {s.identityId ? (
            <p className="hint">Username and password/key come from the identity "{identities.find((i) => i.id === s.identityId)?.name ?? '?'}".</p>
          ) : (
            <>
              {s.protocol === 'ssh' && (
                <div className="seg slim">
                  <button type="button" className={s.auth === 'password' ? 'on' : ''} onClick={() => set('auth', 'password')}>
                    Password
                  </button>
                  <button type="button" className={s.auth === 'key' ? 'on' : ''} onClick={() => set('auth', 'key')}>
                    SSH-Key
                  </button>
                </div>
              )}
              {s.protocol !== 'teamviewer' && (
                <Field label={s.protocol === 'rdp' ? 'Username (DOMAIN\\user)' : 'Username'}>
                  <input autoFocus={isNew && focusField === 'user'} value={s.username} onChange={(e) => set('username', e.target.value)} />
                </Field>
              )}
              {!usesKey ? (
                <Field label="Password">
                  <div className="pw-wrap">
                    <input
                      autoFocus={isNew && focusField === 'pw'}
                      type={showPw ? 'text' : 'password'}
                      value={s.password}
                      onChange={(e) => set('password', e.target.value)}
                    />
                    <button type="button" className="ghost icon-only" title="Show / hide" onClick={() => setShowPw((v) => !v)}>
                      <IconEye size={16} />
                    </button>
                  </div>
                </Field>
              ) : (
                <>
                  <Field label="Key">
                    <select value={s.keyId} onChange={(e) => set('keyId', e.target.value)}>
                      <option value="">Choose key…</option>
                      {keys.map((k) => (
                        <option key={k.id} value={k.id}>
                          {k.name} ({k.kind})
                        </option>
                      ))}
                    </select>
                  </Field>
                  {keys.length === 0 && (
                    <div className="hint">
                      No key yet.{' '}
                      <button type="button" className="link" onClick={onGoToKeys}>
                        Add key
                      </button>
                    </div>
                  )}
                </>
              )}
            </>
          )}
        </div>

        <div className="group-card">
          <h3>Notes</h3>
          <textarea rows={3} value={s.notes} onChange={(e) => set('notes', e.target.value)} placeholder="Optional" />
        </div>

        {error && <div className="error">{error}</div>}
      </form>

      <div className="panel-foot">
        <div className="row">
          <button className="primary big grow" form="host-form" disabled={busy}>
            {busy ? 'One moment…' : isNew ? 'Save & connect' : 'Connect'}
          </button>
          {s.protocol === 'ssh' && (
            <button type="button" className="big" title="Manage files of this host" disabled={busy} onClick={() => void run('sftp')}>
              <IconFolder size={16} /> SFTP
            </button>
          )}
        </div>
        {!isNew && (
          <div className="links">
            {s.protocol === 'ssh' ? (
              <button className="link" onClick={forgetHostKey}>
                Reset host key
              </button>
            ) : (
              <span />
            )}
            <button className="link danger" onClick={() => void askConfirm(`Really delete "${initial.name}"?`, { title: 'Delete host', confirmLabel: 'Delete', danger: true }).then((ok) => { if (ok) void onDelete(initial); })}>
              Delete
            </button>
          </div>
        )}
      </div>
    </aside>
  );
}
