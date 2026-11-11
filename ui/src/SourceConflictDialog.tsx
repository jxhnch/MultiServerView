import { useState } from 'react';
import { api } from './bridge';
import type { SourceMode } from './types';

const LABEL: Record<SourceMode, string> = { local: 'this PC', folder: 'your folder', gist: 'GitHub', supabase: 'Supabase' };

/**
 * The vault at the source and the one on this PC have both changed (or the old sync left both behind):
 * there is no merging, the user decides which version stays. The other one is kept as a backup.
 */
export function SourceConflictDialog({
  mode,
  message,
  onLater,
  onDone,
  onReplaced,
}: {
  mode: SourceMode;
  message: string;
  onLater: () => void;
  onDone: () => Promise<void>;
  onReplaced: () => void;
}) {
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const where = LABEL[mode];

  const choose = async (choice: 'source' | 'mine') => {
    setBusy(choice);
    setError('');
    try {
      await api.sourceResolve(choice);
      if (choice === 'source') onReplaced();
      else await onDone();
    } catch (e) {
      setError((e as Error).message);
      setBusy('');
    }
  };

  return (
    <div className="overlay">
      <div className="modal" style={{ width: 'min(540px, 94vw)' }} onKeyDown={(e) => e.key === 'Escape' && onLater()}>
        <h2>Two versions of your vault</h2>
        <p className="muted small" style={{ marginTop: 0 }}>
          {message}
        </p>
        <p className="small">Only one can stay. The other one is kept as a backup file, so nothing is lost.</p>
        {error && <div className="error">{error}</div>}
        <div className="choice-list">
          <button className="primary-choice" autoFocus disabled={!!busy} onClick={() => void choose('source')}>
            <b>{busy === 'source' ? 'Loading…' : `Use the version from ${where}`}</b>
            <span className="small">The changes of this PC are replaced (kept as a backup file). Afterwards you unlock again.</span>
          </button>
          <button disabled={!!busy} onClick={() => void choose('mine')}>
            <b>{busy === 'mine' ? 'Saving…' : 'Keep the version of this PC'}</b>
            <span className="muted small">It replaces the vault at {where}. Changes made there are gone (GitHub keeps older versions).</span>
          </button>
        </div>
        <div className="actions">
          <span className="grow" />
          <button disabled={!!busy} onClick={onLater}>
            Decide later
          </button>
        </div>
      </div>
    </div>
  );
}
