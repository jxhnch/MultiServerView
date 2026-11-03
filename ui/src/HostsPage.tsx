import { useMemo, useState } from 'react';
import { IconEdit, IconFolder, IconGrid, IconPlus, IconSearch, IconServer, IconStar, IconTrash, IconDuplicate, IconPlay } from './Icons';
import { HostTile } from './HostTile';
import { ContextMenu } from './ContextMenu';
import { PromptDialog } from './Prompt';
import { filterServers, parseTarget } from './search';
import type { Server } from './types';

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
}: {
  servers: Server[];
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
  const [menu, setMenu] = useState<{ x: number; y: number; server: Server } | null>(null);
  const [wsMenu, setWsMenu] = useState<{ x: number; y: number; name: string } | null>(null);
  const [wsDialog, setWsDialog] = useState<{ kind: 'new' } | { kind: 'rename'; name: string } | { kind: 'move'; server: Server } | null>(null);
  const [dropWs, setDropWs] = useState<string | null>(null);
  const q = query.trim();

  // With search text, all workspaces are searched; otherwise only the selected one is shown.
  const filtered = useMemo(() => {
    if (q) return filterServers(servers, q);
    const scope = workspace ? servers.filter((s) => s.workspace === workspace) : servers;
    return [...scope].sort((a, b) => a.name.localeCompare(b.name));
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

  const target = q ? parseTarget(q) : null;
  const canConnect = q.length > 0 && (filtered.length > 0 || target !== null);

  function submit(forceNew = false) {
    if (!q) return;
    if (filtered.length > 0) onConnect(filtered[0], forceNew);
    else if (target) onQuick(target);
  }

  const sub = (s: Server) => `${s.protocol}, ${s.username || s.host}`;

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
        <button className="tool" title="Several SSH hosts side by side in one tab" onClick={onSplit}>
          <IconGrid size={15} /> Split view
        </button>
      </div>

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
                    className={'hcard' + (selectedId === s.id ? ' sel' : '')}
                    tabIndex={0}
                    draggable
                    onDragStart={(e) => e.dataTransfer.setData('text/msv-host', s.id)}
                    title={`${s.name} – ${s.host}:${s.port}`}
                    onClick={() => onSelect(s)}
                    onContextMenu={(e) => {
                      e.preventDefault();
                      setMenu({ x: e.clientX, y: e.clientY, server: s });
                    }}
                    onDoubleClick={(e) => onConnect(s, e.shiftKey)}
                    onKeyDown={(e) => e.key === 'Enter' && onConnect(s, e.shiftKey)}
                  >
                    <HostTile server={s} />
                    <span className="hcard-text">
                      <span className="hcard-name">{s.name}</span>
                      <span className="hcard-sub">{sub(s)}</span>
                    </span>
                    {s.favorite && (
                      <span className="fav" title="Favorite">
                        <IconStar size={13} filled />
                      </span>
                    )}
                    {openServerIds.has(s.id) && <span className="status-dot connected live-dot" title="Session open" />}
                  </div>
                ))}
              </div>
            </section>
          ))}
          <p className="muted small">Double-click or Enter connects · click shows details · right-click for more · Shift opens an additional session</p>
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
          server={wsDialog.server}
          workspaces={workspaces}
          onClose={() => setWsDialog(null)}
          onPick={(target) => {
            const server = wsDialog.server;
            setWsDialog(null);
            onMove([server], target);
          }}
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
            { label: 'Duplicate', icon: <IconDuplicate size={14} />, onClick: () => onDuplicateHost(menu.server) },
            { label: 'Delete', icon: <IconTrash size={14} />, danger: true, separator: true, onClick: () => onDelete(menu.server) },
          ]}
        />
      )}
    </div>
  );
}

/** Picker for "Move to workspace…": existing workspaces, a new one or none. */
function MoveDialog({
  server,
  workspaces,
  onPick,
  onClose,
}: {
  server: Server;
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
        <h2>Move "{server.name}"</h2>
        <div className="pop-list" style={{ width: '100%' }}>
          {workspaces
            .filter((w) => w !== server.workspace)
            .map((w) => (
              <button type="button" key={w} className="pop-item" onClick={() => onPick(w)}>
                <span className="pop-name">{w}</span>
              </button>
            ))}
          {server.workspace && (
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
