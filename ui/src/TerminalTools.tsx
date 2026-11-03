import { useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { api } from './bridge';
import { IconCode, IconFolder, IconText } from './Icons';
import { FONTS, THEMES, getTermSettings, onTermSettings, setTermSettings, type TermSettings } from './terminalThemes';
import { SnippetVarsDialog } from './SnippetValuesDialog';
import { builtinVars, fillVars, parseVars } from './snippetVars';
import type { Server, Snippet } from './types';

/** Popover that closes when you click outside. */
function Popover({ button, title, children }: { button: ReactNode; title: string; children: (close: () => void) => ReactNode }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [open]);
  return (
    <div className="pop-anchor" ref={ref}>
      <button title={title} onClick={() => setOpen((o) => !o)} className={open ? 'active' : ''}>
        {button}
      </button>
      {open && <div className="popover">{children(() => setOpen(false))}</div>}
    </div>
  );
}

/** Snippets: a click sends the command to the terminal (with Enter if "run automatically" is on). */
export function SnippetMenu({ server, onSend, onManage }: { server: Server; onSend: (text: string) => void; onManage: () => void }) {
  const [items, setItems] = useState<Snippet[] | null>(null);
  const [asking, setAsking] = useState<Snippet | null>(null);
  const load = () => api.snippetsList().then(setItems).catch(() => setItems([]));
  const builtins = builtinVars(server);

  /** Sends the finished command to the terminal (multi-line: lines separated by Enter). */
  const send = (s: Snippet, command: string) => onSend(command.replace(/\r?\n/g, '\r') + (s.autoRun ? '\r' : ''));

  return (
    <>
    <Popover
      title="Snippets"
      button={
        <span onClick={load} className="row-inline">
          <IconCode size={15} /> Snippets
        </span>
      }
    >
      {(close) => (
        <div className="pop-list">
          {items === null && <div className="muted small pad">Loading…</div>}
          {items?.length === 0 && <div className="muted small pad">No snippets yet.</div>}
          {items?.map((s) => (
            <button
              key={s.id}
              className="pop-item"
              title={s.command}
              onClick={() => {
                // A small dialog asks for variables like {{container}}; {{host}}, {{user}} and {{port}} are filled in by the app.
                if (parseVars(s.command, builtins).length > 0) setAsking(s);
                else send(s, fillVars(s.command, {}, builtins));
                close();
              }}
            >
              <span className="pop-name">{s.name}</span>
              <span className="pop-sub mono">{s.command.split('\n')[0]}</span>
            </button>
          ))}
          <button
            className="pop-item manage"
            onClick={() => {
              close();
              onManage();
            }}
          >
            Manage snippets…
          </button>
        </div>
      )}
    </Popover>
    {/* As a portal in .app (theme variables): inside the toolbar the full-screen overlay would get stuck there. */}
    {asking && createPortal(
      <SnippetVarsDialog
        snippet={asking}
        vars={parseVars(asking.command, builtins)}
        builtins={builtins}
        onClose={() => setAsking(null)}
        onSubmit={(command) => {
          send(asking, command);
          setAsking(null);
        }}
      />,
      document.querySelector('.app') ?? document.body
    )}
    </>
  );
}

/** Appearance: theme picker with preview, font and font size. Applies to all terminals. */
export function AppearanceMenu() {
  const [s, setS] = useState<TermSettings>(getTermSettings());
  useEffect(() => onTermSettings(setS), []);
  return (
    <Popover
      title="Appearance"
      button={
        <span className="row-inline">
          <IconText size={15} /> Aa
        </span>
      }
    >
      {() => (
        <div className="pop-appearance">
          <div className="pop-section">Font</div>
          <div className="row" style={{ alignItems: 'center' }}>
            <select value={s.font} onChange={(e) => setTermSettings({ font: e.target.value })}>
              {FONTS.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.name}
                </option>
              ))}
            </select>
            <div className="stepper">
              <button onClick={() => setTermSettings({ fontSize: s.fontSize - 1 })} aria-label="Smaller">
                −
              </button>
              <span>{s.fontSize}</span>
              <button onClick={() => setTermSettings({ fontSize: s.fontSize + 1 })} aria-label="Larger">
                +
              </button>
            </div>
          </div>
          <div className="pop-section">Theme</div>
          <div className="theme-grid">
            {THEMES.map((t) => (
              <button key={t.id} className={'theme-card' + (s.theme === t.id ? ' on' : '')} onClick={() => setTermSettings({ theme: t.id })}>
                <span className="theme-preview" style={{ background: t.bg }}>
                  <i style={{ background: t.fg, width: '70%' }} />
                  <i style={{ background: t.ansi[2], width: '45%' }} />
                  <i style={{ background: t.ansi[4], width: '58%' }} />
                  <i style={{ background: t.ansi[1], width: '30%' }} />
                </span>
                <span className="theme-name">{t.name}</span>
              </button>
            ))}
          </div>
        </div>
      )}
    </Popover>
  );
}

export function SftpButton({ onClick }: { onClick: () => void }) {
  return (
    <button title="Manage files of this host via SFTP" onClick={onClick}>
      <IconFolder size={15} /> SFTP
    </button>
  );
}
