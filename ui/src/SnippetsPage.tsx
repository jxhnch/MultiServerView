import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api } from './bridge';
import { IconClose, IconCode, IconEdit, IconPlus, IconTrash } from './Icons';
import { parseVars } from './snippetVars';
import type { Snippet } from './types';
import { askConfirm } from './Confirm';

export function SnippetsPage({ toast }: { toast: (msg: string, kind?: 'ok' | 'error') => void }) {
  const [items, setItems] = useState<Snippet[]>([]);
  const [editing, setEditing] = useState<Snippet | null>(null);

  const load = useCallback(() => api.snippetsList().then(setItems).catch((e: Error) => toast(e.message, 'error')), [toast]);
  useEffect(() => void load(), [load]);

  async function remove(s: Snippet) {
    if (!(await askConfirm(`Delete the snippet "${s.name}"?`, { title: 'Delete snippet', confirmLabel: 'Delete', danger: true }))) return;
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
          <p className="muted">Save frequent commands once and run them in the terminal with one click (the "Snippets" button at the top right of the terminal).</p>
        </div>
        <button className="primary" onClick={() => setEditing({ id: '', name: '', command: '', autoRun: true })}>
          <IconPlus size={15} /> New snippet
        </button>
      </div>

      {items.length === 0 ? (
        <div className="empty-card">
          <IconCode size={28} />
          <h3>No snippets yet</h3>
          <p>For example "apt update && apt upgrade -y" or "docker ps".</p>
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
              {parseVars(s.command).length > 0 && <span className="chip">Variables</span>}
              {!s.autoRun && <span className="chip">no Enter</span>}
              <button className="ghost icon-only" title="Edit" onClick={() => setEditing(s)}>
                <IconEdit size={16} />
              </button>
              <button className="ghost icon-only danger" title="Delete" onClick={() => void remove(s)}>
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
            toast('Snippet saved');
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
          <h2>{initial.id ? 'Edit snippet' : 'New snippet'}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        <label>
          Name
          <input autoFocus value={s.name} onChange={(e) => setS({ ...s, name: e.target.value })} placeholder="e.g. Update system" />
        </label>
        <label>
          Command
          <textarea rows={5} className="mono" spellCheck={false} value={s.command} onChange={(e) => setS({ ...s, command: e.target.value })} placeholder="docker logs -f {{container}} --tail {{lines=100}}" />
        </label>
        <p className="muted small">
          The app asks for variables in double curly braces when you run the snippet, e.g. <code>{'{{container}}'}</code> or with a default value{' '}
          <code>{'{{lines=100}}'}</code>. Filled in automatically: <code>{'{{host}}'}</code>, <code>{'{{user}}'}</code> and <code>{'{{port}}'}</code> of the host.
        </p>
        <label className="toggle-row">
          <span>Send Enter automatically (run immediately)</span>
          <input type="checkbox" checked={s.autoRun} onChange={(e) => setS({ ...s, autoRun: e.target.checked })} />
        </label>
        {error && <div className="error">{error}</div>}
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={busy || !s.name.trim() || !s.command.trim()}>
            Save
          </button>
        </div>
      </form>
    </div>
  );
}
