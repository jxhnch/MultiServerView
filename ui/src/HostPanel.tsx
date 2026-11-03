import { useMemo, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconEye, IconCheck, IconFolder } from './Icons';
import { HostTile } from './HostTile';
import { DEFAULT_PORT, PROTOCOL_LABEL, type Identity, type KeyInfo, type Protocol, type Server } from './types';

const PROTOCOLS: Protocol[] = ['ssh', 'rdp', 'vnc'];

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
    </label>
  );
}

/**
 * Detail-Panel für einen Host (rechts angedockt oder als Schublade über einer Session).
 * "Connect" speichert bei Bedarf und verbindet sofort.
 */
export function HostPanel({
  initial,
  drawer,
  groups,
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
  // Fokus aufs erste fehlende Feld (z. B. nach Schnellverbinden mit "ssh root@host" -> Passwort).
  const focusField: 'host' | 'user' | 'pw' = !initial.host ? 'host' : !initial.username ? 'user' : 'pw';

  const set = <K extends keyof Server>(k: K, v: Server[K]) => setS((p) => ({ ...p, [k]: v }));
  const build = (): Server => ({
    ...s,
    name: s.name.trim() || s.host.trim(),
    host: s.host.trim(),
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
    if (!s.host.trim()) return 'Bitte eine Adresse eingeben.';
    if (!s.identityId && s.protocol === 'ssh' && s.auth === 'key' && !s.keyId) return 'Bitte einen Key auswählen (oder auf «Passwort» wechseln).';
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
        toast('Gespeichert');
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
      toast('Host-Key vergessen – wird beim nächsten Verbinden neu gespeichert');
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
        <h2>{isNew ? 'Neuer Host' : 'Host Details'}</h2>
        {!isNew && (
          <button className="ghost icon-only" title="Speichern" disabled={!dirty || busy} onClick={() => void run('save')}>
            <IconCheck size={17} />
          </button>
        )}
        <button className="ghost icon-only" title="Schliessen" onClick={onClose}>
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
          <h3>Adresse</h3>
          <div className="addr-row">
            <HostTile server={s} size="md" />
            <input
              autoFocus={isNew && focusField === 'host'}
              value={s.host}
              onChange={(e) => set('host', e.target.value)}
              placeholder="192.168.1.10 oder server.example.ch"
            />
          </div>
        </div>

        <div className="group-card">
          <h3>Allgemein</h3>
          <Field label="Name">
            <input value={s.name} onChange={(e) => set('name', e.target.value)} placeholder="z. B. web-prod-01" />
          </Field>
          <Field label="Gruppe">
            <input list="groups" value={s.group} onChange={(e) => set('group', e.target.value)} placeholder="z. B. Produktion" />
          </Field>
          <datalist id="groups">
            {groups.map((g) => (
              <option key={g} value={g} />
            ))}
          </datalist>
          <Field label="Tags">
            <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="mit Komma trennen" />
          </Field>
          <label className="toggle-row">
            <span>Favorit</span>
            <input type="checkbox" checked={s.favorite} onChange={(e) => set('favorite', e.target.checked)} />
          </label>
        </div>

        <div className="group-card">
          <div className="port-row">
            {PROTOCOL_LABEL[s.protocol]} auf
            <input type="number" value={s.port} onChange={(e) => set('port', Number(e.target.value))} />
            Port
          </div>
        </div>

        <div className="group-card">
          <h3>Verbindung</h3>
          <Field label="Über Jump Host">
            <select value={s.jumpHostId} onChange={(e) => set('jumpHostId', e.target.value)}>
              <option value="">Direkt verbinden</option>
              {jumpCandidates
                .filter((j) => j.id !== s.id && j.protocol === 'ssh')
                .map((j) => (
                  <option key={j.id} value={j.id}>
                    {j.name} ({j.host})
                  </option>
                ))}
            </select>
          </Field>
          {s.jumpHostId && <p className="hint">Die Verbindung läuft zuerst zum Jump Host und von dort weiter zu diesem Server (auch mehrstufig, wenn der Jump Host selbst einen hat).</p>}
        </div>

        <div className="group-card">
          <h3>Zugangsdaten</h3>
          {identities.length > 0 && (
            <Field label="Identity (gemeinsame Zugangsdaten)">
              <select value={s.identityId} onChange={(e) => set('identityId', e.target.value)}>
                <option value="">Eigene Zugangsdaten</option>
                {identities.map((i) => (
                  <option key={i.id} value={i.id}>
                    {i.name}
                  </option>
                ))}
              </select>
            </Field>
          )}
          {s.identityId ? (
            <p className="hint">Benutzer und Passwort/Key kommen aus der Identity «{identities.find((i) => i.id === s.identityId)?.name ?? '?'}».</p>
          ) : (
            <>
              {s.protocol === 'ssh' && (
                <div className="seg slim">
                  <button type="button" className={s.auth === 'password' ? 'on' : ''} onClick={() => set('auth', 'password')}>
                    Passwort
                  </button>
                  <button type="button" className={s.auth === 'key' ? 'on' : ''} onClick={() => set('auth', 'key')}>
                    SSH-Key
                  </button>
                </div>
              )}
              <Field label={s.protocol === 'rdp' ? 'Benutzer (DOMÄNE\\benutzer)' : 'Benutzer'}>
                <input autoFocus={isNew && focusField === 'user'} value={s.username} onChange={(e) => set('username', e.target.value)} />
              </Field>
              {!usesKey ? (
                <Field label="Passwort">
                  <div className="pw-wrap">
                    <input
                      autoFocus={isNew && focusField === 'pw'}
                      type={showPw ? 'text' : 'password'}
                      value={s.password}
                      onChange={(e) => set('password', e.target.value)}
                    />
                    <button type="button" className="ghost icon-only" title="Anzeigen / verbergen" onClick={() => setShowPw((v) => !v)}>
                      <IconEye size={16} />
                    </button>
                  </div>
                </Field>
              ) : (
                <>
                  <Field label="Key">
                    <select value={s.keyId} onChange={(e) => set('keyId', e.target.value)}>
                      <option value="">Key wählen…</option>
                      {keys.map((k) => (
                        <option key={k.id} value={k.id}>
                          {k.name} ({k.kind})
                        </option>
                      ))}
                    </select>
                  </Field>
                  {keys.length === 0 && (
                    <div className="hint">
                      Noch kein Key vorhanden.{' '}
                      <button type="button" className="link" onClick={onGoToKeys}>
                        Key hinzufügen
                      </button>
                    </div>
                  )}
                </>
              )}
            </>
          )}
        </div>

        <div className="group-card">
          <h3>Notizen</h3>
          <textarea rows={3} value={s.notes} onChange={(e) => set('notes', e.target.value)} placeholder="Optional" />
        </div>

        {error && <div className="error">{error}</div>}
      </form>

      <div className="panel-foot">
        <div className="row">
          <button className="primary big grow" form="host-form" disabled={busy}>
            {busy ? 'Moment…' : isNew ? 'Speichern & verbinden' : 'Connect'}
          </button>
          {s.protocol === 'ssh' && (
            <button type="button" className="big" title="Dateien dieses Hosts verwalten" disabled={busy} onClick={() => void run('sftp')}>
              <IconFolder size={16} /> SFTP
            </button>
          )}
        </div>
        {!isNew && (
          <div className="links">
            {s.protocol === 'ssh' ? (
              <button className="link" onClick={forgetHostKey}>
                Host-Key zurücksetzen
              </button>
            ) : (
              <span />
            )}
            <button className="link danger" onClick={() => confirm(`«${initial.name}» wirklich löschen?`) && void onDelete(initial)}>
              Löschen
            </button>
          </div>
        )}
      </div>
    </aside>
  );
}
