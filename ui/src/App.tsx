import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { api, isNative, onEvent } from './bridge';
import { emptyServer, type Identity, type KeyInfo, type Server, type SessionStatus, type SyncInfo, type SyncStatus } from './types';
import { Unlock } from './Unlock';
import { CommandPalette, type PaletteAction } from './CommandPalette';
import { HostPanel } from './HostPanel';
import { Keychain } from './Keychain';
import { CloudPanel } from './CloudPanel';
import { TerminalView } from './TerminalView';
import { VncView } from './VncView';
import { RdpView } from './RdpView';
import { SftpView } from './SftpView';
import { SnippetsPage } from './SnippetsPage';
import { ForwardsPage } from './ForwardsPage';
import { KnownHostsPage } from './KnownHostsPage';
import { LogsPage } from './LogsPage';
import { HostTile } from './HostTile';
import { SettingsPage } from './SettingsPage';
import { SplitView, type SplitPane } from './SplitView';
import { SplitPicker } from './SplitPicker';
import { HostsPage, type ImportKind } from './HostsPage';
import { getUiSettings, onUiSettings, setUiSettings } from './uiSettings';
import { readTextAuto } from './files';
import { IconClock, IconClose, IconCloud, IconCode, IconFolder, IconForward, IconGrid, IconKey, IconLock, IconMoon, IconPlus, IconServer, IconSettings, IconShield, IconSun } from './Icons';

type Phase = 'loading' | 'locked' | 'ready';
type Section = 'hosts' | 'keys' | 'snippets' | 'forwards' | 'knownhosts' | 'logs' | 'cloud' | 'settings';
type Theme = 'light' | 'dark';
interface Tab {
  id: string;
  serverId: string;
  kind: 'session' | 'sftp' | 'split';
  panes?: SplitPane[];
  reconnectKey: number;
}
interface Toast {
  id: number;
  msg: string;
  kind: 'ok' | 'error';
}
interface PanelState {
  server: Server;
  key: number;
}
const HOME = 'home';

