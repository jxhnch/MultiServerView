# AI tools (MCP server)

[← Back to the README](../README.md)

An optional MCP server lets AI tools use the app the way you do. It is **off by default**.

## Setup

1. Open **AI tools** in the left menu and switch the server on.
2. Pick your tool: **Claude Code, Codex, Cursor, VS Code Copilot, Antigravity, Claude Desktop** or any other MCP client.
3. The page shows the exact command or config text, and can write it into the tool's config file with one click.

## What the AI can do

| Area | Actions |
|---|---|
| **Servers** | Run commands; read, write, upload, download, rename and delete files and change their permissions over SSH/SFTP; work in interactive terminals (sudo, top, editors, installers) |
| **Without SSH** | Telnet and serial consoles (switches, routers), and PowerShell on Windows servers through WinRM |
| **Virtual machines** | See the power state of Proxmox and VMware hosts and start, shut down, restart, suspend or force-stop them |
| **Remote desktops** | See and operate VNC screens (and RDP screens in the Linux app): screenshots, clicks, drag, scroll, typing and shortcuts |
| **Management** | Add, change and delete hosts, generate and install SSH keys, manage snippets, identities and port forwards |
| **This PC** | List, read, write and move files (deleting goes to the recycle bin) |

## Security

> [!WARNING]
> Anything that has the token can use these actions while the vault is unlocked. Only enable the server when you want that.

- It listens on `127.0.0.1` only and needs the token shown on the **AI tools** page.
- Saved passwords and private keys are never shown to the AI.
- Every connection it makes appears under **Logs**.

If you want it to do less:

- **Read-only mode** offers only tools that look.
- You can limit it to selected workspaces.
