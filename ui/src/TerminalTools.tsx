import { useEffect, useRef, useState, type ReactNode } from 'react';
import { api } from './bridge';
import { IconCode, IconFolder, IconText } from './Icons';
import { FONTS, THEMES, getTermSettings, onTermSettings, setTermSettings, type TermSettings } from './terminalThemes';
import type { Snippet } from './types';

/** Popover, der sich bei Klick daneben schliesst. */
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

/** Snippets: Klick schickt den Befehl ins Terminal (mit Enter, wenn "automatisch ausführen" an ist). */
export function SnippetMenu({ onSend, onManage }: { onSend: (text: string) => void; onManage: () => void }) {
  const [items, setItems] = useState<Snippet[] | null>(null);
  const load = () => api.snippetsList().then(setItems).catch(() => setItems([]));
  return (
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
          {items === null && <div className="muted small pad">Lade…</div>}
          {items?.length === 0 && <div className="muted small pad">Noch keine Snippets.</div>}
          {items?.map((s) => (
            <button
              key={s.id}
              className="pop-item"
              title={s.command}
              onClick={() => {
                onSend(s.command.replace(/\r?\n/g, '\r') + (s.autoRun ? '\r' : ''));
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
            Snippets verwalten…
          </button>
        </div>
      )}
    </Popover>
  );
}

/** Aussehen: Theme-Auswahl mit Vorschau, Schriftart und -grösse. Gilt für alle Terminals. */
export function AppearanceMenu() {
  const [s, setS] = useState<TermSettings>(getTermSettings());
  useEffect(() => onTermSettings(setS), []);
  return (
    <Popover
      title="Aussehen"
      button={
        <span className="row-inline">
          <IconText size={15} /> Aa
        </span>
      }
    >
      {() => (
        <div className="pop-appearance">
          <div className="pop-section">Schrift</div>
          <div className="row" style={{ alignItems: 'center' }}>
            <select value={s.font} onChange={(e) => setTermSettings({ font: e.target.value })}>
              {FONTS.map((f) => (
                <option key={f.id} value={f.id}>
                  {f.name}
                </option>
              ))}
            </select>
            <div className="stepper">
              <button onClick={() => setTermSettings({ fontSize: s.fontSize - 1 })} aria-label="Kleiner">
                −
              </button>
              <span>{s.fontSize}</span>
              <button onClick={() => setTermSettings({ fontSize: s.fontSize + 1 })} aria-label="Grösser">
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
    <button title="Dateien dieses Hosts per SFTP verwalten" onClick={onClick}>
      <IconFolder size={15} /> SFTP
    </button>
  );
}
