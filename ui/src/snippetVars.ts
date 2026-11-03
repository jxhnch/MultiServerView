import type { Server } from './types';

/** A variable in a snippet: "{{name}}" or with a default value "{{name=value}}". */
export interface SnippetVar {
  name: string;
  defaultValue: string;
}

const PATTERN = /\{\{\s*([A-Za-z_][\w-]*)\s*(?:=([^}]*))?\}\}/g;

/** Values the app fills in from the host itself. It asks for everything else when running. */
export function builtinVars(server: Pick<Server, 'host' | 'username' | 'port'>): Record<string, string> {
  return { host: server.host, user: server.username, port: String(server.port) };
}

/** Variables the user has to fill in (without the automatic ones), in order of first appearance. */
export function parseVars(command: string, builtins: Record<string, string> = {}): SnippetVar[] {
  const found = new Map<string, SnippetVar>();
  for (const m of command.matchAll(PATTERN)) {
    const name = m[1];
    if (name in builtins) continue;
    const existing = found.get(name);
    const def = (m[2] ?? '').trim();
    // If one occurrence has a default value, it applies to all occurrences of this variable.
    if (!existing) found.set(name, { name, defaultValue: def });
    else if (!existing.defaultValue && def) existing.defaultValue = def;
  }
  return [...found.values()];
}

/** Fills in values. If a value is missing, the default of that occurrence applies (or empty). */
export function fillVars(command: string, values: Record<string, string>, builtins: Record<string, string> = {}): string {
  return command.replace(PATTERN, (_all, name: string, def?: string) => {
    if (name in builtins) return builtins[name];
    return values[name] ?? (def ?? '').trim();
  });
}

// ---------------------------------------------------------------- last used values (convenience only, per snippet)

const KEY = 'msv-snippet-values-v1';

export function loadLastValues(snippetId: string): Record<string, string> {
  try {
    const all = JSON.parse(localStorage.getItem(KEY) ?? '{}') as Record<string, Record<string, string>>;
    return all[snippetId] ?? {};
  } catch {
    return {};
  }
}

export function saveLastValues(snippetId: string, values: Record<string, string>) {
  try {
    const all = JSON.parse(localStorage.getItem(KEY) ?? '{}') as Record<string, Record<string, string>>;
    all[snippetId] = values;
    localStorage.setItem(KEY, JSON.stringify(all));
  } catch {
    /* storage not available - then simply without remembering */
  }
}
