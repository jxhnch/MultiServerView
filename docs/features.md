# Features

[← Back to the README](../README.md)

- [Connections](#connections)
- [SFTP](#sftp)
- [Working with sessions](#working-with-sessions)
- [Organizing hosts](#organizing-hosts)
- [Import](#import)
- [msv command line](#msv-command-line)

## Connections

**SSH terminal.** A real terminal (xterm.js) with password or key login, host key verification (trust on first use), 10 color schemes, selectable fonts, and a password prompt after a failed login, just like PuTTY.

**RDP.** The native Windows RDP control (the same one `mstsc` and mRemoteNG use) right inside a tab. In the Linux app, RDP is drawn inside the app.

**VNC.** noVNC in a tab, including Ctrl+Alt+Del and clipboard.

**Local terminal.** A shell of this PC in a tab: PowerShell, Command Prompt, WSL or Git Bash on Windows, bash, zsh or fish on Linux. It is a real terminal (full-screen programs, Ctrl+C and resizing work), can be put into a split view and is found with `Ctrl+K`. Only these built-in shells can be started; nothing is saved in the vault, and the AI tools cannot open one.

**Telnet and serial.** The same terminal for network devices such as switches and routers: Telnet with proper option negotiation (not encrypted), and serial consoles (COM ports, `/dev/ttyUSB0`, any baud rate).

**WinRM (experimental).** A PowerShell prompt on a Windows server without SSH. HTTPS is recommended.

**Web hosts (integrated browser).** Open the web page of a firewall, router, Proxmox or printer in a tab.

- On Windows it is a real browser (WebView2 with a private profile): pages look as usual, with back, forward, reload and an address bar. On Linux the page is shown through a local proxy.
- The saved user name and password are filled in on the device's own pages only, and sent once per tab. HTTP basic authentication works too.
- Jump hosts can be used.
- A self-signed certificate is accepted the first time and remembered like an SSH host key (see *Known Hosts*). A changed certificate is refused.
- The address does not need to be exact. When you open the host, the app tries http and https and the usual ports (443, 8443, 8006, 80, 8080), repairs typing mistakes such as `https//`, and saves the address that answers. An address typed with `https` is never changed to unencrypted `http`, and you get a message when only `http` works. Hosts behind a jump host are not tried.
- The page can also be opened in the system browser.

**TeamViewer.** Save a partner ID (and password) as a host and start the installed TeamViewer client with one click.

## SFTP

A two-pane file browser in the style of WinSCP. The left pane works on your PC too (recycle bin, Show in Explorer).

| Area | What you can do |
|---|---|
| **Transfer** | Drag & drop, whole folders, progress, speed and cancel, overwrite choices (all / only older / skip) |
| **Navigate** | Editable path bar, back and forward history, bookmarks, sortable columns, filter, hidden-files switch |
| **Select** | Multi-select with masks, right-click menus, move by dragging onto a folder |
| **Edit** | Open remote files in your own editor (uploaded again on every save), or use the built-in text editor |
| **Inspect** | Change permissions (chmod, also recursive), folder sizes, SHA-256 checksums, compare folders |

**Safe transfers.** A file is first written next to its target and replaces it only when it is complete, so a cancelled transfer does not damage a file that was already there. Deleting or renaming a link acts on the link, never on what it points to. Links are not copied; a *move* then keeps the original.

**Two servers side by side.** *Left side* switches the left pane from this PC to another SSH host. Copy and move then go straight from one server to the other: the data passes through your PC but is not written to its disk. Copying a folder onto or into itself is refused.

**F-keys:** F2 rename, F4 edit, F5 copy, F6 move, F7 new folder, Del delete, Alt+Enter properties.

## Working with sessions

- **Jump hosts:** chain one or more SSH hosts for SSH, SFTP, tunnels, VNC, RDP and web hosts.
- **Port forwarding:** local (`-L`), remote (`-R`) and SOCKS5 (`-D`), start and stop with one click.
- **Split view:** several terminals side by side in one tab, with broadcast (type once, send to all).
- **Virtual machine control:** hosts imported from Proxmox or VMware show a status dot. A small power button starts, shuts down, restarts, suspends or force-stops the VM.

## Organizing hosts

- **Workspaces:** one section per customer, teacher, project or lab, so you never stare at hundreds of hosts at once.
- **Quick switcher:** `Ctrl+K`, a search bar that also understands `ssh user@host`. Plus tabs, favorites, groups, OS logos, and a light and dark theme that follows your system unless you choose one.
- **Identities, keys and snippets:** reuse credentials across hosts, generate SSH keys and install the public key on a server with one click. Snippets can contain `{{variables}}` that ask for values when you run them.
- **Groups in your order:** drag a group by its heading to move the whole group. `msv` lists them in the same order.
- **Groups by drag and drop:** drag a host (or several selected ones) onto another group to move it there. While you drag, areas appear at the bottom: one creates a new group, one takes the host out of its group.
- **Your own order:** drag a host to a new place in its section. Right-click and choose *Sort these hosts by name again* to undo it. `msv` lists the hosts in the same order.
- **Online status:** while the host list is on screen, each listed host gets a short TCP connection to its port once a minute (no login, nothing is sent). Green = it answers, orange = the machine answers but the port is closed, red = no answer. Hosts behind a jump host and serial hosts are not checked. Switch it off in *Settings → Host list*.
- **Wake-on-LAN:** enter a MAC address in the host details, then right-click the host and choose *Wake up*. It works in your own network.
- **Several hosts at once:** Ctrl+click or *Select*, then give them the same SSH key, identity or user name and password, move them to a workspace or delete them.
- **Signing in:** when a login is missing or fails, you can enter a password or pick an SSH key, and save it for the host.
- **Known hosts and history:** review and reset stored host keys (one by one or all at once), and see your connection history (local only, no passwords).

## Import

Duplicates are skipped and existing hosts stay untouched.

| Source | Details | Saved passwords and keys |
|---|---|---|
| **Proxmox VE** | Read-only API token with certificate pinning. Adds VMs and containers with their IPs and operating systems, optionally with an SSH key as their login, and the Proxmox web interface as a *Web* host. The address may be typed without `https://` and port. Pools can become workspaces or groups. Everything an import added can be deleted again with one click | Not readable |
| **VMware Workstation** | Reads your VM library, including addresses from running VMs and the VMware DHCP leases | Not readable |
| **mRemoteNG** | `confCons.xml` | Passwords, decrypted with its master password (default or custom) |
| **OpenSSH config** | `~/.ssh/config` | Keys referenced by `IdentityFile` |
| **PuTTY** | Directly from Windows or from a `.reg` file | Not readable |
| **CSV** | Your own list | Passwords |

PuTTY keys (`.ppk`) must be converted to OpenSSH format in PuTTYgen first. There is no Termius import.

## msv command line

Pick a server and connect from any terminal.

| Command | Result |
|---|---|
| `msv` | Pick from a list. Type to filter, Enter connects, Esc cancels |
| `msv <name>` | Connect right away, or pick from the matches |
| `msv list` | Print all servers |
| `msv --data <folder>` | Use another data folder |

It works for SSH, Telnet and serial hosts, in the same window. It asks for your master password and only reads the vault, so it also works while the app is open.

**Getting it on your PATH**

- **Windows installer:** tick the checkbox during setup.
- **Windows portable ZIP:** add the unpacked folder to your PATH yourself.
- **Linux packages** (`.deb`, `.rpm`): `msv` is installed in `/usr/bin`.
