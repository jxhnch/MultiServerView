import { useEffect, useRef, useState } from 'react';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { WebLinksAddon } from '@xterm/addon-web-links';
import '@xterm/xterm/css/xterm.css';
import { api, binaryToB64, fromB64, onEvent, toB64 } from './bridge';
import { SessionFrame } from './SessionFrame';
import { AppearanceMenu, SftpButton, SnippetMenu } from './TerminalTools';
import { findFont, findTheme, getTermSettings, onTermSettings, toXtermTheme } from './terminalThemes';
import { PasswordPrompt, isAuthError, type Credentials } from './PasswordPrompt';
import type { Server, SessionStatus } from './types';


/**
 * An SSH terminal (xterm.js). Stays mounted as long as the tab is open - even when you switch to
 * another tab (it is only hidden then). That way scrollback and connection are not lost.
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
  bare,
  registerSender,
  onUserInput,
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
  /** Only the terminal, without toolbar (for the split view). */
  bare?: boolean;
  /** Registers a function that sends text into this terminal as if typed. */
  registerSender?: (send: ((text: string) => void) | null) => void;
  /** Called on every user input (for broadcast). */
  onUserInput?: (data: string) => void;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const termRef = useRef<Terminal | null>(null);
  const fitRef = useRef<FitAddon | null>(null);
  const statusRef = useRef<SessionStatus>('connecting');
  const activeRef = useRef(active);
  const credRef = useRef<Credentials | undefined>(undefined);
  const [retry, setRetry] = useState(0);
  const [prompt, setPrompt] = useState<'none' | 'missing' | 'failed'>('none');
  const reconnectRef = useRef(onReconnect);
  const [status, setStatusState] = useState<SessionStatus>('connecting');
  const [bg, setBg] = useState(() => findTheme(getTermSettings().theme).bg);
  activeRef.current = active;
  reconnectRef.current = onReconnect;
  const inputRef = useRef(onUserInput);
  inputRef.current = onUserInput;

  const setStatus = (s: SessionStatus) => {
    statusRef.current = s;
    setStatusState(s);
    onStatus(tabId, s);
  };

  // ---------------------------------------------------------------- Build the terminal once
  useEffect(() => {
    const st = getTermSettings();
    const term = new Terminal({
      fontFamily: findFont(st.font).css,
      fontSize: st.fontSize,
      lineHeight: 1.18,
      cursorBlink: st.cursorBlink,
      cursorStyle: st.cursorStyle,
      scrollback: st.scrollback,
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

    // Copy/paste like in Windows Terminal.
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
      if (statusRef.current === 'connected') {
        void api.sshInput(tabId, toB64(d)).catch(() => undefined);
        inputRef.current?.(d);
      }
      else if (d === '\r' && (statusRef.current === 'closed' || statusRef.current === 'error')) reconnectRef.current();
    });
    term.onBinary((d) => {
      if (statusRef.current === 'connected') void api.sshInput(tabId, binaryToB64(d)).catch(() => undefined);
    });
    term.onResize(({ cols, rows }) => {
      if (statusRef.current === 'connected') void api.sshResize(tabId, cols, rows).catch(() => undefined);
    });

    // Follow the size when the container changes (only when visible).
    let raf = 0;
    const ro = new ResizeObserver(() => {
      cancelAnimationFrame(raf);
      raf = requestAnimationFrame(() => {
        if (hostRef.current && hostRef.current.clientWidth > 0 && hostRef.current.clientHeight > 0) fit.fit();
      });
    });
    ro.observe(hostRef.current!);

    // Events from the host for this session
    const off = onEvent((e) => {
      if (!('sessionId' in e) || e.sessionId !== tabId) return;
      if (e.event === 'ssh.data') term.write(fromB64(e.data));
      else if (e.event === 'ssh.info') term.writeln(`\x1b[2m${e.message}\x1b[0m`);
      else if (e.event === 'ssh.closed') {
        setStatus('closed');
        term.writeln(`\r\n\x1b[33m● Disconnected${e.reason ? ` (${e.reason})` : ''}. Press Enter to reconnect.\x1b[0m`);
      }
    });

    // Copy selected text automatically (if enabled in the settings)
    term.onSelectionChange(() => {
      if (getTermSettings().copyOnSelect && term.hasSelection()) navigator.clipboard?.writeText(term.getSelection()).catch(() => undefined);
    });

    // Change appearance live (theme, font, size)
    const offSettings = onTermSettings((n) => {
      term.options.theme = toXtermTheme(findTheme(n.theme));
      term.options.fontFamily = findFont(n.font).css;
      term.options.fontSize = n.fontSize;
      term.options.cursorStyle = n.cursorStyle;
      term.options.cursorBlink = n.cursorBlink;
      term.options.scrollback = n.scrollback;
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

  // ---------------------------------------------------------------- (Re)connect
  useEffect(() => {
    const term = termRef.current;
    const fit = fitRef.current;
    if (!term || !fit) return;
    let cancelled = false;

    if (reconnectKey > 0) term.writeln('\x1b[2m────────── new connection ──────────\x1b[0m');
    term.writeln(`\x1b[2mConnecting to ${server.username ? server.username + '@' : ''}${server.host}:${server.port} …\x1b[0m`);
    const cred = credRef.current;
    credRef.current = undefined;

    // Password host without a saved password: ask right away, like PuTTY.
    if (!cred && server.protocol === 'ssh' && server.auth === 'password' && !server.password && !server.identityId) {
      term.writeln('\x1b[2mNo password saved – please enter it.\x1b[0m');
      setStatus('error');
      setPrompt('missing');
      return;
    }
    setStatus('connecting');

    try {
      if (hostRef.current && hostRef.current.clientWidth > 0) fit.fit();
    } catch {
      /* container has no size yet */
    }

    api
      .sshOpen(tabId, server.id, term.cols || 120, term.rows || 30, cred)
      .then(() => {
        if (cancelled) return;
        setStatus('connected');
        if (activeRef.current) term.focus();
        // Report the size once more to be safe (the layout may have changed while connecting)
        void api.sshResize(tabId, term.cols, term.rows).catch(() => undefined);
      })
      .catch((err: Error) => {
        if (cancelled) return;
        setStatus('error');
        term.writeln(`\r\n\x1b[31m✖ ${err.message}\x1b[0m`);
        term.writeln('\x1b[2mPress Enter to try again.\x1b[0m');
        if (isAuthError(err.message)) setPrompt('failed');
      });

    return () => {
      cancelled = true;
      void api.sshClose(tabId).catch(() => undefined);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId, reconnectKey, retry]);

  // ---------------------------------------------------------------- Sender for broadcast
  useEffect(() => {
    registerSender?.((text) => {
      if (statusRef.current === 'connected') void api.sshInput(tabId, toB64(text)).catch(() => undefined);
    });
    return () => registerSender?.(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tabId]);

  // ---------------------------------------------------------------- Focus when switching
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

  if (bare)
    return (
      <div className="bare-body" style={{ background: bg }}>
        <div className="term-wrap" style={{ background: bg }}>
          <div className="term-host" ref={hostRef} />
        </div>
      </div>
    );

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
            server={server}
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
      {prompt !== 'none' && (
        <PasswordPrompt
          server={server}
          failed={prompt === 'failed'}
          onCancel={() => setPrompt('none')}
          onSubmit={(cred) => {
            credRef.current = cred;
            setPrompt('none');
            setRetry((n) => n + 1);
          }}
        />
      )}
    </SessionFrame>
  );
}