export function App() {
  const [phase, setPhase] = useState<Phase>('loading');
  const [vaultExists, setVaultExists] = useState(true);
  const [servers, setServers] = useState<Server[]>([]);
  const [keys, setKeys] = useState<KeyInfo[]>([]);
  const [identities, setIdentities] = useState<Identity[]>([]);
  const [syncInfo, setSyncInfo] = useState<SyncInfo | null>(null);
  const [syncStatus, setSyncStatus] = useState<SyncStatus>({ state: 'off', message: '' });
  const [tabs, setTabs] = useState<Tab[]>([]);
  const [statuses, setStatuses] = useState<Record<string, SessionStatus>>({});
  const [active, setActive] = useState<string>(HOME);
  const [section, setSection] = useState<Section>('hosts');
  const [query, setQuery] = useState('');
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [panel, setPanel] = useState<PanelState | null>(null);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [theme, setThemeState] = useState<Theme>(getUiSettings().theme);
  const [splitPicker, setSplitPicker] = useState(false);

  const byId = useMemo(() => new Map(servers.map((s) => [s.id, s])), [servers]);
  const groups = useMemo(() => [...new Set(servers.map((s) => s.group).filter(Boolean))].sort(), [servers]);
  const openServerIds = useMemo(() => new Set(tabs.map((t) => t.serverId)), [tabs]);
  const themeClass = theme === 'light' ? 'theme-light' : 'theme-dark';

  const toggleTheme = () => setUiSettings({ theme: theme === 'light' ? 'dark' : 'light' });
  useEffect(() => onUiSettings((u) => setThemeState(u.theme)), []);

  // ---------------------------------------------------------------- Toasts
  const toast = useCallback((msg: string, kind: 'ok' | 'error' = 'ok') => {
    const id = Date.now() + Math.random();
    setToasts((prev) => [...prev, { id, msg, kind }]);
    setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), kind === 'error' ? 7000 : 3200);
  }, []);

  // ---------------------------------------------------------------- Laden
  const refresh = useCallback(async () => {
    const [s, k, ids, sync] = await Promise.all([api.serversList(), api.keysList(), api.identitiesList(), api.syncGet()]);
    setServers(s);
    setKeys(k);
    setIdentities(ids);
    setSyncInfo(sync);
    setSyncStatus((prev) => (sync.configured ? (prev.state === 'off' ? { state: 'ok', message: 'Verbunden' } : prev) : { state: 'off', message: '' }));
  }, []);

  useEffect(() => {
    api.vaultStatus().then(async (st) => {
      setVaultExists(st.exists);
      if (st.unlocked) {
        await refresh();
        setPhase('ready');
      } else setPhase('locked');
    });
  }, [refresh]);

  useEffect(
    () =>
      onEvent((e) => {
        if (e.event === 'sync.status') setSyncStatus({ state: e.state, message: e.message, at: e.at });
        else if (e.event === 'vault.changed') void refresh();
      }),
    [refresh],
  );

  async function onUnlocked() {
    await refresh();
    setPhase('ready');
  }

  async function lock() {
    await api.vaultLock();
    setServers([]);
    setKeys([]);
    setIdentities([]);
    setTabs([]);
    setStatuses({});
    setPanel(null);
    setActive(HOME);
    setVaultExists(true);
    setPhase('locked');
  }

  // ---------------------------------------------------------------- Tabs
  const goTo = useCallback((s: Section) => {
    setSection(s);
    setActive(HOME);
    setPanel(null);
  }, []);

  const open = useCallback(
    async (s: Server, forceNew = false, kind: 'session' | 'sftp' = 'session') => {
      setPaletteOpen(false);
      setPanel(null);
      setQuery('');
      const existing = forceNew ? undefined : tabs.find((t) => t.serverId === s.id && t.kind === kind);
      if (existing) setActive(existing.id);
      else {
        const t: Tab = { id: crypto.randomUUID(), serverId: s.id, kind, reconnectKey: 0 };
        setTabs((prev) => [...prev, t]);
        setActive(t.id);
      }
      try {
        const updated = await api.serversTouch(s.id);
        setServers((prev) => prev.map((x) => (x.id === updated.id ? updated : x)));
      } catch {
        /* "zuletzt benutzt" ist nicht kritisch */
      }
    },
    [tabs],
  );

  const openSplit = useCallback((ids: string[]) => {
    setSplitPicker(false);
    setPanel(null);
    const panes: SplitPane[] = ids.map((serverId) => ({ id: crypto.randomUUID(), serverId, reconnectKey: 0 }));
    const t: Tab = { id: crypto.randomUUID(), serverId: ids[0], kind: 'split', panes, reconnectKey: 0 };
    setTabs((prev) => [...prev, t]);
    setActive(t.id);
  }, []);

  const closeTab = useCallback(
    (id: string) => {
      const i = tabs.findIndex((t) => t.id === id);
      const next = tabs.filter((t) => t.id !== id);
      setTabs(next);
      setStatuses((prev) => {
        const { [id]: _gone, ...rest } = prev;
        return rest;
      });
      if (active === id) setActive(next[Math.min(i, next.length - 1)]?.id ?? HOME);
    },
    [tabs, active],
  );

  const reconnect = useCallback(
    (id: string) => setTabs((prev) => prev.map((t) => (t.id === id ? { ...t, reconnectKey: t.reconnectKey + 1 } : t))),
    [],
  );

  const duplicate = useCallback(
    (id: string) => {
      const t = tabs.find((x) => x.id === id);
      const s = t && byId.get(t.serverId);
      if (s && t.kind !== 'split') void open(s, true, t.kind);
    },
    [tabs, byId, open],
  );

  const cycle = useCallback(
    (dir: 1 | -1) => {
      const order = [HOME, ...tabs.map((t) => t.id)];
      const i = order.indexOf(active);
      setActive(order[(i + dir + order.length) % order.length]);
    },
    [tabs, active],
  );

  const setStatus = useCallback((id: string, s: SessionStatus) => setStatuses((prev) => (prev[id] === s ? prev : { ...prev, [id]: s })), []);

  // ---------------------------------------------------------------- Panel
  const openPanel = useCallback((server: Server) => setPanel((p) => ({ server, key: (p?.key ?? 0) + 1 })), []);
  const newHost = useCallback(() => {
    setSection('hosts');
    setActive(HOME);
    openPanel(emptyServer());
  }, [openPanel]);

  // ---------------------------------------------------------------- Tastenkürzel
  // Im Terminal gehören einfache Strg-Kombinationen der Shell (Strg+K, Strg+W, ...). Deshalb:
  //   Strg+K  (nicht im Terminal)  /  Strg+Umschalt+K (überall)  -> Schnell verbinden
  //   Strg+Tab, Strg+Umschalt+Tab  -> Tab wechseln          Strg+1…9 -> Tab direkt
  //   Strg+Umschalt+W / N / D      -> Tab schliessen / neuer Host / Session duplizieren
  useEffect(() => {
    if (phase !== 'ready') return;
    function onKey(e: KeyboardEvent) {
      if (!e.ctrlKey && !e.metaKey) return;
      const key = e.key.toLowerCase();
      const inTerminal = !!(document.activeElement as HTMLElement | null)?.closest('.xterm');
      let handled = true;

      if (key === 'k' && (e.shiftKey || !inTerminal)) setPaletteOpen((o) => !o);
      else if (e.key === 'Tab') cycle(e.shiftKey ? -1 : 1);
      else if (/^[1-9]$/.test(e.key) && !e.shiftKey) {
        const n = Number(e.key);
        setActive(n === 1 ? HOME : (tabs[n - 2]?.id ?? active));
      } else if (key === 'w' && e.shiftKey) {
        if (active !== HOME) closeTab(active);
      } else if (key === 'n' && e.shiftKey) newHost();
      else if (key === 'd' && e.shiftKey) {
        if (active !== HOME) duplicate(active);
      } else handled = false;

      if (handled) {
        e.preventDefault();
        e.stopPropagation();
      }
    }
    window.addEventListener('keydown', onKey, true); // capture: vor xterm
    return () => window.removeEventListener('keydown', onKey, true);
  }, [phase, tabs, active, cycle, closeTab, duplicate, newHost]);

  // Automatisch sperren nach Untätigkeit (Einstellung «Sicherheit»)
  const lastActivity = useRef(Date.now());
  useEffect(() => {
    if (phase !== 'ready') return;
    const touch = () => (lastActivity.current = Date.now());
    const events = ['mousemove', 'mousedown', 'keydown', 'wheel', 'touchstart'] as const;
    events.forEach((e) => window.addEventListener(e, touch, true));
    const timer = window.setInterval(() => {
      const minutes = getUiSettings().autoLockMinutes;
      if (minutes > 0 && Date.now() - lastActivity.current > minutes * 60_000) void lock();
    }, 15_000);
    return () => {
      events.forEach((e) => window.removeEventListener(e, touch, true));
      window.clearInterval(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase]);

  // RDP ist ein natives Fenster über dem UI -> bei Overlays kurz ausblenden
  const drawerOpen = panel !== null && active !== HOME;
  const overlayOpen = paletteOpen || drawerOpen;
  useEffect(() => {
    if (isNative && phase === 'ready') void api.rdpSuspend(overlayOpen).catch(() => undefined);
  }, [overlayOpen, phase]);

  // ---------------------------------------------------------------- Speichern / Löschen / Import
  const save = useCallback(async (s: Server): Promise<Server> => {
    const saved = await api.serversSave(s);
    setServers((prev) => (prev.some((x) => x.id === saved.id) ? prev.map((x) => (x.id === saved.id ? saved : x)) : [...prev, saved]));
    setPanel((p) => (p ? { ...p, server: saved } : p));
    return saved;
  }, []);

  async function connectFromPanel(draft: Server) {
    const saved = await save(draft);
    await open(saved);
  }

  async function sftpFromPanel(draft: Server) {
    const saved = await save(draft);
    await open(saved, false, 'sftp');
  }

  async function remove(s: Server) {
    await api.serversDelete(s.id);
    setServers((prev) => prev.filter((x) => x.id !== s.id));
    setTabs((prev) =>
      prev.flatMap((t) => {
        if (t.kind !== 'split') return t.serverId === s.id ? [] : [t];
        const panes = (t.panes ?? []).filter((p) => p.serverId !== s.id);
        return panes.length === 0 ? [] : [{ ...t, panes, serverId: panes[0].serverId }];
      }),
    );
    setActive(HOME);
    setPanel(null);
  }

  async function toggleFav(s: Server) {
    try {
      await save({ ...s, favorite: !s.favorite });
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  async function duplicateHost(s: Server) {
    try {
      await save({ ...s, id: '', name: `${s.name} (Kopie)`, lastUsed: null, favorite: false });
      toast('Host dupliziert');
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  async function deleteHost(s: Server) {
    if (!confirm(`«${s.name}» wirklich löschen?`)) return;
    try {
      await remove(s);
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  async function importFile(kind: ImportKind, file?: File) {
    try {
      const text = file ? await readTextAuto(file) : '';
      const r =
        kind === 'mremoteng' ? await api.importMremoteng(text)
        : kind === 'sshconfig' ? await api.importSshConfig(text)
        : kind === 'csv' ? await api.importCsv(text)
        : await api.importPutty(kind === 'putty-reg' ? text : undefined);
      await refresh();
      const parts = [`${r.imported} Host${r.imported === 1 ? '' : 's'} importiert`];
      if (r.duplicates) parts.push(`${r.duplicates} schon vorhanden`);
      if (r.skipped) parts.push(`${r.skipped} übersprungen (nicht unterstützt)`);
      toast(parts.join(', ') + (kind === 'csv' ? '.' : '. Passwörter bitte neu eintragen.'));
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  function onPaletteAction(a: PaletteAction) {
    setPaletteOpen(false);
    if (a === 'new') newHost();
    else if (a === 'split') setSplitPicker(true);
    else if (a === 'settings') goTo('settings');
    else if (a === 'keys') goTo('keys');
    else if (a === 'cloud') goTo('cloud');
    else void lock();
  }

  // ---------------------------------------------------------------- Render
  if (phase === 'loading') return <div className={`fill center muted ${themeClass}`}>Lade…</div>;
  if (phase === 'locked')
    return (
      <div className={`fill ${themeClass}`}>
        <Unlock exists={vaultExists} onDone={onUnlocked} onVaultInstalled={() => setVaultExists(true)} />
      </div>
    );

  const panelEl = panel && (
    <HostPanel
      key={panel.key}
      initial={panel.server}
      drawer={active !== HOME}
      groups={groups}
      keys={keys}
      identities={identities}
      jumpCandidates={servers}
      onSave={save}
      onConnect={connectFromPanel}
      onSftp={sftpFromPanel}
      onDelete={remove}
      onClose={() => setPanel(null)}
      onGoToKeys={() => goTo('keys')}
      toast={toast}
    />
  );

  return (
    <div className={`app ${themeClass}`}>
      <div className="tabbar chrome">
        <button className={'tab home' + (active === HOME ? ' on' : '')} onClick={() => setActive(HOME)}>
          <IconServer size={16} /> Hosts
        </button>
        {tabs.map((t) => {
          const st = statuses[t.id] ?? 'connecting';
          if (t.kind === 'split') {
            return (
              <button
                key={t.id}
                className={'tab' + (active === t.id ? ' on' : '')}
                title="Geteilte Ansicht"
                onClick={() => setActive(t.id)}
                onAuxClick={(e) => e.button === 1 && closeTab(t.id)}
              >
                <span className="tile sm neutral">
                  <IconGrid size={13} />
                </span>
                <span className="tab-name">Split · {t.panes?.length ?? 0} Hosts</span>
                <span className={`status-dot ${st}`} />
                <span
                  className="x"
                  role="button"
                  aria-label="Tab schliessen"
                  onClick={(e) => {
                    e.stopPropagation();
                    closeTab(t.id);
                  }}
                >
                  <IconClose size={13} />
                </span>
              </button>
            );
          }
          const s = byId.get(t.serverId);
          if (!s) return null;
          return (
            <button
              key={t.id}
              className={'tab' + (active === t.id ? ' on' : '')}
              title={`${s.name} – ${s.host}`}
              onClick={() => setActive(t.id)}
              onAuxClick={(e) => e.button === 1 && closeTab(t.id)}
            >
              <HostTile server={s} size="sm" />
              <span className="tab-name">{s.name}</span>
              {t.kind === 'sftp' && (
                <span className="tab-kind">
                  <IconFolder size={11} /> SFTP
                </span>
              )}
              <span className={`status-dot ${st}`} />
              <span
                className="x"
                role="button"
                aria-label="Tab schliessen"
                onClick={(e) => {
                  e.stopPropagation();
                  closeTab(t.id);
                }}
              >
                <IconClose size={13} />
              </span>
            </button>
          );
        })}
        <button className="tab-add" title="Neue Verbindung (Ctrl+K)" onClick={() => setPaletteOpen(true)}>
          <IconPlus size={17} />
        </button>
        <span className="grow" />
        <button
          className="bar-btn"
          title={syncInfo?.configured ? `Cloud: ${syncStatus.message || 'verbunden'}` : 'Cloud einrichten'}
          onClick={() => goTo('cloud')}
        >
          <IconCloud size={17} />
          <span className={`badge ${!syncInfo?.configured ? 'off' : syncStatus.state === 'syncing' ? 'syncing' : syncStatus.state === 'error' ? 'error' : 'ok'}`} />
        </button>
        <button className="bar-btn" title="Vault sperren" onClick={lock}>
          <IconLock size={17} />
        </button>
      </div>

      <div className="body">
        <div className={'view' + (active === HOME ? ' on' : '')}>
          <div className="home-shell">
            <nav className="nav">
              <button className={'nav-item' + (section === 'hosts' ? ' on' : '')} onClick={() => goTo('hosts')}>
                <IconServer size={17} /> Hosts
              </button>
              <button className={'nav-item' + (section === 'keys' ? ' on' : '')} onClick={() => goTo('keys')}>
                <IconKey size={17} /> Schlüssel
              </button>
              <button className={'nav-item' + (section === 'snippets' ? ' on' : '')} onClick={() => goTo('snippets')}>
                <IconCode size={17} /> Snippets
              </button>
              <button className={'nav-item' + (section === 'forwards' ? ' on' : '')} onClick={() => goTo('forwards')}>
                <IconForward size={17} /> Port Forwarding
              </button>
              <button className={'nav-item' + (section === 'knownhosts' ? ' on' : '')} onClick={() => goTo('knownhosts')}>
                <IconShield size={17} /> Known Hosts
              </button>
              <button className={'nav-item' + (section === 'logs' ? ' on' : '')} onClick={() => goTo('logs')}>
                <IconClock size={17} /> Logs
              </button>
              <button className={'nav-item' + (section === 'cloud' ? ' on' : '')} onClick={() => goTo('cloud')}>
                <IconCloud size={17} /> Cloud &amp; Sync
              </button>
              <div className="nav-foot">
                <button className={'nav-item grow' + (section === 'settings' ? ' on' : '')} onClick={() => goTo('settings')}>
                  <IconSettings size={17} /> Einstellungen
                </button>
                <button className="ghost" title={theme === 'light' ? 'Dunkles Design' : 'Helles Design'} onClick={toggleTheme}>
                  {theme === 'light' ? <IconMoon size={16} /> : <IconSun size={16} />}
                </button>
              </div>
            </nav>

            <div className="home-main">
              {section === 'hosts' && (
                <HostsPage
                  servers={servers}
                  query={query}
                  setQuery={setQuery}
                  selectedId={panel?.server.id || undefined}
                  openServerIds={openServerIds}
                  onSelect={openPanel}
                  onConnect={(s, forceNew) => void open(s, forceNew)}
                  onSftp={(s) => void open(s, false, 'sftp')}
                  onFav={(s) => void toggleFav(s)}
                  onDuplicateHost={(s) => void duplicateHost(s)}
                  onDelete={(s) => void deleteHost(s)}
                  onSplit={() => setSplitPicker(true)}
                  onNew={newHost}
                  onQuick={(draft) => openPanel({ ...emptyServer(), ...draft })}
                  onImport={importFile}
                />
              )}
              {section === 'keys' && <Keychain keys={keys} identities={identities} servers={servers} onChanged={refresh} toast={toast} />}
              {section === 'snippets' && <SnippetsPage toast={toast} />}
              {section === 'forwards' && <ForwardsPage servers={servers} toast={toast} />}
              {section === 'knownhosts' && <KnownHostsPage toast={toast} />}
              {section === 'logs' && <LogsPage toast={toast} />}
              {section === 'cloud' && <CloudPanel info={syncInfo} status={syncStatus} onChanged={refresh} toast={toast} />}
              {section === 'settings' && <SettingsPage onLock={() => void lock()} toast={toast} />}
            </div>

            {active === HOME && section === 'hosts' && panelEl}
          </div>
        </div>

        {tabs.map((t) => {
          if (t.kind === 'split') {
            return (
              <div key={t.id} className={'view' + (active === t.id ? ' on' : '')}>
                <SplitView
                  tabId={t.id}
                  panes={t.panes ?? []}
                  onPanesChange={(panes) => setTabs((prev) => prev.map((x) => (x.id === t.id ? { ...x, panes, serverId: panes[0]?.serverId ?? x.serverId } : x)))}
                  servers={servers}
                  byId={byId}
                  active={active === t.id}
                  onStatus={setStatus}
                  onEdit={openPanel}
                  onOpenSftp={(srv) => void open(srv, false, 'sftp')}
                  onManageSnippets={() => goTo('snippets')}
                />
              </div>
            );
          }
          const s = byId.get(t.serverId);
          if (!s) return null;
          const common = {
            tabId: t.id,
            server: s,
            active: active === t.id,
            reconnectKey: t.reconnectKey,
            onStatus: setStatus,
            onReconnect: () => reconnect(t.id),
            onDuplicate: () => duplicate(t.id),
            onEdit: () => openPanel(s),
          };
          return (
            <div key={t.id} className={'view' + (active === t.id ? ' on' : '')}>
              {t.kind === 'sftp' && <SftpView {...common} />}
              {t.kind === 'session' && s.protocol === 'ssh' && (
                <TerminalView {...common} onOpenSftp={() => void open(s, false, 'sftp')} onManageSnippets={() => goTo('snippets')} />
              )}
              {t.kind === 'session' && s.protocol === 'vnc' && <VncView {...common} />}
              {t.kind === 'session' && s.protocol === 'rdp' && <RdpView {...common} />}
            </div>
          );
        })}
      </div>

      {drawerOpen && panelEl}

      {splitPicker && <SplitPicker servers={servers.filter((x) => x.protocol === 'ssh')} onConfirm={openSplit} onClose={() => setSplitPicker(false)} />}

      {paletteOpen && (
        <CommandPalette
          servers={servers}
          openServerIds={openServerIds}
          onOpen={(s, forceNew) => void open(s, forceNew)}
          onAction={onPaletteAction}
          onClose={() => setPaletteOpen(false)}
        />
      )}

      <div className="toasts">
        {toasts.map((t) => (
          <div key={t.id} className={'toast ' + t.kind}>
            {t.msg}
          </div>
        ))}
      </div>
      {!isNative && <div className="devbadge">Browser-Mock (unverschlüsselt)</div>}
    </div>
  );
}
