import { useEffect, useState } from 'react';
import { api } from './bridge';
import { IconCheck, IconClose, IconCopy } from './Icons';
import { fmtDateLong, fmtSize, type Side } from './sftpUtil';
import type { FileEntry, FileStat } from './types';

export type OverwriteChoice = 'overwrite' | 'newer' | 'skip' | 'cancel';

/** WinSCP-style question when files with the same names already exist at the destination. */
export function OverwriteDialog({ names, onChoice }: { names: string[]; onChoice: (c: OverwriteChoice) => void }) {
  const shown = names.slice(0, 6);
  return (
    <div className="overlay" onMouseDown={() => onChoice('cancel')}>
      <div className="modal" style={{ width: 'min(480px, 92vw)' }} onMouseDown={(e) => e.stopPropagation()} onKeyDown={(e) => e.key === 'Escape' && onChoice('cancel')}>
        <h2>{names.length === 1 ? 'File already exists' : `${names.length} files already exist`}</h2>
        <p className="muted small" style={{ marginTop: 0 }}>
          {shown.join(', ')}
          {names.length > shown.length ? ` and ${names.length - shown.length} more` : ''} already exist{names.length === 1 ? 's' : ''} at the destination.
        </p>
        <div className="choice-list">
          <button autoFocus onClick={() => onChoice('newer')}>
            <b>Overwrite older only</b>
            <span className="muted small">Files that are newer at the destination stay as they are</span>
          </button>
          <button onClick={() => onChoice('overwrite')}>
            <b>Overwrite all</b>
            <span className="muted small">The files at the destination are replaced</span>
          </button>
          <button onClick={() => onChoice('skip')}>
            <b>Skip existing</b>
            <span className="muted small">Only transfer what is not there yet</span>
          </button>
        </div>
        <div className="actions">
          <span className="grow" />
          <button onClick={() => onChoice('cancel')}>Cancel</button>
        </div>
      </div>
    </div>
  );
}

const BITS: { label: string; bit: number }[][] = [
  [{ label: 'Read', bit: 0x100 }, { label: 'Write', bit: 0x80 }, { label: 'Execute', bit: 0x40 }],
  [{ label: 'Read', bit: 0x20 }, { label: 'Write', bit: 0x10 }, { label: 'Execute', bit: 0x8 }],
  [{ label: 'Read', bit: 0x4 }, { label: 'Write', bit: 0x2 }, { label: 'Execute', bit: 0x1 }],
];
const WHO = ['Owner', 'Group', 'Others'];

