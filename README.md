# MultiServerView

**One window for all your servers.** SSH, SFTP, RDP and VNC in tabs, a fast host switcher, workspaces to keep hundreds of hosts tidy, and an end-to-end encrypted vault that you can keep on your PC, in a folder, on GitHub or in Supabase. For Windows, open source (MIT).

<table>
  <tr>
    <td><a href="docs/screenshots/hosts.png"><img src="docs/screenshots/hosts.png" alt="Hosts" width="300"></a></td>
    <td><a href="docs/screenshots/terminal.png"><img src="docs/screenshots/terminal.png" alt="Terminal" width="300"></a></td>
    <td><a href="docs/screenshots/sftp.png"><img src="docs/screenshots/sftp.png" alt="SFTP" width="300"></a></td>
  </tr>
  <tr>
    <td align="center">Hosts and workspaces</td>
    <td align="center">SSH terminal</td>
    <td align="center">SFTP</td>
  </tr>
  <tr>
    <td><a href="docs/screenshots/import.png"><img src="docs/screenshots/import.png" alt="Import" width="300"></a></td>
    <td><a href="docs/screenshots/forwards.png"><img src="docs/screenshots/forwards.png" alt="Port forwarding" width="300"></a></td>
    <td><a href="docs/screenshots/settings.png"><img src="docs/screenshots/settings.png" alt="Settings" width="300"></a></td>
  </tr>
  <tr>
    <td align="center">Import</td>
    <td align="center">Port forwarding</td>
    <td align="center">Settings</td>
  </tr>
</table>

## Features

### Connect
- **SSH terminal** – real terminal (xterm.js), password or key login, host key verification (trust on first use), 10 color schemes, fonts, a password prompt on failed logins just like PuTTY.
- **SFTP like WinSCP** – two panes with drag & drop, folders with progress, speed and cancel; **F-keys** (F2 rename, F4 edit, F5 copy, F6 move, F7 new folder, Del delete, Alt+Enter properties), multi-select with masks, sortable columns, filter, hidden-files switch, editable path bar with back/forward history and bookmarks, right-click menus, move by dragging onto a folder, **overwrite choices** (all / only older / skip), **permissions (chmod)** with recursion, folder sizes, SHA-256 checksums, **open remote files in your own editor** and upload again on every save, built-in text editor, and **compare folders** to see what is new or different. The left pane works on your PC too (recycle bin, Show in Explorer).
- **RDP** – the native Windows RDP control (the same as mstsc / mRemoteNG) right inside a tab.
- **VNC** – noVNC in a tab, including Ctrl+Alt+Del and clipboard.
- **TeamViewer** – save a partner ID (and password) as a host and start the installed TeamViewer client with one click.
- **Jump hosts** – chain one or more SSH hosts for SSH, SFTP, tunnels, VNC and RDP.
- **Port forwarding** – local (-L), remote (-R) and SOCKS5 (-D), start/stop with one click.
- **Split view** – several terminals side by side in one tab, with broadcast (type once, send to all).

### Organize
- **Workspaces** – one section per customer, teacher, project or lab, so you never stare at hundreds of hosts at once.
- **Quick switcher** – `Ctrl+K`, a search bar that also understands `ssh user@host`, tabs, favorites, groups, OS logos, light and dark theme.
- **Identities, keys and snippets** – reuse credentials across hosts, generate SSH keys and install the public key on a server with one click, snippets with `{{variables}}` that ask for values when you run them.
- **Known hosts and history** – review and reset stored host keys, see your connection history (local only, no passwords).

### Import
Bring your servers with you. Duplicates are skipped, existing hosts stay untouched.
- **Proxmox VE** – connect with a read-only API token (certificate pinning included) and add VMs and containers as hosts, with their IPs and operating systems. Choose whether Proxmox pools become workspaces or groups. Delete everything an import added again with one click.
- **VMware Workstation** – reads your VM library, including addresses from running VMs and the VMware DHCP leases.
- **mRemoteNG**, **OpenSSH config** (`~/.ssh/config`), **PuTTY** (directly from Windows or a `.reg` file) and **CSV**.

### Let AI work on your servers
An optional **MCP server** (off by default) lets AI tools list your hosts, run commands and read, write and list files over SSH/SFTP. Open **AI tools** in the left menu, switch it on and pick your tool (**Claude Code, Codex, Cursor, VS Code Copilot, Gemini CLI, Claude Desktop** or any MCP client): the page shows the exact command or config text to paste. It listens on `127.0.0.1` only and needs the token shown there; anything that has the token can act on your hosts while the vault is unlocked, so only enable it when you want that.

