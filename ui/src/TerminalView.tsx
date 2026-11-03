import { useEffect, useRef, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebLinksAddon } from '@xterm/addon-web-links';
import '@xterm/xterm/css/xterm.css';
import { api, binaryToB64, fromB64, onEvent, toB64 } from './bridge';
import { SessionFrame } from './SessionFrame';
import { AppearanceMenu, SftpButton, SnippetMenu } from './TerminalTools';
import { findFont, findTheme, getTermSettings, onTermSettings, toXtermTheme } from './terminalThemes';
import type { Server, SessionStatus } from './types';


/**
 * Ein SSH-Terminal (xterm.js). Bleibt gemountet, solange der Tab offen ist - auch wenn man in einen
 * anderen Tab wechselt (dann nur versteckt). So gehen Scrollback und Verbindung nicht verloren.
 */
export function TerminalView({
  tabId,
  server,
  active,
  reconnectKey,
  onStatus,
  onReconnect,
  onDuplicate,
  onEdit,
  onOpenSftp,
  onManageSnippets,
}: {
  tabId: string;
  server: Server;
  active: boolean;
  reconnectKey: number;
  onStatus: (tabId: string, status: SessionStatus) => void;
  onReconnect: () => void;
  onDuplicate: () => void;
  onEdit: () => void;
  onOpenSftp: () => void;
  onManageSnippets: () => void;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  const statusRef = useRef<SessionStatus>('connecting');
  const activeRef = useRef(active);
  const reconnectRef = useRef(onReconnect);
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [bg, setBg] = useState(() => findTheme(getTermSettings().theme).bg);
  activeRef.current = active;
  reconnectRef.current = onReconnect;

  const setStatus = (s: SessionStatus) => {
    statusRef.current = s;
    setStatusState(s);
    onStatus(tabId, s);
  };

  // ---------------------------------------------------------------- Terminal einmal aufbauen
  useEffect(() => {
    const st = getTermSettings();
    const term = new Terminal({
      fontFamily: findFont(st.font).css,
      fontSize: st.fontSize,
      lineHeight: 1.18,
      cursorBlink: true,
      cursorStyle: 'bar',
      scrollback: 10000,
      allowProposedApi: true,
      theme: toXtermTheme(findTheme(st.theme)),
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.loadAddon(new WebLinksAddon());
    term.open(hostRef.current!);
    termRef.current = term;
    fitRef.current = fit;

    const copySelection = () => {
      const text = term.getSelection();
      if (text) navigator.clipboard?.writeText(text).catch(() => undefined);
      return text.length > 0;
    };
    const paste = () => navigator.clipboard?.readText().then((t) => t && term.paste(t)).catch(() => undefined);

    // Kopieren/Einfügen wie in Windows Terminal.
    term.attachCustomKeyEventHandler((ev) => {
      if (ev.type !== 'keydown') return true;
      const k = ev.key.toLowerCase();
      if (ev.ctrlKey && ev.shiftKey && k === 'c') {
        copySelection();
        return false;
      }
      if (ev.ctrlKey && ev.shiftKey && k === 'v') {
        void paste();
        return false;
      }
      if (ev.ctrlKey && !ev.shiftKey && !ev.altKey && k === 'c' && term.hasSelection()) {
        copySelection();
        term.clearSelection();
        return false;
      }
      return true;
    });
    const onContext = (e: MouseEvent) => {
      e.preventDefault();
      if (term.hasSelection()) {
        copySelection();
        term.clearSelection();
      } else void paste();
    };
    hostRef.current!.addEventListener('contextmenu', onContext);

    term.onData((d) => {
      if (statusRef.current === 'connected') void api.sshInput(tabId, toB64(d)).catch(() => undefined);
      else if (d === '\r' && (statusRef.current === 'closed' || statusRef.current === 'error')) reconnectRef.current();
    });
    term.onBinary((d) => {
      if (statusRef.current === 'connected') void api.sshInput(tabId, binaryToB64(d)).catch(() => undefined);
    });
    term.onResize(({ cols, rows }) => {
      if (statusRef.current === 'connected') void api.sshResize(tabId, cols, rows).catch(() => undefined);
    });

    // Grösse nachziehen, wenn der Container sich ändert (nur wenn sichtbar).
    let raf = 0;
    const ro = new ResizeObserver(() => {
      cancelAnimationFrame(raf);
      raf = requestAnimationFrame(() => {
        if (hostRef.current && hostRef.current.clientWidth > 0 && hostRef.current.clientHeight > 0) fit.fit();
      });
    });
    ro.observe(hostRef.current!);

    // Ereignisse vom Host für diese Session
    const off = onEvent((e) => {
      if (!('sessionId' in e) || e.sessionId !== tabId) return;
      if (e.event === 'ssh.data') term.write(fromB64(e.data));
      else if (e.event === 'ssh.info') term.writeln(`\x1b[2m${e.message}\x1b[0m`);
      else if (e.event === 'ssh.closed') {
        setStatus('closed');
        term.writeln(`\r\n\x1b[33m● Verbindung getrennt${e.reason ? ` (${e.reason})` : ''}. Enter drücken zum erneuten Verbinden.\x1b[0m`);
      }
    });

    // Aussehen live ändern (Theme, Schrift, Grösse)
    const offSettings = onTermSettings((n) => {
      term.options.theme = toXtermTheme(findTheme(n.theme));
      term.options.fontFamily = findFont(n.font).css;
      term.options.fontSize = n.fontSize;
      setBg(findTheme(n.theme).bg);
      requestAnimationFrame(() => {
        try {
          if (hostRef.current && hostRef.current.clientWidth > 0) fit.fit();
        } catch {
          /* ignore */
        }
      });
    });

    return () => {
      off();
      offSettings();
      ro.disconnect();
      cancelAnimationFrame(raf);
      hostRef.current?.removeEventListener('contextmenu', onContext);
      term.dispose();
      termRef.current = null;
      fitRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId]);

  // ---------------------------------------------------------------- (Neu) verbinden
  useEffect(() => {
    const term = termRef.current;
    const fit = fitRef.current;
    if (!term || !fit) return;
    let cancelled = false;

    if (reconnectKey > 0) term.writeln('\x1b[2m────────── neue Verbindung ──────────\x1b[0m');
    term.writeln(`\x1b[2mVerbinde mit ${server.username ? server.username + '@' : ''}${server.host}:${server.port} …\x1b[0m`);
    setStatus('connecting');

    try {
      if (hostRef.current && hostRef.current.clientWidth > 0) fit.fit();
    } catch {
      /* Container noch ohne Grösse */
    }

    api
      .sshOpen(tabId, server.id, term.cols || 120, term.rows || 30)
      .then(() => {
        if (cancelled) return;
        setStatus('connected');
        if (activeRef.current) term.focus();
        // Grösse sicherheitshalber nochmal melden (Layout kann sich während des Verbindens geändert haben)
        void api.sshResize(tabId, term.cols, term.rows).catch(() => undefined);
      })
      .catch((err: Error) => {
        if (cancelled) return;
        setStatus('error');
        term.writeln(`\r\n\x1b[31m✖ ${err.message}\x1b[0m`);
        term.writeln('\x1b[2mEnter drücken zum erneuten Versuch.\x1b[0m');
      });

    return () => {
      cancelled = true;
      void api.sshClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey]);

  // ---------------------------------------------------------------- Fokus beim Wechseln
  useEffect(() => {
    if (!active) return;
    const id = requestAnimationFrame(() => {
      try {
        fitRef.current?.fit();
      } catch {
        /* ignore */
      }
      termRef.current?.focus();
    });
    return () => cancelAnimationFrame(id);
  }, [active]);

  return (
    <SessionFrame
      server={server}
      status={status}
      onReconnect={onReconnect}
      onDuplicate={onDuplicate}
      onEdit={onEdit}
      extraActions={
        <>
          <SnippetMenu
            onSend={(text) => {
              if (statusRef.current === 'connected') void api.sshInput(tabId, toB64(text)).catch(() => undefined);
              termRef.current?.focus();
            }}
            onManage={onManageSnippets}
          />
          <AppearanceMenu />
          <SftpButton onClick={onOpenSftp} />
        </>
      }
    >
      <div className="term-wrap" style={{ background: bg }}>
        <div className="term-host" ref={hostRef} />
      </div>
    </SessionFrame>
  );
}
