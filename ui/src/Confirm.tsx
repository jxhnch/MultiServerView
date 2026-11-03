import { useEffect, useRef, useState } from 'react';

export interface ConfirmOptions {
  title?: string;
  confirmLabel?: string;
  /** Highlighted in red (e.g. when deleting); the focus is then on "Cancel". */
  danger?: boolean;
}

interface Request extends ConfirmOptions {
  message: string;
  resolve: (ok: boolean) => void;
}

let push: ((r: Request) => void) | null = null;

/**
 * Replaces window.confirm: its own dialog in the design of the app instead of the gray browser window.
 * Returns true if confirmed. Without a mounted <ConfirmHost/> it falls back to window.confirm.
 */
export function askConfirm(message: string, options: ConfirmOptions = {}): Promise<boolean> {
  return new Promise((resolve) => {
    if (!push) {
      resolve(window.confirm(message));
      return;
    }
    push({ message, ...options, resolve });
  });
}

/** Mount once in the app. `onOpenChange` reports whether a dialog is open (the RDP windows have to make room then). */
export function ConfirmHost({ onOpenChange }: { onOpenChange?: (open: boolean) => void }) {
  const [queue, setQueue] = useState<Request[]>([]);
  const current = queue[0];
  const cancelRef = useRef<HTMLButtonElement>(null);
  const okRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    push = (r) => setQueue((q) => [...q, r]);
    return () => {
      push = null;
    };
  }, []);

  useEffect(() => {
    onOpenChange?.(!!current);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [!!current]);

  useEffect(() => {
    if (current) (current.danger ? cancelRef : okRef).current?.focus();
  }, [current]);

  if (!current) return null;

  const answer = (ok: boolean) => {
    current.resolve(ok);
    setQueue((q) => q.slice(1));
  };

  return (
    <div className="overlay confirm-overlay" onMouseDown={() => answer(false)}>
      <div
        className="modal"
        role="alertdialog"
        aria-modal="true"
        style={{ width: 'min(420px, 92vw)' }}
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.stopPropagation();
            answer(false);
          }
        }}
      >
        <h2>{current.title ?? 'Please confirm'}</h2>
        <p className="confirm-text">{current.message}</p>
        <div className="actions">
          <span className="grow" />
          <button ref={cancelRef} type="button" onClick={() => answer(false)}>
            Cancel
          </button>
          <button ref={okRef} type="button" className={'primary' + (current.danger ? ' danger' : '')} onClick={() => answer(true)}>
            {current.confirmLabel ?? 'OK'}
          </button>
        </div>
      </div>
    </div>
  );
}
