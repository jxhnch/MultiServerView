# Comparison with Termius

Status: November 2026. Based on the publicly available Termius pages (documentation, pricing, changelog).
Please check plan details on termius.com yourself.

## What MultiServerView has (equal or comparable)
SSH terminal, SFTP with two panes and drag & drop, port forwarding (local/remote/dynamic), snippets with variables, identities,
SSH key management and generator, known hosts, connection history, terminal themes and fonts, groups, tags and workspaces,
operating system icons, encrypted sync (shared folder or Supabase), light/dark, **multi-hop jump hosts**, **split view with broadcast**,
remote file editing over SFTP, imports from mRemoteNG, OpenSSH config, PuTTY, CSV, Proxmox and VMware Workstation,
context menu, settings page (including master password change, auto-lock, backup).
**Additionally compared to Termius: RDP and VNC.**

## What Termius can do and MultiServerView cannot (yet)
| Area | Termius | MultiServerView |
|---|---|---|
| Protocols | Mosh, Telnet, Serial, local terminal | missing |
| Jump hosts | chain inherited per group | chain per host (multi-hop) yes, per group no |
| Terminal | AI autocomplete | missing |
| Split view | up to 16 panes, layouts by drag | grid with several panes, broadcast yes; no free layouts |
| SFTP | change permissions, two servers side by side | missing |
| Snippets | packages (groups) | single snippets with variables |
| Keys | SSH certificates, FIDO2 keys, agent forwarding | import and generation of keys only |
| Import | known_hosts, whole ~/.ssh folder including keys | ssh_config, PuTTY, CSV, mRemoteNG, Proxmox, VMware (without keys and passwords) |
| Logs | record terminal output per host, sync logs | connection history only |
| Teams | shared vaults with permissions, SSO, collaboration | personal vault only |
| Platforms | Windows, macOS, Linux, iOS, Android, iPadOS | Windows only |
| Environment variables per host | yes (Pro) | missing |
