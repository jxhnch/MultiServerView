import {
  siAlpinelinux, siArchlinux, siAlmalinux, siApple, siCentos, siDebian, siFedora, siFreebsd, siGentoo, siKalilinux,
  siLinux, siLinuxmint, siManjaro, siNixos, siOpenbsd, siOpensuse, siPopos, siProxmox, siRaspberrypi, siRedhat,
  siRockylinux, siUbuntu, siVoidlinux,
  type SimpleIcon,
} from 'simple-icons';
import { IconMonitor, ProtocolIcon } from './Icons';
import type { Server } from './types';

/** Operating system id (detected from the host) -> logo. */
const OS_ICONS: Record<string, SimpleIcon> = {
  debian: siDebian, ubuntu: siUbuntu, centos: siCentos, fedora: siFedora, redhat: siRedhat, rocky: siRockylinux,
  almalinux: siAlmalinux, arch: siArchlinux, manjaro: siManjaro, alpine: siAlpinelinux, opensuse: siOpensuse,
  nixos: siNixos, kali: siKalilinux, mint: siLinuxmint, pop: siPopos, gentoo: siGentoo, void: siVoidlinux,
  freebsd: siFreebsd, openbsd: siOpenbsd, raspberrypi: siRaspberrypi, proxmox: siProxmox, linux: siLinux, macos: siApple,
};

const OS_LABEL: Record<string, string> = {
  debian: 'Debian', ubuntu: 'Ubuntu', centos: 'CentOS', fedora: 'Fedora', redhat: 'Red Hat', rocky: 'Rocky Linux',
  almalinux: 'AlmaLinux', arch: 'Arch Linux', manjaro: 'Manjaro', alpine: 'Alpine', opensuse: 'openSUSE', nixos: 'NixOS',
  kali: 'Kali', mint: 'Linux Mint', pop: 'Pop!_OS', gentoo: 'Gentoo', void: 'Void', freebsd: 'FreeBSD', openbsd: 'OpenBSD',
  raspberrypi: 'Raspberry Pi OS', proxmox: 'Proxmox', linux: 'Linux', macos: 'macOS', windows: 'Windows',
};

export const osLabel = (os: string) => OS_LABEL[os] ?? '';

/** Color tile of a host: distro logo (SSH, once detected) or protocol icon. */
export function HostTile({ server, size = 'md' }: { server: Pick<Server, 'protocol' | 'os'>; size?: 'sm' | 'md' | 'lg' }) {
  const px = size === 'sm' ? 13 : size === 'lg' ? 24 : 20;
  if (server.protocol === 'ssh' && server.os === 'windows')
    return (
      <span className={`tile ${size} rdp`} title="Windows">
        <IconMonitor size={px} />
      </span>
    );
  const icon = server.protocol === 'ssh' ? OS_ICONS[server.os] : undefined;

  if (icon) {
    return (
      <span className={`tile ${size}`} style={{ background: `#${icon.hex}` }} title={osLabel(server.os)}>
        <svg width={px} height={px} viewBox="0 0 24 24" fill="#fff" aria-hidden="true">
          <path d={icon.path} />
        </svg>
      </span>
    );
  }
  return (
    <span className={`tile ${size} ${server.protocol}`}>
      <ProtocolIcon protocol={server.protocol} size={px} />
    </span>
  );
}