/** Properties of a file or folder, with size of folders, SHA-256 and (on the server) permissions like chmod. */
export function PropertiesDialog({
  side,
  sessionId,
  entries,
  onClose,
  onChanged,
  toast,
}: {
  side: Side;
  sessionId: string;
  entries: FileEntry[];
  onClose: () => void;
  onChanged: () => void;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const single = entries.length === 1 ? entries[0] : null;
  const [stat, setStat] = useState<FileStat | null>(null);
  const [error, setError] = useState('');
  const [perms, setPerms] = useState<number | null>(null);
  const [octal, setOctal] = useState('');
  const [recursive, setRecursive] = useState(false);
  const [sum, setSum] = useState('');
  const [busy, setBusy] = useState('');

  useEffect(() => {
    let cancelled = false;
    const first = entries[0];
    const load = side === 'remote' ? api.sftpStat(sessionId, first.path) : api.localStat(first.path);
    load
      .then((s) => {
        if (cancelled) return;
        setStat(s);
        if (side === 'remote') {
          setPerms(s.permissions);
          setOctal(s.permissions.toString(8).padStart(3, '0'));
        }
      })
      .catch((e: Error) => !cancelled && setError(e.message));
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const setBits = (value: number) => {
    setPerms(value);
    setOctal(value.toString(8).padStart(3, '0'));
  };

  const applyPerms = async () => {
    if (perms == null) return;
    setBusy('perms');
    try {
      await api.sftpChmod(sessionId, entries.map((e) => e.path), perms, recursive);
      toast('Permissions changed');
      onChanged();
      onClose();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy('');
    }
  };

  const checksum = async () => {
    if (!single) return;
    setBusy('sum');
    try {
      const r = side === 'remote' ? await api.sftpChecksum(sessionId, single.path) : await api.localChecksum(single.path);
      setSum(r.sha256);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy('');
    }
  };

  const title = single ? single.name : `${entries.length} items`;
  return (
    <div className="overlay" onMouseDown={onClose}>
      <div className="modal props" style={{ width: 'min(520px, 94vw)' }} onMouseDown={(e) => e.stopPropagation()} onKeyDown={(e) => e.key === 'Escape' && onClose()}>
        <div className="modal-head">
          <h2 style={{ margin: 0 }}>Properties</h2>
          <button className="ghost icon-only" onClick={onClose} aria-label="Close">
            <IconClose size={16} />
          </button>
        </div>
        <div className="props-name">{title}</div>
        {error && <div className="error">{error}</div>}

        {single && (
          <dl className="props-grid">
            <dt>Location</dt>
            <dd className="mono">{single.path}</dd>
            <dt>Type</dt>
            <dd>{stat ? (stat.isLink ? 'Link' : stat.isDir ? 'Folder' : 'File') : '…'}</dd>
            <dt>Size</dt>
            <dd>
              {stat ? `${fmtSize(stat.size)} (${stat.size.toLocaleString()} bytes)` : '…'}
              {stat?.isDir && stat ? <span className="muted"> · {stat.items.toLocaleString()} files inside</span> : null}
            </dd>
            <dt>Modified</dt>
            <dd>{stat ? fmtDateLong(stat.modified) : '…'}</dd>
            {side === 'remote' && (
              <>
                <dt>Owner / group</dt>
                <dd>{stat ? `${stat.owner} / ${stat.group} (numeric ids)` : '…'}</dd>
              </>
            )}
          </dl>
        )}

        {side === 'remote' && perms != null && (
          <div className="perm-box">
            <h3>Permissions</h3>
            <table className="perm-table">
              <tbody>
                {BITS.map((row, r) => (
                  <tr key={r}>
                    <th>{WHO[r]}</th>
                    {row.map((b) => (
                      <td key={b.bit}>
                        <label>
                          <input type="checkbox" checked={(perms & b.bit) !== 0} onChange={(e) => setBits(e.target.checked ? perms | b.bit : perms & ~b.bit)} /> {b.label}
                        </label>
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
            <div className="row center-v">
              <label className="octal">
                Octal
                <input
                  className="mono"
                  value={octal}
                  maxLength={4}
                  onChange={(e) => {
                    setOctal(e.target.value);
                    if (/^[0-7]{3,4}$/.test(e.target.value)) setPerms(parseInt(e.target.value, 8) & 0x1ff);
                  }}
                />
              </label>
              {(stat?.isDir || entries.some((e) => e.isDir)) && (
                <label className="inline-check">
                  <input type="checkbox" checked={recursive} onChange={(e) => setRecursive(e.target.checked)} /> Apply to everything inside
                </label>
              )}
              <span className="grow" />
              <button className="primary" disabled={busy === 'perms'} onClick={() => void applyPerms()}>
                Apply permissions
              </button>
            </div>
          </div>
        )}

        {single && !single.isDir && (
          <div className="perm-box">
            <h3>Checksum</h3>
            {sum ? (
              <div className="row center-v">
                <code className="sum">{sum}</code>
                <button
                  className="ghost icon-only"
                  title="Copy"
                  onClick={() => void navigator.clipboard.writeText(sum).then(() => toast('SHA-256 copied')).catch(() => undefined)}
                >
                  <IconCopy size={15} />
                </button>
              </div>
            ) : (
              <button disabled={busy === 'sum'} onClick={() => void checksum()}>
                <IconCheck size={14} /> {busy === 'sum' ? 'Calculating…' : 'Calculate SHA-256'}
              </button>
            )}
          </div>
        )}

        <div className="actions">
          <span className="grow" />
          <button onClick={onClose}>Close</button>
        </div>
      </div>
    </div>
  );
}
