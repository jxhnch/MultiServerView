import { useState, type FormEvent } from 'react';

/** Kleiner Eingabedialog (statt window.prompt, das in WebView2 hässlich und unzuverlässig ist). */
export function PromptDialog({
  title,
  label,
  initial = '',
  confirmLabel = 'OK',
  onSubmit,
  onClose,
}: {
  title: string;
  label: string;
  initial?: string;
  confirmLabel?: string;
  onSubmit: (value: string) => void;
  onClose: () => void;
}) {
  const [value, setValue] = useState(initial);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (value.trim()) onSubmit(value.trim());
  };
  return (
    <div className="overlay" onMouseDown={onClose}>
      <form
        className="modal"
        style={{ width: 'min(420px, 92vw)' }}
        onMouseDown={(e) => e.stopPropagation()}
        onSubmit={submit}
        onKeyDown={(e) => e.key === 'Escape' && onClose()}
      >
        <h2>{title}</h2>
        <label>
          {label}
          <input autoFocus value={value} onChange={(e) => setValue(e.target.value)} onFocus={(e) => e.currentTarget.select()} />
        </label>
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Abbrechen
          </button>
          <button className="primary" disabled={!value.trim()}>
            {confirmLabel}
          </button>
        </div>
      </form>
    </div>
  );
}
