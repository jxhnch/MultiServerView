import { useEffect, useMemo, useState } from 'react';
import { getUiSettings, setUiSettings } from './uiSettings';
import { IconEdit, IconFolder, IconGrid, IconPlus, IconPower, IconRefresh, IconSearch, IconServer, IconStar, IconTrash, IconDuplicate, IconPlay, IconTerminal } from './Icons';
import { api, type BulkLogin, type VmAction } from './bridge';
import { BulkLoginDialog } from './BulkLoginDialog';
import { HostTile } from './HostTile';
import { ContextMenu, type MenuItem } from './ContextMenu';
import { PromptDialog } from './Prompt';
import { filterServers, parseTarget } from './search';
import type { Identity, KeyInfo, Server } from './types';

const REACH_TEXT = { up: 'Answers on its port', closed: 'The machine answers, but the port is closed', down: 'No answer' } as const;

/** The user's own order first (drag and drop), everything not placed yet by name. */
const byPlace = (a: Server, b: Server) => (a.sortOrder || 1e9) - (b.sortOrder || 1e9) || a.name.localeCompare(b.name);

/** Termius style: search bar on top, plain cards below ("ssh, root"). */
export function HostsPage({
  servers,
  query,
  setQuery,
  selectedId,
  openServerIds,
  onSelect,
  onConnect,
  onSftp,
  onFav,
  onDuplicateHost,
  onDelete,
  onNew,
  onQuick,
  onSplit,
  workspace,
  workspaces,
  onWorkspace,
  onCreateWorkspace,
  onRenameWorkspace,
  onDissolveWorkspace,
  onMove,
  vmStates,
  onVmPower,
  keys,
  identities,
  onReorder,
  onBulkLogin,
  onDeleteMany,
  shells,
  onShell,
  visible,
  onWake,
}: {
  /** The host list is on screen (only then the hosts are checked). */
  visible: boolean;
  onWake: (s: Server) => void;
  /** Shells of this PC that can be opened in a tab. */
  shells: Server[];
  onShell: (shell: Server) => void;
  keys: KeyInfo[];
  identities: Identity[];
  onReorder: (ids: string[], reset?: boolean) => void;
  onBulkLogin: (list: Server[], login: BulkLogin) => void;
  onDeleteMany: (list: Server[]) => Promise<boolean>;
  servers: Server[];
  /** Power state of VMs imported from Proxmox or VMware (host id -> state). */
  vmStates: Record<string, string>;
  onVmPower: (s: Server, action: VmAction) => void;
  query: string;
  setQuery: (q: string) => void;
  selectedId?: string;
  openServerIds: Set<string>;
  onSelect: (s: Server) => void;
  onConnect: (s: Server, forceNew: boolean) => void;
  onSftp: (s: Server) => void;
  onFav: (s: Server) => void;
  onDuplicateHost: (s: Server) => void;
  onDelete: (s: Server) => void;
  onNew: () => void;
  onQuick: (draft: Partial<Server>) => void;
  onSplit: () => void;
  /** Selected workspace ('' = all). */
  workspace: string;
  workspaces: string[];
  onWorkspace: (w: string) => void;
  onCreateWorkspace: (name: string) => void;
  onRenameWorkspace: (from: string, to: string) => void;
  onDissolveWorkspace: (name: string) => void;
  onMove: (list: Server[], target: string) => void;
}) {
  const [picked, setPicked] = useState<string | null>(null);
  const [menu, setMenu] = useState<{ x: number; y: number; server: Server } | null>(null);
  const [powerMenu, setPowerMenu] = useState<{ x: number; y: number; server: Server } | null>(null);
  const [wsMenu, setWsMenu] = useState<{ x: number; y: number; name: string } | null>(null);
  const [wsDialog, setWsDialog] = useState<{ kind: 'new' } | { kind: 'rename'; name: string } | { kind: 'move'; server: Server } | null>(null);
  const [dropWs, setDropWs] = useState<string | null>(null);
  const [shellMenu, setShellMenu] = useState<{ x: number; y: number } | null>(null);
  // Several hosts at once: Ctrl+click, or the "Select" button and then plain clicks.
  const [selecting, setSelecting] = useState(false);
  const [marked, setMarked] = useState<Set<string>>(new Set());
  const [bulk, setBulk] = useState<'login' | 'move' | null>(null);
  const markedHosts = servers.filter((s) => marked.has(s.id));
  const toggleMark = (id: string) =>
    setMarked((old) => {
      const next = new Set(old);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  const stopSelecting = () => {
    setSelecting(false);
    setMarked(new Set());
  };
  // Drag and drop inside a section changes the order.
  const [dragId, setDragId] = useState<string | null>(null);
  const [dropAt, setDropAt] = useState<{ id: string; after: boolean } | null>(null);
  const endDrag = () => {
    setDragId(null);
    setDropAt(null);
  };
  const q = query.trim();

  // With search text, all workspaces are searched; otherwise only the selected one is shown.
  const filtered = useMemo(() => {
    if (q) return filterServers(servers, q);
    const scope = workspace ? servers.filter((s) => s.workspace === workspace) : servers;
    return [...scope].sort(byPlace);
  }, [servers, q, workspace]);
  const showWorkspace = !workspace || q.length > 0;
  const countOf = (w: string) => servers.filter((s) => s.workspace === w).length;

  // Groups work like folders; without groups, "Hosts" is shown above.
  const grouped = useMemo(() => {
    // Key: (workspace, group). The workspace part only exists in the "All" view or during a search.
    const m = new Map<string, { ws: string; group: string; list: Server[] }>();
    for (const s of filtered) {
      const ws = showWorkspace ? s.workspace : '';
      const key = ws + '\u0000' + s.group;
      const entry = m.get(key) ?? { ws, group: s.group, list: [] };
      entry.list.push(s);
      m.set(key, entry);
    }
    const parts = [...m.values()];
    const hasGroups = parts.some((p) => p.group !== '');
    const hasWorkspaces = parts.some((p) => p.ws !== '');
    const label = (p: { ws: string; group: string }) =>
      p.ws && p.group ? `${p.ws} · ${p.group}` : p.ws || p.group || (hasWorkspaces ? 'No workspace' : hasGroups ? 'No group' : 'Hosts');
    const rank = (p: { ws: string; group: string }) => (p.ws === '' && p.group === '' ? 1 : 0);
    return parts
      .sort((a, b) => rank(a) - rank(b) || a.ws.localeCompare(b.ws) || a.group.localeCompare(b.group))
      .map((p) => [label(p), p.list] as const);
  }, [filtered, showWorkspace]);

  // Which of the listed hosts answer: checked while the list is on screen, then once a minute.
  const [showStatus, setShowStatus] = useState(() => getUiSettings().hostStatus);
  const [reach, setReach] = useState<Record<string, 'up' | 'closed' | 'down'>>({});
  const checkKey = filtered.map((s) => `${s.id}\t${s.host}\t${s.port}\t${s.protocol}\t${s.jumpHostId}`).join('\n');
  useEffect(() => {
    if (!showStatus || !visible || !checkKey) return;
    const ids = checkKey.split('\n').map((line) => line.split('\t')[0]);
    let cancelled = false;
    let busy = false;
    const check = () => {
      if (busy || document.hidden) return;
      busy = true;
      api
        .hostsReachability(ids)
        .then((found) => {
          if (cancelled) return;
          setReach((old) => {
            const next = { ...old };
            for (const id of ids) delete next[id];
            return { ...next, ...found };
          });
        })
        .catch(() => undefined)
        .finally(() => (busy = false));
    };
    const first = window.setTimeout(check, 500); // not while the search is still being typed
    const again = window.setInterval(check, 60_000);
    return () => {
      cancelled = true;
      window.clearTimeout(first);
      window.clearInterval(again);
    };
  }, [showStatus, visible, checkKey]);

  const target = q ? parseTarget(q) : null;
  const canConnect = q.length > 0 && (filtered.length > 0 || target !== null);

  function submit(forceNew = false) {
    if (!q) return;
    if (filtered.length > 0) onConnect(filtered[0], forceNew);
    else if (target) onQuick(target);
  }

  const sub = (s: Server) => (s.protocol === 'serial' ? `serial, ${s.host} ${s.port} baud` : `${s.protocol}, ${s.username || s.host}`);

  return (
    <div className="hosts-main">
      <div className="searchbar">
        <IconSearch size={16} />
        <input
          autoFocus
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key !== 'Enter') return;
            e.preventDefault(); // prevents the Enter from landing in the panel that opens right away
            submit(e.shiftKey);
          }}
          placeholder="Search a host or type  ssh user@hostname …"
        />
        <button className={canConnect ? 'ready' : ''} disabled={!canConnect} onClick={() => submit()}>
          Connect
        </button>
      </div>

      <div className="toolbar ws-bar">
        <button
          className={'tool' + (!workspace ? ' on' : '')}
          onClick={() => onWorkspace('')}
          onDragOver={(e) => e.preventDefault()}
          onDrop={(e) => {
            e.preventDefault();
            const id = e.dataTransfer.getData('text/msv-host');
            const dragged = servers.find((x) => x.id === id);
            if (dragged) onMove([dragged], '');
          }}
          title="Show all hosts (drag a host here = remove it from its workspace)"
        >
          All <span className="ws-count">{servers.length}</span>
        </button>
        {workspaces.map((w) => (
          <button
            key={w}
            className={'tool' + (workspace === w ? ' on' : '') + (dropWs === w ? ' drop' : '')}
            onClick={() => onWorkspace(w)}
            onContextMenu={(e) => {
              e.preventDefault();
              setWsMenu({ x: e.clientX, y: e.clientY, name: w });
            }}
            onDragOver={(e) => {
              e.preventDefault();
              if (dropWs !== w) setDropWs(w);
            }}
            onDragLeave={() => setDropWs((d) => (d === w ? null : d))}
            onDrop={(e) => {
              e.preventDefault();
              setDropWs(null);
              const id = e.dataTransfer.getData('text/msv-host');
              const dragged = servers.find((x) => x.id === id);
              if (dragged) onMove([dragged], w);
            }}
            title="Right-click: rename or dissolve · drag hosts here to move them"
          >
            {w} <span className="ws-count">{countOf(w)}</span>
          </button>
        ))}
        <button className="tool" onClick={() => setWsDialog({ kind: 'new' })} title="Create a new workspace (e.g. per teacher or customer)">
          <IconPlus size={14} /> Workspace
        </button>
      </div>

      <div className="toolbar">
        <button className="primary" onClick={onNew}>
          <IconPlus size={15} /> New host
        </button>
        {shells.length > 0 && (
          <button
            className="tool"
            title="A shell of this PC in a tab"
            onClick={(e) => {
              if (shells.length === 1) return onShell(shells[0]);
              const r = e.currentTarget.getBoundingClientRect();
              setShellMenu({ x: r.left, y: r.bottom + 4 });
            }}
          >
            <IconTerminal size={15} /> Local terminal
          </button>
        )}
        <button className="tool" title="Several terminals side by side in one tab" onClick={onSplit}>
          <IconGrid size={15} /> Split view
        </button>
        <button
          className={'tool' + (showStatus ? ' on' : '')}
          title="Show which hosts answer (a short connection to their port once a minute, no login)"
          aria-pressed={showStatus}
          onClick={() => {
            setUiSettings({ hostStatus: !showStatus });
            setShowStatus(!showStatus);
            setReach({});
          }}
        >
          Online status
        </button>
        <button className={'tool' + (selecting ? ' on' : '')} title="Select several hosts (or Ctrl+click on them)" onClick={() => (selecting ? stopSelecting() : setSelecting(true))}>
          Select
        </button>
      </div>

      {(selecting || markedHosts.length > 0) && (
        <div className="bulk-bar">
          <strong>{markedHosts.length} selected</strong>
          <button onClick={() => setMarked(new Set(filtered.map((s) => s.id)))}>Select all shown</button>
          <span className="grow" />
          <button className="primary" disabled={!markedHosts.length} onClick={() => setBulk('login')}>
            Set login…
          </button>
          <button disabled={!markedHosts.length} onClick={() => setBulk('move')}>
            Move to workspace…
          </button>
          <button
            className="danger"
            disabled={!markedHosts.length}
            onClick={() => void onDeleteMany(markedHosts).then((deleted) => deleted && stopSelecting())}
          >
            Delete…
          </button>
          <button onClick={stopSelecting}>Done</button>
        </div>
      )}

      {servers.length === 0 ? (
        <div className="empty-card">
          <IconServer size={30} />
          <h3>No hosts yet</h3>
          <p>Add a host, type "ssh user@server" above, or bring in hosts via "Import" in the left menu (Proxmox, VMware, mRemoteNG, PuTTY, CSV).</p>
        </div>
      ) : (
        <>
          {filtered.length === 0 && workspace && !q && (
            <p className="muted">
              The workspace "{workspace}" has no host yet. "New host" creates one here, or drag hosts from "All" onto the tab above
              (right-click a host: "Move to workspace…").
            </p>
          )}
          {filtered.length === 0 && (!workspace || q) && (
            <p className="muted">
              No host found.{target ? ' Enter creates "' + (target.host ?? '') + '" and connects.' : ''}
            </p>
          )}
          {grouped.map(([name, list]) => (
            <section key={name}>
              <div className="sec-label">{name}</div>
              <div className="hgrid" style={{ marginTop: 10 }}>
                {list.map((s) => (
                  <div
                    key={s.id}
                    className={
                      'hcard' +
                      (selectedId === s.id ? ' sel' : '') +
                      (picked === s.id ? ' picked' : '') +
                      (marked.has(s.id) ? ' marked' : '') +
                      (dragId === s.id ? ' dragging' : '') +
                      (dropAt?.id === s.id ? (dropAt.after ? ' drop-after' : ' drop-before') : '')
                    }
                    aria-selected={marked.has(s.id)}
                    tabIndex={0}
                    draggable
                    onDragStart={(e) => {
                      e.dataTransfer.setData('text/msv-host', s.id);
                      setDragId(s.id);
                    }}
                    onDragEnd={endDrag}
                    onDragOver={(e) => {
                      // only inside the same section, and not while searching (the list is sorted by relevance then)
                      if (q || !dragId || dragId === s.id || !list.some((x) => x.id === dragId)) return;
                      e.preventDefault();
                      const r = e.currentTarget.getBoundingClientRect();
                      const after = e.clientX > r.left + r.width / 2;
                      if (dropAt?.id !== s.id || dropAt.after !== after) setDropAt({ id: s.id, after });
                    }}
                    onDragLeave={() => setDropAt((d) => (d?.id === s.id ? null : d))}
                    onDrop={(e) => {
                      e.preventDefault();
                      const at = dropAt;
                      const from = dragId;
                      endDrag();
                      if (!from || !at || at.id !== s.id) return;
                      const order = list.map((x) => x.id).filter((id) => id !== from);
                      order.splice(order.indexOf(s.id) + (at.after ? 1 : 0), 0, from);
                      onReorder(order);
                    }}
                    title={`${s.name} – ${s.host}:${s.port}`}
                    onClick={(e) => {
                      if (selecting || e.ctrlKey || e.metaKey) toggleMark(s.id);
                      else setPicked(s.id);
                    }}
                    onContextMenu={(e) => {
                      e.preventDefault();
                      setMenu({ x: e.clientX, y: e.clientY, server: s });
                    }}
                    onDoubleClick={(e) => !selecting && !e.ctrlKey && !e.metaKey && onConnect(s, e.shiftKey)}
                    onKeyDown={(e) => {
                      if (e.target !== e.currentTarget) return;
                      if (e.key === ' ') {
                        e.preventDefault();
                        toggleMark(s.id);
                      } else if (e.key === 'Enter') onConnect(s, e.shiftKey);
                    }}
                  >
                    {(selecting || marked.size > 0) && <input type="checkbox" className="hcard-check" tabIndex={-1} readOnly checked={marked.has(s.id)} />}
                    <HostTile server={s} vmState={vmStates[s.id]} />
                    <span className="hcard-text">
                      <span className="hcard-name">{s.name}</span>
                      <span className="hcard-sub">
                        {showStatus && reach[s.id] && <i className={'reach ' + reach[s.id]} role="img" title={REACH_TEXT[reach[s.id]]} aria-label={REACH_TEXT[reach[s.id]]} />}
                        {sub(s)}
                      </span>
                    </span>
                    {s.favorite && (
                      <span className="fav" title="Favorite">
                        <IconStar size={13} filled />
                      </span>
                    )}
                    {openServerIds.has(s.id) && <span className="status-dot connected live-dot" title="Session open" />}
                    {vmStates[s.id] && vmStates[s.id] !== 'unknown' && (
                      <button
                        type="button"
                        className="edit-btn power-btn"
                        title={`Power: ${vmStates[s.id]}`}
                        aria-label={`Power of ${s.name}`}
                        onClick={(e) => {
                          e.stopPropagation();
                          const r = (e.currentTarget as HTMLElement).getBoundingClientRect();
                          setPowerMenu({ x: r.left, y: r.bottom + 4, server: s });
                        }}
                        onDoubleClick={(e) => e.stopPropagation()}
                      >
                        <IconPower size={15} />
                      </button>
                    )}
                    <button
                      type="button"
                      className="edit-btn"
                      title="Edit host"
                      aria-label={`Edit ${s.name}`}
                      onClick={(e) => {
                        e.stopPropagation();
                        setPicked(s.id);
                        onSelect(s);
                      }}
                      onDoubleClick={(e) => e.stopPropagation()}
                    >
                      <IconEdit size={15} />
                    </button>
                  </div>
                ))}
              </div>
            </section>
          ))}
          <p className="muted small">Double-click or Enter connects · drag to arrange · Ctrl+click selects several · pencil edits · right-click for more · Shift opens an additional session</p>
        </>
      )}

      {wsMenu && (
        <ContextMenu
          x={wsMenu.x}
          y={wsMenu.y}
          onClose={() => setWsMenu(null)}
          items={[
            { label: 'Rename…', icon: <IconEdit size={14} />, onClick: () => setWsDialog({ kind: 'rename', name: wsMenu.name }) },
            { label: 'Dissolve workspace', icon: <IconTrash size={14} />, danger: true, separator: true, onClick: () => onDissolveWorkspace(wsMenu.name) },
          ]}
        />
      )}
      {wsDialog?.kind === 'new' && (
        <PromptDialog
          title="New workspace"
          label="Name (e.g. teacher or customer)"
          confirmLabel="Create"
          onClose={() => setWsDialog(null)}
          onSubmit={(v) => {
            setWsDialog(null);
            onCreateWorkspace(v);
          }}
        />
      )}
      {wsDialog?.kind === 'rename' && (
        <PromptDialog
          title="Rename workspace"
          label="New name"
          initial={wsDialog.name}
          confirmLabel="Rename"
          onClose={() => setWsDialog(null)}
          onSubmit={(v) => {
            const from = wsDialog.name;
            setWsDialog(null);
            onRenameWorkspace(from, v);
          }}
        />
      )}
      {wsDialog?.kind === 'move' && (
        <MoveDialog
          title={`Move "${wsDialog.server.name}"`}
          current={wsDialog.server.workspace}
          workspaces={workspaces}
          onClose={() => setWsDialog(null)}
          onPick={(target) => {
            const server = wsDialog.server;
            setWsDialog(null);
            onMove([server], target);
          }}
        />
      )}

      {bulk === 'login' && (
        <BulkLoginDialog
          hosts={markedHosts}
          keys={keys}
          identities={identities}
          onClose={() => setBulk(null)}
          onApply={(login) => {
            setBulk(null);
            onBulkLogin(markedHosts, login);
            stopSelecting();
          }}
        />
      )}
      {bulk === 'move' && (
        <MoveDialog
          title={`Move ${markedHosts.length} host${markedHosts.length === 1 ? '' : 's'}`}
          current={null}
          workspaces={workspaces}
          onClose={() => setBulk(null)}
          onPick={(target) => {
            setBulk(null);
            onMove(markedHosts, target);
            stopSelecting();
          }}
        />
      )}

      {shellMenu && (
        <ContextMenu
          x={shellMenu.x}
          y={shellMenu.y}
          onClose={() => setShellMenu(null)}
          items={shells.map((sh) => ({ label: sh.name, icon: <IconTerminal size={14} />, onClick: () => onShell(sh) }))}
        />
      )}

      {powerMenu && (
        <ContextMenu
          x={powerMenu.x}
          y={powerMenu.y}
          onClose={() => setPowerMenu(null)}
          items={(() => {
            const st = vmStates[powerMenu.server.id];
            const act = (label: string, action: VmAction, extra: Partial<MenuItem> = {}): MenuItem => ({ label, onClick: () => onVmPower(powerMenu.server, action), ...extra });
            return st === 'running'
              ? [
                  act('Shut down (graceful)', 'shutdown'),
                  act('Restart', 'reboot'),
                  act('Suspend', 'suspend'),
                  act('Force stop', 'stop', { danger: true, separator: true }),
                ]
              : [act('Start', 'start')];
          })()}
        />
      )}

      {menu && (
        <ContextMenu
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(null)}
          items={[
            { label: 'Connect', icon: <IconPlay size={14} />, onClick: () => onConnect(menu.server, false) },
            { label: 'Connect in new tab', icon: <IconDuplicate size={14} />, onClick: () => onConnect(menu.server, true) },
            ...(menu.server.protocol === 'ssh' ? [{ label: 'Open SFTP', icon: <IconFolder size={14} />, onClick: () => onSftp(menu.server) }] : []),
            { label: 'Edit details', icon: <IconEdit size={14} />, onClick: () => onSelect(menu.server), separator: true },
            { label: menu.server.favorite ? 'Remove favorite' : 'Mark as favorite', icon: <IconStar size={14} filled={menu.server.favorite} />, onClick: () => onFav(menu.server) },
            { label: 'Move to workspace…', icon: <IconFolder size={14} />, onClick: () => setWsDialog({ kind: 'move', server: menu.server }) },
            ...(menu.server.protocol === 'ssh'
              ? [{ label: 'Detect system (logo)', icon: <IconRefresh size={14} />, onClick: () => void api.hostsDetectOs([menu.server.id]).catch(() => undefined) }]
              : []),
            ...(servers.some((x) => x.protocol === 'ssh' && !x.os)
              ? [
                  {
                    label: 'Detect system of all hosts without a logo',
                    icon: <IconRefresh size={14} />,
                    onClick: () => void api.hostsDetectOs(servers.filter((x) => x.protocol === 'ssh' && !x.os).map((x) => x.id)).catch(() => undefined),
                  },
                ]
              : []),
            { label: 'Duplicate', icon: <IconDuplicate size={14} />, onClick: () => onDuplicateHost(menu.server) },
            ...(menu.server.macAddress ? [{ label: 'Wake up (Wake-on-LAN)', icon: <IconPower size={14} />, onClick: () => onWake(menu.server) }] : []),
            ...(filtered.some((x) => x.sortOrder)
              ? [{ label: 'Sort these hosts by name again', icon: <IconRefresh size={14} />, onClick: () => onReorder(filtered.map((x) => x.id), true) }]
              : []),
            { label: 'Delete', icon: <IconTrash size={14} />, danger: true, separator: true, onClick: () => onDelete(menu.server) },
          ]}
        />
      )}
    </div>
  );
}