### Protect your data
- **Encrypted vault** – PBKDF2-SHA256 (600,000 iterations) → AES-256-GCM. Passwords and private keys are never stored in plain text.
- **One vault, one place** – store it on **this PC**, in a **folder** (OneDrive, iCloud, Dropbox, Nextcloud …), in a private **GitHub gist** (you only need a token) or in your own free **Supabase** project. Every change is saved there right away, other devices pick it up within a minute. No merging: if two devices change it at the same time, you are asked which version to keep and the other one is saved as a backup file. The data is encrypted before it leaves your PC.
- **Clear vault** – wipe everything with three confirmation steps. Change the master password, set an auto-lock, back up the vault.

## Download and install

Get the latest version from the **[Releases page](https://github.com/jxhnch/MultiServerView/releases)**:

| File | For |
|---|---|
| `MultiServerView-Setup-x.y.z.exe` | Installer wizard (no admin rights needed, Start menu entry, uninstaller). Updates itself: the app checks GitHub for a newer release at startup and installs it on request (switch off in *Settings → About*) |
| `MultiServerView-x.y.z-portable-win-x64.zip` | Portable: unpack anywhere and run `MultiServerView.exe` |

Nothing else has to be installed: .NET is bundled and the WebView2 runtime is part of Windows 10/11.

> **"Windows protected your PC" (SmartScreen)?** The downloads are not code-signed yet. Code-signing certificates cost money and are tied to a verified identity, and SmartScreen also distrusts every new, little-downloaded program. It does not mean the file is malware. Click **More info → Run anyway**. To be sure you have the original, download only from this repository's Releases page; the whole source and the build workflow (`.github/workflows/release.yml`) are public, so you can build it yourself as well.

On first start you create a **master password** (at least 8 characters). There is no recovery: if you forget it, nobody can open your vault.

## Where your data lives and how to start from zero

Uninstalling the app does **not** delete your data (so updates and reinstalls never lose your vault):

| What | Location |
|---|---|
| Vault (local copy), source settings, connection history, settings | `%APPDATA%\MultiServerView` (`vault.msv`, `sync.json`, `history.json`, ...) |
| Interface settings (theme, terminal look) | `%LOCALAPPDATA%\MultiServerView\webview` |
| Your vault at a folder, GitHub or Supabase | wherever you chose it, never touched by the app's uninstaller |

To start completely from scratch (**this permanently deletes your vault, there is no recovery**):

1. Optional: make a backup in *Settings* first.
2. Close MultiServerView and uninstall it (or just close it if you keep it).
3. Delete both folders:

```bash
rmdir /s /q "%APPDATA%\MultiServerView"
rmdir /s /q "%LOCALAPPDATA%\MultiServerView"
```

4. Delete the vault at its source as well (the file `MultiServerView-Vault.msv` in your folder, the gist at GitHub or the row in Supabase), otherwise you can load it again. Then reinstall and create a new master password.

Portable ZIP: the same applies. If you only want to wipe the vault, use *Settings → Clear vault* inside the app (it empties the vault but keeps your master password).

## Keyboard shortcuts

In a terminal, plain Ctrl combinations belong to the shell, so app commands use `Ctrl+Shift`:

| Key | Action |
|---|---|
| `Ctrl+K` | Quick connect (outside the terminal) |
| `Ctrl+Shift+K` | Quick connect (**everywhere**, also inside a terminal) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+1 … 9` | Jump to a tab (1 = Hosts) |
| `Ctrl+Shift+W` | Close tab (middle click works too) |
| `Ctrl+Shift+N` | New host |
| `Ctrl+Shift+D` | Second session to the same host |
| `Shift+Enter` / `Shift+click` | Always open a new session when connecting |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` | Terminal: copy / paste (right click works too) |

When the focus is inside an **RDP session**, all keys go to the remote PC (as in every RDP client). Click another tab with the mouse to leave it.

## Choose where your vault lives

Open **Vault source** in the left menu and pick one of four places:

| Source | What you need |
|---|---|
| **This PC** | Nothing. This is the default. |
| **A folder** | A folder that OneDrive, iCloud, Dropbox or Nextcloud already keeps in step. Choose it, done. |
| **GitHub** | A token with the `gist` permission ([create one here](https://github.com/settings/tokens/new?scopes=gist&description=MultiServerView), the link preselects it). Paste it, done. |
| **Supabase** | A free project, one SQL snippet ([`docs/supabase.sql`](docs/supabase.sql)) and an account. The page guides you through it. |

When you choose a place that is still empty, your vault moves there. When it already contains a vault (for example from your other PC), the app asks you: **use the vault that is there** (your current one is kept as a backup file) or **replace it with mine**. So a second device simply picks the same place and takes the vault that is already there.

Without a connection you can keep working: changes wait on this PC and are saved as soon as the place is reachable again. The master password never leaves your PC. Change it on one device and the others ask for the new one the next time they open the vault.

## Security in short

- Vault file: `%APPDATA%\MultiServerView\vault.msv`, encrypted with a key derived from your master password.
- Private SSH keys are never sent back to the UI after import; connections run entirely in the host process.
- SSH host keys use trust on first use. If the key of a known host changes, the connection is **refused** (you can reset the key in the host dialog).
- VNC runs through a local bridge that only listens on `127.0.0.1` and protects every connection with a one-time token.
- The browser development mode (`npm run dev`) stores demo data unencrypted and is only meant for UI development.

## Build from source

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and [Node.js 20+](https://nodejs.org).

```bash
scripts\publish.cmd          # runs tests, builds the UI, publishes to publish\MultiServerView.exe
```

For quick experiments:

```bash
cd ui && npm install && npm run build
dotnet run --project src/MultiServerView.App
```

> `dotnet publish` does not rebuild the UI. Always run `npm run build` in `ui/` first.

Develop the UI in the browser with demo data and a demo shell:

```bash
cd ui && npm install && npm run dev
```

Or against the real window with hot reload:

```bash
set MSV_DEV_URL=http://localhost:5173
dotnet run --project src/MultiServerView.App
```

Run the tests (also works on Linux/macOS):

```bash
dotnet test tests/MultiServerView.Core.Tests
```

SSH/SFTP/tunnel integration tests only run when `MSV_SSH_TEST_DIR` points to a folder with test keys and a local sshd listens on `127.0.0.1:2222` (user `msvtest`); otherwise they do nothing.

### Project layout

| Folder | Contents |
|---|---|
| `src/MultiServerView.Core` | Vault + crypto, vault sources (folder, GitHub, Supabase), SSH/SFTP/tunnels (SSH.NET), VNC bridge, importers (net8.0, platform independent) |
| `src/MultiServerView.App` | WinForms window with WebView2, bridge to the UI, RDP hosting, DPAPI, auto-save to the source, self-test |
| `ui/` | React + TypeScript (Vite): interface, xterm.js, noVNC |
| `tests/` | xUnit tests (crypto, vault, sources against a simulated Supabase API, GitHub and folders, VNC bridge, importers, SSH against a real sshd) |
| `installer/` | Inno Setup script for the installer wizard |
| `docs/` | Supabase SQL, comparison with Termius, first-run checklist, screenshots |

## Known limitations

- While a palette or dialog is open, a running RDP window is hidden briefly (the native window would otherwise cover the UI). The session keeps running.
- Convert PuTTY keys (`.ppk`) to OpenSSH format in PuTTYgen first.
- Import of saved passwords: mRemoteNG (decrypted with its master password, default or custom) and CSV; keys via `IdentityFile` of an OpenSSH config. PuTTY sessions and Proxmox/VMware have no readable passwords, and PuTTY `.ppk` keys must be converted to OpenSSH format first. There is no Termius import.
- No Telnet/Serial/Mosh, no AI autocomplete, no teams. See the [comparison with Termius](docs/comparison-with-termius.md).
- Windows only.

## License

[MIT](LICENSE). Contributions are welcome: open an issue or a pull request.

## Inspiration and vibe coding

MultiServerView is inspired by [Termius](https://termius.com), whose workflow (one place for hosts, terminal, SFTP and a vault that is available everywhere) I wanted on Windows with RDP and VNC included and under my own control. It is an independent project and not affiliated with Termius.

This project is **vibe-coded**: it was built together with an AI coding assistant (Claude Code). I steer the design and test the result, the assistant writes most of the code. The code is tested and the self-test runs on every build, but please review it yourself before trusting it with production credentials, and report anything that looks wrong.
