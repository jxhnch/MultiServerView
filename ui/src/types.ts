export type Protocol = 'ssh' | 'rdp' | 'vnc';

export interface Server {
  id: string;
  name: string;
  host: string;
  port: number;
  protocol: Protocol;
  username: string;
  password: string;
  group: string;
  tags: string[];
  favorite: boolean;
  lastUsed: string | null;
  notes: string;
}

export const DEFAULT_PORT: Record<Protocol, number> = { ssh: 22, rdp: 3389, vnc: 5900 };

export function emptyServer(): Server {
  return {
    id: '',
    name: '',
    host: '',
    port: 22,
    protocol: 'ssh',
    username: '',
    password: '',
    group: '',
    tags: [],
    favorite: false,
    lastUsed: null,
    notes: '',
  };
}