/** Picker for "Move to workspace…": existing workspaces, a new one or none. */
function MoveDialog({
  title,
  current,
  workspaces,
  onPick,
  onClose,
}: {
  title: string;
  /** Workspace the hosts are in now; null = several different hosts. */
  current: string | null;
  workspaces: string[];
  onPick: (target: string) => void;
  onClose: () => void;
}) {
  const [name, setName] = useState('');
  return (
    <div className="overlay" onMouseDown={onClose}>
      <form
        className="modal"
        style={{ width: 'min(420px, 92vw)' }}
        onMouseDown={(e) => e.stopPropagation()}
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim()) onPick(name.trim());
        }}
        onKeyDown={(e) => e.key === 'Escape' && onClose()}
      >
        <h2>{title}</h2>
        <div className="pop-list" style={{ width: '100%' }}>
          {workspaces
            .filter((w) => w !== current)
            .map((w) => (
              <button type="button" key={w} className="pop-item" onClick={() => onPick(w)}>
                <span className="pop-name">{w}</span>
              </button>
            ))}
          {current !== '' && (
            <button type="button" className="pop-item" onClick={() => onPick('')}>
              <span className="pop-name">No workspace</span>
            </button>
          )}
        </div>
        <label>
          Or a new workspace
          <input autoFocus value={name} onChange={(e) => setName(e.target.value)} placeholder="Name" />
        </label>
        <div className="actions">
          <span className="grow" />
          <button type="button" onClick={onClose}>
            Cancel
          </button>
          <button className="primary" disabled={!name.trim()}>
            Move
          </button>
        </div>
      </form>
    </div>
  );
}
