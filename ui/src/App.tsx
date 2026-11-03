import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, isNative, onEvent } from './bridge';
import { emptyServer, type KeyInfo, type Server, type SessionStatus, type SyncInfo, type SyncStatus } from './types';
import { filterServers } from './search';
import { Unlock } from './Unlock';
import { CommandPalette, type PaletteAction } from './CommandPalette';
import { ServerForm } from './ServerForm';
import { HostsPage } from './HostsPage';
import { Keychain } from './Keychain';
import { CloudPanel } from './CloudPanel';
import { TerminalView } from './TerminalView';
import { VncView } from './VncView';
import { RdpView } from './RdpView';
import { IconChevron, IconClose, IconCloud, IconKey, IconLock, IconPlus, IconSearch, IconServer, ProtocolIcon } from './Icons';

type Phase = 'loading' | 'locked' | 'ready';
type Section = 'hosts' | 'keys' | 'cloud';
interface Tab {
  id: string;
  serverId: string;
  reconnectKey: number;
}
interface Toast {
  id: number;
  msg: string;
  kind: 'ok' | 'error';
}
const HOME = 'home';

export function App() {
  const [phase, setPhase] = useState<Phase>('loading');
  const [vaultExists, setVaultExists] = useState(true);
  const [servers, setServers] = useState<Server[]>([]);
  const [keys, setKeys] = useState<KeyInfo[]>([]);
  const [syncInfo, setSyncInfo] = useState<SyncInfo | null>(null);
  const [syncStatus, setSyncStatus] = useState<SyncStatus>({ state: 'off', message: '' });
  const [tabs, setTabs] = useState<Tab[]>([]);
  const [statuses, setStatuses] = useState<Record<string, SessionStatus>>({});
  const [active, setActive] = useState<string>(HOME);
  const [section, setSection] = useState<Section>('hosts');
  const [filter, setFilter] = useState('');
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [editing, setEditing] = useState<Server | null>(null);
  const [toasts, setToasts] = useState<Toast[]>([]);

  const byId = useMemo(() => new Map(servers.map((s) => [s.id, s])), [servers]);
  const groups = useMemo(() => [...new Set(servers.map((s) => s.group).filter(Boolean))].sort(), [servers]);
  const openServerIds = useMemo(() => new Set(tabs.map((t) => t.serverId)), [tabs]);

  // ---------------------------------------------------------------- Toasts
  const toast = useCallback((msg: string, kind: 'ok' | 'error' = 'ok') => {
    const id = Date.now() + Math.random();
    setToasts((prev) => [...prev, { id, msg, kind }]);
    setTimeout(() => setToasts((prev) => prev.filter((t) => t.id !== id)), kind === 'error' ? 7000 : 3200);
  }, []);

  // ---------------------------------------------------------------- Laden
  const refresh = useCallback(async () => {
    const [s, k, sync] = await Promise.all([api.serversList(), api.keysList(), api.syncGet()]);
    setServers(s);
    setKeys(k);
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

  // Ereignisse vom Host, die nicht zu einer bestimmten Session gehören
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
    setTabs([]);
    setStatuses({});
    setActive(HOME);
    setVaultExists(true);
    setPhase('locked');
  }

  // ---------------------------------------------------------------- Tabs
  const goTo = useCallback((s: Section) => {
    setSection(s);
    setActive(HOME);
  }, []);

  const open = useCallback(
    async (s: Server, forceNew = false) => {
      setPaletteOpen(false);
      const existing = forceNew ? undefined : tabs.find((t) => t.serverId === s.id);
      if (existing) setActive(existing.id);
      else {
        const t: Tab = { id: crypto.randomUUID(), serverId: s.id, reconnectKey: 0 };
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
      if (s) void open(s, true);
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
      } else if (key === 'n' && e.shiftKey) setEditing(emptyServer());
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
  }, [phase, tabs, active, cycle, closeTab, duplicate]);

  // RDP ist ein natives Fenster über dem UI -> bei Overlays kurz ausblenden
  const overlayOpen = paletteOpen || editing !== null;
  useEffect(() => {
    if (isNative && phase === 'ready') void api.rdpSuspend(overlayOpen).catch(() => undefined);
  }, [overlayOpen, phase]);

  // ---------------------------------------------------------------- Speichern / Löschen / Import
  async function save(s: Server) {
    const saved = await api.serversSave(s);
    setServers((prev) => (prev.some((x) => x.id === saved.id) ? prev.map((x) => (x.id === saved.id ? saved : x)) : [...prev, saved]));
    setEditing(null);
  }

  async function remove(s: Server) {
    await api.serversDelete(s.id);
    setServers((prev) => prev.filter((x) => x.id !== s.id));
    setTabs((prev) => prev.filter((t) => t.serverId !== s.id));
    setActive(HOME);
    setEditing(null);
  }

  async function toggleFav(s: Server) {
    try {
      await save({ ...s, favorite: !s.favorite });
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  async function importFile(file: File) {
    try {
      const { imported, skipped } = await api.importMremoteng(await file.text());
      await refresh();
      toast(`${imported} Host${imported === 1 ? '' : 's'} importiert${skipped ? `, ${skipped} übersprungen (nicht unterstützt)` : ''}. Passwörter bitte neu eintragen.`);
    } catch (e) {
      toast((e as Error).message, 'error');
    }
  }

  function onPaletteAction(a: PaletteAction) {
    setPaletteOpen(false);
    if (a === 'new') setEditing(emptyServer());
    else if (a === 'keys') goTo('keys');
    else if (a === 'cloud') goTo('cloud');
    else void lock();
  }

  // ---------------------------------------------------------------- Render
  if (phase === 'loading') return <div className="center muted">Lade…</div>;
  if (phase === 'locked')
    return (
      <Unlock
        exists={vaultExists}
        onDone={onUnlocked}
        onVaultInstalled={() => {
          setVaultExists(true);
        }}
      />
    );

  return (
    <div className="app">
      <Sidebar
        servers={servers}
        filter={filter}
        setFilter={setFilter}
        statuses={statuses}
        tabs={tabs}
        activeServerId={tabs.find((t) => t.id === active)?.serverId}
        section={section}
        onHome={goTo}
        homeActive={active === HOME}
        syncStatus={syncStatus}
        syncConfigured={!!syncInfo?.configured}
        onOpen={open}
        onEdit={setEditing}
        onNew={() => setEditing(emptyServer())}
        onPalette={() => setPaletteOpen(true)}
        onLock={lock}
      />

      <main>
        <div className="tabbar">
          <button className={'tab home' + (active === HOME ? ' on' : '')} onClick={() => setActive(HOME)}>
            <IconServer size={15} /> Start
          </button>
          {tabs.map((t) => {
            const s = byId.get(t.serverId);
            if (!s) return null;
            const st = statuses[t.id] ?? 'connecting';
            return (
              <button
                key={t.id}
                className={'tab' + (active === t.id ? ' on' : '')}
                title={`${s.name} – ${s.host}`}
                onClick={() => setActive(t.id)}
                onAuxClick={(e) => e.button === 1 && closeTab(t.id)}
              >
                <span className={`status-dot ${st}`} />
                <span className={`proto-ico sm ${s.protocol}`}>
                  <ProtocolIcon protocol={s.protocol} size={13} />
                </span>
                <span className="tab-name">{s.name}</span>
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
          <button className="tab add" title="Neue Verbindung (Ctrl+K)" onClick={() => setPaletteOpen(true)}>
            <IconPlus size={16} />
          </button>
        </div>

        <div className="content">
          <div className={'view start' + (active === HOME ? ' on' : '')}>
            {section === 'hosts' && (
              <HostsPage
                servers={servers}
                query={filter}
                onOpen={open}
                onEdit={setEditing}
                onFav={toggleFav}
                onNew={() => setEditing(emptyServer())}
                onImport={importFile}
              />
            )}
            {section === 'keys' && <Keychain keys={keys} servers={servers} onChanged={refresh} toast={toast} />}
            {section === 'cloud' && <CloudPanel info={syncInfo} status={syncStatus} onChanged={refresh} toast={toast} />}
          </div>

          {tabs.map((t) => {
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
              onEdit: () => setEditing(s),
            };
            return (
              <div key={t.id} className={'view' + (active === t.id ? ' on' : '')}>
                {s.protocol === 'ssh' && <TerminalView {...common} />}
                {s.protocol === 'vnc' && <VncView {...common} />}
                {s.protocol === 'rdp' && <RdpView {...common} />}
              </div>
            );
          })}
        </div>
      </main>

      {paletteOpen && (
        <CommandPalette
          servers={servers}
          openServerIds={openServerIds}
          onOpen={(s, forceNew) => void open(s, forceNew)}
          onAction={onPaletteAction}
          onClose={() => setPaletteOpen(false)}
        />
      )}
      {editing && (
        <ServerForm
          initial={editing}
          groups={groups}
          keys={keys}
          onSave={save}
          onDelete={remove}
          onClose={() => setEditing(null)}
          onGoToKeys={() => {
            setEditing(null);
            goTo('keys');
          }}
          toast={toast}
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

// ==================================================================== Sidebar

function Sidebar(props: {
  servers: Server[];
  filter: string;
  setFilter: (v: string) => void;
  statuses: Record<string, SessionStatus>;
  tabs: Tab[];
  activeServerId?: string;
  section: Section;
  homeActive: boolean;
  onHome: (s: Section) => void;
  syncStatus: SyncStatus;
  syncConfigured: boolean;
  onOpen: (s: Server, forceNew?: boolean) => void;
  onEdit: (s: Server) => void;
  onNew: () => void;
  onPalette: () => void;
  onLock: () => void;
}) {
  const { servers, filter } = props;
  const [collapsed, setCollapsed] = useState<Set<string>>(new Set());

  const shown = useMemo(() => (filter ? filterServers(servers, filter) : servers), [servers, filter]);
  const favorites = shown.filter((s) => s.favorite);
  const grouped = useMemo(() => {
    const m = new Map<string, Server[]>();
    for (const s of [...shown].filter((x) => !x.favorite).sort((a, b) => a.name.localeCompare(b.name))) {
      const g = s.group || 'Ohne Gruppe';
      m.set(g, [...(m.get(g) ?? []), s]);
    }
    return [...m.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [shown]);

  const toggle = (g: string) =>
    setCollapsed((prev) => {
      const n = new Set(prev);
      if (n.has(g)) n.delete(g);
      else n.add(g);
      return n;
    });

  const sessionStatus = (serverId: string): SessionStatus | undefined => {
    const t = props.tabs.find((x) => x.serverId === serverId);
    return t ? props.statuses[t.id] ?? 'connecting' : undefined;
  };

  const row = (s: Server, keyPrefix = '') => {
    const st = sessionStatus(s.id);
    return (
      <div
        key={keyPrefix + s.id}
        className={'srv' + (props.activeServerId === s.id ? ' on' : '')}
        onClick={(e) => props.onOpen(s, e.shiftKey)}
        title={`${s.name} – ${s.host}`}
      >
        <span className={`proto-ico ${s.protocol}`}>
          <ProtocolIcon protocol={s.protocol} size={15} />
        </span>
        <span className="srv-text">
          <span className="sname">{s.name}</span>
          <span className="shost">
            {s.username ? `${s.username}@` : ''}
            {s.host}
          </span>
        </span>
        {st && <span className={`status-dot ${st}`} />}
      </div>
    );
  };

  const syncText =
    !props.syncConfigured ? 'Cloud einrichten' : props.syncStatus.state === 'syncing' ? 'Synchronisiere…' : props.syncStatus.state === 'error' ? 'Sync-Fehler' : 'Synchronisiert';
  const syncClass = !props.syncConfigured ? 'off' : props.syncStatus.state === 'error' ? 'error' : props.syncStatus.state === 'syncing' ? 'connecting' : 'connected';

  return (
    <aside>
      <div className="brand">
        <span className="logo-mark">
          <IconServer size={15} />
        </span>
        MultiServerView
      </div>

      <button className="palette-btn" onClick={props.onPalette}>
        <IconSearch size={15} />
        <span className="grow">Schnell verbinden</span>
        <kbd>Ctrl K</kbd>
      </button>

      <div className="sb-tools">
        <input placeholder="Hosts filtern…" value={filter} onChange={(e) => props.setFilter(e.target.value)} />
        <button title="Neuer Host (Ctrl+Shift+N)" onClick={props.onNew}>
          <IconPlus size={16} />
        </button>
      </div>

      <div className="sb-scroll">
        {favorites.length > 0 && (
          <section>
            <h3>Favoriten</h3>
            {favorites.map((s) => row(s, 'fav-'))}
          </section>
        )}
        {grouped.map(([g, list]) => (
          <section key={g}>
            <h3 className="clickable" onClick={() => toggle(g)}>
              <IconChevron size={12} open={!collapsed.has(g)} /> {g} <span className="count">{list.length}</span>
            </h3>
            {!collapsed.has(g) && list.map((s) => row(s))}
          </section>
        ))}
        {shown.length === 0 && <div className="muted pad">Keine Hosts.</div>}
      </div>

      <nav className="sb-nav">
        <button className={props.homeActive && props.section === 'hosts' ? 'on' : ''} onClick={() => props.onHome('hosts')}>
          <IconServer size={16} /> Hosts
        </button>
        <button className={props.homeActive && props.section === 'keys' ? 'on' : ''} onClick={() => props.onHome('keys')}>
          <IconKey size={16} /> Schlüssel
        </button>
        <button className={props.homeActive && props.section === 'cloud' ? 'on' : ''} onClick={() => props.onHome('cloud')}>
          <IconCloud size={16} /> Cloud
        </button>
      </nav>

      <div className="sb-foot">
        <button className={`sync-chip ${syncClass}`} onClick={() => props.onHome('cloud')} title={props.syncStatus.message}>
          <span className="dot-sm" /> {syncText}
        </button>
        <span className="grow" />
        <button className="ghost icon-only" title="Vault sperren" onClick={props.onLock}>
          <IconLock size={16} />
        </button>
      </div>
    </aside>
  );
}

