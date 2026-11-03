import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { api } from './bridge';
import { IconClose } from './Icons';
import type { FileEntry } from './types';
import { askConfirm } from './Confirm';

/** Message from the host when someone changed the file since it was opened (see RemoteChangedException). */
const CHANGED = 'The file was changed on the server since you opened it';

interface Doc {
  lineEnding: 'lf' | 'crlf';
  bom: boolean;
  size: number;
  modified: number;
}

/** Simple text editor for a file on the server: open, edit, save (Ctrl+S). Line endings and BOM stay as in the original. */
export function RemoteEditor({
  sessionId,
  file,
  onClose,
}: {
  sessionId: string;
  file: FileEntry;
  onClose: () => void;
}) {
  const [doc, setDoc] = useState<Doc | null>(null);
  const [saved, setSaved] = useState('');
  const [text, setText] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [savedAt, setSavedAt] = useState('');
  const area = useRef<HTMLTextAreaElement>(null);

  const dirty = doc !== null && text !== saved;

  useEffect(() => {
    let alive = true;
    api
      .sftpReadText(sessionId, file.path)
      .then((r) => {
        if (!alive) return;
        setDoc({ lineEnding: r.lineEnding, bom: r.bom, size: r.size, modified: r.modified });
        setSaved(r.content);
        setText(r.content);
        requestAnimationFrame(() => area.current?.focus());
      })
      .catch((e: Error) => alive && setError(e.message));
    return () => {
      alive = false;
    };
  }, [sessionId, file.path]);

  async function save(force = false) {
    if (!doc || busy) return;
    setBusy(true);
    setError('');
    try {
      const r = await api.sftpWriteText({
        sessionId,
        path: file.path,
        content: text,
        lineEnding: doc.lineEnding,
        bom: doc.bom,
        expectedModified: doc.modified,
        expectedSize: doc.size,
        force,
      });
      setDoc({ ...doc, modified: r.modified, size: r.size });
      setSaved(text);
      setSavedAt(new Date().toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' }));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function close() {
    if (dirty && !(await askConfirm('You have unsaved changes. Close anyway?', { title: 'Unsaved changes', confirmLabel: 'Discard', danger: true }))) return;
    onClose();
  }

  function onKeyDown(e: KeyboardEvent) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
      e.preventDefault();
      if (dirty) void save();
    } else if (e.key === 'Escape') {
      void close();
    } else if (e.key === 'Tab' && e.target === area.current) {
      // Tab inserts a tab character (instead of moving the focus).
      e.preventDefault();
      const t = area.current!;
      t.setRangeText('\t', t.selectionStart, t.selectionEnd, 'end');
      setText(t.value);
    }
  }

  const lines = text === '' ? 0 : text.split('\n').length;

  return (
    // No closing by clicking outside: otherwise changes could be lost by accident.
    <div className="overlay">
      <div className="modal editor" onKeyDown={onKeyDown}>
        <div className="modal-head">
          <h2 className="ellipsis" title={file.path}>
            {file.name}
            {dirty ? ' •' : ''}
          </h2>
          <button type="button" className="ghost icon-only" title="Close" onClick={() => void close()}>
            <IconClose size={16} />
          </button>
        </div>
        <div className="muted small mono ellipsis">{file.path}</div>

        <textarea
          ref={area}
          className="mono editor-text"
          spellCheck={false}
          wrap="off"
          disabled={!doc}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={doc || error ? '' : 'Loading…'}
        />

        {error && (
          <div className="error">
            {error}
            {error.startsWith(CHANGED) && (
              <>
                {' '}
                <button type="button" className="link" onClick={() => void save(true)}>
                  Overwrite anyway
                </button>
              </>
            )}
          </div>
        )}

        <div className="actions">
          <span className="muted small">
            {doc ? `UTF-8 · ${doc.lineEnding === 'crlf' ? 'CRLF' : 'LF'} · ${lines} line${lines === 1 ? '' : 's'}` : ''}
            {savedAt && !dirty ? ` · saved at ${savedAt}` : ''}
          </span>
          <span className="grow" />
          <button type="button" onClick={() => void close()}>
            {dirty ? 'Discard' : 'Close'}
          </button>
          <button className="primary" disabled={!dirty || busy} onClick={() => void save()}>
            {busy ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  );
}
