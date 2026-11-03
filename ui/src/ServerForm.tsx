import { useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose } from './Icons';
import { DEFAULT_PORT, PROTOCOL_LABEL, type KeyInfo, type Protocol, type Server } from './types';

const PROTOCOLS: Protocol[] = ['ssh', 'rdp', 'vnc'];

export function ServerForm({
  initial,
  groups,
  keys,
  onSave,
  onDelete,
  onClose,
  onGoToKeys,
  toast,
}: {
  initial: Server;
  groups: string[];
  keys: KeyInfo[];
  onSave: (s: Server) => Promise<void>;
  onDelete: (s: Server) => Promise<void>;
  onClose: () => void;
  onGoToKeys: () => void;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const [s, setS] = useState<Server>(initial);
  const [tags, setTags] = useState(initial.tags.join(', '));
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const isNew = !initial.id;

  const set = <K extends keyof Server>(k: K, v: Server[K]) => setS((p) => ({ ...p, [k]: v }));

  function setProtocol(p: Protocol) {
    // Port mitziehen, solange er noch der Standard des alten Protokolls ist.
    setS((prev) => ({
      ...prev,
      protocol: p,
      port: prev.port === DEFAULT_PORT[prev.protocol] ? DEFAULT_PORT[p] : prev.port,
    }));
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!s.name.trim() || !s.host.trim()) {
      setError('Name und Host sind Pflicht.');
      return;
    }
    if (s.protocol === 'ssh' && s.auth === 'key' && !s.keyId) {
      setError('Bitte einen Key auswählen (oder auf «Passwort» wechseln).');
      return;
    }
    setBusy(true);
    setError('');
    try {
      await onSave({
        ...s,
        name: s.name.trim(),
        host: s.host.trim(),
        group: s.group.trim(),
        tags: tags.split(',').map((t) => t.trim()).filter(Boolean),
      });
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  async function forgetHostKey() {
    try {
      await api.hostsForget(initial.id);
      toast('Host-Key vergessen – beim nächsten Verbinden wird er neu gespeichert');
    } catch (err) {
      toast((err as Error).message, 'error');
    }
  }

  const userLabel = s.protocol === 'rdp' ? 'Benutzer (DOMÄNE\\benutzer)' : 'Benutzer';
  const usesKey = s.protocol === 'ssh' && s.auth === 'key';

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>{isNew ? 'Neuer Host' : 'Host bearbeiten'}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>

        <div className="seg">
          {PROTOCOLS.map((p) => (
            <button type="button" key={p} className={s.protocol === p ? 'on' : ''} onClick={() => setProtocol(p)}>
              {PROTOCOL_LABEL[p]}
            </button>
          ))}
        </div>

        <label>
          Name
          <input autoFocus value={s.name} onChange={(e) => set('name', e.target.value)} placeholder="web-prod-01" />
        </label>
        <div className="row">
          <label className="grow">
            Host / IP
            <input value={s.host} onChange={(e) => set('host', e.target.value)} placeholder="10.0.0.5 oder server.example.ch" />
          </label>
          <label className="port">
            Port
            <input type="number" value={s.port} onChange={(e) => set('port', Number(e.target.value))} />
          </label>
        </div>

        {s.protocol === 'ssh' && (
          <div className="seg small-seg">
            <button type="button" className={s.auth === 'password' ? 'on' : ''} onClick={() => set('auth', 'password')}>
              Passwort
            </button>
            <button type="button" className={s.auth === 'key' ? 'on' : ''} onClick={() => set('auth', 'key')}>
              SSH-Key
            </button>
          </div>
        )}

        <div className="row">
          <label className="grow">
            {userLabel}
            <input value={s.username} onChange={(e) => set('username', e.target.value)} />
          </label>
          {!usesKey && (
            <label className="grow">
              Passwort
              <input type="password" value={s.password} onChange={(e) => set('password', e.target.value)} />
            </label>
          )}
          {usesKey && (
            <label className="grow">
              Key
              <select value={s.keyId} onChange={(e) => set('keyId', e.target.value)}>
                <option value="">– wählen –</option>
                {keys.map((k) => (
                  <option key={k.id} value={k.id}>
                    {k.name} ({k.kind})
                  </option>
                ))}
              </select>
            </label>
          )}
        </div>
        {usesKey && keys.length === 0 && (
          <div className="hint">
            Du hast noch keinen Key.{' '}
            <button type="button" className="link" onClick={onGoToKeys}>
              Key hinzufügen
            </button>
          </div>
        )}

        <div className="row">
          <label className="grow">
            Gruppe
            <input list="groups" value={s.group} onChange={(e) => set('group', e.target.value)} placeholder="Produktion" />
            <datalist id="groups">
              {groups.map((g) => (
                <option key={g} value={g} />
              ))}
            </datalist>
          </label>
          <label className="grow">
            Tags (Komma)
            <input value={tags} onChange={(e) => setTags(e.target.value)} placeholder="nginx, linux" />
          </label>
        </div>
        <label>
          Notizen
          <textarea rows={2} value={s.notes} onChange={(e) => set('notes', e.target.value)} />
        </label>
        <label className="check">
          <input type="checkbox" checked={s.favorite} onChange={(e) => set('favorite', e.target.checked)} />
          Favorit
        </label>

        {error && <div className="error">{error}</div>}

        <div className="actions">
          {!isNew && (
            <button
              type="button"
              className="danger"
              onClick={() => confirm(`«${s.name}» wirklich löschen?`) && onDelete(initial)}
            >
              Löschen
            </button>
          )}
          {!isNew && s.protocol === 'ssh' && (
            <button type="button" className="ghost" title="Falls der Server neu aufgesetzt wurde und die Host-Key-Warnung kommt" onClick={forgetHostKey}>
              Host-Key zurücksetzen
            </button>
          )}
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Abbrechen
          </button>
          <button className="primary" disabled={busy}>
            Speichern
          </button>
        </div>
      </form>
    </div>
  );
}
