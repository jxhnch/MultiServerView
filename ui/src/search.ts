import type { Server } from './types';

/** Alle Woerter der Suche muessen irgendwo vorkommen. Treffer am Wortanfang / im Namen zaehlen mehr. */
export function scoreServer(s: Server, query: string): number {
  const tokens = query.toLowerCase().split(/\s+/).filter(Boolean);
  if (tokens.length === 0) return 1;

  const name = s.name.toLowerCase();
  const rest = [s.host, s.group, s.username, s.protocol, ...s.tags].join(' ').toLowerCase();

  let score = 0;
  for (const t of tokens) {
    const inName = name.indexOf(t);
    if (inName === 0) score += 100;
    else if (inName > 0) score += name[inName - 1] === '-' || name[inName - 1] === ' ' ? 80 : 50;
    else if (rest.includes(t)) score += 20;
    else return 0;
  }
  return score;
}

export function filterServers(servers: Server[], query: string): Server[] {
  return servers
    .map((s) => ({ s, score: scoreServer(s, query) }))
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score || recency(b.s) - recency(a.s) || a.s.name.localeCompare(b.s.name))
    .map((x) => x.s);
}

export const recency = (s: Server) => (s.lastUsed ? Date.parse(s.lastUsed) : 0);

export function timeAgo(iso: string | null): string {
  if (!iso) return 'nie';
  const min = Math.round((Date.now() - Date.parse(iso)) / 60_000);
  if (min < 1) return 'gerade eben';
  if (min < 60) return `vor ${min} Min`;
  const h = Math.round(min / 60);
  if (h < 24) return `vor ${h} Std`;
  const d = Math.round(h / 24);
  return `vor ${d} ${d === 1 ? 'Tag' : 'Tagen'}`;
}

/** Erkennt "[ssh://|rdp://|vnc://][user@]host[:port]" (auch mit führendem "ssh "). */
export function parseTarget(input: string): Partial<Server> | null {
  const text = input.trim().replace(/^ssh\s+/i, '');
  const m = /^(?:(ssh|rdp|vnc):\/\/)?(?:([^@\s]+)@)?([A-Za-z0-9._-]+|\[[0-9a-fA-F:]+\])(?::(\d{1,5}))?$/.exec(text);
  if (!m) return null;
  const protocol = (m[1]?.toLowerCase() as Server['protocol'] | undefined) ?? 'ssh';
  const host = m[3];
  const port = m[4] ? Number(m[4]) : { ssh: 22, rdp: 3389, vnc: 5900 }[protocol];
  return { name: host, host, port, protocol, username: m[2] ?? '' };
}
