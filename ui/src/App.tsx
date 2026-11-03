import { useCallback, useEffect, useMemo, useState } from 'react';
import { api, isNative } from './bridge';
import { emptyServer, type Server } from './types';
import { filterServers, recency, timeAgo } from './search';
import { Unlock } from './Unlock';
import { CommandPalette } from './CommandPalette';
import { ServerForm } from './ServerForm';

type Phase = 'loading' | 'locked' | 'ready';
interface Tab {
  id: string;
  serverId: string;
}
const HOME = 'home';

export function App() {
  const [phase, setPhase] = useState<Phase>('loading');
  const [vaultExists, setVaultExists] = useState(true);
  const [servers, setServers] = useState<Server[]>([]);
  const [tabs, setTabs] = useState<Tab[]>([]);
  const [active, setActive] = useState<string>(HOME);
  const [filter, setFilter] = useState('');
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [editing, setEditing] = useState<Server | null>(null);

  const byId = useMemo(() => new Map(servers.map((s) => [s.id, s])), [servers]);
  const groups = useMemo(() => [...new Set(servers.map((s) => s.group).filter(Boolean))].sort(), [servers]);

  // ---------------------------------------------------------------- Laden / Sperren
  const refresh = useCallback(async () => setServers(await api.serversList()), []);

  useEffect(() => {
    api.vaultStatus().then(async (st) => {
      setVaultExists(st.exists);
      if (st.unlocked) {
        await refresh();
        setPhase('ready');
      } else setPhase('locked');
    });
  }, [refresh]);

  async function onUnlocked() {
    await refresh();
    setPhase('ready');
  }

  async function lock() {
    await api.vaultLock();
    setServers([]);
    setTabs([]);
    setActive(HOME);
    setVaultExists(true);
    setPhase('locked');
  }

  // ---------------------------------------------------------------- Server oeffnen / Tabs
  const open = useCallback(
    async (s: Server) => {
      setPaletteOpen(false);
      const existing = tabs.find((t) => t.serverId === s.id);
      if (existing) setActive(existing.id);
      else {
        const t: Tab = { id: crypto.randomUUID(), serverId: s.id };
        setTabs((prev) => [...prev, t]);
        setActive(t.id);
      }
      const updated = await api.serversTouch(s.id);
      setServers((prev) => prev.map((x) => (x.id === updated.id ? updated : x)));
    },
    [tabs],
  );

  const closeTab = useCallback(
    (id: string) => {
      setTabs((prev) => {
        const i = prev.findIndex((t) => t.id === id);
        const next = prev.filter((t) => t.id !== id);
        if (active === id) setActive(next[Math.min(i, next.length - 1)]?.id ?? HOME);
        return next;
      });
    },
    [active],
  );

  const cycle = useCallback(
    (dir: 1 | -1) => {
      const order = [HOME, ...tabs.map((t) => t.id)];
      const i = order.indexOf(active);
      setActive(order[(i + dir + order.length) % order.length]);
    },
    [tabs, active],
  );

  // ---------------------------------------------------------------- Shortcuts
  useEffect(() => {
    if (phase !== 'ready') return;
    function onKey(e: KeyboardEvent) {
      const mod = e.ctrlKey || e.metaKey;
      if (mod && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        setPaletteOpen((o) => !o);
      } else if (mod && e.key === 'Tab') {
        e.preventDefault();
        cycle(e.shiftKey ? -1 : 1);
      } else if (mod && /^[1-9]$/.test(e.key)) {
        e.preventDefault();
        const n = Number(e.key);
        setActive(n === 1 ? HOME : (tabs[n - 2]?.id ?? active));
      } else if (mod && e.key.toLowerCase() === 'w' && active !== HOME) {
        e.preventDefault();
        closeTab(active);
      } else if (mod && e.key.toLowerCase() === 'n') {
        e.preventDefault();
        setEditing(emptyServer());
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [phase, tabs, active, cycle, closeTab]);

  // ---------------------------------------------------------------- Speichern / Loeschen
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
    await save({ ...s, favorite: !s.favorite });
  }

  // ---------------------------------------------------------------- Render
  if (phase === 'loading') return <div className="center muted">Lade…</div>;
  if (phase === 'locked') return <Unlock exists={vaultExists} onDone={onUnlocked} />;

  const activeTab = tabs.find((t) => t.id === active);
  const activeServer = activeTab ? byId.get(activeTab.serverId) : undefined;

  return (
    <div className="app">
      <Sidebar
        servers={servers}
        filter={filter}
        setFilter={setFilter}
        openServerIds={new Set(tabs.map((t) => t.serverId))}
        activeServerId={activeServer?.id}
        onOpen={open}
        onEdit={setEditing}
        onNew={() => setEditing(emptyServer())}
        onPalette={() => setPaletteOpen(true)}
        onLock={lock}
      />

      <main>
        <div className="tabbar">
          <button className={'tab' + (active === HOME ? ' on' : '')} onClick={() => setActive(HOME)}>
            Übersicht
          </button>
          {tabs.map((t) => {
            const s = byId.get(t.serverId);
            if (!s) return null;
            return (
              <button
                key={t.id}
                className={'tab' + (active === t.id ? ' on' : '')}
                onClick={() => setActive(t.id)}
                onAuxClick={(e) => e.button === 1 && closeTab(t.id)}
              >
                <span className={`badge ${s.protocol}`}>{s.protocol}</span>
                {s.name}
                <span
                  className="x"
                  onClick={(e) => {
                    e.stopPropagation();
                    closeTab(t.id);
                  }}
                >
                  ×
                </span>
              </button>
            );
          })}
        </div>

        <div className="content">
          {activeServer ? (
            <SessionPlaceholder server={activeServer} onEdit={() => setEditing(activeServer)} />
          ) : (
            <Home servers={servers} onOpen={open} onEdit={setEditing} onFav={toggleFav} onNew={() => setEditing(emptyServer())} />
          )}
        </div>
      </main>

      {paletteOpen && (
        <CommandPalette
          servers={servers}
          onOpen={open}
          onNew={() => {
            setPaletteOpen(false);
            setEditing(emptyServer());
          }}
          onClose={() => setPaletteOpen(false)}
        />
      )}
      {editing && (
        <ServerForm initial={editing} groups={groups} onSave={save} onDelete={remove} onClose={() => setEditing(null)} />
      )}
      {!isNative && <div className="devbadge">Browser-Mock (unverschlüsselt)</div>}
    </div>
  );
}

// ==================================================================== Sidebar

function Sidebar(props: {
  servers: Server[];
  filter: string;
  setFilter: (v: string) => void;
  openServerIds: Set<string>;
  activeServerId?: string;
  onOpen: (s: Server) => void;
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
    for (const s of shown) {
      const g = s.group || 'Ohne Gruppe';
      m.set(g, [...(m.get(g) ?? []), s]);
    }
    return [...m.entries()].sort(([a], [b]) => a.localeCompare(b));
  }, [shown]);

  const toggle = (g: string) =>
    setCollapsed((prev) => {
      const n = new Set(prev);
      n.has(g) ? n.delete(g) : n.add(g);
      return n;
    });

  const row = (s: Server) => (
    <div
      key={s.id}
      className={'srv' + (props.activeServerId === s.id ? ' on' : '')}
      onDoubleClick={() => props.onOpen(s)}
      onClick={() => props.onOpen(s)}
    >
      <span className={`dot ${props.openServerIds.has(s.id) ? 'live' : ''}`} />
      <span className={`badge ${s.protocol}`}>{s.protocol}</span>
      <span className="sname">{s.name}</span>
      <button
        className="edit"
        title="Bearbeiten"
        onClick={(e) => {
          e.stopPropagation();
          props.onEdit(s);
        }}
      >
        ✎
      </button>
    </div>
  );

  return (
    <aside>
      <div className="brand">
        <span className="logo sm">▣</span> MultiServerView
      </div>
      <div className="sb-tools">
        <input placeholder="Filtern…" value={filter} onChange={(e) => props.setFilter(e.target.value)} />
        <button title="Neuer Server (Ctrl+N)" onClick={props.onNew}>
          +
        </button>
      </div>
      <button className="palette-btn" onClick={props.onPalette}>
        Schnell verbinden <kbd>Ctrl K</kbd>
      </button>

      <div className="sb-scroll">
        {favorites.length > 0 && (
          <section>
            <h3>★ Favoriten</h3>
            {favorites.map(row)}
          </section>
        )}
        {grouped.map(([g, list]) => (
          <section key={g}>
            <h3 className="clickable" onClick={() => toggle(g)}>
              {collapsed.has(g) ? '▸' : '▾'} {g} <span className="count">{list.length}</span>
            </h3>
            {!collapsed.has(g) && list.map(row)}
          </section>
        ))}
        {shown.length === 0 && <div className="muted pad">Keine Server.</div>}
      </div>

      <button className="lock" onClick={props.onLock}>
        🔒 Sperren
      </button>
    </aside>
  );
}

// ==================================================================== Home

function Home({
  servers,
  onOpen,
  onEdit,
  onFav,
  onNew,
}: {
  servers: Server[];
  onOpen: (s: Server) => void;
  onEdit: (s: Server) => void;
  onFav: (s: Server) => void;
  onNew: () => void;
}) {
  const recent = useMemo(
    () => [...servers].filter((s) => s.lastUsed).sort((a, b) => recency(b) - recency(a)).slice(0, 6),
    [servers],
  );

  if (servers.length === 0)
    return (
      <div className="empty">
        <h2>Noch keine Server</h2>
        <p className="muted">Lege deinen ersten Server an – oder importiere später aus Termius / mRemoteNG.</p>
        <button className="primary" onClick={onNew}>
          Server hinzufügen
        </button>
      </div>
    );

  const card = (s: Server) => (
    <div key={s.id} className="card" onClick={() => onOpen(s)}>
      <div className="card-top">
        <span className={`badge ${s.protocol}`}>{s.protocol}</span>
        <button
          className={'star' + (s.favorite ? ' on' : '')}
          title="Favorit"
          onClick={(e) => {
            e.stopPropagation();
            onFav(s);
          }}
        >
          ★
        </button>
        <button
          className="edit"
          title="Bearbeiten"
          onClick={(e) => {
            e.stopPropagation();
            onEdit(s);
          }}
        >
          ✎
        </button>
      </div>
      <div className="card-name">{s.name}</div>
      <div className="muted card-host">
        {s.username ? `${s.username}@` : ''}
        {s.host}:{s.port}
      </div>
      <div className="card-foot muted">
        {s.group && <span className="chip">{s.group}</span>}
        <span>{timeAgo(s.lastUsed)}</span>
      </div>
    </div>
  );

  return (
    <div className="home">
      {recent.length > 0 && (
        <>
          <h2>Zuletzt benutzt</h2>
          <div className="grid">{recent.map(card)}</div>
        </>
      )}
      <h2>Alle Server</h2>
      <div className="grid">{[...servers].sort((a, b) => a.name.localeCompare(b.name)).map(card)}</div>
    </div>
  );
}

// ==================================================================== Session (Platzhalter)

function SessionPlaceholder({ server, onEdit }: { server: Server; onEdit: () => void }) {
  const phase = server.protocol === 'ssh' ? 'Phase 2 (SSH-Terminal)' : server.protocol === 'vnc' ? 'Phase 4 (VNC)' : 'Phase 5 (RDP)';
  return (
    <div className="empty">
      <span className={`badge big ${server.protocol}`}>{server.protocol}</span>
      <h2>{server.name}</h2>
      <p className="muted">
        {server.username ? `${server.username}@` : ''}
        {server.host}:{server.port}
      </p>
      <p className="muted">Hier erscheint die Live-Session. Kommt in {phase}.</p>
      <button onClick={onEdit}>Bearbeiten</button>
    </div>
  );
}
