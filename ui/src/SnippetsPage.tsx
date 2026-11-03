import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconCode, IconEdit, IconPlus, IconTrash } from './Icons';
import type { Snippet } from './types';

export function SnippetsPage({ toast }: { toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [items, setItems] = useState<Snippet[]>([]);
  const [editing, setEditing] = useState<Snippet | null>(null);

  const load = useCallback(() => api.snippetsList().then(setItems).catch((e: Error) => toast(e.message, 'error')), [toast]);
  useEffect(() => void load(), [load]);

  async function remove(s: Snippet) {
    if (!confirm(`Snippet «${s.name}» löschen?`)) return;
    try {
      await api.snippetsDelete(s.id);
      await load();
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Snippets</h1>
          <p className="muted">Häufige Befehle einmal speichern und im Terminal mit einem Klick ausführen (Button «Snippets» oben rechts im Terminal).</p>
        </div>
        <button className="primary" onClick={() => setEditing({ id: '', name: '', command: '', autoRun: true })}>
          <IconPlus size={15} /> Neues Snippet
        </button>
      </div>

      {items.length === 0 ? (
        <div className="empty-card">
          <IconCode size={28} />
          <h3>Noch keine Snippets</h3>
          <p>Zum Beispiel «apt update && apt upgrade -y» oder «docker ps».</p>
        </div>
      ) : (
        <div className="list">
          {items.map((s) => (
            <div className="list-row" key={s.id}>
              <span className="list-ico">
                <IconCode size={18} />
              </span>
              <div className="grow">
                <div className="list-title">{s.name}</div>
                <div className="muted small mono ellipsis">{s.command.split('\n')[0]}{s.command.includes('\n') ? ' …' : ''}</div>
              </div>
              {!s.autoRun && <span className="chip">ohne Enter</span>}
              <button className="ghost icon-only" title="Bearbeiten" onClick={() => setEditing(s)}>
                <IconEdit size={16} />
              </button>
              <button className="ghost icon-only danger" title="Löschen" onClick={() => void remove(s)}>
                <IconTrash size={16} />
              </button>
            </div>
          ))}
        </div>
      )}

      {editing && (
        <SnippetForm
          initial={editing}
          onClose={() => setEditing(null)}
          onSaved={async () => {
            setEditing(null);
            await load();
            toast('Snippet gespeichert');
          }}
        />
      )}
    </div>
  );
}

function SnippetForm({ initial, onClose, onSaved }: { initial: Snippet; onClose: () => void; onSaved: () => Promise<void> }) {
  const [s, setS] = useState(initial);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError('');
    try {
      await api.snippetsSave({ id: s.id || undefined, name: s.name.trim(), command: s.command, autoRun: s.autoRun });
      await onSaved();
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>{initial.id ? 'Snippet bearbeiten' : 'Neues Snippet'}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <label>
          Name
          <input autoFocus value={s.name} onChange={(e) => setS({ ...s, name: e.target.value })} placeholder="z. B. System aktualisieren" />
        </label>
        <label>
          Befehl
          <textarea rows={5} className="mono" spellCheck={false} value={s.command} onChange={(e) => setS({ ...s, command: e.target.value })} placeholder="apt update && apt upgrade -y" />
        </label>
        <label className="toggle-row">
          <span>Enter automatisch senden (sofort ausführen)</span>
          <input type="checkbox" checked={s.autoRun} onChange={(e) => setS({ ...s, autoRun: e.target.checked })} />
        </label>
        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Abbrechen
          </button>
          <button className="primary" disabled={busy || !s.name.trim() || !s.command.trim()}>
            Speichern
          </button>
        </div>
      </form>
    </div>
  );
}
