import { useState, type FormEvent } from 'react';
import { DEFAULT_PORT, type Protocol, type Server } from './types';

const PROTOCOLS: Protocol[] = ['ssh', 'rdp', 'vnc'];

export function ServerForm({
  initial,
  groups,
  onSave,
  onDelete,
  onClose,
}: {
  initial: Server;
  groups: string[];
  onSave: (s: Server) => Promise<void>;
  onDelete: (s: Server) => Promise<void>;
  onClose: () => void;
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

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form
        className="modal"
        onMouseDown={(e) => e.stopPropagation()}
        onSubmit={submit}
        onKeyDown={(e) => e.key === 'Escape' && onClose()}
      >
        <h2>{isNew ? 'Neuer Server' : 'Server bearbeiten'}</h2>

        <div className="seg">
          {PROTOCOLS.map((p) => (
            <button type="button" key={p} className={s.protocol === p ? 'on' : ''} onClick={() => setProtocol(p)}>
              {p.toUpperCase()}
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
            <input value={s.host} onChange={(e) => set('host', e.target.value)} placeholder="10.0.0.5" />
          </label>
          <label className="port">
            Port
            <input
              type="number"
              value={s.port}
              onChange={(e) => set('port', Number(e.target.value))}
            />
          </label>
        </div>
        <div className="row">
          <label className="grow">
            Benutzer
            <input value={s.username} onChange={(e) => set('username', e.target.value)} />
          </label>
          <label className="grow">
            Passwort
            <input type="password" value={s.password} onChange={(e) => set('password', e.target.value)} />
          </label>
        </div>
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
              onClick={() => confirm(`"${s.name}" wirklich löschen?`) && onDelete(initial)}
            >
              Löschen
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
