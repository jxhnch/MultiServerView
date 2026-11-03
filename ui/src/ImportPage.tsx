import { useRef } from 'react';
import { isNative } from './bridge';
import { IconMonitor, IconUpload } from './Icons';
import { ProxmoxSection } from './ProxmoxPage';
import type { Identity, Server } from './types';

export type ImportKind = 'mremoteng' | 'sshconfig' | 'putty-win' | 'putty-reg' | 'csv';

const FILE_IMPORTS: { kind: ImportKind; label: string; hint: string; accept: string; nativeOnly?: boolean }[] = [
  { kind: 'mremoteng', label: 'mRemoteNG', hint: 'confCons.xml', accept: '.xml' },
  { kind: 'sshconfig', label: 'OpenSSH config', hint: '~/.ssh/config', accept: '*' },
  { kind: 'putty-win', label: 'PuTTY (from Windows)', hint: 'reads the saved sessions directly', accept: '', nativeOnly: true },
  { kind: 'putty-reg', label: 'PuTTY (.reg file)', hint: 'Export from regedit', accept: '.reg' },
  { kind: 'csv', label: 'CSV file', hint: 'name, host, port, protocol, user, group, workspace …', accept: '.csv,.txt' },
];

/** All ways to add hosts: from servers (Proxmox, VMware) and from files of other programs. */
export function ImportPage({
  servers,
  identities,
  workspaces,
  activeWorkspace,
  onProxmoxImported,
  onRemoveProxmoxImported,
  onVmware,
  onImport,
  toast,
}: {
  servers: Server[];
  identities: Identity[];
  workspaces: string[];
  activeWorkspace: string;
  onProxmoxImported: () => Promise<void>;
  onRemoveProxmoxImported: (connectionId: string) => Promise<number>;
  onVmware: () => void;
  onImport: (kind: ImportKind, file?: File) => void;
  toast: (msg: string, kind?: 'ok' | 'error') => void;
}) {
  const fileRef = useRef<HTMLInputElement>(null);
  const pendingKind = useRef<ImportKind>('mremoteng');

  function start(it: (typeof FILE_IMPORTS)[number]) {
    pendingKind.current = it.kind;
    if (it.kind === 'putty-win') return onImport(it.kind); // comes straight from the Windows registry, no file needed
    if (fileRef.current) {
      fileRef.current.accept = it.accept === '*' ? '' : it.accept;
      fileRef.current.click();
    }
  }

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Import</h1>
          <p className="muted">
            Add hosts directly from your servers or from other programs. Duplicates are skipped, your existing hosts stay unchanged.
          </p>
        </div>
      </div>

      <ProxmoxSection
        servers={servers}
        identities={identities}
        workspaces={workspaces}
        activeWorkspace={activeWorkspace}
        onImported={onProxmoxImported}
        onRemoveImported={onRemoveProxmoxImported}
        toast={toast}
      />

      <section className="import-section">
        <div className="sec-label">VMware Workstation</div>
        <div className="list">
          <div className="list-row">
            <span className="list-ico">
              <IconMonitor size={18} />
            </span>
            <div className="grow">
              <div className="list-title">VM library of this PC</div>
              <div className="muted small">Reads your VMs including their address (running VMs and VMware DHCP entries). Nothing has to be running.</div>
            </div>
            <button className="primary" onClick={onVmware}>
              Import VMs…
            </button>
          </div>
        </div>
      </section>

      <section className="import-section">
        <div className="sec-label">From files and programs</div>
        <input
          ref={fileRef}
          type="file"
          hidden
          onChange={(e) => {
            const f = e.target.files?.[0];
            e.target.value = '';
            if (f) onImport(pendingKind.current, f);
          }}
        />
        <div className="list">
          {FILE_IMPORTS.filter((i) => !i.nativeOnly || isNative).map((it) => (
            <div className="list-row" key={it.kind}>
              <span className="list-ico">
                <IconUpload size={18} />
              </span>
              <div className="grow">
                <div className="list-title">{it.label}</div>
                <div className="muted small">{it.hint}</div>
              </div>
              <button onClick={() => start(it)}>{it.kind === 'putty-win' ? 'Read now' : 'Choose file…'}</button>
            </div>
          ))}
        </div>
      </section>
    </div>
  );
}
