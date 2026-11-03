import { useState, type FormEvent } from 'react';
import { IconClose } from './Icons';
import { fillVars, loadLastValues, saveLastValues, type SnippetVar } from './snippetVars';
import type { Snippet } from './types';

/** Asks for the values of the variables of a snippet ("{{container}}", "{{n=50}}") and returns the finished command. */
export function SnippetVarsDialog({
  snippet,
  vars,
  builtins,
  onSubmit,
  onClose,
}: {
  snippet: Snippet;
  vars: SnippetVar[];
  builtins: Record<string, string>;
  onSubmit: (command: string) => void;
  onClose: () => void;
}) {
  const [values, setValues] = useState<Record<string, string>>(() => {
    const last = loadLastValues(snippet.id);
    return Object.fromEntries(vars.map((v) => [v.name, last[v.name] ?? v.defaultValue]));
  });

  const preview = fillVars(snippet.command, values, builtins);

  function submit(e: FormEvent) {
    e.preventDefault();
    saveLastValues(snippet.id, values);
    onSubmit(preview);
  }

  return (
    <div className="overlay" onMouseDown={onClose}>
      <form className="modal" onMouseDown={(e) => e.stopPropagation()} onSubmit={submit} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2>{snippet.name}</h2>
          <button type="button" className="ghost icon-only" onClick={onClose}>
            <IconClose size={16} />
          </button>
        </div>
        {vars.map((v, i) => (
          <label key={v.name}>
            {v.name}
            <input
              autoFocus={i === 0}
              value={values[v.name] ?? ''}
              onChange={(e) => setValues({ ...values, [v.name]: e.target.value })}
              onFocus={(e) => e.currentTarget.select()}
            />
          </label>
        ))}
        <label>
          Command
          <textarea className="mono" rows={Math.min(5, preview.split('\n').length + 1)} readOnly spellCheck={false} value={preview} />
        </label>
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary">{snippet.autoRun ? 'Run' : 'Insert'}</button>
        </div>
      </form>
    </div>
  );
}
